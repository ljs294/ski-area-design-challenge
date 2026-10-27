using System;
using System.Collections.Generic;
using System.IO;

namespace MountainPlanner.Domain.Design
{
    /// <summary>NW-02. <see cref="Station"/> (task 10, NW-14) stands for lift tops and bottoms since graph format 2.</summary>
    public enum NodeKind : byte { LiftBottom = 1, LiftTop = 2, TrailHead = 3, TrailTail = 4, Junction = 5, PathEnd = 6, Station = 7, Cut = 8 }

    public enum EdgeKind : byte { Lift = 1, TrailSegment = 2, Path = 3 }

    /// <summary>PA-06: a path runs downhill, or both ways when its ends are within 3 ft of each other in elevation.</summary>
    public enum PathDirection : byte { AToB = 1, BToA = 2, Both = 3 }

    public readonly struct NetworkNode
    {
        public readonly ulong Id;
        public readonly NodeKind Kind;
        public readonly PointMm At;
        public NetworkNode(ulong id, NodeKind kind, PointMm at) { Id = id; Kind = kind; At = at; }
    }

    /// <summary>
    /// A directed edge (NW-02): a lift going up, a trail segment from head towards tail (NW-08), or a path. The owner
    /// is the lift's, trail's or path's ID; a segment also holds its span along the trail's centreline, in mm.
    /// </summary>
    public readonly struct NetworkEdge
    {
        public readonly ulong From, To;
        public readonly EdgeKind Kind;
        public readonly ulong Owner;
        public readonly int StartMm, EndMm;

        public NetworkEdge(ulong from, ulong to, EdgeKind kind, ulong owner, int startMm, int endMm)
        {
            From = from;
            To = to;
            Kind = kind;
            Owner = owner;
            StartMm = startMm;
            EndMm = endMm;
        }
    }

    /// <summary>
    /// Where two trails meet (NW-07): one's end snapped to a point on the other, or their footprints overlap. Found
    /// by task 08/10's geometry; the along positions are on each trail's centreline, in mm (0 at the head).
    /// </summary>
    public readonly struct TrailJunction
    {
        public readonly TrailId A, B;
        public readonly int AlongAMm, AlongBMm;
        public readonly PointMm At;

        public TrailJunction(TrailId a, int alongAMm, TrailId b, int alongBMm, PointMm at)
        {
            A = a;
            AlongAMm = alongAMm;
            B = b;
            AlongBMm = alongBMm;
            At = at;
        }
    }

    /// <summary>An automatic path between two trails that come within 30 ft (PA-05), found by task 10's geometry.</summary>
    public readonly struct TrailPath
    {
        public readonly TrailId A, B;
        public readonly int AlongAMm, AlongBMm;
        public readonly PointMm AtA, AtB;
        public readonly PathDirection Direction;

        public TrailPath(TrailId a, int alongAMm, PointMm atA, TrailId b, int alongBMm, PointMm atB, PathDirection direction)
        {
            A = a;
            AlongAMm = alongAMm;
            AtA = atA;
            B = b;
            AlongBMm = alongBMm;
            AtB = atB;
            Direction = direction;
        }
    }

    /// <summary>
    /// The seam to trail geometry (centrelines, footprints), which tasks 08 and 10 implement. The network asks it only
    /// about built trails with a head and a tail; anything it returns about other trails is ignored, and its results
    /// are put in a stable order here, so the graph never depends on the order it found them in (SH-04).
    /// </summary>
    /// <summary>A trail passing through a station (NW-07 v2, NW-14): cut at <see cref="AlongMm"/> on its centreline.</summary>
    public readonly struct StationLink
    {
        public readonly TrailId Trail;
        public readonly int AlongMm;
        public readonly ulong Station;
        public StationLink(TrailId trail, int alongMm, ulong station) { Trail = trail; AlongMm = alongMm; Station = station; }
    }

    /// <summary>
    /// Geometry that finds the trails passing through stations (task 10, NW-14): a footprint within a station's reach of
    /// one of its terminals, whose ends don't attach. Optional, so a geometry without it joins trails by their ends only.
    /// </summary>
    public interface IStationLinks
    {
        void FindStationLinks(DesignDocument doc, IReadOnlyList<Station> stations, List<StationLink> into);
    }

