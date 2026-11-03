using System;
using System.Collections.Generic;

namespace ZID
{
    internal enum RetryDecision
    {
        Fail,
        WaitNetwork,
        Retry
    }

    // One retry policy for every download. The error class comes from the
    // engine adapter, so this file stays engine-neutral.
    internal static class RetryPolicy
    {
        internal const int MaxRetries = 5;

        internal static RetryDecision Decide(EngineErrorClass cls, int retryCount, bool networkAvailable)
        {
            if (NetworkMonitor.ShouldWaitForNetwork(networkAvailable, cls))
                return RetryDecision.WaitNetwork;
            if (IsRetryable(cls) && retryCount < MaxRetries)
                return RetryDecision.Retry;
            return RetryDecision.Fail;
        }

        internal static bool IsRetryable(EngineErrorClass cls)
        {
            // Offline failures are routed to WaitingNetwork before this runs, so
            // Network here means an online connection failure (reset, DNS).
            return cls == EngineErrorClass.Transient ||
                   cls == EngineErrorClass.Network ||
                   cls == EngineErrorClass.Unknown;
        }

        // 2, 4, 8, 16, 32 s, capped at 60 s. A server's Retry-After wins.
        internal static int DelayMs(int retryCount, string errorMessage)
        {
            int after = RetryAfterMs(errorMessage);
            if (after > 0) return after;
            long seconds = 1L << retryCount;
            if (seconds > 60) seconds = 60;
            return (int)(seconds * 1000);
        }

        // Parses "Retry-After: 30"; returns 0 when absent. Capped at 5 minutes.
        internal static int RetryAfterMs(string message)
        {
            if (message == null) return 0;
            int at = message.IndexOf("retry-after", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return 0;
            int i = at + 11;
            while (i < message.Length && (message[i] == ':' || message[i] == ' ' || message[i] == '\t')) i++;
            int start = i;
            while (i < message.Length && message[i] >= '0' && message[i] <= '9') i++;
            int seconds;
            if (i == start || !int.TryParse(message.Substring(start, i - start), out seconds)) return 0;
            if (seconds < 1) seconds = 1;
            if (seconds > 300) seconds = 300;
            return seconds * 1000;
        }

        // Text check for legacy rows persisted as Failed by an older build;
        // live network waits use DownloadState.WaitingNetwork instead.
        internal static bool IsNetworkError(string eta)
        {
            if (eta == null) return false;
            string e = eta.ToLowerInvariant();
            return e.IndexOf("network") >= 0 || e.IndexOf("timed out") >= 0 ||
                   e.IndexOf("dns") >= 0;
        }
    }

    // A crashed engine may restart at most 3 times in 60 s.
    internal sealed class RestartGuard
    {
        internal const int MaxRestarts = 3;
        internal const int WindowSeconds = 60;
        private readonly List<DateTime> _at = new List<DateTime>();

        internal bool Allow(DateTime now)
        {
            for (int i = _at.Count - 1; i >= 0; i--)
                if (now - _at[i] > TimeSpan.FromSeconds(WindowSeconds)) _at.RemoveAt(i);
            if (_at.Count >= MaxRestarts) return false;
            _at.Add(now);
            return true;
        }
    }
}
