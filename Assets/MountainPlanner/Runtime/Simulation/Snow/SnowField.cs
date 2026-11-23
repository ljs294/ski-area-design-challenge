using System;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>SN-13: the surface types. Values are saved: never renumber.</summary>
    public enum SnowType : byte
    {
        None = 0,
        /// <summary>Powder.</summary>
        P = 1,
        /// <summary>Packed powder: traffic on powder, or a first and second grooming (SN-14 v4, task 4.08).</summary>
        PP = 2,
        /// <summary>Machine groomed: snow reworked again and again, a bit stale (SN-14 v4: the third grooming in a row, or
        /// any grooming of old granular snow).</summary>
        MG = 3,
        /// <summary>Hard packed (PR B).</summary>
        HP = 4,
        /// <summary>Icy surface: rain or glaze, then a refreeze (SN-14 v2).</summary>
        IS = 5,
        /// <summary>Corn: the spring cycle's thaw (SN-14 v2).</summary>
        CO = 6,
        /// <summary>Frozen granular: a thaw, then a refreeze.</summary>
        FG = 7,
        /// <summary>Loose granular (traffic on a crust, PR B).</summary>
        LG = 8,
        /// <summary>Wet granular: a thaw or rain.</summary>
        WG = 9,
        /// <summary>Wet powder: new snow near freezing, or powder's first thaw.</summary>
        WP = 10,
        /// <summary>Machine made (SN-13 v2, task 4.08): new snow from the guns or a paint, between wet powder and powder; it
        /// packs, grooms and thaws as powder does.</summary>
        MM = 11,
    }

    /// <summary>
    /// SN-02 v2, SN-04 (task 4.07 PR A2): the live 8 m snow, one cell per <see cref="SimSnow"/> cell. Per cell: A1's pack as
    /// the pillows proved it (<see cref="SnowPack"/>, 28 B), then the conditions record: the type (SN-13), its age in face
    /// hours (SN-18 v2, to 255), the water fallen since the last surface event (SN-14 v2's powder test, 10 µm units), and the
    /// spring-cycle state (SN-14 v2's corn: the thaw–refreeze cycles counted and the face day of the last, and the hours
    /// the skin has been cold and whether the wet came from rain). About 34 B a cell. A bare cell is all zeros.
    /// The snow threads write it inside an hour's job; the worker reads it only between jobs.
    /// </summary>
    internal sealed class SnowField
    {
        public readonly int Width, Height;
        public readonly SnowPack[] Pack;
        public readonly byte[] Type, Age, Flags, CycleDay;
        public readonly ushort[] NewSince;
        /// <summary>
        /// Task 4.14 PR B (WS-04, agreed with 4.07): each cell's runoff in the hour in flight, µm of water (rain on bare ground,
        /// or what leaves the pack, its rain included); 0 where nothing ran (a lake, no weather, a tile not read). Written by
        /// the hour for every cell, read only by the water at the landing; the snow never reads it, and it isn't saved.
        /// </summary>
        public readonly int[] RunoffHour;

        // Flags: bit 0 the wet came from rain or glaze; bits 1–2 cold skin hours (0–3); bits 3–4 thaw–refreeze cycles (0–3).
        public const int RainWet = 1, ColdShift = 1, CycleShift = 3;

        public SnowField(int width, int height)
        {
            Width = width; Height = height;
            int n = width * height;
            Pack = new SnowPack[n];
            Type = new byte[n]; Age = new byte[n]; Flags = new byte[n]; CycleDay = new byte[n];
            NewSince = new ushort[n];
            RunoffHour = new int[n];
        }

        public int Cells => Width * Height;

        /// <summary>Copies every plane into <paramref name="to"/> (the same size): a save's snapshot of the field, taken on the
        /// worker and encoded on the save's task (task 4.07 PR A2).</summary>
        public void CopyTo(SnowField to)
        {
            Array.Copy(Pack, to.Pack, Pack.Length);
            Array.Copy(Type, to.Type, Type.Length); Array.Copy(Age, to.Age, Age.Length);
            Array.Copy(Flags, to.Flags, Flags.Length); Array.Copy(CycleDay, to.CycleDay, CycleDay.Length);
            Array.Copy(NewSince, to.NewSince, NewSince.Length);
        }

        /// <summary>Bytes held per cell (BG-08 v3's count).</summary>
        public const int BytesPerCell = 28 + 4 + 2;

        /// <summary>A cell back to bare ground.</summary>
        public void Clear(int c)
        {
            Pack[c] = default;
            Type[c] = 0; Age[c] = 0; Flags[c] = 0; CycleDay[c] = 0; NewSince[c] = 0;
        }

        /// <summary>
        /// A seed (<c>SetSnowpack</c>, an older save's depths, <c>-snowseed</c>): <paramref name="mm"/> of settled packed powder
        /// at <paramref name="density"/> kg/m³, cold, a day old. Returns the water it holds now minus what it held (µm).
        /// </summary>
        public long Seed(int c, int mm, int density)
        {
            long before = Pack[c].Swe;
            Clear(c);
            if (mm <= 0) return -before;
            int swe = (int)Math.Min(int.MaxValue / 2, (long)mm * density);   // mm × kg/m³ = g/m² = µm of water
            Pack[c] = new SnowPack
            {
                Swe = swe, Density = density * 1000, SurfaceTemp = -500, DeepTemp = -30_000, Albedo = 750_000,
            };
            Type[c] = (byte)SnowType.PP;
            Age[c] = 24;
            return swe - before;
        }

        /// <summary>The cell's part of its tile's hash: the pack and the record, a bare cell as one zero word.</summary>
        public ulong MixCell(ulong h, int c)
        {
            ref var p = ref Pack[c];
            if (p.Swe == 0) return Mix(h, 0);
            h = Mix(h, (uint)p.Swe | (ulong)(uint)p.Liquid << 32);
            h = Mix(h, (uint)p.SurfaceTemp | (ulong)(uint)p.Albedo << 32);
            h = Mix(h, (uint)p.Density | (ulong)(uint)p.DeepTemp << 32);
            h = Mix(h, (uint)p.SurfaceLiquid | (ulong)Type[c] << 32 | (ulong)Age[c] << 40 | (ulong)Flags[c] << 48 | (ulong)CycleDay[c] << 56);
            return Mix(h, NewSince[c]);
        }

        /// <summary>A word into a tile hash: multiply–xorshift, one step a word (the cells are many; FNV's byte steps cost 8×).</summary>
        public static ulong Mix(ulong h, ulong v)
        {
            h = (h ^ v) * 0x9E37_79B9_7F4A_7C15UL;
            return h ^ (h >> 29);
        }

        public const ulong TileSeed = 0x534E_4F57_5449_4C45UL;   // "SNOWTILE"
    }

    /// <summary>The snow's numbers from Pacing (v9), read once per world.</summary>
    internal readonly struct SnowRules
    {
        public readonly SnowTuning Energy;
        public readonly int CanopyHolds, ShedStartGrade, ShedFullGrade, ShedSteps, ShedPileMax, ShedPileRatio;
        public readonly int DriftWind, DriftWindSpan, DriftShareMax, DriftSteps, DriftExposedRise;
        public readonly int PowderNewUm, WetPowderWetBulb, RefreezeSkin, RefreezeHours, CornCycles, CornWindowDays;
        public readonly int[] QualityBase;
        public readonly int QualityAgePerHour, QualityAgeFloor, SeedDensity, SkylineFade;

        public SnowRules(Pacing p)
        {
            Energy = Tuning(p);
            CanopyHolds = p.CanopyHolds; ShedStartGrade = p.ShedStartGrade; ShedFullGrade = p.ShedFullGrade;
            ShedSteps = Math.Max(1, p.ShedRunout / SimSnow.CellMetres); ShedPileMax = p.ShedPileMax;
            ShedPileRatio = p.ShedPileRatio;
            DriftWind = p.DriftWind; DriftWindSpan = p.DriftWindSpan; DriftShareMax = p.DriftShareMax;
            DriftSteps = Math.Max(1, p.DriftReach / SimSnow.CellMetres); DriftExposedRise = p.DriftExposedRise;
            PowderNewUm = p.PowderNewUm; WetPowderWetBulb = p.WetPowderWetBulb; RefreezeSkin = p.RefreezeSkin;
            RefreezeHours = p.SnowRefreezeHours; CornCycles = p.CornCycles; CornWindowDays = p.CornWindowDays;
            QualityBase = (int[])p.QualityBase.Clone();
            QualityAgePerHour = p.QualityAgePerHour; QualityAgeFloor = p.QualityAgeFloor;
            SeedDensity = p.SeedDensity; SkylineFade = p.SkylineFade;
        }

        /// <summary>SN-09 v2: the energy's numbers from Pacing (the default set is <see cref="SnowTuning.Default"/>).</summary>
        public static SnowTuning Tuning(Pacing p) => new SnowTuning(
            p.SnowSurfaceUm, p.SnowThinUm, p.AlbedoFresh, p.AlbedoCold, p.AlbedoWet, p.AlbedoGround, p.AlbedoResetUm,
            p.AlbedoColdTau, p.AlbedoWetTau, p.CanopyShade, p.SnowWindPer10k, p.SnowCalmMms, p.GroundHeatMw,
            p.SnowHoldPermille, p.SnowCrumbUm, p.SettleTau, p.SettleWetFactor, p.DensityCold, p.DensityWarm, p.DensityWet);

        /// <summary>SN-16 v2: a type's quality at an age (‰).</summary>
        public int Quality(int type, int age)
        {
            if (type <= 0 || type > QualityBase.Length) return 0;
            int ageTerm = Math.Max(QualityAgeFloor, 1000 - QualityAgePerHour * age);
            return QualityBase[type - 1] * ageTerm / 1000;
        }
    }

    /// <summary>
    /// The daily snow ledger (task 4.07 PR A2): every µm of water that reached the field this face day, and where it went.
    /// It balances to the quantum: <c>Start + Seeded + Fell − Canopy − ToWater − OffMap − Runoff − Sublimated = End</c>. Fell
    /// counts rain and snow over every cell, lakes too; Canopy is the snow the trees held back (SN-06 v2: it sublimates
    /// there); ToWater is what fell or drifted onto a lake (SN-25); OffMap is what slid or blew off the map's edge or into a
    /// hole in its heights (SN-06 v2, SN-07 v2); Melt is for the report (it's in Runoff once it leaves).
    /// </summary>
    public struct SnowLedger
    {
        public int Day;
        public long Start, Seeded, Fell, Canopy, ToWater, Runoff, Sublimated, Melt, End, OffMap;

        public long Balance => Start + Seeded + Fell - Canopy - ToWater - OffMap - Runoff - Sublimated - End;

        internal void Add(in SnowLedger o)
        {
            Seeded += o.Seeded; Fell += o.Fell; Canopy += o.Canopy; ToWater += o.ToWater; Runoff += o.Runoff;
            Sublimated += o.Sublimated; Melt += o.Melt; OffMap += o.OffMap;
        }

        internal void AddTo(ref StateHasher h)
        {
            h.Add(Day); h.Add(Start); h.Add(Seeded); h.Add(Fell); h.Add(Canopy); h.Add(ToWater); h.Add(Runoff); h.Add(Sublimated);
            h.Add(Melt); h.Add(End); h.Add(OffMap);
        }
    }
}
