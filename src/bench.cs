using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ZID
{
    // Headless core benchmark for the T5.4 row counts. The UI (idle CPU/RAM,
    // IE8 row updates) is not measured here; that stays a manual run.
    internal static class Benchmark
    {
        internal static void Run()
        {
            Console.WriteLine("ZID core benchmark (Release, .NET 2.0)");
            Console.WriteLine("rows  add(ms)  snap(us)  queue(ms)  save+load(ms)  heap(KB)");
            int[] counts = new int[] { 1, 10, 50, 100 };
            for (int i = 0; i < counts.Length; i++) RunOne(counts[i]);
        }

        private static void RunOne(int rows)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long baseBytes = GC.GetTotalMemory(true);

            DownloadManager mgr = new DownloadManager();
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < rows; i++)
            {
                DownloadItem it = new DownloadItem();
                it.Id = i + 1;
                it.Url = "https://example.com/file" + i + ".bin";
                it.FileName = "file" + i + ".bin";
                it.LocalPath = Path.Combine(Path.GetTempPath(), "zid_bench_" + i + ".bin");
                it.State = DownloadState.Queued;
                it.Status = "Queued";
                it.Size = 4L * 1024 * 1024;
                mgr.Add(it);
            }
            double addMs = sw.Elapsed.TotalMilliseconds;
            long usedKb = (GC.GetTotalMemory(true) - baseBytes) / 1024;

            int snapRuns = 200;
            sw = Stopwatch.StartNew();
            for (int i = 0; i < snapRuns; i++) mgr.Snapshot();
            double snapUs = sw.Elapsed.TotalMilliseconds * 1000.0 / snapRuns;

            QueueManager queue = new QueueManager();
            queue.MaxConcurrent = rows;
            DownloadItem claimed;
            sw = Stopwatch.StartNew();
            for (int i = 0; i < rows; i++)
            {
                queue.TryClaimNext(mgr.Snapshot(), false, System.DateTime.Now, out claimed);
                // Benchmark only: re-queue so every claim scans the full set.
                if (claimed != null) claimed.State = DownloadState.Queued;
            }
            double queueMs = sw.Elapsed.TotalMilliseconds;

            string path = Path.Combine(Path.GetTempPath(), "zid_bench_state.json");
            StateStore store = new StateStore(path);
            StateLog log = delegate(string message) { };
            int saveRuns = 5;
            sw = Stopwatch.StartNew();
            for (int i = 0; i < saveRuns; i++)
            {
                store.Save(mgr.Snapshot(), rows + 1, log);
                store.Load(log);
            }
            double saveMs = sw.Elapsed.TotalMilliseconds / saveRuns;
            try { File.Delete(path); } catch { }

            Console.WriteLine(string.Format("{0,4}  {1,7}  {2,8}  {3,9}  {4,13}  {5,8}",
                rows, Fmt(addMs), Fmt(snapUs), Fmt(queueMs), Fmt(saveMs), usedKb));
        }

        private static string Fmt(double value)
        {
            return value.ToString("0.00");
        }
    }
}
