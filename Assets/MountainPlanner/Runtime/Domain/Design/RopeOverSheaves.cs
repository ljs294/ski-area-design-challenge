using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// One rope side's sheave train as the game poses it (task 07, findings #2 and #6; RP-02's equal sharing): a tree of
    /// pivots, from the top pin (an equaliser, a yoke, the combination's block or a terminal row's beam) down through
    /// yokes and rockers to the sheaves, in the line's vertical plane: s along the line (metres), w up. Positions are the
    /// rest pose, already placed on the tower (its lean included). Nodes are listed parents first.
    /// </summary>
    public sealed class SheaveTree
    {
        public readonly struct Node
        {
            /// <summary>The parent node's index, or −1 for the top pin (which hangs from the head itself).</summary>
            public readonly int Parent;
            public readonly double PivotS, PivotW;
            /// <summary>The safe rotation from the rest pose, radians (from the model's clash sweep).</summary>
            public readonly double RangeMin, RangeMax;

            public Node(int parent, double pivotS, double pivotW, double rangeMin, double rangeMax)
            {
                Parent = parent;
                PivotS = pivotS;
                PivotW = pivotW;
                RangeMin = rangeMin;
                RangeMax = rangeMax;
            }
        }

        public readonly struct Sheave
        {
            /// <summary>The node it turns with (its rocker).</summary>
            public readonly int Parent;
            public readonly double AxleS, AxleW;

            public Sheave(int parent, double axleS, double axleW)
            {
                Parent = parent;
                AxleS = axleS;
                AxleW = axleW;
            }
        }

        public IReadOnlyList<Node> Nodes => _nodes;
        /// <summary>The sheaves in order along the line (s ascending).</summary>
        public IReadOnlyList<Sheave> Sheaves => _sheaves;

        readonly Node[] _nodes;
        readonly Sheave[] _sheaves;

        public SheaveTree(IReadOnlyList<Node> nodes, IReadOnlyList<Sheave> sheaves)
        {
            _nodes = new Node[nodes.Count];
            for (int i = 0; i < _nodes.Length; i++)
            {
                if (nodes[i].Parent >= i) throw new ArgumentException("Nodes must be listed parents first.");
                _nodes[i] = nodes[i];
            }
            _sheaves = new Sheave[sheaves.Count];
            for (int i = 0; i < _sheaves.Length; i++)
            {
                if (sheaves[i].Parent < 0 || sheaves[i].Parent >= _nodes.Length) throw new ArgumentException("A sheave needs a node.");
                if (i > 0 && sheaves[i].AxleS < sheaves[i - 1].AxleS) throw new ArgumentException("Sheaves must be in order along the line.");
                _sheaves[i] = sheaves[i];
            }
        }
    }

    /// <summary>
    /// One rope side posed along the whole line (task 07): every train, from the bottom terminal's row to the top's, posed to
    /// the rope, and the rope's spans rebuilt between each train's last contact and the next train's first (RP-03's
    /// catenaries), a fixed three times so each span leaves its last sheave along that sheave's share of the bend (RP-02).
    /// </summary>
    public sealed class PosedSide
    {
        public IReadOnlyList<RopeOverSheaves.Pose> Poses { get; internal set; }
        /// <summary>Spans[i] runs from train i's last contact to train i+1's first.</summary>
        public IReadOnlyList<LiftRope.Span> Spans { get; internal set; }
        /// <summary>The largest difference, radians, between a span's end slope and the direction its train expects there.</summary>
        public double TangentError { get; internal set; }
        public bool Clamped { get; internal set; }
    }

    /// <summary>
    /// Poses a sheave train to the rope (task 07): the rope arrives at angle φ_in and leaves at φ_out (radians from the line's
    /// +s, positive climbing), and real equalising trains share the bend δ = φ_out − φ_in equally among their n sheaves
    /// (RP-02). So between sheave k and k+1 the rope runs at φ_in + (k+1)·δ/n, and each pivot spanning sheaves a..b turns
    /// until the chord of its first and last axles lies along the mean of those directions, φ_in + (a+b+1)/2·δ/n; each
    /// level's own rotation is what's left after its parent's, clamped to its safe range. The rope then touches each sheave
    /// on the circle of radius ρ (the sheave's radius to its flange top plus the rope's radius) round its axle, on the rope's
    /// side, along the normal to its local direction.
    /// Only the sheaves the rope touches take part (a combination's other row, unloaded, stays at rest on its frame).
    /// </summary>
    public static class RopeOverSheaves
    {
        public sealed class Pose
        {
            /// <summary>Each node's rotation from its rest pose relative to its parent, radians.</summary>
            public double[] Local { get; internal set; }
            /// <summary>Each node's absolute rotation from rest, radians.</summary>
            public double[] Absolute { get; internal set; }
            /// <summary>Each sheave's posed axle, and where the rope touches it (s, w).</summary>
            public double[] AxleS { get; internal set; }
            public double[] AxleW { get; internal set; }
            public double[] ContactS { get; internal set; }
            public double[] ContactW { get; internal set; }
            /// <summary>The largest difference between a rope segment's direction and its share of the bend, radians.</summary>
            public double SharingError { get; internal set; }
            /// <summary>True where a node's rotation was clamped to its safe range.</summary>
            public bool Clamped { get; internal set; }
            /// <summary>The sheaves the rope runs over.</summary>
            public bool[] Touched { get; internal set; }
        }

        /// <summary>
        /// Poses <paramref name="tree"/>. <paramref name="ropeAbove"/> is true for support (the rope rides on top of the
        /// sheaves), false for hold-down (beneath them). <paramref name="touched"/>, if given, marks the sheaves the rope runs
        /// over; the rest stay at rest.
        /// </summary>
        public static Pose Solve(SheaveTree tree, double phiIn, double phiOut, bool ropeAbove, double rho, bool[] touched = null)
        {
            int nodes = tree.Nodes.Count, sheaves = tree.Sheaves.Count;
            var on = new List<int>();
            for (int k = 0; k < sheaves; k++) if (touched == null || touched[k]) on.Add(k);
            int n = on.Count;
            var rank = new int[sheaves];
            for (int k = 0; k < sheaves; k++) rank[k] = -1;
            for (int i = 0; i < n; i++) rank[on[i]] = i;
            double delta = phiOut - phiIn;

            // Each node's touched sheaves, first and last by rank.
            var first = new int[nodes];
            var last = new int[nodes];
            for (int i = 0; i < nodes; i++) { first[i] = int.MaxValue; last[i] = -1; }
            for (int k = 0; k < sheaves; k++)
            {
                if (rank[k] < 0) continue;
                for (int p = tree.Sheaves[k].Parent; p >= 0; p = tree.Nodes[p].Parent)
                {
                    first[p] = Math.Min(first[p], k);
                    last[p] = Math.Max(last[p], k);
                }
            }

            var pose = new Pose { Local = new double[nodes], Absolute = new double[nodes], Touched = new bool[sheaves] };
            for (int k = 0; k < sheaves; k++) pose.Touched[k] = rank[k] >= 0;
            var angle = new double[nodes];
            var tS = new double[nodes];
            var tW = new double[nodes];
            for (int i = 0; i < nodes; i++)
            {
                var node = tree.Nodes[i];
                double parentAngle = node.Parent >= 0 ? angle[node.Parent] : 0;
                double local = 0;
                if (last[i] >= 0 && last[i] > first[i])
                {
                    int a = rank[first[i]], b = rank[last[i]];
                    double target = phiIn + (a + b + 1) / 2.0 * delta / n;
                    var sa = tree.Sheaves[first[i]];
                    var sb = tree.Sheaves[last[i]];
                    double rest = Math.Atan2(sb.AxleW - sa.AxleW, sb.AxleS - sa.AxleS);
                    local = target - rest - parentAngle;
                }
                else if (last[i] >= 0 && node.Parent >= 0)
                    local = 0;   // a link with one touched sheave turns with its parent
                double clamped = Math.Max(node.RangeMin, Math.Min(node.RangeMax, local));
                if (clamped != local) pose.Clamped = true;
                pose.Local[i] = clamped;
                angle[i] = parentAngle + clamped;
                // This node's map from rest to posed: M(p) = R(angle) p + t, composed from its parent's.
                double pts = node.Parent >= 0 ? tS[node.Parent] : 0, ptw = node.Parent >= 0 ? tW[node.Parent] : 0;
                double cl = Math.Cos(clamped), sl = Math.Sin(clamped);
                // A_local(p) = R(l)(p − c) + c, so A_local's translation is c − R(l)c.
                double aS = node.PivotS - (cl * node.PivotS - sl * node.PivotW), aW = node.PivotW - (sl * node.PivotS + cl * node.PivotW);
                double cp = Math.Cos(parentAngle), sp = Math.Sin(parentAngle);
                tS[i] = cp * aS - sp * aW + pts;
                tW[i] = sp * aS + cp * aW + ptw;
            }
            pose.Absolute = angle;

            pose.AxleS = new double[sheaves];
            pose.AxleW = new double[sheaves];
            pose.ContactS = new double[sheaves];
            pose.ContactW = new double[sheaves];
            double side = ropeAbove ? 1 : -1;
            for (int k = 0; k < sheaves; k++)
            {
                var sh = tree.Sheaves[k];
                int p = sh.Parent;
                double c = Math.Cos(angle[p]), s = Math.Sin(angle[p]);
                double x = c * sh.AxleS - s * sh.AxleW + tS[p], w = s * sh.AxleS + c * sh.AxleW + tW[p];
                pose.AxleS[k] = x;
                pose.AxleW[k] = w;
                double dir = rank[k] >= 0 ? phiIn + (rank[k] + 0.5) * delta / n : 0;
                pose.ContactS[k] = x - side * rho * Math.Sin(dir);
                pose.ContactW[k] = w + side * rho * Math.Cos(dir);
            }

            double error = 0;
            for (int i = 0; i + 1 < n; i++)
            {
                int k0 = on[i], k1 = on[i + 1];
                double seg = Math.Atan2(pose.ContactW[k1] - pose.ContactW[k0], pose.ContactS[k1] - pose.ContactS[k0]);
                error = Math.Max(error, Math.Abs(seg - (phiIn + (i + 1) * delta / n)));
            }
            pose.SharingError = error;
            return pose;
        }

        /// <summary>
        /// Poses one rope side: <paramref name="trains"/> in order up the line (the bottom terminal's row first, the top's
        /// last), each with whether the rope rides on top (support) and, for a combination, which sheaves are its hold-down
        /// row (<paramref name="holdRow"/>, null otherwise). The rope runs level into the first train and out of the last
        /// (<paramref name="levelEnds"/>). Tension: RP-03 from <paramref name="t0"/> at <paramref name="y0"/>, with
        /// <paramref name="w"/> newtons per metre.
        /// </summary>
        public static PosedSide PoseLine(IReadOnlyList<SheaveTree> trains, IReadOnlyList<bool> ropeAbove, IReadOnlyList<bool[]> holdRow,
                                         double rho, double t0, double w, double y0, bool levelEnds = true, int iterations = 3)
        {
            int n = trains.Count;
            if (n < 2) throw new ArgumentException("A line needs at least two trains (its terminals' rows).");
            var phiIn = new double[n];
            var phiOut = new double[n];
            // A first guess: chords between the trains' rest contacts' middles.
            var midS = new double[n];
            var midW = new double[n];
            for (int i = 0; i < n; i++)
            {
                var sh = trains[i].Sheaves;
                double side = ropeAbove[i] ? 1 : -1;
                midS[i] = (sh[0].AxleS + sh[sh.Count - 1].AxleS) / 2;
                midW[i] = (sh[0].AxleW + sh[sh.Count - 1].AxleW) / 2 + side * rho;
            }
            for (int i = 0; i < n; i++)
            {
                phiIn[i] = i == 0 && levelEnds ? 0 : Math.Atan2(midW[i] - midW[Math.Max(0, i - 1)], midS[i] - midS[Math.Max(0, i - 1)]);
                phiOut[i] = i == n - 1 && levelEnds ? 0 : Math.Atan2(midW[Math.Min(n - 1, i + 1)] - midW[i], midS[Math.Min(n - 1, i + 1)] - midS[i]);
            }
            var poses = new RopeOverSheaves.Pose[n];
            var spans = new LiftRope.Span[n - 1];
            var side2 = new PosedSide();
            for (int it = 0; it < iterations; it++)
            {
                for (int i = 0; i < n; i++)
                {
                    bool[] touched = null;
                    bool above = ropeAbove[i];
                    if (holdRow != null && holdRow[i] != null)
                    {
                        // A combination: the rope turning up loads its hold-down row (rope beneath), turning down its support row.
                        bool up = phiOut[i] > phiIn[i];
                        touched = new bool[holdRow[i].Length];
                        for (int k = 0; k < touched.Length; k++) touched[k] = holdRow[i][k] == up;
                        above = !up;
                    }
                    poses[i] = Solve(trains[i], phiIn[i], phiOut[i], above, rho, touched);
                }
                for (int i = 0; i + 1 < n; i++)
                {
                    int a = LastTouched(poses[i]), b = FirstTouched(poses[i + 1]);
                    spans[i] = LiftRope.Catenary(poses[i].ContactS[a], poses[i].ContactW[a], poses[i + 1].ContactS[b], poses[i + 1].ContactW[b], t0, w, y0);
                }
                double error = 0;
                for (int i = 0; i + 1 < n; i++)
                {
                    double outSlope = Math.Atan(spans[i].Slope(spans[i].From)), inSlope = Math.Atan(spans[i].Slope(spans[i].To));
                    error = Math.Max(error, Math.Max(Math.Abs(outSlope - phiOut[i]), Math.Abs(inSlope - phiIn[i + 1])));
                    phiOut[i] = outSlope;
                    phiIn[i + 1] = inSlope;
                }
                side2.TangentError = error;
            }
            side2.Poses = poses;
            side2.Spans = spans;
            bool clamped = false;
            foreach (var p in poses) clamped |= p.Clamped;
            side2.Clamped = clamped;
            return side2;
        }

        static int FirstTouched(Pose p)
        {
            for (int k = 0; k < p.Touched.Length; k++) if (p.Touched[k]) return k;
            return 0;
        }

        static int LastTouched(Pose p)
        {
            for (int k = p.Touched.Length - 1; k >= 0; k--) if (p.Touched[k]) return k;
            return p.Touched.Length - 1;
        }
    }
}
