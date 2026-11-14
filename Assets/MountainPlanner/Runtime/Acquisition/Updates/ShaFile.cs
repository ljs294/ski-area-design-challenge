#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MountainPlanner.Acquisition.Updates
{
    /// <summary>
    /// The <c>.sha256</c> file release.ps1 writes beside every zip (task P2-10): one line, "&lt;64 hex&gt;  &lt;zip name&gt;",
    /// the format <c>sha256sum</c> reads. The updater checks a downloaded zip against it before unzipping (task 4.23, UP5).
    /// </summary>
    public static class ShaFile
    {
        /// <summary>The lower-case hash for <paramref name="expectedName"/>. Throws <see cref="FormatException"/> on a bad file.</summary>
        public static string Parse(string text, string expectedName)
        {
            string line = (text ?? "").Trim().TrimStart('﻿');
            int nl = line.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) line = line.Substring(0, nl);
            int space = line.IndexOf(' ');
            string hash = (space < 0 ? line : line.Substring(0, space)).ToLowerInvariant();
            string name = space < 0 ? "" : line.Substring(space).Trim().TrimStart('*');
            if (!IsHex64(hash)) throw new FormatException("The .sha256 file doesn't start with a SHA-256 hash.");
            if (name.Length > 0 && !string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"The .sha256 file is for {name}, not {expectedName}.");
            return hash;
        }

        public static bool IsHex64(string s)
        {
            if (s == null || s.Length != 64) return false;
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>The file's SHA-256, lower-case hex.</summary>
        public static string Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return Hex(sha.ComputeHash(stream));
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
