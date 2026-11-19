using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>The ground the pipes are buried in: graded heights, whole millimetres in the package datum.</summary>
    public interface IGroundHeights
    {
        bool TryHeightMm(PointMm p, out int heightMm);
    }

    public enum HNodeKind : byte
    {
        Junction, Hydrant, HighPoint, PumpSuction, PumpDischarge, HouseDischarge, Outlet, Intake,
        /// <summary>A fixed head: a source's water surface, behind an intake, a house's suction or an outlet.</summary>
        Water,
    }

    public enum HLinkKind : byte
    {
        Pipe,
        /// <summary>An inline pump or a pump house's pump (HY-05 v2), with its check valve.</summary>
        Pump,
        /// <summary>Between a source's water and its intake or house suction: a foot valve (water leaves the source only).</summary>
        Intake,
        /// <summary>Between an outlet and its pond's water: water enters the pond only (WS-11).</summary>
        Outlet,
    }

    /// <summary>
    /// The snowmaking network compiled for the solve (HY-01…03): hydraulic nodes and links, each pipe split at its taps and
    /// at its highest point (a pressure check, HY-03), elevations from the graded ground less the bury, and the fixed
    /// factorisation plan. Rebuilt only when the network or the ground under it changes; every solve reuses it.
    /// </summary>
    public sealed class HydraulicGraph
    {
        public const double FtPerMm = 1.0 / 304.8;
        /// <summary>HY-04 v1: Hazen–Williams, US units (ft, gpm, in), C = 120 for every pipe (plan v2, M1: 10.44, not 10.67).</summary>
        public const double HazenK = 10.44;
        public const double HazenC = 120;
        public const double FtPerPsi = 2.31;

        // Nodes.
        public int NodeCount => Kind.Length;
        public readonly HNodeKind[] Kind;
        /// <summary>The pipe's elevation at the node, ft (buried, HY-03); a water node's is its surface.</summary>
        public readonly double[] ElevFt;
        /// <summary>The ground over the node, ft: a hydrant's pressure is read at grade (plan v2, M10).</summary>
        public readonly double[] GradeFt;
        public readonly PointMm[] At;
        /// <summary>The design node a hydraulic node stands for (none for a high point or a water node).</summary>
        public readonly SnowId[] Design;
        /// <summary>A water node's source.</summary>
        public readonly ulong[] Source;
        /// <summary>The node's connected component (over every link, valves ignored), and whether it holds water.</summary>
        public readonly int[] Component;
        public readonly bool[] ComponentHasWater;
        /// <summary>Plan distance from the nearest water along the pipes, ft (SG-07's "furthest from the source").</summary>
        public readonly double[] FromWaterFt;
        /// <summary>Whether water can reach each node over open links, pumps only forward (HY-09 4, 19): a node it can't
        /// gets no pressure on the screen, though the solve gives a stretch behind a check valve a number (no flow).</summary>
        public bool[] Reached { get; private set; }

        // Links.
        public int LinkCount => LinkKind.Length;
        public readonly HLinkKind[] LinkKind;
        public readonly int[] A;
        public readonly int[] B;
        /// <summary>A pipe link's Hazen–Williams resistance: h_f = R·Q^1.852 (ft, gpm).</summary>
        public readonly double[] R;
        public readonly double[] LengthFt;
        public readonly int[] DiameterIn;
        /// <summary>The design pipe a link is part of, or the pump's design node.</summary>
        public readonly SnowId[] LinkDesign;
        public readonly bool[] ValveClosed;
        public readonly PumpSpec[] Pump;

        /// <summary>Every gun with its hydrant's hydraulic node (-1: not on a working hydrant).</summary>
        public readonly SnowGun[] Guns;
        public readonly int[] GunNode;
        /// <summary>The pump houses' compressors by component (SG-06), cfm.</summary>
        public readonly int[] ComponentCfm;
        public readonly List<SnowDiagnostic> Diagnostics;

        // The factorisation over the non-water nodes.
        internal readonly int[] Unknown;      // node → unknown index, or -1 for water nodes
        internal readonly int UnknownCount;
        internal readonly SparseCholesky Cholesky;
        internal readonly int[] SlotAA, SlotBB, SlotAB;
        readonly Dictionary<SnowId, int> _byDesign;

        public int NodeOf(SnowId design) => _byDesign.TryGetValue(design, out int i) ? i : -1;

        HydraulicGraph(Builder b)
        {
            Kind = b.Kind.ToArray();
            ElevFt = b.Elev.ToArray();
            GradeFt = b.Grade.ToArray();
            At = b.At.ToArray();
            Design = b.Design.ToArray();
            Source = b.Source.ToArray();
            LinkKind = b.LKind.ToArray();
            A = b.LA.ToArray();
            B = b.LB.ToArray();
            R = b.LR.ToArray();
            LengthFt = b.LLen.ToArray();
            DiameterIn = b.LDia.ToArray();
            LinkDesign = b.LDesign.ToArray();
            ValveClosed = b.LClosed.ToArray();
            Pump = b.LPump.ToArray();
            Guns = b.Guns.ToArray();
            GunNode = b.GunNode.ToArray();
            Diagnostics = b.Diags;
            _byDesign = b.ByDesign;

            int n = Kind.Length;
            Component = new int[n];
            var uf = new int[n];
            for (int i = 0; i < n; i++) uf[i] = i;
            for (int k = 0; k < A.Length; k++) Union(uf, A[k], B[k]);
            var compOf = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(uf, i);
                if (!compOf.TryGetValue(r, out int c)) compOf[r] = c = compOf.Count;
                Component[i] = c;
            }
            ComponentHasWater = new bool[compOf.Count];
            for (int i = 0; i < n; i++) if (Kind[i] == HNodeKind.Water) ComponentHasWater[Component[i]] = true;
            ComponentCfm = new int[compOf.Count];
            foreach (var (node, cfm) in b.HouseCfm) ComponentCfm[Component[node]] += cfm;

            // Unknowns: every node that isn't water, in a component that holds water (a dry component has no solution).
            Unknown = new int[n];
            int u = 0;
            for (int i = 0; i < n; i++) Unknown[i] = Kind[i] != HNodeKind.Water && ComponentHasWater[Component[i]] ? u++ : -1;
            UnknownCount = u;
            var edges = new List<(int, int)>();
            for (int k = 0; k < A.Length; k++)
                if (Unknown[A[k]] >= 0 && Unknown[B[k]] >= 0) edges.Add((Unknown[A[k]], Unknown[B[k]]));
            Cholesky = new SparseCholesky(u, edges);
            SlotAA = new int[A.Length];
            SlotBB = new int[A.Length];
            SlotAB = new int[A.Length];
            for (int k = 0; k < A.Length; k++)
            {
                int a = Unknown[A[k]], c = Unknown[B[k]];
                SlotAA[k] = a >= 0 ? Cholesky.Slot(a, a) : -1;
                SlotBB[k] = c >= 0 ? Cholesky.Slot(c, c) : -1;
                SlotAB[k] = a >= 0 && c >= 0 && a != c ? Cholesky.Slot(a, c) : -1;
            }
            FromWaterFt = Distances();
        }

        static int Find(int[] uf, int i) { while (uf[i] != i) { uf[i] = uf[uf[i]]; i = uf[i]; } return i; }
        static void Union(int[] uf, int a, int b) { a = Find(uf, a); b = Find(uf, b); if (a != b) uf[Math.Max(a, b)] = Math.Min(a, b); }

        double[] Distances()
        {
            int n = Kind.Length;
            var d = new double[n];
            for (int i = 0; i < n; i++) d[i] = double.PositiveInfinity;
            var adj = Adjacency();
            var heap = new SortedSet<(double, int)>();
            for (int i = 0; i < n; i++) if (Kind[i] == HNodeKind.Water) { d[i] = 0; heap.Add((0, i)); }
            while (heap.Count > 0)
            {
                var (du, x) = heap.Min;
                heap.Remove(heap.Min);
                foreach (int k in adj[x])
                {
                    int y = A[k] == x ? B[k] : A[k];
                    double nd = du + LengthFt[k];
                    if (nd < d[y]) { if (!double.IsPositiveInfinity(d[y])) heap.Remove((d[y], y)); d[y] = nd; heap.Add((nd, y)); }
                }
            }
            return d;
        }

        /// <summary>Each node's links, in link order.</summary>
        public List<int>[] Adjacency()
        {
            var adj = new List<int>[Kind.Length];
            for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>();
            for (int k = 0; k < A.Length; k++) { adj[A[k]].Add(k); adj[B[k]].Add(k); }
            return adj;
        }

        /// <summary>HY-04: a pipe's resistance R with h_f = R·Q^1.852 (ft, gpm, in).</summary>
        public static double Resistance(double lengthFt, int diameterIn) =>
            HazenK * lengthFt / (Math.Pow(HazenC, 1.852) * Math.Pow(diameterIn, 4.87));

        /// <summary>HY-04: friction head over a pipe, ft (the worked example: 1,000 gpm, 1,000 ft, 8 in → 21.2 ft).</summary>
        public static double FrictionFt(double lengthFt, double gpm, int diameterIn) =>
            Resistance(lengthFt, diameterIn) * Math.Pow(Math.Abs(gpm), 1.852);

        /// <summary>HY-07: velocity, ft/s (Q ÷ (2.448·D²)).</summary>
        public static double VelocityFtS(double gpm, int diameterIn) => Math.Abs(gpm) / (2.448 * diameterIn * (double)diameterIn);

        /// <summary>
        /// Compiles the network over the ground. <paramref name="sourceLevelMm"/> gives each source's water surface for the
        /// water nodes' elevations (the solve sets their heads each time).
        /// </summary>
        public static HydraulicGraph Compile(SnowNetwork net, IGroundHeights ground, int buryMm, int sampleMm, Func<ulong, int?> sourceLevelMm)
        {
            var b = new Builder(ground, buryMm, sourceLevelMm);
            // Pipe ends and stand-alone nodes first, in ID order.
            foreach (var n in net.Nodes)
            {
                if (n.IsTap) continue;
                switch (n.Kind)
                {
                    case SnowNodeKind.Intake:
                    {
                        int at = b.NodeAt(n, HNodeKind.Intake);
                        int w = b.Water(n.Source, n.At, n.Id);
                        b.Link(HLinkKind.Intake, w, at, 0, 0, n.Id);
                        break;
                    }
                    case SnowNodeKind.Outlet:
                    {
                        int at = b.NodeAt(n, HNodeKind.Outlet);
                        int w = b.Water(n.Source, n.At, n.Id);
                        b.Link(HLinkKind.Outlet, at, w, 0, 0, n.Id);
                        break;
                    }
                    case SnowNodeKind.House:
                    {
                        int dis = b.NodeAt(n, HNodeKind.HouseDischarge);
                        int w = b.Water(n.Source, n.At, n.Id);
                        int suc = b.Node(HNodeKind.PumpSuction, n.At, b.Elev[dis], b.Grade[dis], SnowId.None);
                        b.Link(HLinkKind.Intake, w, suc, 0, 0, n.Id);
                        b.Link(HLinkKind.Pump, suc, dis, 0, 0, n.Id, pump: n.Pump);
                        var h = net.FindHouse(n.House);
                        if (h != null) b.HouseCfm.Add((dis, h.CompressorCfm));
                        if (n.Source == 0) b.Diag(1, n.Id, "This pump house has no water source.");
                        break;
                    }
                    default:
                        b.NodeAt(n, n.Kind == SnowNodeKind.Hydrant ? HNodeKind.Hydrant : HNodeKind.Junction);
                        break;
                }
            }
            // Taps (hydrants, junctions; pumps make two sides), then each pipe split at them.
            foreach (var p in net.Pipes)
            {
                var stops = new List<(double c, int node, int nodeAfter)>();
                stops.Add((0, b.NodeOf(p.From), b.NodeOf(p.From)));
                foreach (var t in net.TapsOn(p.Id))
                {
                    double c = SnowGeometry.Project(p.Route, t.At).ChainageMm;
                    if (t.Kind == SnowNodeKind.Pump)
                    {
                        int up = b.Node(HNodeKind.PumpSuction, t.At, double.NaN, double.NaN, SnowId.None);
                        int down = b.Node(HNodeKind.PumpDischarge, t.At, double.NaN, double.NaN, SnowId.None);
                        b.Ground(up, t.At, t.Id);
                        b.Ground(down, t.At, t.Id);
                        b.ByDesign[t.Id] = t.Forward ? down : up;
                        if (t.Forward) b.Link(HLinkKind.Pump, up, down, 0, 0, t.Id, pump: t.Pump);
                        else b.Link(HLinkKind.Pump, down, up, 0, 0, t.Id, pump: t.Pump);
                        stops.Add((c, up, down));
                    }
                    else
                    {
                        int at = b.NodeAt(t, t.Kind == SnowNodeKind.Hydrant ? HNodeKind.Hydrant : HNodeKind.Junction);
                        stops.Add((c, at, at));
                    }
                }
                double len = SnowGeometry.Length(p.Route);
                stops.Add((len, b.NodeOf(p.To), b.NodeOf(p.To)));
                stops.Sort((x, y) => x.c.CompareTo(y.c));
                for (int i = 0; i + 1 < stops.Count; i++)
                    b.Segment(p, stops[i].c, stops[i + 1].c, stops[i].nodeAfter, stops[i + 1].node, sampleMm);
            }
            // Guns on their hydrants.
            foreach (var g in net.Guns)
            {
                int node = -1;
                if (g.Hydrant.IsNone) b.Diag(2, g.Id, "This gun isn't on a hydrant: place a hydrant within 50 ft.");
                else
                {
                    node = b.NodeOf(g.Hydrant);
                    if (node >= 0 && b.Degree(node) == 0) { b.Diag(3, g.Hydrant, "This hydrant is on no pipe."); node = -1; }
                }
                b.Guns.Add(g);
                b.GunNode.Add(node);
            }
            var graph = new HydraulicGraph(b);
            graph.CompileChecks(net);
            return graph;
        }

        /// <summary>The network-shape diagnostics (HY-09 4, 7, 19): no route, a pump facing away, a closed valve.</summary>
        void CompileChecks(SnowNetwork net)
        {
            int n = NodeCount;
            var adj = Adjacency();
            // Reach from water over every open link, pumps only forward, intakes and outlets only their way.
            var reachOpen = Reach(adj, k => !ValveClosed[k], -1);
            Reached = reachOpen;
            var reachAll = Reach(adj, k => true, -1);
            for (int i = 0; i < Guns.Length; i++)
            {
                int node = GunNode[i];
                if (node < 0) continue;
                if (!reachAll[node])
                {
                    if (!ComponentHasWater[Component[node]]) Diagnostics.Add(new SnowDiagnostic(4, Guns[i].Id, "No pipe reaches this gun from a water source."));
                    else Diagnostics.Add(new SnowDiagnostic(4, Guns[i].Id, "No route reaches this gun: a pump on the way pushes the other way."));
                }
                else if (!reachOpen[node]) Diagnostics.Add(new SnowDiagnostic(19, Guns[i].Id, "A closed valve cuts this gun off."));
            }
            // HY-09 7: an operating pump whose suction no water reaches while its discharge side does.
            for (int k = 0; k < LinkCount; k++)
            {
                if (LinkKind[k] != HLinkKind.Pump || !Pump[k].On) continue;
                var without = Reach(adj, j => !ValveClosed[j], k);
                bool sucWet = without[A[k]];
                bool disWet = ReachBackwards(adj, B[k], k);
                if (!sucWet && disWet) Diagnostics.Add(new SnowDiagnostic(7, LinkDesign[k], "This pump faces away from the source: flip it or turn it off."));
            }
        }

        bool[] Reach(List<int>[] adj, Func<int, bool> open, int skip)
        {
            var seen = new bool[NodeCount];
            var stack = new Stack<int>();
            for (int i = 0; i < NodeCount; i++) if (Kind[i] == HNodeKind.Water) { seen[i] = true; stack.Push(i); }
            while (stack.Count > 0)
            {
                int x = stack.Pop();
                foreach (int k in adj[x])
                {
                    if (k == skip || !open(k)) continue;
                    bool directed = LinkKind[k] != HLinkKind.Pipe;
                    int y;
                    if (A[k] == x) y = B[k];
                    else if (!directed) y = A[k];
                    else continue;
                    if (seen[y]) continue;
                    seen[y] = true;
                    stack.Push(y);
                }
            }
            return seen;
        }

        // Whether water reaches node `from` from any source by any way other than link `skip` (pipes both ways, pumps any way).
        bool ReachBackwards(List<int>[] adj, int from, int skip)
        {
            var seen = new bool[NodeCount];
            var stack = new Stack<int>();
            seen[from] = true;
            stack.Push(from);
            while (stack.Count > 0)
            {
                int x = stack.Pop();
                if (Kind[x] == HNodeKind.Water) return true;
                foreach (int k in adj[x])
                {
                    if (k == skip || ValveClosed[k]) continue;
                    int y = A[k] == x ? B[k] : A[k];
                    if (seen[y]) continue;
                    seen[y] = true;
                    stack.Push(y);
                }
            }
            return false;
        }

        sealed class Builder
        {
            public readonly List<HNodeKind> Kind = new List<HNodeKind>();
            public readonly List<double> Elev = new List<double>(), Grade = new List<double>();
            public readonly List<PointMm> At = new List<PointMm>();
            public readonly List<SnowId> Design = new List<SnowId>();
            public readonly List<ulong> Source = new List<ulong>();
            public readonly List<HLinkKind> LKind = new List<HLinkKind>();
            public readonly List<int> LA = new List<int>(), LB = new List<int>(), LDia = new List<int>();
            public readonly List<double> LR = new List<double>(), LLen = new List<double>();
            public readonly List<SnowId> LDesign = new List<SnowId>();
            public readonly List<bool> LClosed = new List<bool>();
            public readonly List<PumpSpec> LPump = new List<PumpSpec>();
            public readonly List<SnowGun> Guns = new List<SnowGun>();
            public readonly List<int> GunNode = new List<int>();
            public readonly List<(int, int)> HouseCfm = new List<(int, int)>();
            public readonly List<SnowDiagnostic> Diags = new List<SnowDiagnostic>();
            public readonly Dictionary<SnowId, int> ByDesign = new Dictionary<SnowId, int>();
            readonly List<int> _degree = new List<int>();
            readonly IGroundHeights _ground;
            readonly int _bury;
            readonly Func<ulong, int?> _level;
            readonly HashSet<SnowId> _noGround = new HashSet<SnowId>();

            public Builder(IGroundHeights ground, int buryMm, Func<ulong, int?> level)
            {
                _ground = ground;
                _bury = buryMm;
                _level = level;
            }

            public int Degree(int node) => _degree[node];
            public int NodeOf(SnowId id) => ByDesign.TryGetValue(id, out int i) ? i : -1;

            public void Diag(int code, SnowId on, string text) => Diags.Add(new SnowDiagnostic(code, on, text));

            public int Node(HNodeKind kind, PointMm at, double elev, double grade, SnowId design, ulong source = 0)
            {
                Kind.Add(kind);
                Elev.Add(elev);
                Grade.Add(grade);
                At.Add(at);
                Design.Add(design);
                Source.Add(source);
                _degree.Add(0);
                return Kind.Count - 1;
            }

            /// <summary>A design node's hydraulic node, its elevation from the ground less the bury.</summary>
            public int NodeAt(SnowNode n, HNodeKind kind)
            {
                int i = Node(kind, n.At, double.NaN, double.NaN, n.Id, n.Source);
                Ground(i, n.At, n.Id);
                ByDesign[n.Id] = i;
                return i;
            }

            public void Ground(int i, PointMm at, SnowId design)
            {
                if (_ground.TryHeightMm(at, out int mm))
                {
                    Grade[i] = mm * FtPerMm;
                    Elev[i] = (mm - _bury) * FtPerMm;
                }
                else
                {
                    Grade[i] = Elev[i] = 0;
                    if (_noGround.Add(design)) Diag(8, design, "No ground height here: the network is outside the area.");
                }
            }

            /// <summary>A source's water surface (a fixed head) behind an intake, a house or an outlet.</summary>
            public int Water(ulong source, PointMm at, SnowId design)
            {
                int? mm = source == 0 ? null : _level(source);
                if (source != 0 && mm == null) Diag(1, design, "Its water source is gone: choose a pond, dam or lake.");
                double ft = (mm ?? 0) * FtPerMm;
                return Node(HNodeKind.Water, at, ft, ft, SnowId.None, source);
            }

            public int Link(HLinkKind kind, int a, int b, double r, double lengthFt, SnowId design, int dia = 0, bool closed = false, PumpSpec pump = default)
            {
                LKind.Add(kind);
                LA.Add(a);
                LB.Add(b);
                LR.Add(r);
                LLen.Add(lengthFt);
                LDia.Add(dia);
                LDesign.Add(design);
                LClosed.Add(closed);
                LPump.Add(pump);
                _degree[a]++;
                _degree[b]++;
                return LKind.Count - 1;
            }

            /// <summary>
            /// One pipe segment from chainage c0 to c1 (HY-03): its 3D length from the ground sampled every
            /// <paramref name="sampleMm"/>, and split at its highest point when that stands above both ends.
            /// </summary>
            public void Segment(SnowPipe p, double c0, double c1, int a, int b, int sampleMm)
            {
                int steps = Math.Max(1, (int)Math.Ceiling((c1 - c0) / sampleMm - 1e-9));
                var z = new double[steps + 1];
                var pts = new PointMm[steps + 1];
                bool ok = true;
                for (int i = 0; i <= steps; i++)
                {
                    pts[i] = SnowGeometry.At(p.Route, c0 + (c1 - c0) * i / steps);
                    if (_ground.TryHeightMm(pts[i], out int mm)) z[i] = mm * FtPerMm;
                    else { ok = false; z[i] = 0; }
                }
                if (!ok && _noGround.Add(p.Id)) Diag(8, p.Id, "No ground height under this pipe: it runs outside the area.");
                double step = (c1 - c0) * FtPerMm / steps;
                int high = -1;
                double top = Math.Max(z[0], z[steps]) + 1.0;   // a high point counts when it stands a foot above both ends
                for (int i = 1; i < steps; i++) if (z[i] > top) { top = z[i]; high = i; }
                bool closedA = p.FromClosed && a == NodeOf(p.From);
                bool closedB = p.ToClosed && b == NodeOf(p.To);
                if (high < 0)
                {
                    double len = Length3D(z, 0, steps, step);
                    Link(HLinkKind.Pipe, a, b, Resistance(len, p.DiameterIn), len, p.Id, p.DiameterIn, closedA || closedB);
                    return;
                }
                int h = Node(HNodeKind.HighPoint, pts[high], z[high] - _bury * FtPerMm, z[high], SnowId.None);
                double l1 = Length3D(z, 0, high, step), l2 = Length3D(z, high, steps, step);
                Link(HLinkKind.Pipe, a, h, Resistance(l1, p.DiameterIn), l1, p.Id, p.DiameterIn, closedA);
                Link(HLinkKind.Pipe, h, b, Resistance(l2, p.DiameterIn), l2, p.Id, p.DiameterIn, closedB);
            }

            static double Length3D(double[] z, int i0, int i1, double step)
            {
                double s = 0;
                for (int i = i0; i < i1; i++) s += Math.Sqrt(step * step + (z[i + 1] - z[i]) * (z[i + 1] - z[i]));
                return Math.Max(s, 1e-3);
            }
        }
    }

    /// <summary>One HY-09 diagnostic: its number (1–20), the entity it points at and the player's words.</summary>
    public readonly struct SnowDiagnostic
    {
        public readonly int Code;
        public readonly SnowId On;
        public readonly string Text;
        public SnowDiagnostic(int code, SnowId on, string text)
        {
            Code = code;
            On = on;
            Text = text;
        }

        /// <summary>Warnings let the network run; errors stop the part they name.</summary>
        public bool IsWarning => Code == 13 && Text.StartsWith("Near", StringComparison.Ordinal) || Code == 14 || Code == 15 || Code == 20;
        public override string ToString() => $"{Code}: {Text}";
    }
}
