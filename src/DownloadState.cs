using System;

namespace ZID
{
    // One state for the whole app. Persisted and UI strings remain in
    // DownloadStates so state.json and ui.html stay backward compatible.
    public enum DownloadState
    {
        Queued,
        Starting,
        Downloading,
        WaitingNetwork,
        Paused,
        Merging,
        Waiting,
        Completed,
        Failed,
        Removed
    }

    internal static class DownloadStates
    {
        // Legacy status <-> state. "Connecting"/"Done"/"Error" are the strings
        // already used by state.json and ui.html, so they stay on the wire.
        internal static DownloadState Parse(string status)
        {
            switch (status)
            {
                case "Queued":      return DownloadState.Queued;
                case "Connecting":  return DownloadState.Starting;
                case "Downloading": return DownloadState.Downloading;
                case "WaitingNetwork": return DownloadState.WaitingNetwork;
                case "Paused":      return DownloadState.Paused;
                case "Merging":     return DownloadState.Merging;
                case "Waiting":     return DownloadState.Waiting;
                case "Done":        return DownloadState.Completed;
                case "Error":       return DownloadState.Failed;
                case "Removed":     return DownloadState.Removed;
                default:            return DownloadState.Paused;
            }
        }

        internal static string ToStatus(DownloadState state)
        {
            switch (state)
            {
                case DownloadState.Queued:      return "Queued";
                case DownloadState.Starting:    return "Connecting";
                case DownloadState.Downloading: return "Downloading";
                case DownloadState.WaitingNetwork: return "WaitingNetwork";
                case DownloadState.Paused:      return "Paused";
                case DownloadState.Merging:     return "Merging";
                case DownloadState.Waiting:     return "Waiting";
                case DownloadState.Completed:   return "Done";
                case DownloadState.Failed:      return "Error";
                case DownloadState.Removed:     return "Removed";
                default:                        return "Paused";
            }
        }

        internal static bool IsActive(DownloadState state)
        {
            return state == DownloadState.Queued ||
                   state == DownloadState.Starting ||
                   state == DownloadState.Downloading;
        }

        internal static bool IsFinished(DownloadState state)
        {
            return state == DownloadState.Completed || state == DownloadState.Failed;
        }

        // Same-state writes are allowed (idempotent); everything else must be
        // listed. Derived from the real flows: hidden audio waits (Queued ->
        // Waiting), engine retry/reconnect goes back to Starting or Queued,
        // editing moves a finished row to Paused, and a restart starts/queues a
        // Completed row. The manager enforces this; load/claim paths write directly.
        internal static bool CanTransition(DownloadState from, DownloadState to)
        {
            if (from == to) return true;
            switch (from)
            {
                case DownloadState.Queued:
                    return to == DownloadState.Starting || to == DownloadState.Waiting ||
                           to == DownloadState.Paused || to == DownloadState.Removed;
                case DownloadState.Starting:
                    return to == DownloadState.Downloading || to == DownloadState.Failed ||
                           to == DownloadState.Paused || to == DownloadState.Queued ||
                           to == DownloadState.WaitingNetwork || to == DownloadState.Removed;
                case DownloadState.Downloading:
                    return to == DownloadState.Starting || to == DownloadState.Queued ||
                           to == DownloadState.Paused || to == DownloadState.Merging ||
                           to == DownloadState.Completed || to == DownloadState.Failed ||
                           to == DownloadState.WaitingNetwork || to == DownloadState.Removed;
                case DownloadState.WaitingNetwork:
                    return to == DownloadState.Queued || to == DownloadState.Starting ||
                           to == DownloadState.Paused || to == DownloadState.Failed ||
                           to == DownloadState.Removed;
                case DownloadState.Paused:
                    return to == DownloadState.Queued || to == DownloadState.Starting ||
                           to == DownloadState.Removed;
                case DownloadState.Waiting:
                    return to == DownloadState.Queued || to == DownloadState.Starting ||
                           to == DownloadState.Paused || to == DownloadState.Failed ||
                           to == DownloadState.Removed;
                case DownloadState.Merging:
                    return to == DownloadState.Completed || to == DownloadState.Failed ||
                           to == DownloadState.Removed;
                case DownloadState.Completed:
                    // Restart, edit and a late audio stream all start from Done.
                    return to == DownloadState.Starting || to == DownloadState.Queued ||
                           to == DownloadState.Paused || to == DownloadState.Merging ||
                           to == DownloadState.Failed || to == DownloadState.Removed;
                case DownloadState.Failed:
                    return to == DownloadState.Queued || to == DownloadState.Starting ||
                           to == DownloadState.Paused || to == DownloadState.Removed;
                default:
                    return false;
            }
        }
    }
}
