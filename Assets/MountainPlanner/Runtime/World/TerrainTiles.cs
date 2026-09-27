using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using UnityEngine;

namespace MountainPlanner.World
{
    /// <summary>
    /// Heights of the resort in its local frame (x east, z north, y elevation in metres). Everything that
    /// needs the ground (camera, forest, future lifts) asks this, so a custom mesh could replace Unity
    /// Terrain without touching them (0.3 §4.3, "Why Unity Terrain").
    /// </summary>
    public interface ITerrainSurface
    {
        /// <summary>Elevation in metres at a local position; NaN outside the terrain.</summary>
        float HeightAt(float x, float z);

        /// <summary>The terrain's footprint in local x/z.</summary>
        Rect Footprint { get; }
    }

    /// <summary>Per-quality-preset terrain detail (0.3 §8; the graphics settings in 0.4 pick one).</summary>
    public enum TerrainDetail { Low, Medium, High, Ultra }

    public static class TerrainDetailSettings
    {
        /// <summary>Screen-space error in pixels before Unity lowers a patch's detail (lower = sharper, slower).</summary>
        public static float PixelError(TerrainDetail d) => d switch
        {
            TerrainDetail.Low => 16f,
            TerrainDetail.Medium => 8f,
            TerrainDetail.High => 4f,
            _ => 2f,
        };

        public static float BasemapDistance(TerrainDetail d) => d switch
        {
            TerrainDetail.Low => 800f,
            TerrainDetail.Medium => 1500f,
            TerrainDetail.High => 2500f,
            _ => 4000f,
        };

        public static void Apply(Terrain terrain, TerrainDetail detail)
        {
            terrain.heightmapPixelError = PixelError(detail);
            terrain.basemapDistance = BasemapDistance(detail);
        }
    }

    /// <summary>
    /// Turns a package's terrain cache into Unity Terrain tiles (0.3 §4.3 step 4; task 06): one Terrain
    /// per 1,024 m tile, linked to its neighbours so level of detail stitches across edges.
    ///
    /// Every tile is loaded at 1,025²: the 513² ring tiles are upsampled on load (exact at their own
    /// vertices, midpoints in between; the same rule the cache uses where 1 m meets 2 m), because
    /// Unity stitches neighbours' LOD only between equal resolutions.
    /// </summary>
    public static class TerrainTiles
    {
        public const int Resolution = 1025;

        /// <summary>
        /// GPU-instanced terrain drawing. Off: instanced tiles created at runtime render flat-lit and untextured
        /// in the URP player (per-pixel normals and the material keyword didn't help; task 06). Non-instanced
        /// runs at 500+ fps for 121 tiles on the reference PC; task 15 revisits instancing with profiling.
        /// </summary>
        public static bool DrawInstanced;

        /// <summary>
        /// A tile starts with a tiny splat map (all snow, layer 0) and grows to its cover resolution when
        /// its cover is painted: allocating 1024² six-layer maps up front added over a second to opening.
        /// </summary>
        public const int UnpaintedSplatResolution = 16;

        /// <summary>
        /// A tile's heights as Unity wants them: [row from the south, column from the west], normalised
        /// 0–1 of the cache's height range. Safe on any thread (no Unity API).
        /// </summary>
        public static float[,] LoadHeights(string packageFolder, CacheTile tile)
        {
            ushort[] v = TerrainCache.ReadTile(packageFolder, tile);
            int res = tile.Resolution;
            var heights = new float[Resolution, Resolution];
            const float scale = 1f / TerrainCache.MaxValue;
            if (res == Resolution)
            {
                for (int j = 0; j < res; j++)
                {
                    int row = Resolution - 1 - j; // cache row 0 is the north edge; Unity row 0 is the south
                    for (int i = 0; i < res; i++) heights[row, i] = v[j * res + i] * scale;
                }
                return heights;
            }
            if (res * 2 - 1 != Resolution) throw new InvalidOperationException($"Unexpected tile resolution {res}.");
            for (int j = 0; j < Resolution; j++)
            {
                int j0 = j / 2, j1 = Math.Min(res - 1, (j + 1) / 2);
                int row = Resolution - 1 - j;
                for (int i = 0; i < Resolution; i++)
                {
                    int i0 = i / 2, i1 = Math.Min(res - 1, (i + 1) / 2);
                    int a = v[j0 * res + i0], b = v[j0 * res + i1], c = v[j1 * res + i0], d = v[j1 * res + i1];
                    // Integer midpoints, rounded exactly as the cache rounds 1 m edges that meet 2 m tiles,
                    // so shared edge vertices are identical on both sides.
                    int value = (i & 1) == 0 && (j & 1) == 0 ? a
                              : (j & 1) == 0 ? (a + b + 1) >> 1
                              : (i & 1) == 0 ? (a + c + 1) >> 1
                              : (a + b + c + d + 2) >> 2;
                    heights[row, i] = value * scale;
                }
            }
            return heights;
        }

