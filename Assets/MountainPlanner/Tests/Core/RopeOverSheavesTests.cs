using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Posing sheave trains to the rope (task P3-07, findings #2 and #6; RP-02's equal sharing). Synthetic rigs with the
    // models' proportions: 17 in sheaves at 484 mm pitch, pairs on rockers, rockers on yokes, yokes on an equaliser.
    public sealed class RopeOverSheavesTests
    {
        const double Pitch = 0.484, Rho = 0.216 + 0.0445 / 2;   // flange radius + rope radius
        static double Deg(double d) => d * Math.PI / 180;

        /// <summary>
        /// An 8-sheave train on an arc of radius 10.385 m (the reference breakover), rope side up (support) or down
        /// (hold-down), centred at s = 0, the rope at w = 0; pins sit on the side away from the rope.
        /// </summary>
        static SheaveTree Eight(bool support, double range = 0.6)
        {
            double sign = support ? 1 : -1, r = 10.385;
            var nodes = new List<SheaveTree.Node>();
            var sheaves = new List<SheaveTree.Sheave>();
            double PinW(double depth) => -sign * depth;
            nodes.Add(new SheaveTree.Node(-1, 0, PinW(0.55), -range, range));                    // 0: equaliser
            for (int y = 0; y < 2; y++)
            {
                double ys = (y - 0.5) * 4 * Pitch;
                nodes.Add(new SheaveTree.Node(0, ys, PinW(0.42), -range, range));               // 1, 2: yokes
            }
            for (int k = 0; k < 4; k++)
            {
                double rs = (k - 1.5) * 2 * Pitch;
                nodes.Add(new SheaveTree.Node(1 + k / 2, rs, PinW(0.3), -range, range));        // 3..6: rockers
            }
            for (int k = 0; k < 8; k++)
            {
                double s = (k - 3.5) * Pitch;
                double w = -sign * (Rho + (r - Math.Sqrt(r * r - s * s)));                       // axles on the arc, below the rope for support
                sheaves.Add(new SheaveTree.Sheave(3 + k / 2, s, w));
            }
            return new SheaveTree(nodes, sheaves);
        }

        static double SegmentAngle(RopeOverSheaves.Pose p, int k) =>
            Math.Atan2(p.ContactW[k + 1] - p.ContactW[k], p.ContactS[k + 1] - p.ContactS[k]);

        [Test]
        public void RP02_ASupportTrainSharesTheBendEquallyAndTheRopeRidesOnTop()
        {
            var tree = Eight(true);
            var pose = RopeOverSheaves.Solve(tree, Deg(12), Deg(-6), true, Rho);
            Assert.That(pose.Clamped, Is.False);
            Assert.That(pose.SharingError, Is.LessThan(Deg(0.6)), "each sheave takes about 18°/8");
            for (int k = 0; k < 8; k++)
            {
                double d = Math.Sqrt(Math.Pow(pose.ContactS[k] - pose.AxleS[k], 2) + Math.Pow(pose.ContactW[k] - pose.AxleW[k], 2));
                Assert.That(d, Is.EqualTo(Rho).Within(1e-9), "on the flange-top circle");
                Assert.That(pose.ContactW[k], Is.GreaterThan(pose.AxleW[k]), "the rope above the sheave");
            }
            // The whole train turns toward the rope's mean direction (+3°); its two yokes split ±δ/4 about it.
            Assert.That(pose.Absolute[0], Is.EqualTo(Deg(3)).Within(Deg(0.5)));
            Assert.That(pose.Local[1], Is.EqualTo(-pose.Local[2]).Within(Deg(0.1)));
            for (int k = 0; k + 1 < 8; k++)
                Assert.That(SegmentAngle(pose, k), Is.EqualTo(Deg(12) + (k + 1) * Deg(-18) / 8).Within(Deg(0.6)), $"segment {k}");
        }

        [Test]
        public void RP02_AHoldDownTrainHoldsTheRopeFromAbove()
        {
            var tree = Eight(false);
            var pose = RopeOverSheaves.Solve(tree, Deg(-4), Deg(14), false, Rho);
            Assert.That(pose.SharingError, Is.LessThan(Deg(0.6)));
            for (int k = 0; k < 8; k++) Assert.That(pose.ContactW[k], Is.LessThan(pose.AxleW[k]), "the rope beneath the sheave");
        }

        [Test]
        public void RP02_OnACombinationOnlyTheRowTheRopeTouchesTurns()
        {
            // A c8 stand-in: a block with a hold-down row (sheaves 0–3, above) and a support row (4–7, below).
            var nodes = new List<SheaveTree.Node>
            {
                new SheaveTree.Node(-1, 0, 0, -0.6, 0.6),            // 0: block
                new SheaveTree.Node(0, 0, 0.5, -0.6, 0.6),           // 1: hold yoke
                new SheaveTree.Node(0, 0, -0.5, -0.6, 0.6),          // 2: support yoke
                new SheaveTree.Node(1, -Pitch, 0.4, -0.6, 0.6),      // 3, 4: hold rockers
                new SheaveTree.Node(1, Pitch, 0.4, -0.6, 0.6),
                new SheaveTree.Node(2, -Pitch, -0.4, -0.6, 0.6),     // 5, 6: support rockers
                new SheaveTree.Node(2, Pitch, -0.4, -0.6, 0.6),
            };
            var sheaves = new List<SheaveTree.Sheave>();
            // Ordered along the line; hold sheaves above the rope, support below, interleaved by s.
            var raw = new List<(double s, double w, int parent, bool hold)>();
            for (int k = 0; k < 4; k++) raw.Add(((k - 1.5) * Pitch, Rho, 3 + k / 2, true));
            for (int k = 0; k < 4; k++) raw.Add(((k - 1.5) * Pitch + 1e-3, -Rho, 5 + k / 2, false));
            raw = raw.OrderBy(x => x.s).ToList();
            foreach (var x in raw) sheaves.Add(new SheaveTree.Sheave(x.parent, x.s, x.w));
            var tree = new SheaveTree(nodes, sheaves);
            var touched = raw.Select(x => !x.hold).ToArray();   // the rope bends down: it presses on the support row
            var pose = RopeOverSheaves.Solve(tree, Deg(6), Deg(-2), true, Rho, touched);
            Assert.That(pose.Local[1], Is.EqualTo(0), "the unloaded hold-down row rests on its frame");
            Assert.That(pose.Local[3], Is.EqualTo(0));
            Assert.That(pose.Local[5], Is.Not.EqualTo(0), "the loaded support row's rockers turn");
        }

        [Test]
        public void ARotationPastTheSafeRangeIsClamped()
        {
            var tree = Eight(true, range: Deg(2));
            var pose = RopeOverSheaves.Solve(tree, Deg(25), Deg(-10), true, Rho);
            Assert.That(pose.Clamped, Is.True);
            Assert.That(pose.Local.All(l => Math.Abs(l) <= Deg(2) + 1e-12), Is.True);
        }

        /// <summary>A tree moved to (s, w) on the line.</summary>
        static SheaveTree At(SheaveTree t, double s, double w) => new SheaveTree(
            t.Nodes.Select(n => new SheaveTree.Node(n.Parent, n.PivotS + s, n.PivotW + w, n.RangeMin, n.RangeMax)).ToList(),
            t.Sheaves.Select(x => new SheaveTree.Sheave(x.Parent, x.AxleS + s, x.AxleW + w)).ToList());

        [Test]
        public void RP02_AlongALineEachSpanLeavesItsTrainAlongItsShareOfTheBend()
        {
            // The bottom terminal's hold-down row (rope level in), three support towers up a 25% line, the top's support row.
            var trains = new List<SheaveTree> { At(Eight(false), 4, 3.5) };
            var above = new List<bool> { false };
            for (int i = 1; i <= 3; i++)
            {
                trains.Add(At(Eight(true), 80 * i, 0.25 * 80 * i + 10));
                above.Add(true);
            }
            trains.Add(At(Eight(true), 330, 0.25 * 330 + 3.5));
            above.Add(true);
            var quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            var side = RopeOverSheaves.PoseLine(trains, above, null, Rho, quad.CounterweightNewtons, quad.LoadedNewtonsPerMetre, 3.5);
            Assert.That(side.TangentError, Is.LessThan(Deg(0.5)), $"tangent mismatch {side.TangentError * 180 / Math.PI:F3}°");
            Assert.That(side.Spans.Count, Is.EqualTo(4));
            // Spans meet the trains at their contacts: continuous rope.
            for (int i = 0; i < side.Spans.Count; i++)
            {
                var sp = side.Spans[i];
                var a = side.Poses[i];
                Assert.That(sp.Y(sp.From), Is.EqualTo(a.ContactW[7]).Within(1e-6));
            }
            // The bottom row takes the rope from level (into its first sheave) to the first span's slope, in eighths.
            double exit = Math.Atan(side.Spans[0].Slope(side.Spans[0].From));
            Assert.That(SegmentAngle(side.Poses[0], 0), Is.EqualTo(exit / 8).Within(Deg(0.6)));
            Assert.That(side.TangentError, Is.LessThan(Deg(0.01)), $"RP-02: {side.TangentError * 180 / Math.PI:F4}°");
        }

        [Test]
        public void SH04_PosingIsDeterministic()
        {
            var a = RopeOverSheaves.Solve(Eight(true), Deg(7), Deg(-3), true, Rho);
            var b = RopeOverSheaves.Solve(Eight(true), Deg(7), Deg(-3), true, Rho);
            Assert.That(a.ContactS, Is.EqualTo(b.ContactS));
            Assert.That(a.ContactW, Is.EqualTo(b.ContactW));
        }
    }
}
