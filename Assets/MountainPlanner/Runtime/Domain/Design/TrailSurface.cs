using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The numbers behind the look of built trails (task 07, SK3; the terrain shader's trail map), per lattice point
    /// over a window round the built trails: the footprint's cover, the height change (edited minus natural), graded or
    /// not, the offset across the nearest trail and the edited ground's slope across it. Display data, never stored and
    /// never read by a rule.
    /// Task P3-11: it keeps its fields between updates and works out again only the points an update can have changed
    /// (the trails that changed, old place and new, and the ground that moved), each point exactly as a whole redraw
    /// would; each trail is located only over its own box. The window is the same rule as ever: the trails' box and 2 m,
    /// at most <see cref="MaxPoints"/> a side from its north-west corner.
    /// </summary>
    public sealed class TrailSurface
    {
        public const int MaxPoints = 2048;

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

        /// <summary>A block of window points, inclusive (i columns east, j rows south).</summary>
        public readonly struct Block
        {
            public readonly int I0, J0, I1, J1;
            public Block(int i0, int j0, int i1, int j1) { I0 = i0; J0 = j0; I1 = i1; J1 = j1; }
            public bool IsEmpty => I1 < I0 || J1 < J0;
            public Block Grown(int by) => new Block(I0 - by, J0 - by, I1 + by, J1 + by);
            public Block Within(int w, int h) => new Block(Math.Max(0, I0), Math.Max(0, J0), Math.Min(w - 1, I1), Math.Min(h - 1, J1));
            public override string ToString() => $"[{I0}..{I1}] x [{J0}..{J1}]";
        }

        /// <summary>The window: lattice columns C0..C0+W-1 and rows R0..R0+H-1 (rows run south).</summary>
        public int C0 { get; private set; }
        public int R0 { get; private set; }
        public int W { get; private set; }
        public int H { get; private set; }
        public int Count => _shapes.Count;
        /// <summary>The fields, W × H, rows from the north. Zero away from the trails.</summary>
        public float[] Foot { get; private set; } = Array.Empty<float>();
        public float[] Change { get; private set; } = Array.Empty<float>();
        public float[] Graded { get; private set; } = Array.Empty<float>();
        public float[] Offset { get; private set; } = Array.Empty<float>();
        public float[] Across { get; private set; } = Array.Empty<float>();
        double[] _nearest = Array.Empty<double>();

        /// <summary>The blocks the last update worked out again (the whole window when it moved).</summary>
        public IReadOnlyList<Block> Redrawn => _redrawn;
        /// <summary>Whether the last update moved or resized the window (everything was drawn again).</summary>
        public bool WindowChanged { get; private set; }
        /// <summary>Window points the last update worked out again.</summary>
        public long PointsRedrawn { get; private set; }

        readonly List<Shape> _shapes = new List<Shape>();
        readonly List<Block> _redrawn = new List<Block>();
        TerrainEdits _drawnOver;

        /// <summary>
        /// Updates the fields for <paramref name="shapes"/> (the built trails with a centreline, in the design's order)
        /// over <paramref name="edited"/>. Returns false when there's nothing to show (no window).
        /// </summary>
        public bool Update(IReadOnlyList<Shape> shapes, EditedHeights edited)
        {
            _redrawn.Clear();
            PointsRedrawn = 0;
            WindowChanged = false;
            var lattice = edited.Lattice;
            if (shapes.Count == 0)
            {
                WindowChanged = W != 0;
                _shapes.Clear();
                W = H = 0;
                _drawnOver = edited.Edits;
                return false;
            }
            // The window: the trails' box (each centreline widened by its reach) and 2 m, capped from the north-west.
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var s in shapes)
            {
                double half = s.Half;
                for (int i = 0; i < s.Line.Length; i += 2)
                {
                    minX = Math.Min(minX, s.Line[i] - half); maxX = Math.Max(maxX, s.Line[i] + half);
                    minY = Math.Min(minY, s.Line[i + 1] - half); maxY = Math.Max(maxY, s.Line[i + 1] + half);
                }
            }
            int c0 = (int)Math.Floor(minX - 2 - lattice.West), r0 = (int)Math.Floor(lattice.North - (maxY + 2));
            int w = Math.Min(MaxPoints, (int)Math.Ceiling(maxX + 2 - lattice.West) - c0 + 1);
            int h = Math.Min(MaxPoints, (int)Math.Ceiling(lattice.North - (minY - 2)) - r0 + 1);

            var dirty = new List<Block>();
            bool fresh = _drawnOver == null || W == 0 || !SameOrder(shapes);
            if (c0 != C0 || r0 != R0 || w != W || h != H)
            {
                WindowChanged = true;
                if (fresh) Resize(c0, r0, w, h, dirty);
                else Move(c0, r0, w, h, dirty);
            }
            if (fresh) { dirty.Clear(); dirty.Add(new Block(0, 0, W - 1, H - 1)); }
            else
            {
                // Where the trails that changed reach, where they were and where they are, by lattice tile.
                var tiles = new HashSet<EditTileKey>();
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
                // And the ground that moved (a trail's, a lift's or a stroke's); each tile 3 points round for the slope's ±2 m reads.
                foreach (var key in TerrainEdits.Changed(_drawnOver, edited.Edits)) tiles.Add(key);
                foreach (var key in tiles)
                    dirty.Add(new Block(key.FirstColumn - C0 - 3, key.FirstRow - R0 - 3,
                                        key.FirstColumn + EditTileKey.Side - 1 - C0 + 3, key.FirstRow + EditTileKey.Side - 1 - R0 + 3));
            }
            _shapes.Clear();
            _shapes.AddRange(shapes);
            _drawnOver = edited.Edits;
            if (_reach.Count > 0)
            {
                var now = new HashSet<double[]>();
                foreach (var s in shapes) now.Add(s.Line);
                var old = new List<double[]>();
                foreach (var k in _reach.Keys) if (!now.Contains(k)) old.Add(k);
                foreach (var k in old) _reach.Remove(k);
            }
            foreach (var b in dirty)
            {
                // The slope across a point reads the offsets either side of it, so one more point round.
                var block = b.Grown(1).Within(W, H);
                if (block.IsEmpty) continue;
                Redraw(block, edited);
                _redrawn.Add(block);
                PointsRedrawn += (long)(block.I1 - block.I0 + 1) * (block.J1 - block.J0 + 1);
            }
            return true;
        }

        // A new window drawn whole.
        void Resize(int c0, int r0, int w, int h, List<Block> dirty)
        {
            C0 = c0; R0 = r0; W = w; H = h;
            if (Foot.Length != w * h)
            {
                Foot = new float[w * h]; Change = new float[w * h]; Graded = new float[w * h];
                Offset = new float[w * h]; Across = new float[w * h]; _nearest = new double[w * h];
            }
            dirty.Add(new Block(0, 0, w - 1, h - 1));
        }

        // The window moved or grew: what both cover keeps its numbers (each point's depends only on where it is), and what's
        // new is worked out, with both windows' edges (a point on an edge has no slope across).
        void Move(int c0, int r0, int w, int h, List<Block> dirty)
        {
            int oc0 = C0, or0 = R0, ow = W, oh = H;
            var foot = new float[w * h]; var change = new float[w * h]; var graded = new float[w * h];
            var offset = new float[w * h]; var across = new float[w * h]; var nearest = new double[w * h];
            for (int i = 0; i < nearest.Length; i++) nearest[i] = double.MaxValue;
            // The overlap, in the new window's points.
            int i0 = Math.Max(0, oc0 - c0), i1 = Math.Min(w, oc0 + ow - c0) - 1;
            int j0 = Math.Max(0, or0 - r0), j1 = Math.Min(h, or0 + oh - r0) - 1;
            if (i1 >= i0 && j1 >= j0)
            {
                int len = i1 - i0 + 1;
                for (int j = j0; j <= j1; j++)
                {
                    int from = (r0 + j - or0) * ow + (c0 + i0 - oc0), to = j * w + i0;
                    Array.Copy(Foot, from, foot, to, len); Array.Copy(Change, from, change, to, len);
                    Array.Copy(Graded, from, graded, to, len); Array.Copy(Offset, from, offset, to, len);
                    Array.Copy(Across, from, across, to, len); Array.Copy(_nearest, from, nearest, to, len);
                }
                if (j0 > 0) dirty.Add(new Block(0, 0, w - 1, j0 - 1));
                if (j1 < h - 1) dirty.Add(new Block(0, j1 + 1, w - 1, h - 1));
                if (i0 > 0) dirty.Add(new Block(0, j0, i0 - 1, j1));
                if (i1 < w - 1) dirty.Add(new Block(i1 + 1, j0, w - 1, j1));
                // The old window's edges, now inside.
                dirty.Add(new Block(oc0 - c0, or0 - r0, oc0 - c0, or0 + oh - 1 - r0));
                dirty.Add(new Block(oc0 + ow - 1 - c0, or0 - r0, oc0 + ow - 1 - c0, or0 + oh - 1 - r0));
                dirty.Add(new Block(oc0 - c0, or0 - r0, oc0 + ow - 1 - c0, or0 - r0));
                dirty.Add(new Block(oc0 - c0, or0 + oh - 1 - r0, oc0 + ow - 1 - c0, or0 + oh - 1 - r0));
            }
            else dirty.Add(new Block(0, 0, w - 1, h - 1));
            // The new window's edges.
            dirty.Add(new Block(0, 0, 0, h - 1));
            dirty.Add(new Block(w - 1, 0, w - 1, h - 1));
            dirty.Add(new Block(0, 0, w - 1, 0));
            dirty.Add(new Block(0, h - 1, w - 1, h - 1));
            C0 = c0; R0 = r0; W = w; H = h;
            Foot = foot; Change = change; Graded = graded; Offset = offset; Across = across; _nearest = nearest;
        }

        // The lattice tiles a trail's look reaches: each centreline segment's box, widened by the reach it's located over.
        void Reach(Shape s, EditLattice lattice, HashSet<EditTileKey> into)
        {
            foreach (var key in ReachOf(s, lattice)) into.Add(key);
        }

        readonly Dictionary<double[], HashSet<EditTileKey>> _reach = new Dictionary<double[], HashSet<EditTileKey>>();

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

        // Whether a trail reaches any tile a block of window points touches.
        bool Reaches(Shape s, EditLattice lattice, Block b)
        {
            var tiles = ReachOf(s, lattice);
            var a = EditTileKey.Of(C0 + b.I0, R0 + b.J0);
            var z = EditTileKey.Of(C0 + b.I1, R0 + b.J1);
            for (int r = a.Row; r <= z.Row; r++)
                for (int c = a.Column; c <= z.Column; c++)
                    if (tiles.Contains(new EditTileKey(c, r))) return true;
            return false;
        }

        /// <summary>Whether a point (the lattice's coordinates, metres) is inside the window.</summary>
        public bool Covers(EditLattice lattice, double x, double y)
        {
            if (W == 0) return false;
            double c = x - lattice.West, r = lattice.North - y;
            return c >= C0 && c <= C0 + W - 1 && r >= R0 && r <= R0 + H - 1;
        }

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

        // A trail's reach in window points: its centreline's box widened by the reach it's located over.
        Block BoxOf(Shape s, EditLattice lattice)
        {
            double reach = s.Half + 2;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < s.Line.Length; i += 2)
            {
                minX = Math.Min(minX, s.Line[i]); maxX = Math.Max(maxX, s.Line[i]);
                minY = Math.Min(minY, s.Line[i + 1]); maxY = Math.Max(maxY, s.Line[i + 1]);
            }
            return new Block((int)Math.Floor(minX - reach - 1 - lattice.West) - C0, (int)Math.Floor(lattice.North - (maxY + reach + 1)) - R0,
                             (int)Math.Ceiling(maxX + reach + 1 - lattice.West) - C0, (int)Math.Ceiling(lattice.North - (minY - reach - 1)) - R0);
        }

        // Every point of the block from scratch: each trail that reaches it, in order, then the change and the slope.
        void Redraw(Block b, EditedHeights edited)
        {
            var lattice = edited.Lattice;
            for (int j = b.J0; j <= b.J1; j++)
            {
                int row = j * W;
                for (int i = b.I0; i <= b.I1; i++)
                {
                    int at = row + i;
                    Foot[at] = 0; Graded[at] = 0; Change[at] = 0; Offset[at] = 0; Across[at] = 0;
                    _nearest[at] = double.MaxValue;
                }
            }
            foreach (var s in _shapes)
            {
                var box = BoxOf(s, lattice);
                int i0 = Math.Max(b.I0, box.I0), i1 = Math.Min(b.I1, box.I1), j0 = Math.Max(b.J0, box.J0), j1 = Math.Min(b.J1, box.J1);
                if (i1 < i0 || j1 < j0 || !Reaches(s, lattice, b)) continue;
                var line = s.Line;
                double half = s.Half;
                var fp = s.Footprint;
                var along = new double[line.Length / 2];
                for (int k = 1; k < along.Length; k++)
                    along[k] = along[k - 1] + Math.Sqrt(Sq(line[2 * k] - line[2 * k - 2]) + Sq(line[2 * k + 1] - line[2 * k - 1]));
                int lw = i1 - i0 + 1, lh = j1 - j0 + 1;
                var located = TrailGrader.Locate(line, along, lattice, C0 + i0, R0 + j0, lw, lh, 0, 0, half + 2);
                for (int lj = 0; lj < lh; lj++)
                    for (int li = 0; li < lw; li++)
                    {
                        double d = located.Dist[lj * lw + li];
                        if (d > half + 2) continue;
                        int i = i0 + li, j = j0 + lj, at = j * W + i;
                        int pc = C0 + i, pr = R0 + j;
                        if (!fp.Contains(pc, pr) && !fp.Contains(pc - 1, pr - 1)) continue;
                        // The painted footprint (islands included), fading over its last metre: the most any trail covers the
                        // point, so a later trail's paint edge never cuts a line through an earlier one (the review, 2026-10-20).
                        float cover = (float)Math.Max(0, Math.Min(1, fp.PointClearance(pc, pr)));
                        if (cover > Foot[at]) Foot[at] = cover;
                        if (s.Graded && cover > Graded[at]) Graded[at] = cover;
                        if (d >= _nearest[at]) continue;
                        _nearest[at] = d;
                        Offset[at] = (float)located.Offset[lj * lw + li];
                    }
            }
            // The edited ground's change and its slope across the trail (along the gradient of the offset).
            for (int j = b.J0; j <= b.J1; j++)
                for (int i = b.I0; i <= b.I1; i++)
                {
                    int at = j * W + i;
                    if (Foot[at] <= 0 && _nearest[at] > 1e9) continue;
                    int c = C0 + i, r = R0 + j;
                    double x = lattice.X(c), y = lattice.Y(r);
                    Change[at] = (float)(edited.AtPoint(c, r) - edited.Source.At(x, y));
                    if (i == 0 || j == 0 || i == W - 1 || j == H - 1) continue;
                    double ux = (Offset[at + 1] - Offset[at - 1]) / 2, uy = (Offset[at - W] - Offset[at + W]) / 2;   // rows run south
                    double len = Math.Sqrt(ux * ux + uy * uy);
                    if (len < 1e-6) continue;
                    ux /= len;
                    uy /= len;
                    // Over ±2 m, so the snow/earth boundary that reads it follows the face, not the 1 m lattice's steps.
                    double slope = (edited.At(x + 2 * ux, y + 2 * uy) - edited.At(x - 2 * ux, y - 2 * uy)) / 4;
                    Across[at] = (float)Math.Abs(slope);
                }
        }

        static double Sq(double v) => v * v;
    }
}
