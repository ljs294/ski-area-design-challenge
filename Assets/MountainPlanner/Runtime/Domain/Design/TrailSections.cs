using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>A segment (owner NT43): the stretch of one run between two nodes, mm from the run's head.</summary>
    public sealed class Segment
    {
        public TrailId Piece { get; }
        public int StartMm { get; }
        public int EndMm { get; }
        /// <summary>The network nodes at its top and bottom (NT42-merged, so a trail runs on where they match).</summary>
        public ulong From { get; }
        public ulong To { get; }
        public bool Built { get; }

        internal Segment(TrailId piece, int startMm, int endMm, ulong from, ulong to, bool built)
        {
            Piece = piece;
            StartMm = startMm;
            EndMm = endMm;
            From = from;
            To = to;
            Built = built;
        }

        public override string ToString() => $"{Piece}@{StartMm}-{EndMm}";
    }

    /// <summary>A trail as the player sees it: its name and segments, top first; a shaped one, or a run's own.</summary>
    public sealed class ResolvedTrail
    {
        public TrailLineId Id { get; }
        public string Name { get; }
        public IReadOnlyList<Segment> Segments { get; }
        /// <summary>The saved trail it comes from (every piece of one broken in two, NT28), or null for a run's own trail.</summary>
        public TrailLine Line { get; }
        /// <summary>Whether it is the saved trail itself (its first piece), not a run's own or a broken-off piece.</summary>
        public bool Saved => Line != null && Line.Id == Id;
        public bool Built => Segments[0].Built;

        internal ResolvedTrail(TrailLineId id, string name, IReadOnlyList<Segment> segments, TrailLine line)
        {
            Id = id;
            Name = name;
            Segments = segments;
            Line = line;
        }

        public bool Holds(Segment s)
        {
            foreach (var x in Segments) if (ReferenceEquals(x, s)) return true;
            return false;
        }
    }

    /// <summary>
    /// The trails as the player sees them (owner NT24–NT46; the game's TrailSections.Of), from the document and the two
    /// graphs (built and planned, NT44). Every run is cut into segments at its nodes; a shaped trail's spans find their
    /// ends by anchor (or, gone or moved far, by where they were, NT46) and hold every segment whose top they cover; a
    /// segment no shaped trail holds belongs to its run's own trail. A trail that no longer runs end to end shows as Upper
    /// and Lower (NT28). Derived, never saved; deterministic (trails in ID order).
    /// </summary>
    public sealed class TrailSections
    {
        /// <summary>How far an anchor may be from where it was saved and still be trusted (NT46): 100 m.</summary>
        public const int AnchorDriftMm = 100_000;

        public IReadOnlyList<ResolvedTrail> Trails => _trails;
        public IReadOnlyList<Segment> Segments => _segments;

        readonly List<ResolvedTrail> _trails = new List<ResolvedTrail>();
        readonly List<Segment> _segments = new List<Segment>();
        readonly Dictionary<ulong, List<Segment>> _byPiece = new Dictionary<ulong, List<Segment>>();
        readonly Dictionary<ulong, IReadOnlyList<PieceStop>> _stops = new Dictionary<ulong, IReadOnlyList<PieceStop>>();
        readonly Dictionary<Segment, ResolvedTrail> _trailOf = new Dictionary<Segment, ResolvedTrail>();

        public IReadOnlyList<Segment> SegmentsOf(TrailId piece) => _byPiece.TryGetValue(piece.Value, out var l) ? l : (IReadOnlyList<Segment>)Array.Empty<Segment>();
        public ResolvedTrail TrailOf(Segment s) => s != null && _trailOf.TryGetValue(s, out var t) ? t : null;
        /// <summary>The trail holding a run's first segment (its own trail, unless shaped).</summary>
        public ResolvedTrail TrailOf(TrailId piece) { var l = SegmentsOf(piece); return l.Count > 0 ? TrailOf(l[0]) : null; }

        public ResolvedTrail Find(TrailLineId id)
        {
            foreach (var t in _trails) if (t.Id == id) return t;
            return null;
        }

        /// <summary>The segment of a run that holds the point <paramref name="alongMm"/> from its head.</summary>
        public Segment SegmentAt(TrailId piece, int alongMm)
        {
            foreach (var s in SegmentsOf(piece)) if (alongMm >= s.StartMm && alongMm <= s.EndMm) return s;
            return null;
        }

        /// <summary>The lasting name of the node at <paramref name="alongMm"/> on a run (NT43): its best anchor there.</summary>
        public Anchor AnchorAt(TrailId piece, int alongMm)
        {
            Anchor best = default;
            if (_stops.TryGetValue(piece.Value, out var stops))
                foreach (var st in stops)
                    if (st.AlongMm == alongMm && st.Anchor.Kind != 0 && (best.Kind == 0 || st.Anchor.Rank < best.Rank)) best = st.Anchor;
            return best;
        }

        /// <summary>The trails of <paramref name="doc"/>, with both stages' graphs built on <paramref name="geometry"/> (NT44).</summary>
        public static TrailSections Build(DesignDocument doc, ITrailGeometry geometry) =>
            Of(doc, DesignNetwork.Build(doc, geometry, WorkStage.Built), DesignNetwork.Build(doc, geometry, WorkStage.Plan));

        public static TrailSections Of(DesignDocument doc, NetworkGraph built, NetworkGraph plan)
        {
            var ts = new TrailSections();
            foreach (var t in doc.Trails)   // ID order
            {
                var g = t.Stage == WorkStage.Built ? built : plan;
                if (g == null) continue;
                var stops = g.StopsOf(t.Id);
                if (stops.Count < 2) continue;
                ts._stops[t.Id.Value] = stops;
                var list = new List<Segment>();
                for (int i = 1; i < stops.Count; i++)
                {
                    if (stops[i].Node == stops[i - 1].Node) continue;
                    var s = new Segment(t.Id, stops[i - 1].AlongMm, stops[i].AlongMm, stops[i - 1].Node, stops[i].Node, t.Stage == WorkStage.Built);
                    list.Add(s);
                    ts._segments.Add(s);
                }
                if (list.Count > 0) ts._byPiece[t.Id.Value] = list;
            }

            // Each shaped trail's spans as stretches along their runs; a segment goes to the first trail whose span holds
            // its top (so a node that's gone leaves its stretch with the trail above, NT46).
            var owner = new Dictionary<Segment, int>();
            var ranges = new List<List<(TrailId piece, int a0, int a1)>>();
            for (int li = 0; li < doc.Lines.Count; li++)
            {
                var r = new List<(TrailId, int, int)>();
                foreach (var sp in doc.Lines[li].Spans)
                {
                    var stops = ts._stops.TryGetValue(sp.Piece.Value, out var x) ? x : null;
                    if (stops == null) continue;
                    int length = stops[stops.Count - 1].AlongMm;
                    int a0 = Resolve(stops, sp.From, sp.FromMm, 0), a1 = Resolve(stops, sp.To, sp.ToMm, length);
                    if (a1 > a0) r.Add((sp.Piece, a0, a1));
                }
                ranges.Add(r);
                foreach (var (piece, a0, a1) in r)
                    foreach (var s in ts.SegmentsOf(piece))
                        if (!owner.ContainsKey(s) && s.StartMm >= a0 && s.StartMm < a1) owner[s] = li;
            }
            var used = new HashSet<ulong>();
            foreach (var l in doc.Lines) used.Add(l.Id.Value);
            for (int li = 0; li < doc.Lines.Count; li++)
            {
                var line = doc.Lines[li];
                var mine = new List<Segment>();
                foreach (var (piece, a0, a1) in ranges[li])
                    foreach (var s in ts.SegmentsOf(piece))
                        if (owner.TryGetValue(s, out int o) && o == li && s.StartMm >= a0 && s.StartMm < a1 && !mine.Contains(s)) mine.Add(s);
                ts.AddPieces(doc, line.Id, line.Name, mine, line, used);
            }
            // Every run's leftovers: its own trail (Upper and Lower where a shaped trail took its middle).
            foreach (var t in doc.Trails)
            {
                var free = new List<Segment>();
                foreach (var s in ts.SegmentsOf(t.Id)) if (!owner.ContainsKey(s)) free.Add(s);
                if (free.Count == 0) continue;
                ts.AddPieces(doc, OwnId(doc, t.Id, used), t.Name, free, null, used);
            }
            ts._trails.Sort((a, b) => a.Id.CompareTo(b.Id));
            return ts;
        }

        // A run's own trail's ID: keyed by the run, the next one if a shaped trail founded on it still holds that key.
        static TrailLineId OwnId(DesignDocument doc, TrailId piece, HashSet<ulong> used)
        {
            for (ulong k = 0; ; k++)
            {
                var id = TrailLineId.Of(doc.Seed, piece, k);
                if (!used.Contains(id.Value)) { used.Add(id.Value); return id; }
            }
        }

        // NT28: a trail's segments in pieces that run end to end; the first keeps the ID and, when it's alone, the name.
        void AddPieces(DesignDocument doc, TrailLineId id, string name, List<Segment> segs, TrailLine line, HashSet<ulong> used)
        {
            if (segs.Count == 0) return;
            var pieces = new List<List<Segment>>();
            foreach (var s in segs)
            {
                if (pieces.Count == 0) { pieces.Add(new List<Segment> { s }); continue; }
                var last = pieces[pieces.Count - 1];
                var prev = last[last.Count - 1];
                if (prev.To == s.From && prev.Built == s.Built) last.Add(s);
                else pieces.Add(new List<Segment> { s });
            }
            var names = pieces.Count > 1 ? TrailLines.BreakNames(name, pieces.Count) : new[] { name };
            for (int i = 0; i < pieces.Count; i++)
            {
                var pid = i == 0 ? id : OwnId(doc, pieces[i][0].Piece, used);
                var trail = new ResolvedTrail(pid, names[i], pieces[i], line);
                _trails.Add(trail);
                foreach (var s in pieces[i]) _trailOf[s] = trail;
            }
        }

        // Where an anchor is now along its run: the stop it names nearest where it was saved; if none, or one more than
        // AnchorDriftMm away, where it was saved (NT46). A saved place of 0 means "no hint" (a v6 save's whole runs).
        static int Resolve(IReadOnlyList<PieceStop> stops, Anchor a, int hint, int fallback)
        {
            int length = stops[stops.Count - 1].AlongMm, best = -1;
            long bestD = long.MaxValue;
            foreach (var st in stops)
            {
                bool match = a.Kind == AnchorKind.Cut
                    ? st.Anchor.Kind == AnchorKind.Cut && st.Anchor.At.Within(a.At, 1000)
                    : st.Anchor.Equals(a);
                if (a.Kind == AnchorKind.Head && st.AlongMm == 0) match = true;
                if (a.Kind == AnchorKind.Tail && st.AlongMm == length) match = true;
                if (!match) continue;
                long d = Math.Abs((long)st.AlongMm - hint);
                if (d < bestD) { bestD = d; best = st.AlongMm; }
            }
            bool hinted = hint > 0 && a.Kind != AnchorKind.Head && a.Kind != AnchorKind.Tail;
            if (best >= 0 && (!hinted || bestD <= AnchorDriftMm)) return best;
            return hinted ? Math.Min(hint, length) : fallback;
        }
    }
}
