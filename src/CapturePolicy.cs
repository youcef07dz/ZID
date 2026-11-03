using System;

namespace ZID
{
    // Decides whether a clipboard URL is worth a new download. The clipboard
    // chain can fire twice for one copy, and copying a URL from ZID itself must
    // never capture it again.
    internal sealed class CapturePolicy
    {
        internal const int MaxUrlLength = 2048;
        internal const int DebounceMs = 2000;

        private string _lastText = "";
        private int _lastAt;
        private string _selfCopied = "";

        // Called when ZID puts a URL on the clipboard; the next clipboard event
        // with that same URL is swallowed once.
        internal void NoteSelfCopy(string url)
        {
            _selfCopied = Util.IsBlank(url) ? "" : url.Trim();
        }

        internal bool Accept(string text, int nowMs)
        {
            if (Util.IsBlank(text)) return false;
            text = text.Trim();
            if (text.Length == 0 || text.Length > MaxUrlLength) return false;
            if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0) return false;
            if (!IsSupportedUrl(text)) return false;

            if (_selfCopied.Length > 0)
            {
                bool same = string.Equals(text, _selfCopied, StringComparison.OrdinalIgnoreCase);
                _selfCopied = "";
                if (same) return false;
            }

            if (string.Equals(text, _lastText, StringComparison.Ordinal) &&
                unchecked(nowMs - _lastAt) < DebounceMs) return false;

            _lastText = text;
            _lastAt = nowMs;
            return true;
        }

        internal static bool IsSupportedUrl(string text)
        {
            return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                   text.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
        }
    }
}
