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
    /// dirty rows into the texture's own memory (one upload), and the lake state only when its version moves.
    /// Iteration 1 renders the one shared state of <see cref="WaterBodies.AllWater"/>.
    /// </summary>
    public sealed class SurfaceStates : System.IDisposable
    {
        public const float CellMetres = Domain.Snow.SurfaceGrid.CellMetres;
        /// <summary>Snow this deep (metres) covers the ground completely; thinner snow lets the ground show.</summary>
        public const float FullCoverMetres = 0.15f;

        static readonly int MapId = Shader.PropertyToID("_SnowDepthMap"), RectId = Shader.PropertyToID("_SnowDepthRect"),
                            ParamsId = Shader.PropertyToID("_SnowDepthParams"), LakeId = Shader.PropertyToID("_LakeState"),
                            SurfaceId = Shader.PropertyToID("_SnowSurfaceMap"), SizeId = Shader.PropertyToID("_SnowDepthSize");

        public readonly SnowDepthField Snow;
        public readonly WaterBodies Water;
        /// <summary>
        /// Task 4.07 PR A2: the live snow's surface per cell, four bytes (type, quality ‰ ÷ 4, age in face hours, density kg/m³
        /// ÷ 4), as the worker stages it: the conditions layer's texture and the cursor's readout. All zero for static snow.
        /// </summary>
        public readonly byte[] Surface;
        readonly Texture2D _depth, _surface;
        int _waterVersion = -1;
        // Task 4.07 PR A2: the worker's tiles wait here for the GPU, a few a frame, each copied into the maps by rectangle
        // (a landed hour changes most tiles; uploading the whole map for each was 2 × 7.5 MB at 11 km).
        readonly System.Collections.Generic.Queue<RectInt> _tiles = new System.Collections.Generic.Queue<RectInt>();
        readonly System.Collections.Generic.HashSet<int> _tilesQueued = new System.Collections.Generic.HashSet<int>();
        // A frame's tiles are stacked into one staging strip per map (two uploads a frame, not two a tile), then copied into the
        // maps by CopyTexture; three strips in turn, so the CPU never writes one the GPU may still be reading. The rows gather
        // in managed scratch first (a native copy pins its managed source on every call: per row that was most of the cost).
        // With CopyTexture the maps' own CPU copies aren't kept per tile; a whole-map upload (Sync) refreshes them first.
        Texture2D[] _stageDepth, _stageSurface;
        int _stageNext;
        const int Strips = 3;
        readonly RectInt[] _placed = new RectInt[TilesPerFrame];
        float[] _scratchDepth;
        byte[] _scratchSurface;
        bool _mirrorStale;
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
            _surface.SetPixelData(Surface, 0);
            _surface.Apply(false);
            if (CanCopy)
            {
                // The staging strips now, not on the first tile (that frame was 1.4 ms).
                _stageDepth = new Texture2D[Strips];
                _stageSurface = new Texture2D[Strips];
                for (int k = 0; k < Strips; k++)
                {
                    _stageDepth[k] = new Texture2D(Tile, Tile * TilesPerFrame, TextureFormat.RFloat, false, true) { name = "Snow depth strip" };
                    _stageSurface[k] = new Texture2D(Tile, Tile * TilesPerFrame, TextureFormat.RGBA32, false, true) { name = "Snow surface strip" };
                }
                _scratchDepth = new float[Tile * Tile * TilesPerFrame];
                _scratchSurface = new byte[Tile * Tile * TilesPerFrame * 4];
            }
            Shader.SetGlobalTexture(MapId, _depth);
            Shader.SetGlobalTexture(SurfaceId, _surface);
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
            int key = (y / Tile) * 65536 + x / Tile;
            if (_tilesQueued.Add(key)) _tiles.Enqueue(new RectInt(x, y, width, height));
        }

        /// <summary>Tiles still waiting for the GPU.</summary>
        public int TilesWaiting => _tiles.Count;

        /// <summary>The surface's bytes at a cell (type, quality ÷ 4, age, density ÷ 4).</summary>
        public (byte Type, byte Quality, byte Age, byte Density) SurfaceAt(int x, int y)
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
            if (_tiles.Count == 0) return;
            UploadWatch.Restart();
            UploadTilesNow();
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
                    Snow.MarkChanged(r.x, r.y, r.width, r.height);
                    wholeSurface = true;
                    continue;
                }
                _mirrorStale = true;
                // Tile k's rows start at row k × Tile, Tile cells apart (an edge tile's narrower rows leave the rest unused).
                int at = staged * Tile * Tile;
                for (int j = 0; j < r.height; j++)
                {
                    System.Array.Copy(cells, (r.y + j) * fw + r.x, _scratchDepth, at + j * Tile, r.width);
                    System.Buffer.BlockCopy(Surface, ((r.y + j) * fw + r.x) * 4, _scratchSurface, (at + j * Tile) * 4, r.width * 4);
                }
                placed[staged++] = r;
            }
            if (staged > 0)
            {
                var sd = _stageDepth[_stageNext];
                var ss = _stageSurface[_stageNext];
                _stageNext = (_stageNext + 1) % Strips;
                NativeArray<float>.Copy(_scratchDepth, sd.GetPixelData<float>(0), staged * Tile * Tile);
                NativeArray<byte>.Copy(_scratchSurface, ss.GetPixelData<byte>(0), staged * Tile * Tile * 4);
                sd.Apply(false);
                ss.Apply(false);
                for (int k = 0; k < staged; k++)
                {
                    var r = placed[k];
                    Graphics.CopyTexture(sd, 0, 0, 0, k * Tile, r.width, r.height, _depth, 0, 0, r.x, r.y);
                    Graphics.CopyTexture(ss, 0, 0, 0, k * Tile, r.width, r.height, _surface, 0, 0, r.x, r.y);
                }
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
            }
            if (Water.Version != _waterVersion)
            {
                _waterVersion = Water.Version;
                var s = Water[WaterBodies.AllWater];
                Shader.SetGlobalVector(LakeId, new Vector4((int)s.State + 1, s.IceMetres, s.SnowMetres, 0));
            }
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(LakeId, Vector4.zero);
            if (_depth == null) return;
            void Drop(Object o)
            {
                if (o == null) return;
                if (Application.isPlaying) Object.Destroy(o);
                else Object.DestroyImmediate(o);
            }
            Drop(_depth);
            Drop(_surface);
            if (_stageDepth != null)
                for (int k = 0; k < _stageDepth.Length; k++) { Drop(_stageDepth[k]); Drop(_stageSurface[k]); }
        }
    }
}
