using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The trail look's numbers (task P3-11): updating only what a change reaches gives exactly what a whole redraw
    /// gives, through trails added, repainted and removed, ground moved by a stroke and a lift, undo, and the window
    /// growing; and a whole redraw is exactly the old one (every trail located over the whole window). Task 4.01 (TL24):
    /// the fields are kept by 64 m tile wherever the trails are, so a resort wider than the old 2048 m window has every
    /// trail drawn.
    /// </summary>
    public sealed class TrailSurfaceTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly EarthworkSite Site = new EarthworkSite(Lattice, West + 2000, North - 2000);
        static readonly LiftType Quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);

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

        static TrailId Add(DesignHistory h, DesignEarthwork work, TrailShape shape, PointMm head, PointMm tail, bool graded = true)
        {
            Commit(h, work, new AddTrail(h.Revision, "Run " + (h.Document.Trails.Count + 1), graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun,
                                         shape, head, tail, WorkStage.Built));
            return h.Document.Trails.First(t => t.Shape.Equals(shape)).Id;
        }

        // The built trails as the map draws them: the earthwork's own footprint and centreline, else their own.
        static List<TrailSurface.Shape> Shapes(DesignDocument doc, DesignEarthwork work)
        {
            var list = new List<TrailSurface.Shape>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built || !t.HasEnds) continue;
                var b = work.DetailOf(t.Id.Value) as TrailBuild;
                var own = b?.OwnLineFor(t);
                TrailFootprint fp;
                double[] line;
                if (own != null && own.Found) { fp = b.Footprint; line = own.Points; }
                else
                {
                    fp = b != null && b.IsFor(t) ? b.Footprint : TrailFootprint.Build(t.Shape, Lattice, Site.OriginX, Site.OriginY);
                    var c = TrailCentreline.Find(fp, Site.OriginX + t.Head.Value.XMetres, Site.OriginY + t.Head.Value.YMetres,
                                                 Site.OriginX + t.Tail.Value.XMetres, Site.OriginY + t.Tail.Value.YMetres);
                    line = c.Found ? c.Points : null;
                }
                if (line != null) list.Add(new TrailSurface.Shape(t, line, fp, t.Settings.Grading == TrailGrading.Graded));
            }
            return list;
        }

        static void AssertSame(TrailSurface a, TrailSurface b, string when)
        {
            Assert.That(a.Chunks.Keys.OrderBy(k => k), Is.EqualTo(b.Chunks.Keys.OrderBy(k => k)), when + ": the tiles");
            foreach (var pair in a.Chunks)
            {
                var x = pair.Value;
                var y = b.Chunks[pair.Key];
                string at = $"{when}, tile {pair.Key}";
                Assert.That(x.Foot, Is.EqualTo(y.Foot), at + ": the footprint");
                Assert.That(x.Change, Is.EqualTo(y.Change), at + ": the change");
                Assert.That(x.Graded, Is.EqualTo(y.Graded), at + ": graded");
                Assert.That(x.Offset, Is.EqualTo(y.Offset), at + ": the offset");
                Assert.That(x.Across, Is.EqualTo(y.Across), at + ": the slope across");
            }
        }

        static long Points(TrailSurface s) => (long)s.Chunks.Count * TrailSurface.Side * TrailSurface.Side;

        [Test]
        public void TS1_UpdatingWhatChangedEqualsAWholeRedraw()
        {
            var h = new DesignHistory(DesignDocument.Empty(5));
            var work = new DesignEarthwork(Ground, Site);
            work.Prime(h.Document, h.Terrain);
            h.Changed += s => work.Follow(s.Document, s.Terrain);
            var kept = new TrailSurface();
            int step = 0;
            void Check(string when)
            {
                var edited = new EditedHeights(Ground, Lattice, h.Terrain);
                var shapes = Shapes(h.Document, work);
                kept.Update(shapes, edited);
                var fresh = new TrailSurface();
                fresh.Update(shapes, edited);
                AssertSame(kept, fresh, $"step {++step}, {when}");
                if (step > 1)
                    Assert.That(kept.PointsRedrawn, Is.LessThan(Points(kept)), when + ": only part of it is worked out again");
            }
            var a = Add(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            var b = Add(h, work, Strip(80, (-120, -80), (140, -220)), M(-120, -80), M(140, -220), graded: false);   // crosses a: ties
            var island = TrailShape.FromStrokes(null, new[]
            {
                new Stroke(DesignUnits.FeetToMm(150), false, new[] { M(300, 0), M(320, -400) }),
                new Stroke(DesignUnits.FeetToMm(40), true, new[] { M(310, -200) }),
            });
            Add(h, work, island, M(300, 0), M(320, -400), graded: false);
            Check("three trails, one with an island");
            Commit(h, work, new RepaintTrail(h.Revision, a, Strip(140, (0, 0), (15, -300))));
            Check("a repaint");
            var dabs = Enumerable.Range(0, 20).Select(i => new BrushDab(M(-60 + i * 6, -150), 1000)).ToList();
            Commit(h, work, new AddTerrainStrokes(h.Revision, new[] { new StrokePainting(BrushSettings.RaiseOrLower(true, 6096, 40), dabs) }));
            Check("a stroke across two trails (ground only)");
            var g = new EditedHeights(Ground, Lattice, h.Terrain);
            var lift = LiftBuild.Build(Quad, g, Site.OriginX + 310, Site.OriginY - 450, Site.OriginX + 310, Site.OriginY + 150, TowerStance.Plumb);
            Commit(h, work, new AddLift(h.Revision, "Chair", Quad.CatalogId, M(310, -450), M(310, 150), TowerMode.Automatic, TowerStance.Plumb,
                                        lift.Towers, WorkStage.Built, lift.Levels));
            Check("a lift up the island trail (ground only)");
            Commit(h, work, new DeleteTrail(h.Revision, b));
            Check("a trail removed");
            h.Undo();
            h.Undo();
            Check("two undone");
            h.Redo();
            Check("one redone");
            // A long run from far to the west: past where the old 2048 m window stopped, every point of it drawn.
            Add(h, work, Strip(100, (-2700, 300), (-1000, 200), (200, 100)), M(-2700, 300), M(200, 100), graded: false);
            Check("a run 2.9 km long");
            Assert.That(kept.Covers(Lattice, Site.OriginX - 2690, Site.OriginY + 300), Is.True, "the far end is drawn");
            Assert.That(kept.TryAt((int)Math.Round(Site.OriginX - 2690 - West), (int)Math.Round(North - (Site.OriginY + 300)), out var far, out int i), Is.True);
            Assert.That(far.Foot[i], Is.GreaterThan(0.5f), "on the far end's footprint");
            Commit(h, work, new RepaintTrail(h.Revision, a, Strip(100, (0, 0), (0, -300))));
            Check("a repaint with the long run there");
        }

        [Test]
        public void TS2_AWholeRedrawIsTheOldWholeWindowOne()
        {
            var h = new DesignHistory(DesignDocument.Empty(5));
            var work = new DesignEarthwork(Ground, Site);
            work.Prime(h.Document, h.Terrain);
            Add(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            Add(h, work, Strip(80, (-120, -80), (140, -220)), M(-120, -80), M(140, -220), graded: false);
            Add(h, work, Strip(120, (200, 50), (260, -350)), M(200, 50), M(260, -350));
            // And one 2.5 km away, past the old window.
            Add(h, work, Strip(90, (-2500, -100), (-2450, -400)), M(-2500, -100), M(-2450, -400));
            var edited = new EditedHeights(Ground, Lattice, h.Terrain);
            var shapes = Shapes(h.Document, work);
            var now = new TrailSurface();
            now.Update(shapes, edited);
            // Each tile against the old rule over a window 2 points round it (the old window's own edges have no slope).
            const int n = TrailSurface.Side;
            foreach (var pair in now.Chunks)
            {
                var k = pair.Key;
                int w = n + 4;
                var (foot, change, graded, offset, across) = Old(shapes, edited, k.FirstColumn - 2, k.FirstRow - 2, w, w);
                var chunk = pair.Value;
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        int at = j * n + i, old = (j + 2) * w + i + 2;
                        string where = $"tile {k} point ({i}, {j})";
                        Assert.That(chunk.Foot[at], Is.EqualTo(foot[old]), where + ": the footprint");
                        Assert.That(chunk.Change[at], Is.EqualTo(change[old]), where + ": the change");
                        Assert.That(chunk.Graded[at], Is.EqualTo(graded[old]), where + ": graded");
                        Assert.That(chunk.Offset[at], Is.EqualTo(offset[old]), where + ": the offset");
                        Assert.That(chunk.Across[at], Is.EqualTo(across[old]), where + ": the slope across");
                    }
            }
            // The trail 2.5 km to the west has its tiles.
            Assert.That(now.Chunks.Keys.Any(k => k.FirstColumn < (int)(Site.OriginX - 2400 - West)), Is.True, "the far trail's tiles");
        }

        // The trail map's fields as task 07 worked them out before task P3-11: each trail located over the whole window.
        static (float[], float[], float[], float[], float[]) Old(List<TrailSurface.Shape> shapes, EditedHeights edited, int c0, int r0, int w, int h)
        {
            var lattice = edited.Lattice;
            var foot = new float[w * h];
            var graded = new float[w * h];
            var change = new float[w * h];
            var offset = new float[w * h];
            var across = new float[w * h];
            var nearest = new double[w * h];
            for (int i = 0; i < nearest.Length; i++) nearest[i] = double.MaxValue;
            foreach (var s in shapes)
            {
                var line = s.Line;
                double half = s.Half;
                var fp = s.Footprint;
                var along = new double[line.Length / 2];
                for (int k = 1; k < along.Length; k++)
                    along[k] = along[k - 1] + Math.Sqrt(Math.Pow(line[2 * k] - line[2 * k - 2], 2) + Math.Pow(line[2 * k + 1] - line[2 * k - 1], 2));
                var located = TrailGrader.Locate(line, along, lattice, c0, r0, w, h, 0, 0, half + 2);
                for (int i = 0; i < w * h; i++)
                {
                    double d = located.Dist[i];
                    if (d > half + 2) continue;
                    int pc = c0 + i % w, pr = r0 + i / w;
                    if (!fp.Contains(pc, pr) && !fp.Contains(pc - 1, pr - 1)) continue;
                    float cover = Math.Max(0f, Math.Min(1f, (float)fp.PointClearance(pc, pr)));
                    if (cover > foot[i]) foot[i] = cover;
                    if (s.Graded && cover > graded[i]) graded[i] = cover;
                    if (d >= nearest[i]) continue;
                    nearest[i] = d;
                    offset[i] = (float)located.Offset[i];
                }
            }
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    int at = j * w + i;
                    if (foot[at] <= 0 && nearest[at] > 1e9) continue;
                    int c = c0 + i, r = r0 + j;
                    double x = lattice.X(c), y = lattice.Y(r);
                    change[at] = (float)(edited.AtPoint(c, r) - edited.Source.At(x, y));
                    if (i == 0 || j == 0 || i == w - 1 || j == h - 1) continue;
                    double ux = (offset[at + 1] - offset[at - 1]) / 2, uy = (offset[at - w] - offset[at + w]) / 2;
                    double len = Math.Sqrt(ux * ux + uy * uy);
                    if (len < 1e-6) continue;
                    ux /= len;
                    uy /= len;
                    double slope = (edited.At(x + 2 * ux, y + 2 * uy) - edited.At(x - 2 * ux, y - 2 * uy)) / 4;
                    across[at] = (float)Math.Abs(slope);
                }
            return (foot, change, graded, offset, across);
        }
    }
}
