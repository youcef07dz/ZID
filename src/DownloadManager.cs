using System;
using System.Collections.Generic;

namespace ZID
{
    internal delegate void DownloadStateChanged(DownloadItem item, DownloadState from, DownloadState to);

    // Owns the authoritative download list, its lock and the state transitions.
    // MainForm drives the workers and I/O; every user-visible state change goes
    // through SetState so transitions are checked and observers see a snapshot.
    internal sealed class DownloadManager
    {
        private readonly List<DownloadItem> _items = new List<DownloadItem>();
        private readonly object _sync = new object();
        private int _nextId = 1;

        internal event DownloadStateChanged StateChanged;

        // Returns false when the transition is not allowed or is a no-op. The
        // event fires after the lock is released, so a listener may read the list.
        internal bool SetState(DownloadItem item, DownloadState next)
        {
            DownloadState from;
            lock (_sync)
            {
                from = item.State;
                if (from == next || !DownloadStates.CanTransition(from, next)) return false;
                item.State = next;
            }
            Raise(item, from, next);
            return true;
        }

        private void Raise(DownloadItem item, DownloadState from, DownloadState to)
        {
            DownloadStateChanged h = StateChanged;
            if (h != null) h(item, from, to);
        }

        // Lifecycle verbs keep call sites readable; each is one transition.
        internal bool Queue(DownloadItem item)   { return SetState(item, DownloadState.Queued); }
        internal bool Start(DownloadItem item)   { return SetState(item, DownloadState.Starting); }
        internal bool Pause(DownloadItem item)   { return SetState(item, DownloadState.Paused); }
        internal bool Wait(DownloadItem item)    { return SetState(item, DownloadState.Waiting); }
        internal bool WaitNetwork(DownloadItem item) { return SetState(item, DownloadState.WaitingNetwork); }
        internal bool Merge(DownloadItem item)   { return SetState(item, DownloadState.Merging); }
        internal bool Complete(DownloadItem item) { return SetState(item, DownloadState.Completed); }
        internal bool Fail(DownloadItem item)    { return SetState(item, DownloadState.Failed); }

        internal int NextId
        {
            get { lock (_sync) return _nextId; }
            set { lock (_sync) _nextId = Math.Max(1, value); }
        }

        internal int TakeNextId()
        {
            lock (_sync) return _nextId++;
        }

        internal int Count
        {
            get { lock (_sync) return _items.Count; }
        }

        // Callers iterate a copy; item objects are still shared for now.
        internal List<DownloadItem> Snapshot()
        {
            lock (_sync) return new List<DownloadItem>(_items);
        }

        internal void Add(DownloadItem item)
        {
            lock (_sync) _items.Add(item);
        }

        internal void AddRange(IEnumerable<DownloadItem> items)
        {
            lock (_sync) _items.AddRange(items);
        }

        internal bool Remove(DownloadItem item)
        {
            DownloadState from;
            bool ok;
            lock (_sync)
            {
                from = item.State;
                ok = _items.Remove(item);
                if (ok) item.State = DownloadState.Removed;
            }
            if (ok && from != DownloadState.Removed) Raise(item, from, DownloadState.Removed);
            return ok;
        }

        // Swaps with the nearest neighbour of the same priority, so the list
        // order is the queue order within a priority. Persisted by list order.
        internal bool Move(DownloadItem item, int delta)
        {
            lock (_sync)
            {
                int at = _items.IndexOf(item);
                if (at < 0 || delta == 0) return false;
                int step = delta < 0 ? -1 : 1;
                for (int i = at + step; i >= 0 && i < _items.Count; i += step)
                {
                    if (_items[i].Priority != item.Priority) continue;
                    DownloadItem other = _items[i];
                    _items[i] = item;
                    _items[at] = other;
                    return true;
                }
                return false;
            }
        }

        internal int RemoveById(int id)
        {
            List<DownloadItem> removed = new List<DownloadItem>();
            List<DownloadState> was = new List<DownloadState>();
            lock (_sync)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Id != id) continue;
                    was.Add(_items[i].State);
                    _items[i].State = DownloadState.Removed;
                    removed.Add(_items[i]);
                }
                _items.RemoveAll(delegate(DownloadItem it) { return it.Id == id; });
            }
            for (int i = 0; i < removed.Count; i++)
                if (was[i] != DownloadState.Removed)
                    Raise(removed[i], was[i], DownloadState.Removed);
            return removed.Count;
        }

        // Bulk cleanup (history trimming) discards the rows; no events.
        internal int RemoveWhere(Predicate<DownloadItem> match)
        {
            lock (_sync) return _items.RemoveAll(match);
        }

        internal int CountWhere(Predicate<DownloadItem> match)
        {
            lock (_sync)
            {
                int n = 0;
                for (int i = 0; i < _items.Count; i++)
                    if (match(_items[i])) n++;
                return n;
            }
        }

        internal DownloadItem Find(int id)
        {
            lock (_sync)
            {
                for (int i = 0; i < _items.Count; i++)
                    if (_items[i].Id == id) return _items[i];
            }
            return null;
        }

        // Same normalized URL; an unfinished row wins over a completed one.
        internal DownloadItem FindByUrl(string url)
        {
            DownloadItem done = null;
            lock (_sync)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    DownloadItem it = _items[i];
                    if (it.State == DownloadState.Removed || !Util.SameUrl(it.Url, url)) continue;
                    if (it.State == DownloadState.Completed) { if (done == null) done = it; }
                    else return it;
                }
            }
            return done;
        }

        // A row that already owns the target file, whatever its URL.
        internal DownloadItem FindByPath(string path)
        {
            if (Util.IsBlank(path)) return null;
            lock (_sync)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (!Util.IsBlank(_items[i].LocalPath) &&
                        string.Equals(_items[i].LocalPath, path, StringComparison.OrdinalIgnoreCase))
                        return _items[i];
                }
            }
            return null;
        }
    }
}
