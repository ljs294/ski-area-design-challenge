using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The drawn resort (Phase 3 task 11): its frozen lines keep the mix the owner chose (DB3, DB4) and stay paintable,
    /// and, on the Jackson Hole 5 km package (local only, E2: set MP_JH5_PACKAGE to its folder), every lift and trail goes
    /// in through the design's commands and the same mountain builds the same design.
    /// </summary>
    public class DrawnResortTests
    {
        [Test]
        public void DB3_TwoLiftsOfEachBuildableType()
        {
            Assert.That(DrawnResort.Lifts.Length, Is.EqualTo(6));
            foreach (string id in new[] { LiftCatalogIds.SesselliftFgq4, LiftCatalogIds.MontaFg4, LiftCatalogIds.SesselliftD6 })
                Assert.That(DrawnResort.Lifts.Count(l => l.CatalogId == id), Is.EqualTo(2), id);
            Assert.That(DrawnResort.Lifts.All(l => LiftCatalogIds.Buildable(l.CatalogId)), Is.True);
            Assert.That(DrawnResort.Lifts.Select(l => l.Name).Distinct().Count(), Is.EqualTo(6), "names are unique");
            foreach (var l in DrawnResort.Lifts)
            {
                double length = Math.Sqrt(Math.Pow(l.TopX - l.BottomX, 2) + Math.Pow(l.TopZ - l.BottomZ, 2));
                Assert.That(length, Is.InRange(800, 1600), l.Name);
            }
        }

        [Test]
        public void DB4_TenGradedTenNaturalTwoCatTracksTwoIslands()
        {
            var runs = DrawnResort.Trails;
            Assert.That(runs.Length, Is.EqualTo(20));
            Assert.That(runs.Count(r => r.Graded), Is.EqualTo(10));
            Assert.That(runs.Count(r => r.CatTrack), Is.EqualTo(2));
            Assert.That(runs.Where(r => r.CatTrack).All(r => r.Graded && r.WidthFeet == DrawnResort.CatTrackFeet), Is.True);
            Assert.That(runs.Count(r => r.IslandAt >= 0), Is.EqualTo(2));
            Assert.That(runs.Select(r => r.Name).Distinct().Count(), Is.EqualTo(20), "names are unique");
            foreach (var r in runs)
            {
                // TR-01's brush, 25-400 ft in 5 ft steps; DB4's 60-200 ft for runs.
                Assert.That(r.WidthFeet % 5, Is.EqualTo(0), r.Name);
                if (!r.CatTrack) Assert.That(r.WidthFeet, Is.InRange(60, 200), r.Name);
                Assert.That(r.Centreline.Length % 2, Is.EqualTo(0), r.Name);
                Assert.That(r.Centreline.Length / 2, Is.GreaterThanOrEqualTo(10), r.Name);
                for (int i = 2; i < r.Centreline.Length; i += 2)
                {
                    double step = Math.Sqrt(Math.Pow(r.Centreline[i] - r.Centreline[i - 2], 2) + Math.Pow(r.Centreline[i + 1] - r.Centreline[i - 1], 2));
                    Assert.That(step, Is.InRange(1, 35), $"{r.Name}: points about 25 m apart (a little more where two legs join)");
                }
                Assert.That(r.IslandAt, Is.LessThan(r.Centreline.Length / 2), r.Name);
                // Everything inside the 5 km core.
                Assert.That(r.Centreline.All(v => Math.Abs(v) < 2400), Is.True, r.Name);
            }
        }

        [Test]
        public void DB1_TheResortBuildsByCommandTheSameEachTime()
        {
            string dir = Environment.GetEnvironmentVariable("MP_JH5_PACKAGE");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Assert.Ignore("set MP_JH5_PACKAGE to the Jackson Hole 5 km package folder");
            byte[] first = null;
            for (int pass = 0; pass < 2; pass++)
            {
                var watch = Stopwatch.StartNew();
                var (history, report) = Build(dir);
                TestContext.Progress.WriteLine($"pass {pass}: {watch.Elapsed.TotalSeconds:F1} s\n{report.Text}");
                Assert.That(report.Rejected, Is.EqualTo(0), report.Text);
                Assert.That(report.Accepted, Is.EqualTo(26));
                var doc = history.Document;
                Assert.That(doc.Lifts.Count, Is.EqualTo(6));
                Assert.That(doc.Lifts.All(l => l.Stage == WorkStage.Built && l.Towers.Count > 0 && l.Levels.HasValue), Is.True);
                byte[] bytes = DesignBytes.Encode(doc);
                if (first == null) first = bytes;
                else Assert.That(bytes, Is.EqualTo(first), "the same mountain builds the same design");
            }
        }

        /// <summary>The resort built over the package's natural ground, with the game's fold over the core (TerrainEditSession's).</summary>
        internal static (DesignHistory history, DrawnResort.Report report) Build(string dir)
        {
            using var r = new Loaded(dir);
            return (r.History, r.Report);
        }

        /// <summary>The package's ground and the resort built over it, kept open for edits (the probe).</summary>
        internal sealed class Loaded : IDisposable
        {
            public readonly PackageHeights Heights;
            public readonly EarthworkSite Site;
            public readonly EditLattice Lattice;
            public readonly LocalFrame Frame;
            public readonly DesignHistory History;
            public readonly DesignEarthwork Fold;
            public readonly DrawnResort.Report Report;

            public Loaded(string dir)
            {
                var m = ResortPackage.ReadManifest(dir);
                var square = SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0);
                var tiles = TileGrid.For(square);
                Lattice = new EditLattice(tiles.West, tiles.North);
                var core = square.Core;
                Site = new EarthworkSite(Lattice, m.Site.CentreX, m.Site.CentreY,
                                         new PointRect((int)Math.Ceiling(core.West - Lattice.West), (int)Math.Ceiling(Lattice.North - core.North),
                                                       (int)Math.Floor(core.East - Lattice.West), (int)Math.Floor(Lattice.North - core.South)));
                Heights = new PackageHeights(dir, m);
                History = new DesignHistory(DesignDocument.Empty(1));
                Fold = new DesignEarthwork(Heights, Site);
                Fold.Prime(History.Document, History.Terrain);
                var fold = Fold;
                History.Changed += s => fold.Follow(s.Document, s.Terrain);   // as TerrainEditSession wires it
                Frame = new LocalFrame(new AlbersPoint(m.Site.CentreX, m.Site.CentreY));
                Report = DrawnResort.Build(History, Fold, Heights, Lattice, Frame);
            }

            public void Dispose() => Heights.Dispose();
        }

        /// <summary>
        /// The edit bench's edits (-designbench) through the design's earthwork alone, on the 5 km package (local only):
        /// for each preview, how long the fold took here, which pieces it graded again and how many tiles it opened, and
        /// whether its edits equal a fresh fold of the same design (task P3-11's fix: the fold must stay exact). The
        /// times are the .NET runtime's, faster than the player's; the player's numbers decide. MP_PROBE_SPOTS sets how
        /// many of the bench's ten spots (default 3).
        /// </summary>
        [Test]
        public void Probe_TheResortsEditsThroughTheFold()
        {
            string dir = Environment.GetEnvironmentVariable("MP_JH5_PACKAGE");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Assert.Ignore("set MP_JH5_PACKAGE to the Jackson Hole 5 km package folder");
            int spots = int.TryParse(Environment.GetEnvironmentVariable("MP_PROBE_SPOTS"), out int n) ? n : 3;
            using var r = new Loaded(dir);
            Assert.That(r.Report.Rejected, Is.EqualTo(0), r.Report.Text);
            var history = r.History;
            var natural = history.Document.Trails.Where(t => t.Settings.Grading == TrailGrading.Natural).Select(t => t.Id).ToList();
            var builtTrails = history.Document.Trails.Select(t => t.Id).ToList();
            var log = new System.Text.StringBuilder();
            int inexact = 0;

            // A preview as the tools make it, timed, then checked against a fresh fold of the same design.
            DesignEarthwork.Result Preview(string what, DesignCommand cmd)
            {
                var after = cmd.Apply(history.Document);
                Assert.That(after.Accepted, Is.True, what + ": " + after);
                var watch = Stopwatch.StartNew();
                var rebuilt = r.Fold.Rebuild(after.Document, history.Terrain);
                double ms = watch.Elapsed.TotalMilliseconds;
                int trails = 0, lifts = 0, others = 0;
                foreach (var kv in rebuilt.Overlay)
                {
                    if (kv.Value == null) continue;
                    if (after.Document.FindTrail(new TrailId(kv.Key)) != null) trails++;
                    else if (after.Document.Lifts.Any(l => l.Id.Value == kv.Key)) lifts++;
                    else others++;
                }
                var fresh = DesignEarthwork.Regrade(r.Heights, r.Site, after.Document);
                int differ = TerrainEdits.Changed(fresh, rebuilt.Edits).Count;
                if (differ > 0) inexact++;
                log.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0,-34} {1,8:F1} ms  graded again: {2} trails, {3} lifts, {4} other; {5} tiles opened; {6}",
                    what, ms, trails, lifts, others, rebuilt.Tiles.Count, differ == 0 ? "exact" : $"DIFFERS from a fresh fold on {differ} tiles"));
                return rebuilt;
            }
            void CommitAndUndo(DesignCommand cmd, DesignEarthwork.Result rebuilt)
            {
                var c = history.Submit(cmd, rebuilt.Edits);
                Assert.That(c.Accepted, Is.True, c.ToString());
                r.Fold.Accept(rebuilt);
                history.Undo();
            }
            // Down the fall line every 25 m on the ground as it is now, as the bench's FallLine.
            List<PointMm> FallLine(double x, double z, double length)
            {
                var ground = new EditedHeights(r.Heights, r.Lattice, history.Terrain);
                var pts = new List<PointMm>();
                for (double walked = 0; walked <= length; walked += 25)
                {
                    pts.Add(PointMm.FromMetres(x, z));
                    var p = r.Frame.ToAlbers(x, z);
                    double east = ground.At(p.X + 5, p.Y) - ground.At(p.X - 5, p.Y), north = ground.At(p.X, p.Y + 5) - ground.At(p.X, p.Y - 5);
                    double len = Math.Sqrt(east * east + north * north);
                    if (len < 1e-9) { z -= 25; continue; }
                    x -= east / len * 25;
                    z -= north / len * 25;
                }
                return pts;
            }

            for (int s = 0; s < spots; s++)
            {
                double angle = s * Math.PI * 2 / 10;
                double hx = Math.Sin(angle) * 600, hz = Math.Cos(angle) * 600;
                foreach (double length in new[] { 300.0, 1000.0 })
                {
                    var path = FallLine(hx, hz, length);
                    var shape = TrailShape.FromStrokes(null, new[] { new Stroke(DesignUnits.FeetToMm(100), false, path.ToArray()) });
                    var add = new AddTrail(history.Revision, "Bench " + s, TrailSettings.GradedRun, shape, path[0], path[path.Count - 1], WorkStage.Built);
                    CommitAndUndo(add, Preview($"spot {s}: new {length:F0} m trail", add));
                }
                {
                    var t = history.Document.FindTrail(natural[s % natural.Count]);
                    var grade = new EditTrail(history.Revision, t.Id, t.Name, t.Shape, t.Head.Value, t.Tail.Value, TrailSettings.GradedRun, WorkStage.Built);
                    CommitAndUndo(grade, Preview($"spot {s}: grade {t.Name}", grade));
                }
                {
                    var id = builtTrails[(s * 2) % builtTrails.Count];
                    var remove = new DeleteTrail(history.Revision, id);
                    CommitAndUndo(remove, Preview($"spot {s}: remove {history.Document.FindTrail(id).Name}", remove));
                }
                {
                    var type = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
                    var path = FallLine(hx, hz, 25);
                    double dx = path[1].XMetres - path[0].XMetres, dz = path[1].YMetres - path[0].YMetres, len = Math.Sqrt(dx * dx + dz * dz);
                    double tx = hx - dx / len * 600, tz = hz - dz / len * 600;
                    var b = r.Frame.ToAlbers(hx, hz);
                    var tp = r.Frame.ToAlbers(tx, tz);
                    var ground = new EditedHeights(r.Heights, r.Lattice, history.Terrain);
                    var built = LiftBuild.Build(type, ground, b.X, b.Y, tp.X, tp.Y, TowerStance.Leaned, null, 0, 0, suggest: false);
                    var add = new AddLift(history.Revision, "Bench lift " + s, LiftCatalogIds.SesselliftFgq4, PointMm.FromMetres(hx, hz),
                                          PointMm.FromMetres(tx, tz), TowerMode.Automatic, TowerStance.Leaned, built.Towers, WorkStage.Built, built.Levels);
                    CommitAndUndo(add, Preview($"spot {s}: new lift", add));
                }
            }
            TestContext.Progress.WriteLine(log.ToString());
            Assert.That(inexact, Is.EqualTo(0), "every preview equals a fresh fold of its design\n" + log);
        }
    }
}
