using System.Linq;
using System.Text;
using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // demo.bat 61 (task P3-03): a readable walk through the design document, printed as the test runs.
    public sealed class DesignDemoTests
    {
        [Test]
        public void DesignDocumentWalkthrough()
        {
            var log = new StringBuilder();
            void Say(string s) => log.AppendLine(s);
            var h = new DesignHistory(DesignDocument.Empty(2026));
            void State(string what) => Say($"  r{h.Revision,-3} {what,-46} trails {h.Document.Trails.Count}, lifts {h.Document.Lifts.Count}, undo {h.UndoCount}, redo {h.RedoCount}");

            Say("Mountain Planner design document (task P3-03)");
            Say("Every change is a command against the current revision; undo and redo restore exact snapshots.");
            Say("");
            State("an empty design");
            var chair = AddLift(h, "Summit Chair", P(0, 0), P(1500, 4000), WorkStage.Built, Tower(600, 35), Tower(1400, 40), Tower(2400, 30));
            State("Break ground: Summit Chair, 3 towers");
            var run = AddTrail(h, "Upper Ridge", P(1520, 4000), P(10, 20));
            State("Break ground: Upper Ridge, head at the top");
            var lower = new TrailId(h.Submit(new SplitTrail(h.Revision, run, P(750, 2000), Ft(-1510), Ft(-3980))).Created[0]);
            State($"Split: \"{h.Document.FindTrail(run).Name}\" + \"{h.Document.FindTrail(lower).Name}\"");
            var splitBytes = Bytes(h);
            h.Submit(new JoinTrails(h.Revision, run, lower));
            State("Join them back");
            h.Undo();
            State($"Undo  (bytes match the split: {DesignBytes.Same(Bytes(h), splitBytes)})");
            h.Redo();
            State("Redo");
            var tower = h.Document.FindLift(chair).Towers[1].Id;
            h.Submit(new EditTower(h.Revision, tower, Ft(1450), Ft(45)));
            State("Tower 2: slide 50 ft, Page Up (+5 ft)");

            long old = h.Revision - 1;
            var stale = h.Submit(new RenameTrail(old, run, "Ridge"));
            Say($"  stale  Rename against r{old}: {stale}");
            var bad = h.Submit(new EditTower(h.Revision, tower, Ft(1450), Ft(105)));
            Say($"  bad    Tower to 105 ft: {bad}");
            var plan = h.Submit(new AddTrail(h.Revision, "Back Bowl", TrailSettings.NaturalRun, Straight(P(2000, 3800), P(2600, 600)),
                P(2000, 3800), P(2600, 600), WorkStage.Plan));
            State("Save plan: Back Bowl (natural, orange)");
            h.Submit(new BreakGround(h.Revision, new[] { new TrailId(plan.Created[0]) }, null));
            State("Break ground on the plan");

            var graph = DesignNetwork.Build(h.Document);
            Say("");
            Say($"Network (derived, never stored): {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
            foreach (var e in graph.Edges)
            {
                string owner = e.Kind == EdgeKind.Lift ? h.Document.FindLift(new LiftId(e.Owner)).Name : h.Document.FindTrail(new TrailId(e.Owner)).Name;
                graph.TryGetNode(e.From, out var from);
                graph.TryGetNode(e.To, out var to);
                Say($"  {e.Kind,-12} {owner,-14} {from.Kind} -> {to.Kind}");
            }
            Say("");
            Say($"Document bytes: {Bytes(h).Length} bytes, SHA-256 {Sha(Bytes(h)).Substring(0, 16)}…");
            Say($"Same script again gives the same bytes: {DesignBytes.Same(Bytes(h), Bytes(Replay()))}");
            Say($"Golden design: {Sha(Bytes(Scripted())).Substring(0, 16)}… ({Scripted().Document.Trails.Count} trails: "
                + string.Join(", ", Scripted().Document.Trails.Select(t => t.Name)) + ")");

            TestContext.Out.Write(log.ToString());
            Assert.That(stale.Code, Is.EqualTo(RejectionCode.Stale));
            Assert.That(bad.Accepted, Is.False);
            Assert.That(graph.Edges.Count(e => e.Kind == EdgeKind.TrailSegment), Is.EqualTo(2));
        }

        static DesignHistory Replay()
        {
            var h = new DesignHistory(DesignDocument.Empty(2026));
            var chair = AddLift(h, "Summit Chair", P(0, 0), P(1500, 4000), WorkStage.Built, Tower(600, 35), Tower(1400, 40), Tower(2400, 30));
            var run = AddTrail(h, "Upper Ridge", P(1520, 4000), P(10, 20));
            var lower = new TrailId(h.Submit(new SplitTrail(h.Revision, run, P(750, 2000), Ft(-1510), Ft(-3980))).Created[0]);
            h.Submit(new JoinTrails(h.Revision, run, lower));
            h.Undo();
            h.Redo();
            h.Submit(new EditTower(h.Revision, h.Document.FindLift(chair).Towers[1].Id, Ft(1450), Ft(45)));
            var plan = h.Submit(new AddTrail(h.Revision, "Back Bowl", TrailSettings.NaturalRun, Straight(P(2000, 3800), P(2600, 600)),
                P(2000, 3800), P(2600, 600), WorkStage.Plan));
            h.Submit(new BreakGround(h.Revision, new[] { new TrailId(plan.Created[0]) }, null));
            return h;
        }
    }
}
