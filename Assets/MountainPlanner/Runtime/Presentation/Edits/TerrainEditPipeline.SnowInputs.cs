using System;
using System.Collections.Generic;
using System.Diagnostics;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Snow;
using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.Presentation.Edits
{
    /// <summary>
    /// Task 4.07 PR A2: the live snow's inputs after an edit. The snow stays on the ground; only its inputs change. Each
    /// change samples the edited cells again as the opener did (SN-01 v2: the mean of every post and canopy texel in a cell,
    /// through <see cref="SurfaceSampler"/>), then <see cref="SnowTerrain.Patch"/> works out the new slopes, ridge rise and
    /// canopy over the rectangle, which goes to the worker (command 32) and into the presentation's copy. Only cells wholly
    /// inside the core (1 m in) are sampled again: their posts and texels all come from core tiles, the ones edits touch
    /// (SH-06); a cell on the core's edge keeps its downloaded inputs. What a cell holds is a function of the edited ground
    /// alone, never of the order the edits came in, so a reloaded save rebuilds the same inputs.
    /// The core tiles' canopy texels are kept on the CPU from a tile's first edit (read from the cache, then each patch's
    /// splat laid over them), about 1 MB a tile.
    /// </summary>
    public sealed partial class TerrainEditPipeline
    {
        int[] _snowMm;
        byte[] _snowCanopyBytes;
        Rect _core;
        SnowTerrain _snowTerrain;
        EditedHeights _snowHeights;
        System.Threading.CancellationToken _snowToken;
        readonly Dictionary<(int, int), byte[]> _canopyTexels = new Dictionary<(int, int), byte[]>();
        readonly List<CanopyWindow> _canopyQueue = new List<CanopyWindow>();
        readonly object _canopyLock = new object();
        // Cells to sample again (written on the main thread), and cells sampled but not yet in a patch (the snow's run only).
        CellRect _snowCells = CellRect.Empty, _patchPending = CellRect.Empty;

        /// <summary>The live snow's inputs changed over a rectangle (on the main thread): the patch for the worker, never touched
        /// again by the pipeline. Already in the presentation's copy.</summary>
        public Action<SnowTerrainPatch> SnowTerrainChanged { get; set; }
        /// <summary>The worker owns live snow: an edit changes the snow's inputs and leaves the depths alone (no static snowpack).</summary>
        public bool LiveSnow { get; set; }
        public bool HasSnowTerrain => _snowTerrain != null;
        /// <summary>No snow work is waiting or running: the presentation's inputs hold every edit so far.</summary>
        public bool SnowSettled => !_snowRunning && !_snowDue;
        int _snowInputsVersion;
        /// <summary>Task 4.28: counts the times earthwork changed the cells' mean heights (the resort's own array), so the
        /// ground's moisture knows when to follow.</summary>
        public int SnowInputsVersion => System.Threading.Volatile.Read(ref _snowInputsVersion);

        readonly struct CellRect
        {
            public readonly int I0, J0, I1, J1;   // [I0, I1) × [J0, J1)
            public CellRect(int i0, int j0, int i1, int j1) { I0 = i0; J0 = j0; I1 = i1; J1 = j1; }
            public static readonly CellRect Empty = new CellRect(0, 0, 0, 0);
            public bool IsEmpty => I1 <= I0 || J1 <= J0;
            public CellRect Union(CellRect o) =>
                IsEmpty ? o : o.IsEmpty ? this : new CellRect(Math.Min(I0, o.I0), Math.Min(J0, o.J0), Math.Max(I1, o.I1), Math.Max(J1, o.J1));
        }

        /// <summary>A patch's canopy texels (the splat's forest-floor weight) for the CPU copy, in the splat's rows (south first).</summary>
        sealed class CanopyWindow
        {
            public (int, int) Tile;
            public int X, Y, Width, Height;
            public byte[] Bytes;
        }

        /// <summary>The snow's live inputs as the opener built them: the cells' mean heights and canopy and the core's rectangle.</summary>
        void AttachSnowInputs(int[] mm, byte[] canopy, Rect core)
        {
            _snowMm = mm;
            _snowCanopyBytes = canopy;
            _core = core;
        }

        /// <summary>
        /// The presentation's copy of the snow's inputs (the resort's), once built: edits made before it came are patched into it
        /// now, and every later edit patches it in place. The worker gets clones of it and the patches.
        /// </summary>
        public void AttachSnowTerrain(SnowTerrain terrain)
        {
            if (terrain == null || _snowMm == null || _states == null || terrain.Width != _states.Snow.Width || terrain.Depth != _states.Snow.Height) return;
            _snowTerrain = terrain;
            _snowDue = true;
            if (!_snowRunning) RunSnow(Stopwatch.StartNew(), _snowToken);
        }

        /// <summary>On the main thread: a patch's cells and canopy texels for the snow's next run.</summary>
        void NoteSnowInputs(Patch p, int ci0, int cj0, int ci1, int cj1)
        {
            if (_snowMm == null) return;
            _snowCells = _snowCells.Union(new CellRect(ci0, cj0, ci1 + 1, cj1 + 1));
            if (p.Splat == null || !_grid.IsCore(new TileKey(p.Tile.Item1, p.Tile.Item2))) return;
            int w = p.Splat.Width, h = p.Splat.Height;
            var bytes = new byte[w * h];
            byte[] t0 = p.Splat.Textures[0];
            for (int k = 0; k < bytes.Length; k++) bytes[k] = t0[k * 4 + 1];
            lock (_canopyLock) _canopyQueue.Add(new CanopyWindow { Tile = p.Tile, X = p.SplatX, Y = p.SplatY, Width = w, Height = h, Bytes = bytes });
        }

        /// <summary>A core tile's current canopy texels: from the cache the first time, then kept by the windows laid over them.</summary>
        byte[] CanopyTexelsOf((int, int) key)
        {
            if (_canopyTexels.TryGetValue(key, out var t)) return t;
            if (!_cacheTiles.TryGetValue(key, out var tile)) return null;
            var splat = SplatTexels.Load(_folder, tile);
            int n = splat.Resolution;
            t = new byte[n * n];
            byte[] t0 = splat.Textures[0];
            for (int k = 0; k < t.Length; k++) t[k] = t0[k * 4 + 1];
            _canopyTexels[key] = t;
            return t;
        }

        /// <summary>
        /// Off the main thread, in the snow's run (one at a time): lays the queued canopy windows over the CPU copies, samples
        /// the changed cells again and, once the presentation's inputs are here, makes the patch for everything sampled since
        /// the last one. Returns null when there's nothing to patch yet.
        /// </summary>
        SnowTerrainPatch SampleSnowInputs(CellRect cells, EditedHeights heights, List<CanopyWindow> windows, SnowTerrain terrain)
        {
            foreach (var win in windows)
            {
                var texels = CanopyTexelsOf(win.Tile);
                if (texels == null) continue;
                int n = (int)Math.Round(Math.Sqrt(texels.Length));
                for (int y = 0; y < win.Height; y++)
                {
                    int row = win.Y + y;
                    if (row < 0 || row >= n) continue;
                    int x0 = Math.Max(0, win.X), x1 = Math.Min(n, win.X + win.Width);
                    if (x1 > x0) Array.Copy(win.Bytes, y * win.Width + (x0 - win.X), texels, row * n + x0, x1 - x0);
                }
            }
            if (!cells.IsEmpty && heights != null)
            {
                var inside = Interior(cells);
                if (!inside.IsEmpty)
                {
                    Resample(inside, heights);
                    _patchPending = _patchPending.Union(inside);
                }
            }
            if (terrain == null || _patchPending.IsEmpty) return null;
            var p = terrain.Patch(_snowMm, _snowCanopyBytes, _patchPending.I0, _patchPending.J0, _patchPending.I1, _patchPending.J1);
            _patchPending = CellRect.Empty;
            return p;
        }

        /// <summary>The cells of <paramref name="r"/> wholly inside the core, 1 m in.</summary>
        CellRect Interior(CellRect r)
        {
            const int cell = SurfaceGrid.CellMetres;
            int width = _states.Snow.Width, depth = _states.Snow.Height;
            int i0 = Mathf.Max(r.I0, Mathf.CeilToInt((_core.xMin + 1 - _ring.xMin) / cell)), i1 = Mathf.Min(r.I1, Mathf.FloorToInt((_core.xMax - 1 - _ring.xMin) / cell));
            int j0 = Mathf.Max(r.J0, Mathf.CeilToInt((_core.yMin + 1 - _ring.yMin) / cell)), j1 = Mathf.Min(r.J1, Mathf.FloorToInt((_core.yMax - 1 - _ring.yMin) / cell));
            return new CellRect(Math.Max(0, i0), Math.Max(0, j0), Math.Min(width, i1), Math.Min(depth, j1));
        }

        /// <summary>SN-01 v2 over the rectangle from the core tiles' edited posts and current canopy texels.</summary>
        void Resample(CellRect r, EditedHeights heights)
        {
            int pw = r.I1 - r.I0, ph = r.J1 - r.J0, width = _states.Snow.Width;
            var sampler = new SurfaceSampler(pw, ph);
            long cellMm = SurfaceGrid.CellMetres * 1000L;
            foreach (var kv in _cacheTiles)
            {
                var key = kv.Key;
                if (!_grid.IsCore(new TileKey(key.Item1, key.Item2))) continue;
                var tile = kv.Value;
                var (ox, oz) = _frame.ToLocal(new AlbersPoint(tile.West, tile.North - _cache.TileMetres));
                var (west, south, side) = SnowSamples.TileMm(ox, oz, _ring.xMin, _ring.yMin, _cache.TileMetres);
                // Quick reject: the tile and the rectangle don't meet (a post on the shared edge belongs to both).
                if (west > r.I1 * cellMm || west + side < r.I0 * cellMm || south > r.J1 * cellMm || south + side < r.J0 * cellMm) continue;
                west -= r.I0 * cellMm;
                south -= r.J0 * cellMm;
                bool lastEast = !_cacheTiles.ContainsKey((key.Item1 + 1, key.Item2)), lastNorth = !_cacheTiles.ContainsKey((key.Item1, key.Item2 - 1));
                sampler.AddPosts(new EditedPosts(this, heights.Edits, key.Item1 * 1024, key.Item2 * 1024, _cache.HeightMin, _cache.HeightRange),
                                 west, south, side, lastEast, lastNorth, 0, 0, pw, ph);
                var texels = CanopyTexelsOf(key);
                if (texels != null)
                    sampler.AddCanopy(new SnowSamples.Canopy(texels, (int)Math.Round(Math.Sqrt(texels.Length)), 1, 0), west, south, side, lastEast, lastNorth, 0, 0, pw, ph);
            }
            var mm = new int[pw * ph];
            var canopy = new byte[pw * ph];
            sampler.Heights(mm);
            sampler.Canopy(canopy);
            for (int j = 0; j < ph; j++)
            {
                Array.Copy(mm, j * pw, _snowMm, (r.J0 + j) * width + r.I0, pw);
                Array.Copy(canopy, j * pw, _snowCanopyBytes, (r.J0 + j) * width + r.I0, pw);
            }
            System.Threading.Interlocked.Increment(ref _snowInputsVersion);
        }

        /// <summary>A core tile's edited posts as the tile holds them (<see cref="Normalised(IPointDeltas, int, int, int, int)"/>),
        /// row 0 at its south edge, in whole mm.</summary>
        readonly struct EditedPosts : ITileSamples
        {
            readonly TerrainEditPipeline _p;
            readonly IPointDeltas _ground;
            readonly int _gc, _gr;
            readonly double _min, _range;
            public EditedPosts(TerrainEditPipeline p, IPointDeltas ground, int gc, int gr, double min, double range)
            { _p = p; _ground = ground; _gc = gc; _gr = gr; _min = min; _range = range; }
            public int Resolution => TerrainTiles.Resolution;
            public int Value(int column, int row) => SnowSamples.Millimetres(_p.Normalised(_ground, _gc, _gr, column, TerrainTiles.Resolution - 1 - row), _min, _range);
        }
    }
}
