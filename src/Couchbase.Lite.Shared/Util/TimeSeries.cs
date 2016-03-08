//
// TimeSeries.cs
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
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.IO.MemoryMappedFiles;

namespace Couchbase.Lite.Util
{
    internal sealed class TimeSeries : IDisposable
    {
        private static readonly string Tag = typeof(TimeSeries).Name;
        private const int MaxDocSize = 100 * 1024; // bytes
        private const int MaxDocEventCount = 1000;

        private TaskFactory _scheduler = new TaskFactory(new SingleTaskThreadpoolScheduler());
        private Database _db;
        private FileStream _out;
        private Exception _error;
        private uint _eventsInFile;
        private string _docType;
        private string _path;

        public TimeSeries(Database db, string docType)
        {
            if (db == null) {
                Log.To.NoDomain.E(Tag, "db cannot be null in ctor, throwing...");
                throw new ArgumentNullException("db");
            }

            if (docType == null) {
                Log.To.NoDomain.E(Tag, "docType cannot be null in ctor, throwing...");
                throw new ArgumentNullException("docType");
            }

            var filename = String.Format("TS-{0}.tslog", docType);
            _path = Path.Combine(db.DbDirectory, filename);
            try {
                _out = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            } catch(Exception e) {
                throw Misc.CreateExceptionAndLog(Log.To.NoDomain, e, Tag, "Failed to open {0} for write",
                    _path);
            }

            _db = db;
            _docType = docType;
        }

        public void AddEvent(IDictionary<string, object> eventToAdd)
        {
            AddEvent(eventToAdd, DateTime.Now);
        }

        public void AddEvent(IDictionary<string, object> eventToAdd, DateTime time)
        {
            if (eventToAdd == null) {
                Log.To.NoDomain.E(Tag, "eventToAdd cannot be null in AddEvent, throwing...");
                throw new ArgumentNullException("eventToAdd");
            }

            var props = new Dictionary<string, object>(eventToAdd);
            _scheduler.StartNew(() =>
            {
                props["t"] = time.MillisecondsSinceEpoch();
                var json = default(byte[]);
                try {
                    json = Manager.GetObjectMapper().WriteValueAsBytes(props).ToArray();
                } catch(Exception e) {
                    Log.To.NoDomain.W(Tag, "Got exception trying to serialize json, aborting save...", e);
                    return;
                }

                var pos = _out.Position;
                if(pos + json.Length + 20 > MaxDocSize || _eventsInFile > MaxDocEventCount) {
                    TransferToDB();
                    pos = 0;
                }

                try {
                    if(pos == 0) {
                        _out.WriteByte((byte)'[');
                    } else {
                        _out.WriteByte((byte)',');
                        _out.WriteByte((byte)'\n');
                    }

                    _out.Write(json, 0, json.Length);
                    _out.Flush();
                } catch(Exception e) {
                    Log.To.NoDomain.W(Tag, "Error while writing event to file, recording...");
                    _error = e;
                }

                ++_eventsInFile;
            });
        }

        public Task Flush()
        {
            return _scheduler.StartNew(() =>
            {
                if(_eventsInFile > 0 || _out.Position > 0) {
                    TransferToDB();
                }
            });
        }

        public Replication CreatePushReplication(Uri remoteUrl, bool purgeWhenPushed)
        {
            var push = _db.CreatePushReplication(remoteUrl);
            _db.SetFilter("com.couchbase.DocIDPrefix", (rev, props) =>
                rev.Document.Id.StartsWith(props.GetCast<string>("prefix")));

            push.Filter = "com.couchbase.DocIDPrefix";
            push.FilterParams = new Dictionary<string, object> {
                { "prefix", String.Format("TS-{0}-", _docType) }
            };

            push.Options = new ReplicationOptionsDictionary {
                { ReplicationOptionsDictionaryKeys.PurgePushed, purgeWhenPushed },
                { ReplicationOptionsDictionaryKeys.AllNew, true },
            };

            return push;
        }