    public interface ITrailGeometry
    {
        /// <summary>The trail's centreline length (TR-03), in mm.</summary>
        int CentrelineLengthMm(Trail trail);
        void FindJunctions(DesignDocument doc, List<TrailJunction> into);
        void FindPaths(DesignDocument doc, List<TrailPath> into);
    }

    /// <summary>
    /// A geometry that also knows planned runs (NT44: plans split among plans) and where a point falls along a run (NT45's
    /// cut points). Without it, plans have no junctions and a cut falls along the straight line from head to tail.
    /// </summary>
    public interface IStagedTrailGeometry
    {
        void FindJunctions(DesignDocument doc, WorkStage stage, List<TrailJunction> into);
        /// <summary>How far along the run's centreline (mm from the head) the point nearest <paramref name="p"/> is.</summary>
        int AlongOf(Trail trail, PointMm p);
    }

    /// <summary>
    /// A node on one run, in order from its head (owner NT43): where the run's segments start and end, what made it (its
    /// <see cref="Anchor"/>, for a saved trail to find it again), and the network node it is, after NT42's merging.
    /// </summary>
    public readonly struct PieceStop
    {
        public readonly int AlongMm;
        public readonly ulong Node;
        public readonly Anchor Anchor;
        public PieceStop(int alongMm, ulong node, Anchor anchor) { AlongMm = alongMm; Node = node; Anchor = anchor; }
    }

    /// <summary>The derived ski network (NW-01): rebuilt from the document on every load and edit, never stored.</summary>
    public sealed class NetworkGraph
    {
        /// <summary>2 (task 10): lift ends are stations (NW-14), written after the edges.</summary>
        public const int FormatVersion = 2;

        /// <summary>Sorted by ID.</summary>
        public IReadOnlyList<NetworkNode> Nodes { get; }
        /// <summary>Sorted by kind, owner, start, then the end nodes.</summary>
        public IReadOnlyList<NetworkEdge> Edges { get; }
        /// <summary>NW-14's stations, by ID; each is a node of kind <see cref="NodeKind.Station"/>.</summary>
        public IReadOnlyList<Station> Stations { get; }

        /// <summary>The runs it was built for (NT44: plans and built runs have a graph each).</summary>
        public WorkStage Stage { get; }

        readonly Dictionary<ulong, PieceStop[]> _stops;

        internal NetworkGraph(NetworkNode[] nodes, NetworkEdge[] edges, IReadOnlyList<Station> stations, WorkStage stage = WorkStage.Built,
                              Dictionary<ulong, PieceStop[]> stops = null)
        {
            Nodes = nodes;
            Edges = edges;
            Stations = stations ?? Array.Empty<Station>();
            Stage = stage;
            _stops = stops ?? new Dictionary<ulong, PieceStop[]>();
        }

        /// <summary>
        /// A run's nodes, from its head (NT43): every stop the run was cut at, with what made it, several to a node where
        /// NT42 merged them. Empty for a run not in this graph. Not part of <see cref="Encode"/>.
        /// </summary>
        public IReadOnlyList<PieceStop> StopsOf(TrailId piece) => _stops.TryGetValue(piece.Value, out var s) ? s : Array.Empty<PieceStop>();

        /// <summary>The station node holding a lift's top or bottom; 0 when the lift isn't built.</summary>
        public ulong StationOf(LiftId lift, bool top) => DesignStations.Of(Stations, lift, !top)?.Id ?? 0;

        /// <summary>The station with this node ID, or null.</summary>
        public Station StationById(ulong id)
        {
            foreach (var s in Stations) if (s.Id == id) return s;
            return null;
        }

        public bool TryGetNode(ulong id, out NetworkNode node)
        {
            int lo = 0, hi = Nodes.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = Nodes[mid].Id.CompareTo(id);
                if (c == 0)
                {
                    node = Nodes[mid];
                    return true;
                }
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            node = default;
            return false;
        }

