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
        /// <summary>The fold's order: every trail (0) before every lift (1); within a phase, <see cref="Order"/>.</summary>
        int Phase { get; }
        /// <summary>Creation order within the phase (the document ordinal the ID was minted at).</summary>
        long Order { get; }
        /// <summary>Every lattice point it may read or write; its writes stay inside.</summary>
        PointRect Reads { get; }
        /// <summary>Whether its writes would be the same as <paramref name="other"/>'s on the same ground.</summary>
        bool SameAs(IEarthworkItem other);
        /// <summary>
        /// Its writes over <paramref name="ground"/>, the source plus every earlier piece's writes (inside
        /// <see cref="Reads"/>). <paramref name="detail"/> is anything a tool wants back (a trail's stations, volumes).
        /// </summary>
        EarthworkWrites Build(EditedHeights ground, EarthworkContext context, out object detail);
    }

    /// <summary>Gathers a design's built pieces of one kind. Task 08 registers trails; task 09 registers lifts.</summary>
    public interface IEarthworkContributor
    {
        void Collect(DesignDocument doc, EarthworkSite site, List<IEarthworkItem> into);
    }

    /// <summary>Where the design sits on the lattice: the local frame's origin (TE5, the site's centre) in the lattice's frame.</summary>
    public readonly struct EarthworkSite
    {
        public readonly EditLattice Lattice;
        public readonly double OriginX, OriginY;
        public EarthworkSite(EditLattice lattice, double originX, double originY) { Lattice = lattice; OriginX = originX; OriginY = originY; }

        public void PointOf(PointMm p, out double x, out double y) { x = OriginX + p.XMetres; y = OriginY + p.YMetres; }
    }

    /// <summary>What a piece may ask about the fold so far.</summary>
    public sealed class EarthworkContext
    {
        readonly Dictionary<EditTileKey, ulong[]> _claims;
        internal EarthworkContext(Dictionary<EditTileKey, ulong[]> claims) { _claims = claims; }

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
    /// source heights, trails by creation, then lifts. After a command, only what can have changed is rebuilt: the tiles
    /// of pieces that are new, changed or gone, and of later pieces whose ground those changed, each later piece
    /// regraded only when the writes under it moved. Points outside the rebuilt tiles keep the committed edits exactly,
    /// so a saved design's ground never shifts unless something on it is edited. Edits no piece explains (a test bench,
    /// or lift earthwork before its contributor exists) are kept as they are, laid last.
    ///
    /// Holds the pieces' last writes as a cache (derived, never saved); thread-safe (one rebuild at a time).
    /// </summary>
    public sealed class DesignEarthwork
    {
        static readonly List<IEarthworkContributor> Registered = new List<IEarthworkContributor> { new TrailEarthwork() };

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

        sealed class Entry
        {
            public IEarthworkItem Item;
            public EarthworkWrites Writes;
            public object Detail;
        }

        readonly IHeightSource _source;
        readonly EarthworkSite _site;
        readonly Dictionary<ulong, Entry> _cache = new Dictionary<ulong, Entry>();
        readonly object _gate = new object();
        EarthworkWrites _foreign = new EarthworkWrites();
        bool _primed;

        public DesignEarthwork(IHeightSource source, EarthworkSite site)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _site = site;
        }

        public EarthworkSite Site => _site;
        public bool Primed { get { lock (_gate) return _primed; } }

        /// <summary>The result of a rebuild.</summary>
        public sealed class Result
        {
            public TerrainEdits Edits { get; internal set; }
            /// <summary>The tiles that were rebuilt (a superset of those that changed).</summary>
            public IReadOnlyList<EditTileKey> Tiles { get; internal set; }
            /// <summary>How many pieces were graded again.</summary>
            public int Regraded { get; internal set; }
        }

        /// <summary>
        /// Learns a design and its committed edits (a load or a new session): grades every built piece in order from the
        /// source, and keeps whatever the committed edits hold that no piece explains. Changes nothing.
        /// </summary>
        public void Prime(DesignDocument doc, TerrainEdits committed)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            committed = committed ?? TerrainEdits.Empty;
            lock (_gate)
            {
                _cache.Clear();
                _foreign = new EarthworkWrites();
                _primed = false;
                var items = Collect(doc);
                var tiles = new HashSet<EditTileKey>();
                foreach (var it in items) foreach (var k in it.Reads.TileKeys()) tiles.Add(k);
                Fold(items, committed, tiles, new HashSet<ulong>(), PointRect.Empty, out _);
                // Foreign: committed changes on points no piece claims, and cleared cells no piece clears.
                foreach (var tile in committed.Tiles)
                {
                    var key = tile.Key;
                    var claimed = new ulong[EditTileKey.Count / 64];
                    var clears = new ulong[EditTileKey.Count / 64];
                    foreach (var e in _cache.Values) { e.Writes.OrClaims(key, claimed); e.Writes.OrClears(key, clears); }
                    for (int j = 0; j < EditTileKey.Side; j++)
                        for (int i = 0; i < EditTileKey.Side; i++)
                        {
                            int at = j * EditTileKey.Side + i;
                            ulong bit = 1UL << (at & 63);
                            int d = tile.DeltaMm(i, j);
                            if (d != 0 && (claimed[at >> 6] & bit) == 0) _foreign.Write(key.FirstColumn + i, key.FirstRow + j, d);
                            if (tile.IsCleared(i, j) && (clears[at >> 6] & bit) == 0) _foreign.Clear(key.FirstColumn + i, key.FirstRow + j);
                        }
                }
                _primed = true;
            }
        }

        /// <summary>
        /// The terrain edits for <paramref name="doc"/>, from <paramref name="committed"/> (the history's current edits):
        /// rebuilds the tiles that can have changed. Primes first if it hasn't.
        /// </summary>
        public Result Rebuild(DesignDocument doc, TerrainEdits committed)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            committed = committed ?? TerrainEdits.Empty;
            lock (_gate)
            {
                if (!_primed) throw new InvalidOperationException("Prime the earthwork with the committed design first.");
                var items = Collect(doc);
                var present = new HashSet<ulong>();
                foreach (var it in items) present.Add(it.Key);
                var tiles = new HashSet<EditTileKey>();
                var changed = PointRect.Empty;
                var dirty = new HashSet<ulong>();
                // Gone: their old writes come off.
                var gone = new List<ulong>();
                foreach (var kv in _cache) if (!present.Contains(kv.Key)) gone.Add(kv.Key);
                gone.Sort();
                foreach (var k in gone)
                {
                    var old = _cache[k].Writes;
                    foreach (var t in old.Tiles) tiles.Add(t);
                    changed = changed.Union(old.Bounds);
                    _cache.Remove(k);
                }
                // New or changed: their old writes and everything they read.
                foreach (var it in items)
                {
                    if (_cache.TryGetValue(it.Key, out var e) && e.Item.SameAs(it)) continue;
                    dirty.Add(it.Key);
                    if (e != null) foreach (var t in e.Writes.Tiles) tiles.Add(t);
                    foreach (var t in it.Reads.TileKeys()) tiles.Add(t);
                }
                var edits = Fold(items, committed, tiles, dirty, changed, out int regraded);
                var list = new List<EditTileKey>(tiles);
                list.Sort();
                return new Result { Edits = edits, Tiles = list, Regraded = regraded };
            }
        }

        /// <summary>What a piece's last grade handed back (a trail's stations and volumes), or null.</summary>
        public object DetailOf(ulong key)
        {
            lock (_gate) return _cache.TryGetValue(key, out var e) ? e.Detail : null;
        }

        /// <summary>A piece's last writes, or null (tests, the trail map).</summary>
        public EarthworkWrites WritesOf(ulong key)
        {
            lock (_gate) return _cache.TryGetValue(key, out var e) ? e.Writes : null;
        }

        List<IEarthworkItem> Collect(DesignDocument doc)
        {
            var items = new List<IEarthworkItem>();
            lock (Registered) foreach (var c in Registered) c.Collect(doc, _site, items);
            items.Sort((a, b) => a.Phase != b.Phase ? a.Phase.CompareTo(b.Phase) : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Key.CompareTo(b.Key));
            return items;
        }

        /// <summary>
        /// Folds the pieces in order over the source on <paramref name="tiles"/> (which grows as later pieces need
        /// regrading), the committed edits elsewhere. Dirty pieces are graded again, and so is any later piece whose reads
        /// meet a change; the rest lay their cached writes.
        /// </summary>
        TerrainEdits Fold(List<IEarthworkItem> items, TerrainEdits committed, HashSet<EditTileKey> tiles, HashSet<ulong> dirty,
                          PointRect changed, out int regraded)
        {
            regraded = 0;
            var work = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            var claims = new Dictionary<EditTileKey, ulong[]>();
            foreach (var t in tiles) { work[t] = new TerrainEditTile.Builder(t); claims[t] = new ulong[EditTileKey.Count / 64]; }
            var done = new List<(IEarthworkItem item, EarthworkWrites writes)>();
            var context = new EarthworkContext(claims);

            foreach (var it in items)
            {
                bool regrade = dirty.Contains(it.Key) || !_cache.ContainsKey(it.Key) || it.Reads.Intersects(changed);
                EarthworkWrites writes;
                if (regrade)
                {
                    // Everything it reads must be folded up to here.
                    var fresh = new List<EditTileKey>();
                    foreach (var t in it.Reads.TileKeys())
                        if (!work.ContainsKey(t)) { work[t] = new TerrainEditTile.Builder(t); claims[t] = new ulong[EditTileKey.Count / 64]; tiles.Add(t); fresh.Add(t); }
                    foreach (var t in fresh)
                        foreach (var (pi, pw) in done) Lay(pi, pw, t, work, claims);
                    var built = new List<TerrainEditTile>(work.Count);
                    foreach (var b in work.Values) built.Add(b.Build());
                    var ground = new EditedHeights(_source, _site.Lattice, committed.With(built));
                    writes = it.Build(ground, context, out object detail);
                    _cache.TryGetValue(it.Key, out var old);
                    changed = changed.Union(EarthworkWrites.Difference(old?.Writes, writes));
                    _cache[it.Key] = new Entry { Item = it, Writes = writes, Detail = detail };
                    regraded++;
                }
                else writes = _cache[it.Key].Writes;
                foreach (var t in writes.Tiles)
                    if (work.ContainsKey(t)) Lay(it, writes, t, work, claims);
                done.Add((it, writes));
            }
            foreach (var t in work.Keys) _foreign.ApplyTo(work[t]);
            var result = new List<TerrainEditTile>(work.Count);
            foreach (var b in work.Values) result.Add(b.Build());
            return committed.With(result);
        }

        static void Lay(IEarthworkItem item, EarthworkWrites writes, EditTileKey tile, SortedDictionary<EditTileKey, TerrainEditTile.Builder> work,
                        Dictionary<EditTileKey, ulong[]> claims)
        {
            writes.ApplyTo(work[tile]);
            if (item.Phase == 0) writes.OrClaims(tile, claims[tile]);
        }
    }
}
