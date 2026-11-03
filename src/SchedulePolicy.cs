namespace ZID
{
    // Start/stop schedules in seconds since midnight; -1 means unset.
    internal static class SchedulePolicy
    {
        internal static int SecondsOfDay(System.DateTime now)
        {
            return now.Hour * 3600 + now.Minute * 60 + now.Second;
        }

        // True while new downloads should wait. One-shot: before the start time.
        // Daily: outside [start, stop), including a window that crosses midnight.
        internal static bool IsPending(int start, int stop, bool daily, int now)
        {
            if (start < 0) return false;
            if (!daily) return now < start;
            if (stop >= 0)
            {
                if (stop > start) return !(now >= start && now < stop);
                return !(now >= start || now < stop);
            }
            return now < start;
        }

        // True while a speed-limit window is active. A window that crosses
        // midnight is supported; equal start/end means all day.
        internal static bool InWindow(int start, int end, int now)
        {
            if (start < 0 || end < 0) return false;
            if (start == end) return true;
            if (start < end) return now >= start && now < end;
            return now >= start || now < end;
        }

        // Next occurrence of HH:MM; MinValue when the text is not a time.
        internal static System.DateTime NextAt(string hhmm, System.DateTime now)
        {
            int secs = Aria.ParseHhmm(hhmm);
            if (secs < 0) return System.DateTime.MinValue;
            System.DateTime at = now.Date.AddSeconds(secs);
            if (at <= now) at = at.AddDays(1);
            return at;
        }

        // True when an active stop time should pause downloads. A daily window
        // that crosses midnight is only reached after the stop and before start.
        internal static bool StopReached(int start, int stop, bool daily, int now)
        {
            if (stop < 0) return false;
            if (!daily || start < 0) return now >= stop;
            if (stop > start) return now >= stop;
            return now >= stop && now < start;
        }
    }
}