        /// <summary>The graph's canonical bytes, for NW-01's golden.</summary>
        public byte[] Encode()
        {
            using var s = new MemoryStream();
            using var w = new BinaryWriter(s); // little-endian on every platform
            w.Write((byte)'M');
            w.Write((byte)'P');
            w.Write((byte)'N');
            w.Write((byte)'G');
            w.Write((ushort)FormatVersion);
            w.Write(Nodes.Count);
            foreach (var n in Nodes)
            {
                w.Write(n.Id);
                w.Write((byte)n.Kind);
                w.Write(n.At.X);
                w.Write(n.At.Y);
            }
            w.Write(Edges.Count);
            foreach (var e in Edges)
            {
                w.Write(e.From);
                w.Write(e.To);
                w.Write((byte)e.Kind);
                w.Write(e.Owner);
                w.Write(e.StartMm);
                w.Write(e.EndMm);
            }
            w.Write(Stations.Count);
            foreach (var st in Stations)
            {
                w.Write(st.Id);
                w.Write(st.Members.Count);
                foreach (var m in st.Members)
                {
                    w.Write(m.Lift.Value);
                    w.Write(m.Bottom);
                }
            }
            w.Flush();
            return s.ToArray();
        }
    }

    /// <summary>
    /// Builds the network graph (NW-02 v2, NW-06–NW-08, NW-14, JN-04) from authored data. Built lifts and built trails
    /// with both ends take part; plans are left out (NW-02). Lift ends are stations (NW-14, owner NT13): a trail end
    /// within 100 ft of a terminal attaches to its station (owner ruling P3-03 Q3: attachment is derived, never stored),
    /// and a geometry with <see cref="IStationLinks"/> adds the trails passing through one. Trail-to-trail junctions and
    /// paths come from <see cref="ITrailGeometry"/>; without it, each trail is one straight segment from head to tail.
    /// Node IDs are keyed hashes of what they join, so a reload rebuilds the same ones (JN-04).
    /// </summary>
    public static class DesignNetwork
    {
        /// <summary>
        /// PA-05 to PA-08, automatic paths between trails that pass within 30 ft: off for now (owner NT22, 2026-10-26: "a
        /// lot of little paths connecting trails in places where the trees are thin … I do not want this feature yet").
        /// The geometry still finds them; the network adds none.
        /// </summary>
        public static readonly bool AutomaticPaths = false;

        /// <param name="stage">NT44: built runs (the network) or planned runs (their own graph, for their sections).</param>
        public static NetworkGraph Build(DesignDocument doc, ITrailGeometry geometry = null, WorkStage stage = WorkStage.Built)
        {
            var staged = geometry as IStagedTrailGeometry;
            var nodes = new Dictionary<ulong, NetworkNode>(); // lookup only; output is sorted below
            var edges = new List<NetworkEdge>();

            void AddNode(ulong id, NodeKind kind, PointMm at)
            {
                if (!nodes.ContainsKey(id)) nodes.Add(id, new NetworkNode(id, kind, at));
            }

            // Stations (NW-14): every built lift's terminals, merged where their areas overlap; lifts go from the bottom's
            // station to the top's.
            var stations = DesignStations.Of(doc);
            foreach (var st in stations) AddNode(st.Id, NodeKind.Station, st.Centre);
            foreach (var lift in doc.Lifts)
            {
                if (lift.Stage != WorkStage.Built) continue;
                ulong bottom = DesignStations.Of(stations, lift.Id, true).Id, top = DesignStations.Of(stations, lift.Id, false).Id;
                edges.Add(new NetworkEdge(bottom, top, EdgeKind.Lift, lift.Id.Value, 0, 0));
            }

            // Trails that take part, with their centreline lengths.
            var trails = new List<Trail>();
            var lengths = new Dictionary<ulong, int>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != stage || !t.HasEnds) continue;
                int length = geometry != null
                    ? geometry.CentrelineLengthMm(t)
                    : (int)Math.Min(int.MaxValue, Math.Round(Math.Sqrt(t.Head.Value.DistanceSquared(t.Tail.Value))));
                if (length <= 0) continue;
                trails.Add(t);
                lengths.Add(t.Id.Value, length);
            }

            var stops = new Dictionary<ulong, List<Stop>>();
            foreach (var t in trails) stops.Add(t.Id.Value, new List<Stop>());

