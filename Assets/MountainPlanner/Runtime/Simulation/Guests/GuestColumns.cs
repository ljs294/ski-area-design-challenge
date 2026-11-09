using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// GU-02: what a skier is doing. The values are saved and ride in the render column (GU-16), so they are never
    /// renumbered. <see cref="Free"/> is an empty slot.
    /// </summary>
    public enum GuestState : byte
    {
        Free = 0,
        /// <summary>On its way to the mountain; it appears at its arrival node when its tick comes.</summary>
        Arriving = 1,
        /// <summary>At a node, picking the next edge (GU-07); it may be waiting for a lift to open or a spacing gate.</summary>
        Choosing = 2,
        /// <summary>In a lift's line (LO-02).</summary>
        Queued = 3,
        /// <summary>On the way up, hidden for the compressed ride (LO-06).</summary>
        Riding = 4,
        /// <summary>Skiing a trail segment (GU-10).</summary>
        Skiing = 5,
        /// <summary>Walking a path, or across an Area to a lift (GU-09).</summary>
        Walking = 6,
        /// <summary>At a node until its tick comes. Unused in Phase 4; lodges, lessons and rests will need it.</summary>
        Dwelling = 7,
    }

    /// <summary>
    /// GU-01, GU-02 and BG-08: up to <see cref="Pacing.SkierCap"/> skiers as columns, allocated once and reused through a
    /// free stack, so a tick allocates nothing. A later feature (a wallet, needs, a ticket) is a new column, never a change
    /// to a struct, and the sidecar writes columns by tag so adding one stays additive (SI-12).
    ///
    /// Two structures make the cost scale with *activity* rather than with the cap, which is what lets the cap rise from
    /// 3,000 to tens of thousands without a rewrite:
    /// - **The timing wheel.** A skier sits in the bucket of the tick it is next due. A tick walks one bucket, so the work
    ///   is the number of skiers due (about a hundred at 30,000 skiers), not the cap. A skier due further out than the
    ///   wheel is long comes round again and is re-inserted, in pop order, so the order stays deterministic.
    /// - **Parties.** Only a party's lead is scheduled while it travels; the lead moves every member in one go
    ///   (GU-02: a party of one to four boards together). Members are scheduled on their own only while riding, because a
    ///   party too big for one carrier splits across carriers and gathers again at the top.
    /// </summary>
    sealed class GuestColumns
    {
        public readonly int Cap;
        public readonly int RecordSegments;

        // ---- who the skier is ----
        /// <summary>The day's serial, 1 upward: every keyed draw and the skier card name a skier by this, never by slot.</summary>
        public readonly int[] Serial;
        /// <summary>GU-05: the ability band, 0 beginner to 3 expert.</summary>
        public readonly byte[] Band;
        /// <summary>GU-18: the archetype whose reputation this skier moves (Phase 4: the band).</summary>
        public readonly byte[] Arch;
        /// <summary>GU-05: ability, thousandths.</summary>
        public readonly short[] Ability;
        /// <summary>GU-06: the arrival node it came in at, and goes home to by preference.</summary>
        public readonly int[] Home;
        /// <summary>GU-06: the laps it means to ski before leaving.</summary>
        public readonly byte[] StayLaps;
        /// <summary>The tick it reached the mountain (the skier card's "here since").</summary>
        public readonly long[] ArriveTick;

        // ---- the party (GU-02) ----
        /// <summary>The party's id, for the card and for keyed draws (a party decides together).</summary>
        public readonly int[] PartyId;
        /// <summary>The party's lead slot; a lead's own lead is itself.</summary>
        public readonly int[] Lead;
        /// <summary>The next member of the party, from the lead, or −1.</summary>
        public readonly int[] Member;
        /// <summary>On a lead: how many skiers the party has.</summary>
        public readonly byte[] PartySize;
        /// <summary>On a lead: how many members are at the node with it (a party gathers before it picks).</summary>
        public readonly byte[] PartyHere;

        // ---- where it is ----
        public readonly byte[] State;
        /// <summary>1 once the stay is over: it takes its route home (GU-11).</summary>
        public readonly byte[] Homeward;
        /// <summary>The node it is at, or the node it is travelling to.</summary>
        public readonly int[] Node;
        /// <summary>The ski or path edge it is on, or −1.</summary>
        public readonly int[] Edge;
        /// <summary>The lift it is queued for or riding, or −1.</summary>
        public readonly int[] Lift;
        public readonly long[] EntryTick, ExitTick;
        /// <summary>LO-06: the rope's run time at which a rider steps off (a hold stops the rope, so this is not a tick).</summary>
        public readonly long[] RideExitRunMs;
        /// <summary>The tick the wheel next wakes it (not always <see cref="ExitTick"/>: a queued skier wakes to leave).</summary>
        public readonly long[] DueTick;
        /// <summary>SN-24, GU-16: which lateral band of the trail it skis, keyed per segment.</summary>
        public readonly byte[] Lane;
        /// <summary>GU-10: its speed as a share of the trail band's base speed, permille.</summary>
        public readonly short[] Speed;

        // ---- how the day is going ----
        public readonly byte[] Laps;
        public readonly int[] WaitTicks;
        public readonly long[] JoinTick;
        /// <summary>How many choices it has made today: the ordinal of its keyed draws (SI-06).</summary>
        public readonly int[] Ordinal;
        /// <summary>GU-13: satisfaction, thousandths.</summary>
        public readonly short[] Sat;
        /// <summary>GU-13: the quality of the snow it has skied this lap, weighted by ticks, and the ticks.</summary>
        public readonly int[] LapQuality, LapTicks;
        /// <summary>GU-17: what this lap has met: bit 0 a misrated trail, bit 1 a Marginal one, bit 2 a crowded one.</summary>
        public readonly byte[] LapFlags;
        /// <summary>SI-08: a running hash of the day record, folded as each segment is written, so the state hash is cheap.</summary>
        public readonly ulong[] RecHash;

        // ---- the day record (GU-02), a ring of RecordSegments, packed to 6 bytes ----
        /// <summary>The segment, as an edge index, or a lift index with <see cref="LiftMark"/> set.</summary>
        public readonly ushort[] RecSeg;
        /// <summary>Ticks from the face day's start, so a day's record needs no more than a ushort.</summary>
        public readonly ushort[] RecEntry, RecExit;
        public readonly byte[] RecCount, RecHead;

        /// <summary>Set in a record entry's or a render record's segment to mean "a lift", not a trail segment.</summary>
        public const ushort LiftMark = GuestView.LiftMark;

        /// <summary>
        /// LO-13: the archetype of a scripted feeder's skier. It uses the real line, boarding and ride, so the lift code
        /// has one path, but it is in no ledger of the crowd and the guests row never moves it: it rides and vanishes.
        /// </summary>
        public const byte FeederArch = 255;

        // ---- the lists a slot belongs to ----
        /// <summary>LO-02: the next skier in the lift's line, or −1 (the line is a list over slots, so it has no fixed cap).</summary>
        public readonly int[] LineNext;
        /// <summary>LO-06: the next hidden rider of the lift, in order of stepping off, or −1.</summary>
        public readonly int[] RideNext;
        /// <summary>The next skier in this slot's wheel bucket, or −1.</summary>
        public readonly int[] WheelNext;
        /// <summary>
        /// 1 while the slot sits in a wheel bucket. A slot is in **at most one** bucket, ever: scheduling it again takes
        /// it out of the one it is in first, and freeing it takes it out too. Without that a slot could end up in two
        /// chains, and the two would splice into a loop the moment one of them was walked.
        /// </summary>
        public readonly byte[] InWheel;

        // ---- the free stack ----
        readonly int[] _free;
        int _freeCount;

        // ---- the timing wheel ----
        readonly int[] _bucketHead, _bucketTail;
        readonly int _mask;

        /// <summary>How many skiers are on the hill (every slot that isn't free).</summary>
        public int Count { get; private set; }

        public GuestColumns(Pacing p)
        {
            Cap = p.SkierCap;
            RecordSegments = p.RecordSegments;
            int n = Cap;
            Serial = new int[n]; Band = new byte[n]; Arch = new byte[n]; Ability = new short[n];
            Home = new int[n]; StayLaps = new byte[n]; ArriveTick = new long[n];
            PartyId = new int[n]; Lead = new int[n]; Member = new int[n]; PartySize = new byte[n]; PartyHere = new byte[n];
            State = new byte[n]; Homeward = new byte[n];
            Node = new int[n]; Edge = new int[n]; Lift = new int[n];
            EntryTick = new long[n]; ExitTick = new long[n]; DueTick = new long[n]; RideExitRunMs = new long[n];
            Lane = new byte[n]; Speed = new short[n];
            Laps = new byte[n]; WaitTicks = new int[n]; JoinTick = new long[n]; Ordinal = new int[n];
            Sat = new short[n]; LapQuality = new int[n]; LapTicks = new int[n]; LapFlags = new byte[n]; RecHash = new ulong[n];
            RecSeg = new ushort[n * RecordSegments]; RecEntry = new ushort[n * RecordSegments]; RecExit = new ushort[n * RecordSegments];
            RecCount = new byte[n]; RecHead = new byte[n];
            LineNext = new int[n]; RideNext = new int[n]; WheelNext = new int[n]; InWheel = new byte[n];
            _free = new int[n];
            _bucketHead = new int[p.WheelBuckets]; _bucketTail = new int[p.WheelBuckets];
            _mask = p.WheelBuckets - 1;
            Reset();
        }

        /// <summary>Empties the hill: every slot free, in slot order, and every bucket empty.</summary>
        public void Reset()
        {
            _freeCount = Cap;
            // The stack pops the lowest slot first, so a fresh day fills slots in order.
            for (int i = 0; i < Cap; i++) _free[i] = Cap - 1 - i;
            Array.Clear(State, 0, Cap);
            Array.Clear(InWheel, 0, Cap);
            // Every chain pointer is "none", so a slot a save doesn't mention reads back exactly as a fresh one.
            for (int i = 0; i < Cap; i++) { LineNext[i] = -1; RideNext[i] = -1; WheelNext[i] = -1; }
            for (int i = 0; i < _bucketHead.Length; i++) { _bucketHead[i] = -1; _bucketTail[i] = -1; }
            Count = 0;
        }

        // ================= slots =================

        /// <summary>A free slot, cleared, or −1 when the hill is at its cap (GU-03's Cap line).</summary>
        public int Take()
        {
            if (_freeCount == 0) return -1;
            int s = _free[--_freeCount];
            Serial[s] = 0; Band[s] = 0; Arch[s] = 0; Ability[s] = 0; Home[s] = -1; StayLaps[s] = 0;
            PartyId[s] = 0; Lead[s] = s; Member[s] = -1; PartySize[s] = 1; PartyHere[s] = 0;
            State[s] = (byte)GuestState.Arriving; Homeward[s] = 0;
            Node[s] = -1; Edge[s] = -1; Lift[s] = -1;
            EntryTick[s] = 0; ExitTick[s] = 0; DueTick[s] = 0; RideExitRunMs[s] = 0;
            Lane[s] = 0; Speed[s] = 1000;
            Laps[s] = 0; WaitTicks[s] = 0; JoinTick[s] = 0; Ordinal[s] = 0;
            Sat[s] = 0; LapQuality[s] = 0; LapTicks[s] = 0; LapFlags[s] = 0; RecHash[s] = 0;
            RecCount[s] = 0; RecHead[s] = 0;
            LineNext[s] = -1; RideNext[s] = -1; WheelNext[s] = -1; InWheel[s] = 0;
            Count++;
            return s;
        }

        /// <summary>
        /// Gives a slot back, taking it out of the wheel on the way. <see cref="WheelNext"/> is deliberately left as it
        /// is: a bucket that is being walked right now may still be pointing through this slot, and truncating the chain
        /// would lose every skier behind it.
        /// </summary>
        public void Give(int s)
        {
            if (State[s] == (byte)GuestState.Free) return;
            Unschedule(s);
            State[s] = (byte)GuestState.Free;
            Edge[s] = -1; Lift[s] = -1; Node[s] = -1;
            LineNext[s] = -1; RideNext[s] = -1;
            _free[_freeCount++] = s;
            Count--;
        }

        public bool Alive(int s) => s >= 0 && s < Cap && State[s] != (byte)GuestState.Free;
        public int FreeCount => _freeCount;

        // ================= the wheel =================

        /// <summary>
        /// Puts a skier in the bucket of the tick it is next due, **in order of (due tick, slot)**.
        ///
        /// The order matters and the sort is what makes it safe: a skier due further ahead than the wheel is long shares
        /// a bucket with a nearer tick and is put back when that bucket fires, so a plain append would let the number of
        /// buckets decide who reached a trail's gate first — the wheel would be a rule rather than an index. Sorted,
        /// a tick's skiers always come out in the same order, whatever size the wheel is (SI-08). A bucket holds about
        /// (skiers ÷ buckets) entries, so the walk is a handful of steps.
        /// </summary>
        public void Schedule(int s, long dueTick)
        {
            if (InWheel[s] != 0) Unschedule(s);
            DueTick[s] = dueTick;
            int b = (int)(dueTick & _mask);
            int prev = -1;
            for (int k = _bucketHead[b]; k >= 0; k = WheelNext[k])
            {
                if (DueTick[k] > dueTick || (DueTick[k] == dueTick && k > s)) break;
                prev = k;
            }
            if (prev < 0) { WheelNext[s] = _bucketHead[b]; _bucketHead[b] = s; }
            else { WheelNext[s] = WheelNext[prev]; WheelNext[prev] = s; }
            if (WheelNext[s] < 0) _bucketTail[b] = s;
            InWheel[s] = 1;
        }

        /// <summary>Takes a skier out of the bucket it sits in. A bucket holds few skiers, so the walk is short.</summary>
        public void Unschedule(int s)
        {
            if (InWheel[s] == 0) return;
            InWheel[s] = 0;
            int b = (int)(DueTick[s] & _mask);
            int prev = -1;
            for (int k = _bucketHead[b]; k >= 0; k = WheelNext[k])
            {
                if (k == s)
                {
                    int next = WheelNext[s];
                    if (prev < 0) _bucketHead[b] = next; else WheelNext[prev] = next;
                    if (_bucketTail[b] == s) _bucketTail[b] = prev;
                    return;
                }
                prev = k;
            }
        }

        /// <summary>
        /// Takes the whole bucket for <paramref name="tick"/>, leaving it empty, and returns its first skier (−1 when none).
        /// The caller walks it with <see cref="WheelNext"/>; skiers whose due tick is still ahead are re-scheduled, which
        /// is how a skier on a segment longer than the wheel waits without the wheel growing.
        /// </summary>
        public int TakeDue(long tick)
        {
            int b = (int)(tick & _mask);
            int first = _bucketHead[b];
            _bucketHead[b] = -1; _bucketTail[b] = -1;
            return first;
        }

        // ================= the day record (GU-02) =================

        /// <summary>
        /// Writes a finished segment into the skier's record and folds it into the running hash. Ticks are offsets from the
        /// face day's start, which a ushort holds (a face day is about 8,900 ticks).
        /// </summary>
        public void Record(int s, ushort segment, long entryTick, long exitTick, long dayStartTick)
        {
            int at = s * RecordSegments + (RecHead[s] + RecCount[s]) % RecordSegments;
            ushort a = Offset(entryTick, dayStartTick), b = Offset(exitTick, dayStartTick);
            RecSeg[at] = segment; RecEntry[at] = a; RecExit[at] = b;
            if (RecCount[s] < RecordSegments) RecCount[s]++;
            else RecHead[s] = (byte)((RecHead[s] + 1) % RecordSegments);
            // SI-08: one fold per segment, so hashing the state never walks the records.
            ulong h = RecHash[s];
            h = Fold(h, segment); h = Fold(h, a); h = Fold(h, b);
            RecHash[s] = h;
        }

        static ushort Offset(long tick, long dayStartTick)
        {
            long d = tick - dayStartTick;
            return d <= 0 ? (ushort)0 : d >= ushort.MaxValue ? ushort.MaxValue : (ushort)d;
        }

        static ulong Fold(ulong h, ulong v)
        {
            if (h == 0) h = 14695981039346656037UL;
            h ^= v;
            return h * 1099511628211UL;
        }

        /// <summary>The <paramref name="k"/>th entry of a skier's record, oldest first.</summary>
        public void ReadRecord(int s, int k, out ushort segment, out ushort entry, out ushort exit)
        {
            int at = s * RecordSegments + (RecHead[s] + k) % RecordSegments;
            segment = RecSeg[at]; entry = RecEntry[at]; exit = RecExit[at];
        }

        // ================= saving (SI-12) =================

        /// <summary>The wheel's buckets, for the sidecar: the restore puts every skier back in the bucket it was in.</summary>
        public int BucketCount => _bucketHead.Length;
        public int BucketHead(int b) => _bucketHead[b];
        public int BucketTail(int b) => _bucketTail[b];
        public void SetBucket(int b, int head, int tail) { _bucketHead[b] = head; _bucketTail[b] = tail; }
        public int FreeAt(int k) => _free[k];
        public void SetFree(int count) { _freeCount = count; Count = Cap - count; }
        public void SetFreeAt(int k, int slot) { _free[k] = slot; }
    }
}
