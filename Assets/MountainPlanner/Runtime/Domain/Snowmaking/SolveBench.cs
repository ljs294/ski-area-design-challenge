using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>
    /// BG-03's network (task 4.15): 500 guns on about 200 pipes, for the Core test and the player's <c>-solvebench</c>. A
    /// looped 11 × 10 grid of 12 in mains 1,000 ft apart on a 900 ft slope (steep enough that the top guns fall short and
    /// SG-07's re-solves run), fed by four 5,000 hp pump houses along the bottom, each on its own pond at the bottom's
    /// height, with 500 hydrants tapped evenly along the mains and a gun set On on each. Run at 10 °F wet bulb (48 gpm,
    /// 16 cfm a gun). Fixed: the same network every time.
    /// </summary>
    public static class SolveBench
    {
        public const int Columns = 11, Rows = 10, Guns = 500, WetBulbTenthsF = 100;
        public const double SpacingFt = 1000, RiseFtPerRow = 100;
        /// <summary>BG-03: a cold solve, re-solves included, within this (ms).</summary>
        public const double BudgetMs = 20;
        const double MmPerFt = 304.8;

        public sealed class Bench
        {
            public SnowNetwork Network;
            public HydraulicGraph Graph;
            public readonly Dictionary<ulong, int> LevelsMm = new Dictionary<ulong, int>();
            public SourceLevel Level(ulong s) => LevelsMm.TryGetValue(s, out int mm) ? new SourceLevel(mm, true, false, false) : new SourceLevel(0, false, true, false);

            /// <summary>Every gun forced on at the bench's wet bulb.</summary>
            public GunAsk[] Asks()
            {
                var a = new GunAsk[Graph.Guns.Length];
                for (int i = 0; i < a.Length; i++) a[i] = new GunAsk { Wanted = true, Forced = true, WetBulbTenthsF = WetBulbTenthsF, Priority = 3 };
                return a;
            }
        }

        sealed class Slope : IGroundHeights
        {
            public bool TryHeightMm(PointMm p, out int heightMm)
            {
                heightMm = (int)Math.Round(Math.Max(0, p.Y) / SpacingFt * RiseFtPerRow);
                return true;
            }
        }

        static PointMm Ft(double x, double y) => new PointMm((int)Math.Round(x * MmPerFt), (int)Math.Round(y * MmPerFt));

        public static Bench Build()
        {
            ulong next = 0x1000;
            SnowId Id() => new SnowId(next++);
            var nodes = new List<SnowNode>();
            var pipes = new List<SnowPipe>();
            var houses = new List<PumpHouse>();
            var guns = new List<SnowGun>();
            var b = new Bench();

            var grid = new SnowNode[Columns, Rows];
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Columns; c++)
                {
                    grid[c, r] = new SnowNode(Id(), SnowNodeKind.Junction, Ft(c * SpacingFt, r * SpacingFt), nodes.Count + 1, 0, default);
                    nodes.Add(grid[c, r]);
                }
            SnowPipe Pipe(SnowNode a, SnowNode z, int d)
            {
                var p = new SnowPipe(Id(), "", d, new[] { a.At, z.At }, a.Id, z.Id);
                pipes.Add(p);
                return p;
            }
            var mains = new List<SnowPipe>();
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c + 1 < Columns; c++) mains.Add(Pipe(grid[c, r], grid[c + 1, r], 12));
            for (int c = 0; c < Columns; c++)
                for (int r = 0; r + 1 < Rows; r++) mains.Add(Pipe(grid[c, r], grid[c, r + 1], 12));
            ulong pond = 0x40;
            foreach (int c in new[] { 0, 3, 7, 10 })
            {
                b.LevelsMm[pond] = 0;
                var at = Ft(c * SpacingFt, -200);
                var h = new PumpHouse(Id(), "House", at, 0, SnowRules.HouseLengthMm, SnowRules.HouseWidthMm, pond, SnowRules.DefaultCompressorCfm, HouseFoundation.Slab);
                houses.Add(h);
                var n = new SnowNode(Id(), SnowNodeKind.House, at, houses.Count, pond, new PumpSpec(SnowRules.MaxHp, 850, 600, true), house: h.Id);
                nodes.Add(n);
                Pipe(n, grid[c, 0], 20);
                pond++;
            }
            // 199 mains: 102 × 3 + 97 × 2 = 500 hydrants, evenly along each.
            int count = mains.Count, threes = Guns - 2 * count;
            for (int i = 0; i < count; i++)
            {
                var p = mains[i];
                int k = i < threes ? 3 : 2;
                var a = p.Route[0];
                var z = p.Route[p.Route.Count - 1];
                for (int j = 1; j <= k; j++)
                {
                    double t = j / (double)(k + 1);
                    var at = new PointMm((int)Math.Round(a.X + (z.X - a.X) * t), (int)Math.Round(a.Y + (z.Y - a.Y) * t));
                    var h = new SnowNode(Id(), SnowNodeKind.Hydrant, at, nodes.Count + 1, 0, default, p.Id, true);
                    nodes.Add(h);
                    guns.Add(new SnowGun(Id(), GunModel.Stick20, at, h.Id, 0, GunOverride.On));
                }
            }
            b.Network = SnowNetwork.From(nodes, pipes, houses, guns);
            b.Graph = HydraulicGraph.Compile(b.Network, new Slope(), SnowRules.BuryMm, SnowRules.SampleMm, s => b.LevelsMm.TryGetValue(s, out int mm) ? mm : (int?)null);
            return b;
        }
    }
}
