using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.25 PR B round 3 (owner 2026-11-21: the dots drove straight across between runs): a driving groomer follows the
    /// trails and paths the way the sim drives them (GO-15: the shortest way over trails and paths, either way along them),
    /// from the network node nearest where it stopped to the one nearest where it starts next. Screen only: the sim's minutes
    /// are its own. Made once a drive (when the snapshot's pieces change), never per frame; a straight line where no route is
    /// found (no network, no geometry, or the places aren't joined).
    /// </summary>
    public sealed partial class MountainViewer
    {
        /// <summary>The route's sample spacing along a trail, metres.</summary>
        const float RouteSampleMetres = 8f;

        sealed class GroomRoute
        {
            public Vector2[] At;
            public float[] Cum;
            public float Length;
        }

        SimNetwork _grNet;
        object _grDoc, _grGeometry;
        Vector2[] _grNodeAt = Array.Empty<Vector2>();
        bool[] _grNodeKnown = Array.Empty<bool>();
        List<int>[] _grAdj = Array.Empty<List<int>>();
        readonly GroomRoute[] _groomerRoute = new GroomRoute[GroomerLayer.Cap];
        readonly long[] _groomerRouteKey = new long[GroomerLayer.Cap];
        readonly List<Vector2> _grScratch = new List<Vector2>();

        /// <summary>Where groomer <paramref name="i"/> is <paramref name="t"/> of the way along its drive from a to b.</summary>
        Vector2 DriveAlong(int i, long key, Vector2 a, Vector2 b, float t)
        {
            key |= 1L << 62;   // never 0, the cleared key
            if (_groomerRouteKey[i] != key)
            {
                // not ready yet (no network or geometry): a straight line now, and asked again next frame
                if (!EnsureGroomNetwork()) return Vector2.Lerp(a, b, t);
                _groomerRouteKey[i] = key;
                _groomerRoute[i] = RouteBetween(a, b);
            }
            var r = _groomerRoute[i];
            if (r == null || r.Length <= 0) return Vector2.Lerp(a, b, t);
            float want = Mathf.Clamp01(t) * r.Length;
            int lo = 0, hi = r.Cum.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (r.Cum[mid] <= want) lo = mid; else hi = mid; }
            float seg = r.Cum[hi] - r.Cum[lo];
            return seg <= 1e-4f ? r.At[lo] : Vector2.Lerp(r.At[lo], r.At[hi], (want - r.Cum[lo]) / seg);
        }

        /// <summary>The network's node places and its trail and path links (again when the network or design changed).</summary>
        bool EnsureGroomNetwork()
        {
            var net = _trailSnowNet;
            var doc = Design?.Document;
            if (net == null || doc == null || _netGeometry == null || net.NodeCount == 0) return false;
            if (ReferenceEquals(net, _grNet) && ReferenceEquals(doc, _grDoc) && ReferenceEquals(_netGeometry, _grGeometry)) return _grNodeAt.Length == net.NodeCount;
            _grNet = net; _grDoc = doc; _grGeometry = _netGeometry;
            Array.Clear(_groomerRouteKey, 0, _groomerRouteKey.Length);
            Array.Clear(_groomerRoute, 0, _groomerRoute.Length);
            int n = net.NodeCount;
            _grNodeAt = new Vector2[n];
            _grNodeKnown = new bool[n];
            _grAdj = new List<int>[n];
            for (int u = 0; u < n; u++) _grAdj[u] = new List<int>();
            var starts = net.EdgeStartMm; var ends = net.EdgeEndMm;
            for (int e = 0; e < net.EdgeFrom.Length; e++)
            {
                byte kind = net.EdgeKindOf[e];
                if (kind != (byte)SimEdgeKind.TrailSegment && kind != (byte)SimEdgeKind.Path) continue;
                _grAdj[net.EdgeFrom[e]].Add(e);
                _grAdj[net.EdgeTo[e]].Add(e);
                if (kind != (byte)SimEdgeKind.TrailSegment || e >= starts.Length || e >= ends.Length) continue;
                var trail = TrailOfEdge(net, e, doc);
                if (trail == null) continue;
                if (!_grNodeKnown[net.EdgeFrom[e]]) { _grNodeAt[net.EdgeFrom[e]] = DesignFrame.ToLocal(_netGeometry.PointAlong(trail, starts[e])); _grNodeKnown[net.EdgeFrom[e]] = true; }
                if (!_grNodeKnown[net.EdgeTo[e]]) { _grNodeAt[net.EdgeTo[e]] = DesignFrame.ToLocal(_netGeometry.PointAlong(trail, ends[e])); _grNodeKnown[net.EdgeTo[e]] = true; }
            }
            return true;
        }

        static Trail TrailOfEdge(SimNetwork net, int e, DesignDocument doc)
        {
            int k = net.EdgeTrail[e];
            return k < 0 || k >= net.TrailIds.Length ? null : doc.FindTrail(new TrailId(net.TrailIds[k]));
        }

        int NearestGroomNode(Vector2 p)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int u = 0; u < _grNodeAt.Length; u++)
            {
                if (!_grNodeKnown[u] || _grAdj[u].Count == 0) continue;
                float d = (_grNodeAt[u] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = u; }
            }
            return best;
        }

        /// <summary>The way from <paramref name="a"/> to <paramref name="b"/> over the trails and paths, or null.</summary>
        GroomRoute RouteBetween(Vector2 a, Vector2 b)
        {
            if (!EnsureGroomNetwork()) return null;
            int s = NearestGroomNode(a), z = NearestGroomNode(b);
            if (s < 0 || z < 0) return null;
            var net = _grNet;
            int n = net.NodeCount;
            var dist = new long[n];
            var pred = new int[n];
            var done = new bool[n];
            for (int u = 0; u < n; u++) { dist[u] = long.MaxValue; pred[u] = -1; }
            dist[s] = 0;
            // plain Dijkstra over a few hundred nodes, once a drive
            for (int it = 0; it < n; it++)
            {
                int u = -1;
                for (int v = 0; v < n; v++) if (!done[v] && dist[v] != long.MaxValue && (u < 0 || dist[v] < dist[u])) u = v;
                if (u < 0 || u == z) break;
                done[u] = true;
                foreach (int e in _grAdj[u])
                {
                    int v = net.EdgeFrom[e] == u ? net.EdgeTo[e] : net.EdgeFrom[e];
                    long dv = dist[u] + Math.Max(0, net.EdgeLengthMm[e]);
                    if (dv < dist[v]) { dist[v] = dv; pred[v] = e; }
                }
            }
            if (dist[z] == long.MaxValue) return null;
            var edges = new List<int>();
            for (int u = z, guard = n; u != s && guard-- > 0;)
            {
                int e = pred[u];
                if (e < 0) return null;
                edges.Add(e);
                u = net.EdgeFrom[e] == u ? net.EdgeTo[e] : net.EdgeFrom[e];
            }
            edges.Reverse();
            var pts = _grScratch;
            pts.Clear();
            pts.Add(a);
            int at = s;
            var doc = Design.Document;
            foreach (int e in edges)
            {
                bool forward = net.EdgeFrom[e] == at;
                int next = forward ? net.EdgeTo[e] : net.EdgeFrom[e];
                var trail = net.EdgeKindOf[e] == (byte)SimEdgeKind.TrailSegment && e < net.EdgeStartMm.Length && e < net.EdgeEndMm.Length ? TrailOfEdge(net, e, doc) : null;
                if (trail != null)
                {
                    int lo = net.EdgeStartMm[e], hi = net.EdgeEndMm[e];
                    int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(hi - lo) / 1000f / RouteSampleMetres));
                    for (int j = 0; j <= steps; j++)
                    {
                        int mm = forward ? lo + (int)((long)(hi - lo) * j / steps) : hi - (int)((long)(hi - lo) * j / steps);
                        pts.Add(DesignFrame.ToLocal(_netGeometry.PointAlong(trail, mm)));
                    }
                }
                else if (_grNodeKnown[next]) pts.Add(_grNodeAt[next]);   // a path: straight to its far node
                at = next;
            }
            pts.Add(b);
            var r = new GroomRoute { At = pts.ToArray(), Cum = new float[pts.Count] };
            for (int j = 1; j < r.At.Length; j++) r.Cum[j] = r.Cum[j - 1] + (r.At[j] - r.At[j - 1]).magnitude;
            r.Length = r.Cum[r.At.Length - 1];
            return r;
        }
    }
}
