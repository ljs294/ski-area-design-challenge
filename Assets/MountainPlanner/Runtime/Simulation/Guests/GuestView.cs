using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SI-16: the crowd in the snapshot — the counts, the demand stack line by line (GU-03), the day's reasons (GU-13),
    /// the reputations (GU-18), the Popularity cells (4.21), and the one skier the player has selected (GU-02).
    ///
    /// The other skiers are **not** here. Their places ride in the render column
    /// (<see cref="SkierRenderBuffers"/>), which is handed over by a pointer swap beside the snapshot, because at
    /// thousands of skiers their records are far larger than SI-16's 64 KB and copying them every tick would be the most
    /// expensive thing the simulation did. The day records never leave the worker at all (GU-02).
    /// </summary>
    public sealed class GuestView
    {
        /// <summary>
        /// Set in a day record's or a render record's segment to mean "a lift", not a trail segment: the rest of the
        /// value is the lift's or the edge's index.
        /// </summary>
        public const ushort LiftMark = 0x8000;

        public int SkierCount;
        /// <summary>Skiers by <see cref="GuestState"/>, indexed by its value.</summary>
        public readonly int[] ByState = new int[8];

        // GU-03: the day's demand, per archetype, and what each line of the stack did to it.
        public readonly int[] Demand = new int[Pacing.Archetypes];
        public readonly int[] Arrived = new int[Pacing.Archetypes];
        /// <summary>Permille per archetype per <see cref="DemandLine"/> id.</summary>
        public readonly int[] Stack = new int[Pacing.Archetypes * SimWorld.DemandLines];
        public int LiftCapacity, TerrainCapacity;
        /// <summary>GU-06, GU-07: the arrival places, and per band how much of the mountain it can ski home from.</summary>
        public int Bases, Nodes;
        public readonly int[] Reachable = new int[Pacing.Bands];
        public readonly int[] UsableBases = new int[Pacing.Bands];

        // The day's guest ledger (SI-10 balances it).
        public int ArrivedClosed, Capped, WentHome, Stranded, Left;

        // GU-18: the reputations, thousandths, and the headline.
        public readonly int[] Reputation = new int[Pacing.Archetypes];
        public int HeadlineReputation;

        /// <summary>GU-13: satisfaction moved today per archetype per <see cref="SatisfactionReason"/> id, thousandths.</summary>
        public readonly long[] Reasons = new long[Pacing.Archetypes * SimWorld.Reasons];

        /// <summary>4.21's cells: 0–100, or −1 for "—" (nobody has skied or ridden anything yet).</summary>
        public readonly sbyte[] LiftPopularity, TrailPopularity;

        // GU-02: the selected skier. Serial 0 means nobody is selected.
        public int SelectedSerial;
        public byte SelectedBand, SelectedArch, SelectedState;
        public int SelectedAbility, SelectedLaps, SelectedSatisfaction, SelectedWaitSeconds, SelectedStayLaps;
        public int SelectedPartySize;
        public long SelectedHereSinceTick;
        /// <summary>GU-07: the factors behind its last choice, permille, by <see cref="ChoiceFactor"/> id.</summary>
        public readonly int[] SelectedWhy = new int[SimWorld.ChoiceFactors];
        /// <summary>GU-02: its day record, oldest first — the segment (or lift) and the ticks it was on it.</summary>
        public int SelectedRecordCount;
        public readonly ushort[] SelectedRecordSegment, SelectedRecordEntry, SelectedRecordExit;

        public GuestView(Pacing p)
        {
            LiftPopularity = new sbyte[p.MaxLifts];
            TrailPopularity = new sbyte[p.MaxTrails];
            SelectedRecordSegment = new ushort[p.RecordSegments];
            SelectedRecordEntry = new ushort[p.RecordSegments];
            SelectedRecordExit = new ushort[p.RecordSegments];
        }

        /// <summary>SI-16's budget: what this block costs a snapshot at its full size.</summary>
        public int SizeBytes =>
            64 + ByState.Length * 4 + Demand.Length * 4 + Arrived.Length * 4 + Stack.Length * 4
            + Reputation.Length * 4 + Reasons.Length * 8
            + LiftPopularity.Length + TrailPopularity.Length
            + SelectedWhy.Length * 4 + SelectedRecordSegment.Length * 6;

        public void Clear()
        {
            SkierCount = 0;
            Array.Clear(ByState, 0, ByState.Length);
            Array.Clear(Demand, 0, Demand.Length);
            Array.Clear(Arrived, 0, Arrived.Length);
            Array.Clear(Stack, 0, Stack.Length);
            LiftCapacity = TerrainCapacity = 0;
            Bases = Nodes = 0;
            Array.Clear(Reachable, 0, Reachable.Length);
            Array.Clear(UsableBases, 0, UsableBases.Length);
            ArrivedClosed = Capped = WentHome = Stranded = Left = 0;
            Array.Clear(Reputation, 0, Reputation.Length);
            HeadlineReputation = 0;
            Array.Clear(Reasons, 0, Reasons.Length);
            SelectedSerial = 0;
            SelectedRecordCount = 0;
            Array.Clear(SelectedWhy, 0, SelectedWhy.Length);
        }
    }
}
