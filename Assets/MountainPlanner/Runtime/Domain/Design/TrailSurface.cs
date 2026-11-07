using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The numbers behind the look of built trails (task 07, SK3; the terrain shader's trail map), per lattice point
    /// round the built trails: the footprint's cover, the height change (edited minus natural), graded or not, the offset
    /// across the nearest trail and the edited ground's slope across it. Display data, never stored and never read by a rule.
    /// Task P3-11: it keeps its fields between updates and works out again only the points an update can have changed
    /// (the trails that changed, old place and new, and the ground that moved), each point exactly as a whole redraw
    /// would; each trail is located only over its own reach.
    /// Task 4.01 (TL24): the fields are kept in <see cref="Chunk"/>s, one per 64 m lattice tile a trail reaches, wherever the
    /// trails are, so a resort of any size has all its trails drawn (the old single window stopped 2048 points from its
    /// north-west corner). A point outside every chunk is zero: no trail reaches it.
    /// </summary>
    public sealed class TrailSurface
    {
        /// <summary>Points a chunk side (one lattice tile).</summary>
        public const int Side = EditTileKey.Side;

        /// <summary>A built trail as the look draws it: its automatic centreline (x, y pairs), reach, footprint and grading.</summary>
        public readonly struct Shape
        {
            public readonly Trail Trail;
            public readonly double[] Line;
            public readonly TrailFootprint Footprint;
            public readonly bool Graded;
            public double Half => Footprint.MaxClearance * 1.5 + 3;

            public Shape(Trail trail, double[] line, TrailFootprint footprint, bool graded)
            {
                Trail = trail;
                Line = line;
                Footprint = footprint;
                Graded = graded;
            }
        }

        /// <summary>A block of lattice points, inclusive (columns east, rows south).</summary>
        public readonly struct Block
        {
            public readonly int I0, J0, I1, J1;
            public Block(int i0, int j0, int i1, int j1) { I0 = i0; J0 = j0; I1 = i1; J1 = j1; }
            public bool IsEmpty => I1 < I0 || J1 < J0;
            public Block Grown(int by) => new Block(I0 - by, J0 - by, I1 + by, J1 + by);
            public Block Meet(Block o) => new Block(Math.Max(I0, o.I0), Math.Max(J0, o.J0), Math.Min(I1, o.I1), Math.Min(J1, o.J1));
            public Block Join(Block o) => IsEmpty ? o : o.IsEmpty ? this : new Block(Math.Min(I0, o.I0), Math.Min(J0, o.J0), Math.Max(I1, o.I1), Math.Max(J1, o.J1));
            public static Block Of(EditTileKey key) => new Block(key.FirstColumn, key.FirstRow, key.FirstColumn + Side - 1, key.FirstRow + Side - 1);
            public override string ToString() => $"[{I0}..{I1}] x [{J0}..{J1}]";
        }

        /// <summary>One lattice tile's fields, <see cref="Side"/> × <see cref="Side"/>, rows from the north.</summary>
        public sealed class Chunk
        {
            public readonly EditTileKey Key;
            public readonly float[] Foot = new float[Side * Side], Change = new float[Side * Side], Graded = new float[Side * Side],
                                    Offset = new float[Side * Side], Across = new float[Side * Side];
            // Whether a trail located the point (its nearest distance is set): the slope is worked out there too.
            internal readonly bool[] Near = new bool[Side * Side];
            internal readonly double[] Nearest = new double[Side * Side];
            public Chunk(EditTileKey key) => Key = key;
        }

        /// <summary>The chunks, by tile.</summary>
        public IReadOnlyDictionary<EditTileKey, Chunk> Chunks => _chunks;
        public int Count => _shapes.Count;

        /// <summary>The blocks of lattice points the last update worked out again, each inside one chunk.</summary>
        public IReadOnlyList<Block> Redrawn => _redrawn;
        /// <summary>Whether the last update added or removed chunks.</summary>
        public bool ChunksChanged { get; private set; }
        /// <summary>Lattice points the last update worked out again.</summary>
        public long PointsRedrawn { get; private set; }

        readonly Dictionary<EditTileKey, Chunk> _chunks = new Dictionary<EditTileKey, Chunk>();
        readonly List<Shape> _shapes = new List<Shape>();
        readonly List<Block> _redrawn = new List<Block>();
        TerrainEdits _drawnOver;

        /// <summary>The point's fields: false (and zeros) away from every chunk.</summary>
        public bool TryAt(int column, int row, out Chunk chunk, out int index)
        {
            index = 0;
            if (!_chunks.TryGetValue(EditTileKey.Of(column, row), out chunk)) return false;
            index = (row - chunk.Key.FirstRow) * Side + column - chunk.Key.FirstColumn;
            return true;
        }

        /// <summary>
        /// Updates the fields for <paramref name="shapes"/> (the built trails with a centreline, in the design's order)
        /// over <paramref name="edited"/>. Returns false when there's nothing to show.
        /// </summary>
        public bool Update(IReadOnlyList<Shape> shapes, EditedHeights edited)
        {
            _redrawn.Clear();
            PointsRedrawn = 0;
            ChunksChanged = false;
            var lattice = edited.Lattice;
            if (shapes.Count == 0)
            {
                ChunksChanged = _chunks.Count != 0;
                _chunks.Clear();
                _shapes.Clear();
                _reach.Clear();
                _along.Clear();
                _drawnOver = edited.Edits;
                return false;
            }
            bool fresh = _drawnOver == null || _chunks.Count == 0 || !SameOrder(shapes);
            // Where the trails that changed reach, where they were and where they are, by lattice tile.
            var tiles = new HashSet<EditTileKey>();
            if (!fresh)
            {
                var before = new Dictionary<TrailId, Shape>(_shapes.Count);
                foreach (var s in _shapes) before[s.Trail.Id] = s;
                foreach (var s in shapes)
                {
                    if (before.TryGetValue(s.Trail.Id, out var was))
                    {
                        before.Remove(s.Trail.Id);
                        if (Same(was, s)) continue;
                        Reach(was, lattice, tiles);
                    }
                    Reach(s, lattice, tiles);
                }
                foreach (var gone in before.Values) Reach(gone, lattice, tiles);
                // And the ground that moved (a trail's, a lift's or a stroke's).
                foreach (var key in TerrainEdits.Changed(_drawnOver, edited.Edits)) tiles.Add(key);
            }
            // The chunks: every tile a trail reaches.
            var want = new HashSet<EditTileKey>();
            foreach (var s in shapes) want.UnionWith(ReachOf(s, lattice));
            var dirty = new Dictionary<EditTileKey, Block>();
            var stale = new List<EditTileKey>();
            foreach (var key in _chunks.Keys) if (!want.Contains(key)) stale.Add(key);
            foreach (var key in stale) _chunks.Remove(key);
            foreach (var key in want)
            {
                if (_chunks.ContainsKey(key)) continue;
                _chunks.Add(key, new Chunk(key));
                if (!fresh) dirty[key] = Block.Of(key);
            }
            ChunksChanged = stale.Count > 0 || dirty.Count > 0 || fresh;
            _shapes.Clear();
            _shapes.AddRange(shapes);
            _drawnOver = edited.Edits;
            Prune(shapes);
            if (fresh) foreach (var key in _chunks.Keys) dirty[key] = Block.Of(key);
            else
            {
                // Each tile 3 points round for the slope's ±2 m reads, and one more: the slope across a point reads the
                // offsets either side of it.
                foreach (var key in tiles)
                {
                    var b = Block.Of(key).Grown(4);
                    var a = EditTileKey.Of(b.I0, b.J0);
                    var z = EditTileKey.Of(b.I1, b.J1);
                    for (int r = a.Row; r <= z.Row; r++)
                        for (int c = a.Column; c <= z.Column; c++)
                        {
                            var k = new EditTileKey(c, r);
                            if (!_chunks.ContainsKey(k)) continue;
                            var part = b.Meet(Block.Of(k));
                            dirty[k] = dirty.TryGetValue(k, out var had) ? had.Join(part) : part;
                        }
                }
            }
            // Every block's trails first, then every block's change and slope (which read the neighbours' offsets).
            var order = new List<EditTileKey>(dirty.Keys);
            order.Sort();
            foreach (var key in order) Locate(_chunks[key], dirty[key], lattice);
            foreach (var key in order)
            {
                var b = dirty[key];
                Shade(_chunks[key], b, edited);
                _redrawn.Add(b);
                PointsRedrawn += (long)(b.I1 - b.I0 + 1) * (b.J1 - b.J0 + 1);
            }
            return true;
        }

        void Prune(IReadOnlyList<Shape> shapes)
        {
            if (_reach.Count == 0) return;
            var now = new HashSet<double[]>();
            foreach (var s in shapes) now.Add(s.Line);
            var old = new List<double[]>();
            foreach (var k in _reach.Keys) if (!now.Contains(k)) old.Add(k);
            foreach (var k in old) { _reach.Remove(k); _along.Remove(k); }
        }

        // The lattice tiles a trail's look reaches: each centreline segment's box, widened by the reach it's located over.
        void Reach(Shape s, EditLattice lattice, HashSet<EditTileKey> into)
        {
            foreach (var key in ReachOf(s, lattice)) into.Add(key);
        }

        readonly Dictionary<double[], HashSet<EditTileKey>> _reach = new Dictionary<double[], HashSet<EditTileKey>>();
        readonly Dictionary<double[], double[]> _along = new Dictionary<double[], double[]>();

        HashSet<EditTileKey> ReachOf(Shape s, EditLattice lattice)
        {
            if (_reach.TryGetValue(s.Line, out var tiles)) return tiles;
            tiles = new HashSet<EditTileKey>();
            double reach = s.Half + 3;
            var line = s.Line;
            int n = line.Length / 2;
            for (int k = 0; k < n; k++)
            {
                int e = Math.Min(k + 1, n - 1);
                double ax = line[2 * k], ay = line[2 * k + 1], bx = line[2 * e], by = line[2 * e + 1];
                var box = new PointRect((int)Math.Floor(Math.Min(ax, bx) - reach - lattice.West), (int)Math.Floor(lattice.North - (Math.Max(ay, by) + reach)),
                                        (int)Math.Ceiling(Math.Max(ax, bx) + reach - lattice.West), (int)Math.Ceiling(lattice.North - (Math.Min(ay, by) - reach)));
                foreach (var key in box.TileKeys()) tiles.Add(key);
            }
            _reach[s.Line] = tiles;
            return tiles;
        }

        double[] AlongOf(Shape s)
        {
            if (_along.TryGetValue(s.Line, out var along)) return along;
            var line = s.Line;
            along = new double[line.Length / 2];
            for (int k = 1; k < along.Length; k++)
                along[k] = along[k - 1] + Math.Sqrt(Sq(line[2 * k] - line[2 * k - 2]) + Sq(line[2 * k + 1] - line[2 * k - 1]));
            _along[s.Line] = along;
            return along;
        }

        /// <summary>Whether a point (the lattice's coordinates, metres) is under the map: a trail reaches its tile.</summary>
        public bool Covers(EditLattice lattice, double x, double y) =>
            _chunks.ContainsKey(EditTileKey.Of((int)Math.Floor(x - lattice.West), (int)Math.Floor(lattice.North - y)));

        // The surviving trails keep their order (the nearest trail's tie goes to the first in the design's order).
        bool SameOrder(IReadOnlyList<Shape> shapes)
        {
            var now = new Dictionary<TrailId, int>(shapes.Count);
            for (int i = 0; i < shapes.Count; i++) now[shapes[i].Trail.Id] = i;
            int last = -1;
            foreach (var s in _shapes)
            {
                if (!now.TryGetValue(s.Trail.Id, out int at)) continue;
                if (at < last) return false;
                last = at;
            }
            return true;
        }

        static bool Same(Shape a, Shape b)
        {
            if (a.Graded != b.Graded || a.Footprint.MaxClearance != b.Footprint.MaxClearance) return false;
            if (!ReferenceEquals(a.Footprint, b.Footprint) && !a.Trail.Shape.Equals(b.Trail.Shape)) return false;
            if (ReferenceEquals(a.Line, b.Line)) return true;
            if (a.Line.Length != b.Line.Length) return false;
            for (int i = 0; i < a.Line.Length; i++) if (a.Line[i] != b.Line[i]) return false;
            return true;
        }

        // A trail's reach in lattice points: its centreline's box widened by the reach it's located over.
        static Block BoxOf(Shape s, EditLattice lattice)
        {
            double reach = s.Half + 2;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < s.Line.Length; i += 2)
            {
                minX = Math.Min(minX, s.Line[i]); maxX = Math.Max(maxX, s.Line[i]);
                minY = Math.Min(minY, s.Line[i + 1]); maxY = Math.Max(maxY, s.Line[i + 1]);
            }
            return new Block((int)Math.Floor(minX - reach - 1 - lattice.West), (int)Math.Floor(lattice.North - (maxY + reach + 1)),
                             (int)Math.Ceiling(maxX + reach + 1 - lattice.West), (int)Math.Ceiling(lattice.North - (minY - reach - 1)));
        }

        // Every point of the block from scratch: each trail that reaches its chunk, in order.
        void Locate(Chunk chunk, Block b, EditLattice lattice)
        {
            int fc = chunk.Key.FirstColumn, fr = chunk.Key.FirstRow;
            for (int r = b.J0; r <= b.J1; r++)
            {
                int row = (r - fr) * Side - fc;
                for (int c = b.I0; c <= b.I1; c++)
                {
                    int at = row + c;
                    chunk.Foot[at] = 0; chunk.Graded[at] = 0; chunk.Change[at] = 0; chunk.Offset[at] = 0; chunk.Across[at] = 0;
                    chunk.Near[at] = false;
                    chunk.Nearest[at] = double.MaxValue;
                }
            }
            foreach (var s in _shapes)
            {
                if (!ReachOf(s, lattice).Contains(chunk.Key)) continue;
                var part = b.Meet(BoxOf(s, lattice));
                if (part.IsEmpty) continue;
                var line = s.Line;
                double half = s.Half;
                var fp = s.Footprint;
                int lw = part.I1 - part.I0 + 1, lh = part.J1 - part.J0 + 1;
                var located = TrailGrader.Locate(line, AlongOf(s), lattice, part.I0, part.J0, lw, lh, 0, 0, half + 2);
                for (int lj = 0; lj < lh; lj++)
                    for (int li = 0; li < lw; li++)
                    {
                        double d = located.Dist[lj * lw + li];
                        if (d > half + 2) continue;
                        int pc = part.I0 + li, pr = part.J0 + lj, at = (pr - fr) * Side + pc - fc;
                        if (!fp.Contains(pc, pr) && !fp.Contains(pc - 1, pr - 1)) continue;
                        // The painted footprint (islands included), fading over its last metre: the most any trail covers the
                        // point, so a later trail's paint edge never cuts a line through an earlier one (the review, 2026-10-20).
                        float cover = (float)Math.Max(0, Math.Min(1, fp.PointClearance(pc, pr)));
                        if (cover > chunk.Foot[at]) chunk.Foot[at] = cover;
                        if (s.Graded && cover > chunk.Graded[at]) chunk.Graded[at] = cover;
                        if (d >= chunk.Nearest[at]) continue;
                        chunk.Nearest[at] = d;
                        chunk.Near[at] = true;
                        chunk.Offset[at] = (float)located.Offset[lj * lw + li];
                    }
            }
        }

        // The offset at any point: zero where no trail reaches.
        float OffsetAt(Chunk chunk, int c, int r)
        {
            int fc = chunk.Key.FirstColumn, fr = chunk.Key.FirstRow;
            if (c >= fc && c < fc + Side && r >= fr && r < fr + Side) return chunk.Offset[(r - fr) * Side + c - fc];
            return TryAt(c, r, out var other, out int at) ? other.Offset[at] : 0;
        }

        // The edited ground's change and its slope across the trail (along the gradient of the offset).
        void Shade(Chunk chunk, Block b, EditedHeights edited)
        {
            var lattice = edited.Lattice;
            int fc = chunk.Key.FirstColumn, fr = chunk.Key.FirstRow;
            for (int r = b.J0; r <= b.J1; r++)
                for (int c = b.I0; c <= b.I1; c++)
                {
                    int at = (r - fr) * Side + c - fc;
                    if (chunk.Foot[at] <= 0 && !chunk.Near[at]) continue;
                    double x = lattice.X(c), y = lattice.Y(r);
                    chunk.Change[at] = (float)(edited.AtPoint(c, r) - edited.Source.At(x, y));
                    double ux = (OffsetAt(chunk, c + 1, r) - OffsetAt(chunk, c - 1, r)) / 2, uy = (OffsetAt(chunk, c, r - 1) - OffsetAt(chunk, c, r + 1)) / 2;   // rows run south
                    double len = Math.Sqrt(ux * ux + uy * uy);
                    if (len < 1e-6) continue;
                    ux /= len;
                    uy /= len;
                    // Over ±2 m, so the snow/earth boundary that reads it follows the face, not the 1 m lattice's steps.
                    double slope = (edited.At(x + 2 * ux, y + 2 * uy) - edited.At(x - 2 * ux, y - 2 * uy)) / 4;
                    chunk.Across[at] = (float)Math.Abs(slope);
                }
        }

        static double Sq(double v) => v * v;
    }
}
