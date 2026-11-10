using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Simulation
{
    /// <summary>What an edge of the network is (saved in the sidecar's keys; never renumbered).</summary>
    public enum SimEdgeKind : byte { None = 0, TrailSegment = 1, Path = 2 }

    /// <summary>
    /// SI-21: what the submitter works out about one trail-segment edge, off the worker, from the design it belongs to.
    /// The worker reads these and never computes geometry. Bands are 0 green to 3 double black;
    /// <see cref="NoPosting"/> means the player hasn't put a sign on the trail (only possible in a save made before
    /// posting was required, R1).
    /// </summary>
    public readonly struct TrailEdgeFacts
    {
        /// <summary>A posted band of this value means "no sign": guests then read the true rating (GU-17, 4.21's R1).</summary>
        public const byte NoPosting = 255;

        /// <summary>GU-17, TD-02 v2: the trail's own rating, the band of its steepest sustained pitch.</summary>
        public readonly byte TrueBand;
        /// <summary>GU-17: the sign the player posted, or <see cref="NoPosting"/>.</summary>
        public readonly byte PostedBand;
        /// <summary>GU-07: the trail's mean width, centimetres.</summary>
        public readonly int WidthCm;
        /// <summary>GU-07: this segment's area, square metres (its crowd capacity comes from this).</summary>
        public readonly int AreaM2;
        /// <summary>GU-10: how long a reference skier of the trail's band takes over this segment, real ms at 1×.</summary>
        public readonly int BaseMs;

        public TrailEdgeFacts(byte trueBand, byte postedBand, int widthCm, int areaM2, int baseMs)
        {
            TrueBand = trueBand; PostedBand = postedBand; WidthCm = widthCm; AreaM2 = areaM2; BaseMs = baseMs;
        }
    }

    /// <summary>
    /// SI-21: the submitter's answers about the design's trails, asked once per edge while the network is built, on the
    /// background task that builds it. Implemented in the app over the Domain's ratings and footprints.
    /// </summary>
    public interface INetworkFacts
    {
        /// <summary>
        /// The facts for the piece of <paramref name="trailId"/> from <paramref name="startMm"/> to
        /// <paramref name="endMm"/> along its centreline, or false when the trail isn't rated yet (the edge then takes
        /// safe defaults and guests treat it as a blue of ordinary width).
        /// </summary>
        bool TrailSegment(ulong trailId, int startMm, int endMm, out TrailEdgeFacts facts);
    }

    /// <summary>
    /// LO-10 and GU-07's input (SI-21): the built network as plain arrays, made by the submitter off the worker from the
    /// app's <see cref="NetworkGraph"/>. Ski edges only (trail segments and paths, never a lift), in the graph's order;
    /// each lift's top and bottom as node indices (−1 for a plan). Immutable. The worker searches it with the trails'
    /// readiness bits (4.07 writes them; every built trail is ready until then), so it never builds geometry.
    ///
    /// Beside the graph it carries, per edge, what guests need to choose (GU-07, GU-10): the kind, the trail's true and
    /// posted bands, the length, the mean width, the area and a reference ski time; and per node and per edge a stable
    /// **key**, so skiers can be put back on the right edge when a design change renumbers everything (SI-13).
    /// </summary>
    public sealed class SimNetwork
    {
        public readonly int NodeCount;
        /// <summary>Per ski edge: from and to node, and the trail it runs along as an index into <see cref="TrailIds"/>
        /// (−1 for a path, which is always open).</summary>
        public readonly int[] EdgeFrom, EdgeTo, EdgeTrail;
        /// <summary>Each node's first outgoing edge and how many (edges are grouped by their from node).</summary>
        public readonly int[] FirstEdge, EdgeCount;
        /// <summary>The built trails, in ID order.</summary>
        public readonly ulong[] TrailIds;

        /// <summary>SI-13: each node's stable id, so a skier is put back where it was after a design change.</summary>
        public readonly ulong[] NodeIds;
        /// <summary>SI-13: each edge's stable key, from its trail and the piece of it the edge covers.</summary>
        public readonly ulong[] EdgeKeys;
        /// <summary>A hash over <see cref="NodeIds"/> and <see cref="EdgeKeys"/>: equal means no remap is needed (SI-13).</summary>
        public readonly ulong ShapeHash;

        /// <summary>GU-07: what each edge is.</summary>
        public readonly byte[] EdgeKindOf;
        /// <summary>GU-17: each edge's trail's true and posted band (<see cref="TrailEdgeFacts.NoPosting"/> for no sign).</summary>
        public readonly byte[] EdgeTrueBand, EdgePostedBand;
        /// <summary>The edge's length along the ground, millimetres.</summary>
        public readonly int[] EdgeLengthMm;
        /// <summary>GU-07: the trail's mean width in centimetres, and this edge's area in square metres (0 for a path).</summary>
        public readonly int[] EdgeWidthCm, EdgeAreaM2;
        /// <summary>GU-10: a reference skier's time over the edge, real ms at 1× (a walker's, for a path).</summary>
        public readonly int[] EdgeBaseMs;

        /// <summary>The reverse of the edge list, grouped by to-node: how the routing search runs backwards (GU-07).</summary>
        public readonly int[] IntoFirst, IntoCount, IntoEdge;

        SimNetwork(int nodes, int[] from, int[] to, int[] trail, ulong[] trails, ulong[] nodeIds,
                   ulong[] keys, byte[] kinds, byte[] trueBand, byte[] postedBand, int[] lengthMm, int[] widthCm, int[] areaM2, int[] baseMs)
        {
            NodeCount = nodes;
            EdgeFrom = from; EdgeTo = to; EdgeTrail = trail; TrailIds = trails;
            NodeIds = nodeIds;
            EdgeKeys = keys; EdgeKindOf = kinds; EdgeTrueBand = trueBand; EdgePostedBand = postedBand;
            EdgeLengthMm = lengthMm; EdgeWidthCm = widthCm; EdgeAreaM2 = areaM2; EdgeBaseMs = baseMs;
            FirstEdge = new int[nodes];
            EdgeCount = new int[nodes];
            for (int n = 0; n < nodes; n++) FirstEdge[n] = -1;
            for (int e = 0; e < from.Length; e++)
            {
                if (e > 0 && from[e] < from[e - 1]) throw new ArgumentException("Edges must be grouped by their from node.");
                if (FirstEdge[from[e]] < 0) FirstEdge[from[e]] = e;
                EdgeCount[from[e]]++;
            }
            // The reverse index, by counting sort on the to node (GU-07's search runs from the bases backwards).
            IntoFirst = new int[nodes];
            IntoCount = new int[nodes];
            IntoEdge = new int[from.Length];
            for (int e = 0; e < to.Length; e++) IntoCount[to[e]]++;
            int at = 0;
            for (int n = 0; n < nodes; n++) { IntoFirst[n] = at; at += IntoCount[n]; }
            var fill = new int[nodes];
            for (int e = 0; e < to.Length; e++) IntoEdge[IntoFirst[to[e]] + fill[to[e]]++] = e;

            ulong h = 14695981039346656037UL;
            for (int n = 0; n < nodes; n++) h = Mix(h, nodeIds[n]);
            for (int e = 0; e < keys.Length; e++) { h = Mix(h, keys[e]); h = Mix(h, (ulong)(uint)to[e]); }
            ShapeHash = h;
        }

        static ulong Mix(ulong h, ulong v)
        {
            for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; }
            return h;
        }

        /// <summary>GU-07: this edge's comfortable crowd, skiers (GU-03's density over its area); at least one.</summary>
        public int CapacityOf(int edge, int skiersPerAcre)
        {
            long acresTimes1000 = EdgeAreaM2[edge] * 1000L / 4047;
            int c = (int)(acresTimes1000 * skiersPerAcre / 1000);
            return c < 1 ? 1 : c;
        }

        /// <summary>
        /// A network from raw arrays (tests): edges in any order (sorted here by from node, stably), trails in ID order.
        /// Edges take default facts — a blue trail 100 ft wide, timed from its length — unless
        /// <paramref name="facts"/> gives them, one per edge in the order passed in.
        /// </summary>
        public static SimNetwork Of(int nodes, IReadOnlyList<(int from, int to, int trail)> edges, ulong[] trailIds,
                                    IReadOnlyList<TrailEdgeFacts> facts = null, IReadOnlyList<int> lengthsMm = null)
        {
            var list = new List<(int from, int to, int trail, int k)>(edges.Count);
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                if (e.from < 0 || e.from >= nodes || e.to < 0 || e.to >= nodes) throw new ArgumentException("An edge's node is out of range.");
                if (e.trail < -1 || e.trail >= trailIds.Length) throw new ArgumentException("An edge's trail is out of range.");
                list.Add((e.from, e.to, e.trail, i));
            }
            list.Sort((a, b) => a.from != b.from ? a.from.CompareTo(b.from) : a.k.CompareTo(b.k));
            for (int i = 1; i < trailIds.Length; i++)
                if (trailIds[i - 1] >= trailIds[i]) throw new ArgumentException("Trails must be in ID order, each once.");
            int n = list.Count;
            var from = new int[n]; var to = new int[n]; var trail = new int[n];
            var keys = new ulong[n]; var kinds = new byte[n]; var trueBand = new byte[n]; var posted = new byte[n];
            var lengthMm = new int[n]; var widthCm = new int[n]; var areaM2 = new int[n]; var baseMs = new int[n];
            for (int i = 0; i < n; i++)
            {
                var e = list[i];
                from[i] = e.from; to[i] = e.to; trail[i] = e.trail;
                keys[i] = Key(e.trail < 0 ? 0 : trailIds[e.trail], e.k, e.trail < 0 ? (uint)e.to : 0);
                bool path = e.trail < 0;
                kinds[i] = (byte)(path ? SimEdgeKind.Path : SimEdgeKind.TrailSegment);
                int len = lengthsMm != null && e.k < lengthsMm.Count ? lengthsMm[e.k] : 500_000;   // 500 m
                lengthMm[i] = len;
                if (!path && facts != null && e.k < facts.Count)
                {
                    var f = facts[e.k];
                    trueBand[i] = f.TrueBand; posted[i] = f.PostedBand;
                    widthCm[i] = f.WidthCm; areaM2[i] = f.AreaM2; baseMs[i] = f.BaseMs;
                }
                else
                {
                    trueBand[i] = path ? (byte)0 : (byte)1;
                    posted[i] = trueBand[i];
                    widthCm[i] = path ? 0 : 3048;                        // 100 ft
                    areaM2[i] = path ? 0 : (int)(len / 1000L * 30);
                    baseMs[i] = DefaultMs(len, path);
                }
                if (baseMs[i] < 1) baseMs[i] = 1;
                if (!path && areaM2[i] < 1) areaM2[i] = 1;
            }
            var nodeIds = new ulong[nodes];
            for (int k = 0; k < nodes; k++) nodeIds[k] = (ulong)(k + 1);
            return new SimNetwork(nodes, from, to, trail, trailIds, nodeIds, keys, kinds, trueBand, posted, lengthMm, widthCm, areaM2, baseMs);
        }

        /// <summary>A blue skier at 20 mph, or a walker at 3 mph, over <paramref name="lengthMm"/>: real ms at 1×.</summary>
        static int DefaultMs(int lengthMm, bool path)
        {
            long tenthsMph = path ? 30 : 200;
            // mm ÷ (tenths of a mph × 0.44704 m/s ÷ 10) → ms: mm × 10 000 ÷ (tenths × 4 470).
            long ms = lengthMm * 10_000L / (tenthsMph * 4_470);
            return (int)Math.Max(1, Math.Min(int.MaxValue, ms));
        }

        /// <summary>SI-13: an edge's stable key, from its trail and the piece of the trail it covers.</summary>
        internal static ulong Key(ulong trailId, long startMm, uint extra) =>
            DesignKey.Hash(trailId == 0 ? 0x9E3779B97F4A7C15UL : trailId, 0x45444745UL /* "EDGE" */, unchecked((ulong)startMm), extra);

        /// <summary>
        /// From the app's built graph: the ski edges, their facts from <paramref name="facts"/> (SI-21), and through
        /// <paramref name="liftTop"/>/<paramref name="liftBottom"/> each lift's station nodes (−1 where the lift isn't in
        /// the graph), for the lifts in <paramref name="liftIds"/>' order.
        /// </summary>
        public static SimNetwork From(NetworkGraph graph, IReadOnlyList<ulong> liftIds, out int[] liftTop, out int[] liftBottom,
                                      INetworkFacts facts = null)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            var index = new Dictionary<ulong, int>(graph.Nodes.Count);
            for (int i = 0; i < graph.Nodes.Count; i++) index[graph.Nodes[i].Id] = i;
            var trails = new SortedSet<ulong>();
            foreach (var e in graph.Edges) if (e.Kind == EdgeKind.TrailSegment) trails.Add(e.Owner);
            var trailIds = new ulong[trails.Count];
            trails.CopyTo(trailIds);
            // Keep each kept edge's source edge beside it, so its facts and key can never drift out of order.
            var kept = new List<(int from, int to, int trail, NetworkEdge e, int k)>();
            int ordinal = 0;
            foreach (var e in graph.Edges)
            {
                ordinal++;
                if (e.Kind == EdgeKind.Lift) continue;
                if (!index.TryGetValue(e.From, out int a) || !index.TryGetValue(e.To, out int b)) continue;
                int t = e.Kind == EdgeKind.TrailSegment ? Array.BinarySearch(trailIds, e.Owner) : -1;
                kept.Add((a, b, t, e, ordinal));
            }
            kept.Sort((x, y) => x.from != y.from ? x.from.CompareTo(y.from) : x.k.CompareTo(y.k));
            int n = kept.Count;
            var from = new int[n]; var to = new int[n]; var trail = new int[n];
            var keys = new ulong[n]; var kinds = new byte[n]; var trueBand = new byte[n]; var posted = new byte[n];
            var lengthMm = new int[n]; var widthCm = new int[n]; var areaM2 = new int[n]; var baseMs = new int[n];
            for (int i = 0; i < n; i++)
            {
                var k = kept[i];
                from[i] = k.from; to[i] = k.to; trail[i] = k.trail;
                bool path = k.trail < 0;
                kinds[i] = (byte)(path ? SimEdgeKind.Path : SimEdgeKind.TrailSegment);
                if (path)
                {
                    keys[i] = Key(0, (long)graph.Nodes[k.from].Id, (uint)graph.Nodes[k.to].Id);
                    lengthMm[i] = Distance(graph.Nodes[k.from].At, graph.Nodes[k.to].At);
                    widthCm[i] = 0; areaM2[i] = 0;
                    baseMs[i] = DefaultMs(lengthMm[i], true);
                }
                else
                {
                    keys[i] = Key(k.e.Owner, k.e.StartMm, (uint)(k.e.EndMm - k.e.StartMm));
                    lengthMm[i] = Math.Max(1, k.e.EndMm - k.e.StartMm);
                    if (facts != null && facts.TrailSegment(k.e.Owner, k.e.StartMm, k.e.EndMm, out var f))
                    {
                        trueBand[i] = f.TrueBand; posted[i] = f.PostedBand;
                        widthCm[i] = f.WidthCm; areaM2[i] = f.AreaM2; baseMs[i] = f.BaseMs;
                    }
                    else
                    {
                        trueBand[i] = 1; posted[i] = 1;
                        widthCm[i] = 3048;
                        areaM2[i] = (int)(lengthMm[i] / 1000L * 30);
                        baseMs[i] = DefaultMs(lengthMm[i], false);
                    }
                    if (areaM2[i] < 1) areaM2[i] = 1;
                }
                if (baseMs[i] < 1) baseMs[i] = 1;
            }
            var nodeIds = new ulong[graph.Nodes.Count];
            for (int i = 0; i < nodeIds.Length; i++) nodeIds[i] = graph.Nodes[i].Id;
            liftTop = new int[liftIds.Count];
            liftBottom = new int[liftIds.Count];
            for (int i = 0; i < liftIds.Count; i++)
            {
                var id = new LiftId(liftIds[i]);
                liftTop[i] = index.TryGetValue(graph.StationOf(id, top: true), out int t) ? t : -1;
                liftBottom[i] = index.TryGetValue(graph.StationOf(id, top: false), out int b) ? b : -1;
            }
            return new SimNetwork(graph.Nodes.Count, from, to, trail, trailIds, nodeIds, keys, kinds, trueBand, posted,
                                  lengthMm, widthCm, areaM2, baseMs);
        }

        static int Distance(PointMm a, PointMm b)
        {
            double d = Math.Sqrt(a.DistanceSquared(b));
            return (int)Math.Max(1, Math.Min(int.MaxValue, Math.Round(d)));
        }
    }
}
