using System;
using System.Collections.Generic;
using System.IO;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// Areas built into the game (task P2-03, G2/D1): the Jackson Hole demo, which the build step copies with a prebuilt
    /// terrain cache into the player's StreamingAssets/Demo/&lt;packageId&gt;. They're read in place and never written:
    /// no cache lease, no view.json, no rename, delete or Free space, so the game may sit in a read-only folder. The
    /// folder layout is the library's own (package folders with a manifest), so nothing on disk changes format.
    /// </summary>
    public static class BundledAreas
    {
        /// <summary>The folder under StreamingAssets that holds them.</summary>
        public const string FolderName = "Demo";

        public const string Refusal = "Built into the game: it can't be renamed or deleted.";
        public const string StaleCache = "The built-in demo doesn't match this version of the game. Reinstall the game to open it.";

        /// <summary>
        /// The bundled areas under <paramref name="root"/> (none when it's null or missing). Each is marked
        /// <see cref="LibraryEntry.Bundled"/>; one without a current terrain cache can't be built here, so it's listed
        /// but refused.
        /// </summary>
        public static List<LibraryEntry> Scan(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return new List<LibraryEntry>();
            var entries = ResortLibrary.ScanFolder(root, null);
            foreach (var e in entries)
            {
                e.Bundled = true;
                e.RenameRefusal = Refusal;
                if (!e.CacheReady) e.Refusal = StaleCache;
            }
            return entries;
        }

        /// <summary>True when <paramref name="folder"/> is inside <paramref name="root"/> (a bundled package or one of its files).</summary>
        public static bool Contains(string root, string folder)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(folder)) return false;
            string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return f.StartsWith(r, StringComparison.OrdinalIgnoreCase) && f.Length > r.Length;
        }
    }
}
