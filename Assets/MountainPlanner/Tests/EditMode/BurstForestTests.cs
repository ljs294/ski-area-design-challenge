using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.World;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The game grows forests with Burst; the downloader and CI grow them as plain C#. Both must give the
    /// same bytes, or a cache built by one would differ from one built by the other (0.3 §7).
    /// </summary>
    public sealed class BurstForestTests
    {
        static string JacksonHole2Km()
        {
            for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "TestData", "jackson-hole-2km");
                if (Directory.Exists(candidate))
                {
                    var canopy = new FileInfo(Path.Combine(candidate, "canopy-core.grid"));
                    if (canopy.Length < 200) Assert.Ignore("The test terrain is a Git LFS pointer; run `git lfs pull`.");
                    return candidate;
                }
            }
            Assert.Ignore("TestData/jackson-hole-2km is not available here.");
            return null;
        }

        [Test]
        public void BurstAndPlainCSharpGrowTheSameForest()
        {
            Assert.That(BurstForestPlanter.IsBurstCompiled(), Is.True, "the planter runs Burst-compiled (Burst enabled, synchronous compilation)");
            string dir = JacksonHole2Km();
            var manifest = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0));

            var field = new ForestField(manifest, dir);
            var managed = field.Prepare(tiles);
            var clock = Stopwatch.StartNew();
            new ManagedForestPlanter().Plant(managed);
            double managedSeconds = clock.Elapsed.TotalSeconds;

            var burst = field.Prepare(tiles);
            new BurstForestPlanter().Plant(burst);   // warm-up: the first call compiles
            burst = field.Prepare(tiles);
            clock.Restart();
            new BurstForestPlanter().Plant(burst);
            double burstSeconds = clock.Elapsed.TotalSeconds;

            UnityEngine.Debug.Log($"[BurstForestTests] {managed.TileCount.Sum():N0} trees: plain C# {managedSeconds * 1000:F0} ms, Burst {burstSeconds * 1000:F0} ms");
            Assert.That(burst.TileCount, Is.EqualTo(managed.TileCount), "same trees per tile");
            var a = ForestField.Split(managed, tiles);
            var b = ForestField.Split(burst, tiles);
            for (int n = 0; n < a.Length; n++)
                Assert.That(GridFile.HashValues(ForestField.Encode(b[n], TileGrid.TileMetres)),
                            Is.EqualTo(GridFile.HashValues(ForestField.Encode(a[n], TileGrid.TileMetres))), $"terrain tile {n} identical");

            // The krummholz path too, forced on every core cell with a stand-in model.
            ForestPlan Forced()
            {
                var plan = field.Prepare(tiles);
                plan.KrummholzModel = 0;
                for (int i = 0; i < plan.Cells.Length; i++)
                    if (plan.Cells[i].Kind == MountainPlanner.Domain.Cover.ForestCell.Core) plan.Cells[i].Krummholz = 200;
                return plan;
            }
            var km = Forced();
            new ManagedForestPlanter().Plant(km);
            var kb = Forced();
            new BurstForestPlanter().Plant(kb);
            Assert.That(kb.TileCount, Is.EqualTo(km.TileCount), "krummholz: same trees per tile");
            Assert.That(kb.Points.Select(p => (p.X, p.Y, p.HeightCode, p.Prototype, p.Rotation, p.Width32)),
                        Is.EqualTo(km.Points.Select(p => (p.X, p.Y, p.HeightCode, p.Prototype, p.Rotation, p.Width32))), "krummholz: identical trees");
        }
    }
}
