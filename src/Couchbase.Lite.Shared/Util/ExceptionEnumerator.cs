﻿//
// ExceptionEnumerator.cs
//
// Author:
// 	Jim Borden  <jim.borden@couchbase.com>
//
// Copyright (c) 2016 Couchbase, Inc All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
using System;
using System.Collections.Generic;
using System.Collections;

namespace Couchbase.Lite.Util
{
    internal sealed class ExceptionEnumerable : IEnumerable<Exception>
    {
        private readonly Exception _e;

        public ExceptionEnumerable(Exception e)
        {
            _e = e;
        }

        #region IEnumerable

        public IEnumerator<Exception> GetEnumerator()
        {
            return new ExceptionEnumerator(_e);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion
    }

    internal sealed class ExceptionEnumerator : IEnumerator<Exception>
    {
        private readonly Exception _original;
        private Queue<Exception> _exceptionQueue = new Queue<Exception>();
        private bool _first = true;

        public ExceptionEnumerator(Exception e)
        {
            _original = e;
            Reset();
        }

        private void Add(Exception e)
        {
            if (e == null) {
                return;
            }

            var ae = e as AggregateException;
            if (ae != null) {
                foreach (var child in ae.InnerExceptions) {
                    Add(child);
                }

                return;
            }

            Add(e.InnerException);
            _exceptionQueue.Enqueue(e);
        }

        #region IEnumerator

        public bool MoveNext()
        {
            if (_first) {
                _first = false;
                return true;
            }

            if (_exceptionQueue.Count == 1) {
                return false;
            }

            _exceptionQueue.Dequeue();
            return true;
        }

        public void Reset()
        {
            _exceptionQueue.Clear();
            Add(_original);
        }

        public Exception Current
        {
            get {
                return _exceptionQueue.Peek();
            }
        }

        object IEnumerator.Current
        {
            get {
                return Current;
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            // No-op
        }

        #endregion

    }
}

