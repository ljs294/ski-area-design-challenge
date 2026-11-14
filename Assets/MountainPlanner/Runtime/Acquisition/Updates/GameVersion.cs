#nullable enable
using System;
using System.Globalization;
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition.Updates
{
    /// <summary>
    /// A release number, 0.3.2, for comparing what is installed with GitHub's latest release (task 4.23, UP4). Reads a
    /// tag ("v0.3.2"), a folder name ("0.3.2") or a build's stamped version; a preview's "+dev…" build metadata is kept
    /// in <see cref="Build"/>, so <see cref="IsPreview"/> can stop previews from ever being offered an update.
    /// Engine-free: the launcher (tools/launcher) compiles this file too.
    /// </summary>
    public readonly struct GameVersion : IComparable<GameVersion>, IEquatable<GameVersion>
    {
        public readonly int Major, Minor, Patch;
        /// <summary>"dev.7.abc1234" for a preview, "" for a release.</summary>
        public readonly string Build;

        public GameVersion(int major, int minor, int patch, string build = "")
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Build = build ?? "";
        }

        /// <summary>True between tags (Persistence.ReleaseVersion: anything with "+", or 0.0.0).</summary>
        public bool IsPreview => !ReleaseVersion.IsRelease(ToString());

        /// <summary>"0.3.2", or "0.3.2+dev.7.abc1234".</summary>
        public string Number => $"{Major}.{Minor}.{Patch}";

        public override string ToString() => Build.Length == 0 ? Number : Number + "+" + Build;

        /// <summary>The release tag, "v0.3.2".</summary>
        public string Tag => "v" + Number;

        /// <summary>Reads "v0.3.2", "0.3.2" or "0.3.2+dev.7.abc1234"; false for anything else (v0.1, v0.2.0-alpha).</summary>
        public static bool TryParse(string? text, out GameVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text!.Trim();
            if (s.StartsWith("v", StringComparison.Ordinal)) s = s.Substring(1);
            string build = "";
            int plus = s.IndexOf('+');
            if (plus >= 0)
            {
                build = s.Substring(plus + 1);
                s = s.Substring(0, plus);
                if (build.Length == 0) return false;
            }
            string[] parts = s.Split('.');
            if (parts.Length != 3) return false;
            var numbers = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (parts[i].Length == 0 || parts[i].Length > 9) return false;
                foreach (char c in parts[i]) if (c < '0' || c > '9') return false;
                numbers[i] = int.Parse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture);
            }
            version = new GameVersion(numbers[0], numbers[1], numbers[2], build);
            return true;
        }

        public static GameVersion Parse(string text) =>
            TryParse(text, out var v) ? v : throw new FormatException($"'{text}' isn't a version such as 0.3.2.");

        /// <summary>By the three numbers only: a preview of 0.3.2 compares equal to 0.3.2.</summary>
        public int CompareTo(GameVersion other)
        {
            if (Major != other.Major) return Major.CompareTo(other.Major);
            if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
            return Patch.CompareTo(other.Patch);
        }

        public bool Equals(GameVersion other) => CompareTo(other) == 0 && Build == other.Build;
        public override bool Equals(object? obj) => obj is GameVersion v && Equals(v);
        public override int GetHashCode() => (Major * 1000 + Minor) * 1000 + Patch;
        public static bool operator >(GameVersion a, GameVersion b) => a.CompareTo(b) > 0;
        public static bool operator <(GameVersion a, GameVersion b) => a.CompareTo(b) < 0;
    }
}
