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
    public sealed class SurfaceStates : System.IDisposable
    {
        public const float CellMetres = Domain.Snow.SurfaceGrid.CellMetres;
        /// <summary>Snow this deep (metres) covers the ground completely; thinner snow lets the ground show.</summary>
        public const float FullCoverMetres = 0.15f;

        static readonly int MapId = Shader.PropertyToID("_SnowDepthMap"), RectId = Shader.PropertyToID("_SnowDepthRect"),
                            ParamsId = Shader.PropertyToID("_SnowDepthParams"), BodyMapId = Shader.PropertyToID("_WaterBodyMap"),
                            BodyStatesId = Shader.PropertyToID("_WaterBodyStates"), BodyParamsId = Shader.PropertyToID("_WaterBodyParams");

        public readonly SnowDepthField Snow;
        public readonly WaterBodies Water;
        readonly Texture2D _depth;
        readonly Rect _ring;
        Texture2D _bodyMap, _bodyStates;
        int _waterVersion = -1;
        /// <summary>The bound table's hash (0: none), so a caller rebinds only when the table changed.</summary>
        public ulong BoundTable { get; private set; }

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
            Shader.SetGlobalTexture(MapId, _depth);
            _bodyMap = new Texture2D(1, 1, TextureFormat.R8, false, true) { name = "Water bodies (none)", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            _bodyMap.SetPixelData(new byte[1], 0);
            _bodyMap.Apply(false);
            Shader.SetGlobalTexture(BodyMapId, _bodyMap);
            Shader.SetGlobalVector(BodyParamsId, new Vector4(0, 1, 1, 1));
            Shader.SetGlobalVector(RectId, new Vector4(ring.xMin, ring.yMin, 1 / (width * CellMetres), 1 / (depth * CellMetres)));
            Sync();
            Shader.SetGlobalVector(ParamsId, new Vector4(FullCoverMetres, 0, 0, 1));
        }

        /// <summary>Pushes changes to the GPU. Cheap when nothing changed; call it once a frame.</summary>
        public void Sync()
        {
            var (x, y, w, h) = Snow.TakeDirty();
            if (w > 0)
            {
                NativeArray<float> pixels = _depth.GetPixelData<float>(0);
                float[] cells = Snow.Cells;
                for (int j = y; j < y + h; j++)
                    NativeArray<float>.Copy(cells, j * Snow.Width + x, pixels, j * Snow.Width + x, w);
                _depth.Apply(false);
            }
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
            if (Water.Count != table.Count) Water.Resize(table.Count);
            Shader.SetGlobalVector(BodyParamsId, new Vector4(1, table.Count, table.Width, table.Depth));
            BoundTable = table.Hash;
            Sync();
            return true;
        }

        static void DestroyTexture(Texture2D t)
        {
            if (t == null) return;
            if (Application.isPlaying) Object.Destroy(t);
            else Object.DestroyImmediate(t);
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(BodyParamsId, Vector4.zero);
            Shader.SetGlobalTexture(BodyMapId, null);
            Shader.SetGlobalTexture(BodyStatesId, null);
            DestroyTexture(_bodyMap);
            DestroyTexture(_bodyStates);
            _bodyMap = _bodyStates = null;
            BoundTable = 0;
            DestroyTexture(_depth);
        }
    }
}
