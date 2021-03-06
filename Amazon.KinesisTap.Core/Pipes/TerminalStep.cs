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
 using System;
using System.Collections.Generic;
using System.Text;

namespace Amazon.KinesisTap.Core
{
    /// <summary>
    /// Last step of a list. Used to attach and handler to hancle the output
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class TerminalStep<T> : Step<T, T>
    {
        private readonly Action<T> _handler;

        /// <summary>
        /// Handler to handle the output
        /// </summary>
        /// <param name="handler"></param>
        public TerminalStep(Action<T> handler)
        {
            _handler = handler;
        }

        public override IStep Next { get => throw new NotImplementedException("No more next"); set => throw new NotImplementedException("No more next"); }

        public override void OnNext(T value)
        {
            _handler(value);
        }
    }
}
