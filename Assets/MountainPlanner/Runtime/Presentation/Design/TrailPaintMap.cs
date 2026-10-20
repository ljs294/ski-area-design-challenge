using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using Unity.Collections;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>What a trail tool paints on the ground while it draws (TF-02).</summary>
    public interface ITrailPaint
    {
        /// <summary>The design's 1 m lattice and where the local frame sits in it: the paint's cells are the footprint's.</summary>
        void Align(EditLattice lattice, double originX, double originY);
        /// <summary>
        /// Paints (or rubs out) the capsule <paramref name="radius"/> metres round the segment from <paramref name="a"/> to
        /// <paramref name="b"/> (local x/z): exactly the cells <see cref="TrailFootprint"/> paints for that stretch of a stroke.
        /// </summary>
        void Stamp(Vector2 a, Vector2 b, float radius, bool erase);
        /// <summary>Shows a worked-out footprint: the cells it paints, a patch apart red.</summary>
        void Show(TrailFootprint footprint);
        /// <summary>Takes all the paint off the ground.</summary>
        void Clear();
        /// <summary>The brush under the pointer (local x/z), drawn on the ground; hidden when <paramref name="shown"/> is false.</summary>
        void Cursor(Vector2 xz, float radius, bool erase, bool shown);
        /// <summary>The paint's tint for style C (the live difficulty estimate's colour).</summary>
        void Tint(Color colour);
    }

    /// <summary>
    /// The trail being painted, on the ground the way a brush paints (owner, 2026-10-19 and 2026-10-20: "laggy, not very
    /// elegant"). Its cells are the design's own 1 m lattice cells, painted with the footprint's own capsule rule, so what
    /// the brush lays down while dragging is exactly what the worked-out footprint shows after: nothing pops when a stroke
    /// ends. The map covers only what's painted (it grows), always at 1 m, whatever the site's size. Stamps land on the
    /// CPU copy and reach the GPU once a frame by dirty rectangle through a small staging texture. Display only.
    /// </summary>
    public sealed class TrailPaintMap : ITrailPaint, IDisposable
    {
        const int Stage = 256, Margin = 128, Step = 64, MaxCells = 4096;
        static readonly int MapId = Shader.PropertyToID("_PaintMap"), RectId = Shader.PropertyToID("_PaintRect"),
                            ParamsId = Shader.PropertyToID("_PaintParams"), CursorId = Shader.PropertyToID("_PaintCursor"),
                            TintId = Shader.PropertyToID("_PaintTint");

        EditLattice _lattice;
        double _originX, _originY;
        bool _aligned;
        // The window, in lattice cells: columns _c0.._c0+_w-1, rows _r0.._r0+_h-1 (rows run south).
        int _c0, _r0, _w, _h;
        byte[] _cpu = Array.Empty<byte>();   // rg pairs, texture rows from the south
        Texture2D _map, _stage;
        RectInt _dirty, _shown;   // in window cells (x column, y row from the north)
        bool _realloc;
        int _style;

        /// <summary>The look (0 A: light fill and a crisp edge; 1 B: white-blue highlight; 2 C: the difficulty's colour).</summary>
        public int Style
        {
            get => _style;
            set { _style = Mathf.Clamp(value, 0, 2); Shader.SetGlobalVector(ParamsId, new Vector4(_shown.width > 0 ? 1 : 0, _style, 0, 0)); }
        }

        public TrailPaintMap()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(CursorId, Vector4.zero);
            Shader.SetGlobalColor(TintId, new Color(0.85f, 0.42f, 0.16f));
        }

        public void Align(EditLattice lattice, double originX, double originY)
        {
            if (_aligned && lattice.West == _lattice.West && lattice.North == _lattice.North && originX == _originX && originY == _originY) return;
            _lattice = lattice;
            _originX = originX;
            _originY = originY;
            _aligned = true;
            _w = _h = 0;
            _cpu = Array.Empty<byte>();
            _shown = _dirty = default;
            _realloc = true;
        }

        public void Stamp(Vector2 a, Vector2 b, float radius, bool erase)
        {
            if (!_aligned) return;
            double ax = a.x + _originX, ay = a.y + _originY, bx = b.x + _originX, by = b.y + _originY, r = radius;
            int cMin = (int)Math.Floor(Math.Min(ax, bx) - r - _lattice.West) - 1, cMax = (int)Math.Ceiling(Math.Max(ax, bx) + r - _lattice.West) + 1;
            int rMin = (int)Math.Floor(_lattice.North - Math.Max(ay, by) - r) - 1, rMax = (int)Math.Ceiling(_lattice.North - Math.Min(ay, by) + r) + 1;
            if (!Cover(cMin, rMin, cMax, rMax)) return;
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy, r2 = r * r;
            for (int row = Math.Max(rMin, _r0); row <= Math.Min(rMax, _r0 + _h - 1); row++)
            {
                double y = _lattice.North - row - 0.5;
                for (int col = Math.Max(cMin, _c0); col <= Math.Min(cMax, _c0 + _w - 1); col++)
                {
                    // TrailFootprint.Capsule's own test: the cell's centre within r of the segment.
                    double x = _lattice.West + col + 0.5;
                    double t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len2));
                    double ex = ax + t * dx - x, ey = ay + t * dy - y;
                    if (ex * ex + ey * ey > r2) continue;
                    int at = Index(col, row);
                    _cpu[at] = erase ? (byte)0 : (byte)255;
                    _cpu[at + 1] = 0;
                }
            }
            Touch(Math.Max(cMin, _c0) - _c0, Math.Max(rMin, _r0) - _r0, Math.Min(cMax, _c0 + _w - 1) - _c0, Math.Min(rMax, _r0 + _h - 1) - _r0);
        }

        public void Show(TrailFootprint fp)
        {
            if (!_aligned || fp == null) { ClearCpu(); return; }
            if (!Cover(fp.Column0, fp.Row0, fp.Column0 + fp.Width - 1, fp.Row0 + fp.Height - 1)) return;
            ClearCpu();
            if (fp.IsEmpty) return;
            var labels = fp.PatchLabels(out int main);
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            for (int j = 0; j < fp.Height; j++)
                for (int i = 0; i < fp.Width; i++)
                {
                    int label = labels[j * fp.Width + i];
                    if (label == 0) continue;
                    int col = fp.Column0 + i, row = fp.Row0 + j;
                    int at = Index(col, row);
                    if (label == main) _cpu[at] = 255; else _cpu[at + 1] = 255;
                    x0 = Math.Min(x0, col - _c0); x1 = Math.Max(x1, col - _c0);
                    y0 = Math.Min(y0, row - _r0); y1 = Math.Max(y1, row - _r0);
                }
            if (x0 <= x1) Touch(x0, y0, x1, y1);
        }

        public void Clear()
        {
            ClearCpu();
            Shader.SetGlobalVector(CursorId, Vector4.zero);   // a tool's close or commit takes the brush off the ground too
        }

        public void Cursor(Vector2 xz, float radius, bool erase, bool shown) =>
            Shader.SetGlobalVector(CursorId, shown ? new Vector4(xz.x, xz.y, radius, erase ? 2 : 1) : Vector4.zero);

        public void Tint(Color colour) => Shader.SetGlobalColor(TintId, colour);

        int Index(int col, int row) => ((_h - 1 - (row - _r0)) * _w + (col - _c0)) * 2;

        /// <summary>Grows the window to hold the cells given (with a margin); false past the largest map.</summary>
        bool Cover(int cMin, int rMin, int cMax, int rMax)
        {
            if (_w > 0 && cMin >= _c0 && rMin >= _r0 && cMax < _c0 + _w && rMax < _r0 + _h) return true;
            int nc0 = _w > 0 ? Math.Min(_c0, cMin - Margin) : cMin - Margin, nr0 = _w > 0 ? Math.Min(_r0, rMin - Margin) : rMin - Margin;
            int nc1 = _w > 0 ? Math.Max(_c0 + _w, cMax + Margin) : cMax + Margin, nr1 = _w > 0 ? Math.Max(_r0 + _h, rMax + Margin) : rMax + Margin;
            nc0 = (int)Math.Floor(nc0 / (double)Step) * Step; nr0 = (int)Math.Floor(nr0 / (double)Step) * Step;
            int nw = (int)Math.Ceiling((nc1 - nc0) / (double)Step) * Step, nh = (int)Math.Ceiling((nr1 - nr0) / (double)Step) * Step;
            if (nw > MaxCells || nh > MaxCells) return false;
            var next = new byte[nw * nh * 2];
            for (int row = _r0; row < _r0 + _h; row++)
            {
                int from = ((_h - 1 - (row - _r0)) * _w) * 2, to = ((nh - 1 - (row - nr0)) * nw + (_c0 - nc0)) * 2;
                Array.Copy(_cpu, from, next, to, _w * 2);
            }
            if (_shown.width > 0) _shown = new RectInt(_shown.x + _c0 - nc0, _shown.y + _r0 - nr0, _shown.width, _shown.height);
            _c0 = nc0; _r0 = nr0; _w = nw; _h = nh; _cpu = next;
            _realloc = true;
            return true;
        }

        void ClearCpu()
        {
            if (_shown.width == 0) return;
            for (int y = _shown.yMin; y < _shown.yMax; y++)
            {
                int row = _r0 + y;
                Array.Clear(_cpu, Index(_c0 + _shown.xMin, row), _shown.width * 2);
            }
            _dirty = _dirty.width == 0 ? _shown : Union(_dirty, _shown);
            _shown = default;
        }

        void Touch(int x0, int y0, int x1, int y1)
        {
            if (x1 < x0 || y1 < y0) return;
            var r = new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
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
            if (!_aligned || _w == 0) return;
            if (_realloc || _map == null)
            {
                if (_map != null) Destroy(_map);
                _map = new Texture2D(_w, _h, TextureFormat.RG16, false, true)
                {
                    name = "Trail paint", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                };
                if (_stage == null) _stage = new Texture2D(Stage, Stage, TextureFormat.RG16, false, true) { name = "Trail paint stage" };
                var all = _map.GetPixelData<byte>(0);
                NativeArray<byte>.Copy(_cpu, all, _cpu.Length);
                _map.Apply(false);
                _realloc = false;
                _dirty = default;
                Bind();
                return;
            }
            if (_dirty.width == 0) return;
            var stage = _stage.GetPixelData<byte>(0);
            // Texture rows run north from the south edge: window row y (from the north) is texture row _h - 1 - y.
            int ty0 = _h - _dirty.yMax, ty1 = _h - _dirty.yMin;
            for (int sy = ty0; sy < ty1; sy += Stage)
                for (int sx = _dirty.xMin; sx < _dirty.xMax; sx += Stage)
                {
                    int cw = Mathf.Min(Stage, _dirty.xMax - sx), ch = Mathf.Min(Stage, ty1 - sy);
                    for (int y = 0; y < ch; y++)
                        NativeArray<byte>.Copy(_cpu, ((sy + y) * _w + sx) * 2, stage, y * Stage * 2, cw * 2);
                    _stage.Apply(false);
                    Graphics.CopyTexture(_stage, 0, 0, 0, 0, cw, ch, _map, 0, 0, sx, sy);
                }
            _dirty = default;
            Bind();
        }

        void Bind()
        {
            // The window's south-west corner in the local frame: lattice x of column _c0, y of row _r0 + _h's top edge.
            double west = _lattice.West + _c0 - _originX, south = _lattice.North - (_r0 + _h) - _originY;
            Shader.SetGlobalTexture(MapId, _map);
            Shader.SetGlobalVector(RectId, new Vector4((float)west, (float)south, 1f / _w, 1f / _h));
            Shader.SetGlobalVector(ParamsId, new Vector4(_shown.width > 0 ? 1 : 0, _style, 0, 0));
        }

        static void Destroy(UnityEngine.Object o)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(CursorId, Vector4.zero);
            if (_map != null) Destroy(_map);
            if (_stage != null) Destroy(_stage);
            _map = _stage = null;
        }
    }
}
