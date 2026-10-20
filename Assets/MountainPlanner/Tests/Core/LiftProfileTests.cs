using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 09's acceptance: towers keep the rope clear of the ground everywhere (TW-01 v2), over recorded lift lines on a
    // second mountain (fixes must be general, P3-07 round 3). Each recorded line is a strip of the real 1 m ground round the
    // line; the lift is built by the one build path, then its clearance checked again, independently, from what's stored.
    public sealed class LiftProfileTests
    {
        const string Fixture = "lift-profiles";
        const string FileName = "sugarloaf.bin";
        const double Ft = 0.3048;
        /// <summary>The strip reaches this far either side of the line and past each end: room for the pads, their 50 ft
        /// faces (GR-11) and the clearing.</summary>
        const double Margin = 40;

        /// <summary>One recorded line: the bullwheels, and the ground every metre in the line's own frame.</summary>
        sealed class Strip : IHeightSource
        {
            public double Bx, By, Tx, Ty;
            public int Along, Across;   // samples
            public float[] Heights;     // [i along][j across], i = 0 at Margin behind the bottom, j = 0 at Margin to the left
            double Ux => (Tx - Bx) / Length;
            double Uy => (Ty - By) / Length;
            public double Length => Math.Sqrt((Tx - Bx) * (Tx - Bx) + (Ty - By) * (Ty - By));

            public double At(double x, double y)
            {
                double dx = x - Bx, dy = y - By;
                double s = dx * Ux + dy * Uy + Margin;
                double w = dx * Uy - dy * Ux + Margin;   // right of uphill positive
                s = Math.Max(0, Math.Min(Along - 1.000001, s));
                w = Math.Max(0, Math.Min(Across - 1.000001, w));
                int i = (int)s, j = (int)w;
                double fs = s - i, fw = w - j;
                double h00 = Heights[i * Across + j], h01 = Heights[i * Across + j + 1];
                double h10 = Heights[(i + 1) * Across + j], h11 = Heights[(i + 1) * Across + j + 1];
                return (h00 * (1 - fw) + h01 * fw) * (1 - fs) + (h10 * (1 - fw) + h11 * fw) * fs;
            }
        }

        sealed class Recording
        {
            public double West, North;
            public List<Strip> Lines = new List<Strip>();
        }

        static Recording Read(byte[] bytes)
        {
            var r = new Recording();
            using var br = new BinaryReader(new MemoryStream(bytes));
            if (new string(br.ReadChars(4)) != "MPLP") throw new InvalidDataException("not a lift-profile recording");
            int version = br.ReadInt32();
            if (version != 1) throw new InvalidDataException("lift-profile recording version " + version);
            r.West = br.ReadDouble();
            r.North = br.ReadDouble();
            int count = br.ReadInt32();
            for (int k = 0; k < count; k++)
            {
                var s = new Strip { Bx = br.ReadDouble(), By = br.ReadDouble(), Tx = br.ReadDouble(), Ty = br.ReadDouble(), Along = br.ReadInt32(), Across = br.ReadInt32() };
                s.Heights = new float[s.Along * s.Across];
                for (int i = 0; i < s.Heights.Length; i++) s.Heights[i] = br.ReadSingle();
                r.Lines.Add(s);
            }
            return r;
        }

        [TestCase(LiftCatalogIds.SesselliftFgq4)]
        [TestCase(LiftCatalogIds.MontaFg4)]
        public void TW01v2_TowersKeepTheRopeClearOverRecordedLinesOnASecondMountain(string catalogId)
        {
            string dir = TestData.Folder(Fixture);
            var rec = Read(TestData.Bytes(dir, FileName));
            var type = LiftType.Of(catalogId);
            var lattice = new EditLattice(rec.West, rec.North);
            int ok = 0, red = 0;
            var heads = new SortedDictionary<LiftRope.Head, int>();
            foreach (var line in rec.Lines)
            {
                var ground = new EditedHeights(line, lattice, TerrainEdits.Empty);
                var b = LiftBuild.Build(type, ground, line.Bx, line.By, line.Tx, line.Ty, TowerStance.Leaned);
                foreach (var t in b.Rope.Towers) heads[t.Head] = heads.TryGetValue(t.Head, out int c) ? c + 1 : 1;
                string what = $"{line.Length / Ft:F0} ft, rise {(line.At(line.Tx, line.Ty) - line.At(line.Bx, line.By)) / Ft:F0} ft";
                if (b.Ok)
                {
                    ok++;
                    // Checked again from what's stored, over the ground as built (the lift's own pads, trench and clearing).
                    var stored = b.Towers.Select((t, i) => new Tower(TowerId.Make(1, i), t.AlongMm, t.HeightMm)).ToList();
                    var lift = new Lift(LiftId.Make(1, 1), "Recorded", WorkStage.Built, catalogId, new PointMm(0, 0),
                                        new PointMm((int)Math.Round((line.Tx - line.Bx) * 1000), (int)Math.Round((line.Ty - line.By) * 1000)),
                                        TowerMode.Automatic, TowerStance.Leaned, stored, b.Levels);
                    var built = new EditedHeights(line, lattice, b.Edits);
                    Assert.That(LiftClearance.Failing(built, lift, type, line.Bx, line.By), Is.Empty, what);
                    TestContext.Progress.WriteLine($"{what}: ok, {b.Towers.Count} towers");
                }
                else
                {
                    red++;
                    // Never a silent failure: a line nothing fits is red with its reason.
                    Assert.That(b.Problems, Is.Not.Empty, what);
                    TestContext.Progress.WriteLine($"{what}: RED {string.Join("; ", b.Problems)}");
                }
            }
            TestContext.Progress.WriteLine($"{catalogId}: {ok} of {rec.Lines.Count} lines within every rule; heads {string.Join(", ", heads.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Assert.That(rec.Lines.Count, Is.GreaterThanOrEqualTo(10));
            Assert.That(ok, Is.GreaterThanOrEqualTo(0.7 * rec.Lines.Count));
        }

        /// <summary>
        /// Records the fixture from a local copy of the Sugarloaf area package (owner's machine): set MP_LIFT_SITE to the
        /// package folder. Lines between LF-08 terminal sites as a player draws them, as in the Jackson Hole fan.
        /// </summary>
        [Test, Explicit("Records TestData/lift-profiles/sugarloaf.bin from a local package (MP_LIFT_SITE)")]
        public void RecordTheSecondMountain()
        {
            string package = Environment.GetEnvironmentVariable("MP_LIFT_SITE");
            if (string.IsNullOrEmpty(package)) Assert.Ignore("MP_LIFT_SITE isn't set");
            var m = ResortPackage.ReadManifest(package);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
            using var heights = new PackageHeights(package, m);
            double cx = m.Site.CentreX, cy = m.Site.CentreY, half = m.Site.SizeMetres / 2 - 200;
            var sites = new List<(double X, double Y, double H)>();
            for (double y = cy - half; y <= cy + half; y += 40)
                for (double x = cx - half; x <= cx + half; x += 40)
                {
                    double lo = double.MaxValue, hi = double.MinValue;
                    for (double dy = -6; dy <= 6; dy += 2)
                        for (double dx = -6; dx <= 6; dx += 2)
                        {
                            double h = heights.At(x + dx, y + dy);
                            lo = Math.Min(lo, h);
                            hi = Math.Max(hi, h);
                        }
                    if (hi - lo <= 10 * Ft) sites.Add((x, y, heights.At(x, y)));
                }
            var lines = new List<(double, double, double, double)>();
            foreach (var cell in sites.GroupBy(p => (Math.Floor((p.X - cx) / 600), Math.Floor((p.Y - cy) / 600))).OrderBy(g => g.Key))
            {
                var bottom = cell.First();
                var top = sites.Where(t =>
                {
                    double d = Math.Sqrt((t.X - bottom.X) * (t.X - bottom.X) + (t.Y - bottom.Y) * (t.Y - bottom.Y));
                    return d >= 300 && d <= 1200 && t.H - bottom.H >= 30 && (t.H - bottom.H) / Ft <= 1100;
                }).OrderBy(t => Math.Abs(t.H - bottom.H - 183)).ThenBy(t => t.X).ThenBy(t => t.Y).FirstOrDefault();
                if (top.H == 0) continue;
                lines.Add((bottom.X, bottom.Y, top.X, top.Y));
            }
            // At most 16, spread through the list.
            if (lines.Count > 16) lines = Enumerable.Range(0, 16).Select(i => lines[i * lines.Count / 16]).ToList();
            string dir = Path.Combine(TestData.Folder("formats"), "..", Fixture);
            Directory.CreateDirectory(dir);
            using var bw = new BinaryWriter(File.Create(Path.Combine(dir, FileName)));
            bw.Write("MPLP".ToCharArray());
            bw.Write(1);
            bw.Write(tiles.West);
            bw.Write(tiles.North);
            bw.Write(lines.Count);
            foreach (var (bx, by, tx, ty) in lines)
            {
                double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
                double ux = (tx - bx) / length, uy = (ty - by) / length;
                int along = (int)Math.Ceiling(length + 2 * Margin) + 1, across = (int)(2 * Margin) + 1;
                bw.Write(bx); bw.Write(by); bw.Write(tx); bw.Write(ty); bw.Write(along); bw.Write(across);
                for (int i = 0; i < along; i++)
                    for (int j = 0; j < across; j++)
                    {
                        double s = i - Margin, w = j - Margin;
                        bw.Write((float)heights.At(bx + ux * s + uy * w, by + uy * s - ux * w));
                    }
            }
            TestContext.Progress.WriteLine($"{lines.Count} lines recorded from {sites.Count} terminal sites");
        }
    }
}
