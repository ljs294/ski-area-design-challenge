using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>What a trail tool paints on the ground while it draws (TF-02).</summary>
    public interface ITrailPaint
    {
        /// <summary>The design's 1 m lattice and where the local frame sits in it: the paint's cells are the footprint's.</summary>
        void Align(EditLattice lattice, double originX, double originY);
        /// <summary>
        /// Paints (or rubs out) the capsule <paramref name="radius"/> metres round the segment from <paramref name="a"/> to
        /// <paramref name="b"/> (local x/z), the stretch of a stroke <see cref="TrailFootprint"/> paints, its edge anti-aliased.
        /// </summary>
        void Stamp(Vector2 a, Vector2 b, float radius, bool erase);
        /// <summary>
        /// Shows the clearing as painted: <paramref name="baseShape"/> (the trail's own, when editing; null for a new one),
        /// then the strokes painted onto it (the same stamps, replayed in order, so nothing changes when a stroke ends), and
        /// the worked-out footprint's patches apart in red. Sections finding 7: a built trail keeps no strokes.
        /// </summary>
        void Redraw(TrailShape baseShape, IReadOnlyList<Stroke> strokes, TrailFootprint footprint);
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
    /// CPU copy and reach the GPU once a frame by dirty rectangle: the dirty rows into a raw buffer (SetData copies at the
    /// call, so the player's render thread never reads bytes a later stamp has changed) and a compute kernel writes them
    /// into the paint texture (findings 2026-10-20: a shared staging texture put blocks of paint in the wrong places).
    /// Display only.
    /// </summary>
    public sealed class TrailPaintMap : ITrailPaint, IDisposable
    {
        const int Margin = 128, Step = 64, MaxCells = 4096;
        /// <summary>The upload kernel (Resources).</summary>
        public const string UploadResource = "MountainPlannerDesign/TrailPaintUpload";
        static readonly int MapId = Shader.PropertyToID("_PaintMap"), RectId = Shader.PropertyToID("_PaintRect"),
                            ParamsId = Shader.PropertyToID("_PaintParams"), CursorId = Shader.PropertyToID("_PaintCursor"),
                            TintId = Shader.PropertyToID("_PaintTint"), CpuId = Shader.PropertyToID("_Cpu"),
                            TargetId = Shader.PropertyToID("_Map"), UploadRectId = Shader.PropertyToID("_Rect"),
                            WidthId = Shader.PropertyToID("_Width");

        EditLattice _lattice;
        double _originX, _originY;
        bool _aligned;
        // The window, in lattice cells: columns _c0.._c0+_w-1, rows _r0.._r0+_h-1 (rows run south).
        int _c0, _r0, _w, _h;
        byte[] _cpu = Array.Empty<byte>();   // rg pairs, texture rows from the south
        uint[] _words = Array.Empty<uint>();   // the dirty rows' bytes as the buffer's words
        RenderTexture _map;
        GraphicsBuffer _buffer;
        ComputeShader _upload;
        readonly int[] _rect = new int[4];
        RectInt _dirty, _shown;   // in window cells (x column, y row from the north)
        bool _realloc;
        int _style = 1;   // B, the owner's choice (TL25): a blue fill with a blue edge

        /// <summary>The look (0 A: orange; 1 B: blue, the game's (owner, TL25); 2 C: the live difficulty's colour). -paintstyle a/b/c for reviews.</summary>
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
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            for (int row = Math.Max(rMin, _r0); row <= Math.Min(rMax, _r0 + _h - 1); row++)
            {
                double y = _lattice.North - row - 0.5;
                for (int col = Math.Max(cMin, _c0); col <= Math.Min(cMax, _c0 + _w - 1); col++)
                {
                    // TrailFootprint.Capsule's own distance, from the cell's centre to the segment; the coverage falls from
                    // 1 to 0 across the metre round the brush's edge, so the edge follows the brush, not the cells' steps.
                    double x = _lattice.West + col + 0.5;
                    double t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len2));
                    double ex = ax + t * dx - x, ey = ay + t * dy - y, d2 = ex * ex + ey * ey;
                    if (d2 > (r + 0.5) * (r + 0.5)) continue;
                    double cover = Math.Max(0, Math.Min(1, r - Math.Sqrt(d2) + 0.5));
                    byte v = (byte)Math.Round(cover * 255);
                    int at = Index(col, row);
                    if (erase) { int keep = 255 - v; if (_cpu[at] > keep) _cpu[at] = (byte)keep; }
                    else if (_cpu[at] < v) _cpu[at] = v;
                    _cpu[at + 1] = 0;
                }
            }
            Touch(Math.Max(cMin, _c0) - _c0, Math.Max(rMin, _r0) - _r0, Math.Min(cMax, _c0 + _w - 1) - _c0, Math.Min(rMax, _r0 + _h - 1) - _r0);
        }

        public void Redraw(TrailShape baseShape, IReadOnlyList<Stroke> strokes, TrailFootprint fp)
        {
            if (!_aligned) return;
            ClearCpu();
            if (baseShape == null && (strokes == null || strokes.Count == 0)) return;
            if (fp != null && !Cover(fp.Column0 - 2, fp.Row0 - 2, fp.Column0 + fp.Width + 1, fp.Row0 + fp.Height + 1)) return;
            if (baseShape != null && !baseShape.IsEmpty) Fill(baseShape);
            if (strokes != null)
                foreach (var s in strokes)
                {
                    float r = s.WidthMm / 2000f;
                    var pts = s.Points;
                    if (pts.Count == 1) Stamp(Local(pts[0]), Local(pts[0]), r, s.Erase);
                    for (int k = 0; k + 1 < pts.Count; k++) Stamp(Local(pts[k]), Local(pts[k + 1]), r, s.Erase);
                }
            if (fp == null || fp.IsEmpty || fp.Patches < 2) return;
            // A patch apart (TF-02) turns red, its anti-aliased rim with it.
            var labels = fp.PatchLabels(out int main);
            for (int j = -1; j <= fp.Height; j++)
                for (int i = -1; i <= fp.Width; i++)
                {
                    int col = fp.Column0 + i, row = fp.Row0 + j;
                    int at = Index(col, row);
                    if (_cpu[at] == 0) continue;
                    int label = Label(labels, fp, i, j);
                    if (label == 0) for (int n = 0; n < 4 && label == 0; n++) label = Label(labels, fp, i + (n == 0 ? 1 : n == 1 ? -1 : 0), j + (n == 2 ? 1 : n == 3 ? -1 : 0));
                    if (label == 0 || label == main) continue;
                    _cpu[at + 1] = _cpu[at];
                    _cpu[at] = 0;
                }
        }

        // The clearing's cells into the paint: each 1 m cell as much as its sixteen 250 mm cells are cleared, so its edge
        // is soft where the clearing crosses it, as a brush's is.
        void Fill(TrailShape shape)
        {
            shape.Bounds(out int x0, out int y0, out int x1, out int y1);
            int cMin = (int)Math.Floor(_originX + x0 / 1000.0 - _lattice.West), cMax = (int)Math.Ceiling(_originX + x1 / 1000.0 - _lattice.West);
            int rMin = (int)Math.Floor(_lattice.North - (_originY + y1 / 1000.0)), rMax = (int)Math.Ceiling(_lattice.North - (_originY + y0 / 1000.0));
            if (!Cover(cMin - 2, rMin - 2, cMax + 2, rMax + 2)) return;
            var mask = shape.Mask;
            for (int row = rMin; row <= rMax; row++)
                for (int col = cMin; col <= cMax; col++)
                {
                    double x = _lattice.West + col - _originX, y = _lattice.North - row - 1 - _originY;
                    int n = 0;
                    for (int b = 0; b < 4; b++)
                        for (int a = 0; a < 4; a++)
                            if (mask.ContainsAt(x + 0.125 + 0.25 * a, y + 0.125 + 0.25 * b)) n++;
                    if (n == 0) continue;
                    int at = Index(col, row);
                    byte v = (byte)(n * 255 / 16);
                    if (_cpu[at] < v) _cpu[at] = v;
                    _cpu[at + 1] = 0;
                }
            Touch(Math.Max(cMin, _c0) - _c0, Math.Max(rMin, _r0) - _r0, Math.Min(cMax, _c0 + _w - 1) - _c0, Math.Min(rMax, _r0 + _h - 1) - _r0);
        }

        static int Label(int[] labels, TrailFootprint fp, int i, int j) =>
            i < 0 || j < 0 || i >= fp.Width || j >= fp.Height ? 0 : labels[j * fp.Width + i];

        static Vector2 Local(PointMm p) => new Vector2((float)p.XMetres, (float)p.YMetres);

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
            if (_upload == null)
            {
                _upload = Resources.Load<ComputeShader>(UploadResource);
                if (_upload == null || !SystemInfo.supportsComputeShaders) return;
            }
            if (_realloc || _map == null || !_map.IsCreated())
            {
                if (_map != null) Destroy(_map);
                _buffer?.Release();
                _map = new RenderTexture(_w, _h, 0, GraphicsFormat.R8G8_UNorm)
                {
                    name = "Trail paint", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, enableRandomWrite = true,
                };
                _map.Create();
                _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, _cpu.Length / 4, 4);
                _words = new uint[_cpu.Length / 4];
                _realloc = false;
                _dirty = new RectInt(0, 0, _w, _h);
            }
            if (_dirty.width == 0) return;
            // Texture rows run north from the south edge: window row y (from the north) is texture row _h - 1 - y. Whole
            // rows go up (a row is a multiple of 64 cells, so whole words).
            int ty0 = _h - _dirty.yMax, rows = _dirty.height;
            int word0 = ty0 * _w / 2, words = rows * _w / 2;
            Buffer.BlockCopy(_cpu, word0 * 4, _words, word0 * 4, words * 4);
            _buffer.SetData(_words, word0, word0, words);
            _upload.SetBuffer(0, CpuId, _buffer);
            _upload.SetTexture(0, TargetId, _map);
            _rect[0] = _dirty.xMin; _rect[1] = ty0; _rect[2] = _dirty.width; _rect[3] = rows;
            _upload.SetInts(UploadRectId, _rect);   // a kept array: no allocation per frame
            _upload.SetInt(WidthId, _w);
            _upload.Dispatch(0, (_dirty.width + 7) / 8, (rows + 7) / 8, 1);
            _dirty = default;
            Bind();
        }

        /// <summary>For the paint checks (EditMode and <c>-paintcheck</c>): the texture the terrain samples, and the CPU paint it should hold.</summary>
        public Texture GpuTexture => _map;
        public ReadOnlySpan<byte> CpuBytes => _cpu;

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
            _buffer?.Release();
            _map = null;
            _buffer = null;
        }
    }
}
