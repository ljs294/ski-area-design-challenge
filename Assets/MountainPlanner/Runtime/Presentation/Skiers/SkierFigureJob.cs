using System.Runtime.InteropServices;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MountainPlanner.Presentation.Skiers
{
    /// <summary>One drawn figure, as the shader reads it (32 bytes; SkierFigure.shader's <c>Figure</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FigureInstance
    {
        /// <summary>Where the root stands (the snow between the boots, or the seat) and its heading (radians, 0 = +z).</summary>
        public float4 PositionYaw;
        /// <summary>The two baked frames it is between, and how far from the first (0..1).</summary>
        public uint FrameA, FrameB;
        public float Blend;
        public uint Outfit;
    }

    /// <summary>
    /// Task 4.22: what each guest's figure looks like this frame, from the dots <see cref="SkierPlaceJob"/> placed: which
    /// figure (skier or snowboarder, keyed on the serial), which LOD (by distance, with hysteresis, as budgets.json says),
    /// which clip and frame (the band's turn on the slope's pitch while skiing; a shuffle or a push while moving on the flat;
    /// a keyed waiting pose while standing), and its outfit. Off-screen and far guests are skipped; the rest are sorted
    /// by figure and LOD, one draw each. Burst, one thread (3,000 guests is well under a tenth of a millisecond).
    /// </summary>
    [BurstCompile]
    public struct SkierFigureJob : IJob
    {
        public const int Kinds = 2, Lods = 6, Groups = Kinds * Lods;
        /// <summary>Clip slots per figure: 16 turns (style × pitch), then travel, three waiting poses, seated.</summary>
        public const int SlotTravel = 16, SlotStand = 17, SlotIdle2 = 18, SlotIdle3 = 19, SlotSeated = 20, Slots = 21;
        public const int Pitches = 4;
        public const float PitchStep = 12f;
        /// <summary>Metres a guest moves per shuffle or push cycle, and the pace under which it stands still instead.</summary>
        public const float StrideMetres = 1.6f, StillMetresPerSecond = 0.25f;

        [ReadOnly] public NativeArray<SkierRender> Records;
        [ReadOnly] public NativeArray<float4> Dots;
        [ReadOnly] public NativeArray<float4> Facing;
        public int Count;
        public float3 CameraPosition;
        [ReadOnly] public NativeArray<float4> Planes;
        /// <summary>Distance × this = the distance a 60° camera at 1080p would see the figure at (budgets.json's terms).</summary>
        public float LodScale;
        [ReadOnly] public NativeArray<float> Until;
        public float Hysteresis;
        public float RealSeconds;
        public int Outfits;
        /// <summary>Per figure kind × slot: the clip's first frame and its frame count.</summary>
        [ReadOnly] public NativeArray<int2> Clips;
        /// <summary>Chair riders (<see cref="SkierChairRiders"/>): the seat and heading, and the outfit (top bit: a snowboarder).</summary>
        [ReadOnly] public NativeArray<float4> Seated;
        [ReadOnly] public NativeArray<uint> SeatedKey;
        public int SeatedCount;

        // per worker slot, kept between frames
        public NativeArray<uint> PrevSerial;
        public NativeArray<float3> PrevPosition;
        public NativeArray<float> Odometer;
        public NativeArray<byte> LastLod;

        public NativeArray<FigureInstance> Scratch;
        public NativeArray<byte> ScratchGroup;
        /// <summary>Out: the instances sorted by group, and each group's start (Groups + 1 entries).</summary>
        public NativeArray<FigureInstance> Sorted;
        public NativeArray<int> Starts;

        public static bool IsBoarder(uint serial) => SkierPlaceJob.IsBoarder(serial);

        public void Execute()
        {
            int n = 0;
            float last = Until[Lods - 1];
            for (int i = 0; i < Count; i++)
            {
                var d = Dots[i];
                if (d.w < 0) continue;
                var r = Records[i];
                float3 p = d.xyz;
                float far = math.distance(p, CameraPosition) * LodScale;
                if (far > last) continue;
                bool inside = Inside(p + new float3(0, 0.9f, 0));
                int kind = IsBoarder(r.Serial) ? 1 : 0;
                int slot = r.Slot;
                bool fresh = slot >= PrevSerial.Length || PrevSerial[slot] != r.Serial;
                float moved = 0;
                int lod = RawLod(far);
                if (slot < PrevSerial.Length)
                {
                    if (fresh) { PrevSerial[slot] = r.Serial; PrevPosition[slot] = p; Odometer[slot] = 0; LastLod[slot] = (byte)lod; }
                    else
                    {
                        moved = math.distance(p.xz, PrevPosition[slot].xz);
                        if (moved > 50f) moved = 0;   // a jump (a new design, a teleport): not a walk
                        Odometer[slot] += moved;
                        PrevPosition[slot] = p;
                        lod = Hysteretic(far, LastLod[slot]);
                        LastLod[slot] = (byte)lod;
                    }
                }
                if (!inside) continue;

                var f = Facing[i];
                float2 dir = math.lengthsq(f.xy) > 1e-6f ? math.normalize(f.xy) : Keyed(r.Serial);
                var st = (GuestState)r.State;
                int clip;
                float phase = 0;
                bool moving = RealSeconds > 1e-4f && moved / RealSeconds > StillMetresPerSecond;
                float odo = slot < Odometer.Length ? Odometer[slot] : 0;
                if (st == GuestState.Skiing)
                {
                    int style = math.clamp((int)r.Band, 0, 3);
                    int pitch = math.clamp((int)math.round(math.max(f.w, 0) / PitchStep), 0, Pitches - 1);
                    clip = style * Pitches + pitch;
                    phase = f.z;
                }
                else if (st == GuestState.Walking || moving)
                {
                    clip = SlotTravel;
                    phase = f.z >= 0 && st == GuestState.Walking ? f.z : odo / StrideMetres;
                }
                else if (st == GuestState.Riding)
                    clip = kind == 0 ? SlotStand : SlotIdle2;   // on a tow or a carpet: a skier stands, a boarder rides one foot out
                else
                {
                    uint h = (r.Serial * 2246822519u) >> 28;
                    clip = h < 6 ? SlotStand : h < 11 ? SlotIdle2 : SlotIdle3;
                }

                int2 range = Clips[kind * Slots + clip];
                FigureInstance fi;
                fi.PositionYaw = new float4(p, math.atan2(dir.x, dir.y));
                Frames(range, phase, out fi.FrameA, out fi.FrameB, out fi.Blend);
                fi.Outfit = Outfits > 0 ? r.Serial % (uint)Outfits : 0;
                Scratch[n] = fi;
                ScratchGroup[n] = (byte)(kind * Lods + lod);
                n++;
            }
            for (int j = 0; j < SeatedCount && n < Scratch.Length; j++)
            {
                var q = Seated[j];
                float far = math.distance(q.xyz, CameraPosition) * LodScale;
                if (far > last || !Inside(q.xyz + new float3(0, 0.5f, 0))) continue;
                uint key = SeatedKey[j];
                int kind = (int)(key >> 31);
                FigureInstance fi;
                fi.PositionYaw = q;
                Frames(Clips[kind * Slots + SlotSeated], 0, out fi.FrameA, out fi.FrameB, out fi.Blend);
                fi.Outfit = key & 0x7FFF_FFFFu;
                Scratch[n] = fi;
                ScratchGroup[n] = (byte)(kind * Lods + RawLod(far));
                n++;
            }
            Sort(n);
        }

        bool Inside(float3 c)
        {
            for (int k = 0; k < 6; k++)
            {
                var pl = Planes[k];
                if (math.dot(pl.xyz, c) + pl.w < -1.3f) return false;
            }
            return true;
        }

        public static void Frames(int2 range, float phase, out uint a, out uint b, out float blend)
        {
            int frames = math.max(range.y, 1);
            if (frames == 1) { a = b = (uint)range.x; blend = 0; return; }
            float t = (phase - math.floor(phase)) * frames;
            int k = math.min((int)t, frames - 1);
            a = (uint)(range.x + k);
            b = (uint)(range.x + (k + 1) % frames);
            blend = t - k;
        }

        int RawLod(float far)
        {
            int lod = 0;
            while (lod < Lods - 1 && far > Until[lod]) lod++;
            return lod;
        }

        int Hysteretic(float far, int lod)
        {
            lod = math.clamp(lod, 0, Lods - 1);
            while (lod < Lods - 1 && far > Until[lod] * (1f + Hysteresis)) lod++;
            while (lod > 0 && far < Until[lod - 1] * (1f - Hysteresis)) lod--;
            return lod;
        }

        static float2 Keyed(uint serial)
        {
            float a = ((serial * 2654435761u) >> 8) * (6.2831853f / 16777216f);
            return new float2(math.sin(a), math.cos(a));
        }

        void Sort(int n)
        {
            for (int g = 0; g <= Groups; g++) Starts[g] = 0;
            for (int i = 0; i < n; i++) Starts[ScratchGroup[i] + 1]++;
            for (int g = 0; g < Groups; g++) Starts[g + 1] += Starts[g];
            // Starts[g] is now group g's first index; fill with a running cursor per group (kept in Starts by shifting).
            for (int i = 0; i < n; i++)
            {
                int g = ScratchGroup[i];
                Sorted[Starts[g]] = Scratch[i];
                Starts[g]++;
            }
            // Each start moved to the next group's start: shift back.
            for (int g = Groups; g > 0; g--) Starts[g] = Starts[g - 1];
            Starts[0] = 0;
        }
    }
}
