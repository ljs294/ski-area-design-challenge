using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Edit sections' clicks (owner NT25, NT27, NT35, NT37, NT40, NT45) worked out on the trails as they stand
    /// (<see cref="TrailSections"/>), each as a <see cref="ShapeTrails"/> the tool can try (for the hover) or submit.
    /// Engine-free, so the dotnet tests cover the rules the game's tool follows.
    /// </summary>
    public static class TrailSectionsEdit
    {
        /// <summary>
        /// NT35: the segments a left-click on <paramref name="target"/> adds, nearest first: down from the trail's bottom
        /// node or up from its top, through any run's segments at the nodes (a reroute), in the trail's stage; the shorter
        /// way wins, down on a tie. Null when none reaches.
        /// </summary>
        public static List<Segment> Route(TrailSections ts, ResolvedTrail trail, Segment target)
        {
            if (target == null || trail.Holds(target) || target.Built != trail.Built) return null;
            var first = trail.Segments[0];
            var last = trail.Segments[trail.Segments.Count - 1];
            var down = Search(ts, trail, last.To, target, true);
            var up = Search(ts, trail, first.From, target, false);
            if (down == null) return up;
            if (up == null) return down;
            return up.Count < down.Count ? up : down;
        }

        static List<Segment> Search(TrailSections ts, ResolvedTrail trail, ulong from, Segment target, bool down)
        {
            var via = new Dictionary<ulong, Segment> { [from] = null };
            var prev = new Dictionary<Segment, Segment>();
            var queue = new Queue<ulong>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                ulong n = queue.Dequeue();
                foreach (var s in ts.Segments)   // run ID order, then along: a tie takes the lower
                {
                    if (s.Built != trail.Built || trail.Holds(s) || prev.ContainsKey(s) || (down ? s.From : s.To) != n) continue;
                    prev[s] = via[n];
                    if (ReferenceEquals(s, target))
                    {
                        var path = new List<Segment>();
                        for (var x = s; x != null; x = prev[x]) path.Insert(0, x);
                        return path;
                    }
                    ulong next = down ? s.To : s.From;
                    if (!via.ContainsKey(next)) { via[next] = s; queue.Enqueue(next); }
                }
            }
            return null;
        }

        /// <summary>NT35: adds a routed chain onto the trail's bottom or top, out of whatever trails held it.</summary>
        public static ShapeTrails Add(DesignDocument doc, long revision, TrailSections ts, ResolvedTrail trail, IReadOnlyList<Segment> route)
        {
            var segs = new List<Segment>(trail.Segments);
            foreach (var s in route)
            {
                if (s.From == segs[segs.Count - 1].To) segs.Add(s);
                else segs.Insert(0, s);
            }
            var desired = new Dictionary<ResolvedTrail, (string name, List<Segment> segs)> { [trail] = (trail.Name, segs) };
            return Write(doc, revision, ts, desired, null);
        }

        /// <summary>NT35, NT27: takes a segment out; it becomes a trail of its own named "Run N" (or what was typed).</summary>
        public static ShapeTrails TakeOut(DesignDocument doc, long revision, TrailSections ts, ResolvedTrail trail, Segment s, string name = null)
        {
            if (!trail.Holds(s) || trail.Segments.Count < 2) return null;
            var rest = new List<Segment>();
            foreach (var x in trail.Segments) if (!ReferenceEquals(x, s)) rest.Add(x);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in ts.Trails) taken.Add(t.Name);
            var desired = new Dictionary<ResolvedTrail, (string, List<Segment>)> { [trail] = (trail.Name, rest) };
            var released = (name ?? TrailLines.NextName(ts.Trails.Count, taken), new List<Segment> { s });
            return Write(doc, revision, ts, desired, released);
        }

        /// <summary>Renames a trail; a run's own trail becomes a shaped one holding the same segments.</summary>
        public static ShapeTrails Rename(DesignDocument doc, long revision, TrailSections ts, ResolvedTrail trail, string name) =>
            Write(doc, revision, ts, new Dictionary<ResolvedTrail, (string, List<Segment>)> { [trail] = (name, new List<Segment>(trail.Segments)) }, null);

        /// <summary>NT45: a node at the point on the run (any bend); nothing else changes.</summary>
        public static ShapeTrails Cut(long revision, TrailId piece, PointMm at) => new ShapeTrails(revision, null, null, new[] { new TrailCut(piece, at) });

        /// <summary>NT45: takes a cut point away (only a cut: junctions come and go with the runs).</summary>
        public static ShapeTrails Rejoin(long revision, TrailCut cut) => new ShapeTrails(revision, null, null, null, new[] { cut });

        /// <summary>
        /// Writes the trails a click changes: each in <paramref name="desired"/> as these segments (a run's own trail
        /// becomes a shaped one keeping its ID), every other shaped trail that held any of them without them, and an
        /// optional released stretch as a new trail.
        /// </summary>
        static ShapeTrails Write(DesignDocument doc, long revision, TrailSections ts, Dictionary<ResolvedTrail, (string name, List<Segment> segs)> desired,
                                 (string name, List<Segment> segs)? released)
        {
            var claimed = new HashSet<Segment>();
            foreach (var kv in desired) foreach (var s in kv.Value.segs) claimed.Add(s);
            if (released.HasValue) foreach (var s in released.Value.segs) claimed.Add(s);
            var put = new List<TrailLine>();
            var remove = new List<TrailLineId>();
            var ids = new HashSet<ulong>();
            foreach (var kv in desired)
            {
                put.Add(new TrailLine(kv.Key.Id, kv.Value.name, SpansOf(ts, kv.Value.segs)));
                ids.Add(put[put.Count - 1].Id.Value);
            }
            // Shaped trails that lose segments to this click keep the rest (or go, NT28).
            foreach (var line in doc.Lines)
            {
                if (ids.Contains(line.Id.Value)) continue;
                var mine = new List<Segment>();
                bool lost = false;
                foreach (var t in ts.Trails)
                {
                    if (t.Line == null || t.Line.Id != line.Id) continue;
                    foreach (var s in t.Segments) { if (claimed.Contains(s)) lost = true; else mine.Add(s); }
                }
                if (!lost) continue;
                if (mine.Count == 0) remove.Add(line.Id);
                else { put.Add(new TrailLine(line.Id, line.Name, SpansOf(ts, mine))); ids.Add(line.Id.Value); }
            }
            if (released.HasValue)
            {
                var r = released.Value;
                put.Add(new TrailLine(TrailLines.FreshId(doc, r.segs[0].Piece, ids), r.name, SpansOf(ts, r.segs)));
            }
            return new ShapeTrails(revision, put, remove);
        }

        /// <summary>A trail's segments as spans: one per stretch of a run, each end by its node's lasting anchor.</summary>
        public static List<Span> SpansOf(TrailSections ts, IReadOnlyList<Segment> segs)
        {
            var spans = new List<Span>();
            for (int i = 0; i < segs.Count;)
            {
                var first = segs[i];
                int j = i;
                while (j + 1 < segs.Count && segs[j + 1].Piece == first.Piece && segs[j + 1].StartMm == segs[j].EndMm) j++;
                var last = segs[j];
                spans.Add(new Span(first.Piece, ts.AnchorAt(first.Piece, first.StartMm), ts.AnchorAt(last.Piece, last.EndMm), first.StartMm, last.EndMm));
                i = j + 1;
            }
            return spans;
        }
    }
}
