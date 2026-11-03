using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace ZID
{
    // HLS (.m3u8) and DASH (.mpd) downloads: fetch the manifest, download the
    // segments in order, concatenate (fMP4 stays fMP4), and mux DASH audio with
    // the existing Mp4Mux. Segments go through the aria2 daemon when available,
    // which keeps TLS/cookies/proxy handling in one place.
    internal static class Playlist
    {
        public static bool IsHls(string url)
        {
            return UrlPath(url).EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDash(string url)
        {
            return UrlPath(url).EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
        }

        private static string UrlPath(string url)
        {
            try { return new Uri(url).AbsolutePath; }
            catch { return url == null ? "" : url; }
        }

        public static bool Download(DownloadItem item, int gen, string defaultFolder,
                                    IDownloadEngine rpc, string proxy, out string error)
        {
            error = "";
            try
            {
                if (IsHls(item.Url)) return DownloadHls(item, gen, defaultFolder, rpc, proxy, out error);
                if (IsDash(item.Url)) return DownloadDash(item, gen, defaultFolder, rpc, proxy, out error);
                error = "not an HLS/DASH URL";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool Aborted(DownloadItem item, int gen)
        {
            return item.Abort || gen != item.Generation;
        }

        private static void Report(DownloadItem item, int done, int total, long bytes, long speed)
        {
            item.Size = bytes;
            item.BytesReceived = bytes;
            item.Speed = speed;
            item.Progress = total > 0 ? (int)((long)done * 100 / total) : 0;
            if (item.Progress > 99) item.Progress = 99;
            item.Eta = done + "/" + total + " parts";
        }

        private static string TempDir(DownloadItem item)
        {
            return item.LocalPath + ".zidseg";
        }

        // ---------- fetching ----------

        private static bool Fetch(DownloadItem item, int gen, IDownloadEngine rpc, string proxy,
                                  string url, string path, string range, out string error)
        {
            error = "";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (Aborted(item, gen)) return false;
                try
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    if (rpc != null) RpcFetch(item, gen, rpc, proxy, url, path, range);
                    else HttpFetch(item, gen, proxy, url, path, range);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    if (Aborted(item, gen)) return false;
                    Thread.Sleep(700);
                }
            }
            return false;
        }

        private static void RpcFetch(DownloadItem item, int gen, IDownloadEngine rpc, string proxy,
                                     string url, string path, string range)
        {
            DownloadItem t = new DownloadItem();
            t.Url = url;
            t.LocalPath = path;
            t.Cookie = item.Cookie;
            t.Referer = item.Referer;
            t.UserAgent = item.UserAgent;
            t.Headers = item.Headers;
            t.Proxy = item.Proxy;
            if (!string.IsNullOrEmpty(range))
                t.Headers = (t.Headers == null ? "" : t.Headers + "\r\n") + "Range: " + range;
            string gid = rpc.Add(t, Path.GetDirectoryName(path), 1, 0, proxy);
            try
            {
                for (int i = 0; i < 1200; i++)
                {
                    if (Aborted(item, gen))
                    {
                        try { rpc.Remove(gid); } catch { }
                        throw new Exception("aborted");
                    }
                    EngineTaskState st = rpc.Tell(gid);
                    if (st.Status == EngineTaskStatus.Complete) return;
                    if (st.Status == EngineTaskStatus.Error)
                    {
                        throw new Exception("segment failed (" + st.ErrorCode + ")");
                    }
                    Thread.Sleep(250);
                }
                throw new Exception("segment timed out");
            }
            finally { rpc.RemoveResult(gid); }
        }

        private static void HttpFetch(DownloadItem item, int gen, string proxy,
                                      string url, string path, string range)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = string.IsNullOrEmpty(item.UserAgent) ? "ZID/1.0" : item.UserAgent;
            if (!string.IsNullOrEmpty(item.Referer)) r.Referer = item.Referer;
            if (!string.IsNullOrEmpty(item.Cookie))
            {
                r.CookieContainer = new CookieContainer();
                r.CookieContainer.SetCookies(new Uri(url), item.Cookie);
            }
            AddHeaders(r, item.Headers);
            if (!string.IsNullOrEmpty(range))
            {
                try
                {
                    string v = range.Substring(6);
                    int d = v.IndexOf('-');
                    if (d > 0)
                    {
                        long a = long.Parse(v.Substring(0, d), CultureInfo.InvariantCulture);
                        long b = long.Parse(v.Substring(d + 1), CultureInfo.InvariantCulture);
                        if (b > int.MaxValue) b = int.MaxValue;
                        r.AddRange((int)a, (int)b);
                    }
                }
                catch { }
            }
            r.Proxy = string.IsNullOrEmpty(proxy) ? null : new WebProxy(proxy);
            r.Timeout = 20000;
            r.ReadWriteTimeout = 60000;
            r.AllowAutoRedirect = true;

            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream f = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[65536];
                while (true)
                {
                    if (Aborted(item, gen)) throw new Exception("aborted");
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    f.Write(buf, 0, n);
                }
            }
        }

        private static void AddHeaders(HttpWebRequest r, string headers)
        {
            if (string.IsNullOrEmpty(headers)) return;
            string[] lines = headers.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                string t = line.Trim();
                int colon = t.IndexOf(':');
                if (colon <= 0) continue;
                try { r.Headers[t.Substring(0, colon).Trim()] = t.Substring(colon + 1).Trim(); }
                catch { }
            }
        }

        private static string FetchText(DownloadItem item, int gen, IDownloadEngine rpc, string proxy,
                                        string url, out string error)
        {
            string dir = Path.Combine(Path.GetTempPath(), "zid-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "manifest.txt");
            try
            {
                if (!Fetch(item, gen, rpc, proxy, url, path, null, out error)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (text.Length > 8 * 1024 * 1024) throw new Exception("manifest too large");
                return text;
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------- HLS ----------

        private sealed class HlsKey
        {
            public string Method = "NONE";
            public string Uri;
            public byte[] Iv;
            public byte[] Bytes;
        }

        private sealed class HlsSegment
        {
            public string Url;
            public long RangeStart;
            public long RangeLen;
            public long Seq;
        }

        private static bool DownloadHls(DownloadItem item, int gen, string defaultFolder,
                                        IDownloadEngine rpc, string proxy, out string error)
        {
            error = "";
            string url = item.Url;
            string text = FetchText(item, gen, rpc, proxy, url, out error);
            if (text == null) return false;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string variant = PickVariant(lines, url);
            if (variant != null)
            {
                url = variant;
                text = FetchText(item, gen, rpc, proxy, url, out error);
                if (text == null) return false;
                lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            }

            List<HlsSegment> segs;
            string init = null;
            HlsKey key = null;
            bool live;
            if (!ParseMedia(lines, url, out segs, out init, out key, out live, out error)) return false;
            if (live) { error = "Live HLS playlists are not supported"; return false; }
            if (segs.Count == 0) { error = "HLS playlist has no segments"; return false; }
            if (key != null && key.Method != "NONE" && key.Method != "AES-128")
            {
                error = "HLS encryption " + key.Method + " is not supported";
                return false;
            }

            // Name the result by what the segments really are.
            string want = init != null ? ".mp4" : ".ts";
            SetLocalExtension(item, defaultFolder, want);

            string tmp = TempDir(item);
            try
            {
                Directory.CreateDirectory(tmp);
                int total = segs.Count + (init != null ? 1 : 0);
                int done = 0;
                long bytes = 0;
                DateTime started = DateTime.Now;

                if (init != null)
                {
                    string ip = Path.Combine(tmp, "init.bin");
                    if (!File.Exists(ip) || new FileInfo(ip).Length == 0)
                        if (!Fetch(item, gen, rpc, proxy, init, ip, null, out error)) return false;
                    bytes += new FileInfo(ip).Length;
                    done++;
                    Report(item, done, total, bytes, Speed(bytes, started));
                }

                if (key != null && key.Method == "AES-128" && key.Bytes == null)
                {
                    string kp = Path.Combine(tmp, "key.bin");
                    if (!Fetch(item, gen, rpc, proxy, key.Uri, kp, null, out error)) return false;
                    key.Bytes = File.ReadAllBytes(kp);
                    if (key.Bytes.Length != 16) { error = "HLS key is not 16 bytes"; return false; }
                }

                for (int i = 0; i < segs.Count; i++)
                {
                    if (Aborted(item, gen)) { error = ""; return false; }
                    HlsSegment sg = segs[i];
                    string sp = Path.Combine(tmp, i.ToString("D6", CultureInfo.InvariantCulture) + ".seg");
                    if (!File.Exists(sp) || new FileInfo(sp).Length == 0)
                    {
                        string range = sg.RangeLen > 0
                            ? "bytes=" + sg.RangeStart + "-" + (sg.RangeStart + sg.RangeLen - 1) : null;
                        if (!Fetch(item, gen, rpc, proxy, sg.Url, sp, range, out error)) return false;
                    }
                    if (key != null && key.Method == "AES-128")
                        DecryptFile(sp, key.Bytes, key.Iv ?? SeqIv(sg.Seq));
                    bytes += new FileInfo(sp).Length;
                    done++;
                    Report(item, done, total, bytes, Speed(bytes, started));
                }

                string part = item.LocalPath + ".part";
                using (FileStream o = new FileStream(part, FileMode.Create, FileAccess.Write))
                {
                    if (init != null) AppendFile(o, Path.Combine(tmp, "init.bin"));
                    for (int i = 0; i < segs.Count; i++)
                        AppendFile(o, Path.Combine(tmp, i.ToString("D6", CultureInfo.InvariantCulture) + ".seg"));
                }
                try { if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath); } catch { }
                File.Move(part, item.LocalPath);
                item.Size = new FileInfo(item.LocalPath).Length;
                item.BytesReceived = item.Size;
                item.Progress = 100;
                item.Speed = 0;
                return true;
            }
            finally
            {
                if (!Aborted(item, gen)) { try { Directory.Delete(tmp, true); } catch { } }
            }
        }

        private static string PickVariant(string[] lines, string baseUrl)
        {
            string best = null;
            long bestBw = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (!l.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
                long bw = 0;
                int b = l.IndexOf("BANDWIDTH=", StringComparison.OrdinalIgnoreCase);
                if (b >= 0)
                {
                    int e = b + 10;
                    int end = e;
                    while (end < l.Length && char.IsDigit(l[end])) end++;
                    long.TryParse(l.Substring(e, end - e), out bw);
                }
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string u = lines[j].Trim();
                    if (u.Length == 0 || u[0] == '#') continue;
                    if (bw > bestBw) { bestBw = bw; best = Resolve(baseUrl, u); }
                    break;
                }
            }
            return best;
        }

        private static bool ParseMedia(string[] lines, string baseUrl,
                                       out List<HlsSegment> segs, out string init,
                                       out HlsKey key, out bool live, out string error)
        {
            segs = new List<HlsSegment>();
            init = null;
            key = null;
            live = true;
            error = "";
            long seq = 0;
            long rangeStart = 0, rangeLen = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                string l = lines[i].Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(l.Substring(22).Trim(), out seq);
                    continue;
                }
                if (l.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
                {
                    live = false;
                    continue;
                }
                if (l.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
                {
                    HlsKey k = ParseKey(l.Substring(11), baseUrl);
                    key = k.Method == "NONE" ? null : k;
                    continue;
                }
                if (l.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
                {
                    string uri = AttrValue(l.Substring(11), "URI");
                    if (!string.IsNullOrEmpty(uri)) init = Resolve(baseUrl, uri);
                    continue;
                }
                if (l.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
                {
                    string v = l.Substring(17).Trim();
                    int at = v.IndexOf('@');
                    long.TryParse(at >= 0 ? v.Substring(0, at) : v, out rangeLen);
                    if (at >= 0) long.TryParse(v.Substring(at + 1), out rangeStart);
                    continue;
                }
                if (l[0] == '#') continue;

                HlsSegment s = new HlsSegment();
                s.Url = Resolve(baseUrl, l);
                s.Seq = seq + segs.Count;
                s.RangeStart = rangeStart;
                s.RangeLen = rangeLen;
                segs.Add(s);
                rangeStart = 0;
                rangeLen = 0;
            }
            if (segs.Count == 0) error = "HLS playlist has no segments";
            return segs.Count > 0;
        }

        private static HlsKey ParseKey(string attrs, string baseUrl)
        {
            HlsKey k = new HlsKey();
            k.Method = (AttrValue(attrs, "METHOD") ?? "NONE").ToUpperInvariant();
            string uri = AttrValue(attrs, "URI");
            if (!string.IsNullOrEmpty(uri)) k.Uri = Resolve(baseUrl, uri);
            string iv = AttrValue(attrs, "IV");
            if (!string.IsNullOrEmpty(iv) && iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                k.Iv = HexBytes(iv.Substring(2));
            return k;
        }

        private static string AttrValue(string attrs, string name)
        {
            string[] parts = attrs.Split(',');
            foreach (string p in parts)
            {
                int eq = p.IndexOf('=');
                if (eq <= 0) continue;
                if (!string.Equals(p.Substring(0, eq).Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
                string v = p.Substring(eq + 1).Trim();
                if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
                return v;
            }
            return null;
        }

        private static byte[] SeqIv(long seq)
        {
            byte[] iv = new byte[16];
            for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(seq >> (8 * i));
            return iv;
        }

        private static byte[] HexBytes(string hex)
        {
            if (hex.Length % 2 != 0) return null;
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return b;
        }

        private static void DecryptFile(string path, byte[] key, byte[] iv)
        {
            byte[] data = File.ReadAllBytes(path);
            using (RijndaelManaged aes = new RijndaelManaged())
            {
                aes.KeySize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform dec = aes.CreateDecryptor(key, iv))
                {
                    byte[] plain = dec.TransformFinalBlock(data, 0, data.Length);
                    File.WriteAllBytes(path, plain);
                }
            }
        }

        // ---------- DASH ----------

        private sealed class DashRep
        {
            public string Id;
            public long Bandwidth;
            public string BaseUrl;
            public string InitTemplate;
            public string MediaTemplate;
            public long StartNumber = 1;
            public long Duration;
            public long Timescale = 1;
            public long TimeOffset;
            public List<string> SegmentUrls = new List<string>();
        }

        private static bool DownloadDash(DownloadItem item, int gen, string defaultFolder,
                                         IDownloadEngine rpc, string proxy, out string error)
        {
            error = "";
            string text = FetchText(item, gen, rpc, proxy, item.Url, out error);
            if (text == null) return false;

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(text);
            XmlElement mpd = doc.DocumentElement;
            if (string.Equals(Attr(mpd, "type"), "dynamic", StringComparison.OrdinalIgnoreCase))
            {
                error = "Live DASH streams are not supported";
                return false;
            }
            double duration = IsoSeconds(Attr(mpd, "mediaPresentationDuration"));
            string mpdBase = Resolve(item.Url, TextOf(Child(mpd, "BaseURL")));
            XmlElement period = Child(mpd, "Period");
            if (period == null) { error = "MPD has no Period"; return false; }
            if (duration <= 0) duration = IsoSeconds(Attr(period, "duration"));
            string periodBase = Resolve(mpdBase, TextOf(Child(period, "BaseURL")));

            DashRep video = PickRep(period, periodBase, duration, "video", out error);
            if (video == null) { error = "no DASH video representation"; return false; }
            DashRep audio = PickRep(period, periodBase, duration, "audio", out error);

            string tmp;
            SetLocalExtension(item, defaultFolder, ".mp4");
            tmp = TempDir(item);
            try
            {
                Directory.CreateDirectory(tmp);
                int total = video.SegmentUrls.Count + 1 + (audio != null ? audio.SegmentUrls.Count : 0);
                int done = 0;
                long bytes = 0;
                DateTime started = DateTime.Now;

                string vfile = Path.Combine(tmp, "video.mp4");
                if (!FetchRep(item, gen, rpc, proxy, video, vfile, tmp, "v",
                              total, ref done, ref bytes, started, out error)) return false;

                string afile = null;
                if (audio != null)
                {
                    afile = Path.Combine(tmp, "audio.m4a");
                    if (!FetchRep(item, gen, rpc, proxy, audio, afile, tmp, "a",
                                  total, ref done, ref bytes, started, out error)) return false;
                }

                item.Eta = "Muxing...";
                if (afile == null)
                {
                    try { if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath); } catch { }
                    File.Move(vfile, item.LocalPath);
                }
                else
                {
                    string muxed = Path.Combine(tmp, "muxed.mp4");
                    string muxErr;
                    if (Mp4Mux.Merge(vfile, afile, muxed, out muxErr) &&
                        File.Exists(muxed) && new FileInfo(muxed).Length > 0)
                    {
                        try { if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath); } catch { }
                        File.Move(muxed, item.LocalPath);
                    }
                    else
                    {
                        // Keep the video track rather than throwing the download away.
                        try { if (File.Exists(item.LocalPath)) File.Delete(item.LocalPath); } catch { }
                        File.Move(vfile, item.LocalPath);
                        error = "audio mux failed: " + muxErr;
                        return false;
                    }
                }
                item.Size = new FileInfo(item.LocalPath).Length;
                item.BytesReceived = item.Size;
                item.Progress = 100;
                item.Speed = 0;
                return true;
            }
            finally
            {
                if (!Aborted(item, gen)) { try { Directory.Delete(tmp, true); } catch { } }
            }
        }

        private static bool FetchRep(DownloadItem item, int gen, IDownloadEngine rpc, string proxy,
                                     DashRep rep, string outPath, string tmp, string prefix,
                                     int total, ref int done, ref long bytes, DateTime started,
                                     out string error)
        {
            error = "";
            string part = outPath + ".part";
            using (FileStream o = new FileStream(part, FileMode.Create, FileAccess.Write))
            {
                if (!string.IsNullOrEmpty(rep.InitTemplate))
                {
                    string ip = Path.Combine(tmp, prefix + "-init.bin");
                    if (!Fetch(item, gen, rpc, proxy, ExpandInit(rep), ip, null, out error))
                        return false;
                    AppendFile(o, ip);
                    done++;
                    bytes += new FileInfo(ip).Length;
                    Report(item, done, total, bytes, Speed(bytes, started));
                }
                for (int i = 0; i < rep.SegmentUrls.Count; i++)
                {
                    if (Aborted(item, gen)) { error = ""; return false; }
                    string sp = Path.Combine(tmp, prefix + "-" + i.ToString("D6", CultureInfo.InvariantCulture) + ".seg");
                    if (!File.Exists(sp) || new FileInfo(sp).Length == 0)
                    {
                        string u = rep.SegmentUrls[i];
                        if (!Fetch(item, gen, rpc, proxy, u, sp, null, out error)) return false;
                    }
                    AppendFile(o, sp);
                    done++;
                    bytes += new FileInfo(sp).Length;
                    Report(item, done, total, bytes, Speed(bytes, started));
                }
            }
            File.Move(part, outPath);
            return true;
        }

        private static DashRep PickRep(XmlElement period, string periodBase, double duration,
                                       string kind, out string error)
        {
            error = "";
            DashRep best = null;
            foreach (XmlElement ase in Children(period, "AdaptationSet"))
            {
                string ct = Attr(ase, "contentType");
                string mime = Attr(ase, "mimeType");
                bool match = ct.Length > 0
                    ? ct.StartsWith(kind, StringComparison.OrdinalIgnoreCase)
                    : mime.StartsWith(kind + "/", StringComparison.OrdinalIgnoreCase);
                if (!match) continue;

                string asBase = Resolve(periodBase, TextOf(Child(ase, "BaseURL")));
                XmlElement asTpl = Child(ase, "SegmentTemplate");
                foreach (XmlElement re in Children(ase, "Representation"))
                {
                    DashRep r = new DashRep();
                    r.Id = Attr(re, "id");
                    long.TryParse(Attr(re, "bandwidth"), out r.Bandwidth);
                    r.BaseUrl = Resolve(asBase, TextOf(Child(re, "BaseURL")));

                    XmlElement tpl = Child(re, "SegmentTemplate");
                    if (tpl == null) tpl = asTpl;
                    bool hasTemplate = tpl != null;
                    if (tpl != null)
                    {
                        r.MediaTemplate = Attr(tpl, "media");
                        r.InitTemplate = Attr(tpl, "initialization");
                        long.TryParse(Attr(tpl, "startNumber"), out r.StartNumber);
                        if (r.StartNumber <= 0) r.StartNumber = 1;
                        long.TryParse(Attr(tpl, "duration"), out r.Duration);
                        long.TryParse(Attr(tpl, "timescale"), out r.Timescale);
                        if (r.Timescale <= 0) r.Timescale = 1;
                        long.TryParse(Attr(tpl, "presentationTimeOffset"), out r.TimeOffset);
                    }

                    XmlElement list = Child(re, "SegmentList");
                    if (list == null) list = Child(ase, "SegmentList");
                    if (list != null)
                    {
                        hasTemplate = true;
                        XmlElement init = Child(list, "Initialization");
                        if (init != null)
                        {
                            string src = Attr(init, "sourceURL");
                            if (!string.IsNullOrEmpty(src)) r.InitTemplate = Resolve(r.BaseUrl, src);
                        }
                        foreach (XmlElement su in Children(list, "SegmentURL"))
                        {
                            string media = Attr(su, "media");
                            if (!string.IsNullOrEmpty(media)) r.SegmentUrls.Add(Resolve(r.BaseUrl, media));
                        }
                    }

                    if (r.SegmentUrls.Count == 0 && !string.IsNullOrEmpty(r.MediaTemplate))
                    {
                        long count = 0;
                        if (r.Duration > 0 && duration > 0)
                            count = (long)Math.Ceiling(duration * r.Timescale / r.Duration);
                        for (long i = 0; i < count; i++)
                            r.SegmentUrls.Add(BuildNumbered(r, i));
                    }

                    if (r.SegmentUrls.Count == 0 && !hasTemplate && !string.IsNullOrEmpty(r.BaseUrl))
                    {
                        // SegmentBase / single-file representation.
                        r.SegmentUrls.Add(r.BaseUrl);
                        r.InitTemplate = null;
                    }

                    if (r.SegmentUrls.Count == 0) continue;
                    if (best == null || r.Bandwidth > best.Bandwidth) best = r;
                }
            }
            return best;
        }

        private static string BuildNumbered(DashRep r, long index)
        {
            string m = r.MediaTemplate;
            if (m == null) return "";
            long number = r.StartNumber + index;
            m = System.Text.RegularExpressions.Regex.Replace(m, @"\$Number%0(\d+)d\$",
                delegate (System.Text.RegularExpressions.Match mm)
                {
                    int w = int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
                    return number.ToString(CultureInfo.InvariantCulture).PadLeft(w, '0');
                });
            if (m.IndexOf("$Number$", StringComparison.Ordinal) >= 0)
                m = m.Replace("$Number$", number.ToString(CultureInfo.InvariantCulture));
            if (m.IndexOf("$Time$", StringComparison.Ordinal) >= 0)
                m = m.Replace("$Time$", (r.TimeOffset + index * r.Duration).ToString(CultureInfo.InvariantCulture));
            m = m.Replace("$RepresentationID$", r.Id == null ? "" : r.Id);
            m = m.Replace("$Bandwidth$", r.Bandwidth.ToString(CultureInfo.InvariantCulture));
            return Resolve(r.BaseUrl, m);
        }

        private static string ExpandInit(DashRep r)
        {
            string m = r.InitTemplate;
            if (string.IsNullOrEmpty(m)) return null;
            m = m.Replace("$RepresentationID$", r.Id == null ? "" : r.Id);
            m = m.Replace("$Bandwidth$", r.Bandwidth.ToString(CultureInfo.InvariantCulture));
            return Resolve(r.BaseUrl, m);
        }

        // ---------- shared ----------

        private static void SetLocalExtension(DownloadItem item, string defaultFolder, string ext)
        {
            if (string.Equals(Path.GetExtension(item.LocalPath), ext, StringComparison.OrdinalIgnoreCase)) return;
            string folder = Path.GetDirectoryName(item.LocalPath);
            if (string.IsNullOrEmpty(folder)) folder = defaultFolder;
            string baseName = Path.GetFileNameWithoutExtension(item.LocalPath);
            string path = Path.Combine(folder, baseName + ext);
            int n = 1;
            while (File.Exists(path))
            {
                path = Path.Combine(folder, baseName + " (" + n + ")" + ext);
                n++;
            }
            item.LocalPath = path;
            item.FileName = Path.GetFileName(path);
            item.Type = ext.TrimStart('.').ToUpperInvariant();
        }

        private static long Speed(long bytes, DateTime started)
        {
            double secs = (DateTime.Now - started).TotalSeconds;
            return secs > 0.2 ? (long)(bytes / secs) : 0;
        }

        private static void AppendFile(FileStream o, string path)
        {
            using (FileStream i = File.OpenRead(path))
            {
                byte[] buf = new byte[65536];
                while (true)
                {
                    int n = i.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    o.Write(buf, 0, n);
                }
            }
        }

        private static string Resolve(string baseUrl, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return baseUrl;
            try { return new Uri(new Uri(baseUrl), rel).AbsoluteUri; }
            catch { return rel; }
        }

        private static double IsoSeconds(string iso)
        {
            if (string.IsNullOrEmpty(iso) || iso[0] != 'P') return 0;
            double total = 0;
            int i = 1;
            while (i < iso.Length)
            {
                int start = i;
                while (i < iso.Length && (char.IsDigit(iso[i]) || iso[i] == '.')) i++;
                double v;
                double.TryParse(iso.Substring(start, i - start), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out v);
                if (i >= iso.Length) break;
                char unit = iso[i++];
                if (unit == 'H') total += v * 3600;
                else if (unit == 'M') total += v * 60;
                else if (unit == 'S') total += v;
                else if (unit == 'D') total += v * 86400;
                else if (unit == 'Y') total += v * 31536000;
                else if (unit == 'T') { }
            }
            return total;
        }

        private static XmlElement Child(XmlElement parent, string local)
        {
            if (parent == null) return null;
            foreach (XmlNode n in parent.ChildNodes)
            {
                XmlElement e = n as XmlElement;
                if (e != null && e.LocalName == local) return e;
            }
            return null;
        }

        private static List<XmlElement> Children(XmlElement parent, string local)
        {
            List<XmlElement> list = new List<XmlElement>();
            if (parent == null) return list;
            foreach (XmlNode n in parent.ChildNodes)
            {
                XmlElement e = n as XmlElement;
                if (e != null && e.LocalName == local) list.Add(e);
            }
            return list;
        }

        private static string Attr(XmlElement e, string name)
        {
            if (e == null) return "";
            string v = e.GetAttribute(name);
            return v == null ? "" : v;
        }

        private static string TextOf(XmlElement e)
        {
            return e == null ? "" : e.InnerText.Trim();
        }
    }
}
