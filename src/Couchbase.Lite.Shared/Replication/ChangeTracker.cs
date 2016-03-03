//
// ChangeTracker.cs
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Couchbase.Lite;
using Couchbase.Lite.Auth;
using Couchbase.Lite.Replicator;
using Couchbase.Lite.Util;

namespace Couchbase.Lite.Replicator
{
    internal enum ChangeTrackerMode
    {
        OneShot,
        LongPoll
    }


    /// <summary>
    /// Reads the continuous-mode _changes feed of a database, and sends the
    /// individual change entries to its client's changeTrackerReceivedChange()
    /// </summary>
    internal class ChangeTracker
    {
        private const string TAG = "ChangeTracker";

        private int _heartbeatMilliseconds = 300000;

        protected Uri databaseURL;

        protected IChangeTrackerClient _changeTrackerClient;

        protected ChangeTrackerMode _mode;

        private Object lastSequenceID;

        private Boolean includeConflicts;

        private TaskFactory WorkExecutor;

        private DateTime _startTime;

        private ManualResetEventSlim _pauseWait = new ManualResetEventSlim(true);

        private readonly object stopMutex = new object();

        private HttpRequestMessage Request;

        private String filterName;

        private IDictionary<String, Object> filterParams;

        private IList<String> docIDs;

        internal ChangeTrackerBackoff backoff;

        protected internal IDictionary<string, object> RequestHeaders;

        private CancellationTokenSource tokenSource;
        private bool _initialSync;

        CancellationTokenSource changesFeedRequestTokenSource;

        private CouchbaseLiteHttpClient _httpClient;

        internal RemoteServerVersion ServerType { get; private set; }

        public bool Paused
        {
            get { return !_pauseWait.IsSet; }
            set
            {
                if(value != Paused) {
                    Log.To.ChangeTracker.I(TAG, "{0} {1}...", value ? "Pausing" : "Resuming", this);
                    if(value) {
                        _pauseWait.Reset();
                    } else {
                        _pauseWait.Set();
                    }
                }
            }
        }

        public IAuthenticator Authenticator { get; set; }

        public bool UsePost { get; set; }

        public Exception Error { get; private set; }

        public TimeSpan PollInterval { get; set; }

        public ChangeTracker(Uri databaseURL, ChangeTrackerMode mode, object lastSequenceID, 
            bool includeConflicts, bool initialSync, IChangeTrackerClient client, TaskFactory workExecutor = null)
        {
            // does not work, do not use it.
            this.databaseURL = databaseURL;
            this._mode = mode;
            this.includeConflicts = includeConflicts;
            this.lastSequenceID = lastSequenceID;
            this._changeTrackerClient = client;
            this.RequestHeaders = new Dictionary<string, object>();
            this.tokenSource = new CancellationTokenSource();
            _initialSync = initialSync;
            WorkExecutor = workExecutor ?? Task.Factory;
        }

        public void SetFilterName(string filterName)
        {
            this.filterName = filterName;
        }

        public void SetFilterParams(IDictionary<String, Object> filterParams)
        {
            this.filterParams = filterParams;
        }

        public void SetClient(IChangeTrackerClient client)
        {
            this._changeTrackerClient = client;
        }

        public string GetChangesFeedPath()
        {
            if (UsePost) {
                return "_changes";
            }

            var path = new StringBuilder("_changes?feed=");
            path.Append(GetFeed());

            path.Append(string.Format("&heartbeat={0}", _heartbeatMilliseconds));
            if (includeConflicts) {
                path.Append("&style=all_docs");
            }

            if (lastSequenceID != null && lastSequenceID.ToString() != "0") {
                path.Append("&since=");
                path.Append(Uri.EscapeUriString(lastSequenceID.ToString()));
            } else if(_initialSync) {
                _initialSync = false;
                // On first replication we can skip getting deleted docs. (SG enhancement in ver. 1.2)
                path.Append("&active_only=true");
            }

            if (docIDs != null && docIDs.Count > 0) {
                filterName = "_doc_ids";
                filterParams = new Dictionary<string, object>();
                filterParams["doc_ids"] = docIDs;
            }

            if (filterName != null) {
                path.Append("&filter=");
                path.Append(Uri.EscapeUriString(filterName));
                if (filterParams != null) {
                    foreach (string filterParamKey in filterParams.Keys) {
                        var value = filterParams.Get(filterParamKey);
                        if (!(value is string)) {
                            try {
                                value = Manager.GetObjectMapper().WriteValueAsString(value);
                            } catch (Exception e) {
                                Log.To.ChangeTracker.E(TAG, "Unable to JSON-serialize a filter parameter value.", e);
                                throw new InvalidOperationException("Unable to JSON-serialize a filter parameter value.", e);
                            }
                        }
                        path.Append("&");
                        path.Append(Uri.EscapeUriString(filterParamKey));
                        path.Append("=");
                        path.Append(Uri.EscapeUriString(value.ToString()));
                    }
                }
            }

            return path.ToString();
        }

