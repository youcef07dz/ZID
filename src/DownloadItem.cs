using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ZID
{
    // =====================================================================
    //  Data model
    // =====================================================================
    public class DownloadItem
    {
        public int      Id;
        public string   FileName;
        public string   Url;
        public string   Cookie;
        public string   Referer;
        public string   UserAgent;
        public string   Headers;    // extra "Name: value" lines, one per line
        public string   Checksum;   // aria2 spec, e.g. sha-256=<hex>
        public string   Mirror;     // fallback URL for the same file
        public string   Proxy;      // per-item proxy, "" = use the global setting
        public string   Type;
        public long     Size;
        public int      Progress;
        public long     Speed;
        public string   Status;

        // Status stays the persisted/UI string; code uses this instead of comparing it.
        public DownloadState State
        {
            get { return DownloadStates.Parse(Status); }
            set { Status = DownloadStates.ToStatus(value); }
        }
        public string   Eta;
        public DateTime Date;
        public DateTime StartAt;    // MinValue = start now; otherwise wait until then
        public int      Priority;   // 1 = high, 0 = normal, -1 = low
        public string   Kind;       // "video", "audio", "muxed" (extension only)
        public string   MergeKey;   // YouTube video id for pairing
        public bool     Merged;
        public bool     Skip;       // auto-paired audio no longer needed
        public bool     Auto;       // hidden audio sent by the extension
        public bool     RefreshPending; // waiting for the browser to send a fresh URL
        public DateTime RefreshAt;      // runtime-only; stale waits stop matching

        // runtime-only
        public string   LocalPath;
        public Thread   Thread;
        public Process  Proc;
        public volatile bool Abort;
        public volatile bool WantRun;
        public bool     ResumeOnLoad;
        public bool     SchedPaused;   // paused by the daily stop time, resume next window
        public string   EngineId;      // engine task handle while the shared engine owns it
        public long     BytesReceived;
        public volatile int ActiveConnections;
        public volatile int Generation;
        public int RetryCount;      // persisted; reset by a manual restart
        // runtime-only credentials from the 401/407 prompt; never written to
        // the state file, so a restart asks again instead of storing a password.
        public string   AuthUser;
        public string   AuthPass;
        public bool     AuthProxy;
        public bool     AuthPrompted;

        // per-item overrides (0 = use the global setting)
        public int      MaxConn;
        public long     SpeedKbps;

        // "", "none", "open", "folder", "shutdown" ("" = show finished dialog)
        public string   AfterDone;
        public bool     AfterDoneRan;
        public DateTime FinishedAt;
    }
}
