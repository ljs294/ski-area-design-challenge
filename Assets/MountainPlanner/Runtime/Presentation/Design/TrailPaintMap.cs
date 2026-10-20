using System;
using MountainPlanner.Domain.Design;
using Unity.Collections;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>What a trail tool paints on the ground while it draws (TF-02).</summary>
    public interface ITrailPaint
    {
        /// <summary>Stamps the brush, <paramref name="radius"/> metres round a local x/z point, or rubs it out.</summary>
        void Stamp(Vector2 xz, float radius, bool erase);
        /// <summary>Shows a worked-out footprint: its largest patch orange, any other patch red.</summary>
        void Show(TrailFootprint footprint);
        /// <summary>Takes all the paint off the ground.</summary>
        void Clear();
    }

    /// <summary>
    /// The trail being painted, shown on the ground the way a brush paints (owner, 2026-10-19; the mockup's footprint
    /// canvas, TF-02): a world-space map over the core that the terrain shader tints, r where the brush has been and g
    /// where a patch stands apart. Stamps land on the CPU copy at once and go to the GPU once a frame by dirty
    /// rectangle, through a small staging texture (no whole-map upload, nothing allocated per frame). Display only.
    /// </summary>
    public sealed class TrailPaintMap : ITrailPaint, IDisposable
    {
        const int MaxTexels = 4096, Stage = 256;
        static readonly int MapId = Shader.PropertyToID("_PaintMap"), RectId = Shader.PropertyToID("_PaintRect"),
                            ParamsId = Shader.PropertyToID("_PaintParams");

        readonly float _west, _south, _texel;
        readonly int _w, _h;
        readonly byte[] _cpu;           // rg pairs, rows north from the south edge
        Texture2D _map, _stage;
        RectInt _dirty;                 // texels waiting for the GPU (empty: width 0)
        RectInt _shown;                 // texels that may hold paint
        bool _any;

        /// <param name="core">The core area in local metres (x east, y north): paint stays inside it.</param>
        public TrailPaintMap(Rect core)
        {
            _texel = Mathf.Max(1f, Mathf.Ceil(Mathf.Max(core.width, core.height) / MaxTexels));
            _west = core.xMin;
            _south = core.yMin;
            _w = Mathf.Max(1, Mathf.CeilToInt(core.width / _texel));
            _h = Mathf.Max(1, Mathf.CeilToInt(core.height / _texel));
            _cpu = new byte[_w * _h * 2];
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
        }

        public void Stamp(Vector2 xz, float radius, bool erase)
        {
            float r = radius / _texel, cx = (xz.x - _west) / _texel, cy = (xz.y - _south) / _texel;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - r)), x1 = Mathf.Min(_w - 1, Mathf.CeilToInt(cx + r));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - r)), y1 = Mathf.Min(_h - 1, Mathf.CeilToInt(cy + r));
            if (x0 > x1 || y0 > y1) return;
            float r2 = r * r;
            for (int y = y0; y <= y1; y++)
            {
                float dy = y + 0.5f - cy;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx;
                    if (dx * dx + dy * dy > r2) continue;
                    int at = (y * _w + x) * 2;
                    _cpu[at] = erase ? (byte)0 : (byte)255;
                    _cpu[at + 1] = 0;
                }
            }
            Touch(new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
        }

        public void Show(TrailFootprint fp)
        {
            ClearCpu();
            if (fp == null || fp.IsEmpty) return;
            var labels = fp.PatchLabels(out int main);
            // The footprint's cells (1 m, lattice-aligned) into the map's texels: local = lattice frame − origin.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int j = 0; j < fp.Height; j++)
                for (int i = 0; i < fp.Width; i++)
                {
                    int label = labels[j * fp.Width + i];
                    if (label == 0) continue;
                    float x = (float)(fp.CellX(fp.Column0 + i) - fp.OriginX), y = (float)(fp.CellY(fp.Row0 + j) - fp.OriginY);
                    int tx = Mathf.FloorToInt((x - _west) / _texel), ty = Mathf.FloorToInt((y - _south) / _texel);
                    if (tx < 0 || ty < 0 || tx >= _w || ty >= _h) continue;
                    int at = (ty * _w + tx) * 2;
                    if (label == main) _cpu[at] = 255;
                    else _cpu[at + 1] = 255;
                    minX = Mathf.Min(minX, tx); maxX = Mathf.Max(maxX, tx);
                    minY = Mathf.Min(minY, ty); maxY = Mathf.Max(maxY, ty);
                }
            if (minX <= maxX) Touch(new RectInt((int)minX, (int)minY, (int)(maxX - minX) + 1, (int)(maxY - minY) + 1));
        }

        public void Clear()
        {
            ClearCpu();
            if (!_any) Shader.SetGlobalVector(ParamsId, Vector4.zero);
        }

        void ClearCpu()
        {
            if (_shown.width == 0) return;
            for (int y = _shown.yMin; y < _shown.yMax; y++)
                Array.Clear(_cpu, (y * _w + _shown.xMin) * 2, _shown.width * 2);
            Touch(_shown);
            _shown = default;
        }

        void Touch(RectInt r)
        {
            _dirty = _dirty.width == 0 ? r : Union(_dirty, r);
            _shown = _shown.width == 0 ? r : Union(_shown, r);
        }

        static RectInt Union(RectInt a, RectInt b)
        {
            int x0 = Mathf.Min(a.xMin, b.xMin), y0 = Mathf.Min(a.yMin, b.yMin);
            return new RectInt(x0, y0, Mathf.Max(a.xMax, b.xMax) - x0, Mathf.Max(a.yMax, b.yMax) - y0);
        }

        /// <summary>Sends this frame's changes to the GPU (once a frame, from the viewer).</summary>
        public void Flush()
        {
            if (_dirty.width == 0) return;
            if (_map == null)
            {
                _map = new Texture2D(_w, _h, TextureFormat.RG16, false, true)
                {
                    name = "Trail paint", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                };
                _stage = new Texture2D(Stage, Stage, TextureFormat.RG16, false, true) { name = "Trail paint stage" };
                var all = _map.GetPixelData<byte>(0);
                NativeArray<byte>.Copy(_cpu, all, _cpu.Length);
                _map.Apply(false);
                _dirty = default;
            }
            else
            {
                var stage = _stage.GetPixelData<byte>(0);
                for (int sy = _dirty.yMin; sy < _dirty.yMax; sy += Stage)
                    for (int sx = _dirty.xMin; sx < _dirty.xMax; sx += Stage)
                    {
                        int cw = Mathf.Min(Stage, _dirty.xMax - sx), ch = Mathf.Min(Stage, _dirty.yMax - sy);
                        for (int y = 0; y < ch; y++)
                            NativeArray<byte>.Copy(_cpu, ((sy + y) * _w + sx) * 2, stage, y * Stage * 2, cw * 2);
                        _stage.Apply(false);
                        Graphics.CopyTexture(_stage, 0, 0, 0, 0, cw, ch, _map, 0, 0, sx, sy);
                    }
                _dirty = default;
            }
            _any = _shown.width > 0;
            Shader.SetGlobalTexture(MapId, _map);
            Shader.SetGlobalVector(RectId, new Vector4(_west, _south, 1f / (_w * _texel), 1f / (_h * _texel)));
            Shader.SetGlobalVector(ParamsId, new Vector4(_any ? 1 : 0, 0, 0, 0));
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            if (_map != null) UnityEngine.Object.Destroy(_map);
            if (_stage != null) UnityEngine.Object.Destroy(_stage);
            _map = _stage = null;
        }
    }
}
