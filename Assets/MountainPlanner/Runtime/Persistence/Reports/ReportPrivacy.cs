#nullable enable
using System;
using System.Text;

namespace MountainPlanner.Persistence.Reports
{
    /// <summary>
    /// Takes the player's Windows user name and computer name out of every text that goes into a report (decision PR6):
    /// paths like C:\Users\pat\AppData read C:\Users\%USERNAME%\AppData. Whole words only, any case, so a name that is
    /// also part of a longer word ("pat" in "path") stays. Names shorter than two letters are left alone.
    /// </summary>
    public sealed class ReportPrivacy
    {
        public const string UserToken = "%USERNAME%";
        public const string ComputerToken = "%COMPUTERNAME%";

        readonly string _user, _computer;

        public ReportPrivacy(string? userName, string? computerName)
        {
            _user = (userName ?? "").Trim();
            _computer = (computerName ?? "").Trim();
        }

        /// <summary>Nothing to hide (tests, or a player whose names we couldn't read).</summary>
        public static readonly ReportPrivacy None = new ReportPrivacy("", "");

        public string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            text = Replace(text, _user, UserToken);
            return Replace(text, _computer, ComputerToken);
        }

        /// <summary>True when <paramref name="text"/> still holds either name as a whole word (tests and the writer's check).</summary>
        public bool Leaks(string text) => Find(text, _user, 0) >= 0 || Find(text, _computer, 0) >= 0;

        static string Replace(string text, string name, string token)
        {
            if (name.Length < 2) return text;
            int at = Find(text, name, 0);
            if (at < 0) return text;
            var sb = new StringBuilder(text.Length);
            int from = 0;
            while (at >= 0)
            {
                sb.Append(text, from, at - from).Append(token);
                from = at + name.Length;
                at = Find(text, name, from);
            }
            return sb.Append(text, from, text.Length - from).ToString();
        }

        static int Find(string text, string name, int start)
        {
            if (name.Length < 2 || string.IsNullOrEmpty(text)) return -1;
            for (int at = text.IndexOf(name, start, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = at + 1 < text.Length ? text.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
            {
                bool before = at == 0 || !IsWordChar(text[at - 1]);
                bool after = at + name.Length >= text.Length || !IsWordChar(text[at + name.Length]);
                if (before && after) return at;
            }
            return -1;
        }

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
