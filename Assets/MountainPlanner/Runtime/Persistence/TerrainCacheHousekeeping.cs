using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// Which cache versions a package keeps, and what may delete them (task P2-04). Games built from branches on
    /// different cache versions share one library, so a build never deletes a newer version's cache, keeps the newest
    /// older one, and never deletes a cache that's open in any game (<see cref="Hold"/>).
    /// </summary>
    public static partial class TerrainCache
    {
        /// <summary>The file an open cache holds (<see cref="Hold"/>); while any game holds it, nothing deletes that cache.</summary>
        public const string LockFile = "in-use.lock";

        const string FolderPrefix = "cache-v";

        public static string FolderFor(string packageFolder, int version) =>
            Path.Combine(packageFolder, FolderPrefix + version.ToString(CultureInfo.InvariantCulture));

        /// <summary>The cache version a folder name stands for ("cache-v12" is 12), or false for any other name.</summary>
        public static bool TryParseFolder(string name, out int version)
        {
            version = 0;
            if (name == null || !name.StartsWith(FolderPrefix, StringComparison.Ordinal) || name.Length == FolderPrefix.Length) return false;
            for (int i = FolderPrefix.Length; i < name.Length; i++)
                if (name[i] < '0' || name[i] > '9') return false;
            return int.TryParse(name.Substring(FolderPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out version);
        }

        /// <summary>The cache versions a package folder has, oldest first.</summary>
        public static List<int> Versions(string packageFolder)
        {
            var versions = new List<int>();
            if (!Directory.Exists(packageFolder)) return versions;
            foreach (string folder in Directory.GetDirectories(packageFolder, FolderPrefix + "*"))
                if (TryParseFolder(Path.GetFileName(folder), out int v)) versions.Add(v);
            versions.Sort();
            return versions;
        }

        /// <summary>
        /// Marks a package's cache as open until the lease is disposed: the game holds it for as long as the area is
        /// on screen, and <see cref="Build"/> while it writes. Any number of games can hold the same cache. It's a
        /// file opened for reading that others may read but not delete, so it also protects the folder from another
        /// game, and a crash lets go of it with the process.
        /// </summary>
        public static IDisposable Hold(string packageFolder, int version = Version)
        {
            string folder = FolderFor(packageFolder, version);
            string path = Path.Combine(folder, LockFile);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
                }
                // Another game is checking the lock this instant (InUse opens it alone for a moment), or is just
                // removing the folder; try again briefly.
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 20) { Thread.Sleep(5); }
            }
        }

        /// <summary>True while any game holds this cache folder (<see cref="Hold"/>).</summary>
        public static bool InUse(string cacheFolder)
        {
            string path = Path.Combine(cacheFolder, LockFile);
            if (!File.Exists(path)) return false;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        /// <summary>
        /// After a build at <paramref name="own"/>: deletes the older versions except the newest of them. Newer
        /// versions belong to newer games and stay; a cache that's open stays. Returns the versions deleted.
        /// </summary>
        public static List<int> Prune(string packageFolder, int own = Version)
        {
            SafeFolder.SweepTrash(packageFolder);
            var removed = new List<int>();
            var older = Versions(packageFolder).FindAll(v => v < own);
            for (int i = 0; i < older.Count - 1; i++)   // the last (newest) older one stays
                if (TryDelete(packageFolder, older[i])) removed.Add(older[i]);
            return removed;
        }

        /// <summary>
        /// "Free space" (task P2-04): deletes every cache older than <paramref name="current"/> that isn't open. The
        /// package and its current and newer caches stay, so nothing has to be downloaded or rebuilt. Returns the bytes
        /// freed, counted before each folder goes.
        /// </summary>
        public static long FreeOlderVersions(string packageFolder, int current = Version)
        {
            long freed = SafeFolder.SweepTrash(packageFolder);
            foreach (int v in Versions(packageFolder))
            {
                if (v >= current) continue;
                long bytes = SafeFolder.Bytes(FolderFor(packageFolder, v));
                if (TryDelete(packageFolder, v)) freed += bytes;
            }
            return freed;
        }

        static bool TryDelete(string packageFolder, int version)
        {
            string folder = FolderFor(packageFolder, version);
            return !InUse(folder) && SafeFolder.TryDelete(folder);
        }
    }

    /// <summary>
    /// Deleting a folder all or nothing: it's renamed out of the way first (which Windows refuses while a game has a
    /// file in it open), then deleted. A delete cut short leaves a ".trash-" folder that no listing reads, and the next
    /// clean-up sweeps it.
    /// </summary>
    static class SafeFolder
    {
        public const string TrashPrefix = ".trash-";

        public static bool IsTrash(string folder) => Path.GetFileName(folder).StartsWith(TrashPrefix, StringComparison.Ordinal);

        /// <summary>False, with the folder untouched, when something in it is open.</summary>
        public static bool TryDelete(string folder)
        {
            if (!Directory.Exists(folder)) return true;
            string parent = Path.GetDirectoryName(Path.GetFullPath(folder).TrimEnd('\\', '/'));
            string name = Path.GetFileName(Path.GetFullPath(folder).TrimEnd('\\', '/'));
            string trash = null;
            for (int n = 0; trash == null || Directory.Exists(trash) || File.Exists(trash); n++)
                trash = Path.Combine(parent, TrashPrefix + name + "-" + n.ToString(CultureInfo.InvariantCulture));
            try { Directory.Move(folder, trash); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
            DeleteQuietly(trash);
            return true;
        }

        /// <summary>Deletes ".trash-" folders left by an interrupted delete; returns the bytes they held.</summary>
        public static long SweepTrash(string parent)
        {
            if (!Directory.Exists(parent)) return 0;
            long freed = 0;
            foreach (string folder in Directory.GetDirectories(parent, TrashPrefix + "*"))
            {
                long bytes = Bytes(folder);
                if (DeleteQuietly(folder)) freed += bytes;
            }
            return freed;
        }

        static bool DeleteQuietly(string folder)
        {
            try
            {
                Directory.Delete(folder, true);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>The bytes of every file under a folder; files that vanish while it counts are skipped.</summary>
        public static long Bytes(string folder)
        {
            if (!Directory.Exists(folder)) return 0;
            long total = 0;
            string[] files;
            try { files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return 0; }
            foreach (string file in files)
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            return total;
        }
    }
}
