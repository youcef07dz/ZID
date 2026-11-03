using System;

namespace ZID
{
    // RPC progress arithmetic: size, byte count, speed, connections and ETA.
    // The audio-pair merge display stays in the form.
    internal static class ProgressPolicy
    {
        internal static void Update(DownloadItem item, long total, long done,
                                    long speed, int connections)
        {
            if (total > 0) item.Size = total;
            item.BytesReceived = done;
            item.Speed = speed;
            item.ActiveConnections = connections;
            if (item.Size > 0) item.Progress = (int)(done * 100 / item.Size);
            if (item.Speed > 0 && item.Progress < 100 && item.Size > 0)
            {
                long eta = (item.Size - done) / item.Speed;
                if (eta < 0) eta = 0;
                item.Eta = Util.FormatEta(eta);
            }
        }
    }
}
