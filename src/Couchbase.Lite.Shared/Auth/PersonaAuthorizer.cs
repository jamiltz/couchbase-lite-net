//
// PersonaAuthorizer.cs
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
using System.Text;

using Couchbase.Lite;
using Couchbase.Lite.Auth;
using Couchbase.Lite.Util;

namespace Couchbase.Lite.Auth
{
    internal class PersonaAuthorizer : Authorizer
    {
        private static readonly string Tag = typeof(PersonaAuthorizer).Name;

        internal const string LoginParameterAssertion = "assertion";

        private static IDictionary<string, string> assertions;

        internal const string AssertionFieldEmail = "email";

        internal const string AssertionFieldOrigin = "origin";

        internal const string AssertionFieldExpiration = "exp";

        internal const string QueryParameter = "personaAssertion";

        private bool skipAssertionExpirationCheck;

        private string emailAddress;

        public PersonaAuthorizer(string emailAddress)
        {
            // set to true to skip checking whether assertions have expired (useful for testing)
            this.emailAddress = emailAddress;
        }

        public virtual void SetSkipAssertionExpirationCheck(bool skipAssertionExpirationCheck
            )
        {
            this.skipAssertionExpirationCheck = skipAssertionExpirationCheck;
        }

        public virtual bool IsSkipAssertionExpirationCheck()
        {
            return skipAssertionExpirationCheck;
        }

        public virtual string GetEmailAddress()
        {
            return emailAddress;
        }

        protected internal virtual bool IsAssertionExpired(IDictionary<string, object> parsedAssertion)
        {
            if (IsSkipAssertionExpirationCheck())
            {
                return false;
            }

            var exp = (DateTime)parsedAssertion.Get(AssertionFieldExpiration);
            var now = DateTime.Now;
            if (exp < now) {
                Log.To.Sync.W(Tag, string.Format("Assertion for {0} expired: {1}", 
                    new SecureLogString(emailAddress, LogMessageSensitivity.PotentiallyInsecure), exp));
                return true;
            }

            return false;
        }

        public virtual string AssertionForSite(Uri site)
        {
            var assertion = AssertionForEmailAndSite(emailAddress, site);
            if (assertion == null)
            {
                Log.To.Sync.W(Tag, String.Format("No assertion found for email: {0}, site: {1}", 
                    new SecureLogString(emailAddress, LogMessageSensitivity.PotentiallyInsecure), site));
                return null;
            }

            var result = ParseAssertion(assertion);
            return IsAssertionExpired (result) ? null : assertion;
        }

        public override string UserInfo { get { return null; } }

        public override string Scheme { get { return null; } }

        public override bool UsesCookieBasedLogin
        {
            get { return true; }
        }

        public override IDictionary<string, string> LoginParametersForSite(Uri site)
        {
            IDictionary<string, string> loginParameters = new Dictionary<string, string>();
            string assertion = AssertionForSite(site);
            if (assertion != null)
            {
                loginParameters[LoginParameterAssertion] = assertion;
                return loginParameters;
            }
            else
            {
                return null;
            }
        }

        public override string LoginPathForSite(Uri site)
        {
            return "/_persona";
        }

        public static string RegisterAssertion(string assertion)
        {
            lock (typeof(PersonaAuthorizer))
            {
                IDictionary<string, object> result = null;
                try {
                    result = ParseAssertion(assertion);
                } catch(ArgumentException) {
                    return null;
                }
                var email = (string)result.Get(AssertionFieldEmail);
                var origin = (string)result.Get(AssertionFieldOrigin);

                // Normalize the origin URL string:
                Uri originURL;
                if(origin == null || !Uri.TryCreate(origin, UriKind.Absolute, out originURL)) {
                    Log.To.Sync.E(Tag, "Couldn't parse origin from assertion '{0}', throwing...",
                        new SecureLogString(assertion, LogMessageSensitivity.Insecure));
                    throw new ArgumentException("Couldn't parse origin", "assertion");
                }

                origin = originURL.AbsoluteUri.ToLower();
  
                return RegisterAssertion(assertion, email, origin);
            }
        }

