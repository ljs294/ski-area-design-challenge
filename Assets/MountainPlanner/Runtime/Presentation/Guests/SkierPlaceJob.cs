using MountainPlanner.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// GU-16 v2 (task 4.13): one frame's place for every skier, from the render column and the precomputed paths. Burst,
    /// one skier per index, nothing allocated. Writes the dot (position and how to draw it, or hidden) and its screen
    /// point for picking.
    ///
    /// - **Skiing, or walking a path:** progress = (draw tick − entry) ÷ (exit − entry), mapped through GU-10's table to
    ///   metres along the edge, then out to its lane and down onto the ground under that lane. GU-10 v3 (Guests Engine
    ///   v0.1): the table is the skier's own band's, and where that band holds back on a pitch the dot traverses, swinging
    ///   across the run (render only; the time down is the worker's).
    /// - **In a line (LO-02):** its rank's place in <see cref="LineLayout"/>, eased there at walking pace, so the line steps
    ///   forward a row as each chair leaves and closes up when someone gives up. Past the line's room it stands at the tail.
    /// - **Walking across an Area to a lift (GU-09):** from the Area's node to the tail of that lift's line.
    /// - **Riding:** on a tow or a carpet, along the lift's line by rope travel; on a chair, hidden (LO-06).
    /// - **Choosing or dwelling:** round its node on a small keyed ring. **Arriving:** not on the hill yet, hidden.
    /// </summary>
    [BurstCompile]
    public struct SkierPlaceJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<SkierRender> Records;
        public double DrawTick;          // day-relative: the rope clock's tick minus the frame's day start
        public float WalkStep;           // metres a queued dot may move this frame
        public float CatchUp;            // the share of a longer way it closes this frame (all of it within about 1.5 s)
        public uint Selected, Hovered;   // serials (0: none)

        [ReadOnly] public NativeArray<int4> Edges;
        [ReadOnly] public NativeArray<float2> EdgeSize;
        [ReadOnly] public NativeArray<float4> Samples;
        [ReadOnly] public NativeArray<float2> Side;
        [ReadOnly] public NativeArray<float> LaneY;
        [ReadOnly] public NativeArray<float3> Table;
        [ReadOnly] public NativeArray<int2> LiftPaths;
        [ReadOnly] public NativeArray<float3> Nodes;
        [ReadOnly] public NativeArray<float3> Slots;
        [ReadOnly] public NativeArray<int2> LiftSlots;
        /// <summary>Per lift index: the rope travel drawn now (ms, cut to 32 bits), and 1 when riders are drawn (tows, carpets).</summary>
        [ReadOnly] public NativeArray<uint> RopeNow;
        [ReadOnly] public NativeArray<byte> DrawRiders;
        /// <summary>Per lift index: how many stand in its line now (the tail a walker heads for).</summary>
        [ReadOnly] public NativeArray<int> LineCount;

        [ReadOnly] public NativeArray<float> Snow;   // metres, 8 m cells
        public float2 SnowOrigin;
        public float SnowCell;
        public int SnowW, SnowH;

        public float4x4 ViewProjection;
        public float2 ScreenSize;
        /// <summary>The camera, for the click target: the dot is drawn pulled towards it (<see cref="DrawnCentre"/>).</summary>
        public float3 CameraPosition;

        /// <summary>Per worker slot: where its dot was last frame and whose it was (the easing state, by slot).</summary>
        [NativeDisableParallelForRestriction] public NativeArray<float3> Prev;
        [NativeDisableParallelForRestriction] public NativeArray<uint> PrevSerial;

        [WriteOnly] public NativeArray<float4> Dots;
        [WriteOnly] public NativeArray<float3> Screen;

        public const float Hidden = -1f;
        /// <summary>How high over the ground a hidden chair rider is taken to be (the follow camera's target).</summary>
        public const float ChairRiderMetres = 8f;
        /// <summary>The dot's class, packed above the band: what the shader colours it by in the State mode.</summary>
        /// <c>ClassOverflow</c>: in the line of a lift whose line has no places at all (not built yet).
        public const int ClassSkiing = 0, ClassQueued = 1, ClassRiding = 2, ClassStanding = 3, ClassOverflow = 4;

        public void Execute(int i)
        {
            var r = Records[i];
            float3 p;
            int cls;
            bool ease = false;
            var st = (GuestState)r.State;
            if (st == GuestState.Skiing || (st == GuestState.Walking && !r.OnALift))
            {
                if (!OnEdge(r, out p)) { Hide(i); return; }
                cls = ClassSkiing;
            }
            else if (st == GuestState.Walking)
            {
                // GU-09: across the Area to the line's tail.
                int lift = r.Index;
                float3 from = r.Node < Nodes.Length ? Nodes[r.Node] : TailOf(lift, 0);
                float3 to = TailOf(lift, lift < LineCount.Length ? LineCount[lift] : 0);
                p = math.lerp(from, to, Progress(r));
                cls = ClassStanding;
            }
            else if (st == GuestState.Queued)
            {
                int lift = r.Index;
                int2 s = lift < LiftSlots.Length ? LiftSlots[lift] : int2.zero;
                if (s.y <= 0) { p = r.Node < Nodes.Length ? Ring(Nodes[r.Node], r.Serial) : float3.zero; cls = ClassOverflow; }
                // Lines grow as long as they need (owner, 2026-11-10): a skier past the end only waits a frame for the
                // layout to lay more places, standing at the tail meanwhile, never in a pile round it.
                else { p = Slots[s.x + math.min((int)r.Rank, s.y - 1)]; cls = ClassQueued; }
                ease = true;
            }
            else if (st == GuestState.Riding)
            {
                int lift = r.Index;
                if (lift >= LiftPaths.Length || lift >= RopeNow.Length) { Hide(i); return; }
                uint now = RopeNow[lift];
                int span = (int)(r.ExitTick - r.EntryTick);
                float f = span > 0 ? math.saturate((int)(now - r.EntryTick) / (float)span) : 1f;
                p = OnLift(lift, f);
                if (lift >= DrawRiders.Length || DrawRiders[lift] == 0)
                {
                    // LO-06: a chair's rider isn't drawn, but where it is still is (the follow camera glides up the line
                    // with it, about a chair's height over the ground).
                    Hide(i);
                    Dots[i] = new float4(p.x, p.y + ChairRiderMetres, p.z, Hidden);
                    return;
                }
                cls = ClassRiding;
            }
            else if (st == GuestState.Choosing || st == GuestState.Dwelling)
            {
                if (r.Node >= Nodes.Length) { Hide(i); return; }
                p = Ring(Nodes[r.Node], r.Serial);
                cls = ClassStanding;
                ease = true;
            }
            else { Hide(i); return; }

            // Easing by slot: the same skier walks to its new place; a new skier in the slot starts where it is.
            int slot = r.Slot;
            if (slot < Prev.Length)
            {
                if (PrevSerial[slot] != r.Serial) { PrevSerial[slot] = r.Serial; Prev[slot] = p; }
                else if (ease)
                {
                    float3 d = p - Prev[slot];
                    float len = math.length(d);
                    // A row forward is a walk; joining a tail far off closes in about 1.5 s whatever the distance
                    // (finding 2, 2026-11-10: at walking pace a far tail drew its newcomers as a loose stream).
                    float step = math.max(WalkStep, len * CatchUp);
                    if (len > step && len < 400f) p = Prev[slot] + d * (step / len);
                    Prev[slot] = p;
                }
                else Prev[slot] = p;
            }

            p.y += SnowAt(p.x, p.z);
            int flags = (r.Serial == Selected && Selected != 0 ? 1 : 0) | (r.Serial == Hovered && Hovered != 0 ? 2 : 0);
            Dots[i] = new float4(p, r.Band + 4 * cls + 64 * flags + 256 * r.Satisfaction);
            // The click target is where the shader draws the dot, not the feet (finding 3, owner 2026-11-10: 0.9 m up close
            // is 20-40 px, so clicks on the dot missed).
            float4 c = math.mul(ViewProjection, new float4(DrawnCentre(p, CameraPosition), 1));
            Screen[i] = c.w > 1e-4f
                ? new float3((c.x / c.w * 0.5f + 0.5f) * ScreenSize.x, (c.y / c.w * 0.5f + 0.5f) * ScreenSize.y, c.w)
                : new float3(0, 0, -1);
        }

        /// <summary>
        /// Where <c>SkierDot.shader</c> centres a dot whose skier stands at <paramref name="feet"/>: a standing skier's middle
        /// (<see cref="SkierLayer.DotLiftMetres"/> up), pulled towards the camera by 1 % of the distance up to
        /// <see cref="SkierLayer.PullMaxMetres"/>, so snow never swallows it. The shader reads the same constants.
        /// </summary>
        public static float3 DrawnCentre(float3 feet, float3 camera)
        {
            float3 world = feet + new float3(0, SkierLayer.DotLiftMetres, 0);
            float3 toCamera = camera - world;
            float dist = math.length(toCamera);
            return world + toCamera / math.max(dist, 1e-3f) * math.min(SkierLayer.PullMaxMetres, dist * SkierLayer.PullShare);
        }

        void Hide(int i)
        {
            Dots[i] = new float4(0, 0, 0, Hidden);
            Screen[i] = new float3(0, 0, -1);
            var r = Records[i];
            if (r.Slot < PrevSerial.Length) PrevSerial[r.Slot] = 0;
        }

        float Progress(in SkierRender r)
        {
            double span = (double)r.ExitTick - r.EntryTick;
            return span > 0 ? (float)math.saturate((DrawTick - r.EntryTick) / span) : 1f;
        }

        bool OnEdge(in SkierRender r, out float3 p)
        {
            p = float3.zero;
            int e = r.Index;
            if (e >= Edges.Length) return false;
            int4 ed = Edges[e];
            if (ed.y < 1) return false;
            float share = Progress(r);
            // GU-10 v3: the share of the band's time → metres along the edge, and how much it is holding back there.
            float along = 0, hold = 1f;
            int tn = ed.w, t0 = ed.z + math.clamp((int)r.Band, 0, SkierPaths.Bands - 1) * tn;
            if (t0 + tn > Table.Length) t0 = ed.z;
            for (int k = 1; k < tn; k++)
            {
                float3 a = Table[t0 + k - 1], b = Table[t0 + k];
                if (share <= b.y || k == tn - 1)
                {
                    float f = b.y > a.y ? math.saturate((share - a.y) / (b.y - a.y)) : 1f;
                    along = math.lerp(a.x, b.x, f);
                    hold = b.z;
                    break;
                }
            }
            int first = ed.x, count = ed.y;
            if (count == 1) { p = Samples[first].xyz; return true; }
            int k0 = math.clamp((int)(along / SkierPaths.StepMetres), 0, count - 2);
            // The last step can be short: walk forward if the sample spacing ran out early.
            while (k0 < count - 2 && Samples[first + k0 + 1].w < along) k0++;
            float4 s0 = Samples[first + k0], s1 = Samples[first + k0 + 1];
            float g = s1.w > s0.w ? math.saturate((along - s0.w) / (s1.w - s0.w)) : 0f;
            float2 side = math.normalizesafe(math.lerp(Side[first + k0], Side[first + k0 + 1], g), new float2(1, 0));
            int lane = math.min(r.Lane, SkierPaths.Lanes - 1);
            float half = EdgeSize[e].y;
            float off = SkierPaths.LaneOffset(lane, half) + Traverse(along, hold, half, r.Serial);
            float edgeOff = half * 0.8f;
            off = math.clamp(off, -edgeOff, edgeOff);
            // The ground under the offset: between the two lanes it falls between.
            float lanePos = half > 1e-3f ? (off / (2f * edgeOff) + 0.5f) * SkierPaths.Lanes - 0.5f : lane;
            int la = math.clamp((int)math.floor(lanePos), 0, SkierPaths.Lanes - 1), lb = math.min(la + 1, SkierPaths.Lanes - 1);
            float lf = math.saturate(lanePos - la);
            int i0 = (first + k0) * SkierPaths.Lanes, i1 = (first + k0 + 1) * SkierPaths.Lanes;
            float y0 = math.lerp(LaneY[i0 + la], LaneY[i0 + lb], lf), y1 = math.lerp(LaneY[i1 + la], LaneY[i1 + lb], lf);
            float3 c = math.lerp(s0.xyz, s1.xyz, g);
            p = new float3(c.x + side.x * off, math.lerp(y0, y1, g), c.z + side.y * off);
            return true;
        }

        /// <summary>GU-10 v3: a traverse's wavelength down the run, metres (one turn each way).</summary>
        public const float TraverseMetres = 24f;

        /// <summary>
        /// GU-10 v3: how far across the run a skier holding back swings at <paramref name="along"/> metres, from its
        /// lane: nothing at ease (hold 1), up to 0.6 of the half width at the caution floor (hold 0.5), phased per skier so
        /// a group of beginners doesn't zig-zag in step.
        /// </summary>
        public static float Traverse(float along, float hold, float halfWidth, uint serial)
        {
            float depth = math.saturate((1f - hold) * 2f);
            if (depth <= 0f) return 0f;
            float phase = ((serial * 2654435761u) >> 8) * (1f / 16777216f);   // keyed per skier, exact
            return math.sin((along / TraverseMetres + phase) * 2f * math.PI) * depth * 0.6f * halfWidth;
        }

        float3 OnLift(int lift, float f)
        {
            int2 lp = LiftPaths[lift];
            if (lp.y < 1) return float3.zero;
            if (lp.y == 1) return Samples[lp.x].xyz;
            float total = Samples[lp.x + lp.y - 1].w;
            float along = f * total;
            int k0 = math.clamp((int)(along / SkierPaths.StepMetres), 0, lp.y - 2);
            while (k0 < lp.y - 2 && Samples[lp.x + k0 + 1].w < along) k0++;
            float4 s0 = Samples[lp.x + k0], s1 = Samples[lp.x + k0 + 1];
            float g = s1.w > s0.w ? math.saturate((along - s0.w) / (s1.w - s0.w)) : 0f;
            return math.lerp(s0.xyz, s1.xyz, g);
        }

        float3 TailOf(int lift, int rank)
        {
            int2 s = lift < LiftSlots.Length ? LiftSlots[lift] : int2.zero;
            if (s.y <= 0) return lift < LiftPaths.Length && LiftPaths[lift].y > 0 ? Samples[LiftPaths[lift].x].xyz : float3.zero;
            return Slots[s.x + math.clamp(rank, 0, s.y - 1)];
        }

        /// <summary>A keyed spot 2–6 m round a point, so a crowd at a node is a crowd and not one dot.</summary>
        static float3 Ring(float3 c, uint serial)
        {
            uint h = serial * 2654435761u;
            float angle = (h & 0xFFFF) / 65536f * 6.2831853f;
            float radius = 2f + ((h >> 16) & 0xFF) / 255f * 4f;
            return new float3(c.x + math.cos(angle) * radius, c.y, c.z + math.sin(angle) * radius);
        }

        float SnowAt(float x, float z)
        {
            if (SnowW < 2 || SnowH < 2 || !Snow.IsCreated || Snow.Length < SnowW * SnowH) return 0;
            float u = math.clamp((x - SnowOrigin.x) / SnowCell - 0.5f, 0, SnowW - 1);
            float v = math.clamp((z - SnowOrigin.y) / SnowCell - 0.5f, 0, SnowH - 1);
            int i = math.min((int)u, SnowW - 2), j = math.min((int)v, SnowH - 2);
            float fu = u - i, fv = v - j;
            float a = Snow[j * SnowW + i], b = Snow[j * SnowW + i + 1];
            float c = Snow[(j + 1) * SnowW + i], d = Snow[(j + 1) * SnowW + i + 1];
            float s = math.lerp(math.lerp(a, b, fu), math.lerp(c, d, fu), fv);
            return math.isnan(s) ? 0 : s;
        }
    }
}
