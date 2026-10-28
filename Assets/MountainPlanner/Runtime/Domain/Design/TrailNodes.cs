using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// NT42 (owner, 2026-10-27, finding S1): the hidden nodes where sections meet. Section ends within 100 ft of each other
    /// (NW-14's station reach) are one node, so ends painted a little apart still meet end to end, for a trail's sections
    /// (<see cref="TrailSections"/>) and for the network (<see cref="DesignNetwork"/>). Derived from the document
    /// alone, never saved, never drawn as a node and never named; the map only draws the lines meeting at
    /// <see cref="PointOf"/>.
    /// <para>Ends merge transitively, closest pairs first (then by trail ID, the head before the tail), but never both ends
    /// of one section, and never a planned end with a built one (NT32). A node's ID is the network's own key for its first
    /// end (<see cref="DesignKey.TrailHeadTag"/> or <see cref="DesignKey.TrailTailTag"/>), so an end on its own keeps the ID
    /// it always had.</para>
    /// </summary>
    public sealed class TrailNodes
    {
        /// <summary>How close two ends are to be one node: NW-14's 100 ft.</summary>
        public static int ReachMm => DesignStations.ReachMm;

        readonly Dictionary<ulong, ulong> _head = new Dictionary<ulong, ulong>(), _tail = new Dictionary<ulong, ulong>();
        readonly Dictionary<ulong, PointMm> _point = new Dictionary<ulong, PointMm>();
        readonly Dictionary<ulong, int> _count = new Dictionary<ulong, int>();

        /// <summary>The document's nodes, built on first use and kept with it.</summary>
        public static TrailNodes Of(DesignDocument doc) => doc.Nodes;

        /// <summary>The node a section's head is in, or 0 when it has no head.</summary>
        public ulong HeadNode(TrailId section) => _head.TryGetValue(section.Value, out var n) ? n : 0;
        public ulong TailNode(TrailId section) => _tail.TryGetValue(section.Value, out var n) ? n : 0;
        /// <summary>Where a node is drawn: the mean of its ends, to the millimetre.</summary>
        public PointMm PointOf(ulong node) => _point[node];
        /// <summary>How many section ends a node holds.</summary>
        public int CountOf(ulong node) => _count.TryGetValue(node, out var n) ? n : 0;

        internal static TrailNodes Build(DesignDocument doc)
        {
            var nodes = new TrailNodes();
            var ends = new List<End>(doc.Trails.Count * 2);
            foreach (var t in doc.Trails)   // ID order
            {
                if (t.Head.HasValue) ends.Add(new End(t, true, t.Head.Value));
                if (t.Tail.HasValue) ends.Add(new End(t, false, t.Tail.Value));
            }
            int n = ends.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            // The trails each group holds, for the guard against both ends of one section.
            var trailsOf = new List<HashSet<ulong>>(n);
            for (int i = 0; i < n; i++) trailsOf.Add(new HashSet<ulong> { ends[i].Trail.Id.Value });

            long reach2 = (long)ReachMm * ReachMm;
            var pairs = new List<(long d2, int a, int b)>();
            for (int a = 0; a < n; a++)
                for (int b = a + 1; b < n; b++)
                {
                    if (ends[a].Trail.Stage != ends[b].Trail.Stage) continue;
                    long d2 = ends[a].At.DistanceSquared(ends[b].At);
                    if (d2 <= reach2) pairs.Add((d2, a, b));
                }
            // Closest first; ties by the ends' own order (trail ID, head before tail), which a and b already are.
            pairs.Sort((x, y) => x.d2 != y.d2 ? x.d2.CompareTo(y.d2) : x.a != y.a ? x.a.CompareTo(y.a) : x.b.CompareTo(y.b));
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            foreach (var (_, a, b) in pairs)
            {
                int ra = Find(a), rb = Find(b);
                if (ra == rb) continue;
                if (trailsOf[ra].Overlaps(trailsOf[rb])) continue;   // never both ends of one section in one node
                int root = Math.Min(ra, rb), other = Math.Max(ra, rb);   // the first end in order stays the root
                parent[other] = root;
                trailsOf[root].UnionWith(trailsOf[other]);
            }
            var sumX = new Dictionary<int, long>();
            var sumY = new Dictionary<int, long>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                var e = ends[r];
                ulong id = DesignKey.Hash(doc.Seed, e.Head ? DesignKey.TrailHeadTag : DesignKey.TrailTailTag, e.Trail.Id.Value);
                (ends[i].Head ? nodes._head : nodes._tail)[ends[i].Trail.Id.Value] = id;
                nodes._count[id] = (nodes._count.TryGetValue(id, out int c) ? c : 0) + 1;
                sumX[r] = (sumX.TryGetValue(r, out long x) ? x : 0) + ends[i].At.X;
                sumY[r] = (sumY.TryGetValue(r, out long y) ? y : 0) + ends[i].At.Y;
            }
            for (int i = 0; i < n; i++)
            {
                if (Find(i) != i) continue;
                var e = ends[i];
                ulong id = DesignKey.Hash(doc.Seed, e.Head ? DesignKey.TrailHeadTag : DesignKey.TrailTailTag, e.Trail.Id.Value);
                int k = nodes._count[id];
                nodes._point[id] = new PointMm((int)Math.Round((double)sumX[i] / k), (int)Math.Round((double)sumY[i] / k));
            }
            return nodes;
        }

        readonly struct End
        {
            public readonly Trail Trail;
            public readonly bool Head;
            public readonly PointMm At;
            public End(Trail trail, bool head, PointMm at) { Trail = trail; Head = head; At = at; }
        }
    }
}
