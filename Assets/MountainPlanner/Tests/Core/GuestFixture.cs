using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The resort the guest tests ski (engine-free, like the rest of Tests/Core). It is small but it has every shape the
    /// rules care about:
    /// <code>
    ///   0 base ──[Easy Rider, quad 101]──► 1 green top ──green──► 0
    ///                                            │
    ///                                            └──blue──► 2 blue pod ──blue──► 0
    ///   0 base ──[Sharp End, quad 202]──► 3 high top ──black──► 0
    ///                                            └──double black──► 0
    ///   2 blue pod ──[Trap, quad 303]──► 4 trap top ──blue only──► 2        (no green way out: beginners must not go up)
    ///   0 base and 5 second base are one Area; a path joins them.
    /// </code>
    /// A beginner may ski only the green; the trap pod's only way down is a blue, so GU-07's reachability must keep a
    /// beginner out of it even though the lift that serves it is open.
    /// </summary>
    static class GuestFixture
    {
        public const int BaseNode = 0, GreenTop = 1, BluePod = 2, HighTop = 3, TrapTop = 4, SecondBase = 5;
        public const ulong EasyRider = 101, SharpEnd = 202, Trap = 303;
        public const ulong GreenTrail = 11, BlueTrail = 12, BlueOut = 13, BlackTrail = 14, DoubleTrail = 15, TrapBlue = 16;

        public const byte Green = 0, Blue = 1, Black = 2, Double = 3;

        /// <summary>A trail segment's facts: the band it is, the sign on it, and how wide and long it is.</summary>
        static TrailEdgeFacts Facts(byte band, int widthFeet = 100, int metres = 800) =>
            TrailEdgeFacts.SteadyPitch(band, band, (int)(widthFeet * 30.48), metres * (int)(widthFeet * 0.3048), SkiMs(band, metres));

        /// <summary>A reference skier of the band over <paramref name="metres"/>, real ms at 1× (GU-10's base speeds).</summary>
        static int SkiMs(byte band, int metres)
        {
            int tenths = band == Green ? 130 : band == Blue ? 200 : band == Black ? 250 : 270;
            return (int)(metres * 10_000L / (tenths * 447L / 100));
        }

        public static SimNetwork Network()
        {
            // (from, to, trail index): the trail ids are sorted, so the index follows that order.
            var trails = new ulong[] { GreenTrail, BlueTrail, BlueOut, BlackTrail, DoubleTrail, TrapBlue };
            var edges = new List<(int from, int to, int trail)>
            {
                (GreenTop, BaseNode, 0),     // Green: the way home for everyone
                (GreenTop, BluePod, 1),      // Blue off the same top
                (BluePod, BaseNode, 2),      // Blue out of the pod
                (HighTop, BaseNode, 3),      // Black
                (HighTop, BaseNode, 4),      // Double black
                (TrapTop, BluePod, 5),       // The trap's only way down: a blue
                (BaseNode, SecondBase, -1),  // A path across the Area
                (SecondBase, BaseNode, -1),
            };
            var facts = new List<TrailEdgeFacts>
            {
                Facts(Green), Facts(Blue), Facts(Blue), Facts(Black, 60), Facts(Double, 40), Facts(Blue),
                default, default,
            };
            var lengths = new List<int> { 800_000, 800_000, 800_000, 800_000, 800_000, 800_000, 60_000, 60_000 };
            return SimNetwork.Of(6, edges, trails, facts, lengths);
        }

        public static LiftInput Quad(ulong id, int bottom, int top, bool built = true) =>
            new LiftInput(id, built, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 360_000, top, bottom, 0, bottom * 10_000);

        /// <summary>A double: two seats, so a party of three or four has to split across carriers (LO-02).</summary>
        public static LiftInput TwoSeater(ulong id, int bottom, int top) =>
            new LiftInput(id, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 2, 6000, 360_000, top, bottom, 0, bottom * 10_000);

        /// <summary>
        /// One <b>double</b> and one green home: the mountain that splits parties. A quad never splits a party of four,
        /// so a resort of quads can't show what happens when half of one rides ahead of the other half.
        /// </summary>
        public static SimDesign OneDouble(long revision = 1)
        {
            var trails = new ulong[] { GreenTrail };
            var edges = new List<(int from, int to, int trail)> { (GreenTop, BaseNode, 0) };
            var facts = new List<TrailEdgeFacts> { Facts(Green, 120, 1000) };
            var lengths = new List<int> { 1_000_000 };
            var net = SimNetwork.Of(2, edges, trails, facts, lengths);
            return new SimDesign(revision, (ulong)revision, 4242, DayHours.Default, new[] { TwoSeater(EasyRider, BaseNode, GreenTop) }, 1, net);
        }

        public static SimDesign Resort(long revision = 1, SimNetwork net = null)
        {
            net = net ?? Network();
            var lifts = new[]
            {
                Quad(EasyRider, BaseNode, GreenTop),
                Quad(SharpEnd, BaseNode, HighTop),
                Quad(Trap, BluePod, TrapTop),
            };
            return new SimDesign(revision, (ulong)revision, 4242, DayHours.Default, lifts, 6, net);
        }

        /// <summary>The resort with one trail's sign changed, to test GU-17's misrate penalty and the gate.</summary>
        public static SimNetwork NetworkPostedEasier()
        {
            var trails = new ulong[] { GreenTrail, BlueTrail, BlueOut, BlackTrail, DoubleTrail, TrapBlue };
            var edges = new List<(int from, int to, int trail)>
            {
                (GreenTop, BaseNode, 0),
                (GreenTop, BluePod, 1),
                (BluePod, BaseNode, 2),
                (HighTop, BaseNode, 3),
                (HighTop, BaseNode, 4),
                (TrapTop, BluePod, 5),
                (BaseNode, SecondBase, -1),
                (SecondBase, BaseNode, -1),
            };
            var facts = new List<TrailEdgeFacts>
            {
                Facts(Green),
                // A blue posted green: the gate reads the harder of the two (GU-17), so a beginner still can't take it,
                // and everyone who skis it loses satisfaction for the sign.
                TrailEdgeFacts.SteadyPitch(Blue, Green, 3048, 24_000, SkiMs(Blue, 800)),
                Facts(Blue), Facts(Black, 60), Facts(Double, 40), Facts(Blue),
                default, default,
            };
            var lengths = new List<int> { 800_000, 800_000, 800_000, 800_000, 800_000, 800_000, 60_000, 60_000 };
            return SimNetwork.Of(6, edges, trails, facts, lengths);
        }

        /// <summary>
        /// One quad and one green home, for the rules about lines: a crowd far past what one lift can carry, which is the
        /// mountain a player builds first and the strain the whole loop is about.
        /// </summary>
        public static SimDesign OneLift(long revision = 1)
        {
            var trails = new ulong[] { GreenTrail };
            var edges = new List<(int from, int to, int trail)> { (GreenTop, BaseNode, 0) };
            var facts = new List<TrailEdgeFacts> { Facts(Green, 120, 1000) };
            var lengths = new List<int> { 1_000_000 };
            var net = SimNetwork.Of(2, edges, trails, facts, lengths);
            var lifts = new[] { Quad(EasyRider, BaseNode, GreenTop) };
            return new SimDesign(revision, (ulong)revision, 4242, DayHours.Default, lifts, 1, net);
        }

        /// <summary>
        /// A generated resort for the scale run: <paramref name="pods"/> pods, each a lift from the base to a top with
        /// two runs home, so the network has about 3 nodes and 3 edges per pod.
        /// </summary>
        public static SimDesign Big(int pods, out SimNetwork net)
        {
            int nodes = 1 + pods;
            var trails = new ulong[pods * 2];
            for (int p = 0; p < pods; p++) { trails[p * 2] = (ulong)(1000 + p * 2); trails[p * 2 + 1] = (ulong)(1001 + p * 2); }
            Array.Sort(trails);
            var edges = new List<(int from, int to, int trail)>();
            var facts = new List<TrailEdgeFacts>();
            var lengths = new List<int>();
            for (int p = 0; p < pods; p++)
            {
                int top = 1 + p;
                byte band = (byte)(p % 4);
                int a = Array.BinarySearch(trails, (ulong)(1000 + p * 2)), b = Array.BinarySearch(trails, (ulong)(1001 + p * 2));
                edges.Add((top, 0, a)); facts.Add(Facts(0, 120, 1200)); lengths.Add(1_200_000);   // always a green home
                edges.Add((top, 0, b)); facts.Add(Facts(band, 80, 1000)); lengths.Add(1_000_000);
            }
            var lifts = new LiftInput[pods];
            for (int p = 0; p < pods; p++) lifts[p] = Quad((ulong)(100 + p), 0, 1 + p);
            net = SimNetwork.Of(nodes, edges, trails, facts, lengths);
            return new SimDesign(1, 1, 4242, DayHours.Default, lifts, trails.Length, net);
        }
    }
}
