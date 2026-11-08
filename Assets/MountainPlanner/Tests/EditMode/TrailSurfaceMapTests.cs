using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Presentation.Design;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.01 (TL24): the trail look's pages on the GPU are the CPU fields, point for point (each page's own tile and its
    /// east and south neighbours' first column and row), after a whole build and after a change that sends only some
    /// pages; the page table finds every page; and the terrain shader that reads them compiles.
    /// </summary>
    public sealed class TrailSurfaceMapTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly EarthworkSite Site = new EarthworkSite(Lattice, West + 3000, North - 2000);

        sealed class Rolling : IHeightSource
        {
            public double At(double x, double y)
            {
                double s = North - y;
                return 2600 - 0.3 * s + 0.2 * (x - West) + 3 * Math.Sin(s * 0.02) + 0.6 * Math.Sin(x * 0.09) * Math.Cos(y * 0.05);
            }
        }

        static readonly IHeightSource Ground = new Rolling();

        static PointMm M(double x, double y) => PointMm.FromMetres(x, y);

        static TrailShape Strip(int widthFt, params (double x, double y)[] pts) =>
            TrailShape.FromStrokes(null, new[] { new Stroke(DesignUnits.FeetToMm(widthFt), false, pts.Select(p => M(p.x, p.y)).ToArray()) });

        static void Commit(DesignHistory h, DesignEarthwork work, DesignCommand cmd)
        {
            var after = cmd.Apply(h.Document);
            Assert.That(after.Accepted, Is.True, after.ToString());
            var rebuilt = work.Rebuild(after.Document, h.Terrain);
            Assert.That(h.Submit(cmd, rebuilt.Edits).Accepted, Is.True);
            work.Accept(rebuilt);
        }

        static TrailId Add(DesignHistory h, DesignEarthwork work, TrailShape shape, PointMm head, PointMm tail, bool graded)
        {
            Commit(h, work, new AddTrail(h.Revision, "Run " + (h.Document.Trails.Count + 1), graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun,
                                         shape, head, tail, WorkStage.Built, MountainPlanner.Domain.Measure.SlopeBands.Band.Blue));
            return h.Document.Trails.First(t => t.Shape.Equals(shape)).Id;
        }

        TrailSurfaceMap _map;

        [TearDown]
        public void Release() => _map?.Dispose();

        [Test]
        public void TheTerrainShaderCompiles()
        {
            var shader = Shader.Find("MountainPlanner/Terrain");
            Assert.That(shader, Is.Not.Null, "the terrain shader");
            var errors = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToList();
            Assert.That(errors.Select(e => $"{e.message} ({e.file}:{e.line})"), Is.Empty);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
        }

        [Test]
        public void ThePagesOnTheGpuAreTheFields()
        {
            var h = new DesignHistory(DesignDocument.Empty(5));
            var work = new DesignEarthwork(Ground, Site);
            work.Prime(h.Document, h.Terrain);
            h.Changed += s => work.Follow(s.Document, s.Terrain);
            var near = Add(h, work, Strip(100, (0, 0), (10, -300)), M(0, 0), M(10, -300), graded: true);
            Add(h, work, Strip(80, (-2600, 200), (-2550, -150)), M(-2600, 200), M(-2550, -150), graded: false);   // past the old 2048 m window
            var frame = new LocalFrame(new AlbersPoint(Site.OriginX, Site.OriginY));
            _map = new TrailSurfaceMap();
            TrailBuild Built(Trail t) => work.DetailOf(t.Id.Value) as TrailBuild;
            Assert.That(_map.Rebuild(h.Document, new EditedHeights(Ground, Lattice, h.Terrain), frame, Built), Is.EqualTo(2));
            Check("built");
            Assert.That(_map.Covers(-2590, 100), Is.True, "the far trail is drawn");
            int pages = _map.PageCount;
            Commit(h, work, new RepaintTrail(h.Revision, near, Strip(140, (0, 0), (25, -300))));
            _map.Rebuild(h.Document, new EditedHeights(Ground, Lattice, h.Terrain), frame, Built);
            Assert.That(_map.LastSplit, Does.Not.Contain($" {_map.PageCount} of {_map.PageCount} pages"), "only some pages sent");
            Check("a repaint");
            Assert.That(pages, Is.GreaterThan(0));
        }

        void Check(string when)
        {
            var surface = _map.Surface;
            Assert.That(_map.PageCount, Is.EqualTo(surface.Chunks.Count), when + ": a page a tile");
            var points = new TrailSurfaceMap.Point[_map.PointsBuffer.count];
            _map.PointsBuffer.GetData(points);
            var box = _map.TableBox;
            var table = new int[box.Width * box.Height];
            _map.PagesBuffer.GetData(table, 0, 0, table.Length);
            const int side = TrailSurfaceMap.PageSide;
            foreach (var key in surface.Chunks.Keys)
            {
                Assert.That(_map.TrySlot(key, out int slot), Is.True);
                Assert.That(table[(key.Row - box.Row) * box.Width + key.Column - box.Column], Is.EqualTo(slot), $"{when}: the table finds tile {key}");
                for (int j = 0; j < side; j++)
                    for (int i = 0; i < side; i++)
                    {
                        var p = points[slot * side * side + j * side + i];
                        int c = key.FirstColumn + i, r = key.FirstRow + j;
                        var want = new TrailSurfaceMap.Point();
                        if (surface.TryAt(c, r, out var chunk, out int at))
                        {
                            want.FootChange = Mathf.FloatToHalf(chunk.Foot[at]) | (uint)Mathf.FloatToHalf(chunk.Change[at]) << 16;
                            want.GradedAcross = Mathf.FloatToHalf(chunk.Graded[at]) | (uint)Mathf.FloatToHalf(chunk.Across[at]) << 16;
                            want.Offset = chunk.Offset[at];
                        }
                        if (p.FootChange != want.FootChange || p.GradedAcross != want.GradedAcross || p.Offset != want.Offset)
                            Assert.Fail($"{when}: tile {key} point ({i}, {j}) on the GPU isn't the field");
                    }
            }
            for (int k = 0; k < table.Length; k++)
            {
                if (table[k] >= 0) continue;
                var key = new EditTileKey(box.Column + k % box.Width, box.Row + k / box.Width);
                Assert.That(surface.Chunks.ContainsKey(key), Is.False, $"{when}: tile {key} has a page");
            }
        }
    }
}