            // Junctions (NW-07), normalised so A < B and put in a stable order.
            var crossings = new List<(ulong piece, ulong other, int along)>();
            if (geometry != null && (stage == WorkStage.Built || staged != null))
            {
                var found = new List<TrailJunction>();
                if (staged != null) staged.FindJunctions(doc, stage, found);
                else geometry.FindJunctions(doc, found);
                var junctions = new List<TrailJunction>();
                foreach (var j in found)
                {
                    if (j.A == j.B || !lengths.ContainsKey(j.A.Value) || !lengths.ContainsKey(j.B.Value)) continue;
                    junctions.Add(j.A.CompareTo(j.B) < 0 ? j : new TrailJunction(j.B, j.AlongBMm, j.A, j.AlongAMm, j.At));
                }
                junctions.Sort((x, y) =>
                {
                    int c = x.A.CompareTo(y.A);
                    if (c == 0) c = x.B.CompareTo(y.B);
                    if (c == 0) c = x.AlongAMm.CompareTo(y.AlongAMm);
                    if (c == 0) c = x.AlongBMm.CompareTo(y.AlongBMm);
                    if (c == 0) c = x.At.X.CompareTo(y.At.X);
                    if (c == 0) c = x.At.Y.CompareTo(y.At.Y);
                    return c;
                });
                foreach (var j in junctions)
                {
                    ulong id = JunctionNode(doc.Seed, j.A, j.B, j.At);
                    if (nodes.ContainsKey(id)) continue; // the same junction reported twice
                    AddNode(id, NodeKind.Junction, j.At);
                    int la = lengths[j.A.Value], lb = lengths[j.B.Value];
                    int aa = Clamp(j.AlongAMm, la), ab = Clamp(j.AlongBMm, lb);
                    stops[j.A.Value].Add(new Stop(aa, id, JunctionAnchor(aa, la, j.B, ab, lb)));
                    stops[j.B.Value].Add(new Stop(ab, id, JunctionAnchor(ab, lb, j.A, aa, la)));
                    if (aa > 0 && aa < la && ab > 0 && ab < lb) { crossings.Add((j.A.Value, j.B.Value, aa)); crossings.Add((j.B.Value, j.A.Value, ab)); }
                }

                // Paths (PA-05, PA-06), likewise; off for now (owner NT22).
                var foundPaths = new List<TrailPath>();
                if (AutomaticPaths) geometry.FindPaths(doc, foundPaths);
                var paths = new List<TrailPath>();
                foreach (var p in foundPaths)
                {
                    if (p.A == p.B || !lengths.ContainsKey(p.A.Value) || !lengths.ContainsKey(p.B.Value)) continue;
                    // Both ends in one station (NW-14 v2): the station already joins them, so no path.
                    var inA = DesignStations.At(stations, p.AtA);
                    if (inA != null && inA == DesignStations.At(stations, p.AtB)) continue;
                    if (p.A.CompareTo(p.B) < 0) paths.Add(p);
                    else
                    {
                        var dir = p.Direction == PathDirection.AToB ? PathDirection.BToA : p.Direction == PathDirection.BToA ? PathDirection.AToB : p.Direction;
                        paths.Add(new TrailPath(p.B, p.AlongBMm, p.AtB, p.A, p.AlongAMm, p.AtA, dir));
                    }
                }
                paths.Sort((x, y) =>
                {
                    int c = x.A.CompareTo(y.A);
                    if (c == 0) c = x.B.CompareTo(y.B);
                    if (c == 0) c = x.AlongAMm.CompareTo(y.AlongAMm);
                    if (c == 0) c = x.AlongBMm.CompareTo(y.AlongBMm);
                    if (c == 0) c = x.Direction.CompareTo(y.Direction);
                    return c;
                });
                foreach (var p in paths)
                {
                    ulong pathId = PathId(doc.Seed, p.A, p.B, p.AtA);
                    ulong endA = DesignKey.Hash(doc.Seed, DesignKey.PathEndTag, pathId, 0);
                    ulong endB = DesignKey.Hash(doc.Seed, DesignKey.PathEndTag, pathId, 1);
                    if (nodes.ContainsKey(endA)) continue;
                    AddNode(endA, NodeKind.PathEnd, p.AtA);
                    AddNode(endB, NodeKind.PathEnd, p.AtB);
                    stops[p.A.Value].Add(new Stop(Clamp(p.AlongAMm, lengths[p.A.Value]), endA));
                    stops[p.B.Value].Add(new Stop(Clamp(p.AlongBMm, lengths[p.B.Value]), endB));
                    if (p.Direction != PathDirection.BToA) edges.Add(new NetworkEdge(endA, endB, EdgeKind.Path, pathId, 0, 0));
                    if (p.Direction != PathDirection.AToB) edges.Add(new NetworkEdge(endB, endA, EdgeKind.Path, pathId, 0, 0));
                }
            }

