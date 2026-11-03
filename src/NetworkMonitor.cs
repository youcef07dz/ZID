using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ZID
{
    // Connectivity is not a failure: while the adapter or the host is
    // unreachable a row waits here and keeps its retry budget for real errors.
    internal static class NetworkMonitor
    {
        internal static bool IsAvailable()
        {
            try { return NetworkInterface.GetIsNetworkAvailable(); }
            catch { return true; }   // never turn a probe bug into a false outage
        }

        // Schemes the TCP probe understands (FTP rows fall back to adapter-only).
        internal static bool CanProbe(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            return u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps;
        }

        // Cheap TCP connect to the URL's host; no HTTP request is sent.
        internal static bool Probe(string url, int timeoutMs)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) ||
                !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
                return false;

            int port = u.Port;
            if (port <= 0) port = u.Scheme == Uri.UriSchemeHttps ? 443 : 80;

            TcpClient c = new TcpClient();
            try
            {
                IAsyncResult ar = c.BeginConnect(u.Host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false)) return false;
                c.EndConnect(ar);
                return c.Connected;
            }
            catch { return false; }
            finally
            {
                try { c.Close(); } catch { }
            }
        }

        // Offline plus a network-ish error means "wait", not "retry".
        internal static bool ShouldWaitForNetwork(bool online, EngineErrorClass cls)
        {
            if (online) return false;
            return cls == EngineErrorClass.Network ||
                   cls == EngineErrorClass.Transient ||
                   cls == EngineErrorClass.Unknown;
        }

        // Short, bounded stagger: 0.25 s per row, at most 5 s, plus jitter.
        internal static int StaggerMs(int index, int jitter)
        {
            if (index < 0) index = 0;
            if (jitter < 0) jitter = 0;
            int baseMs = index * 250;
            if (baseMs > 5000) baseMs = 5000;
            return baseMs + jitter % 250;
        }
    }
}
