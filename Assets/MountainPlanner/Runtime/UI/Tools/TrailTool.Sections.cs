using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Edit trail's trails made of sections (task P3-10 B2; owner NT24–NT46, the mockup's Edit sections). A click picks the
    /// trail holding the segment under it, not only the painted run; <b>Edit sections</b> opens a session on it:
    /// <b>Choose sections</b> (a left-click on any segment beyond its top or bottom adds everything on the way, turning at
    /// junctions; a right-click takes one out) and <b>Cut</b> (a node at the pointer, on any bend; a click on a cut point
    /// rejoins it). Each click changes a working copy and Ctrl+Z inside puts it back; Done is one step, Esc asks first.
    /// The trails are worked out off the main thread from both stages' graphs (<see cref="TrailSections.Build"/>).
    /// </summary>
    public partial class TrailTool
    {
        /// <summary>The segments' centrelines in local metres, worked out with the trails.</summary>
        sealed class Resolved
        {
            public DesignDocument Doc;
            public TrailSections Sections;
            public Dictionary<Segment, Vector2[]> Lines;
        }

        TrailNetworkGeometry _geo;
        (double, double, double, double) _geoSite = (double.NaN, 0, 0, 0);
        Resolved _resolved;
        Task<Resolved> _job;
        DesignDocument _jobDoc;
        Segment _jobTop;
        bool _jobForSession;
        PointMm? _pickedAt;
        ResolvedTrail _trail;

        TrailSectionsSession _session;
        bool _cutMode, _armEsc;
        Action<int> _setSecMode;
        Segment _hoverSeg;
        int _hoverKind = -1;   // what the hover would do, so the hint and lines change only when it does
        TrailCut? _hoverCut;
        Vector2 _hoverAt;

        // Preview slots for the session: the trail's segments and nodes from 10, the hover from 150.
        const int SecSlots = 10, HoverSlots = 150;

        /// <summary>The trail Edit trail has picked (the trail holding the clicked segment), or null.</summary>
        public ResolvedTrail PickedTrail => _trail;
        /// <summary>The open Edit sections session, or null (tests and captures).</summary>
        public TrailSectionsSession Session => _session;
        /// <summary>True while the trails are being worked out (captures wait).</summary>
        public bool SectionsBusy => _job != null;

        void AddSectionsControls(ToolWindow w)
        {
            var b = w.AddButtons("network", new[] { "Edit sections" }, _ => OpenSession(),
                new[] { "NT25: choose its sections in network mode; a section ends wherever another run meets or crosses it, at a lift, or where you cut it" });
            _sectionsButton = b[0];
            _setSecMode = w.AddSwitch("sections", "Click", new[] { "Choose sections", "Cut" }, 0, SetCutMode,
                "NT35: left-click adds, right-click takes out · NT45: Cut adds a node on any bend, a click on a cut point rejoins it");
            w.ShowOption("sections", false);
        }

        UnityEngine.UIElements.Button _sectionsButton;

        // ---------- the trails, worked out off the main thread ----------

        TrailNetworkGeometry Geometry()
        {
            var host = _ctx.Earthwork;
            if (host?.Earthwork == null || !host.Ready) return null;
            var site = host.Earthwork.Site;
            var key = (site.Lattice.West, site.Lattice.North, site.OriginX, site.OriginY);
            if (_geo == null || !key.Equals(_geoSite))
            {
                var earthwork = host.Earthwork;
                _geoSite = key;
                _geo = new TrailNetworkGeometry(site, host.Heights?.Source, null, t => earthwork.DetailOf(t.Id.Value) as TrailBuild);
            }
            return _geo;
        }

        void StartResolve(DesignDocument doc, bool forSession, Segment top)
        {
            var geo = Geometry();
            if (geo == null) return;
            var site = Site;
            _jobDoc = doc;
            _jobForSession = forSession;
            _jobTop = top;
            _job = Task.Run(() => Resolve(doc, geo, site));
        }

        static Resolved Resolve(DesignDocument doc, TrailNetworkGeometry geo, EarthworkSite site)
        {
            var ts = TrailSections.Build(doc, geo);
            var lines = new Dictionary<Segment, Vector2[]>();
            foreach (var s in ts.Segments)
            {
                var t = doc.FindTrail(s.Piece);
                if (t != null) lines[s] = Stretch(geo.CentrelineOf(t), s.StartMm / 1000.0, s.EndMm / 1000.0, site.OriginX, site.OriginY);
            }
            return new Resolved { Doc = doc, Sections = ts, Lines = lines };
        }

        /// <summary>The centreline (lattice x, y pairs) between two distances along it, every 5 m or closer, in local metres.</summary>
        static Vector2[] Stretch(IReadOnlyList<double> line, double from, double to, double ox, double oy)
        {
            var pts = new List<Vector2>();
            double run = 0;
            for (int i = 0; i + 3 < line.Count; i += 2)
            {
                double ax = line[i], ay = line[i + 1], bx = line[i + 2], by = line[i + 3];
                double len = System.Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                double s0 = run, s1 = run + len;
                run = s1;
                if (len <= 0 || s1 < from || s0 > to) continue;
                double f0 = System.Math.Max(0, (from - s0) / len), f1 = System.Math.Min(1, (to - s0) / len);
                int steps = System.Math.Max(1, (int)System.Math.Ceiling((f1 - f0) * len / 5));
                for (int k = pts.Count == 0 ? 0 : 1; k <= steps; k++)
                {
                    double f = f0 + (f1 - f0) * k / steps;
                    pts.Add(new Vector2((float)(ax + (bx - ax) * f - ox), (float)(ay + (by - ay) * f - oy)));
                }
            }
            return pts.ToArray();
        }

        // Each frame in Edit's Pick: keep the trails current, and take a finished job.
        void TickSections()
        {
            if (_job != null)
            {
                if (!_job.IsCompleted) return;
                var job = _job;
                _job = null;
                if (job.IsFaulted) { Debug.LogException(job.Exception); return; }
                var r = job.Result;
                if (_jobForSession)
                {
                    if (_session == null || r.Doc != _jobDoc) return;
                    _session.Accept(r.Doc, r.Sections, _jobTop);
                    _sessionLines.Add(_resolved);
                    _resolved = r;
                    _hoverKind = -1;
                    DrawSession();
                    SessionStatus();
                    return;
                }
                if (r.Doc == _ctx.History.Document)
                {
                    _resolved = r;
                    TrailFromPick();
                    if (_step == Step.Pick || _mode == TrailToolMode.Remove) DrawLit(PickedLit(), true);   // a hover finds its own next frame
                }
            }
            if (_session == null && (_target != null || _step == Step.Pick) && (_resolved == null || _resolved.Doc != _ctx.History.Document))
                StartResolve(_ctx.History.Document, false, null);
        }

        // ---------- what a pick lights (finding 1) ----------

        IReadOnlyList<Segment> _litSegs;
        int _litCount;

        // The trail holding the run's segment nearest the pointer (Edit), or the run itself (Remove removes the run).
        IReadOnlyList<Segment> LitAt(Trail run, Vector2 at)
        {
            if (_resolved == null || _resolved.Doc != _ctx.History.Document) return null;
            var ts = _resolved.Sections;
            var own = ts.SegmentsOf(run.Id);
            if (_mode == TrailToolMode.Remove || own.Count == 0) return own.Count > 0 ? own : null;
            var s = own.Count == 1 ? own[0] : Nearest(own, at, 1000, out _, out _) ?? own[0];
            return ts.TrailOf(s)?.Segments;
        }

        IReadOnlyList<Segment> PickedLit()
        {
            if (_target == null || _resolved == null) return null;
            if (_mode == TrailToolMode.Remove) { var own = _resolved.Sections.SegmentsOf(_target.Id); return own.Count > 0 ? own : null; }
            return _trail?.Segments;
        }

        // The segments lit along their centrelines, as Edit sections lights its trail; nothing of the run's paint.
        void DrawLit(IReadOnlyList<Segment> segs, bool force = false)
        {
            if (!force && ReferenceEquals(segs, _litSegs)) return;
            var p = _ctx.Preview;
            int slot = StrokeSlots;
            if (segs != null && _resolved?.Lines != null)
                foreach (var s in segs)
                {
                    if (slot >= RedSlots) break;
                    if (!_resolved.Lines.TryGetValue(s, out var l) || l.Length < 2) continue;
                    p.Line(slot, l, l.Length, PreviewInk.Snap);
                    Wide(slot++, 2f);
                }
            for (int k = slot; k < StrokeSlots + _litCount; k++) { p.Hide(k); Wide(k, 1); }
            _litCount = slot - StrokeSlots;
            _litSegs = segs;
        }

        void ClearLit()
        {
            if (_litCount == 0) { _litSegs = null; return; }
            DrawLit(null, true);
        }

        // The picked trail: the one holding the clicked segment (or the run's top, without a click point).
        void TrailFromPick()
        {
            if (_target == null || _resolved == null) { _trail = null; SetSectionsButton(); return; }
            var ts = _resolved.Sections;
            Segment s = null;
            if (_pickedAt.HasValue && _geo != null) s = ts.SegmentAt(_target.Id, _geo.AlongOf(_target, _pickedAt.Value));
            _trail = s != null ? ts.TrailOf(s) : ts.TrailOf(_target.Id);
            if (_trail != null && _mode == TrailToolMode.Edit && _step == Step.Pick)
            {
                _ctx.Window.SetName(_trail.Name);
                _nameField?.SetValueWithoutNotify(_trail.Name);
                _name = _trail.Name;
                PickStatus();
            }
            SetSectionsButton();
        }

        void SetSectionsButton() => _sectionsButton?.SetEnabled(_target != null && _trail != null && _session == null && _step == Step.Pick);

        /// <summary>The picked trail's line for the hint: its sections and, when it's more than this run, what it holds.</summary>
        string TrailFacts()
        {
            if (_trail == null) return _resolved == null || _job != null ? " Working out its sections…" : "";
            int n = _trail.Segments.Count;
            double metres = 0;
            foreach (var s in _trail.Segments) metres += (s.EndMm - s.StartMm) / 1000.0;
            string more = _trail.Segments[0].Piece != _target.Id || _trail.Segments[n - 1].Piece != _target.Id
                ? $" Repaint and Move ends change {_target.Name} only." : "";
            string who = _trail.Name == _target.Name ? "" : $"In {_trail.Name}: ";
            return $" {who}{n} section{(n > 1 ? "s" : "")}, {ToolFormat.Length(metres)} along its runs.{more}";
        }

        /// <summary>Rename for a shaped trail (one holding more than its run, or renamed before): the trail, one step.</summary>
        bool RenameTrail()
        {
            if (_trail?.Line == null || _resolved == null) return false;   // a run's own trail: EditTrail renames the run
            if (_name == _trail.Name) return true;
            var cmd = TrailSectionsEdit.Rename(_ctx.History.Document, _ctx.History.Revision, _resolved.Sections, _trail, _name);
            var r = _ctx.Submit?.Invoke(cmd, null);
            if (r != null && r.Accepted) _ctx.Notice?.Invoke(_name + ": renamed");
            else _ctx.Window.SetMessage(r?.Reason ?? "Can't rename it now", true);
            return true;
        }

        // ---------- the session ----------

        void OpenSession()
        {
            if (_trail == null || _resolved == null || _session != null || _resolved.Doc != _ctx.History.Document) return;
            _session = new TrailSectionsSession(_resolved.Doc, _resolved.Sections, _trail);
            _sessionLines.Clear();
            _cutMode = _armEsc = false;
            _setSecMode?.Invoke(0);
            var w = _ctx.Window;
            foreach (var row in new[] { "name", "brush", "grading", "edit", "network" }) w.ShowOption(row, false);
            w.ShowOption("sections", true);
            w.SetButtons(("secdone", "Done", (System.Action)SessionDone), ("seccancel", "Cancel", (System.Action)(() => CloseSession(false))));
            w.SetKeys((GameAction.ToolNext, "done"), (GameAction.UndoStroke, "undoes a click"));
            _ctx.Claim(GameAction.UndoStroke, true);
            ViewCamera.RightButtonClaimed = true;   // right-click takes a section out
            _ctx.Paint?.Clear();
            _ctx.Preview.HideFrom(StrokeSlots);
            _litSegs = null;
            _litCount = 0;   // the session's lines take these slots
            _strip?.ShowWaiting("Edit sections: the trail's profile shows again when you're done.");
            _hoverKind = -1;
            DrawSession();
            SessionStatus();
        }

        void CloseSession(bool keep)
        {
            if (_session == null) return;
            _session = null;
            _job = null;
            _armEsc = false;
            ViewCamera.RightButtonClaimed = false;
            _ctx.Claim(GameAction.UndoStroke, false);
            var w = _ctx.Window;
            foreach (var row in new[] { "name", "brush", "grading", "edit", "network" }) w.ShowOption(row, true);
            w.ShowOption("sections", false);
            w.SetButtons();
            w.SetKeys();
            w.SetMessage(null);
            _ctx.Preview.HideFrom(SecSlots);
            for (int k = SecSlots; k < HoverSlots + 40; k++) Wide(k, 1);   // the slots go back to other steps' widths
            if (!keep) _resolved = null;   // worked out again for the design as it is
            if (_target != null && _ctx.History.Document.FindTrail(_target.Id) is Trail t) Select(t);
            else { _target = null; _trail = null; PickStatus(); }
        }

        void SetCutMode(int i)
        {
            _cutMode = i == 1;
            _setSecMode?.Invoke(i);
            _hoverKind = -1;
            if (_session != null) { DrawSession(); SessionStatus(); }
        }

        void SessionDone()
        {
            if (_session == null || _job != null) return;
            var cmd = _session.Done(_ctx.History.Revision);
            if (cmd == null) { CloseSession(true); return; }
            var trail = _session.Trail;
            var r = _ctx.Submit?.Invoke(cmd, null);
            if (r == null || !r.Accepted) { _ctx.Window.SetMessage(r?.Reason ?? "The design changed: Esc and try again", true); return; }
            int n = trail?.Segments.Count ?? 0;
            _ctx.Notice?.Invoke($"{trail?.Name}: {n} section{(n == 1 ? "" : "s")}");
            // The design now is the session's working copy: its trails stand.
            _resolved = new Resolved { Doc = _ctx.History.Document, Sections = _session.Sections, Lines = _resolved?.Lines };
            if (_resolved.Lines == null) _resolved = null;
            var top = trail?.Segments[0];
            CloseSession(true);
            if (_resolved != null && top != null) { _trail = _resolved.Sections.TrailOf(top); TrailFromPickKeep(); }
        }

        void TrailFromPickKeep()
        {
            if (_trail == null) return;
            _ctx.Window.SetName(_trail.Name);
            _nameField?.SetValueWithoutNotify(_trail.Name);
            _name = _trail.Name;
            PickStatus();
            SetSectionsButton();
        }

        ToolBackOut SessionEscape()
        {
            if (_session.Changed && !_armEsc)
            {
                _armEsc = true;
                _ctx.Window.SetMessage("Discard the changes? Esc again", true);
                return ToolBackOut.Stepped;
            }
            CloseSession(false);
            return ToolBackOut.Stepped;
        }

        void TickSession(in ToolInput input)
        {
            if (_job != null) return;   // a click is being worked out
            if (input.Pressed(ToolKeys.UndoStroke)) { if (_session.Undo()) Undone(); return; }
            if (input.Pressed(ToolKeys.Next) || input.Pressed(ToolKeys.BreakGround)) { SessionDone(); return; }
            if (!input.HasGround || input.OverPanel) { Hover(-1, null, null, default); return; }
            float reach = Mathf.Max(5f, input.MetresPerPixel * 14);
            var at = DesignFrame.ToLocal(input.Point);
            var trail = _session.Trail;
            if (trail == null) return;
            if (_cutMode)
            {
                // A cut point of this trail's runs near the pointer: a click rejoins it.
                foreach (var c in _session.Working.Cuts)
                {
                    if (!Holds(trail, c.Piece) || (DesignFrame.ToLocal(c.At) - at).magnitude > reach) continue;
                    Hover(3, null, c, DesignFrame.ToLocal(c.At));
                    if (input.Click) Apply(TrailSectionsEdit.Rejoin(0, c), null);
                    return;
                }
                var seg = Nearest(trail.Segments, at, reach, out var q, out float along);
                if (seg == null) { Hover(-1, null, null, default); return; }
                float len = (seg.EndMm - seg.StartMm) / 1000f;
                bool bad = along < MinCutMetres || len - along < MinCutMetres;
                Hover(bad ? 5 : 4, seg, null, q);
                if (input.Click && !bad) Apply(TrailSectionsEdit.Cut(0, seg.Piece, DesignFrame.ToDesign(q)), null);
                return;
            }
            var under = Nearest(_session.Sections.Segments, at, reach, out var p, out _);
            if (under == null) { Hover(-1, null, null, default); return; }
            if (trail.Holds(under))
            {
                Hover(trail.Segments.Count < 2 ? 2 : 1, under, null, p);
                if (input.RightClick && trail.Segments.Count >= 2) { var cmd = _session.TakeOut(under, out var top); Apply(cmd, top); }
                return;
            }
            var route = TrailSectionsEdit.Route(_session.Sections, trail, under);
            Hover(route == null ? 6 : 0, under, null, p);
            if (input.Click && route != null) { var cmd = _session.Add(under, out var top); Apply(cmd, top); }
        }

        // The session's lines wider than a stroke's edge, so they read from a trail-map distance.
        void Wide(int slot, float scale) => (_ctx.Preview as PendingPreview)?.SetWidth(slot, scale, false);

        /// <summary>How close to a node a cut may go: 10 m (the mockup's "already a node here").</summary>
        const float MinCutMetres = 10;

        static bool Holds(ResolvedTrail t, TrailId piece)
        {
            foreach (var s in t.Segments) if (s.Piece == piece) return true;
            return false;
        }

        void Apply(ShapeTrails cmd, Segment top)
        {
            var doc = _session.Try(cmd, out string problem);
            if (doc == null) { _ctx.Window.SetMessage(problem, true); return; }
            _armEsc = false;
            _ctx.Window.SetMessage(null);
            StartResolve(doc, true, top);
            if (_job == null) { _ctx.Window.SetMessage("The ground isn't ready yet", true); return; }
            _ctx.Window.SetHint("Working out the sections…");
        }

        // Each kept click's lines before it, so Ctrl+Z inside puts them back with the session's trails.
        readonly List<Resolved> _sessionLines = new List<Resolved>();

        void Undone()
        {
            _armEsc = false;
            _ctx.Window.SetMessage(null);
            if (_sessionLines.Count > 0)
            {
                _resolved = _sessionLines[_sessionLines.Count - 1];
                _sessionLines.RemoveAt(_sessionLines.Count - 1);
            }
            _hoverKind = -1;
            DrawSession();
            SessionStatus();
        }

        // The segment nearest the pointer within reach, the nearest point on it and how far along it that is (metres).
        Segment Nearest(IReadOnlyList<Segment> segs, Vector2 at, float reach, out Vector2 q, out float along)
        {
            q = default;
            along = 0;
            if (_resolved?.Lines == null) return null;
            Segment best = null;
            float bestD = reach * reach;
            foreach (var s in segs)
            {
                if (!_resolved.Lines.TryGetValue(s, out var line) || line.Length < 2) continue;
                float run = 0;
                for (int k = 1; k < line.Length; k++)
                {
                    var a = line[k - 1];
                    var d = line[k] - a;
                    float l2 = d.sqrMagnitude;
                    float f = l2 > 0 ? Mathf.Clamp01(Vector2.Dot(at - a, d) / l2) : 0;
                    var c = a + d * f;
                    float dd = (c - at).sqrMagnitude;
                    if (dd < bestD) { bestD = dd; best = s; q = c; along = run + Mathf.Sqrt(l2) * f; }
                    run += Mathf.Sqrt(l2);
                }
            }
            return best;
        }

        void Hover(int kind, Segment seg, TrailCut? cut, Vector2 at)
        {
            if (kind == _hoverKind && ReferenceEquals(seg, _hoverSeg) && Nullable.Equals(cut, _hoverCut) && (kind < 4 || (at - _hoverAt).sqrMagnitude < 0.25f)) return;
            _hoverKind = kind;
            _hoverSeg = seg;
            _hoverCut = cut;
            _hoverAt = at;
            DrawHover();
            SessionStatus();
        }

        // The hover's lines: the route a left-click adds (ice, dashed by the preview), a section a right-click takes out
        // (red), the cut tick, a cut point's ring.
        void DrawHover()
        {
            var p = _ctx.Preview;
            p.HideFrom(HoverSlots);
            int slot = HoverSlots;
            var trail = _session?.Trail;
            if (trail == null || _hoverKind < 0) return;
            switch (_hoverKind)
            {
                case 0:
                    foreach (var s in TrailSectionsEdit.Route(_session.Sections, trail, _hoverSeg) ?? new List<Segment>())
                        if (_resolved.Lines.TryGetValue(s, out var l)) { p.Line(slot, l, l.Length, PreviewInk.Ok); Wide(slot++, 2.6f); }
                    break;
                case 1:
                    if (_resolved.Lines.TryGetValue(_hoverSeg, out var o)) { p.Line(slot, o, o.Length, PreviewInk.Bad); Wide(slot, 2.6f); }
                    break;
                case 3:
                    p.Ring(slot, _hoverAt, 6, PreviewInk.Snap);
                    break;
                case 4:
                case 5:
                    p.Ring(slot, _hoverAt, 4, _hoverKind == 5 ? PreviewInk.Bad : PreviewInk.Line);
                    break;
            }
        }

        // The trail lit (its segments, its nodes as dots, its cut points as rings).
        void DrawSession()
        {
            var p = _ctx.Preview;
            p.HideFrom(SecSlots);
            var trail = _session?.Trail;
            if (trail == null || _resolved?.Lines == null) return;
            int slot = SecSlots;
            foreach (var s in trail.Segments)
            {
                if (slot >= HoverSlots - 2) break;
                if (_resolved.Lines.TryGetValue(s, out var l) && l.Length > 1)
                {
                    p.Line(slot, l, l.Length, PreviewInk.Snap);
                    Wide(slot++, 2f);
                    p.Dot(slot++, l[l.Length - 1], PreviewInk.Line);
                    if (ReferenceEquals(s, trail.Segments[0])) p.Dot(slot++, l[0], PreviewInk.Line);
                }
            }
            foreach (var c in _session.Working.Cuts)
                if (Holds(trail, c.Piece) && slot < HoverSlots) p.Ring(slot++, DesignFrame.ToLocal(c.At), 3, PreviewInk.Snap);
            DrawHover();
        }

        void SessionStatus()
        {
            var w = _ctx.Window;
            var trail = _session?.Trail;
            if (trail == null) { w.SetHint("Working out the sections…"); return; }
            w.SetName("Edit sections · " + trail.Name);
            int n = trail.Segments.Count;
            double metres = 0;
            foreach (var s in trail.Segments) metres += (s.EndMm - s.StartMm) / 1000.0;
            string now = $"Now {n} section{(n > 1 ? "s" : "")}, {ToolFormat.Length(metres)}.";
            var before = _session.Changed ? _session.Before : null;
            if (before != null) now += $" Was {before.Segments.Count}.";
            string what = _hoverKind switch
            {
                0 => $"Click: add {Count(TrailSectionsEdit.Route(_session.Sections, trail, _hoverSeg))} to {trail.Name}.",
                1 => $"Right-click: take this section out; {(IsMiddle(trail, _hoverSeg) ? trail.Name + " becomes " + string.Join(" and ", TrailLines.BreakNames(trail.Name, 2)) : "it becomes a trail of its own")}.",
                2 => "Its only section: Remove trail takes the trail.",
                3 => "Click: rejoin. The two sections become one.",
                4 => $"Click: cut here. A new node; both sections stay in {trail.Name}.",
                5 => "Already a node here: cut further from it.",
                6 => $"It doesn't reach {trail.Name}'s top or bottom through the network.",
                _ => _cutMode ? "Click the trail to add a node, on any bend; click a cut point to rejoin it."
                              : "Left-click any section beyond its top or bottom: everything on the way is added, turning at junctions. Right-click a section to take it out.",
            };
            w.SetHint(what + " " + now);
        }

        static string Count(List<Segment> route) => route == null ? "nothing" : $"{route.Count} section{(route.Count > 1 ? "s" : "")}";

        static bool IsMiddle(ResolvedTrail t, Segment s)
        {
            int i = -1;
            for (int k = 0; k < t.Segments.Count; k++) if (ReferenceEquals(t.Segments[k], s)) i = k;
            return i > 0 && i < t.Segments.Count - 1;
        }

        // For tests and captures: the session's clicks by segment, as the pointer would make them.
        /// <summary>Opens Edit sections on the picked trail (the button).</summary>
        public void PressEditSections() => OpenSession();
        /// <summary>A left-click (add) or right-click (take out) on a segment.</summary>
        public bool ClickSection(Segment s, bool right)
        {
            if (_session == null || _job != null) return false;
            var cmd = right ? _session.TakeOut(s, out var top) : _session.Add(s, out top);
            if (cmd == null) return false;
            Apply(cmd, top);
            return _job != null;
        }
        /// <summary>A cut at a point (Cut mode).</summary>
        public bool CutAt(TrailId piece, PointMm at)
        {
            if (_session == null || _job != null) return false;
            Apply(TrailSectionsEdit.Cut(0, piece, at), null);
            return _job != null;
        }
        /// <summary>Shows a hover as the pointer would (captures hold it).</summary>
        public void HoverSection(Segment s, bool cut = false)
        {
            if (_session == null) return;
            if (cut != _cutMode) SetCutMode(cut ? 1 : 0);
            var trail = _session.Trail;
            Vector2 mid = default;
            if (_resolved?.Lines != null && _resolved.Lines.TryGetValue(s, out var l)) mid = l[l.Length / 2];
            int kind = cut ? 4 : trail.Holds(s) ? (trail.Segments.Count < 2 ? 2 : 1) : TrailSectionsEdit.Route(_session.Sections, trail, s) == null ? 6 : 0;
            Hover(kind, s, null, mid);
        }
        /// <summary>A segment's centreline in local metres, as the session draws it (captures frame on it).</summary>
        public bool TryLineOf(Segment s, out Vector2[] line)
        {
            line = null;
            return s != null && _resolved?.Lines != null && _resolved.Lines.TryGetValue(s, out line);
        }
        /// <summary>Done (Enter).</summary>
        public void FinishSections() => SessionDone();
    }
}
