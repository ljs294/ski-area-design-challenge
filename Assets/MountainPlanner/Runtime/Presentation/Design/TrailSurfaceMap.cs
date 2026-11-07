using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// The look of built trails (task 07, decision SK3): 1 m world-space numbers the terrain shader reads round the built
    /// trails. Per lattice point:
    /// the footprint (1 inside, fading over the last metre);
    /// the height change, edited minus natural, in metres (cut negative, fill positive);
    /// 1 on a graded trail, 0 on a natural one (the terrain shader smooths a graded surface's shading);
    /// the edited ground's slope across the trail (0 on a level bench, about 1 on a 1:1 face);
    /// and the offset across the trail, which corduroy and groomer passes run along, at full float precision (finding #11).
    /// It is display data derived from the design and the edited heights, never stored and never read by a rule.
    /// Each trail's painted footprint and automatic centreline come from task 08's TrailFootprint and TrailCentreline;
    /// the numbers are <see cref="TrailSurface"/>'s, which works out again only what a change can reach (task P3-11).
    /// Task 4.01 (TL24): the numbers live in pages, one per 64 m lattice tile a trail reaches, wherever the trails are (the
    /// old single 2048 m texture left the rest of a big resort's trails undrawn). A page holds its tile's 64 × 64 points and
    /// the first column and row of its east and south neighbours, so the shader's bilinear read stays in one page. Pages
    /// sit in one structured buffer, found through a page table over the pages' box; only the pages a change reaches are
    /// sent again (SetData copies at the call, so the render thread never reads bytes a later update changed).
    /// </summary>
    public sealed class TrailSurfaceMap : IDisposable
    {
        /// <summary>Points a page side: a tile and one more.</summary>
        public const int PageSide = TrailSurface.Side + 1;
        const int PagePoints = PageSide * PageSide;

        static readonly int PointsId = Shader.PropertyToID("_TrailPoints"), PagesId = Shader.PropertyToID("_TrailPages"),
                            RectId = Shader.PropertyToID("_TrailRect"), ParamsId = Shader.PropertyToID("_TrailParams");

        /// <summary>One point as the shader reads it: footprint and change, graded and slope as halves, then the offset.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public uint FootChange, GradedAcross;
            public float Offset;
        }

        readonly Dictionary<TrailId, (Trail trail, TrailFootprint fp, double[] line)> _shapes = new Dictionary<TrailId, (Trail, TrailFootprint, double[])>();
        readonly TrailSurface _surface = new TrailSurface();
        readonly List<TrailSurface.Shape> _drawn = new List<TrailSurface.Shape>();
        readonly Dictionary<EditTileKey, int> _slots = new Dictionary<EditTileKey, int>();
        readonly Stack<int> _free = new Stack<int>();
        readonly Point[] _page = new Point[PagePoints];
        int[] _table = Array.Empty<int>();
        GraphicsBuffer _points, _pages;
        EditLattice _lattice;
        LocalFrame _frame;
        int _tableC0, _tableR0, _tableW, _tableH;

        public TrailSurfaceMap() => Hide();

        /// <summary>Draws no trails (the shader reads its buffers only when this is on).</summary>
        public static void Hide() => Shader.SetGlobalVector(ParamsId, Vector4.zero);

        /// <summary>The last rebuild's stages, ms (the [Built] log, task P3-11).</summary>
        public string LastSplit { get; private set; } = "";

        /// <summary>The CPU fields (tests).</summary>
        public TrailSurface Surface => _surface;

        /// <summary>Pages on the GPU.</summary>
        public int PageCount => _slots.Count;

        /// <summary>The page buffer and the page table (tests).</summary>
        public GraphicsBuffer PointsBuffer => _points;
        public GraphicsBuffer PagesBuffer => _pages;
        /// <summary>The page table's first tile column and row and its size in tiles (tests).</summary>
        public (int Column, int Row, int Width, int Height) TableBox => (_tableC0, _tableR0, _tableW, _tableH);
        /// <summary>A tile's page slot (tests).</summary>
        public bool TrySlot(EditTileKey key, out int slot) => _slots.TryGetValue(key, out slot);

        /// <summary>Whether a local point (metres east and north of the frame's origin) is under the map.</summary>
        public bool Covers(float x, float z)
        {
            if (_surface.Chunks.Count == 0) return false;
            var p = _frame.ToAlbers(x, z);
            return _surface.Covers(_lattice, p.X, p.Y);
        }

        /// <summary>The trails the map shows: built, with a stroke. Returns how many it drew.</summary>
        public int Rebuild(DesignDocument doc, EditedHeights edited, LocalFrame frame, Func<Trail, TrailBuild> built = null)
        {
            Prepare(doc, edited, frame, built);
            return Upload();
        }

        bool _show;
        double _shapesMs, _fieldsMs;

        /// <summary>
        /// Works out the numbers for <paramref name="doc"/> over <paramref name="edited"/> (task P3-11: on any thread, while
        /// the ground lands; nothing here touches Unity). <see cref="Upload"/> then packs them on the main thread.
        /// </summary>
        public void Prepare(DesignDocument doc, EditedHeights edited, LocalFrame frame, Func<Trail, TrailBuild> built = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _lattice = edited.Lattice;
            _frame = frame;
            _drawn.Clear();
            var seen = new HashSet<TrailId>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built || !t.HasEnds) continue;
                seen.Add(t.Id);
                // Task 08: each trail's painted footprint and automatic centreline (TR-02, TR-03), kept while the trail is the same.
                if (!_shapes.TryGetValue(t.Id, out var shape) || !Same(shape.trail, t))
                {
                    // The earthwork's own footprint and centreline for this trail (task 08): no second computation. A split
                    // part's line is its pair's, cut (NT2), so it works out its own, as the network does.
                    var b = built?.Invoke(t);
                    var own = b?.OwnLineFor(t);
                    if (own != null && own.Found) shape = (t, b.Footprint, own.Points);
                    else
                    {
                        var fp = b != null && b.IsFor(t) ? b.Footprint : TrailFootprint.Build(t.Shape, edited.Lattice, frame.Origin.X, frame.Origin.Y);
                        var head = frame.ToAlbers(t.Head.Value.XMetres, t.Head.Value.YMetres);
                        var tail = frame.ToAlbers(t.Tail.Value.XMetres, t.Tail.Value.YMetres);
                        var c = TrailCentreline.Find(fp, head.X, head.Y, tail.X, tail.Y);
                        shape = (t, fp, c.Found ? c.Points : null);
                    }
                    _shapes[t.Id] = shape;
                }
                if (shape.line == null) continue;
                _drawn.Add(new TrailSurface.Shape(t, shape.line, shape.fp, t.Settings.Grading == TrailGrading.Graded));
            }
            var stale = new List<TrailId>();
            foreach (var id in _shapes.Keys) if (!seen.Contains(id)) stale.Add(id);
            foreach (var id in stale) _shapes.Remove(id);
            _shapesMs = watch.Elapsed.TotalMilliseconds;
            _show = _surface.Update(_drawn, edited);
            _fieldsMs = watch.Elapsed.TotalMilliseconds - _shapesMs;
        }

        /// <summary>Packs what <see cref="Prepare"/> worked out into the buffers (main thread). Returns how many trails it drew.</summary>
        public int Upload()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            double shapesMs = _shapesMs, fieldsMs = _fieldsMs;
            if (!_show)
            {
                Hide();
                _slots.Clear();
                _free.Clear();
                LastSplit = $"shapes {shapesMs:F1}, nothing to show";
                return 0;
            }
            var chunks = _surface.Chunks;
            var send = new HashSet<EditTileKey>();
            bool whole = false;
            if (_surface.ChunksChanged)
            {
                var gone = new List<EditTileKey>();
                foreach (var key in _slots.Keys) if (!chunks.ContainsKey(key)) gone.Add(key);
                foreach (var key in gone) { _free.Push(_slots[key]); _slots.Remove(key); }
                foreach (var key in chunks.Keys)
                {
                    if (_slots.ContainsKey(key)) continue;
                    _slots[key] = _free.Count > 0 ? _free.Pop() : _slots.Count + _free.Count;
                    send.Add(key);
                }
                int need = 0;
                foreach (var slot in _slots.Values) need = Math.Max(need, slot + 1);
                if (_points == null || _points.count < need * PagePoints)
                {
                    // Room for half as many again, so a resort that grows doesn't resize every time.
                    _points?.Release();
                    _points = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(16, need + need / 2) * PagePoints, Marshal.SizeOf<Point>()) { name = "Trail points" };
                    whole = true;
                }
                Table(chunks);
            }
            // A changed point is on its own page, and on its west and north neighbours' too (their east column and south row).
            foreach (var b in _surface.Redrawn)
            {
                var a = EditTileKey.Of(b.I0 - 1, b.J0 - 1);
                var z = EditTileKey.Of(b.I1, b.J1);
                for (int r = a.Row; r <= z.Row; r++)
                    for (int c = a.Column; c <= z.Column; c++)
                    {
                        var k = new EditTileKey(c, r);
                        if (_slots.ContainsKey(k)) send.Add(k);
                    }
            }
            if (whole) send.UnionWith(_slots.Keys);
            foreach (var key in send) Send(key);
            double packMs = watch.Elapsed.TotalMilliseconds;
            Shader.SetGlobalBuffer(PointsId, _points);
            Shader.SetGlobalBuffer(PagesId, _pages);
            // The local position of lattice point (0, 0): column c is c metres east of it, row r r metres south.
            var (x0, z0) = _frame.ToLocal(new AlbersPoint(_lattice.X(0), _lattice.Y(0)));
            Shader.SetGlobalVector(RectId, new Vector4((float)x0, (float)z0, _tableC0, _tableR0));
            Shader.SetGlobalVector(ParamsId, new Vector4(1, _tableW, _tableH, 0));
            LastSplit = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "shapes {0:F1}, fields {1:F1} over {2:N0} points in {3} blocks, {4} of {5} pages sent{6}, pack and send {7:F1}",
                shapesMs, fieldsMs, _surface.PointsRedrawn, _surface.Redrawn.Count, send.Count, _slots.Count, whole ? " (buffer grown)" : "", packMs);
            return _drawn.Count;
        }

        // The page table over the pages' box: each tile's slot, -1 where no trail reaches.
        void Table(IReadOnlyDictionary<EditTileKey, TrailSurface.Chunk> chunks)
        {
            int c0 = int.MaxValue, r0 = int.MaxValue, c1 = int.MinValue, r1 = int.MinValue;
            foreach (var key in chunks.Keys)
            {
                c0 = Math.Min(c0, key.Column); c1 = Math.Max(c1, key.Column);
                r0 = Math.Min(r0, key.Row); r1 = Math.Max(r1, key.Row);
            }
            _tableC0 = c0; _tableR0 = r0; _tableW = c1 - c0 + 1; _tableH = r1 - r0 + 1;
            int n = _tableW * _tableH;
            if (_table.Length != n) _table = new int[n];
            for (int i = 0; i < n; i++) _table[i] = -1;
            foreach (var pair in _slots) _table[(pair.Key.Row - r0) * _tableW + pair.Key.Column - c0] = pair.Value;
            if (_pages == null || _pages.count < n)
            {
                _pages?.Release();
                _pages = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(64, n + n / 2), sizeof(int)) { name = "Trail pages" };
            }
            _pages.SetData(_table, 0, 0, n);
        }

        // One page: its tile's points, then its east neighbour's first column and its south neighbour's first row.
        void Send(EditTileKey key)
        {
            var chunks = _surface.Chunks;
            for (int dj = 0; dj < 2; dj++)
                for (int di = 0; di < 2; di++)
                {
                    int i0 = di * TrailSurface.Side, j0 = dj * TrailSurface.Side;
                    int i1 = di == 0 ? TrailSurface.Side - 1 : TrailSurface.Side, j1 = dj == 0 ? TrailSurface.Side - 1 : TrailSurface.Side;
                    chunks.TryGetValue(new EditTileKey(key.Column + di, key.Row + dj), out var chunk);
                    for (int j = j0; j <= j1; j++)
                        for (int i = i0; i <= i1; i++)
                        {
                            int at = (j - j0) * TrailSurface.Side + i - i0;
                            ref var p = ref _page[j * PageSide + i];
                            if (chunk == null) { p = default; continue; }
                            p.FootChange = Mathf.FloatToHalf(chunk.Foot[at]) | (uint)Mathf.FloatToHalf(chunk.Change[at]) << 16;
                            p.GradedAcross = Mathf.FloatToHalf(chunk.Graded[at]) | (uint)Mathf.FloatToHalf(chunk.Across[at]) << 16;
                            p.Offset = chunk.Offset[at];
                        }
                }
            _points.SetData(_page, 0, _slots[key] * PagePoints, PagePoints);
        }

        // The same trail by value: the history applies a command again, so an edited trail is never the object graded.
        static bool Same(Trail a, Trail b) =>
            ReferenceEquals(a, b) || a.Id == b.Id && a.Head == b.Head && a.Tail == b.Tail && a.Settings.Equals(b.Settings) && a.Shape.Equals(b.Shape);

        public void Dispose()
        {
            Hide();
            _points?.Release();
            _pages?.Release();
            _points = _pages = null;
            _slots.Clear();
            _free.Clear();
        }
    }
}
