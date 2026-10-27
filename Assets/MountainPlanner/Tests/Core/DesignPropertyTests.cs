using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests
{
    // Property tests over keyed-random command sequences (task P3-03; CF-05, SH-04). Each seed plays a sequence of
    // valid, invalid and stale commands with undos and redos, and checks the invariants after every step.
    public sealed class DesignPropertyTests
    {
        const int Seeds = 40;
        const int Steps = 120;

        [Test]
        public void CF05_EveryCommandUndoesAndRedoesExactly([Range(0, Seeds - 1)] int seed)
        {
            var rng = new TestRng((ulong)seed);
            var h = new DesignHistory(DesignDocument.Empty((ulong)seed * 977));
            int accepted = 0;
            for (int step = 0; step < Steps; step++)
            {
                var before = Bytes(h);
                long revision = h.Revision;
                int roll = rng.Int(100);
                if (roll < 10)
                {
                    if (h.Undo()) Assert.That(h.Revision, Is.GreaterThan(revision));
                    continue;
                }
                if (roll < 18)
                {
                    h.Redo();
                    continue;
                }

                bool stale = roll < 24 && revision > 1;
                var command = RandomCommand(ref rng, h.Document, stale ? revision - 1 - rng.Int((int)System.Math.Min(revision - 1, 5)) : revision);
                var result = h.Submit(command);
                if (stale)
                {
                    Assert.That(result.Code, Is.EqualTo(RejectionCode.Stale), $"seed {seed} step {step}");
                }
                if (!result.Accepted)
                {
                    Assert.That(h.Revision, Is.EqualTo(revision), "a rejection doesn't move the revision");
                    Assert.That(DesignBytes.Same(Bytes(h), before), Is.True, $"seed {seed} step {step}: {command.Label} rejected but changed the document");
                    continue;
                }
                accepted++;
                var after = Bytes(h);
                Assert.That(DesignBytes.Same(after, before), Is.False, $"{command.Label} was accepted but changed nothing");

                // Undo restores the bytes exactly; redo brings the change back exactly.
                Assert.That(h.Undo(), Is.True);
                Assert.That(DesignBytes.Same(Bytes(h), before), Is.True, $"seed {seed} step {step}: undo of {command.Label}");
                Assert.That(h.Redo(), Is.True);
                Assert.That(DesignBytes.Same(Bytes(h), after), Is.True, $"seed {seed} step {step}: redo of {command.Label}");

                // The canonical bytes round-trip.
                Assert.That(DesignBytes.Same(DesignBytes.Encode(DesignBytes.Decode(after)), after), Is.True);
            }
            Assert.That(accepted, Is.GreaterThan(Steps / 4), "the generator should mostly make valid commands");
        }

        [Test]
        public void NT24_TheGeneratorReachesTrailsOfSeveralSections()
        {
            int sessions = 0, saved = 0;
            for (int seed = 0; seed < Seeds; seed++)
            {
                var rng = new TestRng((ulong)seed);
                var h = new DesignHistory(DesignDocument.Empty((ulong)seed * 977));
                for (int step = 0; step < Steps; step++)
                {
                    var command = RandomCommand(ref rng, h.Document, h.Revision);
                    if (h.Submit(command).Accepted && command is EditTrailSections) sessions++;
                    if (h.Document.Lines.Count > 0) saved++;
                }
            }
            Assert.That(sessions, Is.GreaterThan(0), "accepted Edit sections sessions");
            Assert.That(saved, Is.GreaterThan(0), "steps with a saved trail of several sections");
        }

        [Test]
        public void SH04_ReplayingTheSameCommandsGivesTheSameBytes([Range(0, 9)] int seed)
        {
            Assert.That(DesignBytes.Same(Play(seed), Play(seed)), Is.True);
        }

        static byte[] Play(int seed)
        {
            var rng = new TestRng((ulong)seed + 1000);
            var h = new DesignHistory(DesignDocument.Empty((ulong)seed));
            for (int i = 0; i < Steps; i++)
            {
                if (rng.Chance(10)) h.Undo();
                else h.Submit(RandomCommand(ref rng, h.Document, h.Revision));
            }
            return Bytes(h);
        }

        /// <summary>A command over the current document: mostly valid, some deliberately broken.</summary>
        static DesignCommand RandomCommand(ref TestRng rng, DesignDocument doc, long revision)
        {
            var trails = doc.Trails;
            var lifts = doc.Lifts;
            int kind = rng.Int(16);
            if (trails.Count == 0 && kind >= 2 && kind < 9) kind = 0;
            if (lifts.Count == 0 && kind >= 10) kind = 9;
            switch (kind)
            {
                case 0:
                case 1:
                {
                    var head = RandomPoint(ref rng);
                    var tail = new PointMm(head.X + rng.Range(-Ft(500), Ft(500)), head.Y - rng.Range(Ft(200), Ft(3000)));
                    bool ends = rng.Chance(85);
                    var stage = ends && rng.Chance(70) ? WorkStage.Built : WorkStage.Plan;
                    int width = rng.Chance(95) ? Ft(25 + 5 * rng.Int(76)) : Ft(410); // some too wide
                    var parts = new[] { new FootprintPart(new[] { new Stroke(width, false, new[] { head, tail }) }) };
                    return new AddTrail(revision, $"Run {rng.Int(1000)}", TrailSettings.GradedRun, parts, ends ? head : (PointMm?)null, ends ? tail : (PointMm?)null, stage);
                }
                case 2:
                    return new RenameTrail(revision, rng.Pick(trails).Id, rng.Chance(90) ? $"Run {rng.Int(1000)}" : "");
                case 3:
                {
                    var t = rng.Pick(trails);
                    var s = rng.Chance(80)
                        ? (t.Settings.Grading == TrailGrading.Graded ? TrailSettings.NaturalRun : TrailSettings.GradedRun)
                        : new TrailSettings(TrailGrading.Graded, TrailKind.Glade);
                    return new SetTrailSettings(revision, t.Id, s);
                }
                case 4:
                {
                    var t = rng.Pick(trails);
                    if (!t.HasEnds) return new SetTrailEnds(revision, t.Id, RandomPoint(ref rng), RandomPoint(ref rng));
                    var h = t.Head.Value;
                    var tl = t.Tail.Value;
                    // A split near the middle, the centreline taken as the straight line head to tail.
                    var at = new PointMm((int)(((long)h.X + tl.X) / 2), (int)(((long)h.Y + tl.Y) / 2));
                    int dx = (tl.X - h.X) / 8, dy = (tl.Y - h.Y) / 8;
                    if (rng.Chance(15)) dy = -dy; // sometimes pointing the wrong way
                    return new SplitTrail(revision, t.Id, at, dx, dy);
                }
                case 5:
                {
                    var upper = rng.Pick(trails);
                    foreach (var lower in trails)
                        if (lower.Id != upper.Id && upper.Tail.HasValue && lower.Head.HasValue && upper.Tail.Value.Within(lower.Head.Value, DesignUnits.SnapMm))
                            return new JoinTrails(revision, upper.Id, lower.Id);
                    return new JoinTrails(revision, upper.Id, rng.Pick(trails).Id);
                }
                case 6:
                {
                    var t = rng.Pick(trails);
                    var p = t.Head ?? RandomPoint(ref rng);
                    var stroke = new Stroke(Ft(25 + 5 * rng.Int(20)), true, new[] { p });
                    var parts = new List<FootprintPart>(t.Parts);
                    var first = parts[0];
                    var strokes = new List<Stroke>(first.Strokes) { stroke };
                    parts[0] = new FootprintPart(strokes, first.Cuts);
                    return new RepaintTrail(revision, t.Id, parts);
                }
                case 7:
                    return new DeleteTrail(revision, rng.Pick(trails).Id);
                case 8:
                {
                    var plans = new List<TrailId>();
                    foreach (var t in trails) if (t.Stage == WorkStage.Plan && (t.HasEnds || rng.Chance(20))) plans.Add(t.Id);
                    var liftPlans = new List<LiftId>();
                    foreach (var l in lifts) if (l.Stage == WorkStage.Plan) liftPlans.Add(l.Id);
                    return new BreakGround(revision, plans, liftPlans);
                }
                case 9:
                {
                    var bottom = RandomPoint(ref rng);
                    var top = new PointMm(bottom.X + rng.Range(-Ft(800), Ft(800)), bottom.Y + rng.Range(Ft(1000), Ft(5000)));
                    var towers = new List<TowerSpec>();
                    int n = rng.Int(6);
                    for (int i = 0; i < n; i++) towers.Add(new TowerSpec(Ft(150 * (i + 1)), Ft(25 + 5 * rng.Int(rng.Chance(95) ? 16 : 20))));
                    var stage = rng.Chance(60) ? WorkStage.Built : WorkStage.Plan;
                    return new AddLift(revision, $"Chair {rng.Int(100)}", rng.Chance(50) ? LiftCatalogIds.SesselliftFgq4 : LiftCatalogIds.MontaFg4,
                        bottom, top, TowerMode.Automatic, TowerStance.Plumb, towers, stage);
                }
                case 10:
                {
                    var l = rng.Pick(lifts);
                    return new AddTower(revision, l.Id, new TowerSpec(Ft(50 + 5 * rng.Int(200)), Ft(25 + 5 * rng.Int(16))));
                }
                case 11:
                {
                    var l = rng.Pick(lifts);
                    if (l.Towers.Count == 0) return new SetTowerStance(revision, l.Id, l.Stance == TowerStance.Plumb ? TowerStance.Leaned : TowerStance.Plumb);
                    var t = rng.Pick(l.Towers);
                    return new EditTower(revision, t.Id, t.AlongMm + Ft(5) * rng.Range(-3, 3), Ft(25 + 5 * rng.Int(16)));
                }
                case 12:
                {
                    var l = rng.Pick(lifts);
                    if (l.Towers.Count == 0) return new SetTowerMode(revision, l.Id, l.TowerMode == TowerMode.Automatic ? TowerMode.ByHand : TowerMode.Automatic);
                    return new DeleteTower(revision, rng.Pick(l.Towers).Id);
                }
                case 13:
                {
                    var l = rng.Pick(lifts);
                    var keep = new List<TowerId>();
                    foreach (var t in l.Towers) if (rng.Chance(50)) keep.Add(t.Id);
                    var added = new List<TowerSpec>();
                    for (int i = 0; i < 3; i++) added.Add(new TowerSpec(Ft(50 + 5 * rng.Int(200)), Ft(30)));
                    return new ReplaceTowers(revision, l.Id, keep, added, rng.Chance(50) ? TowerMode.Automatic : (TowerMode?)null);
                }
                case 14:
                    if (rng.Chance(50) && trails.Count > 0) return RandomSections(ref rng, doc, revision);   // NT24–NT40
                    return new RenameLift(revision, rng.Pick(lifts).Id, $"Chair {rng.Int(1000)}");
                default:
                    return new DeleteLift(revision, rng.Pick(lifts).Id);
            }
        }

        /// <summary>An Edit sections session: a routed add, a take-out, a cut or a rejoin, valid or not (owner NT24–NT40).</summary>
        static DesignCommand RandomSections(ref TestRng rng, DesignDocument doc, long revision)
        {
            var t = rng.Pick(doc.Trails);
            var line = doc.LineOf(t.Id);
            var ops = new List<SectionOp>();
            int steps = 1 + rng.Int(3);
            for (int i = 0; i < steps; i++)
            {
                var other = rng.Pick(doc.Trails);
                switch (rng.Int(4))
                {
                    case 0:
                        var route = TrailLines.Route(doc, line, other.Id);
                        if (route != null) foreach (var s in route) ops.Add(SectionOp.Add(s));
                        else ops.Add(SectionOp.Add(other.Id));
                        break;
                    case 1:
                        ops.Add(SectionOp.TakeOut(rng.Pick(line.Sections)));
                        break;
                    case 2:
                        var cutAt = t.Head.HasValue && t.Tail.HasValue
                            ? new PointMm((int)(((long)t.Head.Value.X + t.Tail.Value.X) / 2), (int)(((long)t.Head.Value.Y + t.Tail.Value.Y) / 2))
                            : RandomPoint(ref rng);
                        ops.Add(SectionOp.Cut(t.Id, cutAt, t.HasEnds ? t.Tail.Value.X - t.Head.Value.X : 1, t.HasEnds ? t.Tail.Value.Y - t.Head.Value.Y : 0));
                        break;
                    default:
                        int k = line.Sections.Count > 1 ? rng.Int(line.Sections.Count - 1) : 0;
                        ops.Add(SectionOp.Rejoin(line.Sections[k], line.Sections[System.Math.Min(k + 1, line.Sections.Count - 1)]));
                        break;
                }
            }
            return new EditTrailSections(revision, t.Id, ops, rng.Chance(30) ? $"Run {rng.Int(1000)}" : null);
        }

        static PointMm RandomPoint(ref TestRng rng) => new PointMm(rng.Range(-Ft(8000), Ft(8000)), rng.Range(-Ft(8000), Ft(8000)));
    }
}
