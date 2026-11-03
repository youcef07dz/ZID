using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ZID
{
    internal sealed class NeatRequest
    {
        public string Method = "GET";
        public string Url = "";
        public string FileName = "";
        public string Cookie = "";
        public string ContentType = "";
        public string Referer = "";
        public string UserAgent = "";
        public string Kind = "";
        public long Size = 0;
        public bool Auto = false;
        public string Pair = "";
        // Fingerprint headers the page sent for the media URL (Accept-Language,
        // sec-ch-ua, Sec-Fetch-*). Some CDNs sign the URL against them.
        public string Headers = "";
    }

    internal delegate bool NeatDownloadHandler(NeatRequest request);

    // The browser extension talks to the native app over this loopback
    // WebSocket: ws://127.0.0.1:10007/download, subprotocol neatextension.v1.
    // It writes one plain-text download per message and gets one word back.
    internal sealed class NeatLinkServer
    {
        public const int Port = 10007;
        private const int MaxMessage = 4 * 1024 * 1024;
        private const int MaxClients = 16;
        private const int IdleTimeoutMs = 30000;

        private TcpListener _listener;
        private Thread _accept;
        private volatile bool _running;
        private int _clients;
        // Two capture extensions (Neat's and ours) send the same click twice;
        // the first request owns the URL for a few seconds.
        private readonly Dictionary<string, DateTime> _seen = new Dictionary<string, DateTime>();
        private readonly object _seenLock = new object();

        public string Token = "";
        public string LastError = "";

        public event NeatDownloadHandler DownloadRequested;

        public bool Start()
        {
            if (_running) return true;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                _running = true;
                _accept = new Thread(AcceptLoop);
                _accept.IsBackground = true;
                _accept.Name = "NeatLink accept";
                _accept.Start();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _listener = null;
                _running = false;
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (!_running) return; continue; }

                Thread t = new Thread(delegate()
                {
                    try { Serve(client); }
                    catch (Exception ex)
                    {
                        try { client.Close(); } catch { }
                        Logger.Error("CRASH NeatLink client: " + ex);
                    }
                });
                t.IsBackground = true;
                t.Name = "NeatLink client";
                t.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            // A stalled or abandoned client must not keep a thread forever.
            if (Interlocked.Increment(ref _clients) > MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                try { client.Close(); } catch { }
                return;
            }
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = IdleTimeoutMs;
                client.SendTimeout = IdleTimeoutMs;
                using (client)
                using (NetworkStream s = client.GetStream())
                {
                    bool authed;
                    if (!Handshake(s, Token, out authed)) return;
                    StringBuilder fragment = null;
                    int rateStart = 0, rateCount = 0;

                    while (_running)
                    {
                        bool fin;
                        byte[] payload;
                        int op = ReadFrame(s, out payload, out fin);
                        if (op < 0) return;

                        if (op == 9) { WriteFrame(s, 10, payload); continue; }
                        if (op == 8) { WriteFrame(s, 8, new byte[0]); return; }
                        if (op == 10) continue;
                        if (op != 0 && op != 1 && op != 2) continue;

                        // A client that floods the bridge is dropped, not served.
                        if (!NeatLink.RateAllow(ref rateStart, ref rateCount,
                                                Environment.TickCount, 60, 10000))
                            return;

                        string text = Encoding.UTF8.GetString(payload);
                        if (op == 0)
                        {
                            if (fragment == null) continue;
                            fragment.Append(text);
                            if (!fin) continue;
                            text = fragment.ToString();
                            fragment = null;
                        }
                        else if (!fin)
                        {
                            fragment = new StringBuilder(text);
                            continue;
                        }

                        NeatRequest req = NeatLink.Parse(text);
                        if (req == null && text.Trim() == NeatLink.VersionQuery)
                        {
                            WriteFrame(s, 1, Encoding.UTF8.GetBytes(NeatLink.AppVersion()));
                            continue;
                        }
                        bool queued = req != null && Raise(req, authed);
                        // The browser-download hand-off waits for this word
                        // before it cancels the browser's own copy.
                        WriteFrame(s, 1, Encoding.UTF8.GetBytes(NeatLink.Reply(queued)));
                    }
                }
            }
            catch { }
            finally { Interlocked.Decrement(ref _clients); }
        }

        private bool Raise(NeatRequest req, bool authed)
        {
            NeatDownloadHandler h = DownloadRequested;
            if (h == null) return false;
            // The Neat extension sends a generic "normal" capture with a name
            // taken from the URL; ours sends "file" with the real
            // Content-Disposition name and arrives about a second later. Let
            // the generic one wait so the dialog shows the right name.
            if (NeatLink.WaitsForBetter(req.Kind)) Thread.Sleep(1500);
            lock (_seenLock)
            {
                if (NeatLink.RecentlySeen(_seen, req.Url, DateTime.Now, 5)) return true;
            }
            try { return h(req); } catch { return false; }
        }

        internal static bool Handshake(Stream s, string token, out bool authed)
        {
            authed = false;
            StringBuilder sb = new StringBuilder();
            byte[] one = new byte[1];
            while (sb.Length < 16384)
            {
                if (!ReadExact(s, one, 1)) return false;
                sb.Append((char)one[0]);
                if (sb.Length >= 4 &&
                    sb[sb.Length - 4] == '\r' && sb[sb.Length - 3] == '\n' &&
                    sb[sb.Length - 2] == '\r' && sb[sb.Length - 1] == '\n') break;
            }

            Dictionary<string, string> h = NeatLink.Headers(sb.ToString());
            string key, upgrade, connection, protocol;
            if (!h.TryGetValue("sec-websocket-key", out key) || key.Length == 0) return false;
            if (!h.TryGetValue("upgrade", out upgrade) ||
                upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!h.TryGetValue("connection", out connection) ||
                connection.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) < 0) return false;

            if (h.TryGetValue("sec-websocket-protocol", out protocol) && protocol.Length > 0)
                authed = NeatLink.TokenMatches(protocol, token);

            // The bundled extensions send neatextension.v1 with their own
            // Origin; a local tool or web page cannot. No other client may
            // inject a download without the token.
            string origin;
            h.TryGetValue("origin", out origin);
            if (!authed && !NeatLink.ExtensionHandshake(protocol, origin))
            {
                byte[] deny = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                s.Write(deny, 0, deny.Length);
                s.Flush();
                return false;
            }

            StringBuilder resp = new StringBuilder();
            resp.Append("HTTP/1.1 101 Switching Protocols\r\n");
            resp.Append("Upgrade: websocket\r\n");
            resp.Append("Connection: Upgrade\r\n");
            resp.Append("Sec-WebSocket-Accept: ").Append(NeatLink.AcceptKey(key)).Append("\r\n");
            if (h.TryGetValue("sec-websocket-protocol", out protocol) && protocol.Length > 0)
            {
                string first = protocol.Split(',')[0].Trim();
                if (first.Length > 0)
                    resp.Append("Sec-WebSocket-Protocol: ").Append(first).Append("\r\n");
            }
            resp.Append("\r\n");

            byte[] b = Encoding.ASCII.GetBytes(resp.ToString());
            s.Write(b, 0, b.Length);
            s.Flush();
            return true;
        }

        private static int ReadFrame(Stream s, out byte[] payload, out bool fin)
        {
            payload = null;
            fin = false;

            byte[] head = new byte[2];
            if (!ReadExact(s, head, 2)) return -1;

            fin = (head[0] & 0x80) != 0;
            int opcode = head[0] & 0x0F;
            bool masked = (head[1] & 0x80) != 0;
            long length = head[1] & 0x7F;

            if (length == 126)
            {
                byte[] ext = new byte[2];
                if (!ReadExact(s, ext, 2)) return -1;
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                byte[] ext = new byte[8];
                if (!ReadExact(s, ext, 8)) return -1;
                length = 0;
                for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
            }
            if (length < 0 || length > MaxMessage) return -1;

            byte[] mask = new byte[4];
            if (masked && !ReadExact(s, mask, 4)) return -1;

            payload = new byte[(int)length];
            if (length > 0 && !ReadExact(s, payload, (int)length)) return -1;
            if (masked)
                for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

            return opcode;
        }

        private static void WriteFrame(Stream s, int opcode, byte[] data)
        {
            if (data == null) data = new byte[0];
            int extra = data.Length <= 125 ? 0 : (data.Length <= 65535 ? 2 : 8);
            byte[] frame = new byte[2 + extra + data.Length];
            int i = 0;
            frame[i++] = (byte)(0x80 | opcode);
            if (data.Length <= 125)
                frame[i++] = (byte)data.Length;
            else if (data.Length <= 65535)
            {
                frame[i++] = 126;
                frame[i++] = (byte)(data.Length >> 8);
                frame[i++] = (byte)data.Length;
            }
            else
            {
                frame[i++] = 127;
                for (int b = 7; b >= 0; b--) frame[i++] = (byte)((long)data.Length >> (b * 8));
            }
            if (data.Length > 0)
            {
                Buffer.BlockCopy(data, 0, frame, i, data.Length);
                i += data.Length;
            }
            s.Write(frame, 0, i);
            s.Flush();
        }

        private static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n;
                try { n = s.Read(buf, off, count - off); }
                catch { return false; }
                if (n <= 0) return false;
                off += n;
            }
            return true;
        }
    }

    // Pure parsing helpers (also covered by tests.cs).
    internal static class NeatLink
    {
        // One-word reply for the browser-download hand-off: the extension
        // cancels its copy only after "ok".
        public const string ReplyOk = "ok";
        public const string ReplyErr = "err";
        // The extension sends this instead of a download; the app answers its
        // version so a stale extension shows a warning instead of failing later.
        public const string VersionQuery = "zid-version";

        public static string AppVersion()
        {
            try
            {
                Version v = new Version(System.Windows.Forms.Application.ProductVersion);
                string s = v.Major + "." + v.Minor;
                if (v.Build > 0) s += "." + v.Build;
                if (v.Revision > 0) s += "." + v.Revision;
                return s;
            }
            catch { return System.Windows.Forms.Application.ProductVersion; }
        }

        public static string Reply(NeatRequest request)
        {
            return request != null ? ReplyOk : ReplyErr;
        }

        // True only when the app actually accepted the request; the extension
        // keeps its own browser download on "err".
        public static string Reply(bool accepted)
        {
            return accepted ? ReplyOk : ReplyErr;
        }

        // A generic "normal" capture is the Neat extension's; it waits for our
        // named "file" capture instead of opening a second, scrambled dialog.
        internal static bool WaitsForBetter(string kind)
        {
            return string.Equals(kind, "normal", StringComparison.OrdinalIgnoreCase);
        }

        // Two capture extensions (Neat's and ours) deliver the same click twice;
        // the first request owns the URL, the second must not open a second
        // dialog. Returns true when the URL was accepted within the window.
        internal static bool RecentlySeen(Dictionary<string, DateTime> seen, string url,
                                          DateTime now, int seconds)
        {
            string key = Util.NormalizeUrl(url);
            if (key.Length == 0) return false;
            DateTime at;
            if (seen.TryGetValue(key, out at) && (now - at).TotalSeconds < seconds)
                return true;
            seen[key] = now;
            if (seen.Count > 64)
            {
                DateTime cut = now.AddSeconds(-seconds);
                List<string> old = new List<string>();
                foreach (KeyValuePair<string, DateTime> kv in seen)
                    if (kv.Value < cut) old.Add(kv.Key);
                foreach (string k in old) seen.Remove(k);
            }
            return false;
        }

        public static string AcceptKey(string key)
        {
            if (key == null) key = "";
            byte[] hash;
            using (SHA1 sha = new SHA1Managed())
                hash = sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
            return Convert.ToBase64String(hash);
        }

        // Extensions send their own scheme; local tools (aria2, curl, scripts)
        // send no Origin at all. Web pages send http/https and are refused.
        public static bool AllowedOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            string o = origin.Trim().ToLowerInvariant();
            return o.StartsWith("chrome-extension://", StringComparison.Ordinal) ||
                   o.StartsWith("moz-extension://", StringComparison.Ordinal) ||
                   o.StartsWith("safari-web-extension://", StringComparison.Ordinal) ||
                   o.StartsWith("safari-extension://", StringComparison.Ordinal) ||
                   o.StartsWith("ms-browser-extension://", StringComparison.Ordinal);
        }

        // The bundled extensions send their own subprotocol with an extension
        // Origin. Anything else must present token.<token>.
        public static bool ExtensionHandshake(string protocolHeader, string origin)
        {
            if (string.IsNullOrEmpty(origin)) return false;
            if (!AllowedOrigin(origin)) return false;
            if (string.IsNullOrEmpty(protocolHeader)) return false;
            string[] offered = protocolHeader.Split(',');
            for (int i = 0; i < offered.Length; i++)
                if (offered[i].Trim() == "neatextension.v1") return true;
            return false;
        }

        public static bool TokenMatches(string protocolHeader, string token)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(protocolHeader)) return false;
            string[] offered = protocolHeader.Split(',');
            for (int i = 0; i < offered.Length; i++)
                if (offered[i].Trim() == "token." + token) return true;
            return false;
        }

        // One fixed window per connection: the first message starts it, and a
        // full window closes the connection instead of queueing forever.
        public static bool RateAllow(ref int windowStart, ref int count, int nowMs,
                                     int max, int windowMs)
        {
            if (count == 0 || unchecked(nowMs - windowStart) >= windowMs)
            {
                windowStart = nowMs;
                count = 0;
            }
            if (count >= max) return false;
            count++;
            return true;
        }

        public static Dictionary<string, string> Headers(string head)
        {
            Dictionary<string, string> h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (head == null) return h;
            string[] lines = head.Replace("\r\n", "\n").Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                string l = lines[i];
                int c = l.IndexOf(':');
                if (c <= 0) continue;
                string k = l.Substring(0, c).Trim();
                if (k.Length == 0 || h.ContainsKey(k)) continue;
                h[k] = l.Substring(c + 1).Trim();
            }
            return h;
        }

        public static NeatRequest Parse(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;

            NeatRequest r = new NeatRequest();
            string[] lines = message.Replace("\r\n", "\n").Split('\n');

            foreach (string raw in lines)
            {
                if (raw.Length == 0) continue;
                int c = raw.IndexOf(':');
                if (c <= 0) continue;
                string key = raw.Substring(0, c).Trim();
                string val = raw.Substring(c + 1).Trim();

                if (key == "1") r.Method = val;
                else if (key == "2") r.Url = val;
                else if (key == "7") long.TryParse(val, out r.Size);
                else if (key == "8") r.ContentType = val;
                else if (key == "6") r.Kind = val;
                else if (key == "9") r.Auto = val == "1";
                else if (key == "10") r.Pair = val;
                else if (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) r.Cookie = val;
                else if (key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || key == "5") r.Referer = val;
                else if (key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) r.UserAgent = val;
                else if (key.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                    r.FileName = DispositionName(val);
                else if (IsForwardHeader(key))
                    r.Headers += key + ": " + val + "\r\n";
            }

            if (r.Url.Length == 0) return null;
            if (r.Method.Length > 0 && r.Method != "GET") return null;

            Uri u;
            if (!Uri.TryCreate(r.Url, UriKind.Absolute, out u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps &&
                u.Scheme != Uri.UriSchemeFtp) return null;
            if (u.Scheme == Uri.UriSchemeFtp) r.Cookie = "";

            if (r.FileName.Length > 0)
            {
                try { r.FileName = Path.GetFileName(r.FileName); }
                catch { r.FileName = ""; }
            }
            return r;
        }

        // Cookie, Referer, User-Agent and the entity headers are handled on
        // their own; everything else the browser sent is replayed to aria2.
        private static bool IsForwardHeader(string key)
        {
            if (key.Length == 0 || !char.IsLetter(key[0])) return false;
            return !key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase);
        }

        private static string DispositionName(string value)
        {
            if (value == null) return "";
            string lower = value.ToLowerInvariant();

            int i = lower.IndexOf("filename*=");
            if (i >= 0)
            {
                string tail = value.Substring(i + 10).Trim();
                int semi = tail.IndexOf(';');
                if (semi >= 0) tail = tail.Substring(0, semi);
                tail = tail.Trim().Trim('"');
                int sep = tail.IndexOf("''");
                if (sep >= 0) tail = tail.Substring(sep + 2);
                try { return Uri.UnescapeDataString(tail); }
                catch { return tail; }
            }

            i = lower.IndexOf("filename=");
            if (i < 0) return "";
            string name = value.Substring(i + 9).Trim();
            int end = name.IndexOf(';');
            if (end >= 0) name = name.Substring(0, end);
            name = name.Trim();
            if (name.Length >= 2 && name[0] == '"' && name[name.Length - 1] == '"')
                name = name.Substring(1, name.Length - 2);
            return name;
        }
    }
}
