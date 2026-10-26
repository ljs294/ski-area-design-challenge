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
        /// <summary>The shape of the authored data. Task 04's save file freezes it; a change bumps this. 4 (task P3-10):
        /// the named Areas (NW-15).</summary>
        public const int ShapeVersion = 4;

        /// <summary>The design's key seed: every ID is a keyed hash of it (SH-04).</summary>
        public ulong Seed { get; }
        /// <summary>The next ordinal an ID is made from. Saved, so a reload keeps making new IDs.</summary>
        public long NextOrdinal { get; }
        public IReadOnlyList<Trail> Trails => _trails;
        public IReadOnlyList<Lift> Lifts => _lifts;
        /// <summary>The Areas the player has named (NW-15), by ID.</summary>
        public IReadOnlyList<AreaRecord> Areas => _areas;

        readonly Trail[] _trails;
        readonly Lift[] _lifts;
        readonly AreaRecord[] _areas;

        DesignDocument(ulong seed, long nextOrdinal, Trail[] trails, Lift[] lifts, AreaRecord[] areas)
        {
            Seed = seed;
            NextOrdinal = nextOrdinal;
            _trails = trails;
            _lifts = lifts;
            _areas = areas;
        }

        public static DesignDocument Empty(ulong seed) => new DesignDocument(seed, 1, Array.Empty<Trail>(), Array.Empty<Lift>(), Array.Empty<AreaRecord>());

        /// <summary>
        /// A document from stored parts (task 04's reader). Entities are sorted by ID here and checked by the same
        /// rules commands use; a bad document throws <see cref="FormatException"/> rather than loading half.
        /// </summary>
        public static DesignDocument From(ulong seed, long nextOrdinal, IReadOnlyList<Trail> trails, IReadOnlyList<Lift> lifts,
                                          IReadOnlyList<AreaRecord> areas = null)
        {
            var t = new Trail[trails?.Count ?? 0];
            for (int i = 0; i < t.Length; i++) t[i] = trails[i];
            var l = new Lift[lifts?.Count ?? 0];
            for (int i = 0; i < l.Length; i++) l[i] = lifts[i];
            var a = new AreaRecord[areas?.Count ?? 0];
            for (int i = 0; i < a.Length; i++) a[i] = areas[i];
            Array.Sort(t, (x, y) => x.Id.CompareTo(y.Id));
            Array.Sort(l, (x, y) => x.Id.CompareTo(y.Id));
            Array.Sort(a, (x, y) => x.Id.CompareTo(y.Id));
            var doc = new DesignDocument(seed, nextOrdinal, t, l, a);
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
            if (TrailIndex(new TrailId(value)) >= 0 || LiftIndex(new LiftId(value)) >= 0 || FindArea(new AreaId(value)) != null) return true;
            return FindLiftOfTower(new TowerId(value)) != null;
        }

        // Copy-on-write edits: each returns a new document sharing every untouched entity.

        internal DesignDocument WithOrdinal(long nextOrdinal) => new DesignDocument(Seed, nextOrdinal, _trails, _lifts, _areas);

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
            return new DesignDocument(Seed, NextOrdinal, t, _lifts, _areas);
        }

        internal DesignDocument RemoveTrail(TrailId id)
        {
            int i = TrailIndex(id);
            if (i < 0) return this;
            var t = new Trail[_trails.Length - 1];
            Array.Copy(_trails, 0, t, 0, i);
            Array.Copy(_trails, i + 1, t, i, _trails.Length - i - 1);
            return new DesignDocument(Seed, NextOrdinal, t, _lifts, _areas);
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
            return new DesignDocument(Seed, NextOrdinal, _trails, l, _areas);
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
            return new DesignDocument(Seed, NextOrdinal, _trails, l, areas.ToArray());
        }

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
            return new DesignDocument(Seed, NextOrdinal, _trails, _lifts, a.ToArray());
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