            // Trails passing through a station (NW-07 v2): cut there, the station their stop.
            if (geometry is IStationLinks stationLinks && stations.Count > 0)
            {
                var links = new List<StationLink>();
                stationLinks.FindStationLinks(doc, stations, links);
                links.Sort((x, y) => x.Trail != y.Trail ? x.Trail.CompareTo(y.Trail) : x.Station != y.Station ? x.Station.CompareTo(y.Station) : x.AlongMm.CompareTo(y.AlongMm));
                foreach (var l in links)
                    if (lengths.TryGetValue(l.Trail.Value, out int length) && nodes.ContainsKey(l.Station))
                    {
                        Station st = null;
                        foreach (var x in stations) if (x.Id == l.Station) { st = x; break; }
                        var m = st != null && st.Members.Count > 0 ? st.Members[0] : default;
                        stops[l.Trail.Value].Add(new Stop(Clamp(l.AlongMm, length), l.Station, Anchor.Station(m.Lift, m.Bottom)));
                    }
            }

            // NT45: the points the player cut are nodes on their runs.
            foreach (var c in doc.Cuts)
            {
                if (!lengths.TryGetValue(c.Piece.Value, out int length)) continue;
                var t = doc.FindTrail(c.Piece);
                int along = staged != null ? staged.AlongOf(t, c.At) : StraightAlong(t, c.At, length);
                if (along <= 0 || along >= length) continue;
                ulong id = DesignKey.Hash(doc.Seed, DesignKey.TrailHeadTag, c.Piece.Value, PackMetres(c.At) ^ 0x8000_0000_0000_0000UL);
                AddNode(id, NodeKind.Cut, c.At);
                stops[c.Piece.Value].Add(new Stop(along, id, Anchor.Cut(c.At)));
            }

            // Crossings counted along each run, so "the k-th crossing with that run" names one (NT43).
            if (crossings.Count > 0)
            {
                crossings.Sort((x, y) => x.piece != y.piece ? x.piece.CompareTo(y.piece) : x.other != y.other ? x.other.CompareTo(y.other) : x.along.CompareTo(y.along));
                var kOf = new Dictionary<(ulong, ulong, int), int>();
                for (int i = 0, k = 0; i < crossings.Count; i++)
                {
                    k = i > 0 && crossings[i].piece == crossings[i - 1].piece && crossings[i].other == crossings[i - 1].other ? k + 1 : 0;
                    kOf[crossings[i]] = k;
                }
                foreach (var t in trails)
                {
                    var list = stops[t.Id.Value];
                    for (int i = 0; i < list.Count; i++)
                        if (list[i].Anchor.Kind == AnchorKind.Crosses && kOf.TryGetValue((t.Id.Value, list[i].Anchor.Other, list[i].AlongMm), out int k))
                            list[i] = new Stop(list[i].AlongMm, list[i].Node, Anchor.Crosses(new TrailId(list[i].Anchor.Other), k));
                }
            }

            // Ends: an end in a station (NW-14: within 100 ft of a terminal) is that station; an end another trail landed on
            // is that junction; otherwise the trail has its own end node.
            foreach (var t in trails)
            {
                int length = lengths[t.Id.Value];
                var list = stops[t.Id.Value];
                bool headTaken = false, tailTaken = false;
                foreach (var s in list)
                {
                    headTaken |= s.AlongMm == 0;
                    tailTaken |= s.AlongMm == length;
                }
                ulong head = DesignStations.At(stations, t.Head.Value)?.Id ?? 0;
                if (head != 0) list.Add(new Stop(0, head, Anchor.Head));
                else if (!headTaken)
                {
                    head = DesignKey.Hash(doc.Seed, DesignKey.TrailHeadTag, t.Id.Value);
                    AddNode(head, NodeKind.TrailHead, t.Head.Value);
                    list.Add(new Stop(0, head, Anchor.Head));
                }
                ulong tail = DesignStations.At(stations, t.Tail.Value)?.Id ?? 0;
                if (tail != 0) list.Add(new Stop(length, tail, Anchor.Tail));
                else if (!tailTaken)
                {
                    tail = DesignKey.Hash(doc.Seed, DesignKey.TrailTailTag, t.Id.Value);
                    AddNode(tail, NodeKind.TrailTail, t.Tail.Value);
                    list.Add(new Stop(length, tail, Anchor.Tail));
                }
            }

