using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Measure;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Task 4.07 B1 part 2c (SN-20 v4): every trail and shaped trail has a switch, Auto, Open or Closed. It's a field of its
    // own (never in the settings), the shaped trail's wins, one undoable command sets many, and split, join and Edit
    // sections carry it.
    public sealed class TrailSwitchTests
    {
        static DesignHistory New() => new DesignHistory(DesignDocument.Empty(0x4_07));

        static TrailId Add(DesignHistory h, string name, int x = 0)
        {
            var r = h.Submit(new AddTrail(h.Revision, name, TrailSettings.GradedRun, Straight(P(x, 3000), P(x, 1000)), P(x, 3000), P(x, 1000),
                                          WorkStage.Built, SlopeBands.Band.Blue));
            Assert.That(r.Accepted, Is.True, r.ToString());
            return new TrailId(r.Created[0]);
        }

        /// <summary>Two runs, "A" above and "B" below it, shaped into one trail.</summary>
        static (DesignHistory h, TrailId a, TrailId b) Shaped()
        {
            var h = New();
            var a = Add(h, "A");
            var rb = h.Submit(new AddTrail(h.Revision, "B", TrailSettings.GradedRun, Straight(P(0, 1000), P(0, 0)), P(0, 1000), P(0, 0), WorkStage.Built,
                                           SlopeBands.Band.Blue));
            Assert.That(rb.Accepted, Is.True, rb.ToString());
            var b = new TrailId(rb.Created[0]);
            var ts = Sections(h.Document);
            var route = TrailSectionsEdit.Route(ts, ts.TrailOf(a), ts.SegmentsOf(b)[0]);
            Assert.That(h.Submit(TrailSectionsEdit.Add(h.Document, h.Revision, ts, ts.TrailOf(a), route)).Accepted, Is.True);
            return (h, a, b);
        }

        [Test]
        public void SW01_ANewTrailIsAutoAndOneCommandSetsManyAsOneUndoStep()
        {
            var h = New();
            var a = Add(h, "A");
            var b = Add(h, "B", 500);
            Assert.That(h.Document.FindTrail(a).Switch, Is.EqualTo(TrailSwitch.Auto));
            var close = h.Submit(new SetTrailSwitch(h.Revision, new[] { a, b }, null, TrailSwitch.Closed));
            Assert.That(close.Accepted, Is.True, close.ToString());
            Assert.That(h.Document.Trails.All(t => t.Switch == TrailSwitch.Closed), Is.True);
            Assert.That(h.Document.FindTrail(a).Settings, Is.EqualTo(TrailSettings.GradedRun), "the switch never touches the settings");
            Assert.That(h.Undo(), Is.True);
            Assert.That(h.Document.Trails.All(t => t.Switch == TrailSwitch.Auto), Is.True, "one undo step for both");
            Assert.That(new SetTrailSwitch(0, null, null, TrailSwitch.Open).Label, Is.EqualTo("Open trails"));
        }

        [Test]
        public void SW02_TheShapedTrailsSwitchWins()
        {
            var (h, a, b) = Shaped();
            var ts = Sections(h.Document);
            var trail = ts.TrailOf(a);
            Assert.That(trail.Line, Is.Not.Null);
            Assert.That(h.Submit(new SetTrailSwitch(h.Revision, new[] { a, b }, null, TrailSwitch.Closed)).Accepted, Is.True);
            Assert.That(Sections(h.Document).TrailOf(a).Switch, Is.EqualTo(TrailSwitch.Auto), "the runs' own switches don't show through");
            var open = h.Submit(SetTrailSwitch.For(h.Revision, Sections(h.Document).TrailOf(a), TrailSwitch.Open));
            Assert.That(open.Accepted, Is.True, open.ToString());
            ts = Sections(h.Document);
            Assert.That(ts.TrailOf(a).Switch, Is.EqualTo(TrailSwitch.Open));
            Assert.That(ts.TrailOf(b).Switch, Is.EqualTo(TrailSwitch.Open));
            Assert.That(h.Document.Lines.Single().Switch, Is.EqualTo(TrailSwitch.Open));
        }

        [Test]
        public void SW03_EditSectionsCarriesTheSwitch()
        {
            var (h, a, b) = Shaped();
            Assert.That(h.Submit(SetTrailSwitch.For(h.Revision, Sections(h.Document).TrailOf(a), TrailSwitch.Closed)).Accepted, Is.True);
            var ts = Sections(h.Document);
            Assert.That(h.Submit(TrailSectionsEdit.Rename(h.Document, h.Revision, ts, ts.TrailOf(a), "Renamed")).Accepted, Is.True);
            ts = Sections(h.Document);
            Assert.That(ts.TrailOf(a).Switch, Is.EqualTo(TrailSwitch.Closed), "rename keeps it");
            Assert.That(h.Submit(TrailSectionsEdit.Post(h.Document, h.Revision, ts, ts.TrailOf(a), SlopeBands.Band.Black)).Accepted, Is.True);
            ts = Sections(h.Document);
            Assert.That(ts.TrailOf(a).Switch, Is.EqualTo(TrailSwitch.Closed), "posting keeps it");
            var takeOut = h.Submit(TrailSectionsEdit.TakeOut(h.Document, h.Revision, ts, ts.TrailOf(a), ts.SegmentsOf(b)[0]));
            Assert.That(takeOut.Accepted, Is.True, takeOut.ToString());
            ts = Sections(h.Document);
            Assert.That(ts.TrailOf(a).Switch, Is.EqualTo(TrailSwitch.Closed), "the trail that shrinks keeps it");
        }

        [Test]
        public void SW04_SplitAndJoinKeepTheSwitch()
        {
            var h = New();
            var a = Add(h, "A");
            Assert.That(h.Submit(new SetTrailSwitch(h.Revision, new[] { a }, null, TrailSwitch.Open)).Accepted, Is.True);
            var split = h.Submit(new SplitTrail(h.Revision, a, P(0, 2000), 0, -1000));
            Assert.That(split.Accepted, Is.True, split.ToString());
            var lower = new TrailId(split.Created[0]);
            Assert.That(h.Document.FindTrail(a).Switch, Is.EqualTo(TrailSwitch.Open));
            Assert.That(h.Document.FindTrail(lower).Switch, Is.EqualTo(TrailSwitch.Open), "the lower part comes along");
            Assert.That(h.Submit(new JoinTrails(h.Revision, a, lower)).Accepted, Is.True);
            Assert.That(h.Document.FindTrail(a).Switch, Is.EqualTo(TrailSwitch.Open));
        }

        [Test]
        public void SW06_OnlyASwitchEditGoesAtOnce()
        {
            var (h, a, b) = Shaped();
            var before = h.Document;
            Assert.That(h.Submit(SetTrailSwitch.For(h.Revision, Sections(h.Document).TrailOf(a), TrailSwitch.Closed)).Accepted, Is.True);
            Assert.That(SetTrailSwitch.OnlySwitchesDiffer(before, h.Document), Is.True, "a switch edit");
            Assert.That(SetTrailSwitch.OnlySwitchesDiffer(before, before), Is.False, "no change at all");
            var switched = h.Document;
            var ts = Sections(h.Document);
            Assert.That(h.Submit(TrailSectionsEdit.Rename(h.Document, h.Revision, ts, ts.TrailOf(a), "Renamed")).Accepted, Is.True);
            Assert.That(SetTrailSwitch.OnlySwitchesDiffer(switched, h.Document), Is.False, "a rename waits for the network");
            Assert.That(SetTrailSwitch.OnlySwitchesDiffer(before, h.Document), Is.False, "a switch and a rename together wait too");
        }

        [Test]
        public void SW05_AnUnknownTrailOrSwitchIsRefused()
        {
            var h = New();
            var a = Add(h, "A");
            var missing = h.Submit(new SetTrailSwitch(h.Revision, new[] { new TrailId(a.Value + 99) }, null, TrailSwitch.Closed));
            Assert.That(missing.Accepted, Is.False);
            var bad = h.Submit(new SetTrailSwitch(h.Revision, new[] { a }, null, (TrailSwitch)3));
            Assert.That(bad.Accepted, Is.False);
            Assert.That(DesignRules.SwitchProblem((TrailSwitch)3), Is.Not.Null);
            Assert.That(h.Document.FindTrail(a).Switch, Is.EqualTo(TrailSwitch.Auto), "nothing changed");
        }
    }
}
