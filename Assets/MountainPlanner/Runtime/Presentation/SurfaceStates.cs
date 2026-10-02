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
        public const float CellMetres = 8;
        /// <summary>Snow this deep (metres) covers the ground completely; thinner snow lets the ground show.</summary>
        public const float FullCoverMetres = 0.15f;

        static readonly int MapId = Shader.PropertyToID("_SnowDepthMap"), RectId = Shader.PropertyToID("_SnowDepthRect"),
                            ParamsId = Shader.PropertyToID("_SnowDepthParams"), LakeId = Shader.PropertyToID("_LakeState");

        public readonly SnowDepthField Snow;
        public readonly WaterBodies Water;
        readonly Texture2D _depth;
        int _waterVersion = -1;

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
            Shader.SetGlobalTexture(MapId, _depth);
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
                var s = Water[WaterBodies.AllWater];
                Shader.SetGlobalVector(LakeId, new Vector4((int)s.State + 1, s.IceMetres, s.SnowMetres, 0));
            }
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(LakeId, Vector4.zero);
            if (_depth == null) return;
            if (Application.isPlaying) Object.Destroy(_depth);
            else Object.DestroyImmediate(_depth);
        }
    }
}
