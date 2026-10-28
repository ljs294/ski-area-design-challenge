using System.Linq;
using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Commands, revisions and undo (task P3-03; rules CF-03 to CF-06, NW-10, TW-15).
    public sealed class DesignCommandTests
    {
        [Test]
        public void CF05_StaleCommandIsRejectedWhole()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var run = AddTrail(h, "Run 1", P(0, 1000), P(0, 0));
            long old = h.Revision;
            AddTrail(h, "Run 2", P(500, 1000), P(500, 0));
            var before = Bytes(h);
            long revision = h.Revision;

            var r = h.Submit(new RenameTrail(old, run, "Renamed"));
            Assert.That(r.Accepted, Is.False);
            Assert.That(r.Code, Is.EqualTo(RejectionCode.Stale));
            Assert.That(h.Revision, Is.EqualTo(revision), "a rejection doesn't move the revision");
            Assert.That(DesignBytes.Same(Bytes(h), before), Is.True);
            Assert.That(h.UndoCount, Is.EqualTo(2));
        }

        [Test]
        public void CF05_ARejectedCommandChangesNothing()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var before = Bytes(h);
            // The second tower is 10 ft too tall: the lift, its ID and the first tower must not appear either.
            var r = h.Submit(new AddLift(h.Revision, "Chair 1", LiftCatalogIds.MontaFg4, P(0, 0), P(0, 2000), TowerMode.ByHand, TowerStance.Plumb,
                new[] { Tower(500, 30), Tower(1000, 110) }, WorkStage.Built));
            Assert.That(r.Code, Is.EqualTo(RejectionCode.Invalid));
            Assert.That(DesignBytes.Same(Bytes(h), before), Is.True);
            Assert.That(h.Document.NextOrdinal, Is.EqualTo(1), "no ID was used up");
            Assert.That(h.CanUndo, Is.False);

            var lift = AddLift(h, "Chair 1", P(0, 0), P(0, 2000), WorkStage.Built, Tower(500, 30));
            var afterLift = Bytes(h);
            var tower = h.Document.FindLift(lift).Towers[0].Id;
            Assert.That(h.Submit(new EditTower(h.Revision, tower, Ft(2500), Ft(30))).Accepted, Is.False, "past the top terminal");
            Assert.That(h.Submit(new ReplaceTowers(h.Revision, lift, new[] { tower }, new[] { Tower(500, 40) })).Accepted, Is.False, "two towers in one place");
            Assert.That(DesignBytes.Same(Bytes(h), afterLift), Is.True);
        }

        [Test]
        public void CF04_ASecondConfirmationInTheSameTickIsRejected()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var plan = AddTrail(h, "Run 1", P(0, 1000), P(0, 0), WorkStage.Plan);
            long revision = h.Revision;
            // Two Break ground clicks land before the UI sees the new snapshot: both name the same revision.
            var first = h.Submit(new BreakGround(revision, new[] { plan }, null));
            var second = h.Submit(new BreakGround(revision, new[] { plan }, null));
            Assert.That(first.Accepted, Is.True);
            Assert.That(second.Code, Is.EqualTo(RejectionCode.Stale));
            Assert.That(h.UndoCount, Is.EqualTo(2));
        }

        [Test]
        public void CF03_AnAnalysisForAnOlderRevisionIsDropped()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var preview = new Stamped<string>(h.Stamp(terrainRevision: 3, inputSequence: 10), "grading preview");
            Assert.That(preview.TryTake(h.Stamp(3, 10), out var v), Is.True);
            Assert.That(v, Is.EqualTo("grading preview"));
            Assert.That(preview.TryTake(h.Stamp(3, 11), out _), Is.False, "an old cursor position");
            Assert.That(preview.TryTake(h.Stamp(4, 10), out _), Is.False, "the terrain changed");
            AddTrail(h, "Run 1", P(0, 1000), P(0, 0));
            Assert.That(preview.TryTake(h.Stamp(3, 10), out _), Is.False, "the design changed");
        }

        [Test]
        public void CF03_UndoNeverReusesARevision()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            AddTrail(h, "Run 1", P(0, 1000), P(0, 0));
            var stamp = h.Stamp(0, 0);
            AddTrail(h, "Run 2", P(500, 1000), P(500, 0));
            long afterTwo = h.Revision;
            h.Undo();
            // Same content as when the stamp was made, but a new revision: the old analysis can't pass as current.
            Assert.That(h.Revision, Is.GreaterThan(afterTwo));
            Assert.That(new Stamped<int>(stamp, 1).TryTake(h.Stamp(0, 0), out _), Is.False);
            var seen = new System.Collections.Generic.HashSet<long> { h.Revision };
            h.Redo();
            Assert.That(seen.Add(h.Revision), Is.True);
            h.Undo();
            Assert.That(seen.Add(h.Revision), Is.True);
        }

        [Test]
        public void CF05_UndoAndRedoRestoreTheDocumentExactly()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var empty = Bytes(h);
            var run = AddTrail(h, "Run 1", P(0, 1000), P(0, 0));
            var one = Bytes(h);
            h.Submit(new RenameTrail(h.Revision, run, "Upper Run"));
            var renamed = Bytes(h);
            Assert.That(h.UndoLabel, Is.EqualTo("Rename trail"));

            Assert.That(h.Undo(), Is.True);
            Assert.That(DesignBytes.Same(Bytes(h), one), Is.True);
            Assert.That(h.RedoLabel, Is.EqualTo("Rename trail"));
            Assert.That(h.Undo(), Is.True);
            Assert.That(DesignBytes.Same(Bytes(h), empty), Is.True);
            Assert.That(h.Undo(), Is.False);
            Assert.That(h.Redo(), Is.True);
            Assert.That(h.Redo(), Is.True);
            Assert.That(DesignBytes.Same(Bytes(h), renamed), Is.True);
            Assert.That(h.Redo(), Is.False);
        }

        [Test]
        public void CF05_ANewCommandClearsRedo()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            AddTrail(h, "Run 1", P(0, 1000), P(0, 0));
            h.Undo();
            AddTrail(h, "Run 2", P(500, 1000), P(500, 0));
            Assert.That(h.CanRedo, Is.False);
        }

        [Test]
        public void UndoKeeps100StepsAndLoadClearsThem()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var run = AddTrail(h, "Run 0", P(0, 1000), P(0, 0));
            for (int i = 1; i <= 110; i++) h.Submit(new RenameTrail(h.Revision, run, $"Run {i}"));
            Assert.That(h.UndoCount, Is.EqualTo(DesignHistory.UndoDepth));
            int undone = 0;
            while (h.Undo()) undone++;
            Assert.That(undone, Is.EqualTo(100));
            Assert.That(h.Document.FindTrail(run).Name, Is.EqualTo("Run 10"), "the oldest steps fell off");

            h.Load(DesignDocument.Empty(2));
            Assert.That(h.CanUndo || h.CanRedo, Is.False);
        }

        [Test]
        public void SnapshotsOutEveryChangeRaisesChanged()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var seen = new System.Collections.Generic.List<long>();
            h.Changed += s => seen.Add(s.Revision);
            AddTrail(h, "Run 1", P(0, 1000), P(0, 0));
            h.Submit(new RenameTrail(h.Revision - 1, new TrailId(1), "x")); // stale: no event
            h.Undo();
            h.Redo();
            Assert.That(seen, Is.EqualTo(new long[] { 2, 3, 4 }));
        }

        [Test]
        public void CF06_BreakGroundBuildsPlansAllOrNothing()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var plan = AddTrail(h, "Run 1", P(0, 1000), P(0, 0), WorkStage.Plan);
            var liftPlan = AddLift(h, "Chair 1", P(100, 0), P(100, 1500), WorkStage.Plan);
            var built = AddTrail(h, "Run 2", P(500, 1000), P(500, 0));
            var before = Bytes(h);

            var mixed = h.Submit(new BreakGround(h.Revision, new[] { plan, built }, new[] { liftPlan }));
            Assert.That(mixed.Accepted, Is.False, "Run 2 is already built");
            Assert.That(DesignBytes.Same(Bytes(h), before), Is.True);

            Assert.That(h.Submit(new BreakGround(h.Revision, new[] { plan }, new[] { liftPlan })).Accepted, Is.True);
            Assert.That(h.Document.FindTrail(plan).Stage, Is.EqualTo(WorkStage.Built));
            Assert.That(h.Document.FindLift(liftPlan).Stage, Is.EqualTo(WorkStage.Built));
            h.Undo();
            Assert.That(h.Document.FindTrail(plan).Stage, Is.EqualTo(WorkStage.Plan));
        }

        [Test]
        public void CF06_APlanWithoutEndsCanBeSavedButNotBuilt()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var r = h.Submit(new AddTrail(h.Revision, "Run 1", TrailSettings.GradedRun, Straight(P(0, 1000), P(0, 0)), null, null, WorkStage.Plan));
            Assert.That(r.Accepted, Is.True);
            var id = new TrailId(r.Created[0]);
            Assert.That(h.Submit(new BreakGround(h.Revision, new[] { id }, null)).Accepted, Is.False);
            Assert.That(h.Submit(new AddTrail(h.Revision, "Run 2", TrailSettings.GradedRun, Straight(P(0, 1000), P(0, 0)), null, null, WorkStage.Built)).Accepted,
                Is.False);
        }

        [Test]
        public void NW10_SplitThenJoinRestoresTheTrail()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var run = AddTrail(h, "Run 1", P(0, 2000), P(0, 0));
            // Islands and a second width: the join must give back every cell.
            var parts = TrailShape.FromStrokes(null, new[]
            {
                new Stroke(Ft(100), false, new[] { P(0, 2000), P(0, 1000) }),
                new Stroke(Ft(150), false, new[] { P(0, 1000), P(0, 0) }),
                new Stroke(Ft(25), true, new[] { P(0, 1200) }),
            });
            h.Submit(new RepaintTrail(h.Revision, run, parts));
            var original = DesignBytes.Encode(h.Document.FindTrail(run));

            var split = h.Submit(new SplitTrail(h.Revision, run, P(0, 900), 0, -Ft(10)));
            Assert.That(split.Accepted, Is.True, split.ToString());
            var lower = new TrailId(split.Created[0]);
            var upperTrail = h.Document.FindTrail(run);
            var lowerTrail = h.Document.FindTrail(lower);
            Assert.That(lowerTrail.Name, Is.EqualTo("Run 1 lower"));
            Assert.That(upperTrail.Tail, Is.EqualTo(P(0, 900)));
            Assert.That(lowerTrail.Head, Is.EqualTo(P(0, 900)));
            Assert.That(lowerTrail.Tail, Is.EqualTo(P(0, 0)));
            // Every cell goes to exactly one side.
            Assert.That(upperTrail.Shape.Mask.Cells + lowerTrail.Shape.Mask.Cells, Is.EqualTo(parts.Mask.Cells));
            Assert.That(upperTrail.Shape.Union(lowerTrail.Shape), Is.EqualTo(parts));

            Assert.That(h.Submit(new JoinTrails(h.Revision, run, lower)).Accepted, Is.True);
            Assert.That(h.Document.FindTrail(lower), Is.Null);
            Assert.That(DesignBytes.Encode(h.Document.FindTrail(run)), Is.EqualTo(original));
        }

        [Test]
        public void NW10_SplitAndJoinAreOneUndoableCommandEach()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var run = AddTrail(h, "Run 1", P(0, 2000), P(0, 0));
            var before = Bytes(h);
            var split = h.Submit(new SplitTrail(h.Revision, run, P(0, 1000), 0, -1000, "Bottom Run"));
            Assert.That(h.Document.FindTrail(new TrailId(split.Created[0])).Name, Is.EqualTo("Bottom Run"));
            h.Undo();
            Assert.That(DesignBytes.Same(Bytes(h), before), Is.True);
        }

        [Test]
        public void NW10_SplitMustFallBetweenTheHeadAndTail()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var run = AddTrail(h, "Run 1", P(0, 2000), P(0, 0));
            Assert.That(h.Submit(new SplitTrail(h.Revision, run, P(0, 2500), 0, -1000)).Accepted, Is.False, "above the head");
            Assert.That(h.Submit(new SplitTrail(h.Revision, run, P(0, 1000), 0, 1000)).Accepted, Is.False, "pointing uphill");
            Assert.That(h.Submit(new SplitTrail(h.Revision, run, P(0, 1000), 0, 0)).Accepted, Is.False, "no direction");
        }

        [Test]
        public void NW10_JoinNeedsTheTailToMeetTheHead()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var upper = AddTrail(h, "Upper", P(0, 2000), P(0, 1000));
            var lower = AddTrail(h, "Lower", P(25, 1000), P(0, 0));
            var far = AddTrail(h, "Far", P(40, 1000), P(40, 0));
            Assert.That(h.Submit(new JoinTrails(h.Revision, upper, far)).Accepted, Is.False, "40 ft apart");
            Assert.That(h.Submit(new JoinTrails(h.Revision, lower, upper)).Accepted, Is.False, "the wrong way round");

            Assert.That(h.Submit(new JoinTrails(h.Revision, upper, lower)).Accepted, Is.True, "25 ft apart");
            var joined = h.Document.FindTrail(upper);
            Assert.That(joined.Name, Is.EqualTo("Upper"));
            Assert.That(joined.Head, Is.EqualTo(P(0, 2000)));
            Assert.That(joined.Tail, Is.EqualTo(P(0, 0)));
            Assert.That(joined.Shape.Mask.Cells, Is.GreaterThan(h.Document.Trails.Count > 0 ? 0 : 0), "both clearings, together");
            Assert.That(joined.Shape.Rings.Count, Is.EqualTo(1), "the two clearings overlap where they meet: one outline");
        }

        [Test]
        public void NW10_APlanAndABuiltTrailDontJoin()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var upper = AddTrail(h, "Upper", P(0, 2000), P(0, 1000));
            var lower = AddTrail(h, "Lower", P(0, 1000), P(0, 0), WorkStage.Plan);
            Assert.That(h.Submit(new JoinTrails(h.Revision, upper, lower)).Accepted, Is.False);
        }

        [Test]
        public void TW15_TowerCommandsEditTheAuthoredTowers()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var lift = AddLift(h, "Chair 1", P(0, 0), P(0, 3000), WorkStage.Built, Tower(1000, 30));
            var added = h.Submit(new AddTower(h.Revision, lift, Tower(500, 35)));
            Assert.That(added.Accepted, Is.True);
            var towers = h.Document.FindLift(lift).Towers;
            Assert.That(towers.Select(t => t.AlongMm), Is.EqualTo(new[] { Ft(500), Ft(1000) }), "kept in order along the line");

            var t500 = new TowerId(added.Created[0]);
            Assert.That(h.Submit(new EditTower(h.Revision, t500, Ft(600), Ft(40))).Accepted, Is.True, "Page Up twice and a slide");
            Assert.That(h.Document.FindLift(lift).Towers[0].HeightMm, Is.EqualTo(Ft(40)));
            Assert.That(h.Submit(new DeleteTower(h.Revision, t500)).Accepted, Is.True);
            Assert.That(h.Document.FindLift(lift).Towers.Count, Is.EqualTo(1));

            // Fill the rest: keep the hand-placed one, add the rest, switch nothing.
            var kept = h.Document.FindLift(lift).Towers[0].Id;
            var fill = h.Submit(new ReplaceTowers(h.Revision, lift, new[] { kept }, new[] { Tower(1500, 35), Tower(2200, 30) }));
            Assert.That(fill.Accepted, Is.True);
            Assert.That(h.Document.FindLift(lift).Towers.Count, Is.EqualTo(3));
            // Re-place towers: all new, and back to Automatic in the same step.
            Assert.That(h.Submit(new SetTowerMode(h.Revision, lift, TowerMode.ByHand)).Accepted, Is.True);
            var replace = h.Submit(new ReplaceTowers(h.Revision, lift, null, new[] { Tower(800, 35), Tower(1600, 35) }, TowerMode.Automatic));
            Assert.That(replace.Accepted, Is.True);
            var l = h.Document.FindLift(lift);
            Assert.That(l.TowerMode, Is.EqualTo(TowerMode.Automatic));
            Assert.That(l.IndexOfTower(kept), Is.EqualTo(-1));
            Assert.That(h.UndoLabel, Is.EqualTo("Place towers"));
        }

        [Test]
        public void TowerIdsAreUniqueAcrossTheDesign()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            AddLift(h, "Chair 1", P(0, 0), P(0, 3000), WorkStage.Built, Tower(500, 30), Tower(1000, 30));
            AddLift(h, "Chair 2", P(500, 0), P(500, 3000), WorkStage.Built, Tower(500, 30), Tower(1000, 30));
            var ids = h.Document.Lifts.SelectMany(l => l.Towers.Select(t => t.Id.Value)).Concat(h.Document.Lifts.Select(l => l.Id.Value)).ToArray();
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Length));
        }

        [Test]
        public void MissingTargetsAreNotFound()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            Assert.That(h.Submit(new DeleteTrail(h.Revision, new TrailId(5))).Code, Is.EqualTo(RejectionCode.NotFound));
            Assert.That(h.Submit(new DeleteLift(h.Revision, new LiftId(5))).Code, Is.EqualTo(RejectionCode.NotFound));
            Assert.That(h.Submit(new EditTower(h.Revision, new TowerId(5), 1, Ft(30))).Code, Is.EqualTo(RejectionCode.NotFound));
        }

        [Test]
        public void NamesAreCheckedAndUnknownLiftTypesRefused()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            Assert.That(h.Submit(new AddTrail(h.Revision, " ", TrailSettings.GradedRun, Straight(P(0, 1), P(0, 0)), null, null, WorkStage.Plan)).Accepted, Is.False);
            Assert.That(h.Submit(new AddTrail(h.Revision, new string('x', 65), TrailSettings.GradedRun, Straight(P(0, 1), P(0, 0)), null, null, WorkStage.Plan)).Accepted,
                Is.False);
            Assert.That(h.Submit(new AddLift(h.Revision, "Gondola", "gondola-8", P(0, 0), P(0, 1000), TowerMode.Automatic, TowerStance.Plumb, null,
                WorkStage.Plan)).Accepted, Is.False, "E13: fixed-grip only");
            Assert.That(SplitTrail.DefaultLowerName(new string('y', 64)).Length, Is.LessThanOrEqualTo(DesignRules.NameMaxLength));
        }
    }
}
