using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Persistence;
using MountainPlanner.World;
using UnityEditor.Build;
using UnityEngine;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Builds the Jackson Hole demo into a player (task P2-03, G2/D1): after the player is built, the demo package is
    /// copied into its StreamingAssets/Demo/&lt;packageId&gt; with a terrain cache built for this game's version, so a fresh
    /// install opens the title over it in seconds, offline. It's never committed (LFS quota): the source is a local
    /// package, -demoPackage &lt;folder&gt; or else the newest Jackson Hole 5 km download in this PC's library. The package and
    /// its cache are staged once under Library/DemoBundle and reused while they're current.
    /// </summary>
    public static class DemoBundle
    {
        public const string StagingRoot = "Library/DemoBundle";
        public const string DemoName = "Jackson Hole";
        public const double DemoSizeMetres = 5000;

        /// <summary>
        /// The package to bundle: -demoPackage &lt;folder&gt;, else the library's newest Jackson Hole 5 km (D1). Null when
        /// there's none; <paramref name="why"/> then says what to do.
        /// </summary>
        public static string FindSource(string[] args, string dataRoot, out string why)
        {
            why = "";
            int i = Array.IndexOf(args, "-demoPackage");
            if (i >= 0 && i + 1 < args.Length)
            {
                string named = Path.GetFullPath(args[i + 1]);
                if (File.Exists(Path.Combine(named, ResortPackage.ManifestFile))) return named;
                why = $"-demoPackage {named} has no {ResortPackage.ManifestFile}.";
                return null;
            }
            var demo = ResortLibrary.Scan(dataRoot)
                                    .Where(e => e.OriginalName == DemoName && Math.Abs(e.SizeKm * 1000 - DemoSizeMetres) < 1)
                                    .OrderByDescending(e => e.CreatedUtc, StringComparer.Ordinal).FirstOrDefault();
            if (demo != null) return demo.Folder;
            why = $"No {DemoName} 5 km package in {ResortLibrary.ResortsFolder(dataRoot)}: run demo.bat 12, or pass -demoPackage <folder>.";
            return null;
        }

        /// <summary>
        /// A staged copy of <paramref name="source"/>: its manifest and layers (never its view.json, caches or anything
        /// else) and a cache built at <see cref="TerrainCache.Version"/>. Reused when it's already current. Throws
        /// <see cref="BuildFailedException"/> when the package is damaged or the cache doesn't come out current.
        /// </summary>
        public static string Stage(string source, string stagingRoot)
        {
            var problems = PackageValidator.Validate(source);
            if (problems.Count > 0) throw new BuildFailedException($"The demo package {source} can't be bundled: {string.Join(" ", problems)}");
            var manifest = ResortPackage.ReadManifest(source);
            if (manifest.Site.Name != DemoName || Math.Abs(manifest.Site.SizeMetres - DemoSizeMetres) > 1)
                Debug.LogWarning($"[DemoBundle] Bundling {manifest.Site.Name} ({manifest.Site.SizeMetres / 1000:0.#} km), not {DemoName} 5 km (D1).");

            string staged = Path.Combine(stagingRoot, manifest.PackageId);
            if (Directory.Exists(staged) && TerrainCache.IsCurrent(staged, manifest) && SameFiles(source, staged, manifest))
            {
                Debug.Log($"[DemoBundle] Reusing the staged demo {staged}");
                return staged;
            }
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            Directory.CreateDirectory(staged);
            foreach (string file in PackageFiles(manifest)) File.Copy(Path.Combine(source, file), Path.Combine(staged, file));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            TerrainCache.Build(staged, manifest, null, default, new BurstForestPlanter());
            File.Delete(Path.Combine(TerrainCache.FolderFor(staged), TerrainCache.LockFile));   // the build's own lease, released
            var cache = TerrainCache.ReadManifest(staged);
            if (cache.CacheVersion != TerrainCache.Version || !TerrainCache.IsCurrent(staged, manifest))
                throw new BuildFailedException($"The demo's cache came out at v{cache.CacheVersion}, not this game's v{TerrainCache.Version}.");
            Debug.Log($"[DemoBundle] Built the demo's cache v{cache.CacheVersion} ({cache.Tiles.Count} tiles) in {clock.Elapsed.TotalSeconds:F0} s");
            return staged;
        }

        /// <summary>The package's own files: the manifest and every layer it lists.</summary>
        static IEnumerable<string> PackageFiles(PackageManifest manifest) =>
            new[] { ResortPackage.ManifestFile }.Concat(manifest.Layers.Select(l => l.File)).Distinct(StringComparer.OrdinalIgnoreCase);

        static bool SameFiles(string source, string staged, PackageManifest manifest) =>
            PackageFiles(manifest).All(f => File.Exists(Path.Combine(staged, f)) && new FileInfo(Path.Combine(staged, f)).Length == new FileInfo(Path.Combine(source, f)).Length)
            && File.ReadAllText(Path.Combine(source, ResortPackage.ManifestFile)) == File.ReadAllText(Path.Combine(staged, ResortPackage.ManifestFile));

        /// <summary>The player's StreamingAssets folder (&lt;exe name&gt;_Data/StreamingAssets).</summary>
        public static string StreamingAssetsOf(string playerExe) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(playerExe)), Path.GetFileNameWithoutExtension(playerExe) + "_Data", "StreamingAssets");

        /// <summary>Copies a staged demo into the player, replacing any demo there. Returns the bundled folder.</summary>
        public static string CopyInto(string playerExe, string staged)
        {
            string demoRoot = Path.Combine(StreamingAssetsOf(playerExe), BundledAreas.FolderName);
            if (Directory.Exists(demoRoot)) Directory.Delete(demoRoot, true);
            string target = Path.Combine(demoRoot, Path.GetFileName(staged.TrimEnd('\\', '/')));
            CopyTree(staged, target);
            return target;
        }

        static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
                if (Path.GetFileName(file) != TerrainCache.LockFile) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (string dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        /// <summary>
        /// The build step: bundles the demo into <paramref name="playerExe"/>. Without a source package it warns and
        /// builds without one (owner, task P2-03): the game then falls back to the library's Jackson Hole.
        /// </summary>
        public static void AddTo(string playerExe, string[] args, string dataRoot)
        {
            string source = FindSource(args, dataRoot, out string why);
            if (source == null)
            {
                Debug.LogWarning($"[DemoBundle] ********** NO DEMO IN THIS BUILD ********** {why} A fresh install will open on an empty title.");
                return;
            }
            string staged = Stage(source, StagingRoot);
            string bundled = CopyInto(playerExe, staged);
            long bytes = Directory.GetFiles(bundled, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Debug.Log($"[DemoBundle] Bundled {source} → {bundled} ({bytes / 1e6:F0} MB)");
        }
    }
}
