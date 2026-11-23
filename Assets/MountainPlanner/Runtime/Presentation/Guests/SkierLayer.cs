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
        NativeArray<float4> _facing;
        NativeArray<float3> _screen;
        NativeArray<float3> _prev;
        NativeArray<uint> _prevSerial;
        NativeArray<uint> _ropeNow;
        NativeArray<byte> _drawRiders;
        NativeArray<int> _lineCount;
        NativeArray<float> _snow;
        // Task 4.08 PR A: the fine trail snow's lift, slot by slot as the atlas has it, and its page table.
        NativeArray<float> _fineLift;
        NativeArray<int> _finePage;
        int[] _fineSeen = System.Array.Empty<int>();
        ulong _fineLayout;
        int _fineT, _fineTilesX, _fineTilesY;
        float _fineMetres;
        NativeArray<float3> _noSlots;
        NativeArray<float2> _noFacing;
        NativeArray<int2> _noLiftSlots;
        GraphicsBuffer _buffer;
        int _count;
        SkierPaths _paths;
        LineLayout _lines;
        float2 _snowOrigin;
        float _snowCell = 8f;
        /// <summary>Task 4.07 PR A3: whether the dots stand on the snow shell (SurfaceStates.LiftScale, 0 or 1), set every frame so
        /// they follow the Snow layer at once while the copied cells change only every 2 s.</summary>
        public float SnowScale { get; set; } = 1;
        int _snowW, _snowH;
        long _frameTick = long.MinValue, _frameRevision = long.MinValue;
        long _dayStart;
        float2 _screenSize;
        static readonly int DotsId = Shader.PropertyToID("_Dots"), PixelsId = Shader.PropertyToID("_DotPixels"),
                            MetresId = Shader.PropertyToID("_DotMetres"), MinPixelsId = Shader.PropertyToID("_MinDotPixels"),
                            ModeId = Shader.PropertyToID("_ColourMode"), DimId = Shader.PropertyToID("_Dim"), ZTestId = Shader.PropertyToID("_ZTest"),
                            ScreenPixelsId = Shader.PropertyToID("_ScreenPixels"), LiftId = Shader.PropertyToID("_DotLift"),
                            PullMaxId = Shader.PropertyToID("_PullMax"), PullShareId = Shader.PropertyToID("_PullShare"),
                            FigureMetresId = Shader.PropertyToID("_FigureMetres");

        public uint Selected, Hovered;
        /// <summary>
        /// Task 4.22: drawn as figures. The tracks swing with the figures' turns and stand on the drawn snow
        /// (<see cref="SkierPlaceJob.Figures"/>), and a
        /// dot nearer the eye than <see cref="FigureMetres"/> isn't drawn unless it is selected or hovered, or the Traffic
        /// layer has dimmed the dots (its colours read on the figures' dots). Off: the dots as they were.
        /// </summary>
        public bool Figures;
        public float FigureMetres;
        /// <summary>Task 4.22: this frame's records, dots and facings, for the figures (read only, after <see cref="Place"/>).</summary>
        public NativeArray<SkierRender> Records => _records;
        public NativeArray<float4> Dots => _dots;
        public NativeArray<float4> Facing => _facing;
        /// <summary>Debug (-dotsontop): draw every dot over everything, to tell "not drawn" from "hidden by the ground".</summary>
        public bool OnTop;
        public ColourMode Mode = ColourMode.Ability;
        /// <summary>Task 4.13 PR C: how far the dots fade back (0 none, 1 gone), but never the selected or hovered one.</summary>
        public float Dim;
        public float UiScale = 1f;
        public bool Visible = true;
        /// <summary>Task 4.13 PR C2, <c>-dotstress N</c>: each column is padded to this many records (<see cref="DotStress"/>).</summary>
        public int StressTo;
        /// <summary>Task 4.13 PR C2, <c>-skierproof</c>: the stand-in figures near the camera (null: dots only).</summary>
        public SkierProof Proof;
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
            _facing = new NativeArray<float4>(_cap, Allocator.Persistent);
            _screen = new NativeArray<float3>(_cap, Allocator.Persistent);
            _prev = new NativeArray<float3>(_cap, Allocator.Persistent);
            _prevSerial = new NativeArray<uint>(_cap, Allocator.Persistent);
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _cap, 16);
            Lifts(1);
            _snow = new NativeArray<float>(1, Allocator.Persistent);
            _fineLift = new NativeArray<float>(1, Allocator.Persistent);
            _finePage = new NativeArray<int>(1, Allocator.Persistent);
            _noSlots = new NativeArray<float3>(1, Allocator.Persistent);
            _noFacing = new NativeArray<float2>(1, Allocator.Persistent);
            _noLiftSlots = new NativeArray<int2>(1, Allocator.Persistent);
            _linePlace = new NativeArray<int>(_cap, Allocator.Persistent);
            _shown = new NativeArray<int>(_cap, Allocator.Persistent);
            _lineSeats = new NativeArray<int>(8, Allocator.Persistent);
            _colOf = new int[_cap]; _colLine = new int[_cap]; _colStamp = new int[_cap]; _colSerial = new uint[_cap]; _colSection = new int[_cap];
            _order = new int[_cap]; _keys = new long[_cap];
        }

        // ---- task 4.26 BB4: each queued skier's place in its line ----
        NativeArray<int> _linePlace, _shown, _lineSeats;
        int[] _colOf, _colLine, _colStamp, _colSection, _order;
        uint[] _colSerial;
        long[] _keys;
        int[] _nextCol = new int[16];
        readonly int[] _colDepth = new int[64];
        int _stamp;
        long _placedTick = long.MinValue, _placedRevision = long.MinValue;
        LineLayout _placedLines;

        /// <summary>Task 4.26 BB4: record <paramref name="i"/>'s place in its line this frame (row · seats + column), or its rank.</summary>
        public int LinePlaceAt(int i) => i >= 0 && i < _count ? _linePlace[i] : 0;

        /// <summary>Task 4.26 BB4 probe: how many times a skier already in a line was given a new column (it lost its own).</summary>
        public long ColumnResets { get; private set; }

        /// <summary>Task 4.26 BB4 probe: the place record <paramref name="i"/> is walking to now (-1 for none).</summary>
        public int ShownAt(int i) => i >= 0 && i < _count && _records[i].Slot < _shown.Length ? _shown[_records[i].Slot] - 1 : -1;

        /// <summary>
        /// Task 4.26 BB4 (owner 2026-11-23, option A "fill from behind"): which place each queued skier stands on. A skier
        /// keeps the column it joined in for as long as it stays in that line (a newcomer takes the column with fewest,
        /// the next one round on a tie), and stands as many rows from the front as there are skiers ahead of it in that
        /// column. When a chair takes part of the front row, only the skiers behind those gaps step forward, each along
        /// its own lane; nobody crosses a neighbour. The simulation's order is untouched. Once a column (never per frame
        /// without a new one), with no allocation: every queued skier is sorted by line and rank (ties too).
        /// </summary>
        void AssignPlaces()
        {
            var lines = _lines;
            if (_frameTick == _placedTick && _frameRevision == _placedRevision && ReferenceEquals(lines, _placedLines)) return;
            _placedTick = _frameTick; _placedRevision = _frameRevision; _placedLines = lines;
            _stamp++;
            int nLines = lines != null && lines.LiftSlots.IsCreated ? lines.LiftSlots.Length : 0;
            int top = lines != null && lines.TopSlotsAt >= 0 ? lines.TopSlotsAt : int.MaxValue / 2;
            if (_nextCol.Length < nLines) _nextCol = new int[nLines * 2];
            int n = 0;
            for (int i = 0; i < _count; i++)
            {
                var r = _records[i];
                _linePlace[i] = r.Rank;
                int line = LineOf(r, top);
                if (line < 0 || line >= nLines) continue;
                _keys[n] = ((long)line << 32) | ((long)r.Rank << 12) | (uint)(i & 0xFFF);
                _order[n++] = i;
            }
            if (n > 1) Array.Sort<long, int>(_keys, _order, 0, n);
            if (_lineSeats.Length < nLines) { _lineSeats.Dispose(); _lineSeats = new NativeArray<int>(nLines * 2, Allocator.Persistent); }
            // Per line, stretch by stretch from the front (LineLayout.Section): rows across fill lane by lane, a single
            // file in the order of the line. A skier keeps its column within a stretch of rows; walking on into the next
            // stretch it merges, the way a real queue does where it narrows or widens.
            int seats = 1, current = -1, section = 0, sections = 0, rows = 0, start = 0, inSection = 0;
            bool file = false;
            for (int k = 0; k < n; k++)
            {
                int i = _order[k];
                var r = _records[i];
                int line = (int)(_keys[k] >> 32);
                if (line != current)
                {
                    current = line;
                    seats = Math.Max(1, Math.Min(_colDepth.Length, lines.SeatsOf(line)));
                    _lineSeats[line] = seats;
                    sections = lines.SectionCount(line);
                    section = -1; start = 0; rows = 0; inSection = 0; file = false;
                }
                // On to the next stretch once this one is full.
                while (inSection >= rows * seats && section < sections)
                {
                    start += rows * seats;
                    section++;
                    inSection = 0;
                    Array.Clear(_colDepth, 0, seats);
                    if (section < sections) lines.Section(line, section, out rows, out file);
                    else { rows = int.MaxValue / (4 * seats); file = true; }   // past the places laid: in order, at the tail
                }
                int slot = r.Slot;
                if (slot >= _colOf.Length) continue;
                bool mine = _colSerial[slot] == r.Serial && _colLine[slot] == line + 1;
                int col = -1;
                if (file) _linePlace[i] = start + inSection;
                else
                {
                    // Its own column while it stays in this stretch (a pass or two unseen doesn't lose it: a snapshot can show
                    // two skiers on one rank for a moment).
                    if (mine && _colSection[slot] == section && _colOf[slot] >= 0 && _colStamp[slot] >= _stamp - StaleColumnPasses
                        && _colDepth[_colOf[slot] % seats] < rows)
                        col = _colOf[slot] % seats;
                    else
                    {
                        if (mine && _colSection[slot] == section && _colOf[slot] >= 0 && _colStamp[slot] >= _stamp - 2 * StaleColumnPasses) ColumnResets++;
                        int from = _nextCol[line] % seats;
                        col = -1;
                        for (int c = 0; c < seats; c++)
                        {
                            int cc = (from + c) % seats;
                            if (_colDepth[cc] < rows && (col < 0 || _colDepth[cc] < _colDepth[col])) col = cc;
                        }
                        _nextCol[line] = (col + 1) % seats;
                    }
                    _linePlace[i] = start + _colDepth[col]++ * seats + col;
                }
                inSection++;
                _colOf[slot] = col; _colSerial[slot] = r.Serial; _colLine[slot] = line + 1; _colStamp[slot] = _stamp; _colSection[slot] = section;
            }
        }

        /// <summary>How many passes a skier may go unseen in its line and keep its column.</summary>
        const int StaleColumnPasses = 30;

        static int LineOf(SkierRender r, int top) =>
            r.State == (byte)GuestState.Queued ? r.Index : r.State == (byte)GuestState.QueuedDown ? top + r.Index : -1;

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

        /// <summary>
        /// Task 4.08 PR A: the fine trail snow the ground is drawn lifted by: the atlas's layout (a new one lays the copy out
        /// again), then each slot that has gone up since the last call (4 KB a 2 m slot). Called where <see cref="SetSnow"/> is.
        /// </summary>
        public void SetFine(FineSnowAtlas atlas)
        {
            var tiles = atlas?.Tiles;
            if (tiles == null || tiles.Count == 0) { _fineT = 0; _fineLayout = 0; return; }
            int T = tiles.TileFine;
            if (_fineLayout != atlas.LayoutHash)
            {
                _fineLayout = atlas.LayoutHash;
                if (_fineLift.IsCreated) _fineLift.Dispose();
                if (_finePage.IsCreated) _finePage.Dispose();
                _fineLift = new NativeArray<float>(tiles.Count * T * T, Allocator.Persistent);
                _finePage = new NativeArray<int>(tiles.TilesX * tiles.TilesY, Allocator.Persistent);
                for (int k = 0; k < _fineLift.Length; k++) _fineLift[k] = -1;
                for (int k = 0; k < _finePage.Length; k++) _finePage[k] = tiles.Slot[k];
                _fineSeen = new int[tiles.Count];
                _fineT = T; _fineTilesX = tiles.TilesX; _fineTilesY = tiles.TilesY; _fineMetres = atlas.FineMetres;
            }
            for (int s = 0; s < tiles.Count; s++)
            {
                int v = atlas.SlotVersion(s);
                if (v == _fineSeen[s]) continue;
                _fineSeen[s] = v;
                atlas.CopySlotLift(s, _fineLift, s * T * T);
            }
        }

        /// <summary>Per lift index (the design's order): its rope travel drawn now, whether riders show, its line now, and
        /// (task 4.22) whether it is a carpet, whose figures stand on the belt rather than the ground; (task 4.24 PR B, RD-2)
        /// how many stand in its top line.</summary>
        public void SetLift(int index, double ropeSeconds, bool drawRiders, int lineCount, bool belt = false, int topLine = 0)
        {
            if (index < 0) return;
            Lifts(index + 1);
            _ropeNow[index] = unchecked((uint)(long)Math.Round(ropeSeconds * 1000.0));
            _drawRiders[index] = (byte)(drawRiders ? belt ? 2 : 1 : 0);
            _lineCount[index] = lineCount;
            if (index >= _topLine.Length) Array.Resize(ref _topLine, Math.Max(index + 1, _topLine.Length * 2));
            _topLine[index] = topLine;
        }

        /// <summary>RD-2: per lift index, the skiers in its top line (only the growth of the top lines' places reads it).</summary>
        int[] _topLine = new int[8];

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
                if (StressTo > _count) _count = DotStress.Pad(_records, _count, StressTo, unchecked((uint)(frame.Tick - frame.DayStartTick)));
            }
            Drawn = 0;
            if (!Ready || camera == null) return;
            GrowLines();
            AssignPlaces();
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
                Slots = lines != null ? lines.Slots : _noSlots, SlotFacing = lines != null ? lines.Facing : _noFacing, LiftSlots = lines != null ? lines.LiftSlots : _noLiftSlots,
                TopSlotsAt = lines != null && lines.TopSlotsAt >= 0 ? lines.TopSlotsAt : int.MaxValue / 2,
                RopeNow = _ropeNow, DrawRiders = _drawRiders, LineCount = _lineCount,
                Snow = _snow, SnowScale = SnowScale, SnowOrigin = _snowOrigin, SnowCell = _snowCell, SnowW = _snowW, SnowH = _snowH,
                FineLift = _fineLift, FinePage = _finePage, FineT = _fineT, FineTilesX = _fineTilesX, FineTilesY = _fineTilesY, FineMetres = _fineMetres,
                // Picking wants the plain projection (no GPU flip): screen points in the Input System's pixels.
                ViewProjection = camera.projectionMatrix * camera.worldToCameraMatrix,
                ScreenSize = _screenSize = new float2(camera.pixelWidth, camera.pixelHeight),
                CameraPosition = camera.transform.position,
                Prev = _prev, PrevSerial = _prevSerial,
                LinePlace = _linePlace, LineSeats = _lineSeats, Shown = _shown,
                Dots = _dots, Screen = _screen, Facing = _facing, Figures = Figures,
            };
            job.Schedule(_count, 256).Complete();
            for (int i = 0; i < _count; i++) if (_dots[i].w >= 0) Drawn++;
            if (Proof != null && Visible) Proof.Select(_dots, _records, _count, camera);   // hides the dots that become figures
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
            int n = lines.LiftSlots.Length, top = lines.TopSlotsAt >= 0 ? lines.TopSlotsAt : n;
            for (int k = 0; k < n; k++)
            {
                int i = (_growNext + k) % n;
                // RD-2: entries past TopSlotsAt are the top lines (lift i − TopSlotsAt's).
                int queued = i < top ? (i < _lineCount.Length ? _lineCount[i] : 0) : (i - top < _topLine.Length ? _topLine[i - top] : 0);
                if (queued + LineHeadroom <= lines.Capacity(i) || !lines.CanGrow(i)) continue;
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
            _material.SetFloat(FigureMetresId, FigureMetres);
            _material.SetFloat(ZTestId, OnTop ? (float)CompareFunction.Always : (float)CompareFunction.LessEqual);
            var rp = new RenderParams(_material)
            {
                camera = camera,
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 100_000f),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, _count);
            Proof?.Draw(camera);
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
                return p.w >= 0 || record.State == (byte)GuestState.Riding || record.State == (byte)GuestState.RidingDown;
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
            if (at < 0) return 0;
            byte line = _records[at].State;   // the bottom line, or (RD-2) the top line: its own ranks
            if (line != (byte)GuestState.Queued && line != (byte)GuestState.QueuedDown) return 0;
            int lift = _records[at].Index, rank = _records[at].Rank;
            uint next = 0, first = 0;
            int nextRank = int.MaxValue, firstRank = int.MaxValue;
            for (int i = 0; i < _count; i++)
            {
                var r = _records[i];
                if (r.State != line || r.Index != lift || r.Serial == serial) continue;
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
            Proof?.Dispose(); Proof = null;
            if (_records.IsCreated) _records.Dispose();
            if (_dots.IsCreated) _dots.Dispose();
            if (_facing.IsCreated) _facing.Dispose();
            if (_screen.IsCreated) _screen.Dispose();
            if (_prev.IsCreated) _prev.Dispose();
            if (_prevSerial.IsCreated) _prevSerial.Dispose();
            if (_linePlace.IsCreated) _linePlace.Dispose();
            if (_shown.IsCreated) _shown.Dispose();
            if (_lineSeats.IsCreated) _lineSeats.Dispose();
            if (_ropeNow.IsCreated) _ropeNow.Dispose();
            if (_drawRiders.IsCreated) _drawRiders.Dispose();
            if (_lineCount.IsCreated) _lineCount.Dispose();
            if (_snow.IsCreated) _snow.Dispose();
            if (_fineLift.IsCreated) _fineLift.Dispose();
            if (_finePage.IsCreated) _finePage.Dispose();
            if (_noSlots.IsCreated) _noSlots.Dispose();
            if (_noFacing.IsCreated) _noFacing.Dispose();
            if (_noLiftSlots.IsCreated) _noLiftSlots.Dispose();
            _buffer?.Release(); _buffer = null;
            if (_material != null) UnityEngine.Object.Destroy(_material);
        }
    }
}
