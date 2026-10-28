using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Saves;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MountainPlanner.Tests.Core
{
    /// <summary>
    /// Save v8 (sections finding 7), for the owner before the change: what an older save of theirs becomes. Each trail's
    /// footprint by the strokes' rule (v7) against its clearing's (v8), the outline's points against the strokes', and what
    /// the one-time regrade on opening moves. Set MP_OWNER_SAVE (a copy of a save folder) and MP_OWNER_PACKAGE; writes
    /// shape-report.txt beside the save.
    /// </summary>
    [Explicit("The owner's save: set MP_OWNER_SAVE and MP_OWNER_PACKAGE")]
    public sealed class TrailShapeReport
    {
        [Test]
        public void OwnersSaveInVersionEight()
        {
            string save = Environment.GetEnvironmentVariable("MP_OWNER_SAVE"), dir = Environment.GetEnvironmentVariable("MP_OWNER_PACKAGE");
            if (string.IsNullOrEmpty(save) || string.IsNullOrEmpty(dir)) Assert.Ignore("set MP_OWNER_SAVE and MP_OWNER_PACKAGE");
            string json = File.ReadAllText(Path.Combine(save, SaveFile.FileName));
            var raw = JObject.Parse(json);
            var readWatch = System.Diagnostics.Stopwatch.StartNew();
            var file = SaveStore.Read(save);
            double readMs = readWatch.Elapsed.TotalMilliseconds;
            var m = ResortPackage.ReadManifest(dir);
            var tiles = TileGrid.For(SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0));
            var site = new EarthworkSite(new EditLattice(tiles.West, tiles.North), m.Site.CentreX, m.Site.CentreY);
            var lines = new List<string>
            {
                $"Save v{file.MigratedFrom} → v{SaveFile.CurrentVersion}: {file.Design.Trails.Count} trails, save.json {json.Length:N0} bytes",
                "trail | stage | strokes' points | outline points (rings) | footprint m² v7 → v8 | cells that differ",
            };
            int strokePoints = 0, outlinePoints = 0, differ = 0;
            foreach (JObject t in (JArray)raw["Design"]["Trails"])
            {
                var parts = t["Parts"] != null ? SaveJson.ReadParts(t) : null;
                var trail = file.Design.FindTrail(new TrailId(Convert.ToUInt64((string)t["Id"], 16)));
                var now = TrailFootprint.Build(trail.Shape, site.Lattice, site.OriginX, site.OriginY);
                int points = parts?.Sum(p => p.Strokes.Sum(s => s.Points.Count)) ?? 0;
                int cellsOld = 0, cellsDiffer = 0;
                if (parts != null)
                {
                    // The v7 rule: each part's strokes in order at the 1 m cell centres, clipped by its cuts, unioned.
                    for (int r = now.Row0 - 30; r <= now.LastRow + 30; r++)
                        for (int c = now.Column0 - 30; c <= now.LastColumn + 30; c++)
                        {
                            double x = site.Lattice.West + c + 0.5 - site.OriginX, y = site.Lattice.North - r - 0.5 - site.OriginY;
                            bool on = false;
                            foreach (var part in parts)
                            {
                                bool inPart = false;
                                foreach (var s in part.Strokes)
                                    for (int k = 0; k < Math.Max(1, s.Points.Count - 1); k++)
                                        if (InCapsule(x, y, s.Points[k], s.Points[Math.Min(k + 1, s.Points.Count - 1)], s.WidthMm / 2000.0)) inPart = !s.Erase;
                                foreach (var cut in part.Cuts) if (!cut.Keeps(x, y)) inPart = false;
                                on |= inPart;
                            }
                            if (on) cellsOld++;
                            if (on != now.Contains(c, r)) cellsDiffer++;
                        }
                }
                strokePoints += points;
                outlinePoints += trail.Shape.Points;
                differ += cellsDiffer;
                lines.Add($"{trail.Name} | {trail.Stage} | {points} | {trail.Shape.Points} ({trail.Shape.Rings.Count}) | {cellsOld:N0} → {now.Cells:N0} | {cellsDiffer}");
            }
            lines.Add($"all | | {strokePoints:N0} | {outlinePoints:N0} | | {differ}");

            using (var h = new PackageHeights(dir, m))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var fresh = DesignEarthwork.Regrade(h, site, file.Design);
                double ms = watch.Elapsed.TotalMilliseconds;
                var again = DesignEarthwork.Regrade(h, site, file.Design);
                Assert.That(TerrainEditBytes.Encode(again), Is.EqualTo(TerrainEditBytes.Encode(fresh)), "the same save regrades the same way");
                Compare(file.Edits, fresh, out int moved, out double cubic, out int cleared, out int maxMm);
                lines.Add($"regrade on opening: {ms:F0} ms; {moved:N0} lattice points move (largest {maxMm} mm), {cubic:F1} m³ of cut and fill change, {cleared:N0} cleared cells change");
            }
            string folder = Path.Combine(Path.GetTempPath(), "mp-v8-" + Guid.NewGuid().ToString("N"));
            SaveStore.Write(folder, file);
            var w8 = System.Diagnostics.Stopwatch.StartNew();
            var back = SaveStore.Read(folder);
            double read8 = w8.Elapsed.TotalMilliseconds;
            Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(file.Design)), Is.True, "the v8 save reads back the same");
            lines.Add($"written as v8: save.json {new FileInfo(Path.Combine(folder, SaveFile.FileName)).Length:N0} bytes; reading it {read8:F0} ms (the v7 file with its conversion: {readMs:F0} ms)");
            Directory.Delete(folder, true);
            File.WriteAllLines(Path.Combine(save, "shape-report.txt"), lines);
            foreach (var l in lines) TestContext.Progress.WriteLine(l);
        }

        static bool InCapsule(double x, double y, PointMm a, PointMm b, double r)
        {
            double ax = a.XMetres, ay = a.YMetres, dx = b.XMetres - ax, dy = b.YMetres - ay, len2 = dx * dx + dy * dy;
            double t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len2));
            double ex = ax + t * dx - x, ey = ay + t * dy - y;
            return ex * ex + ey * ey <= r * r;
        }

        static void Compare(TerrainEdits a, TerrainEdits b, out int points, out double cubicMetres, out int cells, out int maxMm)
        {
            points = 0; cells = 0; cubicMetres = 0; maxMm = 0;
            var keys = new SortedSet<EditTileKey>();
            foreach (var t in a.Tiles) keys.Add(t.Key);
            foreach (var t in b.Tiles) keys.Add(t.Key);
            foreach (var k in keys)
                for (int j = 0; j < EditTileKey.Side; j++)
                    for (int i = 0; i < EditTileKey.Side; i++)
                    {
                        int c = k.FirstColumn + i, r = k.FirstRow + j;
                        int d = b.DeltaMmAt(c, r) - a.DeltaMmAt(c, r);
                        if (d != 0) { points++; cubicMetres += Math.Abs(d) / 1000.0; maxMm = Math.Max(maxMm, Math.Abs(d)); }
                        if (b.IsClearedAt(c, r) != a.IsClearedAt(c, r)) cells++;
                    }
        }
    }
}
