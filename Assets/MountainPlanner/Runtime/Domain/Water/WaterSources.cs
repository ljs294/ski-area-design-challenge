using System;

namespace MountainPlanner.Domain.Water
{
    /// <summary>
    /// WS-01 (task 4.14 PR B): one snowmaking source as the simulation gets it, a pond, a dam's reservoir or a lake the player
    /// switched on. Built off the main thread from the design and its graded ground (SI-21) and immutable: the water it holds
    /// is the simulation's, matched by <see cref="Id"/>.
    /// </summary>
    public sealed class WaterSource
    {
        public readonly ulong Id;
        public readonly WaterBodyKind Kind;
        /// <summary>Its index in the water-body table the same build made.</summary>
        public readonly int Body;
        public readonly int FullPoolMm;
        /// <summary>WS-02: the water held at each 0.1 ft below full pool.</summary>
        public readonly StageCurve Curve;
        /// <summary>WS-03: a dam's stream, cubic feet a second × 1000 before the season's factor; 0 for a pond or a lake.</summary>
        public readonly int StreamMilliCfs;
        /// <summary>WS-04: the player's spring, gallons a minute (a natural pond only).</summary>
        public readonly int SpringGpm;
        /// <summary>WS-09: takes runoff (and its spring); false for a pumped-only pond.</summary>
        public readonly bool Natural;
        /// <summary>WS-10: a new one starts with nothing in it.</summary>
        public readonly bool StartsEmpty;

        public WaterSource(ulong id, WaterBodyKind kind, int body, int fullPoolMm, StageCurve curve, int streamMilliCfs, int springGpm, bool natural, bool startsEmpty)
        {
            if (kind == WaterBodyKind.Streams) throw new ArgumentException("Streams are no source.", nameof(kind));
            if (curve == null) throw new ArgumentNullException(nameof(curve));
            if (streamMilliCfs < 0 || springGpm < 0) throw new ArgumentOutOfRangeException(nameof(streamMilliCfs));
            Id = id; Kind = kind; Body = body; FullPoolMm = fullPoolMm; Curve = curve;
            StreamMilliCfs = streamMilliCfs; SpringGpm = natural ? springGpm : 0; Natural = natural; StartsEmpty = startsEmpty;
        }
    }

    /// <summary>
    /// The sources, in id order, and where each 8 m cell's runoff goes (<see cref="DrainsTo"/>: 1 + the source, 0 for
    /// none). Sent to the simulation as SetWaterSources (command 33) beside the water-body table; its <see cref="Hash"/>
    /// covers everything, so a change to a pond's options (fills, starts, spring) is sent too.
    /// </summary>
    public sealed class WaterSources
    {
        public static readonly WaterSources None = new WaterSources(Array.Empty<WaterSource>(), 0, 0, null);

        readonly WaterSource[] _sources;
        public readonly int Width, Depth;
        /// <summary>Per snow cell, row-major: 1 + the source its runoff reaches, or 0; null when nothing drains anywhere.</summary>
        public readonly byte[] DrainsTo;
        public readonly ulong Hash;

        public int Count => _sources.Length;
        public WaterSource this[int i] => _sources[i];

        public WaterSources(WaterSource[] sources, int width, int depth, byte[] drainsTo)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            if (sources.Length > 254) throw new ArgumentException("At most 254 sources.", nameof(sources));
            if (drainsTo != null && drainsTo.Length != width * depth) throw new ArgumentException("A label per cell.", nameof(drainsTo));
            var s = (WaterSource[])sources.Clone();
            Array.Sort(s, (a, b) => a.Id.CompareTo(b.Id));
            for (int i = 1; i < s.Length; i++) if (s[i].Id == s[i - 1].Id) throw new ArgumentException("Two sources share an id.", nameof(sources));
            _sources = s; Width = width; Depth = depth; DrainsTo = drainsTo;

            ulong h = 14695981039346656037UL;
            void Mix(long v) { unchecked { for (int k = 0; k < 8; k++) { h ^= (byte)(v >> (8 * k)); h *= 1099511628211UL; } } }
            Mix(s.Length); Mix(width); Mix(depth);
            foreach (var x in s)
            {
                Mix((long)x.Id); Mix((long)x.Kind); Mix(x.Body); Mix(x.FullPoolMm); Mix(x.StreamMilliCfs); Mix(x.SpringGpm);
                Mix(x.Natural ? 1 : 0); Mix(x.StartsEmpty ? 1 : 0); Mix(x.Curve.Steps);
                for (int j = 0; j <= x.Curve.Steps; j++) { Mix(x.Curve.GallonsAtStep(j)); Mix(x.Curve.AreaAtStep(j)); }
            }
            if (drainsTo != null) { Mix(drainsTo.Length); foreach (byte b in drainsTo) { unchecked { h ^= b; h *= 1099511628211UL; } } }
            Hash = h;
        }

        public int IndexOf(ulong id)
        {
            int lo = 0, hi = _sources.Length - 1;
            while (lo <= hi)
            {
                int m = (lo + hi) >> 1;
                ulong v = _sources[m].Id;
                if (v == id) return m;
                if (v < id) lo = m + 1; else hi = m - 1;
            }
            return -1;
        }
    }
}