        public virtual Uri GetChangesFeedURL()
        {
            var dbURLString = databaseURL.ToString();
            if(!dbURLString.EndsWith("/", StringComparison.Ordinal)) {
                dbURLString += "/";
            }

            dbURLString += GetChangesFeedPath();

            Uri result = null;
            if (!Uri.TryCreate(dbURLString, UriKind.Absolute, out result)) {
                Log.To.ChangeTracker.E(TAG, "Changes feed ULR is malformed");
                return null;
            }

            return result;
        }
            
        public void Run()
        {
            IsRunning = true;

            var clientCopy = _changeTrackerClient;
            if (clientCopy == null)
            {
                // This is a race condition that can be reproduced by calling cbpuller.start() and cbpuller.stop()
                // directly afterwards.  What happens is that by the time the Changetracker thread fires up,
                // the cbpuller has already set this.client to null.  See issue #109
                Log.To.ChangeTracker.W(TAG, "ChangeTracker run() loop aborting because client == null");
                return;
            }

            if (tokenSource.IsCancellationRequested) {
                tokenSource.Dispose();
                tokenSource = new CancellationTokenSource();
            }

            if (backoff == null) {
                backoff = new ChangeTrackerBackoff();
            }

            _startTime = DateTime.Now;
            if (Request != null)
            {
                Request.Dispose();
                Request = null;
            }

            var url = GetChangesFeedURL();
            if(UsePost) {
                Request = new HttpRequestMessage(HttpMethod.Post, url);
                var body = GetChangesFeedPostBody();
                Request.Content = new StringContent(body);
                Request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            } else {
                Request = new HttpRequestMessage(HttpMethod.Get, url);
            }
            AddRequestHeaders(Request);

            Log.To.ChangeTracker.V(TAG, "Making request to {0}", new SecureLogUri(url));
            if (tokenSource.Token.IsCancellationRequested) {
                return;
            }
                
            try {
                changesFeedRequestTokenSource = CancellationTokenSource.CreateLinkedTokenSource(tokenSource.Token);

                var option = _mode == ChangeTrackerMode.LongPoll ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
                _httpClient.Authenticator = Authenticator;
                var info = _httpClient.SendAsync(
                    Request, 
                    option,
                    changesFeedRequestTokenSource.Token
                );

                info.ContinueWith(ChangeFeedResponseHandler, changesFeedRequestTokenSource.Token, 
                    TaskContinuationOptions.LongRunning, 
                    TaskScheduler.Default);
            }
            catch (Exception e)
            {
                if (Misc.IsTransientNetworkError(e)) {
                    Log.To.ChangeTracker.I(TAG, "Connection error #{0}, retrying in {1}ms: {2}", backoff.NumAttempts,
                        backoff.GetSleepTime(), e);
                    backoff.SleepAppropriateAmountOfTime();
                    if (IsRunning) {
                        Run();
                    }
                } else {
                    Log.To.ChangeTracker.I(TAG, "Can't connect; giving up: {0}", e);
                    Error = e;
                    Stop();
                }
            }
        }