            // NT42: nodes within 100 ft of each other are one (under the hood: never named, never drawn as a node).
            var merged = MergeClose(doc, nodes, stops, trails, lengths);
            ulong Merged(ulong id) => merged.TryGetValue(id, out ulong to) ? to : id;
            if (merged.Count > 0)
                for (int i = edges.Count - 1; i >= 0; i--)
                {
                    var e = edges[i];
                    if (e.Kind != EdgeKind.Path) continue;
                    ulong from = Merged(e.From), to = Merged(e.To);
                    if (from == to) edges.RemoveAt(i);   // a path whose two ends became one node
                    else if (from != e.From || to != e.To) edges[i] = new NetworkEdge(from, to, e.Kind, e.Owner, e.StartMm, e.EndMm);
                }

            // Segments (NW-08): each trail cut at its stops, from head towards tail; a stretch between two stops of one
            // node is no segment. Each run keeps its stops, with what made them, for its sections (NT43).
            var pieceStops = new Dictionary<ulong, PieceStop[]>();
            foreach (var t in trails)
            {
                var list = stops[t.Id.Value];
                for (int i = 0; i < list.Count; i++) list[i] = new Stop(list[i].AlongMm, Merged(list[i].Node), list[i].Anchor);
                list.Sort((x, y) => x.AlongMm != y.AlongMm ? x.AlongMm.CompareTo(y.AlongMm) : x.Node != y.Node ? x.Node.CompareTo(y.Node) : x.Anchor.Rank.CompareTo(y.Anchor.Rank));
                var kept = new PieceStop[list.Count];
                for (int i = 0; i < list.Count; i++) kept[i] = new PieceStop(list[i].AlongMm, list[i].Node, list[i].Anchor);
                pieceStops.Add(t.Id.Value, kept);
                for (int i = 1; i < list.Count; i++)
                {
                    if (list[i].Node == list[i - 1].Node) continue;
                    edges.Add(new NetworkEdge(list[i - 1].Node, list[i].Node, EdgeKind.TrailSegment, t.Id.Value, list[i - 1].AlongMm, list[i].AlongMm));
                }
            }

