using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.14's guardrails on real ground (owner finding F4): dams recorded on crops of the owner's Doublehead and
    /// Kearsarge areas, found along their OpenStreetMap stream lines. Good sites must build; the bad ones must be refused
    /// by the rule each names (PD-11 not worth building, PD-13 too low over the stream; PD-10, a cliff edge, if one is found). A behaviour
    /// change re-records them with <see cref="RecordTheDamFixtures"/>, with the owner's approval.
    /// </summary>
    public sealed class DamFixtureTests
    {
        /// <summary>A recorded crop: heights at lattice points (column, row) from (Column0, Row0), bilinear between.</summary>
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
            public string Name, Area, Expect;
            public double OriginX, OriginY, West, North;
            public Dam Dam;
            public Dictionary<string, string> Golden = new Dictionary<string, string>();
        }

        static string Folder() => Path.Combine(Path.GetDirectoryName(TestData.Folder("jackson-hole-2km")), "dams");

        static Fixture Read(string name)
        {
            var f = new Fixture { Name = name };
            foreach (string raw in File.ReadAllLines(Path.Combine(Folder(), name + ".dam.txt")))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split(' ');
                double D(int i) => double.Parse(p[i], CultureInfo.InvariantCulture);
                int I(int i) => int.Parse(p[i], CultureInfo.InvariantCulture);
                switch (p[0])
                {
                    case "area": f.Area = line.Substring(5); break;
                    case "expect": f.Expect = p[1]; break;
                    case "origin": f.OriginX = D(1); f.OriginY = D(2); break;
                    case "lattice": f.West = D(1); f.North = D(2); break;
                    case "dam":
                        f.Dam = new Dam(new DamId(1), "Fixture Dam", new PointMm(I(1), I(2)), new PointMm(I(3), I(4)), I(5), p[6] == "left",
                                        p[7] == "far" ? SpillBank.Far : SpillBank.First, 0);
                        break;
                    case "golden": f.Golden[p[1]] = line.Substring(8 + p[1].Length); break;
                }
            }
            return f;
        }

        // The crop file: gzip of int32 column0, row0, width, height, float64 base, then uint16 centimetres over the base.
        static Crop Heights(string name, EditLattice lattice)
        {
            byte[] packed = TestData.Bytes(Folder(), name + ".heights.bin");
            byte[] data;
            using (var gz = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
            using (var ms = new MemoryStream()) { gz.CopyTo(ms); data = ms.ToArray(); }
            var crop = new Crop { Lattice = lattice };
            crop.Column0 = BitConverter.ToInt32(data, 0);
            crop.Row0 = BitConverter.ToInt32(data, 4);
            crop.Width = BitConverter.ToInt32(data, 8);
            crop.Height = BitConverter.ToInt32(data, 12);
            double floor = BitConverter.ToDouble(data, 16);
            crop.H = new float[crop.Width * crop.Height];
            for (int k = 0; k < crop.H.Length; k++) crop.H[k] = (float)(floor + BitConverter.ToUInt16(data, 24 + 2 * k) / 100.0);
            return crop;
        }

        static Dictionary<string, string> Measure(Fixture f, IHeightSource heights)
        {
            var lattice = new EditLattice(f.West, f.North);
            var b = DamEarthwork.Analyse(f.Dam, new EditedHeights(heights, lattice, TerrainEdits.Empty), new EarthworkSite(lattice, f.OriginX, f.OriginY));
            string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
            return new Dictionary<string, string>
            {
                ["problem"] = b.Problem ?? "none",
                ["warning"] = b.Warning ?? "none",
                ["volumeM3"] = F(b.VolumeCubicMetres),
                ["fillM3"] = F(b.FillCubicMetres),
                ["areaM2"] = F(b.AreaSquareMetres),
                ["heightMaxM"] = F(b.HeightMaxMetres),
                ["poolOverBedM"] = F(b.PoolOverBedMetres),
                ["writes"] = DesignTestKit.Sha(b.Writes.CanonicalBytes()),
            };
        }

        static IEnumerable<string> Names() =>
            Directory.Exists(Folder()) ? Directory.GetFiles(Folder(), "*.dam.txt").Select(p => Path.GetFileName(p).Replace(".dam.txt", "")).OrderBy(n => n, StringComparer.Ordinal)
                                       : Enumerable.Empty<string>();

        [TestCaseSource(nameof(Names))]
        public void PD10_PD11_PD13_RealGroundDamsMatchTheirRecording(string name)
        {
            var f = Read(name);
            var got = Measure(f, Heights(name, new EditLattice(f.West, f.North)));
            foreach (var kv in f.Golden) Assert.That(got[kv.Key], Is.EqualTo(kv.Value), $"{name} {kv.Key}");
            Assert.That(f.Golden.Count, Is.EqualTo(got.Count), "every golden recorded");
            string problem = got["problem"];
            switch (f.Expect)
            {
                case "good": Assert.That(problem, Is.EqualTo("none"), $"{name} ({f.Area}) builds"); break;
                case "PD10": Assert.That(problem, Does.Contain("cliff edge"), name); break;
                case "PD11": Assert.That(problem, Does.Contain("more earth than it holds water"), name); break;
                case "PD13": Assert.That(problem, Does.Contain("over the stream"), name); break;
                default: Assert.Fail($"{name}: unknown expectation {f.Expect}"); break;
            }
        }

        // F3: the review's marks. The spillway sits on the dam line; a PD-13 refusal points at the bed on the line; a dam
        // that builds, or one refused for the whole of it (PD-11), points nowhere.
        [TestCaseSource(nameof(Names))]
        public void F3_TheSpillwayAndARefusalSitOnTheDamLine(string name)
        {
            var f = Read(name);
            var lattice = new EditLattice(f.West, f.North);
            var b = DamEarthwork.Analyse(f.Dam, new EditedHeights(Heights(name, lattice), lattice, TerrainEdits.Empty), new EarthworkSite(lattice, f.OriginX, f.OriginY));
            double ax = f.Dam.BankA.XMetres, ay = f.Dam.BankA.YMetres, bx = f.Dam.BankB.XMetres, by = f.Dam.BankB.YMetres;
            double OffLine(double x, double y)
            {
                double dx = bx - ax, dy = by - ay, t = Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)));
                return Math.Sqrt(Math.Pow(ax + t * dx - x, 2) + Math.Pow(ay + t * dy - y, 2));
            }
            Assert.That(OffLine(b.SpillX, b.SpillY), Is.LessThan(0.01), $"{name}: the spillway is on the line");
            if (f.Expect == "PD13")
            {
                Assert.That(double.IsNaN(b.ProblemX), Is.False, $"{name}: a low dam points at the bed");
                Assert.That(OffLine(b.ProblemX, b.ProblemY), Is.LessThan(0.01), name);
            }
            else Assert.That(double.IsNaN(b.ProblemX), Is.True, $"{name}: no single place to point at");
        }

        [Test]
        public void TheRecordingHoldsGoodSitesOnBothAreasAndEveryRefusal()
        {
            var all = Names().Select(Read).ToList();
            Assert.That(all.Where(f => f.Expect == "good").Select(f => f.Area).Distinct().Count(), Is.EqualTo(2), "good sites on Doublehead and Kearsarge (F2)");
            // PD-10 has no real-ground site: the owner's New England valleys hold no cliff below a stream, and the Jackson Hole
            // core's streams cross the flat valley floor. PondDamTests.PD06_ADamOnACliffEdgeIsRefused covers it (ticket 1997).
            Assert.That(all.Select(f => f.Expect).Distinct().OrderBy(x => x, StringComparer.Ordinal), Is.EqualTo(new[] { "PD11", "PD13", "good" }));
        }

        [Test]
        public void PD11_UnderOneToTenIsRefusedAndUnderOneToOneWarned()
        {
            Assert.That(PondRules.WorthProblem("dam", 5000, 4000, out bool refused), Is.Null, "more water than earth");
            Assert.That(refused, Is.False);
            Assert.That(PondRules.WorthProblem("dam", 1000, 1000, out refused), Is.Null, "1:1 exactly is fine");
            Assert.That(PondRules.WorthProblem("dam", 500, 1000, out refused), Is.EqualTo("This dam moves 2× more earth than it holds water."));
            Assert.That(refused, Is.False, "under 1:1: amber, still buildable");
            Assert.That(PondRules.WorthProblem("pond", 100, 1000, out refused), Is.EqualTo("This pond moves 10× more earth than it holds water."));
            Assert.That(refused, Is.False, "1:10 exactly is a warning");
            Assert.That(PondRules.WorthProblem("dam", 698.49, 9685.772, out refused), Is.EqualTo("This dam moves 14× more earth than it holds water: find a deeper, narrower spot."));
            Assert.That(refused, Is.True);
            Assert.That(PondRules.WorthProblem("pond", 0, 50, out refused), Does.Contain("holds no water"));
            Assert.That(refused, Is.True);
            Assert.That(PondRules.WorthProblem("pond", 0, 0.5, out refused), Is.Null, "nothing moved: other rules speak");
        }

        // ---------------- the recorder ----------------

        /// <summary>The library the recorder reads: a scratch copy of the owner's areas with their water.json (refresh-water).</summary>
        static string Library() => Environment.GetEnvironmentVariable("MP_DAMFIX_LIBRARY")
                                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkiAreaDesignChallenge-damfix");

        /// <summary>Passes reads through and keeps their lattice box.</summary>
        sealed class Reads : IHeightSource
        {
            public IHeightSource Inner;
            public EditLattice Lattice;
            public int MinC = int.MaxValue, MinR = int.MaxValue, MaxC = int.MinValue, MaxR = int.MinValue;

            public double At(double x, double y)
            {
                int c = (int)Math.Floor(x - Lattice.West), r = (int)Math.Floor(Lattice.North - y);
                MinC = Math.Min(MinC, c); MinR = Math.Min(MinR, r); MaxC = Math.Max(MaxC, c + 1); MaxR = Math.Max(MaxR, r + 1);
                return Inner.At(x, y);
            }
        }

        sealed class Candidate
        {
            public string Area, Expect;
            public Dam Dam;
            public DamBuild Build;
            public double X, Y;   // the bed, Albers
        }

        [Test, Explicit("Re-records the real-ground dam fixtures from the owner's areas (MP_DAMFIX_LIBRARY); needs the owner's approval")]
        public void RecordTheDamFixtures()
        {
            var picked = new List<(Candidate c, PackageManifest m, string folder)>();
            foreach (string folder in Directory.GetDirectories(Path.Combine(Library(), "Resorts")).OrderBy(d => d, StringComparer.Ordinal))
            {
                var m = ResortPackage.ReadManifest(folder);
                if (m.Water == null) continue;
                var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
                var lattice = new EditLattice(tiles.West, tiles.North);
                var site = new EarthworkSite(lattice, m.Site.CentreX, m.Site.CentreY);
                using var h = new PackageHeights(folder, m);
                var ground = new EditedHeights(h, lattice, TerrainEdits.Empty);
                var found = Search(m, folder, h, ground, site);
                TestContext.Progress.WriteLine($"{m.Site.Name}: {found.Count} candidates: " +
                    string.Join(", ", found.GroupBy(c => c.Expect).Select(g => $"{g.Key} {g.Count()}")));
                foreach (var c in found.Where(c => c.Expect == "good").OrderByDescending(c => c.Build.VolumeCubicMetres).Take(2)) picked.Add((c, m, folder));
                foreach (string bad in new[] { "PD10", "PD11", "PD13" })
                    if (!picked.Any(p => p.c.Expect == bad) && found.FirstOrDefault(c => c.Expect == bad) is Candidate c) picked.Add((c, m, folder));
            }

            Directory.CreateDirectory(Folder());
            foreach (string old in Directory.GetFiles(Folder())) File.Delete(old);
            int n = 0;
            foreach (var (c, m, folder) in picked)
            {
                string name = $"{(c.Area.StartsWith("Doublehead", StringComparison.Ordinal) ? "doublehead" : "kearsarge")}-{c.Expect.ToLowerInvariant()}-{++n}";
                var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
                var lattice = new EditLattice(tiles.West, tiles.North);
                var site = new EarthworkSite(lattice, m.Site.CentreX, m.Site.CentreY);
                using var h = new PackageHeights(folder, m);
                // The crop: the lattice points the earthwork actually reads (the flood stops where the water does), plus 3.
                var seen = new Reads { Inner = h, Lattice = lattice };
                DamEarthwork.Analyse(c.Dam, new EditedHeights(seen, lattice, TerrainEdits.Empty), site);
                int c0 = seen.MinC - 3, r0 = seen.MinR - 3, w = seen.MaxC - seen.MinC + 8, ht = seen.MaxR - seen.MinR + 8;
                var raw = new float[w * ht];
                for (int j = 0; j < ht; j++) for (int i = 0; i < w; i++) raw[j * w + i] = (float)h.At(lattice.X(c0 + i), lattice.Y(r0 + j));
                double floor = Math.Floor(raw.Min());
                Assert.That(raw.Max() - floor, Is.LessThan(650), "centimetres fit 16 bits");
                var data = new byte[24 + raw.Length * 2];
                BitConverter.GetBytes(c0).CopyTo(data, 0);
                BitConverter.GetBytes(r0).CopyTo(data, 4);
                BitConverter.GetBytes(w).CopyTo(data, 8);
                BitConverter.GetBytes(ht).CopyTo(data, 12);
                BitConverter.GetBytes(floor).CopyTo(data, 16);
                for (int k = 0; k < raw.Length; k++) BitConverter.GetBytes((ushort)Math.Round((raw[k] - floor) * 100)).CopyTo(data, 24 + 2 * k);
                using (var fs = File.Create(Path.Combine(Folder(), name + ".heights.bin")))
                using (var gz = new GZipStream(fs, CompressionLevel.Optimal)) gz.Write(data, 0, data.Length);

                var f = new Fixture
                {
                    Name = name, Area = c.Area, Expect = c.Expect, OriginX = m.Site.CentreX, OriginY = m.Site.CentreY, West = lattice.West, North = lattice.North,
                    Dam = new Dam(new DamId(1), "Fixture Dam", c.Dam.BankA, c.Dam.BankB, c.Dam.FullPoolMm, c.Dam.UpstreamLeft, c.Dam.Spill, 0),
                };
                var golden = Measure(f, Heights(name, lattice));   // measured on the stored crop, as the test reads it
                string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
                var lines = new List<string>
                {
                    "# Task 4.14 guardrail fixture (owner finding F4): a dam on a crop of the owner's area, found along its OSM stream lines.",
                    "# Points are design mm from the site's centre (TE5); goldens from DamFixtureTests.RecordTheDamFixtures.",
                    $"area {c.Area}",
                    $"expect {c.Expect}",
                    $"origin {R(f.OriginX)} {R(f.OriginY)}",
                    $"lattice {R(f.West)} {R(f.North)}",
                    $"dam {c.Dam.BankA.X} {c.Dam.BankA.Y} {c.Dam.BankB.X} {c.Dam.BankB.Y} {c.Dam.FullPoolMm} {(c.Dam.UpstreamLeft ? "left" : "right")} {(c.Dam.Spill == SpillBank.Far ? "far" : "first")}",
                };
                foreach (var k in golden.Keys.OrderBy(k => k, StringComparer.Ordinal)) lines.Add($"golden {k} {golden[k]}");
                File.WriteAllLines(Path.Combine(Folder(), name + ".dam.txt"), lines);
                TestContext.Progress.WriteLine($"{name}: {golden["problem"]} · {golden["volumeM3"]} m³ water, {golden["fillM3"]} m³ fill");
            }
        }

        /// <summary>
        /// Dam lines along the area's stream lines, every 80 m inside the core (550 m clear of its edge, so the flood stays on
        /// lidar): from the true bed (the lowest ground within 20 m), across the fall line, bank A where the ground stands
        /// 6 m over the bed and bank B where it reaches A's height on the other side. Each candidate also gets a low twin
        /// (bank A 1 m over the bed: PD-13) and a tall one (15 m, a cliff below: PD-10). Labelled by what the earthwork says.
        /// </summary>
        static List<Candidate> Search(PackageManifest m, string folder, IHeightSource h, EditedHeights ground, EarthworkSite site)
        {
            var found = new List<Candidate>();
            double half = m.Site.SizeMetres / 2 - 550;
            double G(double x, double y) => h.At(x, y);
            var streams = WaterFile.Read(folder, m);
            var tried = new List<(double x, double y)>();
            foreach (var s in streams)
            {
                double carry = 0;
                for (int i = 0; i + 1 < s.Points.Count; i++)
                {
                    var a = s.Points[i];
                    var b = s.Points[i + 1];
                    double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    for (double t = carry; t < len; t += 80)
                    {
                        double x = a.X + (b.X - a.X) * t / len, y = a.Y + (b.Y - a.Y) * t / len;
                        if (Math.Abs(x - m.Site.CentreX) > half || Math.Abs(y - m.Site.CentreY) > half) continue;
                        if (tried.Any(p => (p.x - x) * (p.x - x) + (p.y - y) * (p.y - y) < 60 * 60)) continue;
                        tried.Add((x, y));
                        Try(x, y);
                        if (found.Count(c => c.Expect == "good") >= 6 && found.Any(c => c.Expect == "PD10") && found.Any(c => c.Expect == "PD11")) return found;
                    }
                    carry = Math.Max(0, (carry - len) % 80);
                }
            }
            return found;

            void Try(double x0, double y0)
            {
                double bx = x0, by = y0, h0 = G(x0, y0);
                for (int i = -20; i <= 20; i += 2)
                for (int j = -20; j <= 20; j += 2)
                {
                    double hh = G(x0 + i, y0 + j);
                    if (hh < h0) { h0 = hh; bx = x0 + i; by = y0 + j; }
                }
                double ux = G(bx + 25, by) - G(bx - 25, by), uy = G(bx, by + 25) - G(bx, by - 25);
                double um = Math.Sqrt(ux * ux + uy * uy);
                if (um < 1e-2) return;
                ux /= um; uy /= um;
                double cx = -uy, cy = ux;   // across the fall line
                foreach (double rise in new[] { 6.0, 1.0, 15.0 })
                {
                    double out1 = 0;
                    for (double s = 2; s <= 120 && out1 == 0; s += 1) if (G(bx + cx * s, by + cy * s) >= h0 + rise) out1 = s;
                    if (out1 == 0) return;
                    double ax = bx + cx * out1, ay = by + cy * out1, pool = G(ax, ay);
                    double out2 = 0;
                    for (double s = 2; s <= 240 && out2 == 0; s += 0.5) if (G(bx - cx * s, by - cy * s) >= pool) out2 = s;
                    if (out2 == 0) return;
                    var A = PointMm.FromMetres(ax - site.OriginX, ay - site.OriginY);
                    var B = PointMm.FromMetres(bx - cx * out2 - site.OriginX, by - cy * out2 - site.OriginY);
                    var up = PointMm.FromMetres(bx + ux * 30 - site.OriginX, by + uy * 30 - site.OriginY);
                    var dam = new Dam(new DamId(1), "Fixture Dam", A, B, (int)Math.Round(pool * 1000), DamEarthwork.UpstreamLeftOf(A, B, up), SpillBank.First, 0);
                    var build = DamEarthwork.Analyse(dam, ground, site);
                    string p = build.Problem ?? "";
                    string expect = p.Length == 0 ? (build.Warning == null ? "good" : null)
                        : p.Contains("cliff edge") ? "PD10" : p.Contains("more earth than it holds water") ? "PD11" : p.Contains("over the stream") ? "PD13" : null;
                    if (rise == 1.0 && expect != "PD13") continue;   // the low twin stands for PD-13 only
                    if (rise == 15.0 && expect != "PD10") continue;   // the tall twin looks for a cliff below (PD-10) only
                    if (expect != null) found.Add(new Candidate { Area = m.Site.Name, Expect = expect, Dam = dam, Build = build, X = bx, Y = by });
                }
            }
        }
    }
}
