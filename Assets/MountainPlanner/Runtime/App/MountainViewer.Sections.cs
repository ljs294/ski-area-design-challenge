using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Trails made of sections on the map and in Analysis › Trails (task P3-10 B2; owner NT24–NT46): once the network job
    /// has worked out the trails (<see cref="TrailSections"/>), each trail gets one name on the map along its whole line,
    /// one row, and its line lit when focused. A row's ID is the trail's own (two trails can start on one run); the
    /// card's Edit and Remove arm the tool on the run and point the trail starts at.
    /// </summary>
    public sealed partial class MountainViewer
    {
        TrailSections _sections;
        readonly Dictionary<ulong, (TrailId Piece, int StartMm)> _trailRowAt = new Dictionary<ulong, (TrailId, int)>();
        readonly Dictionary<ulong, Vector2[]> _trailRowLine = new Dictionary<ulong, Vector2[]>();

        /// <summary>The trails last worked out (tests and captures); null until the first network build.</summary>
        internal TrailSections Sections => _sections;

        void ApplySections(DesignDocument doc, TrailSections ts)
        {
            _sections = ts;
            if (_trailLayer == null || ts == null || doc == null) return;
            _trailLabelSpots.Clear();
            _trailRows.Clear();
            _trailRowAt.Clear();
            _trailRowLine.Clear();
            var ground = _trailLayer.Ground;
            foreach (var t in ts.Trails)
            {
                var pts = new List<Vector2>();
                double metres = 0, rating = double.NaN, steepest = double.NaN, meanSum = 0, meanLength = 0;
                bool graded = false, any = false;
                foreach (var s in t.Segments)
                {
                    var piece = doc.FindTrail(s.Piece);
                    var own = ts.SegmentsOf(s.Piece);
                    if (piece == null || own.Count == 0 || !_trailLayer.TryGetLine(s.Piece, out var line)) continue;
                    int length = own[own.Count - 1].EndMm;
                    if (length <= 0) continue;
                    Sub(line, (double)s.StartMm / length, (double)s.EndMm / length, pts);
                    double m = (s.EndMm - s.StartMm) / 1000.0;
                    metres += m;
                    any = true;
                    graded |= piece.Settings.Grading == TrailGrading.Graded;
                    // The trail's figures from its runs' (NT34): the hardest run's rating and steepest, a length-weighted mean.
                    if (_trailLayer.TryGetFacts(s.Piece, out var f))
                    {
                        if (!double.IsNaN(f.Rating) && (double.IsNaN(rating) || f.Rating > rating)) rating = f.Rating;
                        if (!double.IsNaN(f.Steepest) && (double.IsNaN(steepest) || f.Steepest > steepest)) steepest = f.Steepest;
                        if (!double.IsNaN(f.Mean)) { meanSum += f.Mean * m; meanLength += m; }
                    }
                }
                if (!any || pts.Count < 2) continue;
                var drawn = pts.ToArray();
                float top = ground.HeightAt(drawn[0].x, drawn[0].y), bottom = ground.HeightAt(drawn[drawn.Length - 1].x, drawn[drawn.Length - 1].y);
                ulong id = t.Id.Value;
                _trailRowAt[id] = (t.Segments[0].Piece, t.Segments[0].StartMm);
                _trailRowLine[id] = drawn;
                string symbol = t.Built && !double.IsNaN(rating) ? TrailMapView.Symbol(SlopeBands.Of(rating)) + " " : "";
                _trailLabelSpots.Add((symbol + t.Name, _trailLayer.LabelSpots(drawn), metres));
                _trailRows.Add(new MountainPlanner.UI.TrailRow
                {
                    Id = id, Name = t.Name, RatingPercent = rating, MeanPercent = meanLength > 0 ? meanSum / meanLength : double.NaN, SteepestPercent = steepest,
                    LengthMetres = metres, VerticalMetres = float.IsNaN(top) || float.IsNaN(bottom) ? double.NaN : Math.Abs(top - bottom),
                    Graded = graded, Built = t.Built,
                });
            }
            // NT7: the longest trails' names place first; equal lengths keep the trails' order.
            _trailLabelOrder.Clear();
            var byLength = new List<int>(_trailLabelSpots.Count);
            for (int i = 0; i < _trailLabelSpots.Count; i++) byLength.Add(i);
            byLength.Sort((a, b) => _trailLabelSpots[a].Length != _trailLabelSpots[b].Length ? _trailLabelSpots[b].Length.CompareTo(_trailLabelSpots[a].Length) : a.CompareTo(b));
            foreach (int i in byLength) _trailLabelOrder.Add((_trailLabelSpots[i].Name, _trailLabelSpots[i].Spots));
            _trailLabels?.SetLabels(_trailLabelOrder);
            if (Hud != null)
            {
                WireTrailList();
                Hud.SetTrails(_trailRows);
            }
        }

        // The stretch of a drawn line between two fractions of its length, appended (the shared point once).
        static void Sub(Vector2[] line, double from, double to, List<Vector2> into)
        {
            double total = 0;
            for (int k = 1; k < line.Length; k++) total += Vector2.Distance(line[k - 1], line[k]);
            double a = from * total, b = to * total, run = 0;
            Vector2 Add(Vector2 p)
            {
                if (into.Count == 0 || (into[into.Count - 1] - p).sqrMagnitude > 0.01f) into.Add(p);
                return p;
            }
            for (int k = 1; k < line.Length; k++)
            {
                float len = Vector2.Distance(line[k - 1], line[k]);
                double s0 = run, s1 = run + len;
                run = s1;
                if (len <= 0 || s1 < a || s0 > b) continue;
                Add(Vector2.Lerp(line[k - 1], line[k], (float)Math.Max(0, (a - s0) / len)));
                Add(Vector2.Lerp(line[k - 1], line[k], (float)Math.Min(1, (b - s0) / len)));
            }
        }

        /// <summary>The row (trail) holding a run's stretch from <paramref name="startMm"/>; the run's own ID before the trails are in.</summary>
        ulong TrailRowOf(ulong piece, int startMm)
        {
            if (_sections == null) return piece;
            var s = _sections.SegmentAt(new TrailId(piece), Math.Max(0, startMm) + 1);
            var t = s != null ? _sections.TrailOf(s) : _sections.TrailOf(new TrailId(piece));
            return t != null && _trailRowAt.ContainsKey(t.Id.Value) ? t.Id.Value : piece;
        }

        void FocusTrailRow(ulong id)
        {
            if (_trailLayer == null) return;
            if (_trailRowLine.TryGetValue(id, out var line)) _trailLayer.FocusLine(line);
            else _trailLayer.Focus(new TrailId(id));
        }

        // A row's Edit or Remove: the tool on the run the trail starts on, picked where it starts (so Edit trail picks
        // this trail, not another holding the same run).
        void ChooseTrailRow(string toolId, ulong id)
        {
            if (!_trailRowAt.TryGetValue(id, out var at)) { ChooseTrailTool(toolId, id); return; }
            var t = Design?.Document.FindTrail(at.Piece);
            PointMm? point = null;
            if (t != null && _netGeometry != null)
            {
                var seg = _sections?.SegmentAt(at.Piece, at.StartMm + 1);
                int mid = seg != null ? (seg.StartMm + seg.EndMm) / 2 : at.StartMm;
                point = _netGeometry.PointAlong(t, mid);
            }
            ChooseTrailTool(toolId, at.Piece.Value, point);
        }
    
        // ---------- -sectionshots <folder>: Edit sections in the game, a picture a step (task P3-10 B2 review) ----------

        void StartSectionShots(string[] args)
        {
            int i = Array.IndexOf(args, "-sectionshots");
            if (i < 0 || i + 1 >= args.Length) return;
            int j = Array.IndexOf(args, "-sectionjoin");   // -sectionjoin "Run 20" "Run 26": that join instead of the tour
            if (j >= 0 && j + 2 < args.Length) StartCoroutine(JoinShots(args[i + 1], args[j + 1].Replace('_', ' '), args[j + 2].Replace('_', ' ')));   // Run_20: no quoting through the lanes
            else StartCoroutine(SectionShots(args[i + 1]));
        }

        /// <summary>
        /// -sectionjoin <from> <to> (owner request, 2026-10-27): the network map over both trails, then Edit sections on
        /// <paramref name="from"/> with a left-click on <paramref name="to"/>'s nearest reachable section (everything on the
        /// way added), Done, and the network map again; taken back after, so the save is untouched.
        /// </summary>
        System.Collections.IEnumerator JoinShots(string dir, string from, string to)
        {
            System.IO.Directory.CreateDirectory(dir);
            while (_resort == null || !_resort.CoverReady.IsCompleted || _edits == null || WorkBusy || _sections == null || NetworkBusy || _tools == null)
                yield return null;
            for (int k = 0; k < 30; k++) yield return null;
            ResolvedTrail a = null, b = null;
            foreach (var t in _sections.Trails) { if (t.Name == from && a == null) a = t; if (t.Name == to && b == null) b = t; }
            if (a == null || b == null || !_trailRowLine.ContainsKey(a.Id.Value) || !_trailRowLine.ContainsKey(b.Id.Value))
            { Debug.LogWarning($"[JoinShots] no trail named {(a == null ? from : to)}"); Application.Quit(); yield break; }
            Debug.Log($"[JoinShots] {a.Name}: {a.Segments.Count} sections; {b.Name}: {b.Segments.Count} sections");
            FrameSections(_trailRowLine[a.Id.Value], _trailRowLine[b.Id.Value]);
            Hud.SetAnalysisTab("network");
            Hud.SetAnalysis(true);
            yield return Shot(dir, "1-network-before");
            Hud.SetAnalysis(false);
            for (int k = 0; k < 10; k++) yield return null;
            yield return Shot(dir, "2-map-before");

            _toolScripted = true;
            ChooseTrailRow(EditTrailTool.ToolId, a.Id.Value);
            var tool = _tools.Active as TrailTool;
            for (int f = 0; f < 3000 && tool != null && (tool.PickedTrail == null || tool.SectionsBusy); f++) { _tools.Tick(default); yield return null; }
            if (tool?.PickedTrail == null) { Debug.LogWarning("[JoinShots] Edit trail picked no trail"); Application.Quit(); yield break; }
            tool.PressEditSections();
            var session = tool.Session;
            // The target trail's sections in the session's own trails: the one a click reaches with the shortest route.
            Segment target = null;
            List<Segment> best = null;
            var bTop = b.Segments[0];
            var bTrail = session.Sections.TrailOf(session.Sections.SegmentAt(bTop.Piece, bTop.StartMm + 1));
            if (bTrail != null)
                foreach (var s in bTrail.Segments)
                {
                    var route = TrailSectionsEdit.Route(session.Sections, session.Trail, s);
                    if (route != null && (best == null || route.Count < best.Count)) { best = route; target = s; }
                }
            if (target == null)
            {
                if (bTrail != null) tool.HoverSection(bTrail.Segments[0]);
                Debug.LogWarning($"[JoinShots] no route from {a.Name} to {b.Name}");
                yield return Shot(dir, "3-no-route");
                _toolScripted = false;
                _tools.Close();
                Application.Quit();
                yield break;
            }
            var names = new List<string>();
            foreach (var s in best) names.Add($"{Design.Document.FindTrail(s.Piece)?.Name} {s.StartMm / 304.8:0}-{s.EndMm / 304.8:0} ft");
            Debug.Log($"[JoinShots] a click on {b.Name} adds {best.Count}: {string.Join(", ", names)}");
            tool.HoverSection(target);
            yield return Shot(dir, "3-hover-join");
            tool.ClickSection(target, false);
            for (int f = 0; f < 3000 && tool.SectionsBusy; f++) { _tools.Tick(default); yield return null; }
            yield return Shot(dir, "4-joined-session");
            var joined = session.Trail;
            Debug.Log($"[JoinShots] {joined?.Name} now {joined?.Segments.Count} sections; was {a.Segments.Count}");
            var before = _sections;
            tool.FinishSections();
            _toolScripted = false;
            _tools.Close();
            Hud.SetToolbox(false);
            for (int f = 0; f < 3000 && (NetworkBusy || WorkBusy || _sections == before); f++) yield return null;
            foreach (var t in _sections.Trails)
                if (t.Name == from || t.Name == to || t.Name.EndsWith(" " + to) || t.Name.EndsWith(" " + from))
                    Debug.Log($"[JoinShots] after: {t.Name}, {t.Segments.Count} sections");
            yield return Shot(dir, "5-map-after");
            Hud.SetAnalysisTab("network");
            Hud.SetAnalysis(true);
            yield return Shot(dir, "6-network-after");
            Hud.SetAnalysisTab("trails");
            yield return Shot(dir, "7-analysis-after");
            Hud.SetAnalysis(false);
            Design?.Undo();   // the save stays as it was
            Debug.Log("[JoinShots] finished");
            Application.Quit();
        }

        // The camera over a trail (and the run a click reaches), clear of the tool window on the left and the profile
        // strip along the bottom: the view's centre is moved toward the camera and to the left of the lines' middle.
        void FrameSections(Vector2[] line, Vector2[] more)
        {
            Vector2 lo = line[0], hi = line[0];
            foreach (var p in line) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
            if (more != null) foreach (var p in more) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
            var mid = (lo + hi) / 2;
            float d = Mathf.Clamp((hi - lo).magnitude * 1.5f, 500, 2400), yaw = 200;
            var fwd = new Vector2(Mathf.Sin(yaw * Mathf.Deg2Rad), Mathf.Cos(yaw * Mathf.Deg2Rad));
            var right = new Vector2(fwd.y, -fwd.x);
            var at = mid - fwd * (0.14f * d) - right * (0.1f * d);
            Camera.Frame(new Vector3(at.x, _resort.Surface.HeightAt(mid.x, mid.y), at.y), d);
            Camera.SetAngles(yaw, 58);
        }

        /// <summary>
        /// Over an opened save: the first trail (in ID order) that a left-click can carry on through a junction picked in
        /// Edit trail, Edit sections opened, the reroute hovered and added, a section hovered to take out, a cut hovered
        /// and made, then Done and the trails' names and rows; every click is taken back after, so the save is untouched.
        /// </summary>
        System.Collections.IEnumerator SectionShots(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            while (_resort == null || !_resort.CoverReady.IsCompleted || _edits == null || WorkBusy || _sections == null || NetworkBusy || _tools == null)
                yield return null;
            for (int k = 0; k < 30; k++) yield return null;
            var ts = _sections;
            ResolvedTrail pick = null;
            foreach (var t in ts.Trails)
            {
                if (!t.Built || !_trailRowAt.ContainsKey(t.Id.Value)) continue;
                foreach (var s in ts.Segments)
                {
                    var route = TrailSectionsEdit.Route(ts, t, s);
                    if (route == null || route.Count > 3 || route[0].Piece == t.Segments[0].Piece) continue;
                    pick = t;
                    break;
                }
                if (pick != null) break;
            }
            if (pick == null) { Debug.LogWarning("[SectionShots] no trail carries on through a junction"); Application.Quit(); yield break; }
            Debug.Log($"[SectionShots] {ts.Trails.Count} trails, {ts.Segments.Count} segments; editing {pick.Name} ({pick.Segments.Count} sections)");
            FrameSections(_trailRowLine[pick.Id.Value], null);
            _toolScripted = true;
            ChooseTrailRow(EditTrailTool.ToolId, pick.Id.Value);
            var tool = _tools.Active as TrailTool;
            for (int f = 0; f < 3000 && tool != null && (tool.PickedTrail == null || tool.SectionsBusy); f++) { _tools.Tick(default); yield return null; }
            if (tool?.PickedTrail == null) { Debug.LogWarning("[SectionShots] Edit trail picked no trail"); Application.Quit(); yield break; }
            yield return Shot(dir, "1-edit-trail");
            tool.PressEditSections();
            yield return Shot(dir, "2-edit-sections");
            var session = tool.Session;
            Segment target = null;
            foreach (var s in session.Sections.Segments)
            {
                var route = TrailSectionsEdit.Route(session.Sections, session.Trail, s);
                if (route != null && route.Count <= 3 && route[0].Piece != session.Trail.Segments[0].Piece) { target = s; break; }
            }
            if (target != null)
            {
                var reach = new List<Vector2>();
                foreach (var s in TrailSectionsEdit.Route(session.Sections, session.Trail, target))
                    if (tool.TryLineOf(s, out var l)) reach.AddRange(l);
                FrameSections(_trailRowLine[pick.Id.Value], reach.ToArray());
                tool.HoverSection(target);
                yield return Shot(dir, "3-hover-reroute");
                tool.ClickSection(target, false);
                for (int f = 0; f < 3000 && tool.SectionsBusy; f++) { _tools.Tick(default); yield return null; }
                yield return Shot(dir, "4-rerouted");
            }
            var trail = session.Trail;
            if (trail != null && trail.Segments.Count > 1)
            {
                tool.HoverSection(trail.Segments[trail.Segments.Count - 1]);
                yield return Shot(dir, "5-hover-take-out");
            }
            trail = session.Trail;
            if (trail != null)
            {
                Segment longest = trail.Segments[0];
                foreach (var s in trail.Segments) if (s.EndMm - s.StartMm > longest.EndMm - longest.StartMm) longest = s;
                tool.HoverSection(longest, cut: true);
                yield return Shot(dir, "6-hover-cut");
                var piece = Design.Document.FindTrail(longest.Piece);
                if (piece != null && _netGeometry != null)
                {
                    tool.CutAt(longest.Piece, _netGeometry.PointAlong(piece, (longest.StartMm + longest.EndMm) / 2));
                    for (int f = 0; f < 3000 && tool.SectionsBusy; f++) { _tools.Tick(default); yield return null; }
                    yield return Shot(dir, "7-cut");
                }
            }
            int clicks = session.Clicks;
            tool.FinishSections();
            Debug.Log($"[SectionShots] Done: {clicks} clicks in one step; {session.Trail?.Name} has {session.Trail?.Segments.Count} sections");
            _toolScripted = false;
            _tools.Close();
            Hud.SetToolbox(false);
            for (int f = 0; f < 3000 && (NetworkBusy || WorkBusy || _sections == ts); f++) yield return null;
            yield return Shot(dir, "8-done-map");
            Hud.SetAnalysisTab("trails");
            Hud.SetAnalysis(true);
            yield return Shot(dir, "9-analysis-trails");
            Hud.SetAnalysis(false);
            if (clicks > 0) Design?.Undo();   // the save stays as it was
            Debug.Log("[SectionShots] finished");
            Application.Quit();
        }
    }
}
