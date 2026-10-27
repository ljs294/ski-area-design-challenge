using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task P3-10, owner decisions NT24–NT41 (2026-10-27): a trail is a named chain of sections, chosen in Edit sections.
    public sealed class TrailLineTests
    {
        // Three runs end to end down a fall line: A (top) → B → C (bottom), each a trail on its own to start.
        static (DesignHistory h, TrailId a, TrailId b, TrailId c) ThreeRuns(WorkStage stage = WorkStage.Built)
        {
            var h = new DesignHistory(DesignDocument.Empty(0x5EED_2026));
            var a = AddTrail(h, "Run 1", P(0, 3000), P(0, 2000), stage);
            var b = AddTrail(h, "Run 2", P(0, 2000), P(0, 1000), stage);
            var c = AddTrail(h, "Run 3", P(0, 1000), P(0, 0), stage);
            return (h, a, b, c);
        }

        static CommandResult Submit(DesignHistory h, DesignCommand cmd)
        {
            var r = h.Submit(cmd);
            Assert.That(r.Accepted, Is.True, r.ToString());
            return r;
        }

        static string[] Names(DesignDocument doc) => TrailLines.All(doc).Select(l => l.Name).OrderBy(n => n, System.StringComparer.Ordinal).ToArray();

        [Test]
        public void NT24_ASectionOnItsOwnIsATrailWithNothingSaved()
        {
            var (h, a, _, _) = ThreeRuns();
            var line = h.Document.LineOf(a);
            Assert.That(line.Saved, Is.False);
            Assert.That(line.Sections, Is.EqualTo(new[] { a }));
            Assert.That(line.Id, Is.EqualTo(TrailLineId.Of(h.Document.Seed, a)));
            Assert.That(h.Document.Lines, Is.Empty);
            Assert.That(TrailLines.All(h.Document).Count, Is.EqualTo(3));
        }

        [Test]
        public void NT35_AddingASectionAtTheBottomMakesOneTrailThatKeepsItsId()
        {
            var (h, a, b, _) = ThreeRuns();
            var before = h.Document.LineOf(a).Id;
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b) }));
            var line = h.Document.LineOf(b);
            Assert.That(line.Saved, Is.True);
            Assert.That(line.Id, Is.EqualTo(before), "the trail keeps the ID it had as one section");
            Assert.That(line.Sections, Is.EqualTo(new[] { a, b }));
            Assert.That(h.Document.FindTrail(b).Name, Is.EqualTo("Run 1"), "every section carries its trail's name");
            Assert.That(Names(h.Document), Is.EqualTo(new[] { "Run 1", "Run 3" }), "Run 2 was taken over whole, so it went (NT28)");
        }

        [Test]
        public void NT35_ARoutedClickAddsTheWholeChainNearestFirst()
        {
            var (h, a, b, c) = ThreeRuns();
            var route = TrailLines.Route(h.Document, h.Document.LineOf(a), c);
            Assert.That(route, Is.EqualTo(new[] { b, c }));
            Submit(h, new EditTrailSections(h.Revision, a, route.Select(SectionOp.Add).ToArray()));
            Assert.That(h.Document.LineOf(c).Sections, Is.EqualTo(new[] { a, b, c }));
            Assert.That(TrailLines.Route(h.Document, h.Document.LineOf(c), a), Is.Null, "already part of it");
            // Going up works the same way: from C, A is two sections above.
            h.Undo();
            Assert.That(TrailLines.Route(h.Document, h.Document.LineOf(c), a), Is.EqualTo(new[] { b, a }));
        }

        [Test]
        public void NT26_ASectionThatDoesntMeetAnEndIsRefused()
        {
            var (h, a, _, c) = ThreeRuns();
            var r = h.Submit(new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(c) }));
            Assert.That(r.Accepted, Is.False);
            StringAssert.Contains("top or bottom", r.Reason);
        }

        [Test]
        public void NT32_APlanAndABuiltSectionNeverShareATrail()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var a = AddTrail(h, "Run 1", P(0, 3000), P(0, 2000));
            var b = AddTrail(h, "Run 2", P(0, 2000), P(0, 1000), WorkStage.Plan);
            var r = h.Submit(new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b) }));
            Assert.That(r.Accepted, Is.False);
            StringAssert.Contains("planned or all built", r.Reason);
        }

        [Test]
        public void NT27_ATakenOutEndSectionIsATrailOfItsOwnWithTheNextFreeName()
        {
            var (h, a, b, c) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b), SectionOp.Add(c) }));
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.TakeOut(c) }));
            Assert.That(h.Document.LineOf(a).Sections, Is.EqualTo(new[] { a, b }));
            Assert.That(h.Document.LineOf(c).Saved, Is.False);
            Assert.That(h.Document.FindTrail(c).Name, Is.EqualTo("Run 3"), "two trails, so the next free name is Run 3");
        }

        [Test]
        public void NT24_TakingOutTheFoundingSectionNeverSharesAnId()
        {
            // Ridge is keyed by A, its first section; taking A out leaves Ridge its ID, so A on its own takes the next one.
            var (h, a, b, c) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b), SectionOp.Add(c) }, "Ridge"));
            var ridge = h.Document.LineOf(a).Id;
            Submit(h, new EditTrailSections(h.Revision, b, new[] { SectionOp.TakeOut(a) }));
            Assert.That(h.Document.LineOf(b).Id, Is.EqualTo(ridge));
            Assert.That(h.Document.LineOf(a).Id, Is.Not.EqualTo(ridge));
            var ids = TrailLines.All(h.Document).Select(l => l.Id).ToList();
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
        }

        [Test]
        public void NT28_TakingOutAMiddleSectionMakesUpperAndLower()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            var ids = new List<TrailId>();
            for (int i = 0; i < 4; i++) ids.Add(AddTrail(h, "Run " + (i + 1), P(0, 4000 - 1000 * i), P(0, 3000 - 1000 * i)));
            Submit(h, new EditTrailSections(h.Revision, ids[0], ids.Skip(1).Select(SectionOp.Add).ToArray(), "Ridge"));
            var id = h.Document.LineOf(ids[0]).Id;
            Submit(h, new EditTrailSections(h.Revision, ids[0], new[] { SectionOp.TakeOut(ids[1]) }));
            var doc = h.Document;
            Assert.That(doc.FindTrail(ids[0]).Name, Is.EqualTo("Upper Ridge"));
            Assert.That(doc.LineOf(ids[0]).Saved, Is.False, "one section left at the top is a trail on its own");
            Assert.That(doc.LineOf(ids[2]).Sections, Is.EqualTo(new[] { ids[2], ids[3] }));
            Assert.That(doc.LineOf(ids[2]).Name, Is.EqualTo("Lower Ridge"));
            Assert.That(doc.LineOf(ids[2]).Id, Is.Not.EqualTo(id));
            Assert.That(doc.FindTrail(ids[1]).Name, Does.StartWith("Run "));
        }

        [Test]
        public void NT28_ATrailTakenOverFromTheMiddleOfAnotherBreaksThatOne()
        {
            // NT37: Ridge is A → B → C. Chute ends where B starts, so it can take B, Ridge's middle section; Ridge is left
            // as Upper Ridge (A) and Lower Ridge (C). Spur, which also starts there, is untouched.
            var (h, a, b, c) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b), SectionOp.Add(c) }, "Ridge"));
            var spur = AddTrail(h, "Spur", P(0, 2000), P(800, 1200));
            var chute = AddTrail(h, "Chute", P(-600, 2600), P(0, 2000));
            Submit(h, new EditTrailSections(h.Revision, chute, new[] { SectionOp.Add(b) }));
            var doc = h.Document;
            Assert.That(doc.LineOf(chute).Sections, Is.EqualTo(new[] { chute, b }));
            Assert.That(doc.FindTrail(b).Name, Is.EqualTo("Chute"));
            Assert.That(doc.FindTrail(a).Name, Is.EqualTo("Upper Ridge"));
            Assert.That(doc.FindTrail(c).Name, Is.EqualTo("Lower Ridge"));
            Assert.That(doc.FindTrail(spur).Name, Is.EqualTo("Spur"));
        }

        [Test]
        public void NT40_PositionsAreReplacedNeverStackedAndNamesFit()
        {
            Assert.That(TrailLines.BreakNames("Upper Run 4", 2), Is.EqualTo(new[] { "Upper Run 4", "Lower Run 4" }));
            Assert.That(TrailLines.BreakNames("Middle 2 Run 4", 3), Is.EqualTo(new[] { "Upper Run 4", "Middle Run 4", "Lower Run 4" }));
            Assert.That(TrailLines.BreakNames("Run 4", 4), Is.EqualTo(new[] { "Upper Run 4", "Middle 1 Run 4", "Middle 2 Run 4", "Lower Run 4" }));
            string longName = new string('x', DesignRules.NameMaxLength);
            foreach (string n in TrailLines.BreakNames(longName, 2)) Assert.That(DesignRules.NameProblem(n), Is.Null, n);
            Assert.That(TrailLines.Stem("Upper"), Is.EqualTo("Upper"), "a bare word isn't a position");
        }

        [Test]
        public void NT40_ATypedNameBeatsTheAutomaticOne()
        {
            var (h, a, b, c) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b), SectionOp.Add(c) }, "Ridge"));
            var names = new Dictionary<TrailId, string> { [a] = "Summit Pitch", [b] = "Glades" };
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.TakeOut(b) }, null, names));
            Assert.That(h.Document.FindTrail(a).Name, Is.EqualTo("Summit Pitch"));
            Assert.That(h.Document.FindTrail(b).Name, Is.EqualTo("Glades"));
            Assert.That(h.Document.FindTrail(c).Name, Is.EqualTo("Lower Ridge"));
        }

        [Test]
        public void NT27_NextNameSkipsNamesInUse()
        {
            var h = new DesignHistory(DesignDocument.Empty(1));
            AddTrail(h, "Run 2", P(0, 3000), P(0, 2000));
            Assert.That(TrailLines.NextName(h.Document), Is.EqualTo("Run 3"));
            Assert.That(TrailLines.NextName(h.Document, new[] { "Run 3" }), Is.EqualTo("Run 4"));
        }

        [Test]
        public void NT31_CutThenRejoinGivesTheSameBytesAndNeverMovesGround()
        {
            var (h, a, _, _) = ThreeRuns();
            var before = DesignBytes.Encode(h.Document.FindTrail(a));
            var cut = Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Cut(a, P(0, 2500), 0, -Ft(10)) }));
            var lower = new TrailId(cut.Created[0]);
            Assert.That(h.Document.LineOf(a).Sections, Is.EqualTo(new[] { a, lower }), "a cut adds a node; both pieces stay in the trail");
            Assert.That(h.Document.FindTrail(lower).Name, Is.EqualTo("Run 1"));
            Assert.That(TrailChains.Of(h.Document.Trails).Any(ch => ch.Count == 2), Is.True, "the two sides grade as one (NT2)");
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Rejoin(a, lower) }));
            Assert.That(DesignBytes.Same(DesignBytes.Encode(h.Document.FindTrail(a)), before), Is.True, "the trail as it was painted");
            Assert.That(h.Document.FindTrail(lower), Is.Null);
            Assert.That(h.Document.Lines, Is.Empty);
        }

        [Test]
        public void NT31_OnlyACutCanBeRejoined()
        {
            var (h, a, b, _) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b) }));
            var r = h.Submit(new EditTrailSections(h.Revision, a, new[] { SectionOp.Rejoin(a, b) }));
            Assert.That(r.Accepted, Is.False);
            StringAssert.Contains("painted apart", r.Reason);
        }

        [Test]
        public void NT39_ASessionIsOneUndoStep()
        {
            var (h, a, b, c) = ThreeRuns();
            var before = DesignBytes.Encode(h.Document);
            Submit(h, new EditTrailSections(h.Revision, a, new[]
            {
                SectionOp.Add(b), SectionOp.Add(c), SectionOp.Cut(b, P(0, 1500), 0, -Ft(10)), SectionOp.TakeOut(c),
            }, "Ridge"));
            Assert.That(h.Document.LineOf(a).Sections.Count, Is.EqualTo(3));
            h.Undo();
            Assert.That(DesignBytes.Same(DesignBytes.Encode(h.Document), before), Is.True);
        }

        [Test]
        public void NT32_BreakGroundBuildsTheWholeTrail()
        {
            var (h, a, b, _) = ThreeRuns(WorkStage.Plan);
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b) }));
            Submit(h, new BreakGround(h.Revision, new[] { a }, null));
            Assert.That(h.Document.FindTrail(b).Stage, Is.EqualTo(WorkStage.Built));
            Assert.That(h.Document.LineOf(a).Sections.Count, Is.EqualTo(2), "still one trail");
        }

        [Test]
        public void NT33_RemoveTrailRemovesEverySection()
        {
            var (h, a, b, c) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b) }));
            Submit(h, new DeleteTrail(h.Revision, b));
            Assert.That(h.Document.Trails.Select(t => t.Id), Is.EqualTo(new[] { c }));
            Assert.That(h.Document.Lines, Is.Empty);
        }

        [Test]
        public void NT24_RenamingASectionRenamesItsTrail()
        {
            var (h, a, b, _) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b) }));
            Submit(h, new RenameTrail(h.Revision, b, "Ridge"));
            Assert.That(h.Document.FindTrail(a).Name, Is.EqualTo("Ridge"));
            Assert.That(h.Document.LineOf(a).Name, Is.EqualTo("Ridge"));
        }

        [Test]
        public void NT28_MovingASectionsEndsSoItNoLongerMeetsBreaksTheTrail()
        {
            var (h, a, b, c) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b), SectionOp.Add(c) }, "Ridge"));
            Submit(h, new SetTrailEnds(h.Revision, c, P(400, 900), P(400, 0)));
            Assert.That(h.Document.LineOf(a).Sections, Is.EqualTo(new[] { a, b }));
            Assert.That(h.Document.LineOf(a).Name, Is.EqualTo("Upper Ridge"));
            Assert.That(h.Document.FindTrail(c).Name, Is.EqualTo("Lower Ridge"));
        }

        [Test]
        public void NT24_ABrokenFileIsRepairedOnLoadNotRefused()
        {
            var (h, a, b, c) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b), SectionOp.Add(c) }, "Ridge"));
            var doc = h.Document;
            // A hand-edited file: B left out of the middle, so the saved trail no longer runs end to end.
            var broken = new[] { new TrailLine(doc.Lines[0].Id, "Ridge", new[] { a, c }) };
            var read = DesignDocument.From(doc.Seed, doc.NextOrdinal, doc.Trails, doc.Lifts, doc.Strokes, doc.Areas, broken);
            Assert.That(read.Lines, Is.Empty);
            Assert.That(read.FindTrail(a).Name, Is.EqualTo("Upper Ridge"));
            Assert.That(read.FindTrail(c).Name, Is.EqualTo("Lower Ridge"));
            Assert.That(read.FindTrail(b).Name, Is.EqualTo("Ridge"));
        }

        [Test]
        public void NT24_LinesRoundTripThroughTheCanonicalBytes()
        {
            var (h, a, b, c) = ThreeRuns();
            Submit(h, new EditTrailSections(h.Revision, a, new[] { SectionOp.Add(b), SectionOp.Add(c) }, "Ridge"));
            var bytes = DesignBytes.Encode(h.Document);
            var back = DesignBytes.Decode(bytes);
            Assert.That(back.Lines.Single().Sections, Is.EqualTo(new[] { a, b, c }));
            Assert.That(DesignBytes.Same(DesignBytes.Encode(back), bytes), Is.True);
        }
    }
}
