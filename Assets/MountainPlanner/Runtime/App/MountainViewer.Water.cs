using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Water;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.14's water in the viewer: the area's stream lines (WS-08, water.json, read once per area) and the OSM names of its lakes (LK-10), the tools' water
    /// hooks (what a pond's shoreline crosses, the stream a dam crosses and which way is upstream, the lake under a click),
    /// and the body table: the area's lakes plus the design's ponds and reservoirs (<see cref="DesignWater"/>), rebuilt off the
    /// main thread whenever the design moves and sent to the simulation when its hash changes. The first table waits for the
    /// design's water, so a reopened save plays its ponds' ice from the first hour.
    /// </summary>
    public sealed partial class MountainViewer
    {
        sealed class StreamPath
        {
            public StreamLine Line;
            public Vector2[] Xz;   // local metres (x east, z north), OSM's order (downstream)
        }

        List<StreamPath> _streamPaths;
        WaterBodyTable _lakesOnly;
        DesignDocument _waterFor;
        Task<(WaterBodyTable Table, List<Presentation.Ponds.PondWater.Body> Bodies, WaterSources Sources)> _waterBuild;
        /// <summary>Task 4.14 PR B: the sources the worker keeps water for (WS-01), built with the table; null until settled.</summary>
        WaterSources _waterSources;
        Catchments _catchments;
        Presentation.Ponds.PondWater _pondWater;
        EarthworkSite _waterSite;
        DesignDocument _waterBuilding;
        bool _waterSettled;
        object _waterArea;   // the area the above belong to: another area starts afresh

        void FollowWaterArea()
        {
            if (ReferenceEquals(_waterArea, _resort)) return;
            ForgetWater();
            _waterArea = _resort;
        }

        // ---------------- streams (WS-08) ----------------

        List<StreamPath> Streams()
        {
            FollowWaterArea();
            if (_streamPaths != null || _resort == null) return _streamPaths;
            _streamPaths = new List<StreamPath>();
            var m = _resort.Manifest;
            if (m?.Water == null || string.IsNullOrEmpty(_resort.PackageFolder)) return _streamPaths;   // a v2 area: the body only
            try
            {
                foreach (var line in WaterFile.Read(_resort.PackageFolder, m))
                {
                    var xz = new Vector2[line.Points.Count];
                    for (int i = 0; i < xz.Length; i++)
                    {
                        var (x, z) = _resort.Frame.ToLocal(line.Points[i]);
                        xz[i] = new Vector2((float)x, (float)z);
                    }
                    if (xz.Length >= 2) _streamPaths.Add(new StreamPath { Line = line, Xz = xz });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("Streams: " + e.Message + " (the dam card falls back to the streams body)");
                _streamPaths.Clear();
            }
            return _streamPaths;
        }

        Dictionary<ulong, string> _osmLakeNames;

        /// <summary>Task 4.14: the area's water.json arrived in the background (AppFlow.WaterLines); read it from now on.</summary>
        internal void WaterLinesAdded(string packageFolder)
        {
            if (_resort?.PackageFolder == null || packageFolder == null) return;
            if (!string.Equals(System.IO.Path.GetFullPath(_resort.PackageFolder).TrimEnd('\\', '/'),
                               System.IO.Path.GetFullPath(packageFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return;
            try { _resort.Manifest.Water = ResortPackage.ReadManifest(packageFolder).Water; }
            catch (Exception e) when (e is System.IO.IOException || e is System.IO.InvalidDataException) { return; }
            _streamPaths = null;
            _osmLakeNames = null;
            Debug.Log("[Water] the area's stream lines and water names are in");
        }

        /// <summary>
        /// LK-10: a lake's OpenStreetMap name, bound by the named water area's inside point to the lake body under it ("" for
        /// none). Where two names fall in one lake, the first in water.json's order wins.
        /// </summary>
        string OsmLakeName(ulong lake)
        {
            FollowWaterArea();
            var t = LakesOnly;
            if (t == null) return "";
            if (_osmLakeNames == null)
            {
                _osmLakeNames = new Dictionary<ulong, string>();
                var m = _resort.Manifest;
                if (m?.Water != null && !string.IsNullOrEmpty(_resort.PackageFolder))
                {
                    try
                    {
                        foreach (var n in WaterFile.ReadNames(_resort.PackageFolder, m))
                        {
                            var (x, z) = _resort.Frame.ToLocal(n.Inside);
                            int body = BodyAt(t, PointMm.FromMetres(x, z));
                            if (body > WaterBodyTable.Streams && t[body].Kind == WaterBodyKind.Lake && !_osmLakeNames.ContainsKey(t[body].Id))
                                _osmLakeNames.Add(t[body].Id, n.Name);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("Water names: " + e.Message);
                    }
                }
            }
            return _osmLakeNames.TryGetValue(lake, out var name) ? name : "";
        }

        // ---------------- the tools' hooks ----------------

        /// <summary>Sets the water hooks on a new tool context (StartTools).</summary>
        void HookWater(ToolContext c)
        {
            c.WaterUnder = WaterUnder;
            c.StreamsCrossing = StreamsCrossing;
            c.LakeAt = LakeAt;
            c.StreamDistance = StreamDistance;
            c.PreviewWater = PreviewWater;
            c.StreamLines = StreamLines;
        }

        /// <summary>
        /// PD-12: metres from <paramref name="p"/> to the nearest stream within 250 ft, a line from water.json or a cell of the
        /// streams body (an area without lines has only the body), else +∞. With no body table yet, 0: nothing is refused.
        /// </summary>
        double StreamDistance(PointMm p)
        {
            float reach = PondRules.DamStartReachMm / 1000f;
            var at = new Vector2((float)p.XMetres, (float)p.YMetres);
            double best = double.PositiveInfinity;
            var near = NearestStream(at, reach);
            if (near != null)
                for (int i = 0; i + 1 < near.Xz.Length; i++)
                    best = Math.Min(best, Math.Sqrt(SegmentDistanceSquared(at, near.Xz[i], near.Xz[i + 1])));
            var t = LakesOnly;
            if (t == null) return near != null ? best : 0;
            const int Cell = WaterBodyTable.CellMetres;
            int n = (int)Math.Ceiling(reach / Cell);
            long ci = FloorDiv(p.X - (long)t.OriginXMm, Cell * 1000L), cj = FloorDiv(p.Y - (long)t.OriginYMm, Cell * 1000L);
            for (long j = cj - n; j <= cj + n; j++)
            for (long i = ci - n; i <= ci + n; i++)
            {
                if (i < 0 || j < 0 || i >= t.Width || j >= t.Depth) continue;
                if (t.BodyAt((int)(j * t.Width + i)) != WaterBodyTable.Streams) continue;
                double x = (t.OriginXMm / 1000.0) + (i + 0.5) * Cell - p.XMetres, y = (t.OriginYMm / 1000.0) + (j + 0.5) * Cell - p.YMetres;
                double d = Math.Max(0, Math.Sqrt(x * x + y * y) - Cell / 2.0);
                if (d < best) best = d;
            }
            return best <= reach ? best : double.PositiveInfinity;
        }

        int BodyAt(WaterBodyTable t, PointMm p)
        {
            if (t == null) return -1;
            long i = FloorDiv(p.X - (long)t.OriginXMm, WaterBodyTable.CellMetres * 1000L);
            long j = FloorDiv(p.Y - (long)t.OriginYMm, WaterBodyTable.CellMetres * 1000L);
            if (i < 0 || j < 0 || i >= t.Width || j >= t.Depth) return -1;
            return t.BodyAt((int)(j * t.Width + i));
        }

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        WaterBodyTable LakesOnly
        {
            get { FollowWaterArea(); return _lakesOnly ?? _resort?.WaterBodies; }
        }

        string WaterUnder(PointMm p)
        {
            var t = LakesOnly;
            int body = BodyAt(t, p);
            if (body < 0) return null;
            if (body == WaterBodyTable.Streams)
            {
                var near = NearestStream(new Vector2((float)p.XMetres, (float)p.YMetres), 15f);
                return near != null && near.Line.Name.Length > 0 ? near.Line.Name : "a stream";
            }
            return t[body].Kind == WaterBodyKind.Lake ? "a lake" : null;
        }

        StreamPath NearestStream(Vector2 at, float reach)
        {
            StreamPath best = null;
            float bestD = reach * reach;
            foreach (var s in Streams() ?? new List<StreamPath>())
                for (int i = 0; i + 1 < s.Xz.Length; i++)
                {
                    float d = SegmentDistanceSquared(at, s.Xz[i], s.Xz[i + 1]);
                    if (d < bestD) { bestD = d; best = s; }
                }
            return best;
        }

        static float SegmentDistanceSquared(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0;
            return (p - (a + ab * t)).sqrMagnitude;
        }

        /// <summary>
        /// PD-05: the streams a dam line crosses, one per OSM way (a meander crossed twice is one stream), each with a point on
        /// its upstream side: the ground's median at 65, 165 and 330 ft along the stream each way, the higher side upstream; a
        /// tie (under 4 in) sends Upstream = At, and the tool compares the sides' ground instead. Without stream lines, the
        /// streams body's runs along the line, upstream unknown.
        /// </summary>
        IReadOnlyList<StreamCrossing> StreamsCrossing(PointMm a, PointMm b)
        {
            var found = new List<StreamCrossing>();
            var pa = new Vector2((float)a.XMetres, (float)a.YMetres);
            var pb = new Vector2((float)b.XMetres, (float)b.YMetres);
            var paths = Streams();
            if (paths != null && paths.Count > 0)
            {
                foreach (var s in paths)
                    for (int i = 0; i + 1 < s.Xz.Length; i++)
                    {
                        if (!Intersect(pa, pb, s.Xz[i], s.Xz[i + 1], out var hit)) continue;
                        found.Add(Crossing(s, i, hit));
                        break;
                    }
                return found;
            }
            // The streams body: each run of stream cells along the line is one crossing.
            var t = LakesOnly;
            float len = Vector2.Distance(pa, pb);
            int n = Mathf.Max(1, Mathf.CeilToInt(len / 2f));
            int runStart = -1;
            for (int k = 0; k <= n + 1; k++)
            {
                bool wet = false;
                if (k <= n)
                {
                    var q = Vector2.Lerp(pa, pb, k / (float)n);
                    wet = BodyAt(t, PointMm.FromMetres(q.x, q.y)) == WaterBodyTable.Streams;
                }
                if (wet && runStart < 0) runStart = k;
                if (!wet && runStart >= 0)
                {
                    var mid = Vector2.Lerp(pa, pb, (runStart + k - 1) * 0.5f / n);
                    var at = PointMm.FromMetres(mid.x, mid.y);
                    found.Add(new StreamCrossing(0, "", StreamFlow.DefaultWidthMetres("stream"), false, at, at));
                    runStart = -1;
                }
            }
            return found;
        }

        StreamCrossing Crossing(StreamPath s, int segment, Vector2 hit)
        {
            float down = Median(s, segment, hit, +1), up = Median(s, segment, hit, -1);
            var at = PointMm.FromMetres(hit.x, hit.y);
            var upstream = at;
            if (!float.IsNaN(down) && !float.IsNaN(up) && Mathf.Abs(up - down) >= 0.1f)
            {
                var p = Along(s, segment, hit, up > down ? -1 : +1, 50f);
                upstream = PointMm.FromMetres(p.x, p.y);
            }
            return new StreamCrossing(s.Line.Way, s.Line.Name, s.Line.WidthMetres, s.Line.WidthFromOsm, at, upstream);
        }

        float Median(StreamPath s, int segment, Vector2 hit, int dir)
        {
            float h0 = GroundAt(Along(s, segment, hit, dir, 20f)), h1 = GroundAt(Along(s, segment, hit, dir, 50f)), h2 = GroundAt(Along(s, segment, hit, dir, 100f));
            if (float.IsNaN(h0) || float.IsNaN(h1) || float.IsNaN(h2)) return float.NaN;
            return Mathf.Max(Mathf.Min(h0, h1), Mathf.Min(Mathf.Max(h0, h1), h2));
        }

        // The point `metres` along the stream from the hit, downstream (+1) or upstream (−1) in OSM's order.
        static Vector2 Along(StreamPath s, int segment, Vector2 hit, int dir, float metres)
        {
            var at = hit;
            int i = dir > 0 ? segment + 1 : segment;
            float left = metres;
            while (i >= 0 && i < s.Xz.Length)
            {
                float d = Vector2.Distance(at, s.Xz[i]);
                if (d >= left) return Vector2.Lerp(at, s.Xz[i], d > 0 ? left / d : 0);
                left -= d;
                at = s.Xz[i];
                i += dir;
            }
            return at;
        }

        float GroundAt(Vector2 xz) => _resort?.Surface != null ? _resort.Surface.HeightAt(xz.x, xz.y) : float.NaN;

        static bool Intersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out Vector2 hit)
        {
            hit = default;
            var r = b - a;
            var s = d - c;
            float den = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(den) < 1e-9f) return false;
            var ac = c - a;
            float t = (ac.x * s.y - ac.y * s.x) / den, u = (ac.x * r.y - ac.y * r.x) / den;
            if (t < 0 || t > 1 || u < 0 || u > 1) return false;
            hit = a + r * t;
            return true;
        }

        LakeUnder? LakeAt(PointMm p)
        {
            var t = LakesOnly;
            int body = BodyAt(t, p);
            if (body <= 0 || t[body].Kind != WaterBodyKind.Lake) return null;
            var b = t[body];
            string name = Design?.Document?.FindLake(b.Id).Name ?? "";
            if (name.Length == 0) name = OsmLakeName(b.Id);   // LK-10: yours, else OpenStreetMap's
            return new LakeUnder(b.Id, name, b.DepthTenthsFt, b.AreaSquareMetres);
        }

        // ---------------- the body table ----------------

        /// <summary>
        /// Keeps <c>_resort.WaterBodies</c> as the lakes plus the design's ponds and reservoirs. True once the table matches the
        /// open design (the simulation's first table waits for it); a changed table is sent again by its hash.
        /// </summary>
        bool UpdateDesignWater()
        {
            if (_resort?.WaterBodies == null) return false;
            FollowWaterArea();
            _lakesOnly ??= _resort.WaterBodies;
            var design = Design;
            var doc = design?.Document;
            if (doc == null) { SetWaterSources(WaterSources.None); return true; }
            if (_waterBuild != null)
            {
                if (!_waterBuild.IsCompleted) return _waterSettled;
                var built = _waterBuild.IsFaulted ? default : _waterBuild.Result;
                if (_waterBuild.IsFaulted) Debug.LogException(_waterBuild.Exception);
                var forDoc = _waterBuilding;
                _waterBuild = null;
                _waterBuilding = null;
                if (built.Table != null && ReferenceEquals(forDoc, doc))
                {
                    SetWaterTable(built.Table);
                    SetWaterSources(built.Sources ?? WaterSources.None);
                    ShowPonds(built.Bodies);
                    _waterFor = doc;
                    _waterSettled = true;
                    return true;
                }
            }
            if (ReferenceEquals(doc, _waterFor)) return _waterSettled;
            if (doc.Ponds.Count == 0 && doc.Dams.Count == 0)
            {
                SetWaterTable(_lakesOnly);
                ShowPonds(null);
                bool lakeSources = false;
                foreach (var l in doc.Lakes) lakeSources |= l.Source;
                if (!lakeSources)
                {
                    SetWaterSources(WaterSources.None);
                    _waterFor = doc;
                    return _waterSettled = true;
                }
                // WS-04: a lake source's catchment takes a flood over the area: off the main thread too.
                _waterBuilding = doc;
                _waterBuild = BuildLakeSources(_lakesOnly, doc, CatchmentsFor(), Simulation.Pacing.Default.MaxSources);
                return _waterSettled;
            }
            var host = Ground;
            host.Start();
            if (!host.Ready) return _waterSettled = false;
            var fold = host.Earthwork;
            _waterBuilding = doc;
            _waterSite = fold.Site;
            _waterBuild = BuildWater(fold, doc, design.Terrain, _lakesOnly, DamStreamWidths(doc), CatchmentsFor(), Simulation.Pacing.Default.MaxSources);
            return _waterSettled;
        }

        // Its own method, so the lambda's captures are allocated only when a build starts, never on the per-frame path (CF-09).
        static Task<(WaterBodyTable Table, List<Presentation.Ponds.PondWater.Body> Bodies, WaterSources Sources)> BuildWater(DesignEarthwork fold, DesignDocument doc,
            Domain.Edits.TerrainEdits terrain, WaterBodyTable lakes, Dictionary<ulong, double> widths, Catchments catchments, int maxSources) => Task.Run(() =>
        {
            var r = fold.Rebuild(doc, terrain);
            object BuildOf(ulong id) => r.DetailOf(id) ?? fold.DetailOf(id);
            var table = DesignWater.With(lakes, doc, BuildOf, fold.Site);
            var bodies = new List<Presentation.Ponds.PondWater.Body>();
            // PR B: every pond and reservoir is drawn at full pool and moved down to the water it holds (an empty one isn't drawn).
            foreach (var p in doc.Ponds) Add(p.Id.Value);
            foreach (var d in doc.Dams) Add(d.Id.Value);
            void Add(ulong id)
            {
                if (BuildOf(id) is WaterBuild b && b.Wet != null && b.Problem == null)
                    bodies.Add(new Presentation.Ponds.PondWater.Body(id, table.IndexOf(id), b.Wet, b.FullPoolMetres));
            }
            var sources = DesignWater.Sources(table, doc, BuildOf, way => widths.TryGetValue(way, out double w) ? w : 8, catchments, maxSources);
            return (table, bodies, sources);
        });

        static Task<(WaterBodyTable Table, List<Presentation.Ponds.PondWater.Body> Bodies, WaterSources Sources)> BuildLakeSources(WaterBodyTable lakes, DesignDocument doc, Catchments catchments, int maxSources) =>
            Task.Run(() => (lakes, new List<Presentation.Ponds.PondWater.Body>(), DesignWater.Sources(lakes, doc, _ => null, null, catchments, maxSources)));

        /// <summary>WS-03: each dam's stream width (ft): the player's, else OSM's or the class default, else the streams body's 8 ft.</summary>
        Dictionary<ulong, double> DamStreamWidths(DesignDocument doc)
        {
            var widths = new Dictionary<ulong, double>();
            foreach (var d in doc.Dams)
            {
                if (widths.ContainsKey(d.StreamWay)) continue;
                var rec = doc.FindStream(d.StreamWay);
                var s = d.StreamWay != 0 ? StreamOf(d.StreamWay) : null;
                double m = rec.WidthCm > 0 ? rec.WidthCm / 100.0 : s != null ? s.Line.WidthMetres : StreamFlow.DefaultWidthMetres("stream");
                widths[d.StreamWay] = m / 0.3048;
            }
            return widths;
        }

        /// <summary>WS-04: the area's drainage, from the downloaded 8 m heights (once per area).</summary>
        Catchments CatchmentsFor()
        {
            if (_catchments != null || _resort?.SnowMm == null || _lakesOnly == null) return _catchments;
            if (_resort.SnowMm.Length != _lakesOnly.Width * _lakesOnly.Depth) return null;
            return _catchments = Catchments.Of(_lakesOnly.Width, _lakesOnly.Depth, _resort.SnowMm);
        }

        void SetWaterSources(WaterSources s)
        {
            if (s == null || _waterSources != null && _waterSources.Hash == s.Hash) return;
            _waterSources = s;
            _simSourcesSent = false;
        }

        void SetWaterTable(WaterBodyTable t)
        {
            if (t == null || _resort.WaterBodies == t || _resort.WaterBodies != null && _resort.WaterBodies.Hash == t.Hash) return;
            _resort.WaterBodies = t;
            _simWaterSent = false;   // the worker matches its records by id: lakes and unchanged ponds keep their ice
        }

        // Built at full pool; PR B moves each down to the water it holds (ShowPondLevels).
        void ShowPonds(List<Presentation.Ponds.PondWater.Body> bodies)
        {
            _pondBodies = bodies;
            if (bodies == null || bodies.Count == 0) { _pondWater?.Show(Array.Empty<Presentation.Ponds.PondWater.Body>(), _waterSite); return; }
            _pondWater ??= new Presentation.Ponds.PondWater(_resort.Root.transform);
            if (_previewReplaces == 0) _pondWater.Show(bodies, _waterSite);
            else
            {
                var shown = new List<Presentation.Ponds.PondWater.Body>(bodies.Count);
                foreach (var b in bodies) if (b.Id != _previewReplaces) shown.Add(b);   // F3: an edit's review water stands in for it
                _pondWater.Show(shown, _waterSite);
            }
            HideEmptyStarts(Design?.Document);   // PR B: the snapshot moves each to its level from here
        }

        // F3: the tools' review water, its own mesh (id 1, the streams body's look: open water). An edit's review hides the
        // built water it replaces, so the two never stand in the same place.
        Presentation.Ponds.PondWater _previewWater;
        readonly List<Presentation.Ponds.PondWater.Body> _previewBody = new List<Presentation.Ponds.PondWater.Body>(1);
        List<Presentation.Ponds.PondWater.Body> _pondBodies;
        ulong _previewReplaces;

        void PreviewWater(WaterMask wet, double level, ulong replaces)
        {
            _previewBody.Clear();
            var ground = Ground;
            bool show = wet != null && ground != null && ground.Ready && _resort != null;
            if (show)
            {
                _previewWater ??= new Presentation.Ponds.PondWater(_resort.Root.transform);
                _previewBody.Add(new Presentation.Ponds.PondWater.Body(1, WaterBodyTable.Streams, wet, level));
                _previewWater.Show(_previewBody, ground.Earthwork.Site);
            }
            else _previewWater?.Show(_previewBody, default);
            ulong now = show ? replaces : 0;
            if (now == _previewReplaces) return;
            _previewReplaces = now;
            if (_pondWater != null) ShowPonds(_pondBodies);
        }

        IReadOnlyList<Vector2[]> StreamLines()
        {
            var paths = Streams();
            if (paths == null) return null;
            var lines = new List<Vector2[]>(paths.Count);
            foreach (var s in paths) lines.Add(s.Xz);
            return lines;
        }

        void ForgetWater()
        {
            _previewWater?.Dispose();
            _previewWater = null;
            _pondBodies = null;
            _previewReplaces = 0;
            _pondWater?.Dispose();
            _pondWater = null;
            _streamPaths = null;
            _osmLakeNames = null;
            _lakesOnly = null;
            _waterFor = null;
            _waterBuild = null;
            _waterBuilding = null;
            _waterSettled = false;
            _waterSources = null;
            _catchments = null;
        }
    }
}
