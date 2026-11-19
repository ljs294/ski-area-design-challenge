using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The design document (0.3 T15, task P3-03): every trail and lift the player has authored, and nothing derived
    /// (SH-05, NW-06). Immutable: a command makes a new document that shares the unchanged entities, so undo is a
    /// pointer swap. Collections are kept in ID order (SH-04), so the same commands give the same bytes
    /// (<see cref="DesignBytes"/>).
    /// </summary>
    public sealed class DesignDocument
    {
        /// <summary>The shape of the authored data. Task 04's save file freezes it; a change bumps this. 4 (task 15): the
        /// terrain strokes. 5 (task P3-10): the named Areas (NW-15). 6 (task P3-10): the trails made of more than one section (NT24). 7 (task P3-10): trails as spans of runs between nodes, and the
        /// player's cut points (NT43–NT46). 10 (LD9): each lift's drive end. 11 (task 4.21, TD-07): each trail's posted rating. 12 (task 4.03, LO-07 v2 and LO-09):
        /// the resort's hours, and each lift's schedule and own hours. 13 (task 4.11 PR B): each lift's plate. 14 (task 4.14, PD-08, WS-01): ponds, dams and lake sources.
        /// 15 (task 4.15, HY-01): the snowmaking network.</summary>
        public const int ShapeVersion = 15;

        /// <summary>The design's key seed: every ID is a keyed hash of it (SH-04).</summary>
        public ulong Seed { get; }
        /// <summary>The next ordinal an ID is made from. Saved, so a reload keeps making new IDs.</summary>
        public long NextOrdinal { get; }
        public IReadOnlyList<Trail> Trails => _trails;
        public IReadOnlyList<Lift> Lifts => _lifts;
        /// <summary>The confirmed terrain brush strokes (shape 4, task 15), in ID order like the rest.</summary>
        public IReadOnlyList<TerrainStroke> Strokes => _strokes;

        /// <summary>The Areas the player has named (NW-15, shape 5), by ID.</summary>
        public IReadOnlyList<AreaRecord> Areas => _areas;

        /// <summary>
        /// The trails the player has shaped (shape 7, owner NT43): each a name and spans of runs, by ID. A segment in none
        /// belongs to its run's own trail (<see cref="TrailSections"/>).
        /// </summary>
        public IReadOnlyList<TrailLine> Lines => _lines;

        /// <summary>The points the player cut on runs (NT45, shape 7), by run then point.</summary>
        public IReadOnlyList<TrailCut> Cuts => _cuts;

        /// <summary>
        /// LO-07 v2 (owner, 2026-11-08; shape 12): the resort's opening, last chair and stop, the player's to choose within
        /// daylight. The simulation's day rate runs from opening to stop (TM-02 v2).
        /// </summary>
        public DayHours Hours => _hours;

        /// <summary>Task 4.14 (PD-08, shape 14): the dug ponds, by ID.</summary>
        public IReadOnlyList<Pond> Ponds => _ponds;
        /// <summary>Task 4.14 (PD-08, shape 14): the dams across streams, by ID.</summary>
        public IReadOnlyList<Dam> Dams => _dams;
        /// <summary>Task 4.14 (WS-01, LK-02, LK-10; shape 14): what the player set on natural lakes (name, depth, source), by lake ID.</summary>
        public IReadOnlyList<LakeRecord> Lakes => _lakes;
        /// <summary>Task 4.14 (WS-03, LK-10; shape 14): what the player set on stream lines (name, width), by OSM way.</summary>
        public IReadOnlyList<StreamRecord> Streams => _streams;
        /// <summary>Task 4.15 (HY-01, SG-01…04; shape 16): the snowmaking network: pipes, nodes, pump houses and guns.</summary>
        public SnowNetwork Snow => _snow;

        readonly Trail[] _trails;
        readonly Lift[] _lifts;
        readonly TerrainStroke[] _strokes;
        readonly AreaRecord[] _areas;
        readonly TrailLine[] _lines;
        readonly TrailCut[] _cuts;
        readonly DayHours _hours;
        readonly Pond[] _ponds;
        readonly Dam[] _dams;
        readonly LakeRecord[] _lakes;
        readonly StreamRecord[] _streams;
        readonly SnowNetwork _snow;             // task 4.15, shape 16
        TrailNodes _nodes;                      // NT42: where sections meet, built on first use

        DesignDocument(ulong seed, long nextOrdinal, Trail[] trails, Lift[] lifts, TerrainStroke[] strokes, AreaRecord[] areas, TrailLine[] lines,
                       TrailCut[] cuts, DayHours hours, Pond[] ponds, Dam[] dams, LakeRecord[] lakes, StreamRecord[] streams, SnowNetwork snow)
        {
            _snow = snow ?? SnowNetwork.Empty;
            _hours = hours;
            _ponds = ponds;
            _dams = dams;
            _lakes = lakes;
            _streams = streams;
            Seed = seed;
            NextOrdinal = nextOrdinal;
            _trails = trails;
            _lifts = lifts;
            _strokes = strokes;
            _areas = areas;
            _lines = lines;
            _cuts = cuts;
        }

        public static DesignDocument Empty(ulong seed) =>
            new DesignDocument(seed, 1, Array.Empty<Trail>(), Array.Empty<Lift>(), Array.Empty<TerrainStroke>(), Array.Empty<AreaRecord>(),
                Array.Empty<TrailLine>(), Array.Empty<TrailCut>(), DayHours.Default,
                Array.Empty<Pond>(), Array.Empty<Dam>(), Array.Empty<LakeRecord>(), Array.Empty<StreamRecord>(), SnowNetwork.Empty);

        /// <summary>
        /// A document from stored parts (task 04's reader). Entities are sorted by ID here and checked by the same
        /// rules commands use; a bad document throws <see cref="FormatException"/> rather than loading half.
        /// </summary>
        public static DesignDocument From(ulong seed, long nextOrdinal, IReadOnlyList<Trail> trails, IReadOnlyList<Lift> lifts) =>
            From(seed, nextOrdinal, trails, lifts, null, null);

        /// <summary>
        /// A document from stored parts with its terrain strokes (shape 4), named Areas (shape 5) and trails of several
        /// sections (shape 6) and cut points (shape 7). Spans of runs that are gone, and empty trails, are dropped by
        /// <see cref="TrailLines.Normalise"/> as a command would leave them, never refused.
        /// </summary>
        public static DesignDocument From(ulong seed, long nextOrdinal, IReadOnlyList<Trail> trails, IReadOnlyList<Lift> lifts,
                                          IReadOnlyList<TerrainStroke> strokes, IReadOnlyList<AreaRecord> areas = null,
                                          IReadOnlyList<TrailLine> lines = null, IReadOnlyList<TrailCut> cuts = null, DayHours? hours = null,
                                          IReadOnlyList<Pond> ponds = null, IReadOnlyList<Dam> dams = null, IReadOnlyList<LakeRecord> lakes = null,
                                          IReadOnlyList<StreamRecord> streams = null, SnowNetwork snow = null)
        {
            var t = new Trail[trails?.Count ?? 0];
            for (int i = 0; i < t.Length; i++) t[i] = trails[i];
            var l = new Lift[lifts?.Count ?? 0];
            for (int i = 0; i < l.Length; i++) l[i] = lifts[i];
            Array.Sort(t, (a, b) => a.Id.CompareTo(b.Id));
            Array.Sort(l, (a, b) => a.Id.CompareTo(b.Id));
            var k = new TerrainStroke[strokes?.Count ?? 0];
            for (int i = 0; i < k.Length; i++) k[i] = strokes[i];
            Array.Sort(k, (a, b) => a.Id.CompareTo(b.Id));
            var r = new AreaRecord[areas?.Count ?? 0];
            for (int i = 0; i < r.Length; i++) r[i] = areas[i];
            Array.Sort(r, (a, b) => a.Id.CompareTo(b.Id));
            var n = new TrailLine[lines?.Count ?? 0];
            for (int i = 0; i < n.Length; i++) n[i] = lines[i];
            Array.Sort(n, (a, b) => a.Id.CompareTo(b.Id));
            var c = new TrailCut[cuts?.Count ?? 0];
            for (int i = 0; i < c.Length; i++) c[i] = cuts[i];
            Array.Sort(c);
            var p = Sorted(ponds, (a, b) => a.Id.CompareTo(b.Id));
            var d = Sorted(dams, (a, b) => a.Id.CompareTo(b.Id));
            var lk = Sorted(lakes, (a, b) => a.CompareTo(b));
            var st = Sorted(streams, (a, b) => a.CompareTo(b));
            var doc = TrailLines.Normalise(new DesignDocument(seed, nextOrdinal, t, l, k, r, n, c, hours ?? DayHours.Default, p, d, lk, st, snow ?? SnowNetwork.Empty));
            string problem = DesignRules.DocumentProblem(doc);
            if (problem != null) throw new FormatException("Design document: " + problem);
            return doc;
        }

        public Trail FindTrail(TrailId id)
        {
            int i = TrailIndex(id);
            return i >= 0 ? _trails[i] : null;
        }

        public Lift FindLift(LiftId id)
        {
            int i = LiftIndex(id);
            return i >= 0 ? _lifts[i] : null;
        }

        /// <summary>NT42: the hidden nodes where the sections' ends meet (built on first use; derived, never saved).</summary>
        internal TrailNodes Nodes => _nodes ??= TrailNodes.Build(this);   // benign race: every thread builds the same

        public TrailLine FindLine(TrailLineId id)
        {
            foreach (var line in _lines) if (line.Id == id) return line;
            return null;
        }

        public AreaRecord FindArea(AreaId id)
        {
            foreach (var a in _areas) if (a.Id == id) return a;
            return null;
        }

        /// <summary>The lift holding a tower, or null.</summary>
        public Lift FindLiftOfTower(TowerId id)
        {
            foreach (var lift in _lifts) if (lift.IndexOfTower(id) >= 0) return lift;
            return null;
        }

        static T[] Sorted<T>(IReadOnlyList<T> items, Comparison<T> order)
        {
            var a = new T[items?.Count ?? 0];
            for (int i = 0; i < a.Length; i++) a[i] = items[i];
            Array.Sort(a, order);
            return a;
        }

        public Pond FindPond(PondId id)
        {
            foreach (var p in _ponds) if (p.Id == id) return p;
            return null;
        }

        public Dam FindDam(DamId id)
        {
            foreach (var d in _dams) if (d.Id == id) return d;
            return null;
        }

        /// <summary>What the player set on a lake (LK-10), or an empty record for that lake.</summary>
        public LakeRecord FindLake(ulong lake)
        {
            foreach (var x in _lakes) if (x.Lake == lake) return x;
            return new LakeRecord(lake, "", 0, false);
        }

        /// <summary>Whether the lake is switched on as a source (WS-01).</summary>
        public bool IsLakeSource(ulong lake) => FindLake(lake).Source;

        /// <summary>What the player set on a stream line (LK-10), or an empty record for that way.</summary>
        public StreamRecord FindStream(ulong way)
        {
            foreach (var x in _streams) if (x.Way == way) return x;
            return new StreamRecord(way, "", 0);
        }

        public TerrainStroke FindStroke(TerrainStrokeId id)
        {
            int i = StrokeIndex(id);
            return i >= 0 ? _strokes[i] : null;
        }

        internal int StrokeIndex(TerrainStrokeId id)
        {
            int lo = 0, hi = _strokes.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = _strokes[mid].Id.CompareTo(id);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return ~lo;
        }

        internal int TrailIndex(TrailId id)
        {
            int lo = 0, hi = _trails.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = _trails[mid].Id.CompareTo(id);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return ~lo;
        }

        internal int LiftIndex(LiftId id)
        {
            int lo = 0, hi = _lifts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = _lifts[mid].Id.CompareTo(id);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return ~lo;
        }

        internal bool IdInUse(ulong value)
        {
            if (TrailIndex(new TrailId(value)) >= 0 || LiftIndex(new LiftId(value)) >= 0 || StrokeIndex(new TerrainStrokeId(value)) >= 0
                || FindArea(new AreaId(value)) != null || FindLine(new TrailLineId(value)) != null
                || FindPond(new PondId(value)) != null || FindDam(new DamId(value)) != null || _snow.Contains(value)) return true;
            return FindLiftOfTower(new TowerId(value)) != null;
        }

        // Copy-on-write edits: each returns a new document sharing every untouched entity.

        internal DesignDocument WithOrdinal(long nextOrdinal) => new DesignDocument(Seed, nextOrdinal, _trails, _lifts, _strokes, _areas, _lines, _cuts, _hours, _ponds, _dams, _lakes, _streams, _snow);

        internal DesignDocument PutTrail(Trail trail)
        {
            int i = TrailIndex(trail.Id);
            Trail[] t;
            if (i >= 0)
            {
                t = (Trail[])_trails.Clone();
                t[i] = trail;
            }
            else
            {
                i = ~i;
                t = new Trail[_trails.Length + 1];
                Array.Copy(_trails, 0, t, 0, i);
                t[i] = trail;
                Array.Copy(_trails, i, t, i + 1, _trails.Length - i);
            }
            return new DesignDocument(Seed, NextOrdinal, t, _lifts, _strokes, _areas, _lines, _cuts, _hours, _ponds, _dams, _lakes, _streams, _snow);
        }

        internal DesignDocument RemoveTrail(TrailId id)
        {
            int i = TrailIndex(id);
            if (i < 0) return this;
            var t = new Trail[_trails.Length - 1];
            Array.Copy(_trails, 0, t, 0, i);
            Array.Copy(_trails, i + 1, t, i, _trails.Length - i - 1);
            return new DesignDocument(Seed, NextOrdinal, t, _lifts, _strokes, _areas, _lines, _cuts, _hours, _ponds, _dams, _lakes, _streams, _snow);
        }

        internal DesignDocument PutLift(Lift lift)
        {
            int i = LiftIndex(lift.Id);
            Lift[] l;
            if (i >= 0)
            {
                l = (Lift[])_lifts.Clone();
                l[i] = lift;
            }
            else
            {
                i = ~i;
                l = new Lift[_lifts.Length + 1];
                Array.Copy(_lifts, 0, l, 0, i);
                l[i] = lift;
                Array.Copy(_lifts, i, l, i + 1, _lifts.Length - i);
            }
            return new DesignDocument(Seed, NextOrdinal, _trails, l, _strokes, _areas, _lines, _cuts, _hours, _ponds, _dams, _lakes, _streams, _snow);
        }

        internal DesignDocument PutStroke(TerrainStroke stroke)
        {
            int i = StrokeIndex(stroke.Id);
            TerrainStroke[] k;
            if (i >= 0)
            {
                k = (TerrainStroke[])_strokes.Clone();
                k[i] = stroke;
            }
            else
            {
                i = ~i;
                k = new TerrainStroke[_strokes.Length + 1];
                Array.Copy(_strokes, 0, k, 0, i);
                k[i] = stroke;
                Array.Copy(_strokes, i, k, i + 1, _strokes.Length - i);
            }
            return new DesignDocument(Seed, NextOrdinal, _trails, _lifts, k, _areas, _lines, _cuts, _hours, _ponds, _dams, _lakes, _streams, _snow);
        }

        /// <summary>Removes a lift, and its ends from every named Area; an Area left with no ends goes too (NW-15).</summary>
        internal DesignDocument RemoveLift(LiftId id)
        {
            int i = LiftIndex(id);
            if (i < 0) return this;
            var l = new Lift[_lifts.Length - 1];
            Array.Copy(_lifts, 0, l, 0, i);
            Array.Copy(_lifts, i + 1, l, i, _lifts.Length - i - 1);
            var areas = new List<AreaRecord>(_areas.Length);
            foreach (var a in _areas)
            {
                var kept = a.WithoutLift(id);
                if (kept.Ends.Count > 0) areas.Add(kept);
            }
            return new DesignDocument(Seed, NextOrdinal, _trails, l, _strokes, areas.ToArray(), _lines, _cuts, _hours, _ponds, _dams, _lakes, _streams, _snow);
        }

        /// <summary>The saved lines replaced whole (sorted here); <see cref="TrailLines.Normalise"/> and the line commands.</summary>
        /// <summary>One shaped trail replaced by ID (TD-07's sign), or added.</summary>
        internal DesignDocument PutLine(TrailLine line)
        {
            var lines = new List<TrailLine>(_lines.Length + 1);
            bool replaced = false;
            foreach (var l in _lines)
            {
                if (l.Id == line.Id) { lines.Add(line); replaced = true; }
                else lines.Add(l);
            }
            if (!replaced) lines.Add(line);
            return WithLines(lines);
        }

        internal DesignDocument WithLines(IReadOnlyList<TrailLine> lines)
        {
            var n = new TrailLine[lines.Count];
            for (int i = 0; i < n.Length; i++) n[i] = lines[i];
            Array.Sort(n, (a, b) => a.Id.CompareTo(b.Id));
            return new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, n, _cuts, _hours, _ponds, _dams, _lakes, _streams, _snow);
        }

        /// <summary>The cut points replaced whole (sorted here).</summary>
        internal DesignDocument WithCuts(IReadOnlyList<TrailCut> cuts)
        {
            var c = new TrailCut[cuts.Count];
            for (int i = 0; i < c.Length; i++) c[i] = cuts[i];
            Array.Sort(c);
            return new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, _lines, c, _hours, _ponds, _dams, _lakes, _streams, _snow);
        }

        /// <summary>The resort's hours replaced (LO-07 v2, shape 12).</summary>
        internal DesignDocument WithHours(DayHours hours) => new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, _lines, _cuts, hours, _ponds, _dams, _lakes, _streams, _snow);

        /// <summary>One pond replaced by ID, or added (task 4.14).</summary>
        internal DesignDocument PutPond(Pond pond)
        {
            var a = new List<Pond>(_ponds.Length + 1);
            bool replaced = false;
            foreach (var x in _ponds) { if (x.Id == pond.Id) { a.Add(pond); replaced = true; } else a.Add(x); }
            if (!replaced) a.Add(pond);
            a.Sort((x, y) => x.Id.CompareTo(y.Id));
            return new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, _lines, _cuts, _hours, a.ToArray(), _dams, _lakes, _streams, _snow);
        }

        internal DesignDocument RemovePond(PondId id)
        {
            var a = new List<Pond>(_ponds.Length);
            foreach (var x in _ponds) if (x.Id != id) a.Add(x);
            return a.Count == _ponds.Length ? this : new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, _lines, _cuts, _hours, a.ToArray(), _dams, _lakes, _streams, _snow);
        }

        /// <summary>One dam replaced by ID, or added (task 4.14).</summary>
        internal DesignDocument PutDam(Dam dam)
        {
            var a = new List<Dam>(_dams.Length + 1);
            bool replaced = false;
            foreach (var x in _dams) { if (x.Id == dam.Id) { a.Add(dam); replaced = true; } else a.Add(x); }
            if (!replaced) a.Add(dam);
            a.Sort((x, y) => x.Id.CompareTo(y.Id));
            return new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, _lines, _cuts, _hours, _ponds, a.ToArray(), _lakes, _streams, _snow);
        }

        internal DesignDocument RemoveDam(DamId id)
        {
            var a = new List<Dam>(_dams.Length);
            foreach (var x in _dams) if (x.Id != id) a.Add(x);
            return a.Count == _dams.Length ? this : new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, _lines, _cuts, _hours, _ponds, a.ToArray(), _lakes, _streams, _snow);
        }

        /// <summary>A lake's record replaced (LK-10); an empty record leaves the design.</summary>
        internal DesignDocument PutLake(LakeRecord lake)
        {
            var a = new List<LakeRecord>(_lakes.Length + 1);
            foreach (var x in _lakes) if (x.Lake != lake.Lake) a.Add(x);
            if (!lake.IsEmpty) a.Add(lake);
            a.Sort((x, y) => x.CompareTo(y));
            return new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, _lines, _cuts, _hours, _ponds, _dams, a.ToArray(), _streams, _snow);
        }

        /// <summary>A stream's record replaced (LK-10); an empty record leaves the design.</summary>
        internal DesignDocument PutStream(StreamRecord stream)
        {
            var a = new List<StreamRecord>(_streams.Length + 1);
            foreach (var x in _streams) if (x.Way != stream.Way) a.Add(x);
            if (!stream.IsEmpty) a.Add(stream);
            a.Sort((x, y) => x.CompareTo(y));
            return new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, _lines, _cuts, _hours, _ponds, _dams, _lakes, a.ToArray(), _snow);
        }

        /// <summary>The snowmaking network replaced (task 4.15).</summary>
        internal DesignDocument WithSnow(SnowNetwork snow) => ReferenceEquals(snow, _snow) ? this
            : new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, _areas, _lines, _cuts, _hours, _ponds, _dams, _lakes, _streams, snow);

        /// <summary>Whether a command changed the trails or their lines, so <see cref="TrailLines.Normalise"/> must run.</summary>
        internal bool TrailsDifferFrom(DesignDocument other) =>
            !ReferenceEquals(_trails, other._trails) || !ReferenceEquals(_lines, other._lines) || !ReferenceEquals(_cuts, other._cuts);

        internal DesignDocument PutArea(AreaRecord area)
        {
            var a = new List<AreaRecord>(_areas.Length + 1);
            bool replaced = false;
            foreach (var x in _areas)
            {
                if (x.Id == area.Id) { a.Add(area); replaced = true; }
                else a.Add(x);
            }
            if (!replaced) a.Add(area);
            a.Sort((x, y) => x.Id.CompareTo(y.Id));
            return new DesignDocument(Seed, NextOrdinal, _trails, _lifts, _strokes, a.ToArray(), _lines, _cuts, _hours, _ponds, _dams, _lakes, _streams, _snow);
        }
    }

    /// <summary>
    /// Mints IDs for one command: keyed hashes of the seed and the next free ordinals (SH-04). An ordinal whose hash is
    /// already in use is skipped, so IDs never collide and the same document always mints the same IDs.
    /// </summary>
    internal struct IdMint
    {
        readonly DesignDocument _doc;
        long _ordinal;
        List<ulong> _minted;

        public IdMint(DesignDocument doc)
        {
            _doc = doc;
            _ordinal = doc.NextOrdinal;
            _minted = null;
        }

        public long NextOrdinal => _ordinal;

        public TrailId Trail() => new TrailId(Next(DesignKey.TrailTag));
        public LiftId Lift() => new LiftId(Next(DesignKey.LiftTag));
        public AreaId Area() => new AreaId(Next(DesignKey.AreaTag));
        public TowerId Tower() => new TowerId(Next(DesignKey.TowerTag));
        public TerrainStrokeId Stroke() => new TerrainStrokeId(Next(DesignKey.TerrainStrokeTag));
        public PondId Pond() => new PondId(Next(DesignKey.PondTag));
        public DamId Dam() => new DamId(Next(DesignKey.DamTag));
        public SnowId SnowPipe() => new SnowId(Next(DesignKey.SnowPipeTag));
        public SnowId SnowNode() => new SnowId(Next(DesignKey.SnowNodeTag));
        public SnowId PumpHouse() => new SnowId(Next(DesignKey.PumpHouseTag));
        public SnowId SnowGun() => new SnowId(Next(DesignKey.SnowGunTag));

        ulong Next(ulong tag)
        {
            while (true)
            {
                ulong v = DesignKey.Hash(_doc.Seed, tag, (ulong)_ordinal++);
                if (_doc.IdInUse(v) || (_minted != null && _minted.Contains(v))) continue;
                (_minted ??= new List<ulong>()).Add(v);
                return v;
            }
        }
    }
}
