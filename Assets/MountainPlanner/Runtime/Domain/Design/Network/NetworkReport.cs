using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>NW-12's warnings. They never block Break ground.</summary>
    public enum NetworkWarning : byte { None = 0, NoWayDown = 1, NoLiftServes = 2, LeadsNowhere = 3 }

    /// <summary>
    /// What the network says about each built lift and trail (NW-03, NW-12, NW-13), worked out from the graph alone.
    /// <list type="bullet">
    /// <item>A lift is <b>connected</b> when a descent from its top (trail segments and paths, never a lift) reaches some
    /// lift's bottom, its own included (NW-03); otherwise "No way down".</item>
    /// <item>A trail is <b>served</b> when a descent from some lift's top runs along it; otherwise "No lift serves this".</item>
    /// <item>A trail <b>leads nowhere</b> when its tail joins no lift bottom, no other trail and no path (NT6): nothing but
    /// its own end within 30 ft of the tail (TR-05).</item>
    /// </list>
    /// Lists are in ID order (SH-04).
    /// </summary>
    public sealed class NetworkReport
    {
        public sealed class LiftStatus
        {
            public LiftId Lift { get; internal set; }
            public bool Connected { get; internal set; }
            /// <summary>Every trail a descent from the top runs along, by ID (NW-13's highlight).</summary>
            public IReadOnlyList<TrailId> Reaches { get; internal set; }
            public NetworkWarning Warning => Connected ? NetworkWarning.None : NetworkWarning.NoWayDown;
        }

        public sealed class TrailStatus
        {
            public TrailId Trail { get; internal set; }
            public bool Served { get; internal set; }
            public bool LeadsNowhere { get; internal set; }
            /// <summary>The first warning that applies: "No lift serves this" before "Leads nowhere".</summary>
            public NetworkWarning Warning => !Served ? NetworkWarning.NoLiftServes : LeadsNowhere ? NetworkWarning.LeadsNowhere : NetworkWarning.None;
        }

        public IReadOnlyList<LiftStatus> Lifts { get; }
        public IReadOnlyList<TrailStatus> Trails { get; }
        public NetworkGraph Graph { get; }

        NetworkReport(NetworkGraph graph, LiftStatus[] lifts, TrailStatus[] trails)
        {
            Graph = graph;
            Lifts = lifts;
            Trails = trails;
        }

        public int LiftWarnings
        {
            get
            {
                int n = 0;
                foreach (var l in Lifts) if (!l.Connected) n++;
                return n;
            }
        }

        public int TrailWarnings
        {
            get
            {
                int n = 0;
                foreach (var t in Trails) if (t.Warning != NetworkWarning.None) n++;
                return n;
            }
        }

        public LiftStatus Of(LiftId id)
        {
            foreach (var l in Lifts) if (l.Lift == id) return l;
            return null;
        }

        public TrailStatus Of(TrailId id)
        {
            foreach (var t in Trails) if (t.Trail == id) return t;
            return null;
        }

        /// <summary>NW-12's words for a warning.</summary>
        public static string Text(NetworkWarning w) => w switch
        {
            NetworkWarning.NoWayDown => "No way down",
            NetworkWarning.NoLiftServes => "No lift serves this",
            NetworkWarning.LeadsNowhere => "Leads nowhere",
            _ => "",
        };

        /// <summary>Builds the network (NW-01) and reports on it.</summary>
        public static NetworkReport Build(DesignDocument doc, ITrailGeometry geometry) => Of(doc, DesignNetwork.Build(doc, geometry));

        public static NetworkReport Of(DesignDocument doc, NetworkGraph graph)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (graph == null) throw new ArgumentNullException(nameof(graph));

            // Ski edges (segments and paths) out of each node, in the graph's edge order.
            var down = new Dictionary<ulong, List<int>>();
            var edges = graph.Edges;
            for (int i = 0; i < edges.Count; i++)
            {
                if (edges[i].Kind == EdgeKind.Lift) continue;
                if (!down.TryGetValue(edges[i].From, out var list)) down.Add(edges[i].From, list = new List<int>());
                list.Add(i);
            }

            var served = new HashSet<ulong>();
            var liftList = new List<LiftStatus>();
            var visited = new HashSet<ulong>();
            var queue = new Queue<ulong>();
            var lifts = new List<Lift>(doc.Lifts);
            lifts.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var lift in lifts)
            {
                if (lift.Stage != WorkStage.Built) continue;
                ulong top = DesignNetwork.LiftTopNode(doc.Seed, lift.Id);
                visited.Clear();
                queue.Clear();
                var reached = new SortedSet<TrailId>();
                bool connected = false;
                visited.Add(top);
                queue.Enqueue(top);
                while (queue.Count > 0)
                {
                    ulong n = queue.Dequeue();
                    if (n != top && graph.TryGetNode(n, out var node) && node.Kind == NodeKind.LiftBottom) connected = true;
                    if (!down.TryGetValue(n, out var outs)) continue;
                    foreach (int i in outs)
                    {
                        var e = edges[i];
                        if (e.Kind == EdgeKind.TrailSegment)
                        {
                            reached.Add(new TrailId(e.Owner));
                            served.Add(e.Owner);
                        }
                        if (visited.Add(e.To)) queue.Enqueue(e.To);
                    }
                }
                // The top itself is a bottom only for a lift whose ends meet, which LF rules forbid; a descent must move.
                liftList.Add(new LiftStatus { Lift = lift.Id, Connected = connected, Reaches = new List<TrailId>(reached) });
            }

            // NT6: what each trail's last 30 ft meets.
            var lengths = new Dictionary<ulong, int>();
            foreach (var e in edges)
                if (e.Kind == EdgeKind.TrailSegment && (!lengths.TryGetValue(e.Owner, out int l) || e.EndMm > l)) lengths[e.Owner] = e.EndMm;
            var joinsAtTail = new HashSet<ulong>();
            foreach (var e in edges)
            {
                if (e.Kind != EdgeKind.TrailSegment) continue;
                int length = lengths[e.Owner];
                if (e.EndMm >= length - DesignUnits.SnapMm && Joins(graph, e.To)) joinsAtTail.Add(e.Owner);
                if (e.StartMm >= length - DesignUnits.SnapMm && Joins(graph, e.From)) joinsAtTail.Add(e.Owner);
            }

            var trailList = new List<TrailStatus>();
            var trails = new List<Trail>(doc.Trails);
            trails.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var t in trails)
            {
                if (t.Stage != WorkStage.Built || !lengths.ContainsKey(t.Id.Value)) continue;
                trailList.Add(new TrailStatus { Trail = t.Id, Served = served.Contains(t.Id.Value), LeadsNowhere = !joinsAtTail.Contains(t.Id.Value) });
            }
            return new NetworkReport(graph, liftList.ToArray(), trailList.ToArray());
        }

        static bool Joins(NetworkGraph graph, ulong node) =>
            graph.TryGetNode(node, out var n) && (n.Kind == NodeKind.LiftBottom || n.Kind == NodeKind.Junction || n.Kind == NodeKind.PathEnd);

        /// <summary>
        /// NW-08, NT9: a segment's own rating, TD-02 on the stations between <paramref name="startMm"/> and
        /// <paramref name="endMm"/> along the trail: (3 × the length-weighted mean + the steepest 75 ft) / 4, percent.
        /// <paramref name="along"/> is each station's distance from the head (the grade's), <paramref name="stationSlope"/>
        /// the trail's rating's. NaN when no station falls in the segment.
        /// </summary>
        public static double SegmentRating(IReadOnlyList<double> stationSlope, IReadOnlyList<double> along, int startMm, int endMm)
        {
            if (stationSlope == null || along == null) throw new ArgumentNullException(nameof(stationSlope));
            double from = startMm / 1000.0, to = endMm / 1000.0;
            int first = -1, last = -1;
            for (int k = 0; k < along.Count && k < stationSlope.Count; k++)
            {
                if (along[k] < from - 1e-6 || along[k] > to + 1e-6) continue;
                if (first < 0) first = k;
                last = k;
            }
            if (first < 0) return double.NaN;
            int n = last - first + 1;
            double sum = 0, weight = 0;
            for (int k = first; k <= last; k++)
            {
                double w = (k < last ? along[k + 1] - along[k] : 0) / 2 + (k > first ? along[k] - along[k - 1] : 0) / 2;
                if (n == 1) w = 1;
                sum += stationSlope[k] * w;
                weight += w;
            }
            double mean = weight > 0 ? sum / weight : 0;
            double max = 0;
            int win = Math.Min(TrailRating.WindowStations, n);
            for (int k = first; k + win <= last + 1; k++)
            {
                double s = 0;
                for (int t = 0; t < win; t++) s += stationSlope[k + t];
                s /= win;
                if (s > max) max = s;
            }
            return (3 * mean + max) / 4;
        }
    }
}
