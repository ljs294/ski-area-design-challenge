using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MountainPlanner.Domain.Geo;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Picker
{
    /// <summary>
    /// The picker's mini-map (0.3 §6.1, 0.4 §5 "Map view"): a small slippy map built in UI Toolkit, not a
    /// map SDK. USGS basemap tiles (topo or imagery), the data-quality overlay (S1M tiles, 1 m and about
    /// 3 m DEM footprints), and the site square drawn from its exact EPSG:6350 edges with its 3 km ring.
    /// Drag pans; the wheel, + / − and Page Up / Page Down zoom; Home goes back to the square (or the
    /// whole country); a click without dragging, or Enter, places the square; the arrow keys nudge it
    /// (Shift: 1 km), or pan the map before there is one. There are no camera buttons (UI-10).
    /// The plan is drawn as the accepted mockup draws plans: surveyor's orange dashes (7 on, 4 off) and a
    /// dimension line with its figure on a graphite label.
    /// </summary>
    public sealed class TileMapView : VisualElement
    {
        public const int MinZoom = 4, MaxZoom = 15;
        const int TextureCapacity = 160;
        const float DragThreshold = 4;
        const long SettleMs = 450;
        /// <summary>The S1M tile overlay needs few enough 100 km listing folders: zoom 8 and closer.</summary>
        const int S1mMinZoom = 8;

        // Data-quality colours (the legend in SitePicker.uss uses the same values).
        // One blue ramp, darker for finer data: it reads on topo greens and tans and on imagery, and stays
        // clear of the plan's surveyor's orange. About 10 m (everywhere else) is left unshaded.
        public static readonly Color S1mColour = new Color32(0x1d, 0x4f, 0x91, 255);
        public static readonly Color OneMetreColour = new Color32(0x3f, 0x86, 0xd0, 255);
        public static readonly Color ThreeMetreColour = new Color32(0x9f, 0xc4, 0xee, 255);
        static readonly Color PlanOrange = new Color32(0xff, 0x6a, 0x1f, 255);
        // The dimension line: warm white over imagery, graphite over the light topo map.
        static readonly Color DimensionOnImagery = new Color32(0xef, 0xe9, 0xdf, 230);
        static readonly Color DimensionOnTopo = new Color32(0x1c, 0x19, 0x16, 210);
        const float DimensionOffset = 18;   // px outside the square's top edge
        const float OverlayOpacity = 0.24f;

        public ISitePickerServices Services;

        /// <summary>A click without a drag, at this latitude and longitude.</summary>
        public event Action<GeoPoint> Clicked;
        /// <summary>Arrow keys while focused: metres east and north.</summary>
        public event Action<double, double> NudgeRequested;
        /// <summary>The network failed (true) or answered again (false).</summary>
        public event Action<bool> OfflineChanged;

        readonly VisualElement _tileLayer, _vector;
        readonly Image _coverageImage;
        readonly Label _dimension;
        readonly Dictionary<string, Image> _shown = new Dictionary<string, Image>();
        readonly Stack<Image> _pool = new Stack<Image>();
        readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
        readonly LinkedList<string> _textureOrder = new LinkedList<string>();
        readonly HashSet<string> _pending = new HashSet<string>();
        readonly IVisualElementScheduledItem _settle;
        CancellationTokenSource _viewCts = new CancellationTokenSource();    // the overlay's requests: replaced when the view settles
        CancellationTokenSource _tilesCts = new CancellationTokenSource();   // tile loads: only when the map goes away

        double _cx, _cy;     // view centre in world pixels at _zoom
        int _zoom = 5;
        bool _imagery, _showCoverage = true, _offline;
        SiteSquare? _square;
        GeoPoint[] _coreOutline, _ringOutline;          // cached when the square changes
        readonly List<GeoPoint[]> _s1mCorners = new List<GeoPoint[]>();   // cached when the tiles arrive
        (double W, double S, double E, double N) _coverageBox;   // Web Mercator metres the coverage images span
        int _generation;

        bool _pressed, _dragging;
        Vector2 _pressAt, _lastAt;
        int _pointer = -1;

        public TileMapView()
        {
            AddToClassList("tile-map");
            AddToClassList(UiFocus.OwnArrowsClass);   // the arrows nudge the square; Tab leaves the map
            focusable = true;
            style.overflow = Overflow.Hidden;

            _tileLayer = Layer("tile-map__tiles");
            _coverageImage = new Image { scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            _coverageImage.style.position = Position.Absolute;
            _coverageImage.style.opacity = OverlayOpacity;
            Add(_coverageImage);
            _vector = Layer("tile-map__vector");
            _vector.generateVisualContent += DrawVector;
            _dimension = new Label { pickingMode = PickingMode.Ignore };
            _dimension.AddToClassList("tile-map__dim");
            _dimension.AddToClassList("mono");
            _dimension.style.position = Position.Absolute;
            Add(_dimension);

            RegisterCallback<GeometryChangedEvent>(_ => Relayout());
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(_ => { _pressed = _dragging = false; });
            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<DetachFromPanelEvent>(_ => Release());
            _settle = schedule.Execute(OnSettled);
            _settle.Pause();
            SetCentre(new GeoPoint(40.5, -105.5), 5);   // the contiguous US, Rockies in the middle
        }

        VisualElement Layer(string className)
        {
            var layer = new VisualElement { pickingMode = PickingMode.Ignore };
            layer.AddToClassList(className);
            layer.style.position = Position.Absolute;
            layer.style.left = 0; layer.style.top = 0; layer.style.right = 0; layer.style.bottom = 0;
            Add(layer);
            return layer;
        }

        public int Zoom => _zoom;
        public GeoPoint Centre => SlippyMap.FromPixels(_cx, _cy, _zoom);
        public bool IsOffline => _offline;

        public bool Imagery
        {
            get => _imagery;
            set
            {
                if (_imagery == value) return;
                _imagery = value;
                foreach (var image in _shown.Values) Recycle(image);
                _shown.Clear();
                Relayout();   // also repaints the dimension line in the basemap's colour
            }
        }

        public bool ShowCoverage
        {
            get => _showCoverage;
            set
            {
                _showCoverage = value;
                _coverageImage.visible = value;
                if (value) Settle(); else _vector.MarkDirtyRepaint();
            }
        }

        public SiteSquare? Square
        {
            get => _square;
            set
            {
                _square = value;
                _coreOutline = value.HasValue ? SlippyMap.Outline(value.Value.Core, 8) : null;
                _ringOutline = value.HasValue ? SlippyMap.Outline(value.Value.Ring, 4) : null;
                RefreshDimensionText();
                PositionDimension();
                _vector.MarkDirtyRepaint();
            }
        }

        public void SetCentre(GeoPoint centre, int zoom)
        {
            _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            (_cx, _cy) = SlippyMap.ToPixels(centre, _zoom);
            Relayout();
            Settle();
        }

        /// <summary>Shows a latitude/longitude box as large as fits, no closer than zoom 13.</summary>
        public void FitBounds(double south, double north, double west, double east, int maxZoom = 13)
        {
            float w = Mathf.Max(64, layout.width), h = Mathf.Max(64, layout.height);
            int zoom = maxZoom;
            for (; zoom > MinZoom; zoom--)
            {
                var (x0, y0) = SlippyMap.ToPixels(new GeoPoint(north, west), zoom);
                var (x1, y1) = SlippyMap.ToPixels(new GeoPoint(south, east), zoom);
                if (x1 - x0 <= w * 0.8 && y1 - y0 <= h * 0.8) break;
            }
            SetCentre(new GeoPoint((south + north) / 2, (west + east) / 2), zoom);
        }

        public void ZoomBy(int steps) => ZoomAround(steps, new Vector2(layout.width / 2, layout.height / 2));

        /// <summary>The dimension figure in the game's units (call when the units change).</summary>
        public void RefreshDimensionText() =>
            _dimension.text = _square.HasValue ? PickerUnits.Size(_square.Value.SizeKm) : "";

        /// <summary>Home: back to the square at a close zoom, or to the whole country before there is one.</summary>
        public void ResetView()
        {
            if (_square.HasValue) SetCentre(Albers6350.Inverse(_square.Value.Centre), 12);
            else SetCentre(new GeoPoint(40.5, -105.5), 5);
        }

        /// <summary>Forgets the offline state and loads again (the offline panel's Retry).</summary>
        public void Retry()
        {
            _pending.Clear();
            SetOffline(false);
            // Ask for the centre tile straight away, laid out or not, so a missing network shows at once.
            int n = 1 << _zoom;
            int tx = (((int)Math.Floor(_cx / SlippyMap.TileSize)) % n + n) % n;
            int ty = Mathf.Clamp((int)Math.Floor(_cy / SlippyMap.TileSize), 0, n - 1);
            Load(_zoom, tx, ty, Key(_zoom, tx, ty));
            Relayout();
            Settle();
        }

        // ---- coordinates ----

        Vector2 ToLocal(GeoPoint p)
        {
            var (x, y) = SlippyMap.ToPixels(p, _zoom);
            return new Vector2((float)(x - _cx + layout.width / 2), (float)(y - _cy + layout.height / 2));
        }

        /// <summary>The latitude and longitude under a point in the map's own coordinates.</summary>
        public GeoPoint GeoAt(Vector2 local) => FromLocal(local);

        GeoPoint FromLocal(Vector2 local) =>
            SlippyMap.FromPixels(_cx + local.x - layout.width / 2, _cy + local.y - layout.height / 2, _zoom);

        static double MercatorX(double worldPixel, double size) => (worldPixel / size * 2 - 1) * WebMercator.HalfWorld;
        static double MercatorY(double worldPixel, double size) => (1 - worldPixel / size * 2) * WebMercator.HalfWorld;

        // ---- input ----

        void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            Focus();
            _pressed = true;
            _dragging = false;
            _pressAt = _lastAt = evt.localPosition;
            _pointer = evt.pointerId;
            this.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_pressed || evt.pointerId != _pointer) return;
            Vector2 at = evt.localPosition;
            if (!_dragging && (at - _pressAt).magnitude >= DragThreshold) _dragging = true;
            if (_dragging)
            {
                PanBy(_lastAt - at);
                _lastAt = at;
            }
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (!_pressed || evt.pointerId != _pointer) return;
            _pressed = false;
            this.ReleasePointer(evt.pointerId);
            if (!_dragging) Clicked?.Invoke(FromLocal(evt.localPosition));
            _dragging = false;
            evt.StopPropagation();
        }

        void OnWheel(WheelEvent evt)
        {
            ZoomAround(evt.delta.y < 0 ? 1 : -1, evt.localMousePosition);
            evt.StopPropagation();
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            double step = evt.shiftKey ? 1000 : 100;
            float pan = evt.shiftKey ? 256 : 64;
            switch (evt.keyCode)
            {
                case KeyCode.LeftArrow: Arrow(-step, 0, new Vector2(-pan, 0)); break;
                case KeyCode.RightArrow: Arrow(step, 0, new Vector2(pan, 0)); break;
                case KeyCode.UpArrow: Arrow(0, step, new Vector2(0, -pan)); break;
                case KeyCode.DownArrow: Arrow(0, -step, new Vector2(0, pan)); break;
                case KeyCode.Equals: case KeyCode.Plus: case KeyCode.KeypadPlus: case KeyCode.PageUp: ZoomBy(1); break;
                case KeyCode.Minus: case KeyCode.KeypadMinus: case KeyCode.PageDown: ZoomBy(-1); break;
                case KeyCode.Home: ResetView(); break;
                case KeyCode.Return: case KeyCode.KeypadEnter: case KeyCode.Space: Clicked?.Invoke(Centre); break;
                default: return;
            }
            evt.StopPropagation();
        }

        /// <summary>Arrows nudge the square once there is one; before that they pan the map.</summary>
        void Arrow(double east, double north, Vector2 panPixels)
        {
            if (_square.HasValue) NudgeRequested?.Invoke(east, north);
            else PanBy(panPixels);
        }

        void PanBy(Vector2 delta)
        {
            double size = SlippyMap.WorldPixels(_zoom);
            _cx += delta.x;
            _cy = Math.Max(0, Math.Min(size, _cy + delta.y));
            Relayout();
            Settle();
        }

        void ZoomAround(int steps, Vector2 local)
        {
            int zoom = Mathf.Clamp(_zoom + steps, MinZoom, MaxZoom);
            if (zoom == _zoom) return;
            var anchor = FromLocal(local);
            _zoom = zoom;
            var (ax, ay) = SlippyMap.ToPixels(anchor, _zoom);
            _cx = ax - (local.x - layout.width / 2);
            _cy = ay - (local.y - layout.height / 2);
            Relayout();
            Settle();
        }

        // ---- tiles ----

        void Relayout()
        {
            float w = layout.width, h = layout.height;
            if (float.IsNaN(w) || w <= 0 || h <= 0) return;
            int n = 1 << _zoom;
            double left = _cx - w / 2, top = _cy - h / 2;
            int x0 = (int)Math.Floor(left / SlippyMap.TileSize), x1 = (int)Math.Floor((left + w) / SlippyMap.TileSize);
            int y0 = Math.Max(0, (int)Math.Floor(top / SlippyMap.TileSize)), y1 = Math.Min(n - 1, (int)Math.Floor((top + h) / SlippyMap.TileSize));

            var wanted = new HashSet<string>();
            for (int ty = y0; ty <= y1; ty++)
                for (int tx = x0; tx <= x1; tx++)
                {
                    int wx = ((tx % n) + n) % n;
                    string key = Key(_zoom, wx, ty);
                    wanted.Add(key);
                    if (!_shown.TryGetValue(key, out var image))
                    {
                        image = _pool.Count > 0 ? _pool.Pop() : NewTileImage();
                        image.image = _textures.TryGetValue(key, out var tex) ? tex : null;
                        _tileLayer.Add(image);
                        _shown[key] = image;
                    }
                    // Also retries tiles that were wanted before the services arrived or while offline.
                    if (image.image == null) Load(_zoom, wx, ty, key);
                    image.style.left = (float)(tx * SlippyMap.TileSize - left);
                    image.style.top = (float)(ty * SlippyMap.TileSize - top);
                }
            var stale = new List<string>();
            foreach (var kv in _shown) if (!wanted.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (string key in stale)
            {
                Recycle(_shown[key]);
                _shown.Remove(key);
            }
            PlaceCoverage();
            PositionDimension();
            _vector.MarkDirtyRepaint();
        }

        string Key(int z, int x, int y) => (_imagery ? "i/" : "t/") + z + "/" + x + "/" + y;

        static Image NewTileImage()
        {
            var image = new Image { scaleMode = ScaleMode.StretchToFill, pickingMode = PickingMode.Ignore };
            image.AddToClassList("tile-map__tile");
            image.style.position = Position.Absolute;
            image.style.width = SlippyMap.TileSize;
            image.style.height = SlippyMap.TileSize;
            return image;
        }

        void Recycle(Image image)
        {
            image.RemoveFromHierarchy();
            image.image = null;
            _pool.Push(image);
        }

        async void Load(int z, int x, int y, string key)
        {
            if (Services == null || _offline || !_pending.Add(key)) return;
            bool imagery = _imagery;
            try
            {
                byte[] bytes = await Services.TileAsync(imagery, z, x, y, _tilesCts.Token);
                SetOffline(false);
                if (bytes == null || panel == null) return;
                var tex = Decode(bytes);
                Remember(key, tex);
                if (_shown.TryGetValue(key, out var image)) image.image = tex;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { SetOffline(true); }
            catch (Exception ex)
            {
                // A slow or failed answer that isn't "offline": try the tiles still missing again shortly.
                Debug.LogWarning($"[Picker] Tile {key}: {ex.Message}");
                schedule.Execute(Relayout).StartingIn(2000);
            }
            finally { _pending.Remove(key); }
        }

        static Texture2D Decode(byte[] bytes, bool keepReadable = false)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.LoadImage(bytes, !keepReadable);
            return tex;
        }

        void Remember(string key, Texture2D tex)
        {
            if (_textures.TryGetValue(key, out var old)) { UnityEngine.Object.Destroy(old); _textureOrder.Remove(key); }
            _textures[key] = tex;
            _textureOrder.AddFirst(key);
            while (_textureOrder.Count > TextureCapacity)
            {
                string last = _textureOrder.Last.Value;
                _textureOrder.RemoveLast();
                if (_shown.ContainsKey(last)) { _textureOrder.AddFirst(last); break; }
                UnityEngine.Object.Destroy(_textures[last]);
                _textures.Remove(last);
            }
        }

        void SetOffline(bool offline)
        {
            if (_offline == offline) return;
            _offline = offline;
            OfflineChanged?.Invoke(offline);
        }

        // ---- overlays ----

        void Settle()
        {
            _settle.ExecuteLater(SettleMs);
        }

        /// <summary>The view has stopped moving: refresh the coverage images and S1M tiles for it.</summary>
        async void OnSettled()
        {
            _settle.Pause();
            if (Services == null || _offline || !_showCoverage) return;
            float w = layout.width, h = layout.height;
            if (float.IsNaN(w) || w <= 0 || h <= 0) return;
            int generation = ++_generation;
            _viewCts.Cancel();
            _viewCts = new CancellationTokenSource();
            var ct = _viewCts.Token;

            double size = SlippyMap.WorldPixels(_zoom);
            var box = (W: MercatorX(_cx - w / 2, size), S: MercatorY(_cy + h / 2, size), E: MercatorX(_cx + w / 2, size), N: MercatorY(_cy - h / 2, size));
            int pw = Mathf.Clamp(Mathf.RoundToInt(w), 64, 1024), ph = Mathf.Clamp(Mathf.RoundToInt(h), 64, 1024);
            try
            {
                var one = Services.CoverageImageAsync(CoverageLayer.OneMetre, box.W, box.S, box.E, box.N, pw, ph, ct);
                var three = Services.CoverageImageAsync(CoverageLayer.ThreeMetre, box.W, box.S, box.E, box.N, pw, ph, ct);
                var oneBytes = await one;
                var threeBytes = await three;
                if (generation != _generation || panel == null) return;
                Compose(oneBytes, threeBytes);
                _coverageBox = box;
                PlaceCoverage();

                _s1mCorners.Clear();
                if (_zoom >= S1mMinZoom)
                {
                    var albers = AlbersBounds(w, h);
                    var tiles = await Services.S1mTilesAsync(albers, ct);
                    if (generation != _generation || panel == null) return;
                    foreach (var tile in tiles) _s1mCorners.Add(SlippyMap.Corners(tile));
                }
                _vector.MarkDirtyRepaint();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { SetOffline(true); }
            catch (Exception ex) { Debug.LogWarning($"[Picker] Coverage: {ex.Message}"); }
        }

        /// <summary>
        /// Paints the two footprint PNGs into one overlay: 1 m where a 1 m DEM exists, else about 3 m where
        /// a 1/9 arc-second DEM does, else clear. One image, so the two never mix into a third colour.
        /// </summary>
        void Compose(byte[] onePng, byte[] threePng)
        {
            if (_coverageImage.image is Texture2D old) UnityEngine.Object.Destroy(old);
            _coverageImage.image = null;
            var one = onePng != null ? Decode(onePng, keepReadable: true) : null;
            var three = threePng != null ? Decode(threePng, keepReadable: true) : null;
            var basis = one ?? three;
            if (basis == null) return;
            var a = one?.GetPixels32();
            var b = three != null && three.width == basis.width && three.height == basis.height ? three.GetPixels32() : null;
            var pixels = new Color32[basis.width * basis.height];
            Color32 oneColour = OneMetreColour, threeColour = ThreeMetreColour, clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = a != null && a[i].a > 8 ? oneColour : b != null && b[i].a > 8 ? threeColour : clear;
            if (one != null) UnityEngine.Object.Destroy(one);
            if (three != null) UnityEngine.Object.Destroy(three);
            var tex = new Texture2D(basis.width, basis.height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            _coverageImage.image = tex;
        }

        void PlaceCoverage()
        {
            if (_coverageBox.E <= _coverageBox.W) return;
            double size = SlippyMap.WorldPixels(_zoom);
            float left = (float)((_coverageBox.W / WebMercator.HalfWorld + 1) / 2 * size - (_cx - layout.width / 2));
            float right = (float)((_coverageBox.E / WebMercator.HalfWorld + 1) / 2 * size - (_cx - layout.width / 2));
            float top = (float)((1 - _coverageBox.N / WebMercator.HalfWorld) / 2 * size - (_cy - layout.height / 2));
            float bottom = (float)((1 - _coverageBox.S / WebMercator.HalfWorld) / 2 * size - (_cy - layout.height / 2));
            _coverageImage.style.left = left;
            _coverageImage.style.top = top;
            _coverageImage.style.width = right - left;
            _coverageImage.style.height = bottom - top;
        }

        /// <summary>The Albers box around the view (its corners and edge midpoints projected).</summary>
        AlbersBox AlbersBounds(float w, float h)
        {
            double west = double.MaxValue, south = double.MaxValue, east = double.MinValue, north = double.MinValue;
            for (int i = 0; i <= 2; i++)
                for (int j = 0; j <= 2; j++)
                {
                    var a = Albers6350.Forward(FromLocal(new Vector2(w * i / 2, h * j / 2)));
                    west = Math.Min(west, a.X); east = Math.Max(east, a.X);
                    south = Math.Min(south, a.Y); north = Math.Max(north, a.Y);
                }
            return new AlbersBox(west, south, east, north);
        }

        /// <summary>
        /// The dimension line runs parallel to the square's top edge, <see cref="DimensionOffset"/> outside it
        /// (as the mockup dimensions a plan), so it never hides the ground inside the square.
        /// </summary>
        bool DimensionLine(out Vector2 a, out Vector2 b, out Vector2 normal)
        {
            a = b = normal = default;
            if (_coreOutline == null || float.IsNaN(layout.width)) return false;
            a = ToLocal(_coreOutline[0]);              // north-west corner
            b = ToLocal(_coreOutline[8]);              // north-east corner (8 points per edge)
            var along = b - a;
            if (along.sqrMagnitude < 1) return false;
            var centre = ToLocal(Albers6350.Inverse(_square.Value.Centre));
            normal = new Vector2(along.y, -along.x).normalized;
            if (Vector2.Dot(normal, (a + b) / 2 - centre) < 0) normal = -normal;   // point away from the square
            return true;
        }

        void PositionDimension()
        {
            if (!DimensionLine(out var a, out var b, out var n) || (b - a).magnitude < 60) { _dimension.visible = false; return; }
            var mid = (a + b) / 2 + n * DimensionOffset;
            float angle = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
            if (angle > 90) angle -= 180;
            if (angle < -90) angle += 180;
            _dimension.visible = true;
            _dimension.style.left = mid.x;
            _dimension.style.top = mid.y;
            _dimension.style.rotate = new Rotate(new Angle(angle, AngleUnit.Degree));
        }

        // ---- vector drawing ----

        void DrawVector(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            if (_showCoverage)
            {
                // S1M tiles: a deeper fill with their 10 km edges drawn, so the published grid shows.
                var fill = S1mColour;
                fill.a = 0.36f;
                var edge = S1mColour;
                edge.a = 0.8f;
                p.fillColor = fill;
                p.strokeColor = edge;
                p.lineWidth = 1f;
                foreach (var c in _s1mCorners)
                {
                    p.BeginPath();
                    p.MoveTo(ToLocal(c[0]));
                    for (int i = 1; i < 4; i++) p.LineTo(ToLocal(c[i]));
                    p.ClosePath();
                    p.Fill();
                    p.Stroke();
                }
            }
            if (_coreOutline == null) return;

            // The plan, in the mockup's grammar: everything planned is dashed surveyor's orange (solid would
            // mean built). The 3 km ring that downloads with the site is a finer, fainter dash.
            Polyline(p, _ringOutline, new Color(PlanOrange.r, PlanOrange.g, PlanOrange.b, 0.6f), 1.2f, 4, 4);
            var fillCore = PlanOrange;
            fillCore.a = 0.08f;
            p.fillColor = fillCore;
            p.BeginPath();
            p.MoveTo(ToLocal(_coreOutline[0]));
            for (int i = 1; i < _coreOutline.Length; i++) p.LineTo(ToLocal(_coreOutline[i]));
            p.ClosePath();
            p.Fill();
            Polyline(p, _coreOutline, PlanOrange, 2f, 7, 4);

            // The dimension line: extension lines from the top corners, and the line between them.
            if (DimensionLine(out var a, out var b, out var n) && (b - a).magnitude >= 60)
            {
                p.strokeColor = _imagery ? DimensionOnImagery : DimensionOnTopo;
                p.lineWidth = 1f;
                p.lineCap = LineCap.Butt;
                p.BeginPath();
                p.MoveTo(a + n * 5); p.LineTo(a + n * (DimensionOffset + 4));
                p.MoveTo(b + n * 5); p.LineTo(b + n * (DimensionOffset + 4));
                p.MoveTo(a + n * DimensionOffset); p.LineTo(b + n * DimensionOffset);
                p.Stroke();
            }
        }

        /// <summary>A closed line through the points, dashed <paramref name="on"/> px on and <paramref name="off"/> px off.</summary>
        void Polyline(Painter2D p, GeoPoint[] points, Color colour, float width, float on, float off)
        {
            p.strokeColor = colour;
            p.lineWidth = width;
            p.lineJoin = LineJoin.Miter;
            p.lineCap = LineCap.Butt;
            float phase = 0;   // distance into the current on+off cycle
            Vector2 first = ToLocal(points[0]), a = first;
            for (int i = 1; i <= points.Length; i++)
            {
                Vector2 b = i < points.Length ? ToLocal(points[i]) : first;
                float length = (b - a).magnitude, at = 0;
                while (at < length)
                {
                    bool drawing = phase < on;
                    float run = Mathf.Min(length - at, (drawing ? on : on + off) - phase);
                    if (drawing)
                    {
                        p.BeginPath();
                        p.MoveTo(Vector2.Lerp(a, b, at / length));
                        p.LineTo(Vector2.Lerp(a, b, (at + run) / length));
                        p.Stroke();
                    }
                    at += run;
                    phase = (phase + run) % (on + off);
                }
                a = b;
            }
        }

        void Release()
        {
            _viewCts.Cancel();
            _viewCts = new CancellationTokenSource();
            _tilesCts.Cancel();
            _tilesCts = new CancellationTokenSource();
            _pending.Clear();
            foreach (var image in _shown.Values) Recycle(image);
            _shown.Clear();
            foreach (var tex in _textures.Values) UnityEngine.Object.Destroy(tex);
            _textures.Clear();
            _textureOrder.Clear();
            if (_coverageImage.image is Texture2D t) { UnityEngine.Object.Destroy(t); _coverageImage.image = null; }
        }
    }
}