        /// <summary>
        /// Creates one tile's Terrain under <paramref name="parent"/>, positioned in the local frame whose
        /// origin is the site centre. Main thread only.
        /// </summary>
        public static Terrain Create(Transform parent, CacheManifest cache, CacheTile tile, float[,] heights, LocalFrame frame,
                                     Material material, TerrainLayer[] layers, TerrainDetail detail, int splatResolution = 0)
        {
            var data = new TerrainData
            {
                heightmapResolution = Resolution,
                name = $"t{tile.Column}_{tile.Row}",
            };
            data.size = new Vector3((float)cache.TileMetres, (float)cache.HeightRange, (float)cache.TileMetres);
            if (splatResolution > 0) data.alphamapResolution = splatResolution;
            if (layers != null && layers.Length > 0) data.terrainLayers = layers;

            // A Terrain without a TerrainCollider: cooking a million-sample physics heightfield per tile is most
            // of the cost of opening a resort, and nothing needs physics yet (the camera uses SampleHeight).
            // Future drawing tools add colliders where they need them (AddCollider).
            var go = new GameObject(data.name);
            go.transform.SetParent(parent, false);
            var (x, z) = frame.ToLocal(new AlbersPoint(tile.West, tile.North - cache.TileMetres));
            go.transform.localPosition = new Vector3((float)x, (float)cache.HeightMin, (float)z);
            var terrain = go.AddComponent<Terrain>();
            terrain.terrainData = data;
            // Instanced drawing lights the terrain from a normal map Unity builds when heights sync, so the
            // terrain must be instanced before its heights arrive (otherwise it renders flat-lit).
            terrain.drawInstanced = DrawInstanced;
            terrain.groupingID = 1;
            terrain.allowAutoConnect = false;
            if (material != null) terrain.materialTemplate = material;
            TerrainDetailSettings.Apply(terrain, detail);
            data.SetHeightsDelayLOD(0, 0, heights);
            data.SyncHeightmap();
            return terrain;
        }

        /// <summary>
        /// Uploads composed splat texels straight into the tile's alphamap textures (no float[,,] detour:
        /// 1024² × 6 layers as floats would be 25 MB per tile). Main thread only.
        /// </summary>
        public static void ApplySplat(TerrainData data, SplatTexels splat)
        {
            int n = splat.Resolution;
            if (data.alphamapResolution != n) data.alphamapResolution = n;
            // Unity's supported fast path (the one its paint tools use): stage the bytes in a texture, blit
            // to a render texture, then copy that into the alphamap on the GPU. Writing alphamapTextures
            // directly only reaches the basemap; the terrain shader keeps Unity's own copy.
            if (!Staging.TryGetValue(n, out var staging))
            {
                staging = new Texture2D(n, n, TextureFormat.RGBA32, false, true) { name = "SplatStaging" + n, filterMode = FilterMode.Point };
                Staging[n] = staging;
            }
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                for (int t = 0; t < data.alphamapTextureCount && t < splat.Textures.Length; t++)
                {
                    staging.SetPixelData(splat.Textures[t], 0);
                    staging.Apply(false);
                    Graphics.Blit(staging, rt);
                    RenderTexture.active = rt;
                    data.CopyActiveRenderTextureToTexture(TerrainData.AlphamapTextureName, t, new RectInt(0, 0, n, n), Vector2Int.zero, true);
                }
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
            data.SetBaseMapDirty();
        }

        static readonly Dictionary<int, Texture2D> Staging = new Dictionary<int, Texture2D>();

        /// <summary>Adds physics to a tile when something needs raycasts against it (drawing tools, later).</summary>
        public static TerrainCollider AddCollider(Terrain terrain)
        {
            var collider = terrain.GetComponent<TerrainCollider>() ?? terrain.gameObject.AddComponent<TerrainCollider>();
            collider.terrainData = terrain.terrainData;
            return collider;
        }

        /// <summary>Links every tile to its north, east, south and west neighbours (the cache's rows run south).</summary>
        public static void ConnectNeighbours(IReadOnlyDictionary<(int Column, int Row), Terrain> tiles)
        {
            Terrain At(int c, int r) => tiles.TryGetValue((c, r), out var t) ? t : null;
            foreach (var kv in tiles)
            {
                var (c, r) = kv.Key;
                kv.Value.SetNeighbors(At(c - 1, r), At(c, r - 1), At(c + 1, r), At(c, r + 1));
            }
        }
    }

    /// <summary>The loaded tiles as an <see cref="ITerrainSurface"/>.</summary>
    public sealed class UnityTerrainSurface : ITerrainSurface
    {
        readonly Dictionary<(int, int), Terrain> _tiles;
        readonly float _west, _north, _tile;

        public UnityTerrainSurface(Dictionary<(int, int), Terrain> tiles, float west, float north, float tileMetres, Rect footprint)
        {
            _tiles = tiles;
            _west = west;
            _north = north;
            _tile = tileMetres;
            Footprint = footprint;
        }

        public Rect Footprint { get; }

        public float HeightAt(float x, float z)
        {
            int c = Mathf.FloorToInt((x - _west) / _tile), r = Mathf.FloorToInt((_north - z) / _tile);
            if (!_tiles.TryGetValue((c, r), out var t)) return float.NaN;
            return t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y;
        }
    }
}
