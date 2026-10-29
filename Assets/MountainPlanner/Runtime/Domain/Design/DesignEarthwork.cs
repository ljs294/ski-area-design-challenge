using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// One built piece of work that shapes the ground (TL3): a built trail (task 08) or a built lift's pads, trench and
    /// clearing (task 09). Built from the design; immutable.
    /// </summary>
    public interface IEarthworkItem
    {
        /// <summary>Unique within the design: the trail's or lift's ID.</summary>
        ulong Key { get; }
        /// <summary>The fold's order: every trail (0), then every lift (1), then every terrain stroke (2, task 15); within a phase, <see cref="Order"/>.</summary>
        int Phase { get; }
        /// <summary>Creation order within the phase (the document ordinal the ID was minted at).</summary>
        long Order { get; }
        /// <summary>Every lattice point it may read or write; its writes stay inside.</summary>
        PointRect Reads { get; }
        /// <summary>
        /// The edit tiles it may read or write: inside <see cref="Reads"/>, but only those its work reaches (a diagonal run's
        /// box holds many tiles it never touches).
        /// </summary>
        IReadOnlyCollection<EditTileKey> Tiles { get; }
        /// <summary>Whether its writes would be the same as <paramref name="other"/>'s on the same ground.</summary>
        bool SameAs(IEarthworkItem other);
        /// <summary>
        /// Its writes over <paramref name="ground"/>, the source plus every earlier piece's writes (inside
        /// <see cref="Reads"/>). <paramref name="detail"/> is anything a tool wants back (a trail's stations, volumes).
        /// </summary>
        EarthworkWrites Build(EditedHeights ground, EarthworkContext context, out object detail);
    }

    /// <summary>
    /// A piece whose build reads earlier pieces' details through <see cref="EarthworkContext.DetailOf"/> (task 15's strokes
    /// read their lifts' ropes): graded again whenever one of those is (task P3-11), since its tiles alone wouldn't say.
    /// </summary>
    public interface IReadsDetails
    {
        IEnumerable<ulong> DetailsRead { get; }
    }

    /// <summary>
    /// A piece whose build reads the ground over less than its <see cref="IEarthworkItem.Tiles"/> (a lift's wide corridor):
    /// graded again only when a change lands in its reach (task P3-11). Its tiles still open as before, so what it writes
    /// never changes.
    /// </summary>
    public interface IReachTiles
    {
        IReadOnlyCollection<EditTileKey> ReachTiles { get; }
    }

    /// <summary>Gathers a design's built pieces of one kind. Task 08 registers trails, task 09 lifts, task 15 terrain strokes.</summary>
    public interface IEarthworkContributor
    {
        void Collect(DesignDocument doc, EarthworkSite site, List<IEarthworkItem> into);
    }

    /// <summary>Where the design sits on the lattice: the local frame's origin (TE5, the site's centre) in the lattice's frame.</summary>
    public readonly struct EarthworkSite
    {
        public readonly EditLattice Lattice;
        public readonly double OriginX, OriginY;
        /// <summary>The area's core box in lattice points (SH-06, TB-10): where brushes may work. Empty: unbounded (tests).</summary>
        public readonly PointRect Core;
        public EarthworkSite(EditLattice lattice, double originX, double originY) : this(lattice, originX, originY, PointRect.Empty) { }
        public EarthworkSite(EditLattice lattice, double originX, double originY, PointRect core)
        {
            Lattice = lattice; OriginX = originX; OriginY = originY; Core = core;
        }

        public void PointOf(PointMm p, out double x, out double y) { x = OriginX + p.XMetres; y = OriginY + p.YMetres; }
    }

    /// <summary>What a piece may ask about the fold so far.</summary>
    public sealed class EarthworkContext
    {
        readonly Dictionary<EditTileKey, ulong[]> _claims;
        readonly Func<ulong, object> _detail;
        internal EarthworkContext(Dictionary<EditTileKey, ulong[]> claims, Func<ulong, object> detail = null) { _claims = claims; _detail = detail; }

        /// <summary>
        /// What an earlier piece's build handed back in this fold (a lift's <see cref="LiftBuild.Works"/> with its rope, for
        /// task 15's strokes), or null.
        /// </summary>
        public object DetailOf(ulong key) => _detail?.Invoke(key);

        /// <summary>Whether an earlier trail claims lattice point (column, row) (TF-10: the earlier trail keeps its ground).</summary>
        public bool ClaimedEarlier(int column, int row)
        {
            var key = EditTileKey.Of(column, row);
            if (!_claims.TryGetValue(key, out var bits)) return false;
            int at = (row - key.FirstRow) * EditTileKey.Side + (column - key.FirstColumn);
            return (bits[at >> 6] & (1UL << (at & 63))) != 0;
        }
    }

    /// <summary>
    /// The design's earthwork (owner TL3): the terrain edits are every built piece's writes folded in order over the
    /// source heights, trails by creation, then lifts, then terrain strokes (task 15, BR6). After a command, only what can have changed is rebuilt: the tiles
    /// of pieces that are new, changed or gone, and of later pieces whose ground those changed, each later piece
    /// regraded only when the writes under it moved. Points outside the rebuilt tiles keep the committed edits exactly,
    /// so a saved design's ground never shifts unless something on it is edited. Edits no piece explains (a test bench,
    /// or lift earthwork before its contributor exists) are kept as they are, laid last.
    ///
    /// Holds the pieces' last writes as a cache (derived, never saved), one for each set of committed edits the history
    /// can go back to (task P3-11: <see cref="Follow"/>); thread-safe (one rebuild at a time, readers never wait).
    /// </summary>
    public sealed class DesignEarthwork
    {
        static readonly List<IEarthworkContributor> Registered = new List<IEarthworkContributor> { new TrailEarthwork(), new LiftEarthwork(), new BrushEarthwork() };

        /// <summary>Adds a contributor (task 09's lifts). Returns a handle that removes it.</summary>
        public static IDisposable Register(IEarthworkContributor contributor)
        {
            lock (Registered) Registered.Add(contributor ?? throw new ArgumentNullException(nameof(contributor)));
            return new Unregister(contributor);
        }

        sealed class Unregister : IDisposable
        {
            IEarthworkContributor _c;
            public Unregister(IEarthworkContributor c) { _c = c; }
            public void Dispose() { if (_c != null) lock (Registered) Registered.Remove(_c); _c = null; }
        }

        internal sealed class Entry
        {
            public IEarthworkItem Item;
            public EarthworkWrites Writes;
            public object Detail;
        }

        /// <summary>
        /// The pieces' last writes for one set of committed edits (<see cref="Key"/>), never changed once published: a
        /// rebuild works from the state it started with, and readers never wait behind a fold.
        /// </summary>
        internal sealed class State
        {
            public readonly Dictionary<ulong, Entry> Cache;
            public readonly long Version;
            public readonly TerrainEdits Key;
            public State(Dictionary<ulong, Entry> cache, long version, TerrainEdits key) { Cache = cache; Version = version; Key = key; }
        }

        // The edit sets the history can go back or forward to keep their own state (task P3-11: undo and redo put back
        // the very edits object, so a preview after an undo starts from the pieces that made it). About the history's depth.
        const int Remembered = DesignHistory.UndoDepth * 2 + 2;

        readonly IHeightSource _source;
        readonly EarthworkSite _site;
        readonly object _gate = new object();   // one fold at a time
        readonly object _stateGate = new object();   // publishing a state, never held through a fold
        volatile State _state;
        readonly Dictionary<TerrainEdits, Dictionary<ulong, Entry>> _remembered = new Dictionary<TerrainEdits, Dictionary<ulong, Entry>>(SameObject.Instance);
        readonly Queue<TerrainEdits> _rememberedOrder = new Queue<TerrainEdits>();
        // The design the history moved to when no state was known for its edits (an accept may still come): what a later
        // rebuild learns afresh if none does.
        DesignDocument _followDoc;
        TerrainEdits _followTerrain;
        // The state a follow replaced: an accept made from it still lands when the follow put back the very edits it
        // accepts (two designs can share one set: the empty one).
        State _followedFrom;

        public DesignEarthwork(IHeightSource source, EarthworkSite site)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _site = site;
        }

        public EarthworkSite Site => _site;
        public bool Primed => _state != null;

        /// <summary>
        /// Called on the folding thread as each piece starts (true) and finishes (false) grading, with its key and phase
        /// (task P3-11: the edit bench's split of a preview; nothing here times anything).
        /// </summary>
        public Action<ulong, int, bool> Grading;
        /// <summary>Called on the folding thread as each stage of a rebuild ends ("collect", "scan", "open", "fold", "with",
        /// "changed"), for the edit bench's split (task P3-11); nothing here times anything.</summary>
        public Action<string> Stage;

        sealed class SameObject : IEqualityComparer<TerrainEdits>
        {
            public static readonly SameObject Instance = new SameObject();
            public bool Equals(TerrainEdits a, TerrainEdits b) => ReferenceEquals(a, b);
            public int GetHashCode(TerrainEdits e) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e);
        }

        // Under _stateGate: the new state, remembered for its edits (the latest wins when two designs share one set, as the
        // empty set is shared).
        void Publish(Dictionary<ulong, Entry> cache, TerrainEdits key)
        {
            _state = new State(cache, (_state?.Version ?? 0) + 1, key);
            if (!_remembered.ContainsKey(key)) _rememberedOrder.Enqueue(key);
            _remembered[key] = cache;
            while (_rememberedOrder.Count > Remembered) _remembered.Remove(_rememberedOrder.Dequeue());
        }

        /// <summary>
        /// The history moved to <paramref name="doc"/> with <paramref name="terrain"/> (TerrainEditSession, on every change):
        /// an undo or redo puts back the state those edits were made with. For edits it has no state for yet, nothing
        /// changes: a command's accept follows its submit (the history raises the change first); if none does, the next
        /// rebuild learns <paramref name="doc"/> afresh.
        /// </summary>
        public void Follow(DesignDocument doc, TerrainEdits terrain)
        {
            if (doc == null || terrain == null) return;
            lock (_stateGate)
            {
                var s = _state;
                if (s == null) return;
                if (!ReferenceEquals(s.Key, terrain))
                {
                    if (!_remembered.TryGetValue(terrain, out var cache))
                    {
                        _followDoc = doc;
                        _followTerrain = terrain;
                        return;
                    }
                    _followedFrom = s;
                    Publish(cache, terrain);
                }
                _followDoc = null;
                _followTerrain = null;
            }
        }

        // Inside _gate: the state that explains the committed edits. A remembered one, else the design learnt afresh when
        // the history said which it is (Follow), else the latest (a caller that never follows: as before task P3-11).
        State StateFor(TerrainEdits committed)
        {
            DesignDocument relearn;
            lock (_stateGate)
            {
                var s = _state;
                if (s == null) throw new InvalidOperationException("Prime the earthwork with the committed design first.");
                if (ReferenceEquals(s.Key, committed)) return s;
                if (_remembered.TryGetValue(committed, out var cache)) { Publish(cache, committed); return _state; }
                if (!ReferenceEquals(_followTerrain, committed)) return s;
                relearn = _followDoc;
            }
            var learnt = Learn(relearn, committed);
            lock (_stateGate)
            {
                Publish(learnt, committed);
                if (ReferenceEquals(_followTerrain, committed)) { _followDoc = null; _followTerrain = null; }
                return _state;
            }
        }

        // Every built piece of doc graded in order from the source over the tiles they reach (Prime's fold).
        Dictionary<ulong, Entry> Learn(DesignDocument doc, TerrainEdits committed)
        {
            var items = Collect(doc);
            var tiles = new HashSet<EditTileKey>();
            foreach (var it in items) foreach (var k in it.Tiles) tiles.Add(k);
            var overlay = new Dictionary<ulong, Entry>();
            var none = new Dictionary<ulong, Entry>();
            Fold(items, committed ?? TerrainEdits.Empty, tiles, new HashSet<ulong>(), new List<Entry>(), overlay, none, foreign: false, out _);
            return overlay;
        }

        /// <summary>
        /// The result of a rebuild. Its pieces' new writes are remembered only once <see cref="Accept"/> takes it (the
        /// command went in), so a cancelled preview leaves the earthwork as it was.
        /// </summary>
        public sealed class Result
        {
            public TerrainEdits Edits { get; internal set; }
            /// <summary>The tiles that were rebuilt (a superset of those that changed).</summary>
            public IReadOnlyList<EditTileKey> Tiles { get; internal set; }
            /// <summary>The tiles whose content differs from the committed edits (what pending contours redraw).</summary>
            public IReadOnlyList<EditTileKey> Changed { get; internal set; }
            /// <summary>How many pieces were graded again.</summary>
            public int Regraded { get; internal set; }
            internal Dictionary<ulong, Entry> Overlay;
            internal long Version;
            internal State Base;
            internal TerrainEdits From;   // the committed edits it was rebuilt from (a seed's validity, round 4 PR C)

            /// <summary>What a piece's grade handed back in this rebuild (a trail's stations and volumes), or null. A part
            /// of a split chain (NT2) gets its share of the chain's detail.</summary>
            public object DetailOf(ulong key) => Overlay == null ? null : Find(Overlay, key);
            /// <summary>A piece's writes in this rebuild, when it was graded again.</summary>
            public EarthworkWrites WritesOf(ulong key) => Overlay != null && Overlay.TryGetValue(key, out var e) ? e?.Writes : null;
        }

        /// <summary>
        /// Every built piece of <paramref name="doc"/> graded afresh from the untouched ground, in order (save v8, sections
        /// finding 7: an older save's trails once more, as this build grades them). It reads nothing of any earlier edits, so
        /// the same design on the same ground always gives the same edits.
        /// </summary>
        public static TerrainEdits Regrade(IHeightSource source, EarthworkSite site, DesignDocument doc)
        {
            var fresh = new DesignEarthwork(source, site);
            fresh.Prime(DesignDocument.Empty(doc.Seed), TerrainEdits.Empty);
            return fresh.Rebuild(doc, TerrainEdits.Empty).Edits;
        }

        /// <summary>
        /// Learns a design and its committed edits (a load or a new session): grades every built piece in order from the
        /// source. Changes no edits.
        /// </summary>
        public void Prime(DesignDocument doc, TerrainEdits committed)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            committed = committed ?? TerrainEdits.Empty;
            lock (_gate)
            {
                var learnt = Learn(doc, committed);
                lock (_stateGate)
                {
                    _remembered.Clear();
                    _rememberedOrder.Clear();
                    _followedFrom = null;
                    // A change the history made while this learnt (a background prime) stays to be followed.
                    if (ReferenceEquals(_followTerrain, committed)) { _followDoc = null; _followTerrain = null; }
                    Publish(learnt, committed);
                }
            }
        }

        /// <summary>
        /// The terrain edits for <paramref name="doc"/>, from <paramref name="committed"/> (the history's current edits):
        /// rebuilds the tiles that can have changed. What the committed edits hold there that no piece explains (work from
        /// before a contributor existed) is kept as it is, laid last.
        /// <paramref name="seed"/> (task 15 round 4, PR C: a brush session folded again after each press): an earlier,
        /// unaccepted rebuild from the same committed edits and the same accepted state; its pieces still the same here are
        /// laid from it instead of graded again, so only what changed since is folded. The result is the same as without it.
        /// A seed from another state or other committed edits is ignored.
        /// </summary>
        public Result Rebuild(DesignDocument doc, TerrainEdits committed, Result seed = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            committed = committed ?? TerrainEdits.Empty;
            lock (_gate)
            {
                var state = StateFor(committed);
                var cache = state.Cache;
                var items = Collect(doc);
                Stage?.Invoke("collect");
                var present = new HashSet<ulong>();
                foreach (var it in items) present.Add(it.Key);
                var tiles = new HashSet<EditTileKey>();
                var dirty = new HashSet<ulong>();
                var overlay = new Dictionary<ulong, Entry>();
                // The seed (PR C): an earlier rebuild from this state and these committed edits. Its pieces are what that fold
                // laid, so they stand in for the remembered ones (Known reads the overlay first) and the fold re-grades from
                // there by the same tile rule. Its tiles open, since the committed edits hold the remembered writes, not its own.
                bool seeded = seed?.Overlay != null && ReferenceEquals(seed.Base, state) && ReferenceEquals(seed.From, committed);
                if (seeded)
                    foreach (var kv in seed.Overlay)
                    {
                        overlay[kv.Key] = kv.Value;
                        if (kv.Value != null) foreach (var t in kv.Value.Writes.Tiles) tiles.Add(t);
                        if (cache.TryGetValue(kv.Key, out var was)) foreach (var t in was.Writes.Tiles) tiles.Add(t);
                    }
                // Gone: their old writes come off (a change from their own place in the order on), the seed's as well.
                var gone = new List<Entry>();
                foreach (var kv in cache) if (!present.Contains(kv.Key)) gone.Add(kv.Value);
                if (seeded)
                    foreach (var kv in seed.Overlay)
                        if (kv.Value != null && !present.Contains(kv.Key) && !(cache.TryGetValue(kv.Key, out var was) && ReferenceEquals(was, kv.Value)))
                            gone.Add(kv.Value);
                gone.Sort((a, b) => FoldOrder(a.Item, b.Item));
                foreach (var e in gone)
                {
                    foreach (var t in e.Writes.Tiles) tiles.Add(t);
                    overlay[e.Item.Key] = null;
                }
                // New or changed: their old writes and everything they read.
                foreach (var it in items)
                {
                    var e = Known(it.Key, overlay, cache);
                    if (e != null && e.Item.SameAs(it)) continue;
                    dirty.Add(it.Key);
                    if (e != null) foreach (var t in e.Writes.Tiles) tiles.Add(t);
                    foreach (var t in it.Tiles) tiles.Add(t);
                }
                Stage?.Invoke("scan");
                var edits = Fold(items, committed, tiles, dirty, gone, overlay, cache, foreign: true, out int regraded);
                var list = new List<EditTileKey>(tiles);
                list.Sort();
                var changedTiles = TerrainEdits.Changed(committed, edits);
                Stage?.Invoke("changed");
                return new Result { Edits = edits, Tiles = list, Changed = changedTiles, Regraded = regraded, Overlay = overlay, Version = state.Version, Base = state, From = committed };
            }
        }

        /// <summary>
        /// Remembers a rebuild's writes once its command went in, as the state of <paramref name="committedAs"/> (the edits
        /// the history now holds: the result's own, or the same ground as another set, as the brush confirms the set its
        /// map shows). A result from before another accept, prime or undo is ignored (the earthwork stays correct, only
        /// slower next time). Returns whether it was taken.
        /// </summary>
        public bool Accept(Result result, TerrainEdits committedAs = null)
        {
            if (result?.Overlay == null) return false;
            lock (_stateGate)
            {
                var s = _state;
                var key = committedAs ?? result.Edits;
                if (s == null || result.Base == null) return false;
                if (!ReferenceEquals(result.Base, s) && !(ReferenceEquals(result.Base, _followedFrom) && ReferenceEquals(s.Key, key))) return false;
                var cache = new Dictionary<ulong, Entry>(result.Base.Cache);
                foreach (var kv in result.Overlay)
                {
                    if (kv.Value == null) cache.Remove(kv.Key);
                    else cache[kv.Key] = kv.Value;
                }
                _followedFrom = null;
                Publish(cache, key);
                if (ReferenceEquals(_followTerrain, key)) { _followDoc = null; _followTerrain = null; }
                return true;
            }
        }

        /// <summary>What a piece's last accepted grade handed back, or null.</summary>
        public object DetailOf(ulong key)
        {
            var s = _state;
            return s == null ? null : Find(s.Cache, key);
        }

        /// <summary>A piece's detail, or its share of a grouped one (NT2: a split chain graded under its top part's key).</summary>
        static object Find(Dictionary<ulong, Entry> entries, ulong key)
        {
            if (entries.TryGetValue(key, out var e))
                return e?.Detail is IGroupedDetail own ? own.DetailFor(key) : e?.Detail;
            foreach (var other in entries.Values)
                if (other?.Detail is IGroupedDetail g && g.DetailFor(key) is object d) return d;
            return null;
        }

        /// <summary>A piece's last accepted writes, or null (tests, the trail map).</summary>
        public EarthworkWrites WritesOf(ulong key)
        {
            var s = _state;
            return s != null && s.Cache.TryGetValue(key, out var e) ? e.Writes : null;
        }

        List<IEarthworkItem> Collect(DesignDocument doc)
        {
            var items = new List<IEarthworkItem>();
            lock (Registered) foreach (var c in Registered) c.Collect(doc, _site, items);
            items.Sort(FoldOrder);
            return items;
        }

        static int FoldOrder(IEarthworkItem a, IEarthworkItem b) =>
            a.Phase != b.Phase ? a.Phase.CompareTo(b.Phase) : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Key.CompareTo(b.Key);

        static Entry Known(ulong key, Dictionary<ulong, Entry> overlay, Dictionary<ulong, Entry> cache)
        {
            if (overlay.TryGetValue(key, out var e)) return e;
            return cache.TryGetValue(key, out e) ? e : null;
        }

        static bool Meets(IReadOnlyCollection<EditTileKey> tiles, HashSet<EditTileKey> changed)
        {
            if (changed.Count == 0) return false;
            foreach (var t in tiles) if (changed.Contains(t)) return true;
            return false;
        }

        static bool ReadsGraded(IEarthworkItem it, HashSet<ulong> graded)
        {
            if (graded.Count == 0 || !(it is IReadsDetails reads)) return false;
            foreach (ulong k in reads.DetailsRead) if (graded.Contains(k)) return true;
            return false;
        }

        /// <summary>
        /// Folds the pieces in order over the source on <paramref name="tiles"/> (which grows as later pieces need
        /// regrading), the committed edits elsewhere. Dirty pieces are graded again, and so is any later piece whose tiles
        /// hold a change so far (task P3-11: tiles, not one growing box; a gone piece's writes count from its own place, as
        /// the pieces before it never read them) or that reads a regraded piece's detail; the rest lay their remembered
        /// writes. New writes go to <paramref name="overlay"/>.
        /// </summary>
        TerrainEdits Fold(List<IEarthworkItem> items, TerrainEdits committed, HashSet<EditTileKey> tiles, HashSet<ulong> dirty,
                          List<Entry> gone, Dictionary<ulong, Entry> overlay, Dictionary<ulong, Entry> cache, bool foreign, out int regraded)
        {
            regraded = 0;
            var changed = new HashSet<EditTileKey>();
            var graded = new HashSet<ulong>();
            int g = 0;
            var work = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            var claims = new Dictionary<EditTileKey, ulong[]>();
            var foreignTiles = new Dictionary<EditTileKey, TerrainEditTile>();
            void Open(EditTileKey t)
            {
                work[t] = new TerrainEditTile.Builder(t);
                claims[t] = new ulong[EditTileKey.Count / 64];
                tiles.Add(t);
                if (foreign) foreignTiles[t] = Foreign(committed, t, cache);
            }
            foreach (var t in new List<EditTileKey>(tiles)) Open(t);
            Stage?.Invoke("open");
            var done = new List<(IEarthworkItem item, EarthworkWrites writes)>();
            var context = new EarthworkContext(claims, key => Known(key, overlay, cache)?.Detail);
            // The ground pieces read: the committed edits with every work tile as folded so far. Only tiles opened or laid
            // since the last build are rebuilt (task 15: hundreds of small strokes made rebuilding every tile per piece slow);
            // the content is the same as building them all.
            var folded = committed;
            var stale = new SortedSet<EditTileKey>(work.Keys);

            foreach (var it in items)
            {
                for (; g < gone.Count && FoldOrder(gone[g].Item, it) < 0; g++) EarthworkWrites.DifferentTiles(gone[g].Writes, null, changed);
                var known = Known(it.Key, overlay, cache);
                bool regrade = dirty.Contains(it.Key) || known == null || Meets(it is IReachTiles reach ? reach.ReachTiles : it.Tiles, changed) || ReadsGraded(it, graded);
                EarthworkWrites writes;
                if (regrade)
                {
                    // Everything it reads must be folded up to here.
                    var fresh = new List<EditTileKey>();
                    foreach (var t in it.Tiles)
                        if (!work.ContainsKey(t)) { Open(t); fresh.Add(t); stale.Add(t); }
                    foreach (var t in fresh)
                        foreach (var (pi, pw) in done) Lay(pi, pw, t, work, claims);
                    if (stale.Count > 0)
                    {
                        var built = new List<TerrainEditTile>(stale.Count);
                        foreach (var t in stale) built.Add(work[t].Build());
                        stale.Clear();
                        folded = folded.With(built);
                    }
                    var ground = new EditedHeights(_source, _site.Lattice, folded);
                    Grading?.Invoke(it.Key, it.Phase, true);
                    writes = it.Build(ground, context, out object detail);
                    Grading?.Invoke(it.Key, it.Phase, false);
                    EarthworkWrites.DifferentTiles(known?.Writes, writes, changed);
                    overlay[it.Key] = new Entry { Item = it, Writes = writes, Detail = detail };
                    graded.Add(it.Key);
                    regraded++;
                }
                else writes = known.Writes;
                foreach (var t in writes.Tiles)
                    if (work.ContainsKey(t)) { Lay(it, writes, t, work, claims); stale.Add(t); }
                done.Add((it, writes));
            }
            Stage?.Invoke("fold");
            var result = new List<TerrainEditTile>(work.Count);
            foreach (var kv in work)
            {
                if (foreignTiles.TryGetValue(kv.Key, out var f) && f != null) LayForeign(f, kv.Value);
                result.Add(kv.Value.Build());
            }
            var all = committed.With(result);
            Stage?.Invoke("with");
            return all;
        }

        /// <summary>
        /// The committed tile's changes that no remembered piece explains: heights on points none claims, cleared cells none
        /// clears. Null when there are none.
        /// </summary>
        static TerrainEditTile Foreign(TerrainEdits committed, EditTileKey key, Dictionary<ulong, Entry> cache)
        {
            if (!committed.TryGet(key, out var tile) || tile.IsEmpty) return null;
            var claimed = new ulong[EditTileKey.Count / 64];
            var clears = new ulong[EditTileKey.Count / 64];
            foreach (var e in cache.Values) { e.Writes.OrClaims(key, claimed); e.Writes.OrClears(key, clears); }
            TerrainEditTile.Builder b = null;
            for (int j = 0; j < EditTileKey.Side; j++)
                for (int i = 0; i < EditTileKey.Side; i++)
                {
                    int at = j * EditTileKey.Side + i;
                    ulong bit = 1UL << (at & 63);
                    int d = tile.DeltaMm(i, j);
                    if (d != 0 && (claimed[at >> 6] & bit) == 0) (b = b ?? new TerrainEditTile.Builder(key)).SetDeltaMm(i, j, d);
                    if (tile.IsCleared(i, j) && (clears[at >> 6] & bit) == 0) (b = b ?? new TerrainEditTile.Builder(key)).SetCleared(i, j, true);
                }
            return b?.Build();
        }

        static void LayForeign(TerrainEditTile f, TerrainEditTile.Builder into)
        {
            for (int j = 0; j < EditTileKey.Side; j++)
                for (int i = 0; i < EditTileKey.Side; i++)
                {
                    int d = f.DeltaMm(i, j);
                    if (d != 0) into.SetDeltaMm(i, j, d);
                    if (f.IsCleared(i, j)) into.SetCleared(i, j, true);
                }
        }

        static void Lay(IEarthworkItem item, EarthworkWrites writes, EditTileKey tile, SortedDictionary<EditTileKey, TerrainEditTile.Builder> work,
                        Dictionary<EditTileKey, ulong[]> claims)
        {
            writes.ApplyTo(work[tile]);
            if (item.Phase == 0) writes.OrClaims(tile, claims[tile]);
        }
    }
}
