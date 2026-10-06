using System;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Which commit a player was built from: the Windows build (ViewerSetup) writes Resources/BuildInfo.json (git SHA,
    /// whether the working tree had uncommitted changes, when it was built) into a git-ignored folder, so benchmark
    /// results carry the commit they measured (0.3 §8). Editor runs and players built without it read "unknown".
    /// </summary>
    [Serializable]
    public sealed class BuildInfo
    {
        public const string ResourceName = "BuildInfo";

        public string commit = "unknown";
        public bool dirty;
        public string builtUtc = "";

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
    }
}
