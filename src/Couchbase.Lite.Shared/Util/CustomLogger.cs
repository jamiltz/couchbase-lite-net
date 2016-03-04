//
// CustomLogger.cs
//
// Author:
//     Zachary Gramana  <zack@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc
// Copyright (c) 2014 .NET Foundation
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
//
// Copyright (c) 2014 Couchbase, Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
// except in compliance with the License. You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed under the
// License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND,
// either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
//

using System;
using System.Diagnostics;

namespace Couchbase.Lite.Util
{
    internal sealed class CustomLogger : ILogger 
    {

        #region Variables

        private readonly CouchbaseTraceListener _ts;
        private readonly object _locker = new object();

        #endregion

        #region Constructors

        public CustomLogger()
        {
            _ts = new CouchbaseTraceListener();
            try {
                Trace.Listeners.Add(_ts);
            } catch(NotSupportedException e) {
                throw new MethodAccessException("It appears that needed classes for Couchbase Lite .NET have been " +
                "linked away, please ensure that in your Project Options under iOS Build that Linker Behavior " +
                "is set to 'Don't Link'", e);
            }
        }

        #endregion

        #region ILogger

        public void V(string tag, string msg)
        {
            lock (_locker) {
                _ts.WriteLine(SourceLevels.Verbose, msg, tag); 
            }
        }

        public void V(string tag, string msg, Exception tr)
        {
            if (tr == null) {
                V(tag, msg);
            }

            lock (_locker) {
                _ts.WriteLine(SourceLevels.Verbose, String.Format("{0}:\r\n{1}", msg, Misc.Flatten(tr)), tag); 
            }
        }

        public void V(string tag, string format, params object[] args)
        {
            V(tag, string.Format(format, args));
        }

        public void D(string tag, string format, params object[] args)
        {
            D(tag, string.Format(format, args));
        }

        public void D(string tag, string msg)
        {
            lock (_locker) {
                _ts.WriteLine(SourceLevels.ActivityTracing, msg, tag); 
            }
        }

        public void D(string tag, string msg, Exception tr)
        {
            if (tr == null) {
                D(tag, msg);
            }

            lock (_locker) { 
                _ts.WriteLine(SourceLevels.ActivityTracing, String.Format("{0}:\r\n{1}", msg, Misc.Flatten(tr)), tag); 
            }
        }

        public void I(string tag, string msg)
        {
            lock (_locker) {
                _ts.WriteLine(SourceLevels.Information, msg, tag); 
            }
        }

        public void I(string tag, string msg, Exception tr)
        {
            if (tr == null) {
                I(tag, msg);
            }

            lock (_locker) {
                _ts.WriteLine(SourceLevels.Information, String.Format("{0}:\r\n{1}", msg, Misc.Flatten(tr)), tag); 
            }
        }

        public void I(string tag, string format, params object[] args)
        {
            I(tag, String.Format(format, args));
        }

        public void W(string tag, string msg)
        {
            lock (_locker) {
                _ts.WriteLine(SourceLevels.Warning, msg, tag);
            }
        }

        public void W(string tag, Exception tr)
        {
            lock (_locker) {
                _ts.WriteLine(Misc.Flatten(tr).Message, tag); 
            }
        }

        public void W(string tag, string msg, Exception tr)
        {
            if (tr == null) {
                W(tag, msg);
            }

            lock (_locker) { 
                _ts.WriteLine(SourceLevels.Warning, String.Format("{0}:\r\n{1}", msg, Misc.Flatten(tr)), tag); 
            }
        }

        public void W(string tag, string format, params object[] args)
        {
            W(tag, string.Format(format, args));
        }

        public void E(string tag, string msg)
        {
            lock (_locker) { 
                _ts.WriteLine(SourceLevels.Error, tag, msg); 
            }
        }

        public void E(string tag, string msg, Exception tr)
        {
            if (tr == null) {
                E(tag, msg);
            }

            lock (_locker) { 
                _ts.WriteLine(SourceLevels.Error, String.Format("{0}:\r\n{1}", msg, Misc.Flatten(tr)), tag); 
            }
        }

        public void E(string tag, string format, params object[] args)
        {
            E(tag, string.Format(format, args));
        }

        #endregion

    }
}
