namespace ZID
{
    // Engine statuses that only change row state during the RPC loop. Complete
    // and error handling stay in the worker because they touch files/retries.
    internal static class WorkerState
    {
        // Returns true when the worker loop should stop.
        internal static bool Apply(DownloadItem item, DownloadManager downloads,
                                   EngineTaskStatus status)
        {
            if (status == EngineTaskStatus.Paused)
            {
                downloads.Pause(item);
                item.Eta = "Paused";
                item.Speed = 0;
                item.ActiveConnections = 0;
            }
            else if (status == EngineTaskStatus.Waiting)
            {
                downloads.Queue(item);
                item.Eta = "Queued";
            }
            else if (status == EngineTaskStatus.Removed)
            {
                downloads.Pause(item);
                item.Eta = "Paused";
                item.EngineId = null;
                return true;
            }
            return false;
        }
    }
}
