/*
 * Copyright 2018 Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
 using Amazon.KinesisTap.Core.Metrics;
using System;
using System.Collections.Generic;
using System.Text;
using Amazon.KinesisTap.Core;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.Util;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Reflection;

namespace Amazon.KinesisTap.AWS
{
    public class CloudWatchSink : AWSMetricsSink<PutMetricDataRequest, PutMetricDataResponse, MetricValue>
    {
        private IAmazonCloudWatch _cloudWatchClient;
        private string _namespace;
        private readonly Dimension[] _dimensions;
        private int _storageResolution;

        private static Dimension[] _defaultDimensions;
        private static Dimension[] DefaultDimensions
        {
            get
            {
                if (_defaultDimensions == null)
                {
                    List<Dimension> dimensions = new List<Dimension>()
                    {
                        new Dimension() { Name = "ComputerName", Value = Utility.ComputerName }
                    };
                    if (!string.IsNullOrEmpty(EC2InstanceMetadata.InstanceId))
                    {
                        dimensions.Add(new Dimension() { Name = "InstanceID", Value = EC2InstanceMetadata.InstanceId });
                    }
                    _defaultDimensions = dimensions.ToArray();
                }
                return _defaultDimensions;
            }
        }

        private static IDictionary<MetricUnit, StandardUnit> _unitMap;

        private const int ATTEMPT_LIMIT = 1;
        private const int FLUSH_QUEUE_DELAY = 100; //Throttle at about 10 TPS

        public CloudWatchSink(int defaultInterval, IPlugInContext context, IAmazonCloudWatch cloudWatchClient) : base(defaultInterval, context)
        {
            _cloudWatchClient = cloudWatchClient;

            //StorageResolution is used to specify standard or high-resolution metrics. Valid values are 1 and 60
            //It is different to interval.
            //See https://docs.aws.amazon.com/AmazonCloudWatch/latest/APIReference/API_MetricDatum.html for full details
            _storageResolution = base._interval < 60 ? 1 : 60;

            string dimensionsConfig = null;
            if (_config != null)
            {
                dimensionsConfig = _config["dimensions"];
                _namespace = _config["namespace"];
            }
            if (!string.IsNullOrEmpty(dimensionsConfig))
            {
                List<Dimension> dimensions = new List<Dimension>();
                string[] dimensionPairs = dimensionsConfig.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach(var dimensionPair in dimensionPairs)
                {
                    string[] keyValue = dimensionPair.Split('=');
                    string value = ResolveVariables(keyValue[1]);
                    dimensions.Add(new Dimension() { Name = keyValue[0], Value = value });
                }
                _dimensions = dimensions.ToArray();
            }
            else
            {
                _dimensions = DefaultDimensions;
            }

            if (string.IsNullOrEmpty(_namespace))
            {
                _namespace = "KinesisTap";
            }
            else
            {
                _namespace = ResolveVariables(_namespace);
            }
        }

        #region public methods
        static CloudWatchSink()
        {
            _unitMap = new Dictionary<MetricUnit, StandardUnit>();

            foreach (MetricUnit key in Enum.GetValues(typeof(MetricUnit)))
            {
                var standardUnitField = typeof(StandardUnit)
                    .GetTypeInfo()
                    .GetDeclaredField(key.ToString());
                if (standardUnitField != null)
                {
                    StandardUnit cloudWatchUnit = (StandardUnit)standardUnitField
                        .GetValue(null);
                    _unitMap.Add(key, cloudWatchUnit);
                }
            }
        }

        public override void Start()
        {
            base.Start();
            _metrics?.InitializeCounters(this.Id, MetricsConstants.CATEGORY_SINK, CounterTypeEnum.Increment,
                new Dictionary<string, MetricValue>()
            {
                { MetricsConstants.CLOUDWATCH_PREFIX + MetricsConstants.NONRECOVERABLE_SERVICE_ERRORS, MetricValue.ZeroCount },
                { MetricsConstants.CLOUDWATCH_PREFIX + MetricsConstants.RECOVERABLE_SERVICE_ERRORS, MetricValue.ZeroCount },
                { MetricsConstants.CLOUDWATCH_PREFIX + MetricsConstants.SERVICE_SUCCESS, MetricValue.ZeroCount }
            });
        }
        #endregion

        #region protected methods
        protected override int AttemptLimit => ATTEMPT_LIMIT;

        protected override int FlushQueueDelay => FLUSH_QUEUE_DELAY;

        protected override void OnFlush(IDictionary<MetricKey, MetricValue> accumlatedValues, IDictionary<MetricKey, MetricValue> lastValues)
        {
            QueryDataSources(accumlatedValues);

            List<MetricDatum> datums = new List<MetricDatum>();
            if (string.IsNullOrWhiteSpace(_metricsFilter))
            {
                PrepareMetricDatums(accumlatedValues, datums);
                PrepareMetricDatums(lastValues, datums);
            }
            else
            {
                FilterValues(accumlatedValues, lastValues, datums);
            }
            PutMetricDataAsync(datums).Wait();
            PublishMetrics(MetricsConstants.CLOUDWATCH_PREFIX);
            Task.Run(FlushQueueAsync)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception is AggregateException aex)
                    {
                        aex.Handle(ex =>
                        {
                            _logger?.LogError($"FlushQueueAsync Exception {ex}");
                            return true;
                        });
                    }
                });
        }

        protected override string EvaluateVariable(string value)
        {
            string evaluated = base.EvaluateVariable(value);
            try
            {
                return AWSUtilities.EvaluateAWSVariable(evaluated);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message);
                throw;
            }
        }

        protected override async Task<PutMetricDataResponse> SendRequestAsync(PutMetricDataRequest putMetricDataRequest)
        {
            return await _cloudWatchClient.PutMetricDataAsync(putMetricDataRequest);
        }

        protected override bool IsRecoverable(Exception ex)
        {
            return !(ex is InvalidParameterCombinationException
                    || ex is InvalidParameterValueException
                    || ex is MissingRequiredParameterException);
        }
        #endregion

        #region private methods
        private void FilterValues(IDictionary<MetricKey, MetricValue> accumlatedValues, IDictionary<MetricKey, MetricValue> lastValues, List<MetricDatum> datums)
        {
            var filteredAccumulatedValues = FilterValues(accumlatedValues);
            PrepareMetricDatums(filteredAccumulatedValues, datums);
            var filteredLastValues = FilterValues(lastValues);
            PrepareMetricDatums(filteredLastValues, datums);
            if (_aggregatedMetricsFilters.Count > 0)
            {
                var filteredAggregatedAccumulatedValues = 
                    FilterAndAggregateValues(accumlatedValues, 
                        values => new MetricValue(values.Sum(v => v.Value), values.First().Unit));
                PrepareMetricDatums(filteredAggregatedAccumulatedValues, datums);
                var filteredAggregatedLastValues = FilterAndAggregateValues(lastValues, 
                    values => new MetricValue((long)values.Average(v => v.Value), values.First().Unit));
                PrepareMetricDatums(filteredAggregatedLastValues, datums);
            }
        }

        private void PublishMetrics(string prefix)
        {
            _metrics?.PublishCounters(this.Id, MetricsConstants.CATEGORY_SINK, CounterTypeEnum.Increment, new Dictionary<string, MetricValue>()
            {
                { prefix + MetricsConstants.SERVICE_SUCCESS, new MetricValue(_serviceSuccess) },
                { prefix + MetricsConstants.RECOVERABLE_SERVICE_ERRORS, new MetricValue(_recoverableServiceErrors) },
                { prefix + MetricsConstants.NONRECOVERABLE_SERVICE_ERRORS, new MetricValue(_nonrecoverableServiceErrors) }
            });
            _metrics?.PublishCounter(this.Id, MetricsConstants.CATEGORY_SINK, CounterTypeEnum.CurrentValue, prefix + MetricsConstants.LATENCY, _latency, MetricUnit.Milliseconds);
            ResetIncrementalCounters();
        }

        private void ResetIncrementalCounters()
        {
            _serviceSuccess = 0;
            _recoverableServiceErrors = 0;
            _nonrecoverableServiceErrors = 0;
        }

        private async Task PutMetricDataAsync(List<MetricDatum> datums)
        {
            //cloudwatch can only handle 20 datums at a time
            foreach (var subDatums in datums.Chunk(20))
            {
                var putMetricDataRequest = new PutMetricDataRequest()
                {
                    Namespace = _namespace,
                    MetricData = subDatums as List<MetricDatum>
                };
                await PutMetricDataAsync(putMetricDataRequest);
            }
        }

        private void PrepareMetricDatums(IDictionary<MetricKey, MetricValue> metrics, List<MetricDatum> datums)
        {
            foreach (var metric in metrics)
            {
                datums.Add(new MetricDatum()
                {
                    Dimensions = GetDimensions(metric.Key.Id, metric.Key.Category),
                    Value = metric.Value.Value,
                    MetricName = metric.Key.Name,
                    Timestamp = DateTime.UtcNow,
                    StorageResolution = _storageResolution,
                    Unit = _unitMap[metric.Value.Unit]
                });
            }
        }

        private List<Dimension> GetDimensions(string id, string category)
        {
            List<Dimension> dimensions = new List<Dimension>(_dimensions);
            if (!string.IsNullOrEmpty(id))
            {
                dimensions.Add(new Dimension() { Name = "Id", Value = id });
            }
            dimensions.Add(new Dimension() { Name = "Category", Value = category });
            return dimensions;
        }

        private void QueryDataSources(IDictionary<MetricKey, MetricValue> accumlatedValues)
        {
            foreach (var dataSouce in _dataSources?.Values)
            {
                var resultEnvelope = dataSouce.Query(null);
                if (resultEnvelope != null)
                {
                    var metrics = resultEnvelope.Data as ICollection<KeyValuePair<MetricKey, MetricValue>>;
                    foreach (var metric in metrics)
                    {
                        accumlatedValues[metric.Key] = metric.Value;
                    }
                }
            }
        }
        #endregion
    }
}
