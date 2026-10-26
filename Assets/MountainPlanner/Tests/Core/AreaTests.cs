using System.Linq;
using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task P3-10: Areas (NW-15, owner NT19): hubs with two or more lifts, their default names, the player's names and how a
    // name follows its hub; an Area's reach (one larger node).
    public sealed class AreaTests
    {
        static readonly EarthworkSite Site = new EarthworkSite(new Domain.Edits.EditLattice(-5000, 5000), 0, 0);

        static AreaEnd[] EndsOf(DesignDocument doc, LiftId lift, bool bottom)
        {
            var st = DesignStations.Of(DesignStations.Of(doc), lift, bottom);
            return st.Members.Select(m => new AreaEnd(m.Lift, m.Bottom)).ToArray();
        }

        static Area AreaOf(DesignDocument doc, LiftId lift, bool bottom)
        {
            var stations = DesignStations.Of(doc);
            var st = DesignStations.Of(stations, lift, bottom);
            return DesignAreas.Of(doc, stations).SingleOrDefault(a => a.Station == st);
        }

        static AreaId Name(DesignHistory h, AreaId record, AreaEnd[] ends, string name)
        {
            var r = h.Submit(new RenameArea(h.Revision, record, ends, name));
            Assert.That(r.Accepted, Is.True, r.ToString());
            return record.IsNone ? new AreaId(r.Created[0]) : record;
        }

        [Test]
        public void NW15_TwoLiftsMakeAnAreaWithADefaultName()
        {
            var h = new DesignHistory(DesignDocument.Empty(5));
            var one = AddLift(h, "Chair 1", P(0, 0), P(-1500, 3000));
            var lone = AddLift(h, "Chair 9", P(4000, 0), P(4000, 3000));
            Assert.That(AreaOf(h.Document, one, true), Is.Null, "one lift: a station, not an Area");
            var two = AddLift(h, "Chair 2", P(150, 0), P(1500, 3000));
            var area = AreaOf(h.Document, one, true);
            Assert.That(area, Is.Not.Null);
            Assert.That(area.Name, Is.EqualTo("Chair 1 & Chair 2 base"));
            Assert.That(area.Named, Is.False);
            Assert.That(AreaOf(h.Document, lone, true), Is.Null);
            AddLift(h, "Chair 3", P(-1650, 3100), P(-1600, 6000));   // loads by Chair 1's top
            Assert.That(AreaOf(h.Document, one, false).Name, Is.EqualTo("Chair 1 & Chair 3 transfer"));
            Assert.That(AreaOf(h.Document, two, true).Station.Lifts, Is.EquivalentTo(new[] { one, two }));
        }

        [Test]
        public void NW15_ANameFollowsItsHubThroughGrowthMergesAndSplits()
        {
            var h = new DesignHistory(DesignDocument.Empty(5));
            var one = AddLift(h, "Chair 1", P(0, 0), P(-1500, 3000));
            var two = AddLift(h, "Chair 2", P(150, 0), P(1500, 3000));
            var base1 = Name(h, default, EndsOf(h.Document, one, true), "Village");
            Assert.That(AreaOf(h.Document, one, true).Name, Is.EqualTo("Village"));

            // Growth: a third lift joins; the name stays.
            var three = AddLift(h, "Chair 3", P(75, 150), P(75, 4000));
            Assert.That(AreaOf(h.Document, three, true).Name, Is.EqualTo("Village"));

            // A second named hub 350 ft away; a lift between them, 175 ft from each, merges the two: the older name wins.
            var four = AddLift(h, "Chair 4", P(500, 0), P(2500, 3000));
            var five = AddLift(h, "Chair 5", P(650, 0), P(3500, 3000));
            Name(h, default, EndsOf(h.Document, four, true), "Lodge");
            Assert.That(AreaOf(h.Document, four, true).Name, Is.EqualTo("Lodge"));
            var bridge = AddLift(h, "Chair 6", P(325, 0), P(325, 5000));
            Assert.That(AreaOf(h.Document, four, true).Station.Id, Is.EqualTo(AreaOf(h.Document, one, true).Station.Id), "one hub now");
            Assert.That(AreaOf(h.Document, four, true).Name, Is.EqualTo("Village"), "the older name wins");

            // The bridge goes: the hubs split and each has its name again.
            h.Submit(new DeleteLift(h.Revision, bridge));
            Assert.That(AreaOf(h.Document, one, true).Name, Is.EqualTo("Village"));
            Assert.That(AreaOf(h.Document, five, true).Name, Is.EqualTo("Lodge"));
            Assert.That(AreaOf(h.Document, two, true).Station.Lifts, Has.Member(two));
            Assert.That(h.Document.FindArea(base1).Name, Is.EqualTo("Village"));
        }

        [Test]
        public void NW15_RenamingIsOneUndoableStepAndSurvivesAReload()
        {
            var h = new DesignHistory(DesignDocument.Empty(5));
            var one = AddLift(h, "Chair 1", P(0, 0), P(-1500, 3000));
            AddLift(h, "Chair 2", P(150, 0), P(1500, 3000));
            var id = Name(h, default, EndsOf(h.Document, one, true), "Village");
            Assert.That(h.UndoLabel, Is.EqualTo("Name area"));
            Name(h, id, EndsOf(h.Document, one, true), "Base Camp");
            Assert.That(AreaOf(h.Document, one, true).Name, Is.EqualTo("Base Camp"));
            h.Undo();
            Assert.That(AreaOf(h.Document, one, true).Name, Is.EqualTo("Village"));
            h.Undo();
            Assert.That(AreaOf(h.Document, one, true).Named, Is.False);
            h.Redo();
            var reloaded = DesignBytes.Decode(DesignBytes.Encode(h.Document));
            Assert.That(AreaOf(reloaded, one, true).Name, Is.EqualTo("Village"));
            Assert.That(h.Submit(new RenameArea(h.Revision, id, EndsOf(h.Document, one, true), " ")).Accepted, Is.False, "a name can't be blank");
        }

        [Test]
        public void NW15_DeletingItsLiftsForgetsTheName()
        {
            var h = new DesignHistory(DesignDocument.Empty(5));
            var one = AddLift(h, "Chair 1", P(0, 0), P(-1500, 3000));
            var two = AddLift(h, "Chair 2", P(150, 0), P(1500, 3000));
            Name(h, default, EndsOf(h.Document, one, true), "Village");
            h.Submit(new DeleteLift(h.Revision, two));
            Assert.That(h.Document.Areas.Single().Ends.Select(e => e.Lift), Is.EqualTo(new[] { one }), "Chair 2's end goes");
            h.Submit(new DeleteLift(h.Revision, one));
            Assert.That(h.Document.Areas, Is.Empty, "no ends left: the name goes");
        }

        [Test]
        public void NW15_AnAreasReachRidesItsLifts()
        {
            // A base Area of two lifts: nothing runs down from the base itself, but each lift's runs are reached from it.
            var h = new DesignHistory(DesignDocument.Empty(5));
            var one = AddLift(h, "Chair 1", P(0, 0), P(-1500, 3000));
            var two = AddLift(h, "Chair 2", P(150, 0), P(1500, 3000));
            var west = AddTrail(h, "West", P(-1480, 2980), P(10, 20));
            var east = AddTrail(h, "East", P(1480, 2980), P(140, 20));
            var report = NetworkReport.Build(h.Document, new TrailNetworkGeometry(Site));
            var area = report.Areas.Single();
            Assert.That(area.Area.Name, Is.EqualTo("Chair 1 & Chair 2 base"));
            Assert.That(area.TrailsHere, Is.EquivalentTo(new[] { west, east }));
            Assert.That(area.Reach.Trails, Is.EquivalentTo(new[] { west, east }));
            Assert.That(area.Reach.Lifts, Is.EquivalentTo(new[] { one, two }));
            Assert.That(report.ReachFromArea(area.Area.Station.Id).Trails, Is.EquivalentTo(new[] { west, east }));
            Assert.That(report.ReachFrom(area.Area.Station.Id).Trails, Is.Empty, "a plain descent from the base reaches nothing (no path between runs that both end in it)");
        }
    }
}
