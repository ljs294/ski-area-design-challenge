using System;
using MountainPlanner.Simulation;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// Task 4.13: the skiers on screen as dots. One <see cref="Graphics.RenderPrimitives"/> call draws every dot (six
    /// vertices each, read from a structured buffer by instance), coloured by ability band: green, blue, black and red for
    /// experts (owner, 2026-11-09). A frame copies the newest render column in, places every dot in one Burst job
    /// (<see cref="SkierPlaceJob"/>), uploads the positions and draws; picking reads the same job's screen points. Nothing
    /// is allocated per frame: every buffer is sized once to the skier cap.
    /// </summary>
    public sealed class SkierLayer : IDisposable
    {
        public const string ShaderResource = "MountainPlannerGuests/SkierDot";
        /// <summary>The dot's size at 100% UI scale and its hit radius ("9 px dots, 8 px hit radius", p4-02 §10).</summary>
        public const float DotPixels = 9f, HitPixels = 8f;
        /// <summary>A dot never draws bigger than this many metres across, so a quad line's four columns stay four.</summary>
        public const float DotMetres = 1.2f, MinDotPixels = 6f;   // 4 px read as nothing on snow (first capture)
        /// <summary>A queued skier's walking pace on screen, metres per real second at 1×.</summary>
        public const float WalkMetresPerSecond = 1.2f;
        /// <summary>A queued dot far from its place closes the gap by a factor e every this many seconds (5 % left after 1.5 s).</summary>
        public const float CatchUpSeconds = 0.5f;
        /// <summary>
        /// The dot is drawn at a standing skier's middle, this far over its feet, and pulled towards the camera by
        /// <see cref="PullShare"/> of the distance up to <see cref="PullMaxMetres"/>. The shader and the click target both
        /// use these (<see cref="SkierPlaceJob.DrawnCentre"/>), so they can't drift apart (finding 3).
        /// </summary>
        public const float DotLiftMetres = 0.9f, PullMaxMetres = 1.5f, PullShare = 0.01f;

        public enum ColourMode { Ability = 0, State = 1, Satisfaction = 2 }

        readonly int _cap;
        readonly Material _material;
        NativeArray<SkierRender> _records;
        NativeArray<float4> _dots;
        NativeArray<float3> _screen;
        NativeArray<float3> _prev;
        NativeArray<uint> _prevSerial;
        NativeArray<uint> _ropeNow;
        NativeArray<byte> _drawRiders;
        NativeArray<int> _lineCount;
        NativeArray<float> _snow;
        NativeArray<float3> _noSlots;
        NativeArray<int2> _noLiftSlots;
        GraphicsBuffer _buffer;
        int _count;
        SkierPaths _paths;
        LineLayout _lines;
        float2 _snowOrigin;
        float _snowCell = 8f;
        int _snowW, _snowH;
        long _frameTick = long.MinValue, _frameRevision = long.MinValue;
        long _dayStart;
        float2 _screenSize;
        static readonly int DotsId = Shader.PropertyToID("_Dots"), PixelsId = Shader.PropertyToID("_DotPixels"),
                            MetresId = Shader.PropertyToID("_DotMetres"), MinPixelsId = Shader.PropertyToID("_MinDotPixels"),
                            ModeId = Shader.PropertyToID("_ColourMode"), DimId = Shader.PropertyToID("_Dim"), ZTestId = Shader.PropertyToID("_ZTest"),
                            ScreenPixelsId = Shader.PropertyToID("_ScreenPixels"), LiftId = Shader.PropertyToID("_DotLift"),
                            PullMaxId = Shader.PropertyToID("_PullMax"), PullShareId = Shader.PropertyToID("_PullShare");

        public uint Selected, Hovered;
        /// <summary>Debug (-dotsontop): draw every dot over everything, to tell "not drawn" from "hidden by the ground".</summary>
        public bool OnTop;
        public ColourMode Mode = ColourMode.Ability;
        /// <summary>Task 4.13 PR C: how far the dots fade back (0 none, 1 gone), but never the selected or hovered one.</summary>
        public float Dim;
        public float UiScale = 1f;
        public bool Visible = true;
        /// <summary>The dots drawn last frame (hidden riders and arrivals not counted).</summary>
        public int Drawn { get; private set; }
        public SkierPaths Paths => _paths;
        public LineLayout Lines => _lines;
        /// <summary>Whether the paths match the column's design (a new design waits for its paths before anything draws).</summary>
        public bool Ready => _paths != null && _count > 0 && _frameRevision == _paths.Revision;

        public SkierLayer(Shader shader, int cap)
        {
            _cap = Math.Max(1, cap);
            _material = shader != null ? new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true } : null;
            _records = new NativeArray<SkierRender>(_cap, Allocator.Persistent);
            _dots = new NativeArray<float4>(_cap, Allocator.Persistent);
            _screen = new NativeArray<float3>(_cap, Allocator.Persistent);
            _prev = new NativeArray<float3>(_cap, Allocator.Persistent);
            _prevSerial = new NativeArray<uint>(_cap, Allocator.Persistent);
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _cap, 16);
            Lifts(1);
            _snow = new NativeArray<float>(1, Allocator.Persistent);
            _noSlots = new NativeArray<float3>(1, Allocator.Persistent);
            _noLiftSlots = new NativeArray<int2>(1, Allocator.Persistent);
        }

        void Lifts(int n)
        {
            if (_ropeNow.IsCreated && _ropeNow.Length >= n) return;
            if (_ropeNow.IsCreated) { _ropeNow.Dispose(); _drawRiders.Dispose(); _lineCount.Dispose(); }
            n = Math.Max(n, 8);
            _ropeNow = new NativeArray<uint>(n, Allocator.Persistent);
            _drawRiders = new NativeArray<byte>(n, Allocator.Persistent);
            _lineCount = new NativeArray<int>(n, Allocator.Persistent);
        }

        /// <summary>A new design's paths (the old ones are disposed). The column's revision must match before it draws.</summary>
        public void SetPaths(SkierPaths paths)
        {
            if (ReferenceEquals(paths, _paths)) return;
            _paths?.Dispose();
            _paths = paths;
            if (paths != null) Lifts(paths.LiftCount);
        }

        public void SetLines(LineLayout lines)
        {
            if (ReferenceEquals(lines, _lines)) return;
            _lines?.Dispose();
            _lines = lines;
        }

        /// <summary>
        /// SI-17: the snow under the dots, copied from the screen's mirror of the field when it changes (bounded by the
        /// caller to the staging ring's drains, never per frame).
        /// </summary>
        public void SetSnow(int width, int height, Vector2 origin, float cell, float[] depthMetres)
        {
            if (width < 2 || height < 2 || depthMetres == null || depthMetres.Length < width * height) { _snowW = _snowH = 0; return; }
            if (_snow.Length != width * height) { _snow.Dispose(); _snow = new NativeArray<float>(width * height, Allocator.Persistent); }
            NativeArray<float>.Copy(depthMetres, _snow, width * height);
            _snowW = width; _snowH = height; _snowOrigin = new float2(origin.x, origin.y); _snowCell = cell;
        }

        /// <summary>Per lift index (the design's order): its rope travel drawn now, whether riders show, its line now.</summary>
        public void SetLift(int index, double ropeSeconds, bool drawRiders, int lineCount)
        {
            if (index < 0) return;
            Lifts(index + 1);
            _ropeNow[index] = unchecked((uint)(long)Math.Round(ropeSeconds * 1000.0));
            _drawRiders[index] = (byte)(drawRiders ? 1 : 0);
            _lineCount[index] = lineCount;
        }

        /// <summary>
        /// Once a frame: takes the newest column if it moved on, and places every dot at <paramref name="drawTick"/> (the
        /// rope clock's, so dots and chairs share one clock: GU-16 v2, never past the newest snapshot).
        /// </summary>
        public void Place(SkierRenderBuffers.Frame frame, double drawTick, Camera camera, float realSeconds, int ticksPerRealSecond)
        {
            if (frame != null && (frame.Tick != _frameTick || frame.Revision != _frameRevision))
            {
                _frameTick = frame.Tick;
                _frameRevision = frame.Revision;
                _dayStart = frame.DayStartTick;
                _count = Math.Min(frame.Count, _cap);
                NativeArray<SkierRender>.Copy(frame.Skiers, _records, _count);
            }
            Drawn = 0;
            if (!Ready || camera == null) return;
            GrowLines();
            var lines = _lines;
            var job = new SkierPlaceJob
            {
                Records = _records,
                DrawTick = drawTick - _dayStart,
                // At 8× the line steps eight times as often, so the walk keeps up with it.
                WalkStep = WalkMetresPerSecond * realSeconds * Mathf.Max(1, ticksPerRealSecond / 10f),
                CatchUp = Mathf.Clamp01(realSeconds / CatchUpSeconds),
                Selected = Selected, Hovered = Hovered,
                Edges = _paths.Edges, EdgeSize = _paths.EdgeSize, Samples = _paths.Samples, Side = _paths.Side,
                LaneY = _paths.LaneY, Table = _paths.Table, LiftPaths = _paths.Lifts, Nodes = _paths.Nodes,
                Slots = lines != null ? lines.Slots : _noSlots, LiftSlots = lines != null ? lines.LiftSlots : _noLiftSlots,
                RopeNow = _ropeNow, DrawRiders = _drawRiders, LineCount = _lineCount,
                Snow = _snow, SnowOrigin = _snowOrigin, SnowCell = _snowCell, SnowW = _snowW, SnowH = _snowH,
                // Picking wants the plain projection (no GPU flip): screen points in the Input System's pixels.
                ViewProjection = camera.projectionMatrix * camera.worldToCameraMatrix,
                ScreenSize = _screenSize = new float2(camera.pixelWidth, camera.pixelHeight),
                CameraPosition = camera.transform.position,
                Prev = _prev, PrevSerial = _prevSerial,
                Dots = _dots, Screen = _screen,
            };
            job.Schedule(_count, 256).Complete();
            for (int i = 0; i < _count; i++) if (_dots[i].w >= 0) Drawn++;
            _buffer.SetData(_dots, 0, 0, _count);
        }

        /// <summary>How close to its last place a line may come before it is given more (owner: lines grow as they need).</summary>
        public const int LineHeadroom = 50;
        int _growNext;

        /// <summary>
        /// At most one lift a frame: the first (round from the last one grown) whose line is within <see cref="LineHeadroom"/>
        /// of its places gets <see cref="LineLayout.GrowSlots"/> more, so every skier in a line has a place.
        /// </summary>
        void GrowLines()
        {
            var lines = _lines;
            if (lines == null || !lines.LiftSlots.IsCreated) return;
            int n = Math.Min(lines.LiftSlots.Length, _lineCount.Length);
            for (int k = 0; k < n; k++)
            {
                int i = (_growNext + k) % n;
                if (_lineCount[i] + LineHeadroom <= lines.Capacity(i) || !lines.CanGrow(i)) continue;
                if (lines.Grow(i)) { _growNext = (i + 1) % n; LinesGrown++; }
                return;
            }
        }

        /// <summary>How many times a line has been given more places (logs).</summary>
        public int LinesGrown { get; private set; }

        /// <summary>Draws every dot in one call (after <see cref="Place"/>).</summary>
        public void Draw(Camera camera)
        {
            if (!Visible || !Ready || _material == null || _count == 0 || camera == null) return;
            _material.SetBuffer(DotsId, _buffer);
            _material.SetFloat(PixelsId, DotPixels * UiScale);
            _material.SetFloat(MinPixelsId, MinDotPixels);
            _material.SetFloat(MetresId, DotMetres);
            _material.SetVector(ScreenPixelsId, new Vector4(camera.pixelWidth, camera.pixelHeight, 0, 0));
            _material.SetFloat(ModeId, (float)Mode);
            _material.SetFloat(DimId, Dim);
            _material.SetFloat(LiftId, DotLiftMetres);
            _material.SetFloat(PullMaxId, PullMaxMetres);
            _material.SetFloat(PullShareId, PullShare);
            _material.SetFloat(ZTestId, OnTop ? (float)CompareFunction.Always : (float)CompareFunction.LessEqual);
            var rp = new RenderParams(_material)
            {
                camera = camera,
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 100_000f),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, _count);
        }

        /// <summary>
        /// The nearest dot to <paramref name="pointer"/> within the hit radius, nearer the camera on a tie (screen pixels,
        /// bottom-left origin as the Input System gives them). A pass over the frame's screen points: at 3,000 dots it costs
        /// microseconds, so it needs no hash.
        /// </summary>
        public bool Pick(Vector2 pointer, out uint serial, out Vector3 at)
        {
            serial = 0; at = default;
            if (!Ready) return false;
            // At least the drawn dot's own radius plus 2 px: a selected dot is drawn half as big again.
            float r = Math.Max(HitPixels, DotPixels * 0.75f + 2f) * UiScale, best = r * r;
            float bestDepth = float.MaxValue;
            int found = -1;
            for (int i = 0; i < _count; i++)
            {
                var s = _screen[i];
                if (s.z <= 0) continue;
                float dx = s.x - pointer.x, dy = s.y - pointer.y, d = dx * dx + dy * dy;
                if (d > r * r) continue;
                if (d < best - 4f || (d <= best + 4f && s.z < bestDepth)) { best = d; bestDepth = s.z; found = i; }
            }
            if (found < 0) return false;
            serial = _records[found].Serial;
            var p = _dots[found];
            at = new Vector3(p.x, p.y, p.z);
            return true;
        }

        /// <summary>Where the skier with <paramref name="serial"/> is drawn this frame, and its record (the follow camera, the card).</summary>
        public bool TryFind(uint serial, out Vector3 at, out SkierRender record)
        {
            at = default; record = default;
            if (serial == 0) return false;
            for (int i = 0; i < _count; i++)
            {
                if (_records[i].Serial != serial) continue;
                record = _records[i];
                var p = _dots[i];
                at = new Vector3(p.x, p.y, p.z);
                return p.w >= 0 || record.State == (byte)GuestState.Riding;
            }
            return false;
        }

        /// <summary>
        /// LO-02: the skier one place behind <paramref name="serial"/> in the same line (the front again past the tail), or 0
        /// when that skier isn't in a line or is alone in it.
        /// </summary>
        public uint NextInLine(uint serial)
        {
            int at = -1;
            for (int i = 0; i < _count; i++) if (_records[i].Serial == serial) { at = i; break; }
            if (at < 0 || _records[at].State != (byte)GuestState.Queued) return 0;
            int lift = _records[at].Index, rank = _records[at].Rank;
            uint next = 0, first = 0;
            int nextRank = int.MaxValue, firstRank = int.MaxValue;
            for (int i = 0; i < _count; i++)
            {
                var r = _records[i];
                if (r.State != (byte)GuestState.Queued || r.Index != lift || r.Serial == serial) continue;
                if (r.Rank > rank && r.Rank < nextRank) { nextRank = r.Rank; next = r.Serial; }
                if (r.Rank < firstRank) { firstRank = r.Rank; first = r.Serial; }
            }
            return next != 0 ? next : first;
        }

        /// <summary>Record <paramref name="i"/>'s dot (xyz, packed w; w &lt; 0 hidden) and screen point, for logs.</summary>
        public void Describe(int i, out Vector4 dot, out Vector3 screen)
        {
            dot = default; screen = default;
            if (i < 0 || i >= _count) return;
            var d = _dots[i]; var s = _screen[i];
            dot = new Vector4(d.x, d.y, d.z, d.w); screen = new Vector3(s.x, s.y, s.z);
        }

        /// <summary>Record <paramref name="i"/> this frame (logs, captures).</summary>
        public bool RecordAt(int i, out SkierRender record)
        {
            record = i >= 0 && i < _count ? _records[i] : default;
            return i >= 0 && i < _count;
        }

        /// <summary>The records this frame (drawn or not).</summary>
        public int Count => _count;

        /// <summary>Record <paramref name="i"/>'s dot on screen this frame, if it is drawn and on screen (the pick benchmark).</summary>
        public bool ScreenPointOf(int i, out Vector2 at, out uint serial)
        {
            at = default; serial = 0;
            if (!Ready || i < 0 || i >= _count || _dots[i].w < 0) return false;
            var s = _screen[i];
            if (s.z <= 0 || s.x < 0 || s.y < 0 || s.x > _screenSize.x || s.y > _screenSize.y) return false;
            at = new Vector2(s.x, s.y);
            serial = _records[i].Serial;
            return true;
        }

        /// <summary>The serial at the back of lift <paramref name="lift"/>'s line (its highest rank), or 0 with nobody in it.</summary>
        public uint LastInLine(int lift)
        {
            uint last = 0;
            int rank = -1;
            for (int i = 0; i < _count; i++)
            {
                var r = _records[i];
                if (r.State == (byte)GuestState.Queued && r.Index == lift && r.Rank > rank) { rank = r.Rank; last = r.Serial; }
            }
            return last;
        }

        /// <summary>The serial at the front of lift <paramref name="lift"/>'s line (rank 0), or 0 with nobody in it.</summary>
        public uint FrontOfLine(int lift)
        {
            for (int i = 0; i < _count; i++)
            {
                var r = _records[i];
                if (r.State == (byte)GuestState.Queued && r.Index == lift && r.Rank == 0) return r.Serial;
            }
            return 0;
        }

        public void Dispose()
        {
            _paths?.Dispose(); _paths = null;
            _lines?.Dispose(); _lines = null;
            if (_records.IsCreated) _records.Dispose();
            if (_dots.IsCreated) _dots.Dispose();
            if (_screen.IsCreated) _screen.Dispose();
            if (_prev.IsCreated) _prev.Dispose();
            if (_prevSerial.IsCreated) _prevSerial.Dispose();
            if (_ropeNow.IsCreated) _ropeNow.Dispose();
            if (_drawRiders.IsCreated) _drawRiders.Dispose();
            if (_lineCount.IsCreated) _lineCount.Dispose();
            if (_snow.IsCreated) _snow.Dispose();
            if (_noSlots.IsCreated) _noSlots.Dispose();
            if (_noLiftSlots.IsCreated) _noLiftSlots.Dispose();
            _buffer?.Release(); _buffer = null;
            if (_material != null) UnityEngine.Object.Destroy(_material);
        }
    }
}
