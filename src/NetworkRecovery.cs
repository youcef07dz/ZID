using System;
using System.Collections.Generic;
using System.Threading;

namespace ZID
{
    // Everything the network recovery needs from the form. The service owns the
    // scan, throttle and probe thread; the host owns the row resume and the UI.
    internal interface INetworkHost
    {
        List<DownloadItem> Snapshot();
        void ResumeAfterWait(DownloadItem item);
        int StaggerDelay(int index);
        bool NetworkAvailable();
        bool ProbeConnection(string url, int timeoutMs);
        void PushState();
        void Log(string message);
    }

    // Restore path for WaitingNetwork rows and, on a NetworkChange event, for
    // legacy rows that failed while the network was down. The ticker probes at
    // most every 10 s; the event probes immediately.
    internal sealed class NetworkRecovery
    {
        private const int CheckIntervalMs = 10000;

        private readonly INetworkHost _host;
        private int _lastCheck;
        private volatile bool _busy;

        internal NetworkRecovery(INetworkHost host)
        {
            _host = host;
        }

        internal void Check(bool restored)
        {
            List<DownloadItem> snap = _host.Snapshot();
            string probeUrl;
            bool waiting, failed;
            Scan(snap, restored, out waiting, out failed, out probeUrl);
            if (!waiting && !failed) return;

            int now = Environment.TickCount;
            if (Throttled(_lastCheck, now, restored)) return;
            _lastCheck = now;

            // Probe off the UI thread: an unreachable host can take seconds.
            if (_busy) return;
            _busy = true;
            ThreadPool.QueueUserWorkItem(new WaitCallback(Probe),
                                         new object[] { snap, restored, probeUrl });
        }

        private void Probe(object state)
        {
            object[] a = (object[])state;
            try
            {
                ProbeNow((List<DownloadItem>)a[0], (bool)a[1], (string)a[2]);
            }
            catch (Exception ex) { _host.Log("network probe: " + ex.Message); }
            finally { _busy = false; }
        }

        internal void ProbeNow(List<DownloadItem> snap, bool restored, string probeUrl)
        {
            if (!_host.NetworkAvailable()) return;
            // A probe against one pending URL keeps a captive portal or a
            // dead host from being mistaken for a restore.
            if (!Util.IsBlank(probeUrl) && !_host.ProbeConnection(probeUrl, 2000)) return;
            Resume(snap, restored);
        }

        internal void Resume(List<DownloadItem> snap, bool includeFailed)
        {
            int count = 0;
            for (int i = 0; i < snap.Count; i++)
            {
                DownloadItem it = snap[i];
                if (!ShouldResume(it, includeFailed)) continue;

                int delay = _host.StaggerDelay(count);
                count++;
                if (delay <= 0) { _host.ResumeAfterWait(it); continue; }
                ThreadPool.QueueUserWorkItem(new WaitCallback(StaggeredResume),
                                             new object[] { it, delay });
            }
            if (count > 0) _host.Log("network: resuming " + count + " download(s)");
            _host.PushState();
        }

        private void StaggeredResume(object state)
        {
            object[] a = (object[])state;
            try
            {
                Thread.Sleep((int)a[1]);
                _host.ResumeAfterWait((DownloadItem)a[0]);
            }
            catch (Exception ex) { _host.Log("network resume: " + ex.Message); }
        }

        internal static bool ShouldResume(DownloadItem it, bool includeFailed)
        {
            if (it.State == DownloadState.WaitingNetwork) return true;
            return includeFailed && it.State == DownloadState.Failed && !it.Merged &&
                   RetryPolicy.IsNetworkError(it.Eta);
        }

        internal static void Scan(List<DownloadItem> snap, bool includeFailed,
                                  out bool waiting, out bool failed, out string probeUrl)
        {
            waiting = false;
            failed = false;
            probeUrl = null;
            for (int i = 0; i < snap.Count; i++)
            {
                DownloadItem it = snap[i];
                bool isWaiting = it.State == DownloadState.WaitingNetwork;
                bool isFailed = includeFailed && it.State == DownloadState.Failed &&
                                !it.Merged && RetryPolicy.IsNetworkError(it.Eta);
                if (!isWaiting && !isFailed) continue;

                if (isWaiting) waiting = true;
                if (isFailed) failed = true;
                if (probeUrl == null && NetworkMonitor.CanProbe(it.Url)) probeUrl = it.Url;
            }
        }

        internal static bool Throttled(int lastCheck, int now, bool restored)
        {
            return !restored && unchecked(now - lastCheck) < CheckIntervalMs;
        }
    }
}
