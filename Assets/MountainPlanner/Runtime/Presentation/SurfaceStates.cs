using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Water;
using Unity.Collections;
using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The renderer's side of task 10's seams (0.3 §4.6): the snow-depth field as a texture over the ring, and
    /// the water's surface state as shader globals, read by MountainTerrain.shader. Writers change the domain
    /// objects (<see cref="Snow"/>, <see cref="Water"/>); <see cref="Sync"/> pushes what changed: the field's
    /// dirty rows into the texture's own memory (one upload), and the lakes' states only when their version moves.
    /// Task 4.09 PR B: <see cref="SetBodies"/> binds the body map (an R8 texture of body index + 1 per 8 m cell, on the
    /// snow map's grid) and the shader draws each body in its own state from a small states texture. Until a map is bound
    /// every water pixel reads body 0 (<see cref="WaterBodies.AllWater"/>), iteration 1's one shared state.
    /// </summary>
    public sealed class SurfaceStates : System.IDisposable, World.ISnowLift
    {
        public const float CellMetres = Domain.Snow.SurfaceGrid.CellMetres;
        /// <summary>Snow this deep (metres) covers the ground completely; thinner snow lets the ground show. Task 4.07 PR A2,
        /// the owner's finding 2 (2026-11-15): 2 in, as a flat meadow looks white under about 2 in of new snow (was 6 in, which
        /// drew a fresh inch or two as green while the same snow on lake ice drew white).</summary>
        public const float FullCoverMetres = 0.05f;

        static readonly int MapId = Shader.PropertyToID("_SnowDepthMap"), RectId = Shader.PropertyToID("_SnowDepthRect"),
                            ParamsId = Shader.PropertyToID("_SnowDepthParams"), BodyMapId = Shader.PropertyToID("_WaterBodyMap"),
                            BodyStatesId = Shader.PropertyToID("_WaterBodyStates"), BodyParamsId = Shader.PropertyToID("_WaterBodyParams"),
                            SurfaceId = Shader.PropertyToID("_SnowSurfaceMap"), SizeId = Shader.PropertyToID("_SnowDepthSize"),
                            LiftMapId = Shader.PropertyToID("_SnowLiftMap"), LiftParamsId = Shader.PropertyToID("_SnowLiftParams");

        /// <summary>Task 4.07 PR A3: the lift fades in over this far from the ring's edge (the outer <see cref="EdgeZeroCells"/>
        /// cells don't lift at all), so the diorama wall still meets the ground.</summary>
        public const float EdgeFadeMetres = 64;
        public const int EdgeZeroCells = 3;
        /// <summary>Task 4.07 PR A3: a lift station's footprint is dug out this far round (a point on the curved filter reads two
        /// cells either side, so the ground is flat right up to the footprint; the cells cleared reach √2 as far on the
        /// diagonal).</summary>
        public const float StationClearMetres = 16;

        public readonly SnowDepthField Snow;
        public readonly WaterBodies Water;
        /// <summary>
        /// Task 4.07 PR A2: the live snow's surface per cell, four bytes (type, quality ‰ ÷ 4, age in face hours, density kg/m³
        /// ÷ 4), as the worker stages it: the conditions layer's texture and the cursor's readout. All zero for static snow.
        /// </summary>
        public readonly byte[] Surface;
        /// <summary>
        /// Task 4.07 PR A3: how far the ground is lifted per cell (metres), as the GPU has it: the depth × <see cref="Keep"/>,
        /// written as each tile goes up, so <see cref="SnowCursor.LiftAt"/> never runs ahead of the picture.
        /// </summary>
        public readonly float[] Lift;
        /// <summary>Task 4.07 PR A3: the share of a cell's depth that lifts the ground: 0 on lakes and ponds (streams keep their
        /// snow), round lift stations and at the ring's edge, else 1 (fading in over <see cref="EdgeFadeMetres"/>).</summary>
        public readonly float[] Keep;
        /// <summary>Task 4.08 PR A: the fine trail snow's atlas and page table (bound to the layout sent to the worker).</summary>
        public readonly FineSnowAtlas Fine;
        readonly float[] _edgeKeep;
        readonly bool[] _lakeCell, _stationCell;
        readonly Texture2D _depth, _surface, _lift;
        readonly Rect _ring;
        Texture2D _bodyMap, _bodyStates;
        int _waterVersion = -1;
        /// <summary>The bound table's hash (0: none), so a caller rebinds only when the table changed.</summary>
        public ulong BoundTable { get; private set; }
        // Task 4.07 PR A2: the worker's tiles wait here for the GPU, a few a frame, each copied into the maps by rectangle
        // (a landed hour changes most tiles; uploading the whole map for each was 2 × 7.5 MB at 11 km).
        readonly System.Collections.Generic.Queue<RectInt> _tiles = new System.Collections.Generic.Queue<RectInt>();
        readonly System.Collections.Generic.HashSet<int> _tilesQueued = new System.Collections.Generic.HashSet<int>();
        // A frame's tiles are stacked into one staging strip per map (two uploads a frame, not two a tile), then copied into the
        // maps by CopyTexture; three strips in turn, so the CPU never writes one the GPU may still be reading. The rows gather
        // in managed scratch first (a native copy pins its managed source on every call: per row that was most of the cost).
        // With CopyTexture the maps' own CPU copies aren't kept per tile; a whole-map upload (Sync) refreshes them first.
        Texture2D[] _stageDepth, _stageSurface, _stageLift;
        int _stageNext;
        const int Strips = 3;
        readonly RectInt[] _placed = new RectInt[TilesPerFrame];
        float[] _scratchDepth, _scratchLift;
        byte[] _scratchSurface;
        bool _mirrorStale, _liftMirrorStale;
        // Task 4.07 PR A3: the lift waits for the first full pass of the live snow's tiles (a new game's static snowpack would
        // otherwise drop away in 512 m squares as they arrive).
        readonly bool[] _tileSeen;
        int _tilesSeen;
        /// <summary>Tiles uploaded a frame at most (BG-02 v4: ≤ 0.2 ms a frame; 32 KB a tile, so an 11 km map's 480 tiles in
        /// 80 frames).</summary>
        public const int TilesPerFrame = 6;
        const int Tile = 64;

        /// <summary>Iteration 1's states over <paramref name="ring"/> (local x/z): 12 in of snow, frozen snow-covered lakes.</summary>
        public SurfaceStates(Rect ring)
        {
            int width = Mathf.CeilToInt(ring.width / CellMetres), depth = Mathf.CeilToInt(ring.height / CellMetres);
            Snow = new SnowDepthField(width, depth, CellMetres);
            Water = new WaterBodies();
            _ring = ring;
            _depth = new Texture2D(width, depth, TextureFormat.RFloat, false, true)
            {
                name = "Snow depth", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            // Read by texel (the conditions layer blends the types' colours itself through the curved filter).
            _surface = new Texture2D(width, depth, TextureFormat.RGBA32, false, true)
            {
                name = "Snow surface", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
            };
            Surface = new byte[width * depth * 4];
            Lift = new float[width * depth];
            Keep = new float[width * depth];
            _edgeKeep = new float[width * depth];
            _lakeCell = new bool[width * depth];
            _stationCell = new bool[width * depth];
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                {
                    int fromEdge = Mathf.Min(Mathf.Min(i, width - 1 - i), Mathf.Min(j, depth - 1 - j));
                    _edgeKeep[j * width + i] = EdgeKeep(fromEdge);
                }
            System.Array.Copy(_edgeKeep, Keep, Keep.Length);
            _tileSeen = new bool[((width + Tile - 1) / Tile) * ((depth + Tile - 1) / Tile)];
            _lift = new Texture2D(width, depth, TextureFormat.RFloat, false, true)
            {
                name = "Snow lift", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            _surface.SetPixelData(Surface, 0);
            _surface.Apply(false);
            if (CanCopy)
            {
                // The staging strips now, not on the first tile (that frame was 1.4 ms).
                _stageDepth = new Texture2D[Strips];
                _stageSurface = new Texture2D[Strips];
                _stageLift = new Texture2D[Strips];
                for (int k = 0; k < Strips; k++)
                {
                    _stageLift[k] = new Texture2D(Tile, Tile * TilesPerFrame, TextureFormat.RFloat, false, true) { name = "Snow lift strip" };
                    _stageDepth[k] = new Texture2D(Tile, Tile * TilesPerFrame, TextureFormat.RFloat, false, true) { name = "Snow depth strip" };
                    _stageSurface[k] = new Texture2D(Tile, Tile * TilesPerFrame, TextureFormat.RGBA32, false, true) { name = "Snow surface strip" };
                }
                _scratchDepth = new float[Tile * Tile * TilesPerFrame];
                _scratchLift = new float[Tile * Tile * TilesPerFrame];
                _scratchSurface = new byte[Tile * Tile * TilesPerFrame * 4];
            }
            Fine = new FineSnowAtlas(ring, Keep, width, depth);
            Shader.SetGlobalTexture(MapId, _depth);
            Shader.SetGlobalTexture(SurfaceId, _surface);
            Shader.SetGlobalTexture(LiftMapId, _lift);
            Shader.SetGlobalVector(LiftParamsId, Vector4.zero);
            _bodyMap = new Texture2D(1, 1, TextureFormat.R8, false, true) { name = "Water bodies (none)", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _bodyMap.SetPixelData(new byte[1], 0);
            _bodyMap.Apply(false);
            Shader.SetGlobalTexture(BodyMapId, _bodyMap);
            Shader.SetGlobalVector(BodyParamsId, new Vector4(0, 1, 1, 1));
            Shader.SetGlobalVector(RectId, new Vector4(ring.xMin, ring.yMin, 1 / (width * CellMetres), 1 / (depth * CellMetres)));
            Shader.SetGlobalVector(SizeId, new Vector4(width, depth, 1f / width, 1f / depth));
            Sync();
            Shader.SetGlobalVector(ParamsId, new Vector4(FullCoverMetres, 0, 0, 1));
        }

        /// <summary>
        /// Task 4.07 PR A2: a tile the worker staged (SI-17), depths in whole mm and the surface's four bytes a cell, both rows of
        /// <paramref name="width"/>: into the CPU fields now, onto the GPU by <see cref="Sync"/> a few tiles a frame.
        /// </summary>
        public void TakeTile(int x, int y, int width, int height, ushort[] mm, int offset, byte[] surface, int surfaceOffset)
        {
            Snow.CopyMillimetres(mm, offset, x, y, width, height, mark: false);
            for (int j = 0; j < height; j++)
                System.Buffer.BlockCopy(surface, surfaceOffset + j * width * 4, Surface, ((y + j) * Snow.Width + x) * 4, width * 4);
            if (_surfaceStaged != null)
            {
                for (int j = 0; j < height; j++)
                    System.Buffer.BlockCopy(surface, surfaceOffset + j * width * 4, _surfaceStaged, ((y + j) * Snow.Width + x) * 4, width * 4);
                if (_gallery.width > 0) GalleryCells(x, y, width, height);
            }
            int key = (y / Tile) * 65536 + x / Tile;
            if (_tilesQueued.Add(key)) _tiles.Enqueue(new RectInt(x, y, width, height));
            int seen = (y / Tile) * ((Snow.Width + Tile - 1) / Tile) + x / Tile;
            if (seen >= 0 && seen < _tileSeen.Length && !_tileSeen[seen]) { _tileSeen[seen] = true; _tilesSeen++; }
        }

        /// <summary>Tiles still waiting for the GPU.</summary>
        public int TilesWaiting => _tiles.Count;

        Rect _gallery;
        byte[] _surfaceStaged;   // the gallery only (never in play): the surface as the worker staged it, to put back (R21)

        /// <summary>
        /// Task 4.08 PR B, the <c>-snowtypes</c> capture: the eleven snow types side by side in stripes across x inside
        /// <paramref name="window"/> (local metres), on the screen only (the simulation's snow is untouched; a tile it sends
        /// later is striped again). On the trails' fine cells too, with <paramref name="passes"/> since the last grooming, so
        /// the tracks show by type, and <paramref name="moguls"/> (0-255); no corduroy, so the type's own look shows.
        /// </summary>
        public void ShowTypeGallery(Rect window, int passes, int moguls = 0)
        {
            Rect old = _gallery;
            _surfaceStaged ??= (byte[])Surface.Clone();
            _gallery = window;
            int w = Snow.Width, h = Snow.Height;
            for (int ty = 0; ty < h; ty += Tile)
                for (int tx = 0; tx < w; tx += Tile)
                {
                    int tw = Mathf.Min(Tile, w - tx), th = Mathf.Min(Tile, h - ty);
                    // The window before this one goes back to the snow the worker staged (R21), then the new stripes.
                    bool was = old.width > 0 && old.Overlaps(new Rect(_ring.xMin + tx * CellMetres, _ring.yMin + ty * CellMetres, tw * CellMetres, th * CellMetres));
                    if (was)
                        for (int j = 0; j < th; j++)
                            System.Buffer.BlockCopy(_surfaceStaged, ((ty + j) * w + tx) * 4, Surface, ((ty + j) * w + tx) * 4, tw * 4);
                    bool now = window.width > 0 && GalleryCells(tx, ty, tw, th);
                    if (!was && !now) continue;
                    int key = (ty / Tile) * 65536 + tx / Tile;
                    if (_tilesQueued.Add(key)) _tiles.Enqueue(new RectInt(tx, ty, tw, th));
                }
            Fine.ShowTypeGallery(window, passes, moguls);
        }

        /// <summary>The gallery off: every cell back to the snow the worker staged.</summary>
        public void ClearTypeGallery() => ShowTypeGallery(default, 0);

        /// <summary>The gallery's type (1-11) at local x, or 0 outside it.</summary>
        internal static int GalleryType(Rect window, float x, float z)
        {
            if (window.width <= 0 || !window.Contains(new Vector2(x, z))) return 0;
            return 1 + Mathf.Clamp(Mathf.FloorToInt((x - window.xMin) / window.width * 11), 0, 10);
        }

        bool GalleryCells(int x, int y, int width, int height)
        {
            bool any = false;
            for (int j = y; j < y + height; j++)
                for (int i = x; i < x + width; i++)
                {
                    int type = GalleryType(_gallery, _ring.xMin + (i + 0.5f) * CellMetres, _ring.yMin + (j + 0.5f) * CellMetres);
                    if (type == 0) continue;
                    int k = (j * Snow.Width + i) * 4;
                    Surface[k] = (byte)type;
                    Surface[k + 2] = 0;   // fresh: no dulling with age
                    any = true;
                }
            return any;
        }

        /// <summary>The surface's bytes at a cell (type, quality ÷ 4, age, density ÷ 4): the Snow conditions map's.</summary>
        public (byte Type, byte Quality, byte Age, byte Density) ConditionsAt(int x, int y)
        {
            int k = (y * Snow.Width + x) * 4;
            return (Surface[k], Surface[k + 1], Surface[k + 2], Surface[k + 3]);
        }

        static readonly System.Diagnostics.Stopwatch UploadWatch = new System.Diagnostics.Stopwatch();

        /// <summary>The main thread's time for the last frame's tile uploads and the most for any frame (ms; BG-11's ≤ 0.2).</summary>
        public double LastUploadMs { get; private set; }
        public double MaxUploadMs { get; private set; }
        /// <summary>The first frame's uploads (it makes the staging textures), then every later frame's: count, total, over 0.2
        /// ms and over 1 ms, and the most.</summary>
        public double FirstUploadMs { get; private set; } = -1;
        public int UploadFrames { get; private set; }
        public int UploadFramesOverBudget { get; private set; }
        public int UploadFramesOverMs { get; private set; }
        public double UploadTotalMs { get; private set; }
        public double UploadMostLaterMs { get; private set; }

        void UploadTiles()
        {
            if (_tiles.Count == 0 && Fine.SlotsWaiting == 0) return;
            UploadWatch.Restart();
            if (_tiles.Count > 0) UploadTilesNow();
            if (Fine.SlotsWaiting > 0) { Fine.Upload(); LiftVersion++; }   // task 4.08: the fine snow, in the same budget
            LastUploadMs = UploadWatch.Elapsed.TotalMilliseconds;
            if (LastUploadMs > MaxUploadMs) MaxUploadMs = LastUploadMs;
            if (FirstUploadMs < 0) { FirstUploadMs = LastUploadMs; return; }
            UploadFrames++;
            UploadTotalMs += LastUploadMs;
            if (LastUploadMs > 0.2) UploadFramesOverBudget++;
            if (LastUploadMs > 1) UploadFramesOverMs++;
            if (LastUploadMs > UploadMostLaterMs) UploadMostLaterMs = LastUploadMs;
        }

        static bool CanCopy => (SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) != 0;

        void UploadTilesNow()
        {
            bool copy = _stageDepth != null;
            float[] cells = Snow.Cells;
            int fw = Snow.Width;
            bool wholeSurface = false;
            int staged = 0;
            var placed = _placed;
            for (int n = 0; n < TilesPerFrame && _tiles.Count > 0; n++)
            {
                var r = _tiles.Dequeue();
                _tilesQueued.Remove((r.y / Tile) * 65536 + r.x / Tile);
                if (!copy)
                {
                    // No CopyTexture: the textures' own CPU copies take the tile and the whole maps go up (Sync's dirty rows).
                    NativeArray<float> depthPixels = _depth.GetPixelData<float>(0);
                    NativeArray<byte> surfacePixels = _surface.GetPixelData<byte>(0);
                    for (int j = r.y; j < r.yMax; j++)
                    {
                        NativeArray<float>.Copy(cells, j * fw + r.x, depthPixels, j * fw + r.x, r.width);
                        NativeArray<byte>.Copy(Surface, (j * fw + r.x) * 4, surfacePixels, (j * fw + r.x) * 4, r.width * 4);
                    }
                    Snow.MarkChanged(r.x, r.y, r.width, r.height);   // Sync's dirty rows write the lift too
                    wholeSurface = true;
                    continue;
                }
                _mirrorStale = true;
                // Tile k's rows start at row k × Tile, Tile cells apart (an edge tile's narrower rows leave the rest unused).
                int at = staged * Tile * Tile;
                _liftMirrorStale = true;
                for (int j = 0; j < r.height; j++)
                {
                    int row = (r.y + j) * fw + r.x, to = at + j * Tile;
                    for (int i = 0; i < r.width; i++)
                    {
                        float lift = cells[row + i] * Keep[row + i];
                        _scratchLift[to + i] = Lift[row + i] = lift;
                        if (lift > _liftCeiling) _liftCeiling = lift;
                    }
                    System.Array.Copy(cells, (r.y + j) * fw + r.x, _scratchDepth, at + j * Tile, r.width);
                    System.Buffer.BlockCopy(Surface, ((r.y + j) * fw + r.x) * 4, _scratchSurface, (at + j * Tile) * 4, r.width * 4);
                }
                placed[staged++] = r;
            }
            if (staged > 0)
            {
                var sd = _stageDepth[_stageNext];
                var ss = _stageSurface[_stageNext];
                var sl = _stageLift[_stageNext];
                _stageNext = (_stageNext + 1) % Strips;
                NativeArray<float>.Copy(_scratchLift, sl.GetPixelData<float>(0), staged * Tile * Tile);
                sl.Apply(false);
                NativeArray<float>.Copy(_scratchDepth, sd.GetPixelData<float>(0), staged * Tile * Tile);
                NativeArray<byte>.Copy(_scratchSurface, ss.GetPixelData<byte>(0), staged * Tile * Tile * 4);
                sd.Apply(false);
                ss.Apply(false);
                for (int k = 0; k < staged; k++)
                {
                    var r = placed[k];
                    Graphics.CopyTexture(sd, 0, 0, 0, k * Tile, r.width, r.height, _depth, 0, 0, r.x, r.y);
                    Graphics.CopyTexture(ss, 0, 0, 0, k * Tile, r.width, r.height, _surface, 0, 0, r.x, r.y);
                    Graphics.CopyTexture(sl, 0, 0, 0, k * Tile, r.width, r.height, _lift, 0, 0, r.x, r.y);
                }
                LiftVersion++;
            }
            if (wholeSurface) _surface.Apply(false);
        }

        /// <summary>Pushes changes to the GPU. Cheap when nothing changed; call it once a frame.</summary>
        public void Sync()
        {
            UploadTiles();
            var (x, y, w, h) = Snow.TakeDirty();
            if (w > 0)
            {
                NativeArray<float> pixels = _depth.GetPixelData<float>(0);
                float[] cells = Snow.Cells;
                if (_mirrorStale)
                {
                    // Tiles went up by CopyTexture, not through this copy: take the whole field, so the upload brings none back.
                    NativeArray<float>.Copy(cells, pixels, cells.Length);
                    _mirrorStale = false;
                }
                else
                    for (int j = y; j < y + h; j++)
                        NativeArray<float>.Copy(cells, j * Snow.Width + x, pixels, j * Snow.Width + x, w);
                _depth.Apply(false);
                PushLift(x, y, w, h);
            }
            if (!_liftReady && FirstPassDone && _tiles.Count == 0) _liftReady = true;
            if (_liftAuto) SwitchLift(_snowShown && _liftReady);
            if (Water.Version != _waterVersion)
            {
                _waterVersion = Water.Version;
                if (_bodyStates == null || _bodyStates.width != Water.Count)
                {
                    DestroyTexture(_bodyStates);
                    _bodyStates = new Texture2D(Water.Count, 1, TextureFormat.RGBAHalf, false, true)
                    {
                        name = "Water body states", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                    };
                    Shader.SetGlobalTexture(BodyStatesId, _bodyStates);
                }
                NativeArray<ushort> texels = _bodyStates.GetPixelData<ushort>(0);
                for (int b = 0; b < Water.Count; b++)
                {
                    var s = Water[b];
                    texels[4 * b] = Mathf.FloatToHalf((int)s.State + 1);
                    texels[4 * b + 1] = Mathf.FloatToHalf(s.IceMetres);
                    texels[4 * b + 2] = Mathf.FloatToHalf(s.SnowMetres);
                    texels[4 * b + 3] = 0;
                }
                _bodyStates.Apply(false);
                StateUploads++;
            }
        }

        /// <summary>
        /// Task 4.07 PR A3: whether the snow shell stands on the snow, 0 or 1: the shader's lift and <see cref="SnowCursor.LiftAt"/>
        /// are the lift cells × this. 1 while the Snow map layer is shown (<see cref="SnowShown"/>) and the live snow's first
        /// full pass has come in (<see cref="FirstPassDone"/>), 0 otherwise. Round 3 (owner, 2026-11-17): it switches at once;
        /// the ground never moves, and the Snow layer shows or hides the shell (GroundLayers).
        /// </summary>
        public float LiftScale { get; private set; }

        /// <summary>The snow shell stands on the snow (<see cref="LiftScale"/> is 1). Setting it switches the lift and takes
        /// over from the Snow layer for good (<c>-liftab</c>, tests).</summary>
        public bool LiftOn
        {
            get => LiftScale > 0;
            set
            {
                _liftAuto = false;
                SetLiftScale(value ? 1 : 0);
                LiftVersion++;
            }
        }

        /// <summary>The Snow map layer is on (<see cref="MapLayers"/> sets it). On until told.</summary>
        public bool SnowShown { get => _snowShown; set => _snowShown = value; }

        bool _liftAuto = true, _liftReady, _snowShown = true;

        void SwitchLift(bool on)
        {
            if (LiftOn == on) return;
            SetLiftScale(on ? 1 : 0);
            LiftVersion++;   // what is cached on the snow (lines, labels, the traffic) is laid again once
        }

        void SetLiftScale(float scale)
        {
            LiftScale = scale;
            Shader.SetGlobalVector(LiftParamsId, new Vector4(scale, 0, 0, 0));
        }

        /// <summary>Task 4.07 PR A3: the lift at local x, z, as the terrain shader draws it (<see cref="SnowCursor.LiftAt"/>).</summary>
        public float LiftAt(float x, float z) => SnowCursor.LiftAt(this, _ring, x, z);

        /// <summary>Every tile of the field has come in from the worker at least once (the lift's own switch waits for it).</summary>
        public bool FirstPassDone => _tilesSeen == _tileSeen.Length;

        static float EdgeKeep(int cellsFromEdge)
        {
            float metres = (cellsFromEdge - EdgeZeroCells + 0.5f) * CellMetres;
            return Mathf.Clamp01(metres / (EdgeFadeMetres - EdgeZeroCells * CellMetres));
        }

        /// <summary>The lift's cells over a rectangle, from the depth and the keep, onto the lift texture's own CPU copy.</summary>
        void PushLift(int x, int y, int w, int h)
        {
            float[] cells = Snow.Cells;
            int fw = Snow.Width;
            NativeArray<float> pixels = _lift.GetPixelData<float>(0);
            if (_liftMirrorStale)
            {
                // Tiles went up by CopyTexture, not through this copy: the whole lift, from the CPU cells those tiles wrote.
                x = 0; y = 0; w = fw; h = Snow.Height;
                _liftMirrorStale = false;
            }
            for (int j = y; j < y + h; j++)
            {
                int row = j * fw;
                for (int i = x; i < x + w; i++)
                {
                    float lift = cells[row + i] * Keep[row + i];
                    Lift[row + i] = lift;
                    if (lift > _liftCeiling) _liftCeiling = lift;
                }
                NativeArray<float>.Copy(Lift, row + x, pixels, row + x, w);
            }
            _lift.Apply(false);
            LiftUploads++;
            LiftVersion++;
        }

        /// <summary>
        /// The deepest lift cell this field has held, metres (it only grows): the snow shell's culling boxes are padded by
        /// at least this (finding A3-6, <see cref="World.TerrainTiles.PadShellBounds"/>). The shader's lift blends cells, so
        /// it never stands higher.
        /// </summary>
        public float LiftCeiling { get => Mathf.Max(_liftCeiling, Fine?.LiftCeiling ?? 0); private set => _liftCeiling = value; }
        float _liftCeiling;

        /// <summary>Moves on whenever the lift the GPU has changes (its cells or its switch): the skiers' copy follows it.</summary>
        public int LiftVersion { get; private set; }

        /// <summary>Uploads of the lift texture through its CPU copy (tests).</summary>
        public int LiftUploads { get; private set; }

        void RebuildKeep()
        {
            for (int c = 0; c < Keep.Length; c++) Keep[c] = _lakeCell[c] || _stationCell[c] ? 0 : _edgeKeep[c];
            Fine?.KeepChanged();
            _liftMirrorStale = true;
            PushLift(0, 0, Snow.Width, Snow.Height);
        }

        /// <summary>
        /// Task 4.07 PR A3: footprints kept clear of the snow (local x/z); cells within <see cref="StationClearMetres"/> of one
        /// don't lift. Given each carpet's belt and every chair and tow terminal (task 4.26 BB15, owner 2026-11-22: keep
        /// terminals clear, which undoes 4.07 A3 round 3).
        /// </summary>
        public void SetStations(System.Collections.Generic.IReadOnlyList<Footprint> stations)
        {
            System.Array.Clear(_stationCell, 0, _stationCell.Length);
            int fw = Snow.Width, fh = Snow.Height;
            if (stations != null)
                foreach (var f in stations)
                {
                    float reach = Mathf.Max(f.Behind, f.Ahead) + f.HalfWidth + StationClearMetres + CellMetres;
                    int i0 = Mathf.Max(0, Mathf.FloorToInt((f.Centre.x - reach - _ring.xMin) / CellMetres));
                    int i1 = Mathf.Min(fw - 1, Mathf.FloorToInt((f.Centre.x + reach - _ring.xMin) / CellMetres));
                    int j0 = Mathf.Max(0, Mathf.FloorToInt((f.Centre.y - reach - _ring.yMin) / CellMetres));
                    int j1 = Mathf.Min(fh - 1, Mathf.FloorToInt((f.Centre.y + reach - _ring.yMin) / CellMetres));
                    var along = f.Along.sqrMagnitude > 1e-6f ? f.Along.normalized : Vector2.up;
                    var side = new Vector2(-along.y, along.x);
                    for (int j = j0; j <= j1; j++)
                        for (int i = i0; i <= i1; i++)
                        {
                            // A point on the curve reads cells up to two away along each grid axis (16 m each way, so up to
                            // 16 m × √2 on the diagonal): a cell whose centre is that close to the footprint can't lift it.
                            var d = new Vector2(_ring.xMin + (i + 0.5f) * CellMetres, _ring.yMin + (j + 0.5f) * CellMetres) - f.Centre;
                            float a = Vector2.Dot(d, along), s = Mathf.Abs(Vector2.Dot(d, side));
                            float outA = a > f.Ahead ? a - f.Ahead : a < -f.Behind ? -f.Behind - a : 0;
                            float outS = Mathf.Max(0, s - f.HalfWidth);
                            if (Mathf.Sqrt(outA * outA + outS * outS) <= StationClearMetres * 1.415f) _stationCell[j * fw + i] = true;
                        }
                }
            RebuildKeep();
        }

        /// <summary>A lift station's footprint for <see cref="SetStations"/>: the bullwheel's spot, the unit direction toward the
        /// line, and the footprint behind, ahead and to either side (metres).</summary>
        public struct Footprint
        {
            public Vector2 Centre, Along;
            public float Behind, Ahead, HalfWidth;
        }

        /// <summary>How many times the states texture has been uploaded (tests: once per version).</summary>
        public int StateUploads { get; private set; }

        /// <summary>
        /// Binds a body table (task 4.09 PR B): <paramref name="map"/> is its mask dilated one cell
        /// (<see cref="LakeSurfaces.Dilated"/>), on the snow map's grid. A table on any other grid is refused with an error
        /// (never silently). Resizes <see cref="Water"/> to the table's bodies. Returns whether it was bound.
        /// </summary>
        public bool SetBodies(WaterBodyTable table, byte[] map)
        {
            if (table == null || map == null) return false;
            bool grid = table.Width == Snow.Width && table.Depth == Snow.Height && map.Length == table.Width * table.Depth
                        && Mathf.Abs(table.OriginXMm / 1000f - _ring.xMin) <= 0.001f && Mathf.Abs(table.OriginYMm / 1000f - _ring.yMin) <= 0.001f;
            if (!grid)
            {
                Debug.LogError($"[SurfaceStates] the water bodies' grid ({table.Width}x{table.Depth} at {table.OriginXMm} mm, {table.OriginYMm} mm) " +
                               $"isn't the snow map's ({Snow.Width}x{Snow.Height} at {_ring.xMin:F3} m, {_ring.yMin:F3} m): the lakes keep one state");
                return false;
            }
            if (_bodyMap.width != table.Width || _bodyMap.height != table.Depth)
            {
                DestroyTexture(_bodyMap);
                _bodyMap = new Texture2D(table.Width, table.Depth, TextureFormat.R8, false, true)
                {
                    name = "Water bodies", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                };
                Shader.SetGlobalTexture(BodyMapId, _bodyMap);
            }
            _bodyMap.SetPixelData(map, 0);
            _bodyMap.Apply(false);
            // Task 4.07 PR A3: water stays on the earth (lakes and ponds, grown one cell onto the shore; streams keep their snow).
            for (int c = 0; c < _lakeCell.Length; c++) _lakeCell[c] = map[c] >= 2;
            RebuildKeep();
            if (Water.Count != table.Count) Water.Resize(table.Count);
            Shader.SetGlobalVector(BodyParamsId, new Vector4(1, table.Count, table.Width, table.Depth));
            BoundTable = table.Hash;
            Sync();
            return true;
        }

        // ---------------- the ground's moisture (task 4.28) ----------------

        static readonly int MoistureMapId = Shader.PropertyToID("_GroundMoistureMap"), MoistureParamsId = Shader.PropertyToID("_GroundMoistureParams");
        Texture2D _moisture;
        /// <summary>The moisture map is bound: the grass varies with the land.</summary>
        public bool HasMoisture => _moisture != null;

        /// <summary>
        /// Binds the ground's moisture (<see cref="MountainPlanner.Domain.Cover.GroundMoisture"/>, a byte per cell of this grid,
        /// 128 ordinary ground): the grass draws lush where it is wet and straw where it is dry. Read smoothly between cells; a
        /// map of any other size is refused. Returns whether it was bound.
        /// </summary>
        public bool SetMoisture(byte[] map)
        {
            if (map == null || map.Length != Snow.Width * Snow.Height) return false;
            if (_moisture == null)
            {
                _moisture = new Texture2D(Snow.Width, Snow.Height, TextureFormat.R8, false, true)
                {
                    name = "Ground moisture", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                };
                Shader.SetGlobalTexture(MoistureMapId, _moisture);
            }
            _moisture.SetPixelData(map, 0);
            _moisture.Apply(false);
            Shader.SetGlobalVector(MoistureParamsId, new Vector4(GroundLayers.GrassVariation ? 1 : 0, MoistureView ? 1 : 0, 0, 0));
            return true;
        }

        /// <summary>A check's view of the map itself in place of the ground's colour (<c>-moisturemap</c>, demo 154).</summary>
        public static bool MoistureView { get; private set; }

        public void ShowMoisture(bool on)
        {
            MoistureView = on;
            Shader.SetGlobalVector(MoistureParamsId, new Vector4(_moisture != null && GroundLayers.GrassVariation ? 1 : 0, on ? 1 : 0, 0, 0));
        }

        // ---------------- the trails' grass (task 4.29 PR B) ----------------

        static readonly int TrailGrassMapId = Shader.PropertyToID("_TrailGrassMap"), TrailGrassStatesId = Shader.PropertyToID("_TrailGrassStates"),
                            TrailGrassParamsId = Shader.PropertyToID("_TrailGrassParams");
        /// <summary>The grass table's width in texels (layout trail t is at t mod this, t ÷ this).</summary>
        public const int TrailGrassRow = 256;
        /// <summary>A trail whose grass isn't known yet (the snapshot's <c>NoGrass</c>): drawn mown, as before 4.29.</summary>
        public const byte TrailGrassUnknown = 255;
        Texture2D _trailGrassMap, _trailGrassStates;
        byte[] _trailGrass = System.Array.Empty<byte>();
        int _trailGrassCount;
        /// <summary>Uploads of the trails' grass table (tests: one per change).</summary>
        public int TrailGrassUploads { get; private set; }
        /// <summary>The trails' map is bound (<see cref="SetTrailGrassMap"/>).</summary>
        public bool HasTrailGrass => _trailGrassMap != null;

        /// <summary>
        /// Binds which trail each 8 m cell draws as: <paramref name="owners"/> is <see cref="Domain.Snow.TrailCellOwners"/>'s
        /// map of <paramref name="layout"/> (1 + the layout trail, 0 none). A layout on any other grid is refused with an error
        /// (never silently). Every trail's grass reads unknown (drawn mown) until <see cref="SetTrailGrass"/>. Returns whether
        /// it was bound.
        /// </summary>
        public bool SetTrailGrassMap(TrailSnowLayout layout, ushort[] owners)
        {
            if (layout == null || owners == null) return false;
            bool grid = layout.GridWidth == Snow.Width && layout.GridHeight == Snow.Height && owners.Length == Snow.Width * Snow.Height
                        && Mathf.Abs(layout.OriginXMm / 1000f - _ring.xMin) <= 0.001f && Mathf.Abs(layout.OriginYMm / 1000f - _ring.yMin) <= 0.001f;
            if (!grid)
            {
                Debug.LogError($"[SurfaceStates] the trail layout's grid ({layout.GridWidth}x{layout.GridHeight} at {layout.OriginXMm} mm, {layout.OriginYMm} mm) " +
                               $"isn't the snow map's ({Snow.Width}x{Snow.Height} at {_ring.xMin:F3} m, {_ring.yMin:F3} m): the trails draw mown");
                return false;
            }
            int trails = layout.Trails;
            if (_trailGrassMap == null)
            {
                _trailGrassMap = new Texture2D(Snow.Width, Snow.Height, TextureFormat.R16, false, true)
                {
                    name = "Trail grass owners", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                };
                Shader.SetGlobalTexture(TrailGrassMapId, _trailGrassMap);
            }
            _trailGrassMap.SetPixelData(owners, 0);
            _trailGrassMap.Apply(false);
            int rows = Mathf.Max(1, (trails + TrailGrassRow - 1) / TrailGrassRow);
            if (_trailGrassStates == null || _trailGrassStates.height != rows)
            {
                DestroyTexture(_trailGrassStates);
                _trailGrassStates = new Texture2D(TrailGrassRow, rows, TextureFormat.R8, false, true)
                {
                    name = "Trail grass", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                };
                Shader.SetGlobalTexture(TrailGrassStatesId, _trailGrassStates);
            }
            if (_trailGrass.Length != TrailGrassRow * rows) _trailGrass = new byte[TrailGrassRow * rows];
            for (int t = 0; t < _trailGrass.Length; t++) _trailGrass[t] = TrailGrassUnknown;
            _trailGrassCount = trails;
            UploadTrailGrass();
            PushTrailGrassParams();
            return true;
        }

        /// <summary>
        /// Each layout trail's grass, % of brush (0–100, <see cref="TrailGrassUnknown"/> not known), in the bound map's order.
        /// Uploads only when a value changed; cheap otherwise. Returns whether it uploaded.
        /// </summary>
        public bool SetTrailGrass(byte[] grass, int count)
        {
            if (_trailGrassMap == null || grass == null) return false;
            int n = Mathf.Min(Mathf.Min(count, grass.Length), _trailGrassCount);
            bool changed = false;
            for (int t = 0; t < n; t++)
                if (_trailGrass[t] != grass[t]) { _trailGrass[t] = grass[t]; changed = true; }
            if (changed) UploadTrailGrass();
            return changed;
        }

        /// <summary>The grass the table holds for a layout trail (tests).</summary>
        public byte TrailGrassOf(int trail) => trail >= 0 && trail < _trailGrassCount ? _trailGrass[trail] : TrailGrassUnknown;

        void UploadTrailGrass()
        {
            _trailGrassStates.SetPixelData(_trailGrass, 0);
            _trailGrassStates.Apply(false);
            TrailGrassUploads++;
        }

        /// <summary>The shader's switch: the trails' grass shows with the ground's variation (<c>-plaingrass</c> draws every
        /// trail mown, as before 4.28 and 4.29).</summary>
        public void PushTrailGrassParams() =>
            Shader.SetGlobalVector(TrailGrassParamsId, _trailGrassMap != null && GroundLayers.GrassVariation
                ? new Vector4(1, _trailGrassCount, Snow.Width, Snow.Height) : Vector4.zero);

        static void DestroyTexture(Texture2D t)
        {
            if (t == null) return;
            if (Application.isPlaying) Object.Destroy(t);
            else Object.DestroyImmediate(t);
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(LiftParamsId, Vector4.zero);
            Shader.SetGlobalTexture(LiftMapId, null);
            Shader.SetGlobalVector(BodyParamsId, Vector4.zero);
            Shader.SetGlobalTexture(BodyMapId, null);
            Shader.SetGlobalTexture(BodyStatesId, null);
            DestroyTexture(_bodyMap);
            DestroyTexture(_bodyStates);
            _bodyMap = _bodyStates = null;
            BoundTable = 0;
            Shader.SetGlobalVector(MoistureParamsId, Vector4.zero);
            Shader.SetGlobalTexture(MoistureMapId, null);
            DestroyTexture(_moisture);
            _moisture = null;
            MoistureView = false;
            Fine.Dispose();
            Shader.SetGlobalVector(TrailGrassParamsId, Vector4.zero);
            Shader.SetGlobalTexture(TrailGrassMapId, null);
            Shader.SetGlobalTexture(TrailGrassStatesId, null);
            DestroyTexture(_trailGrassMap);
            DestroyTexture(_trailGrassStates);
            _trailGrassMap = _trailGrassStates = null;
            _trailGrassCount = 0;
            DestroyTexture(_depth);
            DestroyTexture(_surface);
            DestroyTexture(_lift);
            if (_stageDepth != null)
                for (int k = 0; k < _stageDepth.Length; k++) { DestroyTexture(_stageDepth[k]); DestroyTexture(_stageSurface[k]); DestroyTexture(_stageLift[k]); }
        }
    }
}
