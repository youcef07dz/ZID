using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ZID
{
    internal delegate void StateLog(string message);

    // Result of loading state.json. NextId is already bumped past every item.
    internal sealed class StateData
    {
        public readonly List<DownloadItem> Items = new List<DownloadItem>();
        public int NextId = 1;
    }

    // Owns state.json. One field table drives both save and load, so a new
    // DownloadItem field is added in one place instead of four.
    internal sealed class StateStore
    {
        private const int SchemaVersion = 1;

        private delegate void FieldWriter(StringBuilder sb, DownloadItem item);
        private delegate void FieldReader(DownloadItem item, string obj);

        private sealed class Field
        {
            public readonly FieldWriter Write;
            public readonly FieldReader Read;
            public Field(FieldWriter write, FieldReader read) { Write = write; Read = read; }
        }

        private static readonly Field[] Fields = new Field[]
        {
            new Field(delegate(StringBuilder sb, DownloadItem it) { Num(sb, "id", it.Id); },
                      delegate(DownloadItem it, string o) { it.Id = ExtractInt(o, "id", 0); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "fileName", it.FileName); },
                      delegate(DownloadItem it, string o) { it.FileName = ExtractStr(o, "fileName", "download.bin"); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "url", it.Url); },
                      delegate(DownloadItem it, string o) { it.Url = ExtractStr(o, "url", ""); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { StrEnc(sb, "cookie", it.Cookie); },
                      delegate(DownloadItem it, string o) { it.Cookie = ExtractStrEnc(o, "cookie"); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "referer", it.Referer); },
                      delegate(DownloadItem it, string o) { it.Referer = ExtractStr(o, "referer", ""); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "userAgent", it.UserAgent); },
                      delegate(DownloadItem it, string o) { it.UserAgent = ExtractStr(o, "userAgent", ""); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { StrEnc(sb, "headers", it.Headers); },
                      delegate(DownloadItem it, string o) { it.Headers = ExtractStrEnc(o, "headers"); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "checksum", it.Checksum); },
                      delegate(DownloadItem it, string o) { it.Checksum = ExtractStr(o, "checksum", ""); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "mirror", it.Mirror); },
                      delegate(DownloadItem it, string o) { it.Mirror = ExtractStr(o, "mirror", ""); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { StrEnc(sb, "proxy", it.Proxy); },
                      delegate(DownloadItem it, string o) { it.Proxy = ExtractStrEnc(o, "proxy"); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "type", it.Type); },
                      delegate(DownloadItem it, string o) { it.Type = ExtractStr(o, "type", "FILE"); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Num(sb, "size", it.Size); },
                      delegate(DownloadItem it, string o) { it.Size = ExtractLong(o, "size", 0); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Num(sb, "progress", it.Progress); },
                      delegate(DownloadItem it, string o) { it.Progress = ExtractInt(o, "progress", 0); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "status", it.Status); },
                      delegate(DownloadItem it, string o) { it.Status = ExtractStr(o, "status", "Paused"); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "localPath", it.LocalPath); },
                      delegate(DownloadItem it, string o) { it.LocalPath = ExtractStr(o, "localPath", ""); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Num(sb, "bytesReceived", it.BytesReceived); },
                      delegate(DownloadItem it, string o) { it.BytesReceived = ExtractLong(o, "bytesReceived", 0); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Num(sb, "parts", it.MaxConn); },
                      delegate(DownloadItem it, string o) { it.MaxConn = ExtractInt(o, "parts", 0); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Num(sb, "speedKbps", it.SpeedKbps); },
                      delegate(DownloadItem it, string o) { it.SpeedKbps = ExtractLong(o, "speedKbps", 0); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "afterDone", it.AfterDone); },
                      delegate(DownloadItem it, string o) { it.AfterDone = ExtractStr(o, "afterDone", ""); }),
            new Field(delegate(StringBuilder sb, DownloadItem it)
                      {
                          Str(sb, "startAt", it.StartAt == DateTime.MinValue ? "" :
                                            it.StartAt.ToString("yyyy-MM-dd HH:mm"));
                      },
                      delegate(DownloadItem it, string o)
                      {
                          string d = ExtractStr(o, "startAt", "");
                          DateTime dt;
                          it.StartAt = DateTime.TryParse(d, out dt) ? dt : DateTime.MinValue;
                      }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Num(sb, "priority", it.Priority); },
                      delegate(DownloadItem it, string o) { it.Priority = ExtractInt(o, "priority", 0); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "kind", it.Kind); },
                      delegate(DownloadItem it, string o) { it.Kind = ExtractStr(o, "kind", ""); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "mergeKey", it.MergeKey); },
                      delegate(DownloadItem it, string o) { it.MergeKey = ExtractStr(o, "mergeKey", ""); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Flag(sb, "merged", it.Merged); },
                      delegate(DownloadItem it, string o) { it.Merged = ExtractInt(o, "merged", 0) != 0; }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Flag(sb, "auto", it.Auto); },
                      delegate(DownloadItem it, string o) { it.Auto = ExtractInt(o, "auto", 0) != 0; }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Flag(sb, "resume", it.ResumeOnLoad); },
                      delegate(DownloadItem it, string o) { it.ResumeOnLoad = ExtractInt(o, "resume", 0) != 0; }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Num(sb, "retryCount", it.RetryCount); },
                      delegate(DownloadItem it, string o) { it.RetryCount = ExtractInt(o, "retryCount", 0); }),
            new Field(delegate(StringBuilder sb, DownloadItem it) { Str(sb, "date", it.Date.ToString("yyyy-MM-dd HH:mm")); },
                      delegate(DownloadItem it, string o)
                      {
                          string d = ExtractStr(o, "date", "");
                          DateTime dt;
                          it.Date = DateTime.TryParse(d, out dt) ? dt : DateTime.Now;
                      })
        };

        private readonly string _path;
        private readonly object _saveLock = new object();

        public StateStore(string path) { _path = path; }

        public void Save(List<DownloadItem> items, int nextId, StateLog log)
        {
            SaveCaptured(Capture(items, nextId), log);
        }

        // Builds the JSON on the calling (UI) thread. The result is immutable,
        // so the background writer never reads a live DownloadItem again.
        public string Capture(List<DownloadItem> items, int nextId)
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("{\"schemaVersion\":").Append(SchemaVersion)
              .Append(",\"nextId\":").Append(nextId).Append(",\"items\":[");
            bool first = true;
            for (int i = 0; i < items.Count; i++)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                for (int f = 0; f < Fields.Length; f++)
                {
                    if (f > 0) sb.Append(',');
                    Fields[f].Write(sb, items[i]);
                }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public void SaveCaptured(string json, StateLog log)
        {
            lock (_saveLock)
            {
                try
                {
                    string tmp = _path + ".tmp";
                    WriteThrough(tmp, json);
                    if (File.Exists(_path))
                        File.Replace(tmp, _path, _path + ".bak");
                    else
                        File.Move(tmp, _path);
                }
                catch (Exception ex) { if (log != null) log("save state: " + ex.Message); }
            }
        }

        // FileOptions.WriteThrough keeps a power loss from leaving a half-written
        // tmp file that File.Replace would then promote to the real state.
        private static void WriteThrough(string path, string text)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write,
                                                  FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter sw = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                sw.Write(text);
                sw.Flush();
                fs.Flush();
            }
        }

        public StateData Load(StateLog log)
        {
            StateData data;
            if (TryLoad(_path, log, out data)) return data;

            if (File.Exists(_path) && log != null)
                log("load state: " + _path + " is unreadable, trying backup");
            if (TryLoad(_path + ".bak", log, out data))
            {
                if (log != null) log("load state: recovered from " + _path + ".bak");
                return data;
            }
            return new StateData();
        }

        private static bool TryLoad(string path, StateLog log, out StateData data)
        {
            data = new StateData();
            if (!File.Exists(path)) return false;
            try
            {
                string json = File.ReadAllText(path);
                if (json.IndexOf("\"items\":", StringComparison.Ordinal) < 0) return false;

                int version = ExtractInt(json, "schemaVersion", 0);
                if (version > SchemaVersion && log != null)
                    log("load state: schema " + version + " is newer than " + SchemaVersion +
                        ", unknown fields are ignored");

                data.NextId = Math.Max(1, ExtractInt(json, "nextId", 1));

                int idx = json.IndexOf("\"items\":[", StringComparison.Ordinal);
                if (idx < 0) return true;
                idx += 9;
                int end = json.LastIndexOf(']');
                if (end <= idx) return true;
                string arr = json.Substring(idx, end - idx);

                foreach (string obj in ExtractObjects(arr))
                {
                    try
                    {
                        DownloadItem it = new DownloadItem();
                        for (int f = 0; f < Fields.Length; f++)
                            Fields[f].Read(it, obj);

                        // Unfinished rows go back to Paused and resume on load.
                        DownloadState state = it.State;
                        if (state == DownloadState.Downloading || state == DownloadState.Starting ||
                            state == DownloadState.Queued || state == DownloadState.Merging)
                        {
                            it.State        = DownloadState.Paused;
                            it.ResumeOnLoad = true;
                            state           = DownloadState.Paused;
                        }

                        it.Eta     = (state == DownloadState.Completed) ? "Done"
                                   : (state == DownloadState.Failed)    ? "Error"
                                   : (state == DownloadState.Waiting)   ? "Waiting for video"
                                   : "Paused";
                        it.Speed   = 0;
                        it.WantRun = false;
                        it.Abort   = false;
                        if (state == DownloadState.Completed) it.AfterDoneRan = true;

                        if (it.Id >= data.NextId) data.NextId = it.Id + 1;
                        data.Items.Add(it);
                    }
                    catch (Exception ex)
                    {
                        if (log != null) log("load state: item skipped: " + ex.Message);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("load state: " + ex.Message);
                return false;
            }
        }

        private static void Num(StringBuilder sb, string name, long v)
        {
            sb.Append('"').Append(name).Append("\":").Append(v);
        }

        private static void Str(StringBuilder sb, string name, string v)
        {
            sb.Append('"').Append(name).Append("\":\"").Append(Aria.JsonEscape(v)).Append('"');
        }

        private static void StrEnc(StringBuilder sb, string name, string v)
        {
            Str(sb, name, Aria.Protect(v));
        }

        private static string ExtractStrEnc(string s, string key)
        {
            return Aria.Unprotect(ExtractStr(s, key, ""));
        }

        private static void Flag(StringBuilder sb, string name, bool v)
        {
            sb.Append('"').Append(name).Append("\":").Append(v ? 1 : 0);
        }

        // Quote-aware scanner: yields each top-level {...} object in the array.
        private static IEnumerable<string> ExtractObjects(string arr)
        {
            int i = 0;
            while (i < arr.Length)
            {
                if (arr[i] != '{') { i++; continue; }
                int start = i;
                int depth = 0;
                bool inStr = false, esc = false;
                for (; i < arr.Length; i++)
                {
                    char c = arr[i];
                    if (inStr)
                    {
                        if (esc) esc = false;
                        else if (c == '\\') esc = true;
                        else if (c == '"') inStr = false;
                    }
                    else
                    {
                        if (c == '"') inStr = true;
                        else if (c == '{') depth++;
                        else if (c == '}') { depth--; if (depth == 0) { i++; break; } }
                    }
                }
                yield return arr.Substring(start, i - start);
            }
        }

        private static int ExtractInt(string s, string key, int def)
        {
            string pat = "\"" + key + "\":";
            int i = s.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return def;
            i += pat.Length;
            int j = i; while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '-')) j++;
            int v; return int.TryParse(s.Substring(i, j - i), out v) ? v : def;
        }

        private static long ExtractLong(string s, string key, long def)
        {
            string pat = "\"" + key + "\":";
            int i = s.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return def;
            i += pat.Length;
            int j = i; while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '-')) j++;
            long v; return long.TryParse(s.Substring(i, j - i), out v) ? v : def;
        }

        private static string ExtractStr(string s, string key, string def)
        {
            string pat = "\"" + key + "\":\"";
            int i = s.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return def;
            i += pat.Length;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if      (n == 'n')  sb.Append('\n');
                    else if (n == 'r')  sb.Append('\r');
                    else if (n == 't')  sb.Append('\t');
                    else if (n == '"')  sb.Append('"');
                    else if (n == '\\') sb.Append('\\');
                    else                sb.Append(n);
                    i += 2;
                }
                else if (c == '"') { break; }
                else { sb.Append(c); i++; }
            }
            return sb.ToString();
        }
    }

    internal delegate void StateWrite(string json, StateLog log);

    // One background writer for state.json. Capture runs on the caller (UI)
    // thread, so the writer never reads live DownloadItem objects. Only the
    // newest pending snapshot is written; intermediate saves are coalesced.
    internal sealed class StateWriter
    {
        private readonly StateWrite _write;
        private readonly StateLog _log;
        private readonly object _sync = new object();
        private string _pending;
        private long _captured;
        private long _written;
        private bool _started;
        private Thread _thread;

        internal StateWriter(StateWrite write, StateLog log)
        {
            _write = write;
            _log = log;
        }

        internal void Save(string json)
        {
            lock (_sync)
            {
                _pending = json;
                _captured++;
                if (!_started)
                {
                    _started = true;
                    _thread = new Thread(Run);
                    _thread.IsBackground = true;
                    _thread.Name = "State writer";
                    _thread.Start();
                }
                Monitor.PulseAll(_sync);
            }
        }

        // True when every snapshot captured before this call is on disk.
        internal bool Flush(int timeoutMs)
        {
            lock (_sync)
            {
                long target = _captured;
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (_written < target)
                {
                    int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return false;
                    Monitor.Wait(_sync, left);
                }
                return true;
            }
        }

        private void Run()
        {
            while (true)
            {
                try
                {
                    string json;
                    long seq;
                    lock (_sync)
                    {
                        while (_pending == null) Monitor.Wait(_sync);
                        json = _pending;
                        _pending = null;
                        seq = _captured;
                    }

                    try { _write(json, _log); }
                    catch (Exception ex) { if (_log != null) _log("save state: " + ex.Message); }

                    lock (_sync)
                    {
                        if (seq > _written) _written = seq;
                        Monitor.PulseAll(_sync);
                    }
                }
                catch (Exception ex)
                {
                    // Losing this thread would stop every later save.
                    if (_log != null) _log("state writer: " + ex.Message);
                }
            }
        }
    }
}
