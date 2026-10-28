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
                                  TrailShape.FromStrokes(null, strokes), head, tail);
            return DrawTrail(h, site, trail, graded, file);
        }

        static string DrawTrail(IHeightSource h, EarthworkSite site, Trail trail, bool graded, string file, TrailGrader.Options options = null)
        {
            var s = new Spot { Name = trail.Name };
            var ground = new EditedHeights(h, site.Lattice, TerrainEdits.Empty);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var build = TrailEarthwork.Grade(trail, ground, site, 1, null, null, options);
            double ms = watch.Elapsed.TotalMilliseconds;
            var tiles = new List<TerrainEditTile>();
            foreach (var key in build.Writes.Tiles) { var b = TerrainEditTile.Empty(key).Edit(); build.Writes.ApplyTo(b); tiles.Add(b.Build()); }
            var after = new EditedHeights(h, site.Lattice, TerrainEdits.Empty.With(tiles));
            var fp = build.Footprint;
            var g = build.Grade;
            var rating = TrailRating.Rate(after, fp, g);

            // The footprint's box, in local metres.
            double minX = fp.CellX(fp.Column0) - site.OriginX, maxX = fp.CellX(fp.Column0 + fp.Width) - site.OriginX;
            double maxY = fp.CellY(fp.Row0) - site.OriginY, minY = fp.CellY(fp.Row0 + fp.Height) - site.OriginY;
            double size = Math.Max(maxX - minX, maxY - minY) + 60;
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
            // Down the crest of the most convex spur on the 2 km area (FindRidgesAndGullies), on its fall line.
            ["ridge"] = new Spot { Name = "Ridge", Path = new double[] { 183, 790, 280, 720, 377, 650 }, WidthsFt = new[] { 120 } },
            // Down the most concave gully, on its fall line.
            ["gully"] = new Spot { Name = "Gully", Path = new double[] { 271, -78, 360, -160, 449, -242 }, WidthsFt = new[] { 120 } },
            // A traverse across a 70-80% face: too steep for a bench in places (GR-07, red).
            ["steep"] = new Spot { Name = "Steep face", Path = new double[] { -650, 380, -660, 500, -650, 640 }, WidthsFt = new[] { 150 } },
        };

        [Test]
        public void FindRidgesAndGullies()
        {
            var (h, site, m) = Open();
            using (h)
            {
                var found = new List<(double score, string line)>();
                for (int y = -800; y <= 800; y += 40)
                    for (int x = -800; x <= 800; x += 40)
                    {
                        double px = site.OriginX + x, py = site.OriginY + y;
                        double gx = (h.At(px + 20, py) - h.At(px - 20, py)) / 40, gy = (h.At(px, py + 20) - h.At(px, py - 20)) / 40;
                        double g = Math.Sqrt(gx * gx + gy * gy);
                        if (g < 0.25 || g > 0.5) continue;
                        double fx = -gx / g, fy = -gy / g, nx = -fy, ny = fx;
                        // Cross curvature averaged over 150 m down the fall line.
                        double curv = 0;
                        for (int t = -75; t <= 75; t += 25)
                        {
                            double cx = px + fx * t, cy = py + fy * t;
                            curv += h.At(cx + nx * 30, cy + ny * 30) + h.At(cx - nx * 30, cy - ny * 30) - 2 * h.At(cx, cy);
                        }
                        curv /= 7;
                        found.Add((curv, $"{x},{y} fall ({fx:F2},{fy:F2}) grade {g * 100:F0}% curvature {curv:F1} m"));
                    }
                found.Sort((a, b) => a.score.CompareTo(b.score));
                var lines = new List<string> { "ridges (most convex):" };
                for (int i = 0; i < 6; i++) lines.Add(found[i].line);
                lines.Add("gullies (most concave):");
                for (int i = found.Count - 1; i >= found.Count - 6; i--) lines.Add(found[i].line);
                File.WriteAllLines(Out("ridges-gullies.txt"), lines);
            }
        }

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
                var parts = TrailShape.FromStrokes(null, strokes);
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
        public void TimingOneKilometre()
        {
            var (h, site, m) = Open();
            using (h)
            {
                // Down the fall line for 1 km from the NE of the area, 100 ft wide, graded.
                var pts = new List<PointMm>();
                double x = 300, y = 700;
                for (int k = 0; k <= 40; k++)
                {
                    pts.Add(PointMm.FromMetres(x, y));
                    double ax = site.OriginX + x, ay = site.OriginY + y;
                    double gx = h.At(ax + 5, ay) - h.At(ax - 5, ay), gy = h.At(ax, ay + 5) - h.At(ax, ay - 5), g = Math.Sqrt(gx * gx + gy * gy);
                    x -= 25 * gx / g; y -= 25 * gy / g;
                }
                var trail = new Trail(TrailId.Make(1, 1), "Bench", WorkStage.Built, TrailSettings.GradedRun,
                                      TrailShape.FromStrokes(null, new[] { new Stroke(DesignUnits.FeetToMm(100), false, pts.ToArray()) }), pts[0], pts[pts.Count - 1]);
                var doc = DesignDocument.Empty(1);
                var r = new AddTrail(1, "Bench", TrailSettings.GradedRun, trail.Shape, trail.Head, trail.Tail, WorkStage.Built).Apply(doc);
                var micro = System.Diagnostics.Stopwatch.StartNew();
                double sum = 0;
                for (int q = 0; q < 100000; q++) sum += h.At(site.OriginX + 300 + (q % 300), site.OriginY + 600 - q / 300);
                TestContext.Progress.WriteLine($"100k package reads: {micro.Elapsed.TotalMilliseconds:F1} ms ({sum:F0})");
                for (int run = 0; run < 3; run++)
                {
                    var w = System.Diagnostics.Stopwatch.StartNew();
                    var work = new DesignEarthwork(h, site);
                    work.Prime(doc, TerrainEdits.Empty);
                    var rebuilt = work.Rebuild(r.Document, TerrainEdits.Empty);
                    double a = w.Elapsed.TotalMilliseconds;
                    var ground = new EditedHeights(h, site.Lattice, TerrainEdits.Empty);
                    var pending = new EditedHeights(h, site.Lattice, rebuilt.Edits);
                    var seg = new List<double>();
                    PendingContours.Build(ground, pending, rebuilt.Changed, 3.048, seg);
                    double b = w.Elapsed.TotalMilliseconds;
                    var build = rebuilt.DetailOf(r.Created[0]) as TrailBuild;
                    TrailRating.Rate(pending, build.Footprint, build.Grade);
                    double c = w.Elapsed.TotalMilliseconds;
                    w.Restart();
                    var fp = TrailFootprint.Build(trail.Shape, site.Lattice, site.OriginX, site.OriginY);
                    double f1 = w.Elapsed.TotalMilliseconds;
                    site.PointOf(trail.Head.Value, out double hx, out double hy);
                    site.PointOf(trail.Tail.Value, out double tx, out double ty);
                    var cl = TrailCentreline.Find(fp, hx, hy, tx, ty);
                    double f2 = w.Elapsed.TotalMilliseconds;
                    var gg = TrailGrader.GradeFootprint(ground, fp, cl.Points, true, 1, trail.Id);
                    double f3 = w.Elapsed.TotalMilliseconds;

                    TestContext.Progress.WriteLine($"  footprint {f1:F1} ({fp.Width}x{fp.Height}), centreline {f2 - f1:F1}, grade {f3 - f2:F1}");
                    TestContext.Progress.WriteLine($"rebuild {a:F1} ms ({rebuilt.Tiles.Count} tiles, {rebuilt.Changed.Count} changed, {build.Grade.LengthMetres:F0} m), contours {b - a:F1} ms, rating {c - b:F1} ms");
                }
            }
        }

        [Test]
        public void TimingCoverRegions()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string g in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(g));
            var m = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
            using var lazy = new PackageHeights(dir, m);
            var cover = new CoverField(m, dir, lazy);
            tiles.TryTileAt(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), out var key);
            var box = tiles.Bounds(key);
            int res = TerrainCache.CoreCoverResolution;
            var region = new byte[90 * 90 * TerrainCache.CoverBands];
            for (int run = 0; run < 3; run++)
            {
                var w = System.Diagnostics.Stopwatch.StartNew();
                for (int k = 0; k < 38; k++)
                {
                    int i0 = (k * 23) % (res - 90), j0 = (k * 37) % (res - 90);
                    cover.BuildRegion(box, res, i0, j0, i0 + 90, j0 + 90, lazy, null, region);
                }
                TestContext.Progress.WriteLine($"38 regions of 90x90 texels: {w.Elapsed.TotalMilliseconds:F1} ms (res {res})");
            }
        }

        /// <summary>
        /// The owner's question (2026-10-20, "can we get even better? would more side cut help?"): each graded trail of a
        /// save graded several ways, with how smooth it skis along the centreline (the grade change between consecutive
        /// 5 m pieces), how much of the paint is level bench, and the earth moved. Same environment as OwnersTrails.
        /// </summary>
        [Test]
        public void OwnersTrailVariants()
        {
            string save = Environment.GetEnvironmentVariable("MP_OWNER_SAVE"), dir = Environment.GetEnvironmentVariable("MP_OWNER_PACKAGE");
            if (string.IsNullOrEmpty(save) || string.IsNullOrEmpty(dir)) Assert.Ignore("set MP_OWNER_SAVE and MP_OWNER_PACKAGE");
            var file = MountainPlanner.Persistence.Saves.SaveJson.Read(File.ReadAllText(Path.Combine(save, "save.json")), out _);
            var m = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
            var site = new EarthworkSite(new EditLattice(tiles.West, tiles.North), m.Site.CentreX, m.Site.CentreY);
            var variants = new (string name, TrailGrader.Options o)[]
            {
                ("10% side slope, ground profile", new TrailGrader.Options { CrossSlopeCap = 0.10, ProfileSmoothStations = 0 }),
                ("the rules (TL23)", new TrailGrader.Options()),
                ("20% side slope", new TrailGrader.Options { CrossSlopeCap = 0.20 }),
                ("profile smoothed 75 ft", new TrailGrader.Options { ProfileSmoothStations = 3 }),
                ("profile smoothed 150 ft", new TrailGrader.Options { ProfileSmoothStations = 6 }),
                ("both: 20% + 150 ft", new TrailGrader.Options { CrossSlopeCap = 0.20, ProfileSmoothStations = 6 }),
            };
            var lines = new List<string>();
            using (var h = new PackageHeights(dir, m))
            {
                var ground = new EditedHeights(h, site.Lattice, TerrainEdits.Empty);
                foreach (var t in file.Design.Trails)
                {
                    if (t.Settings.Grading != TrailGrading.Graded || !t.HasEnds) continue;
                    bool first = true;
                    foreach (var (name, o) in variants)
                    {
                        var build = TrailEarthwork.Grade(t, ground, site, 1, null, null, o);
                        var list = new List<TerrainEditTile>();
                        foreach (var key in build.Writes.Tiles) { var b = TerrainEditTile.Empty(key).Edit(); build.Writes.ApplyTo(b); list.Add(b.Build()); }
                        var after = new EditedHeights(h, site.Lattice, TerrainEdits.Empty.With(list));
                        var line = build.Centreline.Points;
                        if (first)
                        {
                            var (nr, nb) = Rolls(ground, line);
                            lines.Add($"{t.Name}: natural ground along the centreline: rolls {nr:F1}% RMS, {nb} grade changes over 5 points");
                            first = false;
                        }
                        var (rms, breaks) = Rolls(after, line);
                        var g = build.Grade;
                        double paint = 0, bench = 0;
                        for (int k = 0; k < g.Benched.Length; k++) { bench += g.BenchLeft[k] + g.BenchRight[k]; }
                        paint = build.Footprint.AreaSquareMetres / Math.Max(1, g.LengthMetres);
                        const double Yd3 = 1.307950619;
                        lines.Add($"  {name,-24} rolls {rms,4:F1}% RMS, {breaks,3} changes over 5 points; bench {100 * (bench / Math.Max(1, g.Benched.Length)) / Math.Max(1, paint),3:F0}% of the paint; " +
                                  $"cut {g.CutCubicMetres * Yd3,7:F0} yd³, fill {g.FillCubicMetres * Yd3,7:F0} yd³; ungraded {g.UngradedMetres / 0.3048:F0} ft");
                        if (name.StartsWith("both")) DrawTrail(h, site, t, true, "variant-" + t.Id.Value.ToString("x16") + ".png", o);
                    }
                }
            }
            File.WriteAllLines(Out("owner-variants.txt"), lines);
            foreach (var l in lines) TestContext.Progress.WriteLine(l);
        }

        /// <summary>
        /// The owner's ask (2026-10-20): an adversarial check that the graded surface is as smooth as it can look. Every
        /// changed lattice point's sharpest bend (the largest second difference across it, in m per m²), as statistics and
        /// a kink map: a rounded 1:1 toe or crest (GR-12, 10 ft) bends about 0.33; anything well above is an unrounded
        /// crease, and on the bench itself (under 35% slope) anything above 0.05 is a bump. The owner's graded trails (as
        /// OwnersTrails) and the four review spots on the 2 km package.
        /// </summary>
        [Test]
        public void SmoothnessAudit()
        {
            var lines = new List<string>();
            string save = Environment.GetEnvironmentVariable("MP_OWNER_SAVE"), dir = Environment.GetEnvironmentVariable("MP_OWNER_PACKAGE");
            if (!string.IsNullOrEmpty(save) && !string.IsNullOrEmpty(dir))
            {
                var file = MountainPlanner.Persistence.Saves.SaveJson.Read(File.ReadAllText(Path.Combine(save, "save.json")), out _);
                var m = ResortPackage.ReadManifest(dir);
                var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
                var site = new EarthworkSite(new EditLattice(tiles.West, tiles.North), m.Site.CentreX, m.Site.CentreY);
                using (var h = new PackageHeights(dir, m))
                    foreach (var t in file.Design.Trails)
                        if (t.Settings.Grading == TrailGrading.Graded && t.HasEnds) lines.Add(Audit(h, site, t, "audit-" + t.Name.Length + "-" + t.Id.Value.ToString("x16")));
            }
            var (ph, psite, _) = Open();
            using (ph)
                foreach (var kv in Spots)
                {
                    var sp = kv.Value;
                    var strokes = new List<Stroke>();
                    int n = sp.Path.Length / 2;
                    for (int k = 0; k + 1 < n; k++)
                    {
                        int ft = sp.WidthsFt.Length == 1 ? sp.WidthsFt[0] : sp.WidthsFt[k];
                        strokes.Add(new Stroke(DesignUnits.FeetToMm(ft), false, new[] { PointMm.FromMetres(sp.Path[2 * k], sp.Path[2 * k + 1]), PointMm.FromMetres(sp.Path[2 * k + 2], sp.Path[2 * k + 3]) }));
                    }
                    foreach (var (x, y, ft) in sp.Islands) strokes.Add(new Stroke(DesignUnits.FeetToMm(ft), true, new[] { PointMm.FromMetres(x, y) }));
                    var trail = new Trail(TrailId.Make(1, 1), sp.Name, WorkStage.Built, TrailSettings.GradedRun, TrailShape.FromStrokes(null, strokes),
                                          PointMm.FromMetres(sp.Path[0], sp.Path[1]), PointMm.FromMetres(sp.Path[2 * n - 2], sp.Path[2 * n - 1]));
                    lines.Add(Audit(ph, psite, trail, "audit-" + kv.Key));
                }
            File.WriteAllLines(Out("smoothness-audit.txt"), lines);
            foreach (var l in lines) TestContext.Progress.WriteLine(l);
        }

        static string Audit(IHeightSource h, EarthworkSite site, Trail trail, string file)
        {
            var natural = new EditedHeights(h, site.Lattice, TerrainEdits.Empty);
            var build = TrailEarthwork.Grade(trail, natural, site, 1, null, null);
            var list = new List<TerrainEditTile>();
            foreach (var key in build.Writes.Tiles) { var b = TerrainEditTile.Empty(key).Edit(); build.Writes.ApplyTo(b); list.Add(b.Build()); }
            var after = new EditedHeights(h, site.Lattice, TerrainEdits.Empty.With(list));
            var fp = build.Footprint;
            int c0 = fp.Column0 - 3, r0 = fp.Row0 - 3, w = fp.Width + 7, hh = fp.Height + 7;
            var kink = new double[w * hh];
            var slope = new double[w * hh];
            var changed = new bool[w * hh];
            var all = new List<double>();
            var bench = new List<double>();
            var worst = new List<(double k, int c, int r, double slope, double clear, double delta)>();
            for (int j = 1; j < hh - 1; j++)
                for (int i = 1; i < w - 1; i++)
                {
                    int c = c0 + i, r = r0 + j;
                    double z = after.AtPoint(c, r);
                    double d = z - natural.AtPoint(c, r);
                    if (Math.Abs(d) < 0.01) continue;
                    double ex = after.AtPoint(c + 1, r), wx = after.AtPoint(c - 1, r), no = after.AtPoint(c, r - 1), so = after.AtPoint(c, r + 1);
                    double ne = after.AtPoint(c + 1, r - 1), sw = after.AtPoint(c - 1, r + 1), nw = after.AtPoint(c - 1, r - 1), se = after.AtPoint(c + 1, r + 1);
                    double k = Math.Max(Math.Max(Math.Abs(ex - 2 * z + wx), Math.Abs(no - 2 * z + so)), Math.Max(Math.Abs(ne - 2 * z + sw), Math.Abs(nw - 2 * z + se)) / 2);
                    double sl = Math.Sqrt((ex - wx) * (ex - wx) + (no - so) * (no - so)) / 2;
                    int at = j * w + i;
                    kink[at] = k; slope[at] = sl; changed[at] = true;
                    all.Add(k);
                    if (sl < 0.35) bench.Add(k);
                    worst.Add((k, c, r, sl, fp.PointClearance(c, r), d));
                }
            all.Sort(); bench.Sort();
            worst.Sort((a, b) => b.k.CompareTo(a.k));
            double P(List<double> v, double q) => v.Count == 0 ? 0 : v[Math.Min(v.Count - 1, (int)(q * v.Count))];
            int sharp = all.FindAll(v => v > 0.5).Count, bumps = bench.FindAll(v => v > 0.05).Count;
            // The kink map: grey relief, changed points from green (smooth) through yellow (0.33, a rounded toe) to red (1+).
            int scale = Math.Max(1, 900 / Math.Max(w, hh));
            var img = new Rgb(w * scale, hh * scale);
            for (int j = 0; j < hh; j++)
                for (int i = 0; i < w; i++)
                {
                    int at = j * w + i, c = c0 + i, r = r0 + j;
                    double rr, gg, bb;
                    if (changed[at])
                    {
                        double t = Math.Min(1, kink[at] / 1.0);
                        if (t < 0.33) { rr = 60 + 500 * t; gg = 170; bb = 60; } else { rr = 230; gg = 170 * (1 - (t - 0.33) / 0.67); bb = 40; }
                    }
                    else
                    {
                        double s2 = j > 0 && i > 0 && j < hh - 1 && i < w - 1 ? (after.AtPoint(c + 1, r) - after.AtPoint(c - 1, r)) * 40 : 0;
                        rr = gg = bb = Math.Max(40, Math.Min(220, 130 + s2));
                    }
                    bool edge = fp.Contains(c, r) != fp.Contains(c + 1, r) || fp.Contains(c, r) != fp.Contains(c, r + 1);
                    if (edge) { rr = gg = bb = 255; }
                    for (int y = 0; y < scale; y++) for (int x = 0; x < scale; x++) img.Set(i * scale + x, j * scale + y, rr, gg, bb);
                }
            img.Save(Out(file + ".png"));
            var sb = new System.Text.StringBuilder();
            sb.Append($"{trail.Name}: {all.Count:N0} changed points; bend p50 {P(all, 0.5):F3}, p90 {P(all, 0.9):F3}, p99 {P(all, 0.99):F3}, max {(all.Count > 0 ? all[all.Count - 1] : 0):F2} m/m²; ");
            sb.Append($"{sharp} sharper than 0.5 ({100.0 * sharp / Math.Max(1, all.Count):F2}%); bench {bench.Count:N0} points, p90 {P(bench, 0.9):F3}, {bumps} bumps over 0.05 ({100.0 * bumps / Math.Max(1, bench.Count):F1}%)");
            for (int i = 0; i < Math.Min(8, worst.Count); i++)
            {
                var x = worst[i];
                sb.Append($"\n    worst {x.k:F2} at ({x.c - fp.Column0}, {x.r - fp.Row0}) slope {x.slope:P0}, {x.clear:F1} m from the paint edge, change {x.delta:+0.00;-0.00} m");
            }
            return sb.ToString();
        }

        /// <summary>How the ground rolls along a line: the grade over each 5 m piece, then the RMS of the change from one piece to the next, and how many changes pass 5 points.</summary>
        static (double rms, int breaks) Rolls(IHeightSource h, double[] line)
        {
            var heights = new List<double>();
            double carry = 0;
            for (int k = 0; k + 3 < line.Length; k += 2)
            {
                double ax = line[k], ay = line[k + 1], bx = line[k + 2], by = line[k + 3];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                for (double d = carry; d < len; d += 5) heights.Add(h.At(ax + (bx - ax) * d / len, ay + (by - ay) * d / len));
                carry = (carry - len) % 5;
                if (carry < 0) carry += 5;
            }
            double sum = 0;
            int n = 0, breaks = 0;
            for (int i = 2; i < heights.Count; i++)
            {
                double g0 = (heights[i - 1] - heights[i - 2]) / 5, g1 = (heights[i] - heights[i - 1]) / 5, dg = (g1 - g0) * 100;
                sum += dg * dg; n++;
                if (Math.Abs(dg) > 5) breaks++;
            }
            return (n > 0 ? Math.Sqrt(sum / n) : 0, breaks);
        }

        /// <summary>
        /// The owner's own trails (2026-10-19 reports: narrow benches, steps on traverses): every graded trail of a save,
        /// graded alone on its package's natural ground. MP_OWNER_SAVE is the save's folder, MP_OWNER_PACKAGE the package's.
        /// </summary>
        [Test]
        public void OwnersTrails()
        {
            string save = Environment.GetEnvironmentVariable("MP_OWNER_SAVE"), dir = Environment.GetEnvironmentVariable("MP_OWNER_PACKAGE");
            if (string.IsNullOrEmpty(save) || string.IsNullOrEmpty(dir)) Assert.Ignore("set MP_OWNER_SAVE and MP_OWNER_PACKAGE");
            var file = MountainPlanner.Persistence.Saves.SaveJson.Read(File.ReadAllText(Path.Combine(save, "save.json")), out _);
            var m = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
            var site = new EarthworkSite(new EditLattice(tiles.West, tiles.North), m.Site.CentreX, m.Site.CentreY);
            using (var h = new PackageHeights(dir, m))
            {
                var lines = new List<string>();
                foreach (var t in file.Design.Trails)
                {
                    if (t.Settings.Grading != TrailGrading.Graded || !t.HasEnds) continue;
                    string name = "owner-" + t.Id.Value.ToString("x16");
                    lines.Add(DrawTrail(h, site, t, true, name + "-graded.png"));
                }
                File.WriteAllLines(Out("owner-trails.txt"), lines);
                foreach (var l in lines) TestContext.Progress.WriteLine(l);
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