        /// <summary>
        /// don't use this!! this was factored out for testing purposes, and had to be
        /// made public since tests are in their own package.
        /// </summary>
        /// <remarks>
        /// don't use this!! this was factored out for testing purposes, and had to be
        /// made public since tests are in their own package.
        /// </remarks>
        internal static string RegisterAssertion(string assertion, string email, string origin)
        {
            lock (typeof(PersonaAuthorizer))
            {
                var key = GetKeyForEmailAndSite(email, origin);
                if (assertions == null)
                {
                    assertions = new Dictionary<string, string>();
                }
                Log.To.Sync.I(Tag, "Registering key [{0}, {1}]",
                    new SecureLogString(email, LogMessageSensitivity.PotentiallyInsecure), origin);
                assertions[key] = assertion;
                return email;
            }
        }

        public static IDictionary<string, object> ParseAssertion(string assertion)
        {
            // https://github.com/mozilla/id-specs/blob/prod/browserid/index.md
            // http://self-issued.info/docs/draft-jones-json-web-token-04.html
            if (assertion == null) {
                Log.To.Sync.E(Tag, "Assertion cannot be null in ParseAssertion, throwing...");
                throw new ArgumentNullException("assertion");
            }

            var result = new Dictionary<string, object>();
            var components = assertion.Split('.');
            // split on "."
            if (components.Length < 4) {
                Log.To.Sync.E(Tag, "Invalid assertion in ParseAssertion (number of '.' < 4): {0}, throwing...",
                    new SecureLogString(assertion, LogMessageSensitivity.Insecure));
            }

            var component1Decoded = Encoding.UTF8.GetString(StringUtils.ConvertFromUnpaddedBase64String(components[1]));
            var component3Decoded = Encoding.UTF8.GetString(StringUtils.ConvertFromUnpaddedBase64String(components[3]));
            try
            {
                var mapper = Manager.GetObjectMapper();

                var component1Json = mapper.ReadValue<object>(component1Decoded).AsDictionary<object, object>();
                var principal = component1Json.Get("principal").AsDictionary<object, object>();

                result[AssertionFieldEmail] = principal.Get("email");

                var component3Json = mapper.ReadValue<object>(component3Decoded).AsDictionary<object, object>();
                result[AssertionFieldOrigin] = component3Json.Get("aud");

                var expObject = (ulong)component3Json.Get("exp");
                var expDate = Misc.CreateDate(expObject);
                result[AssertionFieldExpiration] = expDate;
            } catch (Exception e) {
                throw Misc.CreateExceptionAndLog(Log.To.Sync, e, Tag,
                "Got exception while parsing assertion ({0})",
                    new SecureLogString(assertion, LogMessageSensitivity.Insecure));
            }

            return result;
        }

        public static string AssertionForEmailAndSite(string email, Uri site)
        {
            var key = GetKeyForEmailAndSite(email, site.ToString());
            Log.To.Sync.V(Tag, "Searching for key [{0}, {1}]", 
                new SecureLogString(email, LogMessageSensitivity.PotentiallyInsecure),
                site);
            
            return assertions.Get(key);
        }

        private static string GetKeyForEmailAndSite(string email, string site)
        {
            return String.Format("{0}:{1}", email, site);
        }

        public override string ToString()
        {
            var sb = new StringBuilder("[PersonaAuthorizer (");
            foreach (var pair in assertions) {
                if (pair.Key.StartsWith(emailAddress)) {
                    sb.AppendFormat("key={0} value={1}, ", 
                        new SecureLogString(pair.Key, LogMessageSensitivity.PotentiallyInsecure),
                        new SecureLogString(pair.Value, LogMessageSensitivity.Insecure));
                }
            }

            sb.Remove(sb.Length - 2, 2);
            sb.Append(")]");
            return sb.ToString();
        }
    }
}