        public IEnumerable<IDictionary<string, object>> GetEventsInRange(DateTime start, DateTime end)
        {
            // Get the first series from the doc containing start (if any):
            IList<IDictionary<string, object>> curSeries = null;
            if (start > DateTime.MinValue) {
                curSeries = GetEvents(start);
            }

            // Start forwards query if I haven't already:
            var q = _db.CreateAllDocumentsQuery();
            ulong startStamp;
            if (curSeries.Count > 0) {
                startStamp = curSeries.Last().GetCast<ulong>("t");
                q.InclusiveStart = false;
            } else {
                startStamp = start > DateTime.MinValue ? start.MillisecondsSinceEpoch() : 0;
            }

            var endStamp = end < DateTime.MaxValue ? end.MillisecondsSinceEpoch() : UInt64.MaxValue;

            var e = default(QueryEnumerator);
            if (startStamp < endStamp) {
                q.StartKey = MakeDocID(startStamp);
                q.EndKey = MakeDocID(endStamp);
                e = q.Run();
            }

            // OK, here is the block for the enumerator:
            var curIndex = 0;
            while (true) {
                while (curIndex >= curSeries.Count) {
                    if (e == null) {
                        yield return null;
                    }

                    if (!e.MoveNext()) {
                        yield return null;
                    }

                    curSeries = e.Current.DocumentProperties.Get("events").AsList<IDictionary<string, object>>();
                    curIndex = 0;
                }

                // Return the next event from curSeries
                var gotEvent = curSeries[curIndex++];
                var timeStamp = gotEvent.GetCast<ulong>("t");
                if (timeStamp > endStamp) {
                    yield return null;
                }

                gotEvent["t"] = Misc.CreateDate(timeStamp);
                yield return gotEvent;
            }
        }

        private IList<IDictionary<string, object>> GetEvents(DateTime t)
        {
            var q = _db.CreateAllDocumentsQuery();
            var timestamp = t > DateTime.MinValue ? t.MillisecondsSinceEpoch() : 0;
            q.StartKey = MakeDocID(timestamp);
            q.Descending = true;
            q.Limit = 1;
            q.Prefetch = true;
            var row = default(QueryRow);
            foreach (var r in q.Run()) {
                row = r;
            }

            if (row == null) {
                return new List<IDictionary<string, object>>();
            }

            var events = row.DocumentProperties.Get("events").AsList<IDictionary<string, object>>();
            return events.SkipWhile(x => x.GetCast<ulong>("t") < timestamp).ToList();
        }

        private void TransferToDB()
        {
            try {
                _out.WriteByte((byte)']');
                _out.Flush();
                _out.Dispose();
            } catch(Exception e) {
                Log.To.NoDomain.W(Tag, "Error writing final byte to file, recording...", e);
                _error = e;
                return;
            }

            // Parse a JSON array from the file:
            var stream = default(Stream);
            #if !NET_3_5
            var mapped = default(MemoryMappedFile);
            try {
                mapped = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, "TimeSeries", 0, MemoryMappedFileAccess.Read);
                stream = mapped.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            } catch(Exception e) {
                Log.To.NoDomain.W(Tag, "Error creating or reading memory mapped file, recording...", e);
                _error = e;
                return;
            }
            #else
            stream = File.OpenRead(_path);
            #endif

            var events = default(IList<object>);
            try {
                events = Manager.GetObjectMapper().ReadValue<IList<object>>(stream);
            } catch(Exception e) {
                Log.To.NoDomain.W(Tag, "Error reading json from file, recording...", e);
                _error = e;
                return;
            } finally {
                stream.Dispose();
                #if !NET_3_5
                mapped.Dispose();
                #endif
            }

            // Add the events to documents in batches:
            var count = events.Count;
            for (var pos = 0; pos < count; pos += MaxDocEventCount) {
                var group = events.Skip(pos).Take(MaxDocEventCount).ToList();
                AddEventsToDB(group);
            }

            // Now erase the file for subsequent events:
            _out.Dispose();
            _out = File.Open(_path, FileMode.Truncate, FileAccess.Write);
            _eventsInFile = 0;

        }

        private void AddEventsToDB(IList<object> events)
        {
            if (events.Count == 0) {
                return;
            }

            var nextEvent = events[0].AsDictionary<string, object>();
            if (nextEvent == null) {
                Log.To.NoDomain.W(Tag, "Invalid object found in log events, aborting...");
                return;
            }

            var timestamp = nextEvent.GetCast<ulong>("t");
            var docID = MakeDocID(timestamp);
            _db.RunAsync((d) =>
            {
                try {
                    _db.GetDocument(docID).PutProperties(new Dictionary<string, object> {
                        { "type", _docType },
                        { "events", events }
                    });
                } catch(Exception e) {
                    Log.To.NoDomain.W(Tag, String.Format("Couldn't save events to '{0}'", docID), e);
                }
            });
        }

        private string MakeDocID(ulong timestamp)
        {
            return String.Format("TS-{0}-{1:D8}", _docType, timestamp);
        }

        private void Stop()
        {
            if (_scheduler != null) {
                _scheduler.StartNew(() =>
                {
                    if (_out != null) {
                        _out.Dispose();
                        _out = null;
                    }
                });

                _scheduler = null;
            }

            _error = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

