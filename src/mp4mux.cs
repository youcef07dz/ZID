using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZID
{
    // Remuxes a fragmented MP4 video track and a fragmented M4A audio track
    // into one plain (non-fragmented) MP4. Sample data is copied, never decoded.
    internal static class Mp4Mux
    {
        // shortcut: fragmented ISO-BMFF only; no fallback muxer.
        public static bool Merge(string videoPath, string audioPath, string outPath, out string error)
        {
            error = "";
            try
            {
                if (String.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                    throw new Exception("video file missing");
                if (String.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
                    throw new Exception("audio file missing");
                MergeCore(videoPath, audioPath, outPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                return false;
            }
        }

        // Reads just the moov header. Returns false while it is not available yet.
        public static bool TryReadTrackKinds(string path, out bool hasVideo, out bool hasAudio)
        {
            hasVideo = false;
            hasAudio = false;
            try
            {
                if (String.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite, 1 << 16))
                {
                    List<Box> top = TopLevel(fs);
                    Box moov = null;
                    for (int i = 0; i < top.Count; i++)
                        if (top[i].Type == "moov") moov = top[i];
                    if (moov == null) return false;

                    byte[] data = ReadBytes(fs, moov.Start, CheckedSize(moov.Size));
                    List<Box> kids = Boxes(data, 8, data.Length);
                    for (int i = 0; i < kids.Count; i++)
                    {
                        if (kids[i].Type != "trak") continue;
                        List<Box> tk = Boxes(data, (int)kids[i].DataStart, (int)kids[i].End);
                        for (int j = 0; j < tk.Count; j++)
                        {
                            if (tk[j].Type != "mdia") continue;
                            List<Box> md = Boxes(data, (int)tk[j].DataStart, (int)tk[j].End);
                            for (int k = 0; k < md.Count; k++)
                            {
                                if (md[k].Type != "hdlr" || md[k].Size < 20) continue;
                                string h = Encoding.ASCII.GetString(data,
                                    (int)md[k].DataStart + 8, 4);
                                if (h == "vide") hasVideo = true;
                                else if (h == "soun") hasAudio = true;
                            }
                        }
                    }
                    return true;
                }
            }
            catch { return false; }
        }

        private sealed class Box
        {
            public string Type;
            public long Start;
            public long Size;
            public int Header;
            public long DataStart;
            public long End;
        }

        private sealed class Trex
        {
            public uint TrackId;
            public int Duration;
            public int Size;
            public uint Flags;
        }

        private sealed class Sample
        {
            public long Offset;
            public int Size;
            public int Duration;
            public int Cts;
            public bool Sync;
            public long Decode;
            public long OutOffset;
        }

        private sealed class Track
        {
            public bool Video;
            public int TrackId;
            public int Timescale;
            public long MediaDuration;
            public long SourceMovieTimescale;
            public byte[] Tkhd;
            public byte[] Edts;
            public byte[] Mdhd;
            public byte[] Hdlr;
            public byte[] Mhd;
            public byte[] Dinf;
            public byte[] Stsd;
            public List<Sample> Samples = new List<Sample>();
        }

        private sealed class Init
        {
            public byte[] Ftyp;
            public byte[] Moov;
            public byte[] Mvhd;
            public long MovieTimescale;
            public long MovieDuration;
            public Track Video;
            public Track Audio;
            public Dictionary<uint, Trex> Trex = new Dictionary<uint, Trex>();
        }

        private sealed class Ref
        {
            public Track Track;
            public Sample Sample;
        }

        private static void MergeCore(string videoPath, string audioPath, string outPath)
        {
            byte[] ftyp = BuildFtyp();
            Init vi, ai;
            Track video, audio;

            using (FileStream vfs = new FileStream(videoPath, FileMode.Open, FileAccess.Read,
                                                   FileShare.Read, 1 << 20))
            using (FileStream afs = new FileStream(audioPath, FileMode.Open, FileAccess.Read,
                                                   FileShare.Read, 1 << 20))
            {
                vi = LoadInit(vfs);
                ai = LoadInit(afs);
                ParseFragments(vfs, vi, true);
                ParseFragments(afs, ai, false);

                video = vi.Video;
                audio = ai.Audio;
                if (video == null) throw new Exception("no video track");
                if (audio == null) throw new Exception("no audio track");
                if (video.Samples.Count == 0) throw new Exception("no video samples");
                if (audio.Samples.Count == 0) throw new Exception("no audio samples");
                if (video.Timescale <= 0 || audio.Timescale <= 0)
                    throw new Exception("bad media timescale");

                video.MediaDuration = SumDuration(video.Samples);
                audio.MediaDuration = SumDuration(audio.Samples);
                if (video.MediaDuration <= 0 || audio.MediaDuration <= 0)
                    throw new Exception("zero media duration");

                long movieTs = vi.MovieTimescale > 0 ? vi.MovieTimescale : 1000;
                long vDur = Scale(video.MediaDuration, movieTs, video.Timescale);
                long aDur = Scale(audio.MediaDuration, movieTs, audio.Timescale);
                long movieDur = Math.Max(vDur, aDur);

                List<Ref> merged = new List<Ref>();
                for (int i = 0; i < video.Samples.Count; i++)
                {
                    Ref r = new Ref();
                    r.Track = video;
                    r.Sample = video.Samples[i];
                    merged.Add(r);
                }
                for (int i = 0; i < audio.Samples.Count; i++)
                {
                    Ref r = new Ref();
                    r.Track = audio;
                    r.Sample = audio.Samples[i];
                    merged.Add(r);
                }
                merged.Sort(delegate(Ref a, Ref b)
                {
                    double ta = (double)a.Sample.Decode / a.Track.Timescale;
                    double tb = (double)b.Sample.Decode / b.Track.Timescale;
                    if (ta < tb) return -1;
                    if (ta > tb) return 1;
                    if (a.Track == b.Track) return a.Sample.Decode.CompareTo(b.Sample.Decode);
                    return a.Track.Video ? -1 : 1;
                });

                long totalData = 0;
                for (int i = 0; i < merged.Count; i++)
                {
                    merged[i].Sample.OutOffset = totalData;
                    totalData += merged[i].Sample.Size;
                    if (totalData < 0) throw new Exception("output too large");
                }

                bool co64 = totalData > 0xF0000000L;
                byte[] moov = null;
                long baseOffset = 0;
                for (int pass = 0; pass < 4; pass++)
                {
                    moov = BuildMoov(vi, video, audio, movieTs, movieDur, co64, 0);
                    baseOffset = ftyp.Length + moov.Length + MdatHeaderSize(totalData);
                    if (!co64 && baseOffset + totalData > (long)uint.MaxValue)
                    {
                        co64 = true;
                        continue;
                    }
                    break;
                }
                moov = BuildMoov(vi, video, audio, movieTs, movieDur, co64, baseOffset);

                using (FileStream outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write,
                                                         FileShare.None, 1 << 20))
                {
                    outFs.Write(ftyp, 0, ftyp.Length);
                    outFs.Write(moov, 0, moov.Length);
                    WriteMdatHeader(outFs, totalData);

                    byte[] buffer = new byte[1 << 20];
                    for (int i = 0; i < merged.Count; i++)
                    {
                        Sample s = merged[i].Sample;
                        FileStream src = merged[i].Track.Video ? vfs : afs;
                        CopyRange(src, outFs, s.Offset, s.Size, buffer);
                    }
                }
            }
        }

        private static byte[] BuildFtyp()
        {
            MemoryStream body = new MemoryStream();
            WriteFourCC(body, "isom");
            Write32(body, 0x200);
            WriteFourCC(body, "isom");
            WriteFourCC(body, "iso6");
            WriteFourCC(body, "mp41");
            return MakeBox("ftyp", body.ToArray());
        }

        private static byte[] BuildMoov(Init init, Track video, Track audio, long movieTs,
                                        long movieDur, bool co64, long baseOffset)
        {
            byte[] mvhd = PatchMvhd(init.Mvhd, movieTs, movieDur);
            byte[] vTrak = BuildTrak(video, 1, video.SourceMovieTimescale, movieTs,
                                     Scale(video.MediaDuration, movieTs, video.Timescale),
                                     co64, baseOffset);
            byte[] aTrak = BuildTrak(audio, 2, audio.SourceMovieTimescale, movieTs,
                                     Scale(audio.MediaDuration, movieTs, audio.Timescale),
                                     co64, baseOffset);
            return MakeBox("moov", Combine(mvhd, vTrak, aTrak));
        }

        private static byte[] BuildTrak(Track t, int trackId, long sourceMovieTs, long movieTs,
                                        long movieDur, bool co64, long baseOffset)
        {
            byte[] tkhd = PatchTkhd(t.Tkhd, trackId, movieDur);
            byte[] edts = t.Edts == null ? null : PatchEdts(t.Edts, sourceMovieTs, movieTs);
            byte[] mdhd = PatchMdhd(t.Mdhd, t.MediaDuration);
            byte[] stbl = BuildStbl(t, co64, baseOffset);
            byte[] minf = MakeBox("minf", Combine(t.Mhd, t.Dinf, stbl));
            byte[] mdia = MakeBox("mdia", Combine(mdhd, t.Hdlr, minf));
            return MakeBox("trak", Combine(tkhd, edts, mdia));
        }

        private static byte[] BuildStbl(Track t, bool co64, long baseOffset)
        {
            if (t.Stsd == null) throw new Exception("missing stsd");
            return MakeBox("stbl", Combine(
                t.Stsd,
                BuildStts(t.Samples),
                BuildCtts(t.Samples),
                BuildStsc(),
                BuildStsz(t.Samples),
                BuildStco(t.Samples, co64, baseOffset),
                BuildStss(t.Samples)));
        }

        private static byte[] BuildStts(List<Sample> samples)
        {
            if (samples.Count == 0) throw new Exception("empty sample table");
            List<int[]> entries = new List<int[]>();
            int count = 1;
            int dur = samples[0].Duration;
            if (dur <= 0) throw new Exception("bad sample duration");
            for (int i = 1; i < samples.Count; i++)
            {
                int d = samples[i].Duration;
                if (d <= 0) throw new Exception("bad sample duration");
                if (d == dur) count++;
                else
                {
                    entries.Add(new int[] { count, dur });
                    count = 1;
                    dur = d;
                }
            }
            entries.Add(new int[] { count, dur });

            MemoryStream body = new MemoryStream();
            Write32(body, 0u);
            Write32(body, (uint)entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                Write32(body, (uint)entries[i][0]);
                Write32(body, (uint)entries[i][1]);
            }
            return MakeBox("stts", body.ToArray());
        }

        private static byte[] BuildCtts(List<Sample> samples)
        {
            bool any = false;
            bool neg = false;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].Cts != 0) any = true;
                if (samples[i].Cts < 0) neg = true;
            }
            if (!any) return null;

            List<int[]> entries = new List<int[]>();
            int count = 1;
            int cts = samples[0].Cts;
            for (int i = 1; i < samples.Count; i++)
            {
                int c = samples[i].Cts;
                if (c == cts) count++;
                else
                {
                    entries.Add(new int[] { count, cts });
                    count = 1;
                    cts = c;
                }
            }
            entries.Add(new int[] { count, cts });

            MemoryStream body = new MemoryStream();
            Write32(body, neg ? 0x01000000u : 0u);
            Write32(body, (uint)entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                Write32(body, (uint)entries[i][0]);
                Write32(body, (uint)entries[i][1]);
            }
            return MakeBox("ctts", body.ToArray());
        }

        private static byte[] BuildStsc()
        {
            MemoryStream body = new MemoryStream();
            Write32(body, 0u);
            Write32(body, 1u);
            Write32(body, 1u);
            Write32(body, 1u);
            Write32(body, 1u);
            return MakeBox("stsc", body.ToArray());
        }

        private static byte[] BuildStsz(List<Sample> samples)
        {
            if (samples.Count == 0) throw new Exception("empty sample table");
            int first = samples[0].Size;
            bool same = true;
            for (int i = 1; i < samples.Count; i++)
                if (samples[i].Size != first) { same = false; break; }

            MemoryStream body = new MemoryStream();
            Write32(body, 0u);
            Write32(body, same ? (uint)first : 0u);
            Write32(body, (uint)samples.Count);
            if (!same)
                for (int i = 0; i < samples.Count; i++)
                    Write32(body, (uint)samples[i].Size);
            return MakeBox("stsz", body.ToArray());
        }

        private static byte[] BuildStco(List<Sample> samples, bool co64, long baseOffset)
        {
            MemoryStream body = new MemoryStream();
            Write32(body, 0u);
            Write32(body, (uint)samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                long off = baseOffset + samples[i].OutOffset;
                if (co64) Write64(body, (ulong)off);
                else
                {
                    if (off < 0 || off > (long)uint.MaxValue) throw new Exception("stco offset overflow");
                    Write32(body, (uint)off);
                }
            }
            return MakeBox(co64 ? "co64" : "stco", body.ToArray());
        }

        private static byte[] BuildStss(List<Sample> samples)
        {
            bool all = true;
            for (int i = 0; i < samples.Count; i++)
                if (!samples[i].Sync) { all = false; break; }
            if (all) return null;

            MemoryStream body = new MemoryStream();
            Write32(body, 0u);
            int n = 0;
            for (int i = 0; i < samples.Count; i++) if (samples[i].Sync) n++;
            Write32(body, (uint)n);
            for (int i = 0; i < samples.Count; i++)
                if (samples[i].Sync) Write32(body, (uint)(i + 1));
            return MakeBox("stss", body.ToArray());
        }

        private static byte[] PatchMvhd(byte[] raw, long movieTs, long movieDur)
        {
            if (raw == null || raw.Length < 108) throw new Exception("bad mvhd");
            byte[] b = (byte[])raw.Clone();
            int ver = b[8];
            int tsOff = ver == 1 ? 28 : 20;
            int durOff = ver == 1 ? 32 : 24;
            int nextOff = ver == 1 ? 116 : 104;
            if (b.Length < nextOff + 4) throw new Exception("bad mvhd");

            Write32(b, tsOff, (uint)movieTs);
            if (ver == 1) Write64(b, durOff, (ulong)movieDur);
            else Write32(b, durOff, (uint)movieDur);
            Write32(b, nextOff, 3);
            return b;
        }

        private static byte[] PatchTkhd(byte[] raw, int trackId, long duration)
        {
            if (raw == null || raw.Length < 32) throw new Exception("bad tkhd");
            byte[] b = (byte[])raw.Clone();
            int ver = b[8];
            int idOff = ver == 1 ? 28 : 20;
            int durOff = ver == 1 ? 36 : 28;
            if (b.Length < durOff + (ver == 1 ? 8 : 4)) throw new Exception("bad tkhd");

            Write32(b, idOff, (uint)trackId);
            if (ver == 1) Write64(b, durOff, (ulong)duration);
            else Write32(b, durOff, (uint)duration);
            return b;
        }

        private static byte[] PatchMdhd(byte[] raw, long duration)
        {
            if (raw == null || raw.Length < 28) throw new Exception("bad mdhd");
            byte[] b = (byte[])raw.Clone();
            int ver = b[8];
            int durOff = ver == 1 ? 32 : 24;
            if (b.Length < durOff + (ver == 1 ? 8 : 4)) throw new Exception("bad mdhd");

            if (ver == 1) Write64(b, durOff, (ulong)duration);
            else Write32(b, durOff, (uint)duration);
            return b;
        }

        private static byte[] PatchEdts(byte[] raw, long sourceMovieTs, long movieTs)
        {
            if (raw == null || sourceMovieTs <= 0 || movieTs <= 0 || sourceMovieTs == movieTs)
                return raw;

            byte[] b = (byte[])raw.Clone();
            List<Box> kids = Boxes(b, 0, b.Length);
            for (int k = 0; k < kids.Count; k++)
            {
                Box elst = kids[k];
                if (elst.Type != "elst") continue;
                int p = (int)elst.DataStart;
                int vf = (int)Read32(b, p);
                int ver = (vf >> 24) & 0xFF;
                int count = (int)Read32(b, p + 4);
                p += 8;
                for (int i = 0; i < count; i++)
                {
                    int segOff = p;
                    long seg;
                    if (ver == 1)
                    {
                        seg = (long)Read64(b, p); p += 8 + 8 + 4;
                    }
                    else
                    {
                        seg = Read32(b, p); p += 4 + 4 + 4;
                    }
                    long scaled = Scale(seg, movieTs, sourceMovieTs);
                    if (ver == 1) Write64(b, segOff, (ulong)scaled);
                    else Write32(b, segOff, (uint)scaled);
                }
            }
            return b;
        }

        private static Init LoadInit(FileStream fs)
        {
            Init init = new Init();
            List<Box> top = TopLevel(fs);
            for (int i = 0; i < top.Count; i++)
            {
                Box b = top[i];
                if (b.Type == "ftyp" && b.Size <= int.MaxValue)
                    init.Ftyp = ReadBytes(fs, b.Start, (int)b.Size);
                else if (b.Type == "moov")
                    init.Moov = ReadBytes(fs, b.Start, CheckedSize(b.Size));
            }
            if (init.Moov == null) throw new Exception("no moov (not a fragmented MP4?)");

            List<Box> kids = Boxes(init.Moov, 8, init.Moov.Length);
            for (int i = 0; i < kids.Count; i++)
            {
                if (kids[i].Type == "mvhd")
                {
                    init.Mvhd = Slice(init.Moov, kids[i]);
                    ParseMvhd(init);
                }
            }
            for (int i = 0; i < kids.Count; i++)
                if (kids[i].Type == "mvex")
                    ParseMvex(init.Moov, kids[i], init);

            for (int i = 0; i < kids.Count; i++)
            {
                if (kids[i].Type != "trak") continue;
                Track t = ParseTrak(init.Moov, kids[i]);
                if (t == null) continue;
                t.SourceMovieTimescale = init.MovieTimescale;
                if (t.Video) init.Video = t;
                else init.Audio = t;
            }
            return init;
        }

        private static void ParseMvhd(Init init)
        {
            byte[] b = init.Mvhd;
            if (b.Length < 108) throw new Exception("bad mvhd");
            int ver = b[8];
            if (ver == 1)
            {
                init.MovieTimescale = (long)Read32(b, 28);
                init.MovieDuration = (long)Read64(b, 32);
            }
            else
            {
                init.MovieTimescale = (long)Read32(b, 20);
                init.MovieDuration = (long)Read32(b, 24);
            }
            if (init.MovieTimescale <= 0) init.MovieTimescale = 1000;
        }

        private static void ParseMvex(byte[] moov, Box mvex, Init init)
        {
            List<Box> kids = Boxes(moov, (int)mvex.DataStart, (int)mvex.End);
            for (int i = 0; i < kids.Count; i++)
            {
                Box b = kids[i];
                if (b.Type != "trex" || b.Size < 32) continue;
                Trex t = new Trex();
                t.TrackId = Read32(moov, (int)b.DataStart + 4);
                t.Duration = (int)Read32(moov, (int)b.DataStart + 12);
                t.Size = (int)Read32(moov, (int)b.DataStart + 16);
                t.Flags = Read32(moov, (int)b.DataStart + 20);
                init.Trex[t.TrackId] = t;
            }
        }

        private static Track ParseTrak(byte[] moov, Box trak)
        {
            Track t = new Track();
            List<Box> kids = Boxes(moov, (int)trak.DataStart, (int)trak.End);
            for (int i = 0; i < kids.Count; i++)
            {
                Box b = kids[i];
                if (b.Type == "tkhd")
                {
                    t.Tkhd = Slice(moov, b);
                    t.TrackId = (int)Read32(t.Tkhd, t.Tkhd[8] == 1 ? 28 : 20);
                }
                else if (b.Type == "edts")
                {
                    t.Edts = Slice(moov, b);
                }
                else if (b.Type == "mdia")
                {
                    List<Box> m = Boxes(moov, (int)b.DataStart, (int)b.End);
                    for (int j = 0; j < m.Count; j++)
                    {
                        Box c = m[j];
                        if (c.Type == "mdhd")
                        {
                            t.Mdhd = Slice(moov, c);
                            t.Timescale = (int)Read32(t.Mdhd, t.Mdhd[8] == 1 ? 28 : 20);
                        }
                        else if (c.Type == "hdlr")
                        {
                            t.Hdlr = Slice(moov, c);
                            t.Video = t.Hdlr.Length >= 20 &&
                                      Encoding.ASCII.GetString(t.Hdlr, 16, 4) == "vide";
                        }
                        else if (c.Type == "minf")
                        {
                            List<Box> n = Boxes(moov, (int)c.DataStart, (int)c.End);
                            for (int k = 0; k < n.Count; k++)
                            {
                                Box d = n[k];
                                if (d.Type == "vmhd" || d.Type == "smhd")
                                    t.Mhd = Slice(moov, d);
                                else if (d.Type == "dinf")
                                    t.Dinf = Slice(moov, d);
                                else if (d.Type == "stbl")
                                {
                                    List<Box> s = Boxes(moov, (int)d.DataStart, (int)d.End);
                                    for (int x = 0; x < s.Count; x++)
                                        if (s[x].Type == "stsd")
                                            t.Stsd = Slice(moov, s[x]);
                                }
                            }
                        }
                    }
                }
            }
            if (t.Tkhd == null || t.Mdhd == null || t.Hdlr == null || t.Stsd == null ||
                t.Mhd == null || t.Dinf == null)
                return null;
            return t;
        }

        private static void ParseFragments(FileStream fs, Init init, bool videoRole)
        {
            Track track = videoRole ? init.Video : init.Audio;
            if (track == null) return;

            List<Box> top = TopLevel(fs);
            long nextDecode = 0;
            for (int i = 0; i < top.Count; i++)
            {
                Box moof = top[i];
                if (moof.Type != "moof") continue;
                byte[] data = ReadBytes(fs, moof.Start, CheckedSize(moof.Size));
                List<Box> kids = Boxes(data, 8, data.Length);
                long mdatData = -1;
                for (int k = 0; k < top.Count; k++)
                {
                    if (top[k].Type == "mdat" && top[k].Start >= moof.Start + moof.Size)
                    {
                        mdatData = top[k].DataStart;
                        break;
                    }
                }
                for (int j = 0; j < kids.Count; j++)
                {
                    if (kids[j].Type != "traf") continue;
                    nextDecode = ParseTraf(fs, data, moof.Start, moof.Size, mdatData, kids[j],
                                           init, track, nextDecode);
                }
            }
            return;
        }

        private static long ParseTraf(FileStream fs, byte[] data, long moofStart, long moofSize,
                                      long mdatData, Box traf, Init init, Track track,
                                      long nextDecode)
        {
            Box tfhd = null, tfdt = null;
            List<Box> runs = new List<Box>();
            List<Box> kids = Boxes(data, (int)traf.DataStart, (int)traf.End);
            for (int i = 0; i < kids.Count; i++)
            {
                if (kids[i].Type == "tfhd") tfhd = kids[i];
                else if (kids[i].Type == "tfdt") tfdt = kids[i];
                else if (kids[i].Type == "trun") runs.Add(kids[i]);
            }
            if (tfhd == null) throw new Exception("missing tfhd");

            int p = (int)tfhd.DataStart;
            uint vf = Read32(data, p); p += 4;
            int flags = (int)(vf & 0xFFFFFF);
            uint trackId = Read32(data, p); p += 4;
            long baseDataOffset = -1;
            if ((flags & 0x1) != 0) { baseDataOffset = (long)Read64(data, p); p += 8; }
            if ((flags & 0x2) != 0) p += 4;

            bool hasDefDur = (flags & 0x8) != 0;
            int defDur = hasDefDur ? (int)Read32(data, p) : 0; if (hasDefDur) p += 4;
            bool hasDefSize = (flags & 0x10) != 0;
            int defSize = hasDefSize ? (int)Read32(data, p) : 0; if (hasDefSize) p += 4;
            bool hasDefFlags = (flags & 0x20) != 0;
            uint defFlags = hasDefFlags ? Read32(data, p) : 0; if (hasDefFlags) p += 4;

            Trex trex;
            if (init.Trex.TryGetValue(trackId, out trex))
            {
                if (!hasDefDur) defDur = trex.Duration;
                if (!hasDefSize) defSize = trex.Size;
                if (!hasDefFlags) defFlags = trex.Flags;
            }

            long decode = nextDecode;
            if (tfdt != null)
            {
                int q = (int)tfdt.DataStart;
                int ver = data[q];
                decode = ver == 1 ? (long)Read64(data, q + 4) : (long)Read32(data, q + 4);
            }

            long basePos = baseDataOffset >= 0 ? baseDataOffset : moofStart;
            long dataPos = 0;
            bool first = true;
            for (int r = 0; r < runs.Count; r++)
            {
                Box run = runs[r];
                int q = (int)run.DataStart;
                uint rvf = Read32(data, q);
                int rver = (int)((rvf >> 24) & 0xFF);
                int rflags = (int)(rvf & 0xFFFFFF);
                int count = (int)Read32(data, q + 4);
                q += 8;

                bool hasOffset = (rflags & 0x1) != 0;
                int offset = 0;
                if (hasOffset) { offset = (int)Read32(data, q); q += 4; }
                bool hasFirst = (rflags & 0x4) != 0;
                uint firstFlags = hasFirst ? Read32(data, q) : 0; if (hasFirst) q += 4;

                if (hasOffset) dataPos = basePos + offset;
                else if (first) dataPos = mdatData >= 0 ? mdatData : moofStart + moofSize + 8;

                bool hasDur = (rflags & 0x100) != 0;
                bool hasSize = (rflags & 0x200) != 0;
                bool hasFlags = (rflags & 0x400) != 0;
                bool hasCts = (rflags & 0x800) != 0;

                for (int i = 0; i < count; i++)
                {
                    int dur = hasDur ? (int)Read32(data, q) : defDur; if (hasDur) q += 4;
                    int size = hasSize ? (int)Read32(data, q) : defSize; if (hasSize) q += 4;
                    uint fl = defFlags;
                    if (hasFlags) { fl = Read32(data, q); q += 4; }
                    else if (i == 0 && hasFirst) fl = firstFlags;
                    int cts = hasCts ? (int)Read32(data, q) : 0; if (hasCts) q += 4;

                    if (dur <= 0) throw new Exception("bad sample duration");
                    if (size < 0) throw new Exception("bad sample size");
                    if (dataPos < 0 || dataPos + size > fs.Length)
                        throw new Exception("sample data outside file");

                    Sample s = new Sample();
                    s.Offset = dataPos;
                    s.Size = size;
                    s.Duration = dur;
                    s.Cts = cts;
                    s.Sync = (fl & 0x00010000) == 0;
                    s.Decode = decode;
                    track.Samples.Add(s);

                    dataPos += size;
                    decode += dur;
                }
                first = false;
            }
            return decode;
        }

        private static List<Box> TopLevel(FileStream fs)
        {
            List<Box> list = new List<Box>();
            long len = fs.Length;
            long pos = 0;
            byte[] h = new byte[16];
            while (pos + 8 <= len)
            {
                ReadAt(fs, pos, h, 8);
                long size = Read32(h, 0);
                string type = FourCC(h, 4);
                int header = 8;
                if (size == 1)
                {
                    if (pos + 16 > len) break;
                    ReadAt(fs, pos + 8, h, 8);
                    size = (long)Read64(h, 0);
                    header = 16;
                }
                else if (size == 0)
                {
                    size = len - pos;
                }
                if (size < header || pos + size > len) break;

                Box b = new Box();
                b.Type = type; b.Start = pos; b.Size = size; b.Header = header;
                b.DataStart = pos + header; b.End = pos + size;
                list.Add(b);
                pos += size;
            }
            return list;
        }

        private static List<Box> Boxes(byte[] data, int start, int end)
        {
            List<Box> list = new List<Box>();
            while (start + 8 <= end)
            {
                long size = Read32(data, start);
                string type = FourCC(data, start + 4);
                int header = 8;
                if (size == 1)
                {
                    if (start + 16 > end) break;
                    size = (long)Read64(data, start + 8);
                    header = 16;
                }
                else if (size == 0)
                {
                    size = end - start;
                }
                if (size < header || start + size > end) break;

                Box b = new Box();
                b.Type = type; b.Start = start; b.Size = size; b.Header = header;
                b.DataStart = start + header; b.End = start + size;
                list.Add(b);
                start += (int)size;
            }
            return list;
        }

        private static byte[] ReadBytes(FileStream fs, long start, int size)
        {
            byte[] b = new byte[size];
            ReadAt(fs, start, b, size);
            return b;
        }

        private static void ReadAt(FileStream fs, long start, byte[] b, int size)
        {
            fs.Position = start;
            int off = 0;
            while (off < size)
            {
                int n = fs.Read(b, off, size - off);
                if (n <= 0) throw new Exception("unexpected end of file");
                off += n;
            }
        }

        private static void CopyRange(FileStream input, FileStream output, long offset,
                                      int size, byte[] buffer)
        {
            input.Position = offset;
            int left = size;
            while (left > 0)
            {
                int n = Math.Min(left, buffer.Length);
                int r = input.Read(buffer, 0, n);
                if (r <= 0) throw new Exception("unexpected end of file");
                output.Write(buffer, 0, r);
                left -= r;
            }
        }

        private static long SumDuration(List<Sample> samples)
        {
            long total = 0;
            for (int i = 0; i < samples.Count; i++) total += samples[i].Duration;
            return total;
        }

        private static long Scale(long value, long outTs, long inTs)
        {
            if (inTs <= 0 || outTs <= 0) return value;
            decimal d = (decimal)value * outTs / inTs;
            return (long)(d + 0.5m);
        }

        private static int MdatHeaderSize(long dataSize)
        {
            return dataSize + 8 <= (long)uint.MaxValue ? 8 : 16;
        }

        private static void WriteMdatHeader(Stream s, long dataSize)
        {
            if (dataSize + 8 <= (long)uint.MaxValue)
            {
                Write32(s, (uint)(dataSize + 8));
                WriteFourCC(s, "mdat");
            }
            else
            {
                Write32(s, 1);
                WriteFourCC(s, "mdat");
                Write64(s, (ulong)(dataSize + 16));
            }
        }

        private static int CheckedSize(long size)
        {
            if (size < 0 || size > int.MaxValue) throw new Exception("box too large");
            return (int)size;
        }

        private static byte[] Slice(byte[] data, Box b)
        {
            int start = (int)b.Start;
            int size = CheckedSize(b.Size);
            byte[] r = new byte[size];
            Buffer.BlockCopy(data, start, r, 0, size);
            return r;
        }

        private static byte[] MakeBox(string type, byte[] body)
        {
            if (body == null) body = new byte[0];
            if (body.Length > int.MaxValue - 8) throw new Exception("box too large");
            byte[] b = new byte[8 + body.Length];
            Write32(b, 0, (uint)b.Length);
            WriteFourCC(b, 4, type);
            Buffer.BlockCopy(body, 0, b, 8, body.Length);
            return b;
        }

        private static byte[] Combine(byte[] a, byte[] b)
        {
            return Combine(new byte[][] { a, b });
        }

        private static byte[] Combine(byte[] a, byte[] b, byte[] c)
        {
            return Combine(new byte[][] { a, b, c });
        }

        private static byte[] Combine(byte[] a, byte[] b, byte[] c, byte[] d)
        {
            return Combine(new byte[][] { a, b, c, d });
        }

        private static byte[] Combine(byte[] a, byte[] b, byte[] c, byte[] d, byte[] e)
        {
            return Combine(new byte[][] { a, b, c, d, e });
        }

        private static byte[] Combine(byte[] a, byte[] b, byte[] c, byte[] d, byte[] e, byte[] f)
        {
            return Combine(new byte[][] { a, b, c, d, e, f });
        }

        private static byte[] Combine(byte[] a, byte[] b, byte[] c, byte[] d, byte[] e, byte[] f, byte[] g)
        {
            return Combine(new byte[][] { a, b, c, d, e, f, g });
        }

        private static byte[] Combine(byte[][] parts)
        {
            int total = 0;
            for (int i = 0; i < parts.Length; i++)
                if (parts[i] != null) total += parts[i].Length;
            byte[] r = new byte[total];
            int off = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null) continue;
                Buffer.BlockCopy(parts[i], 0, r, off, parts[i].Length);
                off += parts[i].Length;
            }
            return r;
        }

        private static void WriteFourCC(Stream s, string fourcc)
        {
            if (fourcc == null || fourcc.Length != 4) throw new Exception("bad fourcc");
            byte[] b = Encoding.ASCII.GetBytes(fourcc);
            s.Write(b, 0, 4);
        }

        private static void WriteFourCC(byte[] b, int off, string fourcc)
        {
            if (fourcc == null || fourcc.Length != 4) throw new Exception("bad fourcc");
            byte[] x = Encoding.ASCII.GetBytes(fourcc);
            b[off] = x[0]; b[off + 1] = x[1]; b[off + 2] = x[2]; b[off + 3] = x[3];
        }

        private static void Write32(Stream s, uint v)
        {
            byte[] b = new byte[4];
            Write32(b, 0, v);
            s.Write(b, 0, 4);
        }

        private static void Write32(byte[] b, int off, uint v)
        {
            b[off] = (byte)(v >> 24);
            b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >> 8);
            b[off + 3] = (byte)v;
        }

        private static void Write64(Stream s, ulong v)
        {
            byte[] b = new byte[8];
            Write64(b, 0, v);
            s.Write(b, 0, 8);
        }

        private static void Write64(byte[] b, int off, ulong v)
        {
            b[off] = (byte)(v >> 56);
            b[off + 1] = (byte)(v >> 48);
            b[off + 2] = (byte)(v >> 40);
            b[off + 3] = (byte)(v >> 32);
            b[off + 4] = (byte)(v >> 24);
            b[off + 5] = (byte)(v >> 16);
            b[off + 6] = (byte)(v >> 8);
            b[off + 7] = (byte)v;
        }

        private static uint Read32(byte[] b, int off)
        {
            return ((uint)b[off] << 24) | ((uint)b[off + 1] << 16) |
                   ((uint)b[off + 2] << 8) | b[off + 3];
        }

        private static ulong Read64(byte[] b, int off)
        {
            return ((ulong)Read32(b, off) << 32) | Read32(b, off + 4);
        }

        private static string FourCC(byte[] b, int off)
        {
            return Encoding.ASCII.GetString(b, off, 4);
        }
    }
}
