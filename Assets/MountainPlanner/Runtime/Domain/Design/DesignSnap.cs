namespace MountainPlanner.Domain.Design
{
    /// <summary>What a construction tool's point snapped to (P3-06).</summary>
    public enum SnapKind : byte { None = 0, LiftBottom = 1, LiftTop = 2, TrailHead = 3, TrailTail = 4, TrailLine = 5 }

    /// <summary>The targets a tool asks for; a lift terminal and a trail's head or tail are TR-05's targets.</summary>
    [System.Flags]
    public enum SnapTargets : byte
    {
        None = 0,
        LiftEnds = 1,
        TrailEnds = 2,
        TrailLines = 4,
        All = LiftEnds | TrailEnds | TrailLines,
    }

    /// <summary>A snapped point: where, onto what, and how far the pointer was from it.</summary>
    public readonly struct SnapHit
    {
        public readonly SnapKind Kind;
        public readonly PointMm At;
        /// <summary>The lift's or trail's ID value; 0 with no snap.</summary>
        public readonly ulong Owner;
        public readonly long DistanceSquaredMm;

        public SnapHit(SnapKind kind, PointMm at, ulong owner, long distanceSquaredMm)
        {
            Kind = kind; At = at; Owner = owner; DistanceSquaredMm = distanceSquaredMm;
        }

        public bool Snapped => Kind != SnapKind.None;
        public static SnapHit None(PointMm at) => new SnapHit(SnapKind.None, at, 0, 0);
    }

    /// <summary>
    /// The nearest point on a trail's centreline (TR-03). The centreline is derived, so task 08's trail geometry
    /// supplies it; until then <see cref="DesignSnap"/> snaps to lift terminals and trail heads and tails only.
    /// </summary>
    public interface ICentrelineSnap
    {
        bool Nearest(Trail trail, PointMm from, out PointMm on);
    }

    /// <summary>
    /// Snapping for the construction tools (P3-06, TR-05, NW-07): the nearest lift top or bottom, trail head or tail,
    /// or point on another trail within 30 ft. Ends beat lines at equal distance, then lower IDs win, so the same
    /// design and pointer always snap the same way (SH-04). Engine-free; allocates nothing.
    /// </summary>
    public static class DesignSnap
    {
        public static SnapHit Nearest(DesignDocument doc, PointMm from, SnapTargets targets = SnapTargets.All,
                                      ICentrelineSnap lines = null, ulong skip = 0, int reachMm = DesignUnits.SnapMm)
        {
            var best = SnapHit.None(from);
            if (doc == null || targets == SnapTargets.None) return best;
            long reach = (long)reachMm * reachMm;

            if ((targets & SnapTargets.LiftEnds) != 0)
            {
                var lifts = doc.Lifts;
                for (int i = 0; i < lifts.Count; i++)
                {
                    var l = lifts[i];
                    if (l.Id.Value == skip) continue;
                    Consider(ref best, SnapKind.LiftBottom, l.Bottom, l.Id.Value, from, reach);
                    Consider(ref best, SnapKind.LiftTop, l.Top, l.Id.Value, from, reach);
                }
            }

            var trails = doc.Trails;
            for (int i = 0; i < trails.Count; i++)
            {
                var t = trails[i];
                if (t.Id.Value == skip) continue;
                if ((targets & SnapTargets.TrailEnds) != 0)
                {
                    if (t.Head.HasValue) Consider(ref best, SnapKind.TrailHead, t.Head.Value, t.Id.Value, from, reach);
                    if (t.Tail.HasValue) Consider(ref best, SnapKind.TrailTail, t.Tail.Value, t.Id.Value, from, reach);
                }
                if ((targets & SnapTargets.TrailLines) != 0 && lines != null && lines.Nearest(t, from, out var on))
                    Consider(ref best, SnapKind.TrailLine, on, t.Id.Value, from, reach);
            }
            return best;
        }

        static void Consider(ref SnapHit best, SnapKind kind, PointMm at, ulong owner, PointMm from, long reach)
        {
            long d = from.DistanceSquared(at);
            if (d > reach) return;
            if (best.Snapped && !Better(kind, d, owner, best)) return;
            best = new SnapHit(kind, at, owner, d);
        }

        // Nearer wins; at equal distance an end beats a line, then the lower kind, then the lower ID.
        static bool Better(SnapKind kind, long d, ulong owner, SnapHit than)
        {
            if (d != than.DistanceSquaredMm) return d < than.DistanceSquaredMm;
            bool line = kind == SnapKind.TrailLine, thanLine = than.Kind == SnapKind.TrailLine;
            if (line != thanLine) return !line;
            if (kind != than.Kind) return kind < than.Kind;
            return owner < than.Owner;
        }
    }
}
