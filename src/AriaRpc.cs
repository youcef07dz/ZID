using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace ZID
{
    // One aria2c process for the whole queue. The app talks JSON-RPC to it on
    // loopback, so pause/unpause are real and no per-download process is needed.
    internal sealed class AriaRpc
    {
        private readonly string _exe;
        private readonly string _toolsDir;
        private readonly string _certs;
        private readonly string _secret;
        private readonly object _lock = new object();
        private Process _proc;
        private int _port;
        private string _lastError = "";
        private bool _versionLogged;

        public AriaRpc(string exe, string toolsDir, string certs)
        {
            _exe = exe;
            _toolsDir = toolsDir;
            _certs = certs;
            _secret = Guid.NewGuid().ToString("N");
        }

        public string LastError { get { return _lastError; } }

        // Starts (or restarts) the daemon. Returns false and sets LastError on failure.
        public bool EnsureStarted()
        {
            lock (_lock)
            {
                if (_proc != null && !_proc.HasExited)
                {
                    try { LogVersion(Call("aria2.getVersion", null)); return true; }
                    catch { }
                }
                return StartLocked();
            }
        }

        // True while the daemon answers RPC. Used to decide whether a failure
        // needs a restart (Recover) or was a transient call error.
        public bool Ping()
        {
            lock (_lock)
            {
                if (_proc == null || _proc.HasExited) return false;
                try { LogVersion(Call("aria2.getVersion", null)); return true; }
                catch { return false; }
            }
        }

        private bool StartLocked()
        {
            StopLocked();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int port = FreePort();
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = _exe;
                psi.Arguments =
                    "--enable-rpc=true --rpc-listen-all=false --rpc-listen-port=" + port +
                    " --rpc-secret=" + _secret + " --no-conf=true --quiet=true" +
                    " --continue=true --auto-save-interval=1 --disk-cache=16M" +
                    " --optimize-concurrent-downloads=true --max-concurrent-downloads=64" +
                    " --console-log-level=error --stop-with-process=" +
                    Process.GetCurrentProcess().Id;
                if (!string.IsNullOrEmpty(_certs) && File.Exists(_certs))
                    psi.Arguments += " --ca-certificate=\"" + _certs + "\" --check-certificate=true";
                psi.WorkingDirectory = _toolsDir;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                try
                {
                    Process p = new Process();
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate { };
                    p.ErrorDataReceived += delegate { };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    _proc = p;
                    _port = port;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    continue;
                }

                for (int i = 0; i < 50; i++)
                {
                    Thread.Sleep(100);
                    try
                    {
                        LogVersion(Call("aria2.getVersion", null));
                        _lastError = "";
                        return true;
                    }
                    catch { }
                    if (_proc.HasExited) break;
                }
                _lastError = "aria2 RPC did not answer on port " + port;
                StopLocked();
            }
            return false;
        }

        // Logs the bundled engine version once, so Diagnostics shows what ran.
        private void LogVersion(object result)
        {
            if (_versionLogged) return;
            _versionLogged = true;
            Dictionary<string, object> d = result as Dictionary<string, object>;
            Logger.Info("aria2 version: " + (d == null ? "unknown" : Str(d, "version")));
        }

        private void StopLocked()
        {
            if (_proc == null) return;
            try
            {
                if (!_proc.HasExited)
                {
                    try { Call("aria2.shutdown", null); } catch { }
                    _proc.WaitForExit(2000);
                }
            }
            catch { }
            try { if (!_proc.HasExited) _proc.Kill(); } catch { }
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }

        public void Stop()
        {
            lock (_lock) StopLocked();
        }

        private static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public string Add(DownloadItem item, string defaultFolder, int defaultParts,
                          long defaultSpeed, string defaultProxy)
        {
            string folder = Path.GetDirectoryName(item.LocalPath);
            if (string.IsNullOrEmpty(folder)) folder = defaultFolder;

            Dictionary<string, object> o = new Dictionary<string, object>();
            o["dir"] = folder;
            o["out"] = Path.GetFileName(item.LocalPath);
            o["continue"] = "true";
            o["auto-file-renaming"] = "false";
            o["file-allocation"] = "none";
            o["max-tries"] = "2";
            o["retry-wait"] = "2";
            o["connect-timeout"] = "20";
            o["timeout"] = "30";
            o["user-agent"] = string.IsNullOrEmpty(item.UserAgent)
                ? "ZID/1.0" : item.UserAgent;
            if (!string.IsNullOrEmpty(item.Referer)) o["referer"] = item.Referer;
            if (!string.IsNullOrEmpty(_certs) && File.Exists(_certs))
                o["ca-certificate"] = _certs;

            int parts = item.MaxConn > 0 ? item.MaxConn : defaultParts;
            if (parts > 1)
            {
                o["split"] = parts.ToString(CultureInfo.InvariantCulture);
                o["max-connection-per-server"] = parts.ToString(CultureInfo.InvariantCulture);
                o["min-split-size"] = "1M";
            }
            long cap = item.SpeedKbps > 0 ? item.SpeedKbps : defaultSpeed;
            if (cap > 0) o["max-download-limit"] = cap.ToString(CultureInfo.InvariantCulture) + "K";
            if (!string.IsNullOrEmpty(item.Checksum)) o["checksum"] = item.Checksum;

            string proxy = string.IsNullOrEmpty(item.Proxy) ? defaultProxy : item.Proxy;
            if (!string.IsNullOrEmpty(proxy)) o["all-proxy"] = proxy;

            List<object> headers = new List<object>();
            if (!string.IsNullOrEmpty(item.Cookie)) headers.Add("Cookie: " + item.Cookie);
            if (!string.IsNullOrEmpty(item.Headers))
            {
                string[] hl = item.Headers.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                foreach (string h in hl)
                {
                    string t = h.Trim();
                    if (t.Length > 0 && t.IndexOf(':') > 0) headers.Add(t);
                }
            }
            if (headers.Count > 0) o["header"] = headers;

            string url = item.Url;
            try
            {
                Uri u = new Uri(url);
                bool ftp = u.Scheme == Uri.UriSchemeFtp;
                // The credential prompt wins over credentials embedded in the URL.
                if (!string.IsNullOrEmpty(item.AuthUser))
                {
                    if (item.AuthProxy)
                    {
                        o["http-proxy-user"] = item.AuthUser;
                        o["http-proxy-passwd"] = item.AuthPass == null ? "" : item.AuthPass;
                    }
                    else
                    {
                        o[ftp ? "ftp-user" : "http-user"] = item.AuthUser;
                        o[ftp ? "ftp-passwd" : "http-passwd"] = item.AuthPass == null ? "" : item.AuthPass;
                    }
                }
                else if (!string.IsNullOrEmpty(u.UserInfo))
                {
                    string info = u.UserInfo;
                    int colon = info.IndexOf(':');
                    string user = Uri.UnescapeDataString(colon < 0 ? info : info.Substring(0, colon));
                    string pass = colon < 0 ? "" : Uri.UnescapeDataString(info.Substring(colon + 1));
                    o[ftp ? "ftp-user" : "http-user"] = user;
                    o[ftp ? "ftp-passwd" : "http-passwd"] = pass;
                }
                if (!string.IsNullOrEmpty(u.UserInfo))
                {
                    UriBuilder ub = new UriBuilder(u);
                    ub.UserName = "";
                    ub.Password = "";
                    url = ub.Uri.AbsoluteUri;
                }
            }
            catch { }

            List<object> uris = new List<object>();
            uris.Add(url);
            if (!string.IsNullOrEmpty(item.Mirror)) uris.Add(item.Mirror);

            object result = Call("aria2.addUri", new object[] { uris, o });
            string gid = result as string;
            if (string.IsNullOrEmpty(gid)) throw new Exception("aria2.addUri returned no gid");
            return gid;
        }

        public Dictionary<string, object> Tell(string gid)
        {
            List<object> keys = new List<object>();
            keys.Add("gid");
            keys.Add("status");
            keys.Add("totalLength");
            keys.Add("completedLength");
            keys.Add("downloadSpeed");
            keys.Add("connections");
            keys.Add("errorCode");
            keys.Add("errorMessage");
            keys.Add("files");
            keys.Add("uris");
            object result = Call("aria2.tellStatus", new object[] { gid, keys });
            Dictionary<string, object> d = result as Dictionary<string, object>;
            if (d == null) throw new Exception("bad tellStatus response");
            return d;
        }

        // Every task the daemon still knows: active, waiting and stopped.
        // Used once at connect to find tasks that outlived our state file.
        public List<string> ListGids()
        {
            List<string> gids = new List<string>();
            AddGids(gids, Call("aria2.tellActive", null));
            AddGids(gids, Call("aria2.tellWaiting", new object[] { 0, 1000 }));
            AddGids(gids, Call("aria2.tellStopped", new object[] { 0, 1000 }));
            return gids;
        }

        private static void AddGids(List<string> gids, object result)
        {
            List<object> list = result as List<object>;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                Dictionary<string, object> d = list[i] as Dictionary<string, object>;
                string gid = Str(d, "gid");
                if (!string.IsNullOrEmpty(gid) && !gids.Contains(gid)) gids.Add(gid);
            }
        }

        public static string FirstFilePath(Dictionary<string, object> d)
        {
            List<object> files = GetList(d, "files");
            if (files == null || files.Count == 0) return "";
            return Str(files[0] as Dictionary<string, object>, "path");
        }

        public static string FirstUri(Dictionary<string, object> d)
        {
            List<object> uris = GetList(d, "uris");
            if (uris == null || uris.Count == 0) return "";
            return Str(uris[0] as Dictionary<string, object>, "uri");
        }

        private static List<object> GetList(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v)) return null;
            return v as List<object>;
        }

        public void Pause(string gid) { Call("aria2.pause", new object[] { gid }); }
        public void Unpause(string gid) { Call("aria2.unpause", new object[] { gid }); }

        public void Remove(string gid)
        {
            try { Call("aria2.forceRemove", new object[] { gid }); } catch { }
            try { Call("aria2.removeDownloadResult", new object[] { gid }); } catch { }
        }

        public void RemoveResult(string gid)
        {
            try { Call("aria2.removeDownloadResult", new object[] { gid }); } catch { }
        }

        public void ChangeOption(string gid, string name, string value)
        {
            Dictionary<string, object> o = new Dictionary<string, object>();
            o[name] = value;
            Call("aria2.changeOption", new object[] { gid, o });
        }

        private object Call(string method, object[] args)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"jsonrpc\":\"2.0\",\"id\":\"zid\",\"method\":\"").Append(method)
              .Append("\",\"params\":[\"token:").Append(_secret).Append('"');
            if (args != null)
                foreach (object a in args) { sb.Append(','); WriteJson(sb, a); }
            sb.Append("]}");

            byte[] body = Encoding.UTF8.GetBytes(sb.ToString());
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(
                "http://127.0.0.1:" + _port + "/jsonrpc");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.KeepAlive = false;
            req.Proxy = null;
            req.ServicePoint.Expect100Continue = false;

            using (Stream s = req.GetRequestStream())
                s.Write(body, 0, body.Length);

            string text;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                text = r.ReadToEnd();

            Dictionary<string, object> obj = Aria.ParseJson(text) as Dictionary<string, object>;
            if (obj == null) throw new Exception("bad RPC response");
            object err;
            if (obj.TryGetValue("error", out err))
            {
                Dictionary<string, object> e = err as Dictionary<string, object>;
                throw new Exception(e != null ? Str(e, "message") : "RPC error");
            }
            object result;
            if (!obj.TryGetValue("result", out result)) throw new Exception("RPC response has no result");
            return result;
        }

        private static void WriteJson(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            string s = v as string;
            if (s != null) { sb.Append('"').Append(Aria.JsonEscape(s)).Append('"'); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is int || v is long || v is double)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            IDictionary<string, object> d = v as IDictionary<string, object>;
            if (d != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Aria.JsonEscape(kv.Key)).Append("\":");
                    WriteJson(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            IList<object> l = v as IList<object>;
            if (l != null)
            {
                sb.Append('[');
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteJson(sb, l[i]);
                }
                sb.Append(']');
                return;
            }
            sb.Append('"').Append(Aria.JsonEscape(v.ToString())).Append('"');
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return "";
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long Long(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }
    }

    // aria2 ships its own Mozilla root bundle and cannot read the Windows store,
    // so a corporate/antivirus root (HTTPS scanning) or a root the installed
    // bundle predates fails with "unable to get local issuer certificate".
    // Merge the shipped bundle with the machine's trusted roots into one PEM.
    internal static class CaBundle
    {
        public static string Prepare(string toolsDir, string cacheDir)
        {
            string shipped = Path.Combine(toolsDir, "ca-certificates.crt");
            try
            {
                string outp = Path.Combine(cacheDir, "ca-bundle.crt");
                DateTime now = DateTime.UtcNow;
                if (File.Exists(outp) && now - File.GetLastWriteTimeUtc(outp) < TimeSpan.FromDays(1) &&
                    (!File.Exists(shipped) || File.GetLastWriteTimeUtc(outp) >= File.GetLastWriteTimeUtc(shipped)))
                    return outp;

                StringBuilder sb = new StringBuilder(400000);
                Dictionary<string, bool> seen = new Dictionary<string, bool>();
                // Re-encode every PEM block: a truncated or half-copied bundle
                // would otherwise make OpenSSL reject the whole file.
                if (File.Exists(shipped)) AppendPem(sb, seen, File.ReadAllText(shipped));
                AppendStore(sb, seen, StoreLocation.CurrentUser);
                AppendStore(sb, seen, StoreLocation.LocalMachine);
                if (sb.Length < 1024) return File.Exists(shipped) ? shipped : "";

                Directory.CreateDirectory(cacheDir);
                File.WriteAllText(outp, sb.ToString(), Encoding.ASCII);
                return outp;
            }
            catch
            {
                // Never leave aria2 without a bundle: fall back to the shipped one.
                return File.Exists(shipped) ? shipped : "";
            }
        }

        private static void AppendPem(StringBuilder sb, Dictionary<string, bool> seen, string pem)
        {
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            int at = 0;
            while (true)
            {
                int a = pem.IndexOf(begin, at, StringComparison.Ordinal);
                if (a < 0) return;
                int b = pem.IndexOf(end, a + begin.Length, StringComparison.Ordinal);
                if (b < 0) return;
                string body = pem.Substring(a + begin.Length, b - a - begin.Length)
                                 .Replace("\r", "").Replace("\n", "").Trim();
                at = b + end.Length;
                try
                {
                    byte[] raw = Convert.FromBase64String(body);
                    X509Certificate2 c = new X509Certificate2(raw);
                    string thumb = c.Thumbprint;
                    if (thumb == null || seen.ContainsKey(thumb)) continue;
                    seen[thumb] = true;
                    AppendCert(sb, raw);
                }
                catch { }
            }
        }

        private static void AppendCert(StringBuilder sb, byte[] raw)
        {
            string b64 = Convert.ToBase64String(raw);
            sb.Append("-----BEGIN CERTIFICATE-----\n");
            for (int i = 0; i < b64.Length; i += 64)
                sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
            sb.Append("-----END CERTIFICATE-----\n");
        }

        private static void AppendStore(StringBuilder sb, Dictionary<string, bool> seen, StoreLocation loc)
        {
            try
            {
                X509Store store = new X509Store(StoreName.Root, loc);
                try
                {
                    store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                    foreach (X509Certificate2 c in store.Certificates)
                    {
                        try
                        {
                            if (c.NotAfter <= DateTime.Now) continue;
                            string thumb = c.Thumbprint;
                            if (thumb == null || seen.ContainsKey(thumb)) continue;
                            seen[thumb] = true;
                            AppendCert(sb, c.RawData);
                        }
                        catch { }
                    }
                }
                finally { store.Close(); }
            }
            catch { }
        }
    }
}
