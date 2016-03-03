﻿//
// CouchbaseLiteHttpClient.cs
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
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using Couchbase.Lite.Replicator;
using Couchbase.Lite.Auth;
using Couchbase.Lite.Util;

namespace Couchbase.Lite
{
    internal sealed class CouchbaseLiteHttpClient : IDisposable
    {
        private HttpClient _httpClient;
        private DefaultAuthHandler _authHandler;

        public IAuthenticator Authenticator { get; set; }

        public CouchbaseLiteHttpClient(HttpClient client, DefaultAuthHandler authHandler)
        {
            _httpClient = client;
            _authHandler = authHandler;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            return SendAsync(message, HttpCompletionOption.ResponseContentRead, token);
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, HttpCompletionOption option, CancellationToken token)
        {
            var challengeResponseAuth = Authenticator as IChallengeResponseAuthenticator;
            if (challengeResponseAuth != null) {
                if (_authHandler != null) {
                    _authHandler.Authenticator = challengeResponseAuth;
                }

                challengeResponseAuth.PrepareWithRequest(message);
            }

            var authHeader = AuthUtils.GetAuthenticationHeaderValue(Authenticator, message.RequestUri);
            if (authHeader != null) {
                _httpClient.DefaultRequestHeaders.Authorization = authHeader;
            }

            return _httpClient.SendAsync(message, option, token);
        }

        public void Dispose()
        {
            Misc.SafeDispose(ref _httpClient);
            Misc.SafeDispose(ref _authHandler);
        }
    }
}

