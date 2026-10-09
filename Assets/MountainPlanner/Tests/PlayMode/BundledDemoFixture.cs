using System;
using System.IO;
using System.Linq;
using MountainPlanner.Persistence;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P2-03: a stand-in for the demo built into a player. The committed Jackson Hole 2 km test terrain is copied
    /// into &lt;scratch&gt;/Demo/&lt;id&gt; with a cache built as the build step builds one, then every file is made read-only,
    /// as in an installed game. <see cref="Snapshot"/> proves nothing was written there.
    /// </summary>
    public sealed class BundledDemoFixture : IDisposable
    {
        public readonly string Scratch, Root, Package;

        public BundledDemoFixture()
        {
            string source = Path.Combine(Path.GetDirectoryName(Application.dataPath), "TestData", "jackson-hole-2km");
            if (!Directory.Exists(source) || new FileInfo(Path.Combine(source, "heights-core.grid")).Length < 1000)
                Assert.Ignore("Git LFS hasn't fetched TestData/jackson-hole-2km.");
            Scratch = Path.Combine(Path.GetTempPath(), "mp-demo-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(Scratch, "StreamingAssets", BundledAreas.FolderName);
            var m = ResortPackage.ReadManifest(source);
            Package = Path.Combine(Root, m.PackageId);
            Directory.CreateDirectory(Package);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(Package, Path.GetFileName(f)));
            TerrainCache.Build(Package, m, null, default, new BurstForestPlanter());
            File.Delete(Path.Combine(TerrainCache.FolderFor(Package), TerrainCache.LockFile));
            foreach (string f in Directory.GetFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.ReadOnly);
        }

        /// <summary>Every file and folder under the demo with its size and write time.</summary>
        public string Snapshot() =>
            string.Join("\n", Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
                                       .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir")));

        public void Dispose()
        {
            if (!Directory.Exists(Scratch)) return;
            foreach (string f in Directory.GetFiles(Scratch, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Scratch, true);
        }
    }
}
