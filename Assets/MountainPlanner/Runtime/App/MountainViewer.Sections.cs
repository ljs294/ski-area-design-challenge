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
        readonly Dictionary<ulong, Rect> _trailRowBounds = new Dictionary<ulong, Rect>();

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
            _trailRowBounds.Clear();
            var ground = _trailLayer.Ground;
            var lines = new List<(Vector2[] Line, PreviewInk Ink)>();
            var covered = new HashSet<TrailId>();
            foreach (var t in ts.Trails)
            {
                var pts = new List<Vector2>();
                var joins = new List<int>();
                TrailId last = default;
                double metres = 0, rating = double.NaN, steepest = double.NaN, meanSum = 0, meanLength = 0;
                bool graded = false, any = false;
                foreach (var s in t.Segments)
                {
                    var piece = doc.FindTrail(s.Piece);
                    var own = ts.SegmentsOf(s.Piece);
                    if (piece == null || own.Count == 0 || !_trailLayer.TryGetLine(s.Piece, out var line)) continue;
                    int length = own[own.Count - 1].EndMm;
                    if (length <= 0) continue;
                    if (pts.Count > 0 && s.Piece != last) joins.Add(pts.Count - 1);   // the trail turns onto another run here
                    last = s.Piece;
                    covered.Add(s.Piece);
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
                var whole = pts.ToArray();
                float top = ground.HeightAt(whole[0].x, whole[0].y), bottom = ground.HeightAt(whole[whole.Length - 1].x, whole[whole.Length - 1].y);
                // Findings 5 and 6: its turns onto other runs rounded, and stopped 50 ft short of its top and bottom.
                var drawn = TrailLineShape.Trim(TrailLineShape.RoundJoins(whole, joins));
                lines.Add((drawn, TrailMapView.InkOf(t.Built, rating)));
                ulong id = t.Id.Value;
                _trailRowAt[id] = (t.Segments[0].Piece, t.Segments[0].StartMm);
                _trailRowLine[id] = drawn;
                _trailRowBounds[id] = BoundsOf(drawn);
                string symbol = t.Built && !double.IsNaN(rating) ? TrailMapView.Symbol(SlopeBands.Of(rating)) + " " : "";
                _trailLabelSpots.Add((symbol + t.Name, _trailLayer.LabelSpots(drawn), metres));
                _trailRows.Add(new MountainPlanner.UI.TrailRow
                {
                    Id = id, Name = t.Name, RatingPercent = rating, MeanPercent = meanLength > 0 ? meanSum / meanLength : double.NaN, SteepestPercent = steepest,
                    LengthMetres = metres, VerticalMetres = float.IsNaN(top) || float.IsNaN(bottom) ? double.NaN : Math.Abs(top - bottom),
                    Graded = graded, Built = t.Built,
                });
            }
            _trailLayer.SetTrailLines(lines, covered);
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

        static Rect BoundsOf(Vector2[] line)
        {
            Vector2 lo = line[0], hi = line[0];
            foreach (var p in line) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
            return Rect.MinMaxRect(lo.x, lo.y, hi.x, hi.y);
        }

        // ---------- trails from the map (sections finding 4) ----------

        ulong _hoveredTrailRow, _focusedTrailRow, _trailPressId;
        Vector2 _trailPress;
        bool _trailPressed;

        /// <summary>The trail under the pointer (its row ID), or 0 (tests and captures).</summary>
        internal ulong HoveredTrail => _hoveredTrailRow;
        /// <summary>For captures: the pointer for the trail hover (screen pixels), or null for the mouse.</summary>
        internal Vector2? TrailPointerForCapture;

        // Every frame: with no tool armed, the pointer off the panels, out of photo mode and the Network view, and no lift
        // under it (lifts first), the trail line nearest the pointer within 8 pixels lights its whole trail; a click (not a
        // drag) opens its card, as a lift's does (F6). Allocates nothing.
        void UpdateTrailHover()
        {
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            Vector2? at = TrailPointerForCapture ?? (mouse != null ? mouse.position.ReadValue() : (Vector2?)null);
            bool picking = Hud != null && cam != null && at.HasValue && !_networkOn && !PhotoOpen && (_tools == null || !_tools.Armed)
                           && _trailLayer != null && _trailLayer.Visible && _hoveredId == 0;
            ulong hit = 0;
            Vector2 pos = at ?? default;
            if (picking && !PointerOverUi(pos)) hit = TrailUnder(cam, pos);
            if (hit != _hoveredTrailRow)
            {
                _hoveredTrailRow = hit;
                FocusTrailRow(hit != 0 ? hit : _focusedTrailRow);
            }
            if (!picking || mouse == null) { _trailPressed = false; return; }
            if (mouse.leftButton.wasPressedThisFrame && !PointerOverUi(pos))
            {
                _trailPressed = hit != 0;
                _trailPress = pos;
                _trailPressId = hit;
            }
            if (!_trailPressed || !mouse.leftButton.wasReleasedThisFrame) return;
            _trailPressed = false;
            if ((pos - _trailPress).sqrMagnitude <= 36 && hit == _trailPressId) Hud.OpenTrailCardFor(hit);
        }

        // The trail whose drawn line passes within 8 pixels of the pointer on the ground (5 m at least), nearest first.
        ulong TrailUnder(UnityEngine.Camera cam, Vector2 screen)
        {
            if (_trailRowLine.Count == 0) return 0;
            var ray = cam.ScreenPointToRay(screen);
            float along = Camera.GroundAlong(ray, 30000);
            if (float.IsNaN(along)) return 0;
            var g = ray.GetPoint(along);
            var p = new Vector2(g.x, g.z);
            float reach = Mathf.Max(5f, 8 * along * 2 * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, cam.pixelHeight));
            float best = reach * reach;
            ulong hit = 0;
            foreach (var kv in _trailRowLine)
            {
                if (!_trailRowBounds.TryGetValue(kv.Key, out var box)) continue;
                if (p.x < box.xMin - reach || p.x > box.xMax + reach || p.y < box.yMin - reach || p.y > box.yMax + reach) continue;
                var line = kv.Value;
                for (int k = 1; k < line.Length; k++)
                {
                    var a = line[k - 1];
                    var d = line[k] - a;
                    float l2 = d.sqrMagnitude;
                    float f = l2 > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, d) / l2) : 0;
                    float dd = (a + d * f - p).sqrMagnitude;
                    if (dd < best) { best = dd; hit = kv.Key; }
                }
            }
            return hit;
        }

        /// <summary>The row (trail) holding a run's stretch from <paramref name="startMm"/>; the run's own ID before the trails are in.</summary>
        ulong TrailRowOf(ulong piece, int startMm)
        {
            if (_sections == null) return piece;
            var s = _sections.SegmentAt(new TrailId(piece), Math.Max(0, startMm) + 1);
            var t = s != null ? _sections.TrailOf(s) : _sections.TrailOf(new TrailId(piece));
            return t != null && _trailRowAt.ContainsKey(t.Id.Value) ? t.Id.Value : piece;
        }

        // The trail card's rename, one undoable step that leaves the ground as it is: a shaped trail as a trail (NT43), a
        // run's own trail as the run.
        string RenameTrailRow(ulong id, string name)
        {
            var history = Design;
            if (history == null) return "No design is open";
            ResolvedTrail trail = null;
            if (_sections != null) foreach (var t in _sections.Trails) if (t.Id.Value == id) { trail = t; break; }
            DesignCommand cmd = trail?.Line != null
                ? TrailSectionsEdit.Rename(history.Document, history.Revision, _sections, trail, name)
                : new RenameTrail(history.Revision, trail != null ? trail.Segments[0].Piece : new TrailId(id), name);
            var r = history.Submit(cmd);
            return r.Accepted ? null : r.Reason;
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
            if (Array.IndexOf(args, "-shapeshots") >= 0) { StartCoroutine(ShapeShots(args[i + 1])); return; }   // finding 7
            int f = Array.IndexOf(args, "-findingshots");   // -findingshots: the sections findings round's fixes instead
            if (f >= 0) { StartCoroutine(FindingShots(args[i + 1])); return; }
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

        /// <summary>
        /// -sectionshots &lt;dir&gt; -shapeshots (sections finding 7, save v8): an older save opened (its one-time regrade and
        /// notice), Edit trail picking a trail (no strokes), and Repaint showing the trail's own clearing as paint. Nothing
        /// is saved.
        /// </summary>
        System.Collections.IEnumerator ShapeShots(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            while (_resort == null || !_resort.CoverReady.IsCompleted || _edits == null || WorkBusy || _sections == null || NetworkBusy || _tools == null)
                yield return null;
            var open = Persistence.Saves.OpenDesign.Current;
            for (int k = 0; k < 3000 && open != null && open.NeedsRegrade; k++) yield return null;
            while (!_edits.Applying.IsCompleted) yield return null;
            for (int k = 0; k < 20; k++) yield return null;
            Debug.Log($"[ShapeShots] opened v{open?.MigratedFrom}: regrade {(open != null && !open.NeedsRegrade ? "done" : "not needed or pending")}, dirty {open?.Dirty}");
            ResolvedTrail run = null;
            foreach (var t in _sections.Trails) if (t.Name == "Run 30" && _trailRowLine.ContainsKey(t.Id.Value)) run = t;
            foreach (var t in _sections.Trails) if (run == null && _trailRowLine.ContainsKey(t.Id.Value)) run = t;
            if (run == null) { Debug.LogWarning("[ShapeShots] no trails"); Application.Quit(); yield break; }
            FrameSections(_trailRowLine[run.Id.Value], null);
            yield return Shot(dir, "1-opened-older-save");
            _toolScripted = true;
            ChooseTrailRow(EditTrailTool.ToolId, run.Id.Value);
            var tool = _tools.Active as TrailTool;
            for (int f = 0; f < 3000 && tool != null && (tool.PickedTrail == null || tool.SectionsBusy); f++) { _tools.Tick(default); yield return null; }
            for (int k = 0; k < 10; k++) { _tools.Tick(default); yield return null; }
            Debug.Log($"[ShapeShots] picked {run.Name}: {tool?.Strokes} strokes held");
            yield return Shot(dir, "2-edit-pick");
            tool?.RepaintForCapture();
            for (int f = 0; f < 3000 && tool != null && tool.Analysing; f++) { _tools.Tick(default); yield return null; }
            for (int k = 0; k < 10; k++) { _tools.Tick(default); yield return null; }
            Debug.Log($"[ShapeShots] repaint: {tool?.Strokes} strokes, clearing {tool?.Raster?.Cells} cells");
            yield return Shot(dir, "3-repaint-own-clearing");
            _toolScripted = false;
            _tools.Close();
            Debug.Log("[ShapeShots] finished");
            Application.Quit();
        }

        /// <summary>
        /// -sectionshots &lt;dir&gt; -findingshots (sections findings 1-6, 2026-10-27): over an opened save, the trail lines by
        /// trail; a trail hovered on the main screen and its card (its name open for typing); Edit trail on it (the trail
        /// lit, the Graded box, no Rename button) with its title open for typing; and a built graded trail's locked box.
        /// Nothing is changed, so the save is untouched.
        /// </summary>
        System.Collections.IEnumerator FindingShots(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            while (_resort == null || !_resort.CoverReady.IsCompleted || _edits == null || WorkBusy || _sections == null || NetworkBusy || _tools == null)
                yield return null;
            for (int k = 0; k < 30; k++) yield return null;
            ResolvedTrail Named(string name)
            {
                foreach (var t in _sections.Trails) if (t.Name == name && _trailRowLine.ContainsKey(t.Id.Value)) return t;
                return null;
            }
            // The owner's two (finding 5), else the first trail holding more than one run and the next with a line.
            var a = Named("Run 30");
            var b = Named("Upper Run 6");
            foreach (var t in _sections.Trails)
            {
                if (!_trailRowLine.ContainsKey(t.Id.Value)) continue;
                if (a == null && t.Line != null) a = t;
                else if (b == null && t != a) b = t;
            }
            if (a == null) { Debug.LogWarning("[FindingShots] no trails"); Application.Quit(); yield break; }
            Debug.Log($"[FindingShots] {_sections.Trails.Count} trails; over {a.Name} and {b?.Name}");
            var lineA = _trailRowLine[a.Id.Value];
            FrameSections(lineA, b != null ? _trailRowLine[b.Id.Value] : null);
            yield return Shot(dir, "1-map-lines");
            // Close over where the two come nearest (the owner's picture for finding 5), and the rest from there.
            if (b != null)
            {
                var lineB = _trailRowLine[b.Id.Value];
                Vector2 near = lineA[0];
                float best = float.MaxValue;
                foreach (var p in lineA) foreach (var q in lineB) { float d = (p - q).sqrMagnitude; if (d < best) { best = d; near = (p + q) / 2; } }
                Camera.Frame(new Vector3(near.x, _resort.Surface.HeightAt(near.x, near.y), near.y), 750);
                Camera.SetAngles(200, 70);
                yield return Shot(dir, "1b-map-close");
            }

            var cam = Camera.GetComponent<UnityEngine.Camera>();
            var mid = lineA[lineA.Length / 2];
            var screen = cam.WorldToScreenPoint(new Vector3(mid.x, _trailLayer.Ground.HeightAt(mid.x, mid.y), mid.y));
            TrailPointerForCapture = new Vector2(screen.x, screen.y);
            for (int k = 0; k < 5; k++) yield return null;
            Debug.Log($"[FindingShots] hover over {a.Name}: {(_hoveredTrailRow == a.Id.Value ? "lit" : "missed " + _hoveredTrailRow)}");
            yield return Shot(dir, "2-hover-trail");
            Hud.OpenTrailCardFor(_hoveredTrailRow != 0 ? _hoveredTrailRow : a.Id.Value);
            TrailPointerForCapture = null;
            yield return Shot(dir, "3-trail-card");
            Hud.RenameTrailCardForCapture(true);
            yield return Shot(dir, "4-card-rename");
            Hud.RenameTrailCardForCapture(false);
            Hud.CloseTrailCardForCapture();

            _toolScripted = true;
            ChooseTrailRow(EditTrailTool.ToolId, a.Id.Value);
            var tool = _tools.Active as TrailTool;
            for (int f = 0; f < 3000 && tool != null && (tool.PickedTrail == null || tool.SectionsBusy); f++) { _tools.Tick(default); yield return null; }
            for (int k = 0; k < 10; k++) { _tools.Tick(default); yield return null; }
            yield return Shot(dir, "5-edit-pick");
            tool?.RenameTitleForCapture(true);
            yield return Shot(dir, "6-title-rename");
            tool?.RenameTitleForCapture(false);
            _toolScripted = false;
            _tools.Close();

            // A built graded trail: its box ticked and locked (NT47).
            ResolvedTrail graded = null;
            foreach (var t in _sections.Trails)
            {
                if (!t.Built || !_trailRowLine.ContainsKey(t.Id.Value)) continue;
                var piece = Design.Document.FindTrail(t.Segments[0].Piece);
                if (piece?.Settings.Grading == TrailGrading.Graded) { graded = t; break; }
            }
            if (graded != null)
            {
                FrameSections(_trailRowLine[graded.Id.Value], null);
                _toolScripted = true;
                ChooseTrailRow(EditTrailTool.ToolId, graded.Id.Value);
                tool = _tools.Active as TrailTool;
                for (int f = 0; f < 3000 && tool != null && (tool.PickedTrail == null || tool.SectionsBusy); f++) { _tools.Tick(default); yield return null; }
                tool?.ChooseGraded(false);   // NT47: does nothing
                for (int k = 0; k < 10; k++) { _tools.Tick(default); yield return null; }
                Debug.Log($"[FindingShots] {graded.Name} graded and built; unticking left it {(tool != null && tool.Graded ? "graded" : "NATURAL")}");
                yield return Shot(dir, "7-graded-locked");
                _toolScripted = false;
                _tools.Close();
            }
            else Debug.Log("[FindingShots] no built graded trail");
            Hud.SetToolbox(false);
            Debug.Log("[FindingShots] finished");
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
