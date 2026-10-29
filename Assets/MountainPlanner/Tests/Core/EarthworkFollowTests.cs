using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The design's earthwork follows the history (task P3-11): undo and redo put back the state their edits were made
    /// with, so a preview after an undo grades only what it changes, and the fold never works from a state that doesn't
    /// explain the committed edits (which once laid a deleted-then-restored lift's old trench back over new work). Every
    /// step is checked against a fresh fold of the same design. Wired as the game wires it: the history's change first,
    /// then the tool's accept.
    /// </summary>
    public sealed class EarthworkFollowTests
    {
        const double West = 500_000.25, North = 4_200_000.75;
        static readonly EditLattice Lattice = new EditLattice(West, North);
        static readonly EarthworkSite Site = new EarthworkSite(Lattice, West + 1000, North - 1000);
        static readonly LiftType Quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);

        sealed class Rolling : IHeightSource
        {
            public double At(double x, double y)
            {
                double s = North - y;
                return 2000 - 0.35 * s + 0.25 * (x - West) + 3 * Math.Sin(s * 0.02) + 0.6 * Math.Sin(x * 0.09) * Math.Cos(y * 0.05);
            }
        }

        static readonly IHeightSource Ground = new Rolling();

        static PointMm M(double x, double y) => PointMm.FromMetres(x, y);

        static TrailShape Strip(int widthFt, params (double x, double y)[] pts) =>
            TrailShape.FromStrokes(null, new[] { new Stroke(DesignUnits.FeetToMm(widthFt), false, pts.Select(p => M(p.x, p.y)).ToArray()) });

        // A session as the game runs one: TerrainEditSession calls Follow on every change.
        static (DesignHistory h, DesignEarthwork work) Session(bool follow = true)
        {
            var h = new DesignHistory(DesignDocument.Empty(77));
            var work = new DesignEarthwork(Ground, Site);
            work.Prime(h.Document, h.Terrain);
            if (follow) h.Changed += s => work.Follow(s.Document, s.Terrain);
            return (h, work);
        }

        // Preview, submit, accept, as the tools do; returns the preview's result.
        static DesignEarthwork.Result Commit(DesignHistory h, DesignEarthwork work, DesignCommand cmd)
        {
            var after = cmd.Apply(h.Document);
            Assert.That(after.Accepted, Is.True, after.ToString());
            var rebuilt = work.Rebuild(after.Document, h.Terrain);
            var r = h.Submit(cmd, rebuilt.Edits);
            Assert.That(r.Accepted, Is.True, r.ToString());
            Assert.That(work.Accept(rebuilt), Is.True, "the accept after the submit is taken");
            return rebuilt;
        }

        static TrailId AddTrail(DesignHistory h, DesignEarthwork work, TrailShape shape, PointMm head, PointMm tail, bool graded = true)
        {
            var cmd = new AddTrail(h.Revision, "Run " + (h.Document.Trails.Count + 1), graded ? TrailSettings.GradedRun : TrailSettings.NaturalRun,
                                   shape, head, tail, WorkStage.Built);
            Commit(h, work, cmd);
            return h.Document.Trails.First(t => t.Shape.Equals(shape)).Id;
        }

        static LiftId AddLiftLine(DesignHistory h, DesignEarthwork work, LiftType type, double x0, double y0, double x1, double y1)
        {
            var g = new EditedHeights(Ground, Lattice, h.Terrain);
            var b = LiftBuild.Build(type, g, Site.OriginX + x0, Site.OriginY + y0, Site.OriginX + x1, Site.OriginY + y1, TowerStance.Leaned);
            var cmd = new AddLift(h.Revision, "Chair " + (h.Document.Lifts.Count + 1), type.CatalogId, M(x0, y0), M(x1, y1),
                                  TowerMode.Automatic, TowerStance.Leaned, b.Towers, WorkStage.Built, b.Levels);
            Commit(h, work, cmd);
            return h.Document.Lifts[h.Document.Lifts.Count - 1].Id;
        }

        static LiftId AddLift(DesignHistory h, DesignEarthwork work, double x, double y0, double y1)
        {
            var g = new EditedHeights(Ground, Lattice, h.Terrain);
            var b = LiftBuild.Build(Quad, g, Site.OriginX + x, Site.OriginY + y0, Site.OriginX + x, Site.OriginY + y1, TowerStance.Plumb);
            var cmd = new AddLift(h.Revision, "Chair " + (h.Document.Lifts.Count + 1), Quad.CatalogId, M(x, y0), M(x, y1),
                                  TowerMode.Automatic, TowerStance.Plumb, b.Towers, WorkStage.Built, b.Levels);
            Commit(h, work, cmd);
            return h.Document.Lifts[h.Document.Lifts.Count - 1].Id;
        }

        static TerrainEdits Fresh(DesignDocument doc) => DesignEarthwork.Regrade(Ground, Site, doc);

        static void AssertFresh(DesignHistory h, string when) =>
            Assert.That(TerrainEdits.Changed(h.Terrain, Fresh(h.Document)), Is.Empty, when + ": the committed ground equals a fresh fold");

        [Test]
        public void FW1_AfterUndoAndRedoAPreviewGradesOnlyWhatItChanges()
        {
            var (h, work) = Session();
            var far = AddTrail(h, work, Strip(100, (-600, 400), (-600, 100)), M(-600, 400), M(-600, 100));
            AddTrail(h, work, Strip(80, (-120, -80), (140, -220)), M(-120, -80), M(140, -220), graded: false);
            AddTrail(h, work, Strip(90, (100, 50), (120, -260)), M(100, 50), M(120, -260));
            h.Undo();
            h.Undo();
            h.Redo();
            AssertFresh(h, "after undo, undo, redo");
            // An edit of the far trail (a new one would reuse the undone trail's ID): only it is graded, and nothing the undo
            // took back is taken off again, since the state put back explains everything else.
            var edit = Commit(h, work, new RepaintTrail(h.Revision, far, Strip(120, (-600, 400), (-590, 100))));
            Assert.That(edit.Regraded, Is.EqualTo(1), "only the edited trail is graded after an undo and a redo");
            Assert.That(edit.Overlay.Count, Is.EqualTo(1), "and nothing the undo took back is taken off again");
            AssertFresh(h, "an edit after undo and redo");
        }

        [Test]
        public void FW2_ADeletedLiftsTrenchNeverComesBackOverNewWork()
        {
            var (h, work) = Session();
            var lift = AddLift(h, work, 0, -300, 400);
            Commit(h, work, new DeleteLift(h.Revision, lift));
            AssertFresh(h, "the lift deleted");
            h.Undo();
            AssertFresh(h, "the delete undone");
            // A graded trail over the bottom terminal: the lift's pad is graded again on the trail's ground, never laid back
            // as it was from a state that didn't know the lift.
            AddTrail(h, work, Strip(150, (-200, -280), (200, -320)), M(-200, -280), M(200, -320));
            AssertFresh(h, "a trail across the restored lift's corridor");
        }

        [Test]
        public void FW3_TheEmptySetAndCommandsThatMoveNoGroundStayExact()
        {
            var (h, work) = Session();
            var a = AddTrail(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            Commit(h, work, new DeleteTrail(h.Revision, a));
            Assert.That(h.Terrain.IsEmpty, Is.True, "nothing left: the shared empty set");
            h.Undo();
            h.Redo();
            h.Undo();
            AssertFresh(h, "back and forth through the empty set");
            // Terrain-less commands keep the same edits object.
            var t = h.Document.Trails[0];
            Assert.That(h.Submit(new RenameTrail(h.Revision, t.Id, "Renamed")).Accepted, Is.True);
            var split = h.Submit(new SplitTrail(h.Revision, t.Id, M(0, -150), 0, -1));
            Assert.That(split.Accepted, Is.True, split.ToString());
            Commit(h, work, new AddTrail(h.Revision, "Across", TrailSettings.GradedRun, Strip(100, (-150, -100), (150, -200)), M(-150, -100), M(150, -200), WorkStage.Built));
            AssertFresh(h, "an edit after a rename and a split");
            h.Undo();
            h.Undo();
            Commit(h, work, new AddTrail(h.Revision, "Again", TrailSettings.NaturalRun, Strip(100, (-150, -100), (150, -200)), M(-150, -100), M(150, -200), WorkStage.Built));
            AssertFresh(h, "an edit after undoing the split");
        }

        [Test]
        public void FW4_AResultFromBeforeAnUndoIsRefused()
        {
            var (h, work) = Session();
            AddTrail(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            AddTrail(h, work, Strip(100, (200, 0), (200, -300)), M(200, 0), M(200, -300));
            var cmd = new AddTrail(h.Revision, "Late", TrailSettings.GradedRun, Strip(100, (-200, 0), (-200, -300)), M(-200, 0), M(-200, -300), WorkStage.Built);
            var stale = work.Rebuild(cmd.Apply(h.Document).Document, h.Terrain);
            h.Undo();
            Assert.That(work.Accept(stale), Is.False, "a preview made before the undo never becomes the state");
            AddTrail(h, work, Strip(100, (-200, 0), (-200, -300)), M(-200, 0), M(-200, -300));
            AssertFresh(h, "after the refused result");
        }

        [Test]
        public void FW5_ChangesNoAcceptExplainsAreLearntAgain()
        {
            var (h, work) = Session();
            AddTrail(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            // Submitted without an accept (a test bench's commit, a tool's refused result): the next rebuild learns the design.
            var cmd = new AddTrail(h.Revision, "Unaccepted", TrailSettings.GradedRun, Strip(100, (-150, -100), (150, -200)), M(-150, -100), M(150, -200), WorkStage.Built);
            var rebuilt = work.Rebuild(cmd.Apply(h.Document).Document, h.Terrain);
            Assert.That(h.Submit(cmd, rebuilt.Edits).Accepted, Is.True);
            AddTrail(h, work, Strip(90, (100, 50), (120, -260)), M(100, 50), M(120, -260));
            AssertFresh(h, "an edit after an unaccepted commit");
            h.Undo();
            h.Undo();
            AddTrail(h, work, Strip(90, (100, 50), (120, -260)), M(100, 50), M(120, -260));
            AssertFresh(h, "undone past it, then an edit");
        }

        [Test]
        public void FW6_StrokesNearALiftAfterItsEditIsUndoneStayExact()
        {
            var (h, work) = Session();
            var lift = AddLift(h, work, 0, -300, 400);
            var l = h.Document.Lifts[0];
            var g = new EditedHeights(Ground, Lattice, h.Terrain);
            var moved = LiftBuild.Build(Quad, g, Site.OriginX + 20, Site.OriginY - 300, Site.OriginX, Site.OriginY + 400, TowerStance.Plumb);
            Commit(h, work, new EditLift(h.Revision, lift, l.CatalogId, M(20, -300), M(0, 400), TowerMode.Automatic, TowerStance.Plumb,
                                         Array.Empty<Tower>(), moved.Towers, moved.Levels));
            h.Undo();
            var dabs = Enumerable.Range(0, 12).Select(i => new BrushDab(M(-30 + i * 5, -290), 1000)).ToList();
            Commit(h, work, new AddTerrainStrokes(h.Revision, new[] { new StrokePainting(BrushSettings.RaiseOrLower(true, 6096, 40), dabs) }));
            AssertFresh(h, "a stroke by the bottom terminal after the lift's edit was undone");
        }

        // The ground, noting every lattice tile a read lands in.
        sealed class Recording : IHeightSource
        {
            public readonly System.Collections.Generic.HashSet<EditTileKey> Tiles = new System.Collections.Generic.HashSet<EditTileKey>();
            public double At(double x, double y)
            {
                Tiles.Add(EditTileKey.Of((int)Math.Floor(x - West), (int)Math.Floor(North - y)));
                Tiles.Add(EditTileKey.Of((int)Math.Ceiling(x - West), (int)Math.Ceiling(North - y)));
                return Ground.At(x, y);
            }
        }

        [Test]
        public void FW8_EveryPieceReadsAndWritesOnlyInsideItsTiles()
        {
            // The fold opens only a piece's tiles (task P3-11 regrades by them): a read or write outside would go unseen.
            var (h, work) = Session();
            AddTrail(h, work, Strip(150, (0, 0), (40, -400)), M(0, 0), M(40, -400));
            var island = TrailShape.FromStrokes(null, new[]
            {
                new Stroke(DesignUnits.FeetToMm(120), false, new[] { M(-300, 100), M(-250, -300) }),
                new Stroke(DesignUnits.FeetToMm(30), true, new[] { M(-275, -100) }),
            });
            AddTrail(h, work, island, M(-300, 100), M(-250, -300), graded: false);
            AddLift(h, work, 200, -300, 400);
            AddLiftLine(h, work, LiftType.Of(LiftCatalogIds.SesselliftD6), -500, 300, 400, -300);   // a diagonal six-pack: the widest type
            var dabs = Enumerable.Range(0, 10).Select(i => new BrushDab(M(180 + i * 4, -290), 1000)).ToList();
            Commit(h, work, new AddTerrainStrokes(h.Revision, new[] { new StrokePainting(BrushSettings.Smooth(15240, 350), dabs) }));
            var items = new System.Collections.Generic.List<IEarthworkItem>();
            foreach (var c in new IEarthworkContributor[] { new TrailEarthwork(), new LiftEarthwork(), new BrushEarthwork() })
                c.Collect(h.Document, Site, items);
            Assert.That(items.Count, Is.EqualTo(5));
            foreach (var it in items)
            {
                // The piece alone, everything before it laid as committed: what it reads and what it writes.
                var reads = new Recording();
                var ground = new EditedHeights(reads, Lattice, h.Terrain);
                var context = new EarthworkContext(new System.Collections.Generic.Dictionary<EditTileKey, ulong[]>(), k => work.DetailOf(k));
                var writes = it.Build(ground, context, out _);
                var mine = new System.Collections.Generic.HashSet<EditTileKey>(it.Tiles);
                Assert.That(reads.Tiles.Where(t => !mine.Contains(t)), Is.Empty, $"{it.GetType().DeclaringType?.Name} {it.Key:x}: reads inside its tiles");
                Assert.That(writes.Tiles.Where(t => !mine.Contains(t)), Is.Empty, $"{it.GetType().DeclaringType?.Name} {it.Key:x}: writes inside its tiles");
                // A lift's reach (task P3-11): what it reads and writes lies in it, and it's narrower than its tiles.
                if (it is IReachTiles r)
                {
                    var reach = new System.Collections.Generic.HashSet<EditTileKey>(r.ReachTiles);
                    Assert.That(reads.Tiles.Where(t => !reach.Contains(t)), Is.Empty, $"lift {it.Key:x}: reads inside its reach");
                    Assert.That(writes.Tiles.Where(t => !reach.Contains(t)), Is.Empty, $"lift {it.Key:x}: writes inside its reach");
                    Assert.That(reach.Where(t => !mine.Contains(t)), Is.Empty, $"lift {it.Key:x}: the reach is within its tiles");
                    Assert.That(reach.Count, Is.LessThan(mine.Count), $"lift {it.Key:x}: the reach is narrower than its tiles");
                }
            }
        }

        [Test]
        public void FW9_WithIsAMergeThatKeepsEveryTileItDoesntReplace()
        {
            // TerrainEdits.With as one merge over the sorted tiles (task P3-11): the same set as rebuilding it key by key.
            ulong seed = 12345;
            uint Next() { seed = seed * 6364136223846793005UL + 1442695040888963407UL; return (uint)(seed >> 33); }
            TerrainEditTile Tile(int c, int r, int delta)
            {
                var b = TerrainEditTile.Empty(new EditTileKey(c, r)).Edit();
                if (delta != 0) b.SetDeltaMm(c & 63, r & 63, delta);
                return b.Build();
            }
            for (int round = 0; round < 200; round++)
            {
                var basis = new System.Collections.Generic.List<TerrainEditTile>();
                var used = new System.Collections.Generic.HashSet<(int, int)>();
                int n = (int)(Next() % 12);
                for (int i = 0; i < n; i++) { int c = (int)(Next() % 6), r = (int)(Next() % 6); if (used.Add((c, r))) basis.Add(Tile(c, r, 1 + (int)(Next() % 9))); }
                var set = TerrainEdits.From(basis);
                var reps = new System.Collections.Generic.List<TerrainEditTile>();
                var repKeys = new System.Collections.Generic.HashSet<(int, int)>();
                int m = (int)(Next() % 8);
                for (int i = 0; i < m; i++) { int c = (int)(Next() % 6), r = (int)(Next() % 6); if (repKeys.Add((c, r))) reps.Add(Tile(c, r, (int)(Next() % 3) == 0 ? 0 : 1 + (int)(Next() % 9))); }
                var merged = set.With(reps);
                // The expected set, key by key.
                var expect = new System.Collections.Generic.SortedDictionary<EditTileKey, TerrainEditTile>();
                foreach (var t in set.Tiles) expect[t.Key] = t;
                foreach (var t in reps) { if (t.IsEmpty) expect.Remove(t.Key); else expect[t.Key] = t; }
                Assert.That(merged.Tiles.Select(t => t.Key), Is.EqualTo(expect.Keys), $"round {round}: the keys, in order");
                foreach (var t in merged.Tiles) Assert.That(ReferenceEquals(t, expect[t.Key]), Is.True, $"round {round}: each tile is the very object");
                Assert.That(TerrainEdits.Changed(merged, TerrainEdits.From(expect.Values)), Is.Empty, $"round {round}");
            }
        }

        [Test]
        public void FW7_APrimeForgetsTheRememberedStates()
        {
            var (h, work) = Session();
            AddTrail(h, work, Strip(100, (0, 0), (0, -300)), M(0, 0), M(0, -300));
            var first = h.Terrain;
            AddTrail(h, work, Strip(100, (200, 0), (200, -300)), M(200, 0), M(200, -300));
            // A load learns its design again: nothing from before it is put back.
            work.Prime(h.Document, h.Terrain);
            h.Undo();
            Assert.That(ReferenceEquals(h.Terrain, first), Is.True);
            AddTrail(h, work, Strip(100, (-200, 0), (-200, -300)), M(-200, 0), M(-200, -300));
            AssertFresh(h, "after a prime and an undo past it");
        }
    }
}
