using System;
using System.IO;
using MountainPlanner.Persistence;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Where this player's built-in areas are (task P2-03): StreamingAssets/Demo, which the build step fills with the
    /// Jackson Hole demo and its prebuilt cache (Editor/DemoBundle). -demo &lt;folder&gt; points elsewhere (demos, the
    /// editor); tests set <see cref="Override"/>. In the editor there's normally none, and the library's Jackson Hole
    /// stands in, as before.
    /// </summary>
    public static class BundledDemo
    {
        /// <summary>Set by tests: the folder to use instead (an empty string means none).</summary>
        public static string Override;

        public static string Root
        {
            get
            {
                if (Override != null) return Override.Length > 0 ? Override : null;
                string[] args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-demo");
                if (i >= 0 && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
                return Path.Combine(Application.streamingAssetsPath, BundledAreas.FolderName);
            }
        }

        /// <summary>True for a package folder that's built into the game: it's opened read-only.</summary>
        public static bool Contains(string folder) => BundledAreas.Contains(Root, folder);
    }
}
