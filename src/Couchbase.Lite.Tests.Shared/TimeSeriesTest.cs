//
// TimeSeriesTest.cs
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
using NUnit.Framework;
using Couchbase.Lite.Util;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections.Generic;

namespace Couchbase.Lite
{
    [TestFixture("ForestDB")]
    public class TimeSeriesTest : LiteTestCase
    {
        private TimeSeries _ts;

        public TimeSeriesTest(string storageType) : base(storageType)
        {
        }

        protected override void SetUp()
        {
            base.SetUp();
            Assert.DoesNotThrow(() => _ts = new TimeSeries(database, "tstest"), "Could not create ts");
        }

        [Test]
        public void TestTimeSeries()
        {
            GenerateEventsSync();
            var i = 0;
            var lastT = 0UL;
            foreach (var row in database.CreateAllDocumentsQuery().Run()) {
                var events = row.Document.GetProperty("events").AsList<IDictionary<string, object>>();
                Console.WriteLine("Doc {0}: {1} events", row.DocumentId, events.Count);
                foreach (var evnt in events) {
                    Assert.AreEqual(i++, evnt.GetCast<int>("i"));
                    var t = evnt.GetCast<ulong>("t");
                    Assert.IsTrue(t >= lastT);
                    lastT = t;
                }
            }
        }

        // Generates 10000 events, one every 100µsec. Fulfils an expectation when they're all in the db.
        private WaitHandle GenerateEventsAsync()
        {
            var mre = new ManualResetEventSlim();
            Task.Factory.StartNew(() =>
            {
                Console.WriteLine("Generating events...");
                var sw = new Stopwatch();
                var random = new Random();
                for (int i = 0; i < 10000; i++) {
                    sw.Start();
                    SpinWait.SpinUntil(() => (sw.ElapsedTicks * 1000000) / Stopwatch.Frequency >= 100);
                    sw.Reset();
                    var r = random.Next();
                    _ts.AddEvent(new Dictionary<string, object> {
                        { "i", i },
                        { "random", r }
                    });
                }

                _ts.Flush().ContinueWith(t => 
                {
                    mre.Set();
                    mre.Dispose();
                });
            });

            return mre.WaitHandle;
        }

        private void GenerateEventsSync()
        {
            GenerateEventsAsync().WaitOne(TimeSpan.FromSeconds(500));
            Console.WriteLine("...Done generating events");
        }
    }
}

