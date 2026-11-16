using System;
using System.IO;
using MountainPlanner.Domain;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task 4.07 PR A2 probe: where the shed and the drift land on real ground (the Jackson Hole 2 km package).</summary>
    [Explicit, Category("SnowReport")]
    public sealed class SnowRunoutProbeTests
    {
        const int W = 256, D = 256;
        static void Say(string s) { TestContext.Progress.WriteLine(s); }

        internal static SnowFieldTests.Mountain JacksonHole()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(f));
            return Crop(dir);
        }

        /// <summary>The package's heights as one tile of posts 2 m apart over the crop, edge to edge (row 0 at its south edge).</summary>
        readonly struct CropPosts : ITileSamples
        {
            readonly int[] _mm;
            public CropPosts(int resolution, int[] mm) { Resolution = resolution; _mm = mm; }
            public int Resolution { get; }
            public int Value(int column, int row) => _mm[row * Resolution + column];
        }

        /// <summary>The 2 km round a package's centre at 8 m through the game's own sampler (SN-01 v2's <see cref="SurfaceSampler"/>,
        /// as the opener and the edits take it): the posts 2 m apart, each cell their mean, edges split fairly; no canopy.
        /// <paramref name="nearest"/> keeps the old one sample at the centre. <paramref name="east"/> and <paramref name="north"/>
        /// (metres) move the crop's centre off the site's, into the ring (task 4.07 PR A2's valley probe).</summary>
        internal static SnowFieldTests.Mountain Crop(string dir, bool nearest = false, double east = 0, double north = 0)
        {
            var man = ResortPackage.ReadManifest(dir);
            var m = new SnowFieldTests.Mountain { Mm = new int[W * D], Feet = new int[W * D], Canopy = new byte[W * D] };
            double cx = man.Site.CentreX + east, cy = man.Site.CentreY + north, west = cx - W * 4, south = cy - D * 4;
            using (var h = new PackageHeights(dir, man))
            {
                if (nearest)
                    for (int j = 0; j < D; j++)
                        for (int i = 0; i < W; i++) m.Mm[j * W + i] = (int)Math.Round(h.At(west + i * 8 + 4, south + j * 8 + 4) * 1000);
                else
                {
                    int res = W * 4 + 1;   // a post every 2 m, both edges included
                    var posts = new int[res * res];
                    for (int r = 0; r < res; r++)
                        for (int c = 0; c < res; c++) posts[r * res + c] = (int)Math.Round(h.At(west + c * 2, south + r * 2) * 1000);
                    var sampler = new SurfaceSampler(W, D);
                    sampler.AddPosts(new CropPosts(res, posts), 0, 0, W * 8000L, true, true, 0, 0, W, D);
                    sampler.Heights(m.Mm);
                }
            }
            for (int c = 0; c < W * D; c++) m.Feet[c] = (int)Math.Round(m.Mm[c] / 304.8);
            var p = Pacing.Default;
            m.Weather = WeatherTerrain.Build(W, D, m.Feet, -W / 2 * 8000, -D / 2 * 8000, (W / 5, D / 5, W * 4 / 5, D * 4 / 5), D / 2 * W + W / 2,
                                             SimWeather.TerrainTuning(p));
            m.Snow = SnowTerrain.Build(W, D, m.Mm, m.Canopy, m.Weather, (int[])p.WindExposure.Clone());
            return m;
        }

        static int Grade(SnowTerrain t, int c)
        {
            long x = t.NormalX[c], y = t.NormalY[c], s = x * x + y * y, up = 127L * 127 - s;
            return up <= 0 ? 100_000 : (int)Math.Min(100_000, Math.Sqrt((double)s / up) * 100);
        }

        [Test]
        public void WhereTheShedLands()
        {
            string probeDir = Environment.GetEnvironmentVariable("MP_PROBE_DIR"), probeRows = Environment.GetEnvironmentVariable("MP_PROBE_ROWS");
            var m = string.IsNullOrEmpty(probeDir) ? JacksonHole() : Crop(probeDir);
            var t = m.Snow;
            var p = Pacing.Default;
            int steps = Math.Max(1, p.DriftReach / 8);
            var catchment = new double[W * D];
            int shedders = 0;
            for (int c = 0; c < W * D; c++)
            {
                int g = Grade(t, c);
                if (g <= p.ShedStartGrade) continue;
                double share = g >= p.ShedFullGrade ? 1 : (g - p.ShedStartGrade) / (double)(p.ShedFullGrade - p.ShedStartGrade);
                shedders++;
                int x = c % W, y = c / W, at = c;
                for (int s = 0; s < steps; s++)
                {
                    int hx = t.NormalX[at], hy = t.NormalY[at], ax = Math.Abs(hx), ay = Math.Abs(hy);
                    int dx = ax * 5 >= ay * 2 ? Math.Sign(hx) : 0, dy = ay * 5 >= ax * 2 ? Math.Sign(hy) : 0;
                    if (dx == 0 && dy == 0) break;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= W || ny >= D) break;
                    x = nx; y = ny; at = ny * W + nx;
                    if (Grade(t, at) < p.ShedStartGrade) break;
                }
                catchment[at] += share;
            }
            var order = new int[W * D];
            for (int c = 0; c < order.Length; c++) order[c] = c;
            Array.Sort(order, (a, b) => catchment[b].CompareTo(catchment[a]));
            Say($"[runout] {shedders} cells shed ({shedders * 100.0 / (W * D):F1}%); the top receivers (cells' worth of snowfall):");
            for (int k = 0; k < 12; k++)
            {
                int c = order[k];
                Say($"[runout]   {c % W},{c / W}: {catchment[c]:F1} cells, grade {Grade(t, c)}%, rise {t.Rise[c]}, {m.Feet[c]} ft");
            }
            int over10 = 0, over30 = 0;
            foreach (double v in catchment) { if (v > 10) over10++; if (v > 30) over30++; }
            Say($"[runout] receivers over 10 cells: {over10}, over 30: {over30}");

            // Then a month of real weather on it: the deepest cells.
            SimWorld w;
            if (string.IsNullOrEmpty(probeRows)) w = SnowFieldTests.World(p, true, m);
            else
            {
                w = new SimWorld(p, SeasonRunner.GoldenDesign(1, false), new SimSite(39.19, -120.25), 2026,
                                 weather: SimWeather.Own(SimWeatherTests.Frozen(probeRows)));
                SnowFieldTests.SendInputs(w, m);
            }
            string until = Environment.GetEnvironmentVariable("MP_PROBE_UNTIL");
            int um = 11, ud = 15;
            if (!string.IsNullOrEmpty(until)) { var u = until.Split('/'); um = int.Parse(u[0]); ud = int.Parse(u[1]); }
            SnowFieldTests.RunTo(w, um, ud, p.IdleTickFactor);
            var deep = new int[W * D];
            for (int c = 0; c < deep.Length; c++) deep[c] = c;
            Array.Sort(deep, (a, b) => w.Snow[b % W, b / W].CompareTo(w.Snow[a % W, a / W]));
            double mean = 0;
            for (int c = 0; c < W * D; c++) mean += w.Snow[c % W, c / W];
            Say($"[runout] Nov 15: mean {mean / (W * D) / 25.4:F1} in; the deepest:");
            for (int k = 0; k < 12; k++)
            {
                int c = deep[k];
                int nb = 0; long nsum = 0;
                for (int b = -2; b <= 2; b++) for (int a = -2; a <= 2; a++)
                {
                    int x = c % W + a, y = c / W + b;
                    if (x < 0 || y < 0 || x >= W || y >= D || (a == 0 && b == 0)) continue;
                    nb++; nsum += w.Snow[x, y];
                }
                Say($"[runout]   {c % W},{c / W}: {w.Snow[c % W, c / W] / 25.4:F0} in (5x5 round it {nsum / (double)nb / 25.4:F0} in), shed catchment {catchment[c]:F1}, grade {Grade(t, c)}%, rise {t.Rise[c]}, {m.Feet[c]} ft, normal {t.NormalX[c]},{t.NormalY[c]}, type {w.Snow.TypeAt(c % W, c / W)}");
            }
        }
    }
}