        private Task ChangeFeedResponseHandler(Task<HttpResponseMessage> responseTask)
        {
            Misc.SafeDispose(ref changesFeedRequestTokenSource);

            if (responseTask.IsCanceled || responseTask.IsFaulted) {
                if (!responseTask.IsCanceled) {
                    var err = Misc.Flatten(responseTask.Exception);
                    var statusCode = Misc.GetStatusCode(err as WebException);
                    if (UsePost && statusCode.HasValue && statusCode.Value == HttpStatusCode.MethodNotAllowed) {
                        // Remote doesn't allow POST _changes, retry as GET
                        UsePost = false;
                        Log.To.ChangeTracker.I(TAG, "Remote server doesn't support POST _changes, " +
                            "retrying as GET");
                        WorkExecutor.StartNew(Run);
                        return Task.FromResult(false);
                    }

                    Log.To.ChangeTracker.I(TAG, String.Format("{0} ChangeFeedResponseHandler faulted", this),
                        err);
                    if (_mode != ChangeTrackerMode.LongPoll || !Misc.IsTransientNetworkError(err)) {
                        Stop();
                    } else if(IsRunning) {
                        backoff.SleepAppropriateAmountOfTime();
                        Log.To.ChangeTracker.I(TAG, "{0} retrying...", this);
                        WorkExecutor.StartNew(Run);
                    }
                }

                return Task.FromResult(false);
            }

            var response = responseTask.Result;
            if (response == null)
                return Task.FromResult(false);
            
            var status = response.StatusCode;
            UpdateServerType(response);

            if ((Int32)status >= 300)
            {
                if (UsePost && status == HttpStatusCode.MethodNotAllowed) {
                    // Remote doesn't allow POST _changes, retry as GET
                    UsePost = false;
                    Log.To.ChangeTracker.I(TAG, "Remote server ({0}) doesn't support POST _changes, " +
                    "retrying as GET", ServerType);
                    WorkExecutor.StartNew(Run);
                    return Task.FromResult(false);
                }

                if (Misc.IsTransientError(status) && _mode == ChangeTrackerMode.LongPoll) {
                    Log.To.ChangeTracker.I(TAG, "{0} transient error ({1}) detected, sleeping...", this,
                        status);
                    backoff.SleepAppropriateAmountOfTime();
                    Log.To.ChangeTracker.I(TAG, "{0} retrying...", this);
                    WorkExecutor.StartNew(Run);
                    return Task.FromResult(false);
                }

                var msg = response.Content != null 
                    ? String.Format("Change tracker got error with status code: {0}", status)
                    : String.Format("Change tracker got error with status code: {0} and null response content", status);
                Log.To.ChangeTracker.E(TAG, msg);
                Error = new CouchbaseLiteException (msg, new Status (status.GetStatusCode ()));
                Stop();
                response.Dispose();
                return Task.FromResult(false);
            }

            switch (_mode)  {
                case ChangeTrackerMode.LongPoll:
                    if (response.Content == null) {
                        throw Misc.CreateExceptionAndLog(Log.To.ChangeTracker, status.GetStatusCode(), TAG,
                            "Got empty change tracker response");
                    }
                            
                    Log.To.ChangeTracker.D(TAG, "Getting stream from change tracker response");
                    return response.Content.ReadAsStreamAsync().ContinueWith(t => {
                        try {
                            ProcessLongPollStream(t);
                            backoff.ResetBackoff();
                        } catch(Exception e) {
                            Log.To.ChangeTracker.I(TAG, 
                                String.Format("{0} exception during changes feed processing, sleeping...", this), e);
                            backoff.SleepAppropriateAmountOfTime();
                            Log.To.ChangeTracker.I(TAG, "{0} retrying...", this);
                            WorkExecutor.StartNew(Run);
                        } finally {
                            response.Dispose();
                        }
                    });
                default:
                    return response.Content.ReadAsStreamAsync().ContinueWith(t => {
                        try {
                            ProcessOneShotStream(t);
                            backoff.ResetBackoff();
                        } finally {
                            response.Dispose();
                        }
                    });
            }
        }

        public bool ReceivedChange(IDictionary<string, object> change)
        {
            if (change == null) {
                return false;
            }

            var seq = change.Get("seq");
            if (seq == null) {
                return false;
            }

            //pass the change to the client on the thread that created this change tracker
            if (_changeTrackerClient != null) {
                Log.To.ChangeTracker.V(TAG, "{0} posting change", this);
                _changeTrackerClient.ChangeTrackerReceivedChange(change);
            }

            lastSequenceID = seq;
            return true;
        }

