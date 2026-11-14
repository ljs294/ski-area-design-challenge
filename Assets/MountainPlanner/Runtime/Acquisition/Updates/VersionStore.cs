#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MountainPlanner.Acquisition.Updates
{
    /// <summary>
    /// The launcher's install folder (task 4.23, UP2): <c>&lt;root&gt;\Launcher.exe</c> and one folder per version,
    /// <c>&lt;root&gt;\versions\0.3.2\</c>. A version counts only once its folder holds the empty <see cref="Marker"/>, which
    /// is written last, so a half-unzipped folder is never played. The newest complete version plays; the one before it is
    /// kept to roll back to. There is no state file: the folders are the state (0.0 cleared, 2026-11-13).
    /// Saves, areas and settings never live here (they're in %LOCALAPPDATA%\SkiAreaDesignChallenge and the registry).
    /// </summary>
    public sealed class VersionStore
    {
        public const string Marker = ".complete";
        public const string PartialSuffix = ".partial";
        public const string GameExe = ReleaseInfo.ProductFile + ".exe";
        public const string LauncherExe = "Launcher.exe";

        public readonly string Root;

        public VersionStore(string root) { Root = Path.GetFullPath(root); }

        /// <summary>The default per-user install root: %LOCALAPPDATA%\Programs\SkiAreaDesignChallenge (no admin).</summary>
        public static string DefaultRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ReleaseInfo.ProductFile);

        public string Versions => Path.Combine(Root, "versions");
        public string Downloads => Path.Combine(Root, "downloads");
        public string Launcher => Path.Combine(Root, LauncherExe);

        public string FolderOf(GameVersion v) => Path.Combine(Versions, v.Number);
        public string ExeOf(GameVersion v) => Path.Combine(FolderOf(v), GameExe);

        /// <summary>Complete versions, newest first.</summary>
        public IReadOnlyList<GameVersion> Installed()
        {
            if (!Directory.Exists(Versions)) return Array.Empty<GameVersion>();
            var found = new List<GameVersion>();
            foreach (string dir in Directory.GetDirectories(Versions))
            {
                string name = Path.GetFileName(dir);
                if (!GameVersion.TryParse(name, out var v) || v.Build.Length > 0 || v.Number != name) continue;
                if (File.Exists(Path.Combine(dir, Marker)) && File.Exists(Path.Combine(dir, GameExe))) found.Add(v);
            }
            found.Sort((a, b) => b.CompareTo(a));
            return found;
        }

        public GameVersion? Current { get { var all = Installed(); return all.Count > 0 ? all[0] : (GameVersion?)null; } }
        public GameVersion? Previous { get { var all = Installed(); return all.Count > 1 ? all[1] : (GameVersion?)null; } }

        /// <summary>
        /// Deletes every version folder but two complete ones (half-unzipped <c>.partial</c> folders included) and every
        /// finished zip in downloads. The two are the newest and the one before it, or the newest and
        /// <paramref name="picked"/> when the player just installed an older release from the version list (R4), so that
        /// one isn't deleted the moment it's in. A <c>.partial</c> download stays, so the next try resumes it. Returns
        /// what it couldn't delete (a running older game, say).
        /// </summary>
        public IReadOnlyList<string> Prune(GameVersion? picked = null)
        {
            var problems = new List<string>();
            var installed = Installed();
            var kept = installed.Take(2).ToList();
            if (picked is GameVersion p && installed.Contains(p) && !kept.Contains(p)) kept = new List<GameVersion> { installed[0], p };
            var keep = new HashSet<string>(kept.Select(v => v.Number), StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(Versions))
                foreach (string dir in Directory.GetDirectories(Versions))
                    if (!keep.Contains(Path.GetFileName(dir))) TryDelete(dir, problems);
            if (Directory.Exists(Downloads))
                foreach (string file in Directory.GetFiles(Downloads))
                    if (!file.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)) TryDelete(file, problems);
            return problems;
        }

        /// <summary>True when <paramref name="path"/> is this folder or inside it (a library folder there would be lost).</summary>
        public static bool IsInside(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
            string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The install root a game runs from, when it was installed by the launcher: <c>&lt;root&gt;\versions\&lt;v&gt;\</c> →
        /// <c>&lt;root&gt;</c>, if <c>&lt;root&gt;\Launcher.exe</c> exists. Null for a game unzipped by hand.
        /// </summary>
        public static string? RootOfGame(string gameFolder)
        {
            try
            {
                var version = new DirectoryInfo(Path.GetFullPath(gameFolder));
                var versions = version.Parent;
                var root = versions?.Parent;
                if (root == null || !string.Equals(versions!.Name, "versions", StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(Path.Combine(root.FullName, LauncherExe)) ? root.FullName : null;
            }
            catch (Exception) { return null; }
        }

        static void TryDelete(string path, List<string> problems)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                problems.Add($"{path}: {e.Message}");
            }
        }
    }
}
