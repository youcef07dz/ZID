using System;
using System.Collections.Generic;

namespace ZID
{
    // Owns the concurrency slot count and picks the next queued item.
    // Priority first (high=1, normal=0, low=-1), then oldest id.
    internal sealed class QueueManager
    {
        private readonly object _sync = new object();
        private int _active;
        private int _maxConcurrent = 2;

        internal int MaxConcurrent
        {
            get { lock (_sync) return _maxConcurrent; }
            set { lock (_sync) _maxConcurrent = value < 1 ? 1 : value; }
        }

        internal int ActiveCount
        {
            get { lock (_sync) return _active; }
        }

        internal bool TryStart(bool schedulePending)
        {
            lock (_sync)
            {
                if (schedulePending || _active >= _maxConcurrent) return false;
                _active++;
                return true;
            }
        }

        internal bool TryClaimNext(List<DownloadItem> items, bool schedulePending,
                                   System.DateTime now, out DownloadItem next)
        {
            next = null;
            lock (_sync)
            {
                if (schedulePending || _active >= _maxConcurrent) return false;
                for (int i = 0; i < items.Count; i++)
                {
                    DownloadItem c = items[i];
                    if (c.State != DownloadState.Queued) continue;
                    if (c.StartAt != System.DateTime.MinValue && c.StartAt > now) continue;
                    // Snapshot order is queue order; only a higher priority wins.
                    if (next == null || c.Priority > next.Priority)
                        next = c;
                }
                if (next == null) return false;

                // Claim it under the lock so two finishing workers cannot start it twice.
                next.State = DownloadState.Starting;
                next.Eta   = "Connecting...";
                _active++;
                return true;
            }
        }

        internal void Release()
        {
            lock (_sync)
            {
                _active--;
                if (_active < 0) _active = 0;
            }
        }

        internal bool HasQueued(List<DownloadItem> items)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].State == DownloadState.Queued) return true;
            return false;
        }
    }
}