        public bool ReceivedPollResponse(IJsonSerializer jsonReader, ref bool timedOut)
        {
            bool started = false;
            var start = DateTime.Now;
            try {
                while (jsonReader.Read()) {
                    _pauseWait.Wait();
                    if (jsonReader.CurrentToken == JsonToken.StartArray) {
                            timedOut = true;
                        started = true;
                    } else if (jsonReader.CurrentToken == JsonToken.EndArray) {
                        started = false;
                    } else if (started) {
                        IDictionary<string, object> change;
                        try {
                            change = jsonReader.DeserializeNextObject();
                        } catch(Exception e) {
                            var ex = e as CouchbaseLiteException;
                            if (ex == null || ex.Code != StatusCode.BadJson) {
                                Log.To.ChangeTracker.W(TAG, "Failure during change tracker JSON parsing", e);
                                throw;
                            }
                                
                            return false;
                        }

                        if (!ReceivedChange(change)) {
                                Log.To.ChangeTracker.W(TAG, "{0} received unparseable change line from server: {1}", 
                                    this, new SecureLogJsonString(change, LogMessageSensitivity.PotentiallyInsecure));
                            return false;
                        }

                        timedOut = false;
                    }
                }
            } catch (CouchbaseLiteException e) {
                var elapsed = DateTime.Now - start;
                timedOut = timedOut && elapsed.TotalSeconds >= 30;
                if (e.CBLStatus.Code == StatusCode.BadJson && timedOut) {
                    return false;
                }

                throw;
            }

            return true;
        }

        public virtual bool Start()
        {
            if (IsRunning) {
                return false;
            }

            Log.To.ChangeTracker.I(TAG, "Starting {0}...", this);
            _httpClient = _changeTrackerClient.GetHttpClient();
            Error = null;
            WorkExecutor.StartNew(Run);
            Log.To.ChangeTracker.I(TAG, "Started {0}", this);

            return true;
        }

        public virtual void Stop()
        {
            // Lock to prevent multiple calls to Stop() method from different
            // threads (eg. one from ChangeTracker itself and one from any other
            // consumers).
            lock(stopMutex)
            {
                if (!IsRunning) {
                    return;
                }

                Log.To.ChangeTracker.I(TAG, "Stopping {0}...");

                IsRunning = false;
                Misc.SafeDispose(ref _httpClient);

                var feedTokenSource = changesFeedRequestTokenSource;
                if (feedTokenSource != null && !feedTokenSource.IsCancellationRequested) {
                    try {
                        feedTokenSource.Cancel();
                    } catch (ObjectDisposedException) {
                        Log.To.ChangeTracker.W(TAG, "Race condition on changesFeedRequestTokenSource detected");
                    } catch (AggregateException e) {
                        if (e.InnerException is ObjectDisposedException) {
                            Log.To.ChangeTracker.W(TAG, "Race condition on changesFeedRequestTokenSource detected");
                        } else {
                            throw;
                        }
                    }
                }

                Stopped();
            }
        }

        public void Stopped()
        {
            if (_changeTrackerClient != null)
            {
                Log.To.ChangeTracker.V(TAG, "{0} posting stopped to client", this);
                _changeTrackerClient.ChangeTrackerStopped(this);
            }
            _changeTrackerClient = null;
            Log.To.ChangeTracker.D(TAG, "change tracker client should be null now");
        }

        public void SetDocIDs(IList<string> docIDs)
        {
            this.docIDs = docIDs;
        }

        public bool IsRunning
        {
            get; private set;
        }

