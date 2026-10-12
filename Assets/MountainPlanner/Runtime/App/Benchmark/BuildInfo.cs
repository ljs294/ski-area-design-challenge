using System;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Which commit and version a player was built from: the Windows build (ViewerSetup) writes Resources/BuildInfo.json
    /// (git SHA, whether the working tree had uncommitted changes, when it was built, and the version from the release
    /// tag, task P2-10) into a git-ignored folder, so benchmark results carry the commit they measured (0.3 §8) and the
    /// title, Player.log and error logs carry the version. Editor runs and players built without it read "unknown".
    /// </summary>
    [Serializable]
    public sealed class BuildInfo
    {
        public const string ResourceName = "BuildInfo";

        public string commit = "unknown";
        public bool dirty;
        public string builtUtc = "";
        /// <summary>"0.2.0" for a build of the v0.2.0 tag, "0.2.0+dev.7.abc1234" in between (Persistence.ReleaseVersion).</summary>
        public string version = "";

        static BuildInfo _current;

        public static BuildInfo Current
        {
            get
            {
                if (_current != null) return _current;
                var text = Resources.Load<TextAsset>(ResourceName);
                _current = text != null ? JsonUtility.FromJson<BuildInfo>(text.text) : new BuildInfo();
                return _current;
            }
        }

        /// <summary>The version as players see it: "v0.2.0", or "Development build" where none was stamped (the editor).</summary>
        public string DisplayVersion => string.IsNullOrEmpty(version) ? "Development build" : "v" + version;

        /// <summary>
        /// The title's short form, clear of the signpost's left post: "v0.2.0" for a release, "v0.2.0 preview" between
        /// tags (the full version is in Credits and the logs).
        /// </summary>
        public string TitleVersion
        {
            get
            {
                if (string.IsNullOrEmpty(version)) return DisplayVersion;
                int plus = version.IndexOf('+');
                return plus < 0 ? "v" + version : "v" + version.Substring(0, plus) + " preview";
            }
        }

        /// <summary>The first commit characters, for log lines.</summary>
        public string ShortCommit => commit != null && commit.Length > 7 ? commit.Substring(0, 7) : commit ?? "unknown";
    }
}
