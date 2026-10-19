using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Review pictures for task P3-08 (explicit, never in CI): plan views of trails graded on the committed Jackson Hole
    // 2 km package, drawn engine-free. Writes PNGs under test-results/p3-08/.
    [Explicit("Writes review pictures")]
    public sealed class TrailPictures
    {
        sealed class Rgb
        {
            public readonly int W, H;
            public readonly byte[] Px;
            public Rgb(int w, int h) { W = w; H = h; Px = new byte[w * h * 3]; }
            public void Set(int x, int y, double r, double g, double b, double a = 1)
            {
                if (x < 0 || y < 0 || x >= W || y >= H) return;
                int i = (y * W + x) * 3;
                Px[i] = (byte)Math.Round(Px[i] * (1 - a) + Clamp(r) * a);
                Px[i + 1] = (byte)Math.Round(Px[i + 1] * (1 - a) + Clamp(g) * a);
                Px[i + 2] = (byte)Math.Round(Px[i + 2] * (1 - a) + Clamp(b) * a);
            }
            static double Clamp(double v) => Math.Max(0, Math.Min(255, v));
            public void Disc(double cx, double cy, double rad, double r, double g, double b)
            {
                for (int y = (int)(cy - rad - 1); y <= cy + rad + 1; y++)
                    for (int x = (int)(cx - rad - 1); x <= cx + rad + 1; x++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= rad * rad) Set(x, y, r, g, b);
            }
            public void Line(double ax, double ay, double bx, double by, double rad, double r, double g, double b)
            {
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int n = Math.Max(1, (int)(len * 2));
                for (int i = 0; i <= n; i++) Disc(ax + (bx - ax) * i / n, ay + (by - ay) * i / n, rad, r, g, b);
            }
            public void Save(string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using var f = File.Create(path);
                f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                Be(ihdr, 0, W); Be(ihdr, 4, H); ihdr[8] = 8; ihdr[9] = 2;
                Chunk(f, "IHDR", ihdr);
                var raw = new byte[(W * 3 + 1) * H];
                for (int y = 0; y < H; y++) Buffer.BlockCopy(Px, y * W * 3, raw, y * (W * 3 + 1) + 1, W * 3);
                using var ms = new MemoryStream();
                ms.WriteByte(0x78); ms.WriteByte(0x01);
                using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                foreach (byte v in raw) { a = (a + v) % 65521; b = (b + a) % 65521; }
                var ad = new byte[4]; Be(ad, 0, (int)((b << 16) | a));
                ms.Write(ad, 0, 4);
                Chunk(f, "IDAT", ms.ToArray());
                Chunk(f, "IEND", new byte[0]);
            }
            static void Be(byte[] d, int o, int v) { d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v; }
            static void Chunk(Stream s, string type, byte[] data)
            {
                var len = new byte[4]; Be(len, 0, data.Length); s.Write(len, 0, 4);
                var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t, 0, 4); s.Write(data, 0, data.Length);
                uint crc = 0xFFFFFFFF;
                foreach (byte v in t) crc = Crc(crc, v);
                foreach (byte v in data) crc = Crc(crc, v);
                var c = new byte[4]; Be(c, 0, (int)(crc ^ 0xFFFFFFFF)); s.Write(c, 0, 4);
            }
            static uint Crc(uint c, byte v)
            {
                c ^= v;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                return c;
            }
        }

        static string Out(string name) => Path.Combine(Path.GetDirectoryName(TestData.Folder("jackson-hole-2km")), "..", "test-results", "p3-08", name);

        static (PackageHeights h, EarthworkSite site, PackageManifest m) Open()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(f));
            var m = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
            var lattice = new EditLattice(tiles.West, tiles.North);
            return (new PackageHeights(dir, m), new EarthworkSite(lattice, m.Site.CentreX, m.Site.CentreY), m);
        }

        /// <summary>A shaded relief of <paramref name="h"/> over a square (local metres from the site's centre), with contours.</summary>
        static Rgb Relief(IHeightSource h, EarthworkSite site, double cx, double cy, double size, int px, double contour, out Func<double, double, (double, double)> toPx)
        {
            var img = new Rgb(px, px);
            double m = size / px, x0 = site.OriginX + cx - size / 2, y0 = site.OriginY + cy + size / 2;
            toPx = (x, y) => ((x - x0) / m, (y0 - y) / m);
            for (int j = 0; j < px; j++)
                for (int i = 0; i < px; i++)
                {
                    double x = x0 + (i + 0.5) * m, y = y0 - (j + 0.5) * m;
                    double z = h.At(x, y), ex = h.At(x + 1, y) - h.At(x - 1, y), ny = h.At(x, y + 1) - h.At(x, y - 1);
                    double nx = -ex / 2, nyy = -ny / 2, nz = 1, len = Math.Sqrt(nx * nx + nyy * nyy + nz * nz);
                    double shade = Math.Max(0, (nx * -0.5 + nyy * 0.6 + nz * 0.62) / len);
                    double v = 70 + 170 * shade;
                    img.Set(i, j, v * 0.97, v, v * 1.02);
                    double zr = h.At(x + m, y), zd = h.At(x, y - m);
                    if (Math.Floor(z / contour) != Math.Floor(zr / contour) || Math.Floor(z / contour) != Math.Floor(zd / contour))
                        img.Set(i, j, 40, 40, 40, 0.45);
                }
            return img;
        }

        sealed class Spot
        {
            public string Name;
            public double[] Path;          // local metres, head first
            public int[] WidthsFt;         // one stroke per pair of points, or one for all
            public (double x, double y, int ft)[] Islands = new (double, double, int)[0];
        }

        [Test]
        public void Overview()
        {
            var (h, site, m) = Open();
            using (h)
            {
                var img = Relief(h, site, 0, 0, 2000, 1000, 10, out var toPx);
                for (int k = -900; k <= 900; k += 100)
                {
                    var (a, b) = toPx(site.OriginX + k, site.OriginY - 1000);
                    var (c, d) = toPx(site.OriginX + k, site.OriginY + 1000);
                    img.Line(a, b, c, d, 0.4, 255, 255, 255);
                    (a, b) = toPx(site.OriginX - 1000, site.OriginY + k);
                    (c, d) = toPx(site.OriginX + 1000, site.OriginY + k);
                    img.Line(a, b, c, d, 0.4, 255, 255, 255);
                }
                img.Save(Out("overview-grid-100m.png"));
                var rows = new List<string>();
                for (int y = 900; y >= -900; y -= 100)
                {
                    var sb = new System.Text.StringBuilder($"{y,5}:");
                    for (int x = -900; x <= 900; x += 100) sb.Append($" {h.At(site.OriginX + x, site.OriginY + y),5:F0}");
                    rows.Add(sb.ToString());
                }
                File.WriteAllLines(Out("overview-heights.txt"), rows);
            }
        }

        /// <summary>
        /// Paints a trail, grades it (graded), and draws: relief and contours of the graded ground, the footprint's edge,
        /// cut (warm) and fill (cool) tint, the centreline, red ungraded stations, the head and tail.
        /// </summary>
        static string Draw(PackageHeights h, EarthworkSite site, Spot s, bool graded, string file)
        {
            var strokes = new List<Stroke>();
            int n = s.Path.Length / 2;
            for (int k = 0; k + 1 < n; k++)
            {
                int ft = s.WidthsFt.Length == 1 ? s.WidthsFt[0] : s.WidthsFt[k];
                strokes.Add(new Stroke(DesignUnits.FeetToMm(ft), false, new[] { PointMm.FromMetres(s.Path[2 * k], s.Path[2 * k + 1]),
                                                                              PointMm.FromMetres(s.Path[2 * k + 2], s.Path[2 * k + 3]) }));
            }
            foreach (var (x, y, ft) in s.Islands) strokes.Add(new Stroke(DesignUnits.FeetToMm(ft), true, new[] { PointMm.FromMetres(x, y) }));
            var head = PointMm.FromMetres(s.Path[0], s.Path[1]);
            var tail = PointMm.FromMetres(s.Path[2 * n - 2], s.Path[2 * n - 1]);
            var trail = new Trail(TrailId.Make(1, 1), s.Name, WorkStage.Built, graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun,
                                  new[] { new FootprintPart(strokes.ToArray()) }, head, tail);
            var ground = new EditedHeights(h, site.Lattice, TerrainEdits.Empty);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var build = TrailEarthwork.Grade(trail, ground, site, 1, null, null);
            double ms = watch.Elapsed.TotalMilliseconds;
            var tiles = new List<TerrainEditTile>();
            foreach (var key in build.Writes.Tiles) { var b = TerrainEditTile.Empty(key).Edit(); build.Writes.ApplyTo(b); tiles.Add(b.Build()); }
            var after = new EditedHeights(h, site.Lattice, TerrainEdits.Empty.With(tiles));
            var fp = build.Footprint;
            var g = build.Grade;
            var rating = TrailRating.Rate(after, fp, g);

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                minX = Math.Min(minX, s.Path[2 * k]); maxX = Math.Max(maxX, s.Path[2 * k]);
                minY = Math.Min(minY, s.Path[2 * k + 1]); maxY = Math.Max(maxY, s.Path[2 * k + 1]);
            }
            double size = Math.Max(maxX - minX, maxY - minY) + 120;
            int px = 900;
            var img = Relief(after, site, (minX + maxX) / 2, (minY + maxY) / 2, size, px, 2, out var toPx);
            double mpp = size / px;
            // Cut and fill tint, and the footprint's edge.
            for (int j = 0; j < px; j++)
                for (int i = 0; i < px; i++)
                {
                    double x = site.OriginX + (minX + maxX) / 2 - size / 2 + (i + 0.5) * mpp, y = site.OriginY + (minY + maxY) / 2 + size / 2 - (j + 0.5) * mpp;
                    double d = after.At(x, y) - h.At(x, y);
                    if (Math.Abs(d) > 0.05)
                    {
                        double a = Math.Min(0.55, Math.Abs(d) / 4);
                        if (d < 0) img.Set(i, j, 214, 120, 40, a); else img.Set(i, j, 40, 120, 214, a);
                    }
                    bool inside = fp.ContainsAt(x, y), right = fp.ContainsAt(x + mpp, y), down = fp.ContainsAt(x, y - mpp);
                    if (inside != right || inside != down) img.Set(i, j, 255, 255, 255, 0.9);
                }
            // Centreline and stations: white dashes; red where ungraded (GR-07).
            for (int k = 0; k + 1 < g.Stations.Length / 2; k++)
            {
                var (ax, ay) = toPx(g.Stations[2 * k], g.Stations[2 * k + 1]);
                var (bx, by) = toPx(g.Stations[2 * k + 2], g.Stations[2 * k + 3]);
                bool red = graded && (!g.Benched[k] || !g.Benched[k + 1]);
                if (red) img.Line(ax, ay, bx, by, 2.6, 225, 40, 30);
                else if (k % 2 == 0) img.Line(ax, ay, bx, by, 1.2, 20, 20, 20);
            }
            var (hx, hy) = toPx(g.Stations[0], g.Stations[1]);
            var (tx, ty) = toPx(g.Stations[g.Stations.Length - 2], g.Stations[g.Stations.Length - 1]);
            img.Disc(hx, hy, 6, 20, 20, 20); img.Disc(hx, hy, 4, 255, 255, 255);
            img.Disc(tx, ty, 6, 20, 20, 20);
            img.Save(Out(file));
            const double Yd3 = 1.307950619;
            return $"{s.Name} ({(graded ? "Graded" : "Natural")}): {g.LengthMetres / 0.3048:F0} ft, vertical {rating.DropMetres / 0.3048:F0} ft, " +
                   $"rating {rating.ValuePercent:F0}% {SlopeBands.Names[(int)rating.Band]} (mean {rating.MeanPercent:F0}%, steepest {rating.MaxPercent:F0}%), " +
                   $"cut {g.CutCubicMetres * Yd3:F0} yd³, fill {g.FillCubicMetres * Yd3:F0} yd³, ungraded {g.UngradedMetres / 0.3048:F0} ft, " +
                   $"{g.ClearedCells} cells cleared; graded in {ms:F0} ms";
        }

        static readonly Dictionary<string, Spot> Spots = new Dictionary<string, Spot>
        {
            // Across a 45% east-facing slope, heading south at about 30%: mixed widths and an island of trees.
            ["sidehill"] = new Spot { Name = "Sidehill", Path = new double[] { 200, -330, 225, -480, 235, -600, 250, -760 }, WidthsFt = new[] { 100, 220, 100 },
                                      Islands = new[] { (245.0, -540.0, 45) } },
            // Down the crest of a spur running south.
            ["ridge"] = new Spot { Name = "Ridge", Path = new double[] { 700, 330, 705, 180, 700, 20 }, WidthsFt = new[] { 120 } },
            // Down the gully beside it.
            ["gully"] = new Spot { Name = "Gully", Path = new double[] { 520, 260, 560, 110, 610, -60 }, WidthsFt = new[] { 120 } },
            // A traverse across a 70-80% face: too steep for a bench in places (GR-07, red).
            ["steep"] = new Spot { Name = "Steep face", Path = new double[] { -650, 380, -660, 500, -650, 640 }, WidthsFt = new[] { 150 } },
        };

        [Test]
        public void Timing()
        {
            var (h, site, m) = Open();
            using (h)
            {
                var s = Spots["sidehill"];
                var strokes = new List<Stroke>();
                for (int k = 0; k + 1 < s.Path.Length / 2; k++)
                    strokes.Add(new Stroke(DesignUnits.FeetToMm(s.WidthsFt[k]), false, new[] { PointMm.FromMetres(s.Path[2 * k], s.Path[2 * k + 1]), PointMm.FromMetres(s.Path[2 * k + 2], s.Path[2 * k + 3]) }));
                var parts = new[] { new FootprintPart(strokes.ToArray()) };
                var ground = new EditedHeights(h, site.Lattice, TerrainEdits.Empty);
                for (int run = 0; run < 3; run++)
                {
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    var fp = TrailFootprint.Build(parts, site.Lattice, site.OriginX, site.OriginY);
                    double a = w.Elapsed.TotalMilliseconds;
                    site.PointOf(PointMm.FromMetres(s.Path[0], s.Path[1]), out double hx, out double hy);
                    site.PointOf(PointMm.FromMetres(s.Path[s.Path.Length - 2], s.Path[s.Path.Length - 1]), out double tx, out double ty);
                    var line = TrailCentreline.Find(fp, hx, hy, tx, ty);
                    double b = w.Elapsed.TotalMilliseconds;
                    var g = TrailGrader.GradeFootprint(ground, fp, line.Points, true, 1, TrailId.Make(1, 1));
                    double c = w.Elapsed.TotalMilliseconds;
                    TestContext.Progress.WriteLine($"footprint {a:F1} ms ({fp.Width}x{fp.Height}), centreline {b - a:F1} ms ({line.Points.Length / 2} pts), grade {c - b:F1} ms");
                }
            }
        }

        [Test]
        public void Trails()
        {
            var (h, site, m) = Open();
            using (h)
            {
                var lines = new List<string>();
                foreach (var kv in Spots)
                {
                    lines.Add(Draw(h, site, kv.Value, true, kv.Key + "-graded.png"));
                    lines.Add(Draw(h, site, kv.Value, false, kv.Key + "-natural.png"));
                }
                File.WriteAllLines(Out("trails.txt"), lines);
                foreach (var l in lines) TestContext.Progress.WriteLine(l);
            }
        }
    }
}