            var nodeArray = new NetworkNode[nodes.Count];
            nodes.Values.CopyTo(nodeArray, 0);
            Array.Sort(nodeArray, (x, y) => x.Id.CompareTo(y.Id));
            var edgeArray = edges.ToArray();
            Array.Sort(edgeArray, (x, y) =>
            {
                int c = x.Kind.CompareTo(y.Kind);
                if (c == 0) c = x.Owner.CompareTo(y.Owner);
                if (c == 0) c = x.StartMm.CompareTo(y.StartMm);
                if (c == 0) c = x.From.CompareTo(y.From);
                if (c == 0) c = x.To.CompareTo(y.To);
                return c;
            });
            return new NetworkGraph(nodeArray, edgeArray, stations, stage, pieceStops);
        }

        /// <summary>
        /// NT42 (owner, 2026-10-27): the nodes that are one. Section ends merge as <see cref="TrailNodes"/> has them (the
        /// rule a trail's sections chain by), into a station when one of them is in it; then junctions and path ends join
        /// any node within 100 ft, closest first, but never two different end nodes together, so the network and the
        /// sections agree. Stations never merge with each other, and a merge that would leave a trail no segment is
        /// skipped. Returns each merged node's new ID (a station's, else the lowest of its members'); the merged nodes are
        /// replaced in <paramref name="nodes"/>, at the mean of their places, as junctions.
        /// </summary>
        static Dictionary<ulong, ulong> MergeClose(DesignDocument doc, Dictionary<ulong, NetworkNode> nodes, Dictionary<ulong, List<Stop>> stops,
                                                   List<Trail> trails, Dictionary<ulong, int> lengths)
        {
            var ids = new List<ulong>(nodes.Keys);
            ids.Sort();
            var index = new Dictionary<ulong, int>(ids.Count);
            for (int i = 0; i < ids.Count; i++) index[ids[i]] = i;
            int n = ids.Count;
            var parent = new int[n];
            var members = new List<List<int>>(n);
            var cluster = new ulong[n];   // the TrailNodes node a set holds ends of, or 0
            for (int i = 0; i < n; i++) { parent[i] = i; members.Add(new List<int> { i }); }
            var trailsAt = new Dictionary<ulong, List<ulong>>();   // node: the trails stopping there
            foreach (var t in trails)
                foreach (var st in stops[t.Id.Value])
                {
                    if (!trailsAt.TryGetValue(st.Node, out var list)) trailsAt[st.Node] = list = new List<ulong>();
                    list.Add(t.Id.Value);
                }
            bool IsStation(int i) => nodes[ids[i]].Kind == NodeKind.Station;
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            void Union(int a, int b, bool ends)
            {
                int ra = Find(a), rb = Find(b);
                if (ra == rb) return;
                bool sa = members[ra].Exists(IsStation), sb = members[rb].Exists(IsStation);
                if (sa && sb) return;                                                             // stations never merge
                if (!ends && cluster[ra] != 0 && cluster[rb] != 0 && cluster[ra] != cluster[rb]) return;
                // a trail whose every stop would be this one node keeps its segment
                var touched = new HashSet<ulong>();
                foreach (int m in members[ra]) if (trailsAt.TryGetValue(ids[m], out var ts)) touched.UnionWith(ts);
                foreach (ulong tr in touched)
                {
                    bool all = true;
                    foreach (var st in stops[tr]) { int r = Find(index[st.Node]); if (r != ra && r != rb) { all = false; break; } }
                    if (all) return;
                }
                int root = sa ? ra : sb ? rb : Math.Min(ra, rb), other = root == ra ? rb : ra;
                parent[other] = root;
                members[root].AddRange(members[other]);
                if (cluster[root] == 0) cluster[root] = cluster[other];
            }

            // 1. The ends, as TrailNodes merges them (only built trails take part in the network).
            var nodesOf = TrailNodes.Of(doc);
            var byCluster = new SortedDictionary<ulong, List<int>>();
            foreach (var t in trails)
            {
                foreach (var st in stops[t.Id.Value])
                {
                    bool head = st.AlongMm == 0, tail = st.AlongMm == lengths[t.Id.Value];
                    if (!head && !tail) continue;
                    ulong c = head ? nodesOf.HeadNode(t.Id) : nodesOf.TailNode(t.Id);
                    if (c == 0) continue;
                    if (!byCluster.TryGetValue(c, out var list)) byCluster[c] = list = new List<int>();
                    list.Add(index[st.Node]);
                }
            }
            foreach (var kv in byCluster)
            {
                var list = kv.Value;
                list.Sort();
                foreach (int i in list) if (cluster[Find(i)] == 0) cluster[Find(i)] = kv.Key;
                for (int i = 1; i < list.Count; i++) Union(list[0], list[i], true);
            }

            // 2. Junctions and path ends join any node within 100 ft, closest first.
            long reach2 = (long)TrailNodes.ReachMm * TrailNodes.ReachMm;
            var pairs = new List<(long d2, int a, int b)>();
            for (int a = 0; a < n; a++)
            {
                var na = nodes[ids[a]];
                if (na.Kind == NodeKind.Station) continue;
                for (int b = a + 1; b < n; b++)
                {
                    var nb = nodes[ids[b]];
                    if (nb.Kind == NodeKind.Station) continue;
                    bool loose = na.Kind == NodeKind.Junction || na.Kind == NodeKind.PathEnd || nb.Kind == NodeKind.Junction || nb.Kind == NodeKind.PathEnd;
                    if (!loose || na.Kind == NodeKind.Cut || nb.Kind == NodeKind.Cut) continue;   // a point the player cut stays (NT45)
                    long d2 = na.At.DistanceSquared(nb.At);
                    if (d2 <= reach2) pairs.Add((d2, a, b));
                }
            }
            pairs.Sort((x, y) => x.d2 != y.d2 ? x.d2.CompareTo(y.d2) : x.a != y.a ? x.a.CompareTo(y.a) : x.b.CompareTo(y.b));
            foreach (var (_, a, b) in pairs) Union(a, b, false);

            // The merged nodes: a station's ID and place, else the lowest member's ID at the mean of their places.
            var merged = new Dictionary<ulong, ulong>();
            for (int r = 0; r < n; r++)
            {
                if (Find(r) != r || members[r].Count < 2) continue;
                int st = -1, low = members[r][0];
                long x = 0, y = 0;
                foreach (int m in members[r])
                {
                    if (IsStation(m)) st = m;
                    if (m < low) low = m;
                    x += nodes[ids[m]].At.X;
                    y += nodes[ids[m]].At.Y;
                }
                ulong rep = ids[st >= 0 ? st : low];
                var at = st >= 0 ? nodes[rep].At : new PointMm((int)(x / members[r].Count), (int)(y / members[r].Count));
                foreach (int m in members[r]) if (ids[m] != rep) { merged[ids[m]] = rep; nodes.Remove(ids[m]); }
                nodes[rep] = new NetworkNode(rep, st >= 0 ? NodeKind.Station : NodeKind.Junction, at);
            }
            return merged;
        }

        public static ulong LiftBottomNode(ulong seed, LiftId lift) => DesignKey.Hash(seed, DesignKey.LiftBottomTag, lift.Value);
        public static ulong LiftTopNode(ulong seed, LiftId lift) => DesignKey.Hash(seed, DesignKey.LiftTopTag, lift.Value);

        /// <summary>JN-04: a junction's ID, from the two trails (in order) and where it is, to the metre.</summary>
        public static ulong JunctionNode(ulong seed, TrailId a, TrailId b, PointMm at)
        {
            if (b.CompareTo(a) < 0) (a, b) = (b, a);
            return DesignKey.Hash(seed, DesignKey.JunctionTag, a.Value, b.Value, PackMetres(at));
        }

        /// <summary>JN-04: a path's ID, from the two trails (in order) and where it leaves the first, to the metre.</summary>
        public static ulong PathId(ulong seed, TrailId a, TrailId b, PointMm atA) => DesignKey.Hash(seed, DesignKey.PathTag, a.Value, b.Value, PackMetres(atA));

        static ulong PackMetres(PointMm p) => ((ulong)(uint)FloorDiv(p.X, 1000) << 32) | (uint)FloorDiv(p.Y, 1000);

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : (int)(((long)a - b + 1) / b);

        static int Clamp(int along, int length) => along < 0 ? 0 : along > length ? length : along;

        /// <summary>
        /// What a junction is on one run (NT43): its own head or tail; the other run's head or tail landing on it; or a
        /// crossing (numbered along the run afterwards).
        /// </summary>
        static Anchor JunctionAnchor(int along, int length, TrailId other, int otherAlong, int otherLength)
        {
            if (along == 0) return Anchor.Head;
            if (along == length) return Anchor.Tail;
            if (otherAlong == 0) return Anchor.Lands(other, true);
            if (otherAlong == otherLength) return Anchor.Lands(other, false);
            return Anchor.Crosses(other, 0);
        }

        /// <summary>Where a point falls along a run, on the straight line from its head to its tail (no geometry).</summary>
        static int StraightAlong(Trail t, PointMm p, int length)
        {
            double hx = t.Head.Value.X, hy = t.Head.Value.Y, dx = t.Tail.Value.X - hx, dy = t.Tail.Value.Y - hy, l2 = dx * dx + dy * dy;
            double f = l2 > 0 ? ((p.X - hx) * dx + (p.Y - hy) * dy) / l2 : 0;
            return Clamp((int)Math.Round(Math.Max(0, Math.Min(1, f)) * length), length);
        }

        readonly struct Stop
        {
            public readonly int AlongMm;
            public readonly ulong Node;
            public readonly Anchor Anchor;
            public Stop(int alongMm, ulong node, Anchor anchor = default) { AlongMm = alongMm; Node = node; Anchor = anchor; }
        }
    }
}
