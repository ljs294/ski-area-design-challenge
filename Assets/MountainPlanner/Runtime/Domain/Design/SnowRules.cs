using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Task 4.15's rules for the snowmaking network as the design holds it (HY-01, HY-02, SG-01…04). Hydraulic failures are
    /// not rules: a network that breaks no rule here may still fail to deliver water, and the diagnostics (HY-09) say why.
    /// </summary>
    public static class SnowRules
    {
        /// <summary>HY-01: the pipe sizes, inches.</summary>
        public static readonly int[] Diameters = { 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24 };
        public const int DefaultDiameterIn = 8;

        public const int MaxNodes = 20_000;
        public const int MaxPipes = 5_000;
        public const int MaxRoutePoints = 4_096;
        public const int MaxHouses = 64;
        public const int MaxGuns = 5_000;

        /// <summary>HY-01: a gun's hose, 50 ft.</summary>
        public const int HoseMm = 15_240;
        /// <summary>A tap sits on its host's route to within this.</summary>
        public const int OnRouteMm = 5;
        /// <summary>Taps on one pipe stay at least 10 ft apart, and that far from its ends.</summary>
        public const int TapGapMm = 3_048;
        /// <summary>The hydrant run's default spacing, 100 ft (SM-07's 20 guns at 100 ft).</summary>
        public const int DefaultSpacingMm = 30_480;
        /// <summary>p4-02 HY-03: snowmaking pipe is buried 6 ft; the graph samples the ground every 10 m for high points.</summary>
        public const int BuryMm = 1_829, SampleMm = 10_000;
        /// <summary>A hydrant run places at most this many hydrants (MapLibre's 500, kept).</summary>
        public const int MaxRun = 500;

        /// <summary>The pump house's default footprint, 60 × 40 ft (MapLibre's, kept).</summary>
        public const int HouseLengthMm = 18_288;
        public const int HouseWidthMm = 12_192;
        /// <summary>Its eave, 16 ft, and 4:12 roof: the ridge is 22 ft 8 in (presentation).</summary>
        public const int HouseEaveMm = 4_877;
        /// <summary>SG-06 v2 (owner-approved plan 2026-11-18, Q3): the default compressor, 5,000 cfm.</summary>
        public const int DefaultCompressorCfm = 5_000;
        public const int MinCompressorCfm = 500;
        public const int MaxCompressorCfm = 20_000;
        /// <summary>A pump house stands within 300 ft of its source's water.</summary>
        public const int HouseReachMm = 91_440;

        public const int MaxHp = 5_000;
        public const int MaxBoostPsi = 1_500;

        public static bool ValidDiameter(int d) => Array.IndexOf(Diameters, d) >= 0;

        public static string PumpProblem(PumpSpec p)
        {
            if (p.Hp <= 0 || p.Hp > MaxHp) return $"A pump needs between 1 and {MaxHp:N0} hp.";
            if (p.EfficiencyPermille <= 0 || p.EfficiencyPermille > 1000) return "A pump's efficiency is between 0.1 % and 100 %.";
            if (p.BoostPsi <= 0 || p.BoostPsi > MaxBoostPsi) return $"A pump adds between 1 and {MaxBoostPsi:N0} psi.";
            return null;
        }

        /// <summary>Every rule the network must hold (a loaded design is checked by these, a command before it applies).</summary>
        public static string NetworkProblem(SnowNetwork net)
        {
            if (net.Nodes.Count > MaxNodes || net.Pipes.Count > MaxPipes || net.Houses.Count > MaxHouses || net.Guns.Count > MaxGuns)
                return "The snowmaking network is larger than the design holds.";
            foreach (var p in net.Pipes)
            {
                if (!ValidDiameter(p.DiameterIn)) return $"A pipe of {p.DiameterIn} in isn't a pipe size.";
                if (p.Route.Count < 2) return "A pipe needs at least two points.";
                if (p.Route.Count > MaxRoutePoints) return "A pipe has too many points.";
                if (p.From == p.To) return "A pipe can't end where it starts.";
                var a = net.FindNode(p.From);
                var b = net.FindNode(p.To);
                if (a == null || b == null) return "A pipe ends on a node that isn't there.";
                if (a.At != p.Route[0] || b.At != p.Route[p.Route.Count - 1]) return "A pipe's route must end on its nodes.";
                foreach (var q in p.Route) if (!q.InRange) return "A pipe point is out of range.";
                if (p.PlanLengthMm() <= 0) return "A pipe has no length.";
            }
            foreach (var n in net.Nodes)
            {
                if (!n.At.InRange) return "A snowmaking node is out of range.";
                if (n.IsPump && PumpProblem(n.Pump) is string pp) return pp;
                if (n.IsTap)
                {
                    if (n.Kind == SnowNodeKind.Intake || n.Kind == SnowNodeKind.Outlet || n.Kind == SnowNodeKind.House)
                        return "Intakes, outlets and pump houses sit at a pipe's end, not on its route.";
                    var host = net.FindPipe(n.Host);
                    if (host == null) return "A hydrant or pump sits on a pipe that isn't there.";
                    if (SnowGeometry.Project(host.Route, n.At).DistanceMm > OnRouteMm) return "A hydrant or pump must sit on its pipe.";
                }
                else if (n.Kind == SnowNodeKind.Pump) return "An inline pump sits on a pipe's route.";
                if (n.Kind == SnowNodeKind.House && net.FindHouse(n.House) == null) return "A pump house's node has no pump house.";
            }
            foreach (var h in net.Houses)
            {
                if (h.LengthMm <= 0 || h.WidthMm <= 0) return "A pump house needs a footprint.";
                if (h.CompressorCfm < MinCompressorCfm || h.CompressorCfm > MaxCompressorCfm)
                    return $"A compressor is between {MinCompressorCfm:N0} and {MaxCompressorCfm:N0} cfm.";
                if (h.BearingCentideg < 0 || h.BearingCentideg >= 36000) return "A pump house's bearing is out of range.";
                if (NodeOfHouse(net, h.Id) == null) return "A pump house has no discharge node.";
            }
            var taken = new HashSet<SnowId>();
            foreach (var g in net.Guns)
            {
                if (!g.At.InRange) return "A gun is out of range.";
                if (g.Hydrant.IsNone) continue;
                var h = net.FindNode(g.Hydrant);
                if (h == null || h.Kind != SnowNodeKind.Hydrant) return "A gun hangs on a hydrant that isn't there.";
                if (!taken.Add(g.Hydrant)) return "A hydrant takes one gun.";
                if (!h.At.Within(g.At, HoseMm)) return "A gun's hose reaches 50 ft.";
            }
            return null;
        }

        public static SnowNode NodeOfHouse(SnowNetwork net, SnowId house)
        {
            foreach (var n in net.Nodes) if (n.Kind == SnowNodeKind.House && n.House == house) return n;
            return null;
        }
    }

    /// <summary>Plan geometry of pipe routes, in whole millimetres where it is stored.</summary>
    public static class SnowGeometry
    {
        public readonly struct OnRoute
        {
            /// <summary>The distance along the route to the projected point, millimetres.</summary>
            public readonly double ChainageMm;
            public readonly PointMm Point;
            public readonly double DistanceMm;
            /// <summary>The route segment the point lies on (from point <c>Segment</c> to <c>Segment + 1</c>).</summary>
            public readonly int Segment;
            public OnRoute(double chainage, PointMm point, double distance, int segment)
            {
                ChainageMm = chainage;
                Point = point;
                DistanceMm = distance;
                Segment = segment;
            }
        }

        /// <summary>The nearest point of a route to <paramref name="p"/>; ties go to the earlier segment.</summary>
        public static OnRoute Project(IReadOnlyList<PointMm> route, PointMm p)
        {
            double best = double.MaxValue, bestChain = 0, run = 0;
            PointMm bestPoint = route.Count > 0 ? route[0] : p;
            int bestSeg = 0;
            for (int i = 0; i + 1 < route.Count; i++)
            {
                double ax = route[i].X, ay = route[i].Y, bx = route[i + 1].X, by = route[i + 1].Y;
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy, len = Math.Sqrt(len2);
                double t = len2 <= 0 ? 0 : ((p.X - ax) * dx + (p.Y - ay) * dy) / len2;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double qx = ax + t * dx, qy = ay + t * dy;
                double d = Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
                if (d < best)
                {
                    best = d;
                    bestChain = run + t * len;
                    bestPoint = new PointMm((int)Math.Round(qx), (int)Math.Round(qy));
                    bestSeg = i;
                }
                run += len;
            }
            return new OnRoute(bestChain, bestPoint, best, bestSeg);
        }

        /// <summary>The point at a distance along a route (clamped to its ends).</summary>
        public static PointMm At(IReadOnlyList<PointMm> route, double chainageMm)
        {
            double run = 0;
            for (int i = 0; i + 1 < route.Count; i++)
            {
                double dx = route[i + 1].X - route[i].X, dy = route[i + 1].Y - route[i].Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (run + len >= chainageMm && len > 0)
                {
                    double t = (chainageMm - run) / len;
                    if (t < 0) t = 0;
                    return new PointMm((int)Math.Round(route[i].X + t * dx), (int)Math.Round(route[i].Y + t * dy));
                }
                run += len;
            }
            return route[route.Count - 1];
        }

        /// <summary>A route's plan length, millimetres.</summary>
        public static double Length(IReadOnlyList<PointMm> route)
        {
            double s = 0;
            for (int i = 1; i < route.Count; i++) s += Math.Sqrt(route[i].DistanceSquared(route[i - 1]));
            return s;
        }

        /// <summary>
        /// The hydrant run (MapLibre's, re-specified): chainages from <paramref name="fromMm"/> to <paramref name="toMm"/>
        /// spaced at most <paramref name="spacingMm"/> apart, both ends included, evenly.
        /// </summary>
        public static List<double> RunBySpacing(double fromMm, double toMm, int spacingMm)
        {
            var list = new List<double>();
            double span = Math.Abs(toMm - fromMm);
            if (spacingMm <= 0) return list;
            int gaps = Math.Max(1, (int)Math.Ceiling(span / spacingMm - 1e-9));
            if (gaps + 1 > SnowRules.MaxRun) return list;
            for (int i = 0; i <= gaps; i++) list.Add(fromMm + (toMm - fromMm) * i / gaps);
            return list;
        }

        /// <summary>The hydrant run by count: <paramref name="count"/> evenly from one end to the other, both included.</summary>
        public static List<double> RunByCount(double fromMm, double toMm, int count)
        {
            var list = new List<double>();
            if (count < 2 || count > SnowRules.MaxRun) return list;
            for (int i = 0; i < count; i++) list.Add(fromMm + (toMm - fromMm) * i / (count - 1));
            return list;
        }
    }
}
