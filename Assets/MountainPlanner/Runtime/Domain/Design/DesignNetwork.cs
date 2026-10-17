using System;
using System.Collections.Generic;
using System.IO;

namespace MountainPlanner.Domain.Design
{
    public enum NodeKind : byte { LiftBottom = 1, LiftTop = 2, TrailHead = 3, TrailTail = 4, Junction = 5, PathEnd = 6 }

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
    public interface ITrailGeometry
    {
        /// <summary>The trail's centreline length (TR-03), in mm.</summary>
        int CentrelineLengthMm(Trail trail);
        void FindJunctions(DesignDocument doc, List<TrailJunction> into);
        void FindPaths(DesignDocument doc, List<TrailPath> into);
    }

    /// <summary>The derived ski network (NW-01): rebuilt from the document on every load and edit, never stored.</summary>
    public sealed class NetworkGraph
    {
        public const int FormatVersion = 1;

        /// <summary>Sorted by ID.</summary>
        public IReadOnlyList<NetworkNode> Nodes { get; }
        /// <summary>Sorted by kind, owner, start, then the end nodes.</summary>
        public IReadOnlyList<NetworkEdge> Edges { get; }

        internal NetworkGraph(NetworkNode[] nodes, NetworkEdge[] edges)
        {
            Nodes = nodes;
            Edges = edges;
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
            w.Flush();
            return s.ToArray();
        }
    }

    /// <summary>
    /// Builds the network graph (NW-02, NW-06–NW-08, JN-04) from authored data. Built lifts and built trails with both
    /// ends take part; plans are left out (NW-02). A trail end within 30 ft of a built lift's top or bottom attaches
    /// to it (TR-05, owner ruling P3-03 Q3: attachment is derived, never stored). Trail-to-trail junctions and paths
    /// come from <see cref="ITrailGeometry"/>; without it, each trail is one straight segment from head to tail.
    /// Node IDs are keyed hashes of what they join, so a reload rebuilds the same ones (JN-04).
    /// </summary>
    public static class DesignNetwork
    {
        public static NetworkGraph Build(DesignDocument doc, ITrailGeometry geometry = null)
        {
            var nodes = new Dictionary<ulong, NetworkNode>(); // lookup only; output is sorted below
            var edges = new List<NetworkEdge>();

            void AddNode(ulong id, NodeKind kind, PointMm at)
            {
                if (!nodes.ContainsKey(id)) nodes.Add(id, new NetworkNode(id, kind, at));
            }

            // Lifts: bottom to top.
            var liftEnds = new List<NetworkNode>();
            foreach (var lift in doc.Lifts)
            {
                if (lift.Stage != WorkStage.Built) continue;
                var bottom = new NetworkNode(LiftBottomNode(doc.Seed, lift.Id), NodeKind.LiftBottom, lift.Bottom);
                var top = new NetworkNode(LiftTopNode(doc.Seed, lift.Id), NodeKind.LiftTop, lift.Top);
                AddNode(bottom.Id, bottom.Kind, bottom.At);
                AddNode(top.Id, top.Kind, top.At);
                liftEnds.Add(bottom);
                liftEnds.Add(top);
                edges.Add(new NetworkEdge(bottom.Id, top.Id, EdgeKind.Lift, lift.Id.Value, 0, 0));
            }

            // Trails that take part, with their centreline lengths.
            var trails = new List<Trail>();
            var lengths = new Dictionary<ulong, int>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built || !t.HasEnds) continue;
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
            if (geometry != null)
            {
                var found = new List<TrailJunction>();
                geometry.FindJunctions(doc, found);
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
                    stops[j.A.Value].Add(new Stop(Clamp(j.AlongAMm, lengths[j.A.Value]), id));
                    stops[j.B.Value].Add(new Stop(Clamp(j.AlongBMm, lengths[j.B.Value]), id));
                }

                // Paths (PA-05, PA-06), likewise.
                var foundPaths = new List<TrailPath>();
                geometry.FindPaths(doc, foundPaths);
                var paths = new List<TrailPath>();
                foreach (var p in foundPaths)
                {
                    if (p.A == p.B || !lengths.ContainsKey(p.A.Value) || !lengths.ContainsKey(p.B.Value)) continue;
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

            // Segments (NW-08): each trail cut at its stops, from head towards tail.
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
                // An end snapped to a lift is that lift's node; an end another trail landed on is that junction;
                // otherwise the trail has its own end node.
                ulong head = NearestLiftEnd(liftEnds, t.Head.Value);
                if (head != 0) list.Add(new Stop(0, head));
                else if (!headTaken)
                {
                    head = DesignKey.Hash(doc.Seed, DesignKey.TrailHeadTag, t.Id.Value);
                    AddNode(head, NodeKind.TrailHead, t.Head.Value);
                    list.Add(new Stop(0, head));
                }
                ulong tail = NearestLiftEnd(liftEnds, t.Tail.Value);
                if (tail != 0) list.Add(new Stop(length, tail));
                else if (!tailTaken)
                {
                    tail = DesignKey.Hash(doc.Seed, DesignKey.TrailTailTag, t.Id.Value);
                    AddNode(tail, NodeKind.TrailTail, t.Tail.Value);
                    list.Add(new Stop(length, tail));
                }
                list.Sort((x, y) => x.AlongMm != y.AlongMm ? x.AlongMm.CompareTo(y.AlongMm) : x.Node.CompareTo(y.Node));
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
            return new NetworkGraph(nodeArray, edgeArray);
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

        /// <summary>The nearest built lift end within 30 ft (TR-05), ties to the lower ID; 0 if none.</summary>
        static ulong NearestLiftEnd(List<NetworkNode> liftEnds, PointMm p)
        {
            ulong best = 0;
            long bestD = long.MaxValue;
            long reach = (long)DesignUnits.SnapMm * DesignUnits.SnapMm;
            foreach (var end in liftEnds)
            {
                long d = end.At.DistanceSquared(p);
                if (d > reach) continue;
                if (d < bestD || (d == bestD && end.Id < best))
                {
                    best = end.Id;
                    bestD = d;
                }
            }
            return best;
        }

        readonly struct Stop
        {
            public readonly int AlongMm;
            public readonly ulong Node;
            public Stop(int alongMm, ulong node) { AlongMm = alongMm; Node = node; }
        }
    }
}