        private void ProcessLongPollStream(Task<Stream> t)
        {
            Log.To.ChangeTracker.D(TAG, "Got stream from change tracker response");
            bool beforeFirstItem = true;
            bool responseOK = false;
            using (var jsonReader = Manager.GetObjectMapper().StartIncrementalParse(t.Result)) {
                responseOK = ReceivedPollResponse(jsonReader, ref beforeFirstItem);
            }

            Log.To.ChangeTracker.V(TAG, "{0} Finished polling", this);

            if (responseOK) {
                backoff.ResetBackoff();
                if (PollInterval.TotalMilliseconds > 30) {
                    Log.To.ChangeTracker.I(TAG, "{0} next poll of _changes feed in {1} sec", this, PollInterval.TotalSeconds);
                    Task.Delay(PollInterval).ContinueWith(_ => WorkExecutor.StartNew(Run));
                } else {
                    WorkExecutor.StartNew(Run);
                }
            } else {
                backoff.SleepAppropriateAmountOfTime();
                if (beforeFirstItem) {
                    var elapsed = DateTime.Now - _startTime;
                    Log.To.ChangeTracker.W(TAG, "{0} longpoll connection closed (by proxy?) after {0} sec", 
                        this, elapsed.TotalSeconds);

                    // Looks like the connection got closed by a proxy (like AWS' load balancer) while the
                    // server was waiting for a change to send, due to lack of activity.
                    // Lower the heartbeat time to work around this, and reconnect:
                    _heartbeatMilliseconds = (int)(elapsed.TotalMilliseconds * 0.75f);
                    backoff.ResetBackoff();
                    WorkExecutor.StartNew(Run);
                } else {
                    Log.To.ChangeTracker.W(TAG, "{0} Received improper _changes feed response", this);
                    WorkExecutor.StartNew(Stop);
                }
            }
        }

        private void ProcessOneShotStream(Task<Stream> t)
        {
            using (var jsonReader = Manager.GetObjectMapper().StartIncrementalParse(t.Result)) {
                bool timedOut = false;
                ReceivedPollResponse(jsonReader, ref timedOut);
            }

            Stopped();
        }

        private void AddRequestHeaders(HttpRequestMessage request)
        {
            foreach (string requestHeaderKey in RequestHeaders.Keys)
            {
                request.Headers.Add(requestHeaderKey, RequestHeaders.Get(requestHeaderKey).ToString());
            }
        }

        private string GetFeed()
        {
            switch (_mode)
            {
                case ChangeTrackerMode.LongPoll:
                    return "longpoll";
                default:
                    return "normal";
            }
        }

        private void UpdateServerType(HttpResponseMessage response)
        {
            var server = response.Headers.Server;
            if (server != null && server.Any()) {
                var serverString = String.Join(" ", server.Select(pi => pi.Product).Where(pi => pi != null).ToStringArray());
                ServerType = new RemoteServerVersion(serverString);
                Log.To.ChangeTracker.I(TAG, "{0} Server Version: {1}", this, ServerType);
            }
        }

        internal IDictionary<string, object> GetChangesFeedParams()
        {
            if (docIDs != null && docIDs.Count > 0) {
                filterName = "_doc_ids";
                filterParams = new Dictionary<string, object>();
                filterParams["doc_ids"] = docIDs;
            }

            var bodyParams = new Dictionary<string, object>();
            bodyParams["feed"] = GetFeed();
            bodyParams["heartbeat"] = _heartbeatMilliseconds;

            if (includeConflicts) {
                bodyParams["style"] = "all_docs";
            }

            if (lastSequenceID != null && lastSequenceID.ToString() != "0") {
                Int64 sequenceAsLong;
                var success = Int64.TryParse(lastSequenceID.ToString(), out sequenceAsLong);
                bodyParams["since"] = success ? sequenceAsLong : lastSequenceID;
            } else if(_initialSync) {
                _initialSync = false;
                // On first replication we can skip getting deleted docs. (SG enhancement in ver. 1.2)
                bodyParams["active_only"] = true;
            }

            if (filterName != null) {
                bodyParams["filter"] = filterName;
                bodyParams.PutAll(filterParams);
            }

            return bodyParams;
        }

        internal string GetChangesFeedPostBody()
        {
            var parameters = GetChangesFeedParams();
            var mapper = Manager.GetObjectMapper();
            var body = mapper.WriteValueAsString(parameters);
            return body;
        }

        public override string ToString()
        {
            return String.Format("ChangeTracker[URL={0}]", new SecureLogUri(databaseURL));
        }
    }
}
