using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Recorded grading fixtures (task P3-08, plan §9, owner TL10): four trails on crops of the Jackson Hole 2 km package
    // (a sidehill with mixed widths and an island, a ridge, a gully, a too-steep face), graded and natural. Golden bytes
    // pin GR-01 to GR-13 and CL-01, CL-02, CL-08 on real ground; the ratings pin TD-01 v2 to TD-03.
    // A behaviour change re-records them with RecordTheGradingFixtures, with the owner's approval.
    public sealed class TrailFixtureTests
    {
        /// <summary>A recorded crop: heights at lattice points (column, row) from (Column0, Row0), bilinear between (SH-02).</summary>
        sealed class Crop : IHeightSource
        {
            public EditLattice Lattice;
            public int Column0, Row0, Width, Height;
            public float[] H;

            public double At(double x, double y)
            {
                double fx = x - Lattice.West - Column0, fy = Lattice.North - y - Row0;
                int i = (int)Math.Floor(fx), j = (int)Math.Floor(fy);
                if (i < 0 || j < 0 || i + 1 >= Width || j + 1 >= Height) throw new ArgumentOutOfRangeException(nameof(x), "outside the recorded crop");
                double tx = fx - i, ty = fy - j;
                int at = j * Width + i;
                double top = H[at] + (H[at + 1] - H[at]) * tx, bottom = H[at + Width] + (H[at + Width + 1] - H[at + Width]) * tx;
                return top + (bottom - top) * ty;
            }
        }

        sealed class Fixture
        {
            public string Name;
            public double OriginX, OriginY, West, North;
            public List<(int widthMm, bool erase, PointMm[] pts)> Strokes = new List<(int, bool, PointMm[])>();
            public PointMm Head, Tail;
            public Dictionary<string, string> Golden = new Dictionary<string, string>();
        }

        const ulong Seed = 0x7A11_2026;

        static string Folder() => Path.Combine(Path.GetDirectoryName(TestData.Folder("jackson-hole-2km")), "grading");

        // A tiny line format, so the fixture reads the same under Unity and .NET without a JSON dependency.
        static Fixture Read(string name)
        {
            var f = new Fixture { Name = name };
            foreach (string raw in File.ReadAllLines(Path.Combine(Folder(), name + ".trail.txt")))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(' ');
                double D(int i) => double.Parse(p[i], CultureInfo.InvariantCulture);
                switch (p[0])
                {
                    case "origin": f.OriginX = D(1); f.OriginY = D(2); break;
                    case "lattice": f.West = D(1); f.North = D(2); break;
                    case "head": f.Head = new PointMm(int.Parse(p[1]), int.Parse(p[2])); break;
                    case "tail": f.Tail = new PointMm(int.Parse(p[1]), int.Parse(p[2])); break;
                    case "stroke":
                    case "erase":
                        var pts = new PointMm[(p.Length - 2) / 2];
                        for (int i = 0; i < pts.Length; i++) pts[i] = new PointMm(int.Parse(p[2 + 2 * i]), int.Parse(p[3 + 2 * i]));
                        f.Strokes.Add((int.Parse(p[1]), p[0] == "erase", pts));
                        break;
                    case "golden": f.Golden[p[1]] = p[2]; break;
                }
            }
            return f;
        }

        static Crop Heights(string name, EditLattice lattice)
        {
            byte[] data = TestData.Bytes(Folder(), name + ".heights.bin");
            var crop = new Crop { Lattice = lattice };
            crop.Column0 = BitConverter.ToInt32(data, 0);
            crop.Row0 = BitConverter.ToInt32(data, 4);
            crop.Width = BitConverter.ToInt32(data, 8);
            crop.Height = BitConverter.ToInt32(data, 12);
            crop.H = new float[crop.Width * crop.Height];
            Buffer.BlockCopy(data, 16, crop.H, 0, crop.H.Length * 4);
            return crop;
        }

        static Trail TrailOf(Fixture f, bool graded)
        {
            var strokes = new List<Stroke>();
            foreach (var (w, erase, pts) in f.Strokes) strokes.Add(new Stroke(w, erase, pts));
            return new Trail(TrailId.Make(Seed, 1), f.Name, WorkStage.Built, graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun,
                             new[] { new FootprintPart(strokes.ToArray()) }, f.Head, f.Tail);
        }

        static Dictionary<string, string> Measure(Fixture f, IHeightSource heights)
        {
            var lattice = new EditLattice(f.West, f.North);
            var site = new EarthworkSite(lattice, f.OriginX, f.OriginY);
            var ground = new EditedHeights(heights, lattice, TerrainEdits.Empty);
            var outp = new Dictionary<string, string>();
            foreach (bool graded in new[] { true, false })
            {
                string k = graded ? "graded" : "natural";
                var build = TrailEarthwork.Grade(TrailOf(f, graded), ground, site, Seed, null, null);
                var tiles = new List<TerrainEditTile>();
                foreach (var key in build.Writes.Tiles) { var b = TerrainEditTile.Empty(key).Edit(); build.Writes.ApplyTo(b); tiles.Add(b.Build()); }
                var after = new EditedHeights(heights, lattice, TerrainEdits.Empty.With(tiles));
                var rating = TrailRating.Rate(after, build.Footprint, build.Grade);
                outp[k + ".writes"] = DesignTestKit.Sha(build.Writes.CanonicalBytes());
                outp[k + ".rating"] = rating.ValuePercent.ToString("F3", CultureInfo.InvariantCulture);
                outp[k + ".band"] = rating.Band.ToString();
                outp[k + ".ungradedStations"] = build.Grade.UngradedStations.ToString(CultureInfo.InvariantCulture);
                outp[k + ".cutM3"] = build.Grade.CutCubicMetres.ToString("F3", CultureInfo.InvariantCulture);
                outp[k + ".fillM3"] = build.Grade.FillCubicMetres.ToString("F3", CultureInfo.InvariantCulture);
            }
            return outp;
        }

        static readonly string[] Names = { "sidehill", "ridge", "gully", "steep-face" };

        [TestCase("sidehill")]
        [TestCase("ridge")]
        [TestCase("gully")]
        [TestCase("steep-face")]
        public void GR_TD_GradingMatchesTheRecordedFixture(string name)
        {
            var f = Read(name);
            var crop = Heights(name, new EditLattice(f.West, f.North));
            var got = Measure(f, crop);
            foreach (var kv in f.Golden)
                Assert.That(got[kv.Key], Is.EqualTo(kv.Value), $"{name} {kv.Key}");
            Assert.That(f.Golden.Count, Is.EqualTo(12), "every golden recorded");
        }

        /// <summary>The crops hold the package's heights at the lattice points (when the package is here): the fixtures stand for real ground.</summary>
        [Test]
        public void SH02_TheCropsAreThePackagesHeights()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string g in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(g));
            var m = ResortPackage.ReadManifest(dir);
            using var h = new PackageHeights(dir, m);
            foreach (string name in Names)
            {
                var f = Read(name);
                var crop = Heights(name, new EditLattice(f.West, f.North));
                for (int j = 1; j < crop.Height - 1; j += 7)
                    for (int i = 1; i < crop.Width - 1; i += 7)
                    {
                        double x = f.West + crop.Column0 + i, y = f.North - crop.Row0 - j;   // lattice points: the crop is bilinear between them
                        Assert.That(crop.At(x, y), Is.EqualTo(h.At(x, y)).Within(3e-4), $"{name} at {i},{j} (float32 storage)");
                    }
            }
        }

        // ---- recording (explicit) ----

        static readonly (string name, double[] path, int[] widthsFt, (double x, double y, int ft)[] islands)[] Spots =
        {
            ("sidehill", new double[] { 200, -330, 225, -480, 235, -600, 250, -760 }, new[] { 100, 220, 100 }, new[] { (245.0, -540.0, 45) }),
            ("ridge", new double[] { 183, 790, 280, 720, 377, 650 }, new[] { 120 }, new (double, double, int)[0]),
            ("gully", new double[] { 271, -78, 360, -160, 449, -242 }, new[] { 120 }, new (double, double, int)[0]),
            ("steep-face", new double[] { -650, 380, -660, 500, -650, 640 }, new[] { 150 }, new (double, double, int)[0]),
        };

        [Test, Explicit("Re-records the grading fixtures from the Jackson Hole 2 km package; needs the owner's approval")]
        public void RecordTheGradingFixtures()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string g in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(g));
            var m = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
            var lattice = new EditLattice(tiles.West, tiles.North);
            Directory.CreateDirectory(Folder());
            using var h = new PackageHeights(dir, m);
            foreach (var (name, path, widths, islands) in Spots)
            {
                var f = new Fixture { Name = name, OriginX = m.Site.CentreX, OriginY = m.Site.CentreY, West = lattice.West, North = lattice.North };
                int n = path.Length / 2;
                for (int k = 0; k + 1 < n; k++)
                    f.Strokes.Add((DesignUnits.FeetToMm(widths.Length == 1 ? widths[0] : widths[k]), false,
                                   new[] { PointMm.FromMetres(path[2 * k], path[2 * k + 1]), PointMm.FromMetres(path[2 * k + 2], path[2 * k + 3]) }));
                foreach (var (x, y, ft) in islands) f.Strokes.Add((DesignUnits.FeetToMm(ft), true, new[] { PointMm.FromMetres(x, y) }));
                f.Head = PointMm.FromMetres(path[0], path[1]);
                f.Tail = PointMm.FromMetres(path[2 * n - 2], path[2 * n - 1]);

                // The crop: the strokes' box plus 40 m.
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                foreach (var (w, _, pts) in f.Strokes)
                    foreach (var p in pts)
                    {
                        double r = w / 2000.0 + 40, x = f.OriginX + p.XMetres, y = f.OriginY + p.YMetres;
                        minX = Math.Min(minX, x - r); maxX = Math.Max(maxX, x + r); minY = Math.Min(minY, y - r); maxY = Math.Max(maxY, y + r);
                    }
                int c0 = (int)Math.Floor(minX - lattice.West), r0 = (int)Math.Floor(lattice.North - maxY);
                int c1 = (int)Math.Ceiling(maxX - lattice.West), r1 = (int)Math.Ceiling(lattice.North - minY);
                var crop = new Crop { Lattice = lattice, Column0 = c0, Row0 = r0, Width = c1 - c0 + 1, Height = r1 - r0 + 1 };
                crop.H = new float[crop.Width * crop.Height];
                for (int j = 0; j < crop.Height; j++)
                    for (int i = 0; i < crop.Width; i++)
                        crop.H[j * crop.Width + i] = (float)h.At(lattice.X(c0 + i), lattice.Y(r0 + j));
                var bytes = new byte[16 + crop.H.Length * 4];
                BitConverter.GetBytes(c0).CopyTo(bytes, 0);
                BitConverter.GetBytes(r0).CopyTo(bytes, 4);
                BitConverter.GetBytes(crop.Width).CopyTo(bytes, 8);
                BitConverter.GetBytes(crop.Height).CopyTo(bytes, 12);
                Buffer.BlockCopy(crop.H, 0, bytes, 16, crop.H.Length * 4);
                File.WriteAllBytes(Path.Combine(Folder(), name + ".heights.bin"), bytes);

                var golden = Measure(f, crop);
                var lines = new List<string>
                {
                    "# Task P3-08 grading fixture: a trail on a crop of the Jackson Hole 2 km package (TestData/jackson-hole-2km).",
                    "# Points are design mm from the site's centre (TE5); goldens from TrailFixtureTests.RecordTheGradingFixtures.",
                    $"origin {f.OriginX.ToString("R", CultureInfo.InvariantCulture)} {f.OriginY.ToString("R", CultureInfo.InvariantCulture)}",
                    $"lattice {f.West.ToString("R", CultureInfo.InvariantCulture)} {f.North.ToString("R", CultureInfo.InvariantCulture)}",
                };
                foreach (var (w, erase, pts) in f.Strokes)
                {
                    var sb = new System.Text.StringBuilder(erase ? "erase " : "stroke ").Append(w);
                    foreach (var p in pts) sb.Append(' ').Append(p.X).Append(' ').Append(p.Y);
                    lines.Add(sb.ToString());
                }
                lines.Add($"head {f.Head.X} {f.Head.Y}");
                lines.Add($"tail {f.Tail.X} {f.Tail.Y}");
                var keys = new List<string>(golden.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var k in keys) lines.Add($"golden {k} {golden[k]}");
                File.WriteAllLines(Path.Combine(Folder(), name + ".trail.txt"), lines);
                TestContext.Progress.WriteLine($"{name}: {crop.Width}x{crop.Height} points, {bytes.Length / 1024} KB");
            }
        }
    }
}
