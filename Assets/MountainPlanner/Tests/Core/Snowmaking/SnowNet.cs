using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;

namespace MountainPlanner.Tests.Snowmaking
{
    /// <summary>
    /// A small kit for task 4.15's tests: networks laid out in feet on a ground the test describes, compiled with no bury
    /// so the hand calculations in the tests read straight off the drawings.
    /// </summary>
    public sealed class SnowNet
    {
        public readonly List<SnowNode> Nodes = new List<SnowNode>();
        public readonly List<SnowPipe> Pipes = new List<SnowPipe>();
        public readonly List<PumpHouse> Houses = new List<PumpHouse>();
        public readonly List<SnowGun> Guns = new List<SnowGun>();
        public readonly Dictionary<ulong, int> LevelsMm = new Dictionary<ulong, int>();
        public readonly Dictionary<ulong, bool> Floors = new Dictionary<ulong, bool>();
        public Func<double, double, double> GroundFt = (x, y) => 0;
        ulong _next = 0x1000;

        public static PointMm Ft(double x, double y) => new PointMm((int)Math.Round(x * 304.8), (int)Math.Round(y * 304.8));

        public SnowId Id() => new SnowId(_next++);

        public SnowNode Node(SnowNodeKind kind, double x, double y, ulong source = 0, PumpSpec pump = default)
        {
            var n = new SnowNode(Id(), kind, Ft(x, y), Nodes.Count + 1, source, pump);
            Nodes.Add(n);
            return n;
        }

        /// <summary>A source's water surface, ft.</summary>
        public SnowNet Water(ulong source, double levelFt)
        {
            LevelsMm[source] = (int)Math.Round(levelFt * 304.8);
            return this;
        }

        public SnowPipe Pipe(SnowNode a, SnowNode b, int diameterIn, params (double x, double y)[] via)
        {
            var route = new List<PointMm> { a.At };
            foreach (var v in via) route.Add(Ft(v.x, v.y));
            route.Add(b.At);
            var p = new SnowPipe(Id(), "", diameterIn, route, a.Id, b.Id);
            Pipes.Add(p);
            return p;
        }

        public SnowNode Tap(SnowNodeKind kind, SnowPipe host, double x, double y, bool forward = true, PumpSpec pump = default)
        {
            var at = SnowGeometry.Project(host.Route, Ft(x, y)).Point;
            var n = new SnowNode(Id(), kind, at, Nodes.Count + 1, 0, pump, host.Id, forward);
            Nodes.Add(n);
            return n;
        }

        public SnowNode House(double x, double y, ulong source, PumpSpec pump, int cfm = SnowRules.DefaultCompressorCfm)
        {
            var h = new PumpHouse(Id(), "House", Ft(x, y), 0, SnowRules.HouseLengthMm, SnowRules.HouseWidthMm, source, cfm, HouseFoundation.Slab);
            Houses.Add(h);
            var n = new SnowNode(Id(), SnowNodeKind.House, Ft(x, y), Houses.Count, source, pump, house: h.Id);
            Nodes.Add(n);
            return n;
        }

        public SnowGun Gun(SnowNode hydrant, GunOverride over = GunOverride.On)
        {
            var g = new SnowGun(Id(), GunModel.Stick20, hydrant.At, hydrant.Id, 0, over);
            Guns.Add(g);
            return g;
        }

        public SnowNetwork Network() => SnowNetwork.From(Nodes, Pipes, Houses, Guns);

        public HydraulicGraph Compile(int buryMm = 0) =>
            HydraulicGraph.Compile(Network(), new Ground(GroundFt), buryMm, 25_000, s => LevelsMm.TryGetValue(s, out int mm) ? mm : (int?)null);

        public SourceLevel Level(ulong s) => LevelsMm.TryGetValue(s, out int mm)
            ? new SourceLevel(mm, !Floors.ContainsKey(s), false, false)
            : new SourceLevel(0, false, false, false);

        /// <summary>A solver with every water node at its source's level.</summary>
        public HydraulicSolver Solver(HydraulicGraph g)
        {
            var s = new HydraulicSolver(g);
            for (int i = 0; i < g.NodeCount; i++)
                if (g.Kind[i] == HNodeKind.Water) s.SetWater(i, LevelsMm[g.Source[i]] / 304.8);
            return s;
        }

        /// <summary>Every gun asked to run at a wet bulb (tenths °F), forced when its override is On.</summary>
        public static GunAsk[] Asks(HydraulicGraph g, int wetBulbTenthsF)
        {
            var a = new GunAsk[g.Guns.Length];
            for (int i = 0; i < a.Length; i++)
                a[i] = new GunAsk { Wanted = g.Guns[i].Override != GunOverride.Off, Forced = g.Guns[i].Override == GunOverride.On, WetBulbTenthsF = wetBulbTenthsF, Priority = 3 };
            return a;
        }

        internal sealed class Ground : IGroundHeights
        {
            readonly Func<double, double, double> _ft;
            public Ground(Func<double, double, double> ft) { _ft = ft; }
            public bool TryHeightMm(PointMm p, out int heightMm)
            {
                heightMm = (int)Math.Round(_ft(p.X / 304.8, p.Y / 304.8) * 304.8);
                return true;
            }
        }
    }
}
