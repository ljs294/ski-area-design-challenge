using System;
using System.Linq;
using System.Reflection;
using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // The design document's shape, IDs and canonical bytes (task P3-03; rules SH-04, SH-05, TW-15, TR-06).
    public sealed class DesignDocumentTests
    {
        /// <summary>
        /// The golden: the scripted design's canonical bytes. A change here is a behaviour change to the document or
        /// its commands, and needs the owner's approval (AGENTS.md determinism rules). Re-pinned for shape 2 (task 09, the
        /// lifts' terminal levels; owner, LT3), from a82d1aa8… at shape 1.
        /// </summary>
        // Shape 3 (LT44): each tower's kind; the scripted design's towers are all Auto.
        // Shape 4 (task 15, BR12, owner OK with the plan 2026-10-26): the terrain strokes, none here; from 224681ef….
        // Shape 5 (task P3-10, NW-15, Master Planner ruling 2026-10-26): the named Areas, none here; from 0642d8d8….
        // Shape 6 (task P3-10, NT24, owner 2026-10-27): the trails of several sections, none here; from 4f6a0657….
        // Shape 7 (task P3-10, NT43–NT46, owner signed off 2026-10-27): spans with anchors and cut points, none here; from fedff97f….
        const string GoldenSha = "315f1a244f4dc874cd68e1fc30b06655a85050a56c3ccf75ba597fe3903286bf";

        [Test]
        public void SH04_SameCommandsInTheSameOrderGiveTheSameBytes()
        {
            var a = Bytes(Scripted());
            var b = Bytes(Scripted());
            Assert.That(DesignBytes.Same(a, b), Is.True);
            Assert.That(Sha(a), Is.EqualTo(GoldenSha), "golden design bytes");
        }

        [Test]
        public void SH04_IdsAreKeyedHashesOfTheSeedAndOrdinal()
        {
            var h = new DesignHistory(DesignDocument.Empty(42));
            var trail = AddTrail(h, "Run 1", P(0, 1000), P(0, 0));
            var lift = AddLift(h, "Chair 1", P(100, 0), P(100, 1000));
            Assert.That(trail, Is.EqualTo(TrailId.Make(42, 1)));
            Assert.That(lift, Is.EqualTo(LiftId.Make(42, 2)));
            Assert.That(h.Document.NextOrdinal, Is.EqualTo(3));

            var other = new DesignHistory(DesignDocument.Empty(43));
            Assert.That(AddTrail(other, "Run 1", P(0, 1000), P(0, 0)), Is.Not.EqualTo(trail), "a different seed keys different IDs");
        }

        [Test]
        public void SH04_CollectionsStayInIdOrder()
        {
            var h = new DesignHistory(DesignDocument.Empty(7));
            for (int i = 0; i < 20; i++) AddTrail(h, $"Run {i}", P(i * 200, 1000), P(i * 200, 0));
            for (int i = 0; i < 5; i++) AddLift(h, $"Chair {i}", P(i * 300, -500), P(i * 300 + 100, 1500));
            var trails = h.Document.Trails.Select(t => t.Id.Value).ToArray();
            var lifts = h.Document.Lifts.Select(l => l.Id.Value).ToArray();
            Assert.That(trails, Is.Ordered);
            Assert.That(lifts, Is.Ordered);
        }

        [Test]
        public void SH05_DocumentHoldsOnlyAuthoredData()
        {
            // The authored shape, property by property (SH-05, NW-06, TW-15). A new property is a format change for
            // task 04 (bump ShapeVersion, migrate, fixture) and must never be derived data.
            AssertProperties(typeof(DesignDocument), "Seed", "NextOrdinal", "Trails", "Lifts", "Strokes", "Areas", "Lines", "Cuts");
            AssertProperties(typeof(TrailLine), "Id", "Name", "Spans");   // NT43: a shaped trail, spans of runs top first
            AssertFields(typeof(Span), "Piece", "From", "To", "FromMm", "ToMm");
            AssertFields(typeof(Anchor), "Kind", "Other", "End", "K", "At");
            AssertFields(typeof(TrailCut), "Piece", "At");
            AssertProperties(typeof(AreaRecord), "Id", "Name", "Ends");   // NW-15 (task P3-10): the player's names, not the hubs
            AssertFields(typeof(AreaEnd), "Lift", "Bottom");
            AssertProperties(typeof(Trail), "Id", "Name", "Stage", "Settings", "Shape", "Head", "Tail", "HasEnds");
            AssertProperties(typeof(FootprintPart), "Strokes", "Cuts");
            AssertProperties(typeof(Stroke), "WidthMm", "Erase", "Points");
            AssertProperties(typeof(Lift), "Id", "Name", "Stage", "CatalogId", "Bottom", "Top", "TowerMode", "Stance", "Towers",
                "Levels", "LengthSquaredMm", "LengthMetres");
            AssertFields(typeof(LiftLevels), "BottomMm", "TopMm");
            AssertFields(typeof(Tower), "Id", "AlongMm", "HeightMm", "Kind");
            AssertFields(typeof(TrailSettings), "Grading", "Kind", "Clearing");
            AssertFields(typeof(Cut), "At", "DirX", "DirY", "KeepAhead");
            AssertProperties(typeof(TerrainStroke), "Id", "Settings", "Dabs");
            AssertFields(typeof(BrushSettings), "Kind", "RadiusMm", "FalloffPermille", "StepMm", "StrengthPermille", "TargetMm", "StartMm", "StartAt", "TargetAt", "LimitMm");
            AssertFields(typeof(BrushDab), "At", "WeightPermille");
            Assert.That(DesignDocument.ShapeVersion, Is.EqualTo(8));
        }

        static void AssertProperties(Type type, params string[] names)
        {
            var found = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal);
            Assert.That(found, Is.EqualTo(names.OrderBy(n => n, StringComparer.Ordinal)), type.Name);
        }

        static void AssertFields(Type type, params string[] names)
        {
            var found = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal);
            Assert.That(found, Is.EqualTo(names.OrderBy(n => n, StringComparer.Ordinal)), type.Name);
        }

        [Test]
        public void TW15_TowerPositionHeightAndStanceAreStored()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var lift = AddLift(h, "Chair 1", P(0, 0), P(0, 3000), WorkStage.Built, Tower(500, 30), Tower(1200, 85));
            Assert.That(h.Submit(new SetTowerStance(h.Revision, lift, TowerStance.Leaned)).Accepted, Is.True);
            Assert.That(h.Submit(new SetTowerMode(h.Revision, lift, TowerMode.ByHand)).Accepted, Is.True);

            var back = DesignBytes.Decode(Bytes(h)).FindLift(lift);
            Assert.That(back.Stance, Is.EqualTo(TowerStance.Leaned));
            Assert.That(back.TowerMode, Is.EqualTo(TowerMode.ByHand));
            Assert.That(back.Towers.Select(t => (t.AlongMm, t.HeightMm)), Is.EqualTo(new[] { (Ft(500), Ft(30)), (Ft(1200), Ft(85)) }));
            Assert.That(back.Towers.Select(t => t.Id), Is.EqualTo(h.Document.FindLift(lift).Towers.Select(t => t.Id)));
        }

        [Test]
        public void TW02_TowerHeightsAre25To100FtIn5FtSteps()
        {
            Assert.That(DesignRules.TowerHeightProblem(Ft(25)), Is.Null);
            Assert.That(DesignRules.TowerHeightProblem(Ft(100)), Is.Null);
            Assert.That(DesignRules.TowerHeightProblem(Ft(20)), Is.Not.Null);
            Assert.That(DesignRules.TowerHeightProblem(Ft(105)), Is.Not.Null);
            Assert.That(DesignRules.TowerHeightProblem(Ft(30) + 1), Is.Not.Null, "between steps");
        }

        [Test]
        public void TR01_BrushIs25To400FtIn5FtSteps()
        {
            Assert.That(DesignRules.BrushWidthProblem(Ft(25)), Is.Null);
            Assert.That(DesignRules.BrushWidthProblem(Ft(400)), Is.Null);
            Assert.That(DesignRules.BrushWidthProblem(Ft(405)), Is.Not.Null);
            Assert.That(DesignRules.BrushWidthProblem(Ft(102)), Is.Not.Null);
        }

        [Test]
        public void NT47_GradingIsPermanentOnceBuilt()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var plan = AddTrail(h, "Run 1", P(0, 1000), P(0, 0), WorkStage.Plan);
            Assert.That(h.Submit(new SetTrailSettings(h.Revision, plan, TrailSettings.NaturalRun)).Accepted, Is.True, "a plan switches freely");
            Assert.That(h.Submit(new SetTrailSettings(h.Revision, plan, TrailSettings.GradedRun)).Accepted, Is.True, "and back");
            var built = AddTrail(h, "Run 2", P(500, 1000), P(500, 0));
            var back = h.Submit(new SetTrailSettings(h.Revision, built, TrailSettings.NaturalRun));
            Assert.That(back.Accepted, Is.False);
            Assert.That(back.ToString(), Does.Contain("permanent"));
            var t = h.Document.FindTrail(built);
            var edit = h.Submit(new EditTrail(h.Revision, built, t.Name, t.Shape, t.Head.Value, t.Tail.Value, TrailSettings.NaturalRun, WorkStage.Built));
            Assert.That(edit.Accepted, Is.False, "Edit trail can't ungrade a built trail either");
            var natural = h.Submit(new AddTrail(h.Revision, "Run 3", TrailSettings.NaturalRun, Straight(P(900, 1000), P(900, 0)), P(900, 1000), P(900, 0), WorkStage.Built));
            var id = new TrailId(natural.Created[0]);
            Assert.That(h.Submit(new SetTrailSettings(h.Revision, id, TrailSettings.GradedRun)).Accepted, Is.True, "a built natural trail can be graded later");
        }

        [Test]
        public void TR06_SettingsAreHeldButOnlyPhase3OnesCanBeChosen()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var run = AddTrail(h, "Run 1", P(0, 1000), P(0, 0), WorkStage.Plan);
            Assert.That(h.Submit(new SetTrailSettings(h.Revision, run, TrailSettings.NaturalRun)).Accepted, Is.True);
            var glade = h.Submit(new SetTrailSettings(h.Revision, run, new TrailSettings(TrailGrading.Natural, TrailKind.Glade)));
            Assert.That(glade.Code, Is.EqualTo(RejectionCode.NotAvailable));
            var thin = h.Submit(new SetTrailSettings(h.Revision, run, new TrailSettings(TrailGrading.Graded, TrailKind.Run, TrailClearing.Thin)));
            Assert.That(thin.Code, Is.EqualTo(RejectionCode.NotAvailable));

            // A later phase's file may hold a glade: the document reads it.
            var trail = h.Document.FindTrail(run);
            var later = DesignDocument.From(1, 9, new[] { trail.WithSettings(new TrailSettings(TrailGrading.Natural, TrailKind.Zone, TrailClearing.None)) }, null);
            Assert.That(DesignBytes.Decode(DesignBytes.Encode(later)).FindTrail(run).Settings.Kind, Is.EqualTo(TrailKind.Zone));
        }

        [Test]
        public void EncodeDecodeEncodeGivesTheSameBytes()
        {
            var bytes = Bytes(Scripted());
            Assert.That(DesignBytes.Same(DesignBytes.Encode(DesignBytes.Decode(bytes)), bytes), Is.True);
        }

        [Test]
        public void DecodeRefusesBadBytes()
        {
            var bytes = Bytes(Scripted());
            Assert.Throws<FormatException>(() => DesignBytes.Decode(bytes.Take(bytes.Length - 3).ToArray()), "truncated");
            Assert.Throws<FormatException>(() => DesignBytes.Decode(bytes.Concat(new byte[] { 0 }).ToArray()), "trailing");
            var future = (byte[])bytes.Clone();
            future[4] = (byte)(DesignDocument.ShapeVersion + 1);
            Assert.Throws<FormatException>(() => DesignBytes.Decode(future), "a newer shape");
            var magic = (byte[])bytes.Clone();
            magic[0] = (byte)'X';
            Assert.Throws<FormatException>(() => DesignBytes.Decode(magic), "not a design");
        }

        [Test]
        public void FromRefusesDuplicateIds()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            AddTrail(h, "Run 1", P(0, 1000), P(0, 0));
            var t = h.Document.Trails[0];
            Assert.Throws<FormatException>(() => DesignDocument.From(1, 5, new[] { t, t }, null));
        }

        [Test]
        public void UnitsAreExactInMillimetres()
        {
            Assert.That(Ft(5), Is.EqualTo(1524));
            Assert.That(DesignUnits.SnapMm, Is.EqualTo(9144));
            Assert.That(DesignUnits.TowerMinMm, Is.EqualTo(7620));
            Assert.That(DesignUnits.BrushMaxMm, Is.EqualTo(121920));
        }
    }
}
