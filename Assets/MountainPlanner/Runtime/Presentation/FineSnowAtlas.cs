using MountainPlanner.Domain.Snow;
using Unity.Collections;
using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Task 4.08 PR A: the fine trail snow on the GPU (snow-rendering-design §3: the managed depth's atlas and page table, here
    /// SN-03 v3's fine field). One slot per 64 m tile of <see cref="FineTiles"/>, each the tile's fine cells with a two-cell
    /// gutter copied from its neighbours, so Snow.hlsl's curve needs one page lookup a point. The worker's tiles
    /// (<see cref="Simulation.FineStagingRing"/>) land in a CPU copy first (<see cref="Take"/>); a few go up a frame
    /// (<see cref="Upload"/>) through staging strips and CopyTexture, inside BG-11's 0.2 ms with the 8 m tiles.
    /// <para>The lift texel is the fine depth × the 8 m cell's keep (lakes, stations, the ring's edge), so the shell and
    /// <see cref="LiftAt"/> agree; a keep change re-sends every slot.</para>
    /// Allocates when bound to a layout; nothing per frame after.
    /// </summary>
    public sealed class FineSnowAtlas : System.IDisposable
    {
        static readonly int LiftId = Shader.PropertyToID("_FineLiftAtlas"), StateId = Shader.PropertyToID("_FineStateAtlas"),
                            PageId = Shader.PropertyToID("_FinePageTable"), ParamsId = Shader.PropertyToID("_FineParams"),
                            GridId = Shader.PropertyToID("_FineGrid");

        /// <summary>Gutter cells round each slot: the curve reads two cells past its own, and a point in a tile with no slot
        /// within two cells of one that has reads that slot (Snow.hlsl's FineSlot), so the trail's edge blends across tiles.</summary>
        public const int Gutter = 3;
        /// <summary>Slots uploaded a frame at most: 8 at 2 m (36 × 36 × 8 B ≈ 10 KB each), 2 at 1 m, 16 at 4 m.</summary>
        public const int SlotsPerFrame = 16;
        /// <summary>This layout's slots a frame (about 8,000 fine cells' worth).</summary>
        public int SlotsThisFrame { get; private set; } = 1;
        const int Strips = 3;
        const float MmPerUnit = 65535f;

        readonly Rect _ring;
        readonly float[] _keep;
        readonly int _gridWidth, _gridHeight;
        public FineTiles Tiles { get; private set; }
        public ulong LayoutHash { get; private set; }
        public float FineMetres { get; private set; }
        /// <summary>Per listed tile: its fine depths (mm) and four state bytes a cell, as the worker staged them; null until the
        /// tile first comes in.</summary>
        ushort[][] _depth;
        byte[][] _state;
        bool[] _dirty, _uploaded;
        int[] _slotVersion;
        readonly System.Collections.Generic.Queue<int> _queue = new System.Collections.Generic.Queue<int>();
        Texture2D _lift, _stateTex, _page;
        Texture2D[] _stripLift, _stripState;
        ushort[] _scratchLift;
        byte[] _scratchState;
        readonly int[] _placed = new int[SlotsPerFrame];
        int _strip, _perRow, _side, _pad;

        /// <summary>The deepest lift a slot has held (metres): the shell's culling pads take the larger of this and the 8 m one.</summary>
        public float LiftCeiling { get; private set; }
        /// <summary>Moves on whenever a slot goes up.</summary>
        public int Version { get; private set; }
        public int SlotsWaiting => _queue.Count;
        public int TilesTaken { get; private set; }

        public FineSnowAtlas(Rect ring, float[] keep, int gridWidth, int gridHeight)
        {
            _ring = ring; _keep = keep; _gridWidth = gridWidth; _gridHeight = gridHeight;
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
        }

        /// <summary>
        /// Lays the atlas out for <paramref name="layout"/> (the one sent to the worker); a layout on another grid is refused.
        /// The same layout again keeps what is there. Until its tiles come in nothing draws from it.
        /// </summary>
        public bool Bind(TrailSnowLayout layout)
        {
            if (layout == null) { Unbind(); return false; }
            if (layout.Hash() == LayoutHash) return true;
            if (layout.GridWidth != _gridWidth || layout.GridHeight != _gridHeight
                || Mathf.Abs(layout.OriginXMm / 1000f - _ring.xMin) > 0.01f || Mathf.Abs(layout.OriginYMm / 1000f - _ring.yMin) > 0.01f)
            {
                Debug.LogError($"[FineSnowAtlas] the trail snow's grid ({layout.GridWidth}x{layout.GridHeight}) isn't the snow map's ({_gridWidth}x{_gridHeight}): the trails draw the 8 m snow");
                Unbind();
                return false;
            }
            Unbind();
            var tiles = new FineTiles(layout);
            Tiles = tiles; LayoutHash = layout.Hash(); FineMetres = layout.FineMetres;
            int n = tiles.Count;
            _depth = new ushort[n][]; _state = new byte[n][]; _dirty = new bool[n]; _uploaded = new bool[n]; _slotVersion = new int[n];
            SlotsThisFrame = Mathf.Clamp(8192 / (tiles.TileFine * tiles.TileFine), 1, SlotsPerFrame);
            if (n == 0) return true;
            _pad = tiles.TileFine + 2 * Gutter;
            _perRow = Mathf.CeilToInt(Mathf.Sqrt(n));
            _side = _perRow * _pad;
            _lift = new Texture2D(_side, _side, TextureFormat.RG32, false, true)
            {
                name = "Fine snow lift", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            _stateTex = new Texture2D(_side, _side, TextureFormat.RGBA32, false, true)
            {
                name = "Fine snow state", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
            };
            _page = new Texture2D(tiles.TilesX, tiles.TilesY, TextureFormat.RFloat, false, true)
            {
                name = "Fine snow pages", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
            };
            ClearTexture(_lift); ClearTexture(_stateTex);
            var pages = _page.GetPixelData<float>(0);
            for (int t = 0; t < pages.Length; t++) pages[t] = 0;
            _page.Apply(false);
            _stripLift = new Texture2D[Strips]; _stripState = new Texture2D[Strips];
            for (int k = 0; k < Strips; k++)
            {
                _stripLift[k] = new Texture2D(_pad, _pad * SlotsPerFrame, TextureFormat.RG32, false, true) { name = "Fine snow lift strip" };
                _stripState[k] = new Texture2D(_pad, _pad * SlotsPerFrame, TextureFormat.RGBA32, false, true) { name = "Fine snow state strip" };
            }
            _scratchLift = new ushort[_pad * _pad * SlotsPerFrame * 2];
            _scratchState = new byte[_pad * _pad * SlotsPerFrame * 4];
            Shader.SetGlobalTexture(LiftId, _lift);
            Shader.SetGlobalTexture(StateId, _stateTex);
            Shader.SetGlobalTexture(PageId, _page);
            Shader.SetGlobalVector(GridId, new Vector4(tiles.TilesX, tiles.TilesY, _perRow, 1f / _side));
            Shader.SetGlobalVector(ParamsId, new Vector4(0, FineMetres, tiles.TileFine, _pad));
            return true;
        }

        static void ClearTexture(Texture2D t)
        {
            var data = t.GetPixelData<byte>(0);
            for (int i = 0; i < data.Length; i++) data[i] = 0;
            t.Apply(false);
        }

        void Unbind()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Destroy(_lift); Destroy(_stateTex); Destroy(_page);
            if (_stripLift != null) for (int k = 0; k < Strips; k++) { Destroy(_stripLift[k]); Destroy(_stripState[k]); }
            _lift = _stateTex = _page = null; _stripLift = _stripState = null;
            Tiles = null; LayoutHash = 0; _queue.Clear(); _drawing = false;
        }

        bool _drawing;

        /// <summary>A tile the worker staged: into the CPU copy now, onto the GPU by <see cref="Upload"/> (with its neighbours'
        /// gutters). A tile staged for another layout is dropped.</summary>
        public void Take(int listed, int size, ulong layout, ushort[] depth, byte[] state, int offset)
        {
            if (Tiles == null || layout != LayoutHash || size != Tiles.TileFine || listed < 0 || listed >= Tiles.Count) return;
            int cells = size * size;
            _depth[listed] ??= new ushort[cells];
            _state[listed] ??= new byte[cells * 4];
            System.Array.Copy(depth, offset, _depth[listed], 0, cells);
            System.Buffer.BlockCopy(state, offset * 4, _state[listed], 0, cells * 4);
            TilesTaken++;
            int t = Tiles.Tiles[listed], tx = t % Tiles.TilesX, ty = t / Tiles.TilesX;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = tx + dx, y = ty + dy;
                    if (x < 0 || y < 0 || x >= Tiles.TilesX || y >= Tiles.TilesY) continue;
                    int s = Tiles.Slot[y * Tiles.TilesX + x];
                    if (s >= 0 && _depth[s] != null) Queue(s);
                }
        }

        void Queue(int s)
        {
            if (_dirty[s]) return;
            _dirty[s] = true;
            _queue.Enqueue(s);
        }

        /// <summary>The 8 m keep changed (a lake, a station): every slot that has come in goes up again.</summary>
        public void KeepChanged()
        {
            if (Tiles == null) return;
            for (int s = 0; s < Tiles.Count; s++) if (_depth[s] != null) Queue(s);
        }

        static bool CanCopy => (SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) != 0;

        /// <summary>Up to <see cref="SlotsPerFrame"/> slots onto the GPU; their page entries switch on with them.</summary>
        public void Upload()
        {
            if (Tiles == null || _queue.Count == 0) return;
            bool copy = CanCopy;
            int staged = 0, texels = _pad * _pad;
            bool pagesChanged = false;
            var pages = _page.GetPixelData<float>(0);
            while (staged < SlotsThisFrame && _queue.Count > 0)
            {
                int s = _queue.Dequeue();
                _dirty[s] = false;
                Pad(s, staged * texels);
                _placed[staged++] = s;
                int t = Tiles.Tiles[s];
                if (pages[t] != s + 1) { pages[t] = s + 1; pagesChanged = true; }
            }
            if (copy)
            {
                var sl = _stripLift[_strip]; var ss = _stripState[_strip];
                _strip = (_strip + 1) % Strips;
                NativeArray<ushort>.Copy(_scratchLift, sl.GetPixelData<ushort>(0), staged * texels * 2);
                NativeArray<byte>.Copy(_scratchState, ss.GetPixelData<byte>(0), staged * texels * 4);
                sl.Apply(false); ss.Apply(false);
                for (int k = 0; k < staged; k++)
                {
                    int s = _placed[k], ox = s % _perRow * _pad, oy = s / _perRow * _pad;
                    Graphics.CopyTexture(sl, 0, 0, 0, k * _pad, _pad, _pad, _lift, 0, 0, ox, oy);
                    Graphics.CopyTexture(ss, 0, 0, 0, k * _pad, _pad, _pad, _stateTex, 0, 0, ox, oy);
                }
            }
            else
            {
                var lift = _lift.GetPixelData<ushort>(0);
                var state = _stateTex.GetPixelData<byte>(0);
                for (int k = 0; k < staged; k++)
                {
                    int s = _placed[k], ox = s % _perRow * _pad, oy = s / _perRow * _pad;
                    for (int j = 0; j < _pad; j++)
                    {
                        NativeArray<ushort>.Copy(_scratchLift, (k * texels + j * _pad) * 2, lift, ((oy + j) * _side + ox) * 2, _pad * 2);
                        NativeArray<byte>.Copy(_scratchState, (k * texels + j * _pad) * 4, state, ((oy + j) * _side + ox) * 4, _pad * 4);
                    }
                }
                _lift.Apply(false); _stateTex.Apply(false);
            }
            for (int k = 0; k < staged; k++) { _uploaded[_placed[k]] = true; _slotVersion[_placed[k]]++; }
            if (pagesChanged) _page.Apply(false);
            if (!_drawing) { _drawing = true; Shader.SetGlobalVector(ParamsId, new Vector4(1, FineMetres, Tiles.TileFine, _pad)); }
            Version++;
        }

        /// <summary>Slot <paramref name="s"/> with its gutters into the scratch from texel <paramref name="at"/>: the tile's own
        /// cells straight from its copy, the gutter ring from the neighbours'.</summary>
        void Pad(int s, int at)
        {
            int T = Tiles.TileFine, t = Tiles.Tiles[s], per = Tiles.Layout.PerSide;
            int gx0 = t % Tiles.TilesX * T, gy0 = t / Tiles.TilesX * T;
            ushort[] depth = _depth[s];
            byte[] state = _state[s];
            for (int j = 0; j < T; j++)
            {
                int cj = (gy0 + j) / per, row = at + (j + Gutter) * _pad + Gutter;
                System.Buffer.BlockCopy(state, j * T * 4, _scratchState, row * 4, T * 4);
                for (int i = 0; i < T; i++)
                {
                    int k = j * T + i, o = row + i;
                    if ((state[k * 4] & Simulation.FineStagingRing.Owned) == 0) { _scratchLift[o * 2] = 0; _scratchLift[o * 2 + 1] = 0; continue; }
                    int ci = (gx0 + i) / per;
                    float keep = ci < _gridWidth && cj < _gridHeight ? _keep[cj * _gridWidth + ci] : 0;
                    float lift = depth[k] / 1000f * keep;
                    _scratchLift[o * 2] = (ushort)Mathf.Clamp(Mathf.RoundToInt(lift * 1000f), 0, 65535);
                    _scratchLift[o * 2 + 1] = 65535;
                    if (lift > LiftCeiling) LiftCeiling = lift;
                }
            }
            for (int j = 0; j < _pad; j++)
            {
                bool edgeRow = j < Gutter || j >= T + Gutter;
                for (int i = 0; i < _pad; i++)
                {
                    if (!edgeRow && i == Gutter) i = T + Gutter;   // the tile's own cells are done
                    int o = at + j * _pad + i;
                    Cell(gx0 - Gutter + i, gy0 - Gutter + j, out float lift, out bool owned, out int stateAt, out byte[] st, uploadedOnly: false);
                    _scratchLift[o * 2] = owned ? (ushort)Mathf.Clamp(Mathf.RoundToInt(lift * 1000f), 0, 65535) : (ushort)0;
                    _scratchLift[o * 2 + 1] = owned ? (ushort)65535 : (ushort)0;
                    if (st != null) System.Buffer.BlockCopy(st, stateAt, _scratchState, o * 4, 4);
                    else { _scratchState[o * 4] = 0; _scratchState[o * 4 + 1] = 0; _scratchState[o * 4 + 2] = 0; _scratchState[o * 4 + 3] = 0; }
                }
            }
        }

        /// <summary>A fine cell of the whole grid (x, y in fine cells from the grid's south-west corner): its lift (metres, depth
        /// × the 8 m keep) and whether a block holds it, with its state bytes; with <paramref name="uploadedOnly"/> only from
        /// slots the GPU has (the CPU twin's view).</summary>
        void Cell(int x, int y, out float lift, out bool owned, out int stateAt, out byte[] state, bool uploadedOnly = true)
        {
            lift = 0; owned = false; stateAt = 0; state = null;
            int T = Tiles.TileFine;
            if (x < 0 || y < 0) return;
            int tx = x / T, ty = y / T;
            if (tx >= Tiles.TilesX || ty >= Tiles.TilesY) return;
            int s = Tiles.Slot[ty * Tiles.TilesX + tx];
            if (s < 0 || _depth[s] == null || (uploadedOnly && !_uploaded[s])) return;
            int k = (y - ty * T) * T + (x - tx * T);
            byte b0 = _state[s][k * 4];
            if ((b0 & Simulation.FineStagingRing.Owned) == 0) return;
            owned = true;
            state = _state[s]; stateAt = k * 4;
            int per = Tiles.Layout.PerSide, ci = x / per, cj = y / per;
            float keep = ci < _gridWidth && cj < _gridHeight ? _keep[cj * _gridWidth + ci] : 0;
            lift = _depth[s][k] / 1000f * keep;
        }

        /// <summary>
        /// The CPU twin of Snow.hlsl's lift at local x, z given the 8 m curve's <paramref name="lift8"/> there: the same
        /// B-spline over the fine cells' lift and presence, as far as the slots have gone up (the queue's tiles count, a frame
        /// early at most).
        /// </summary>
        public float LiftAt(float x, float z, float lift8, float scale)
        {
            if (!_drawing || Tiles == null) return lift8;
            float s = (x - _ring.xMin) / FineMetres - 0.5f, t = (z - _ring.yMin) / FineMetres - 0.5f;
            int i = Mathf.FloorToInt(s), j = Mathf.FloorToInt(t);
            Weights(s - i, out float a0, out float a1, out float a2, out float a3);
            Weights(t - j, out float b0, out float b1, out float b2, out float b3);
            float r = 0, g = 0;
            for (int y = 0; y < 4; y++)
            {
                float by = y == 0 ? b0 : y == 1 ? b1 : y == 2 ? b2 : b3;
                for (int xx = 0; xx < 4; xx++)
                {
                    float ax = xx == 0 ? a0 : xx == 1 ? a1 : xx == 2 ? a2 : a3;
                    Cell(i - 1 + xx, j - 1 + y, out float lift, out bool owned, out _, out _);
                    if (!owned) continue;
                    float w = ax * by;
                    r += w * Mathf.Round(Mathf.Min(lift * 1000f, 65535f)) / 1000f;
                    g += w;
                }
            }
            return Mathf.Max(0, lift8 * (1 - Mathf.Clamp01(g)) + r * scale);
        }

        static void Weights(float f, out float w0, out float w1, out float w2, out float w3)
        {
            float f2 = f * f, f3 = f2 * f;
            w0 = (1 - 3 * f + 3 * f2 - f3) / 6;
            w1 = (4 - 6 * f2 + 3 * f3) / 6;
            w2 = (1 + 3 * f + 3 * f2 - 3 * f3) / 6;
            w3 = f3 / 6;
        }

        /// <summary>The benchmark's <c>-fineab</c>: the shader draws the fine snow (true, once its first slots are up) or the 8 m
        /// snow alone (false). The CPU twin is unchanged; for the GPU's A/B only.</summary>
        public void SetDrawn(bool on)
        {
            if (Tiles == null) return;
            Shader.SetGlobalVector(ParamsId, new Vector4(on && _drawing ? 1 : 0, FineMetres, Tiles.TileFine, _pad));
        }

        /// <summary>Moves on each time slot <paramref name="s"/> goes up (a copy elsewhere follows it, the skiers').</summary>
        public int SlotVersion(int s) => _slotVersion[s];

        /// <summary>Slot <paramref name="s"/>'s own cells' lift (metres, depth × keep; −1 where no block holds the cell) into
        /// <paramref name="into"/> from <paramref name="at"/>, rows of <see cref="FineTiles.TileFine"/>.</summary>
        public void CopySlotLift(int s, NativeArray<float> into, int at)
        {
            int T = Tiles.TileFine, t = Tiles.Tiles[s], per = Tiles.Layout.PerSide;
            int gx0 = t % Tiles.TilesX * T, gy0 = t / Tiles.TilesX * T;
            ushort[] depth = _depth[s];
            byte[] state = _state[s];
            for (int j = 0; j < T; j++)
            {
                int cj = (gy0 + j) / per;
                for (int i = 0; i < T; i++)
                {
                    int k = j * T + i;
                    if (depth == null || (state[k * 4] & Simulation.FineStagingRing.Owned) == 0) { into[at + k] = -1; continue; }
                    int ci = (gx0 + i) / per;
                    float keep = ci < _gridWidth && cj < _gridHeight ? _keep[cj * _gridWidth + ci] : 0;
                    into[at + k] = depth[k] / 1000f * keep;
                }
            }
        }

        /// <summary>The fine cell's state under local x, z (type, moguls, passes ÷ 4, age; type −1 where no block holds it).</summary>
        public (int Type, int Moguls, int Passes, int Age, bool Groomed) StateAt(float x, float z)
        {
            if (Tiles == null) return (-1, 0, 0, 0, false);
            int i = Mathf.FloorToInt((x - _ring.xMin) / FineMetres), j = Mathf.FloorToInt((z - _ring.yMin) / FineMetres);
            Cell(i, j, out _, out bool owned, out int at, out byte[] st);
            if (!owned) return (-1, 0, 0, 0, false);
            return (st[at] & Simulation.FineStagingRing.TypeMask, st[at + 1], st[at + 2] * 4, st[at + 3], (st[at] & Simulation.FineStagingRing.Groomed) != 0);
        }

        static void Destroy(Texture2D t)
        {
            if (t == null) return;
            if (Application.isPlaying) Object.Destroy(t);
            else Object.DestroyImmediate(t);
        }

        public void Dispose()
        {
            Unbind();
            Shader.SetGlobalTexture(LiftId, null);
            Shader.SetGlobalTexture(StateId, null);
            Shader.SetGlobalTexture(PageId, null);
        }
    }
}
