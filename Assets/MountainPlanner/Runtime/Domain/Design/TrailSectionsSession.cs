using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Edit sections as a session (owner NT25, NT39): each click changes a working copy of the design at once and can be
    /// undone inside; Done is one <see cref="ShapeTrails"/> from where it started, so the whole session is one step for
    /// Ctrl+Z after. The trail being edited is followed by its top segment (its run and start). Engine-free: the tool
    /// works out the trails after each click (off the main thread, from the graphs) and hands them back with
    /// <see cref="Accept"/>.
    /// </summary>
    public sealed class TrailSectionsSession
    {
        readonly List<(DesignDocument Doc, TrailSections Sections, TrailId Piece, int StartMm)> _undo = new List<(DesignDocument, TrailSections, TrailId, int)>();
        readonly List<(DesignDocument Doc, TrailSections Sections, TrailId Piece, int StartMm)> _redo = new List<(DesignDocument, TrailSections, TrailId, int)>();
        TrailId _piece;
        int _startMm;

        public DesignDocument Base { get; }
        public TrailSections BaseSections { get; }
        public DesignDocument Working { get; private set; }
        public TrailSections Sections { get; private set; }

        public TrailSectionsSession(DesignDocument doc, TrailSections sections, ResolvedTrail trail)
        {
            Base = Working = doc ?? throw new ArgumentNullException(nameof(doc));
            BaseSections = Sections = sections ?? throw new ArgumentNullException(nameof(sections));
            if (trail == null) throw new ArgumentNullException(nameof(trail));
            _piece = trail.Segments[0].Piece;
            _startMm = trail.Segments[0].StartMm;
        }

        /// <summary>The trail being edited, as it stands after the clicks so far (null only if its top is gone).</summary>
        public ResolvedTrail Trail => TrailAt(Sections, _piece, _startMm);

        /// <summary>The same trail before the session (its "Was").</summary>
        public ResolvedTrail Before => TrailAt(BaseSections, FirstPiece, FirstStartMm);

        TrailId FirstPiece => _undo.Count > 0 ? _undo[0].Piece : _piece;
        int FirstStartMm => _undo.Count > 0 ? _undo[0].StartMm : _startMm;

        public bool Changed => _undo.Count > 0;
        public int Clicks => _undo.Count;
        public bool CanRedo => _redo.Count > 0;

        static ResolvedTrail TrailAt(TrailSections ts, TrailId piece, int startMm)
        {
            foreach (var s in ts.SegmentsOf(piece)) if (s.StartMm <= startMm && startMm < s.EndMm) return ts.TrailOf(s);
            return ts.TrailOf(piece);
        }

        /// <summary>A left-click on <paramref name="target"/> (NT35): what it adds, or null when nothing reaches it.</summary>
        public ShapeTrails Add(Segment target, out Segment newTop)
        {
            newTop = null;
            var trail = Trail;
            var route = trail == null ? null : TrailSectionsEdit.Route(Sections, trail, target);
            if (route == null) return null;
            newTop = route[0].To == trail.Segments[0].From ? route[route.Count - 1] : trail.Segments[0];   // nearest first: up ends at the target
            return TrailSectionsEdit.Add(Working, 0, Sections, trail, route);
        }

        /// <summary>A right-click on one of its segments (NT35): it becomes a trail of its own; null for its only one.</summary>
        public ShapeTrails TakeOut(Segment s, out Segment newTop)
        {
            newTop = null;
            var trail = Trail;
            if (trail == null) return null;
            var cmd = TrailSectionsEdit.TakeOut(Working, 0, Sections, trail, s);
            if (cmd == null) return null;
            newTop = ReferenceEquals(trail.Segments[0], s) ? trail.Segments[1] : trail.Segments[0];
            return cmd;
        }

        /// <summary>Tries a click's command on the working copy; the document it makes, or null with the reason.</summary>
        public DesignDocument Try(ShapeTrails cmd, out string problem)
        {
            problem = null;
            if (cmd == null) { problem = "Nothing to do there"; return null; }
            var r = cmd.Apply(Working);
            if (!r.Accepted) { problem = r.Reason; return null; }
            return r.Document;
        }

        /// <summary>
        /// Keeps a click: the document it made and the trails worked out on it, with the edited trail's top segment
        /// (<paramref name="top"/>, from the trails before the click; null keeps the top as it was).
        /// </summary>
        public void Accept(DesignDocument doc, TrailSections sections, Segment top)
        {
            _undo.Add((Working, Sections, _piece, _startMm));
            _redo.Clear();
            Working = doc;
            Sections = sections;
            if (top != null) { _piece = top.Piece; _startMm = top.StartMm; }
        }

        /// <summary>Ctrl+Z inside the session: the last click put back. False when there's none.</summary>
        public bool Undo()
        {
            if (_undo.Count == 0) return false;
            _redo.Add((Working, Sections, _piece, _startMm));
            Restore(_undo);
            return true;
        }

        /// <summary>Ctrl+Y inside the session.</summary>
        public bool Redo()
        {
            if (_redo.Count == 0) return false;
            _undo.Add((Working, Sections, _piece, _startMm));
            Restore(_redo);
            return true;
        }

        void Restore(List<(DesignDocument Doc, TrailSections Sections, TrailId Piece, int StartMm)> from)
        {
            var s = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            Working = s.Doc;
            Sections = s.Sections;
            _piece = s.Piece;
            _startMm = s.StartMm;
        }

        /// <summary>
        /// Done (NT39): every trail and cut point the session changed, as one command on <paramref name="revision"/>; null
        /// when the design is as it started.
        /// </summary>
        public ShapeTrails Done(long revision)
        {
            var put = new List<TrailLine>();
            var remove = new List<TrailLineId>();
            foreach (var line in Working.Lines)
            {
                var was = Base.FindLine(line.Id);
                if (was == null || !Same(was, line)) put.Add(line);
            }
            foreach (var line in Base.Lines) if (Working.FindLine(line.Id) == null) remove.Add(line.Id);
            var addCuts = new List<TrailCut>();
            var removeCuts = new List<TrailCut>();
            var baseCuts = new HashSet<TrailCut>(Base.Cuts);
            var nowCuts = new HashSet<TrailCut>(Working.Cuts);
            foreach (var c in Working.Cuts) if (!baseCuts.Contains(c)) addCuts.Add(c);
            foreach (var c in Base.Cuts) if (!nowCuts.Contains(c)) removeCuts.Add(c);
            if (put.Count + remove.Count + addCuts.Count + removeCuts.Count == 0) return null;
            return new ShapeTrails(revision, put, remove, addCuts, removeCuts);
        }

        static bool Same(TrailLine a, TrailLine b)
        {
            if (a.Name != b.Name || a.Spans.Count != b.Spans.Count) return false;
            for (int i = 0; i < a.Spans.Count; i++) if (!a.Spans[i].Equals(b.Spans[i])) return false;
            return true;
        }
    }
}
