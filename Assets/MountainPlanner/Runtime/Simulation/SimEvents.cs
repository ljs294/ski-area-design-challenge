using System;
using System.Runtime.InteropServices;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SI-18: the event types. The numbers are stable forever (replays, saves and later consumers key on them): a type is
    /// never renumbered or reused, and a retired one stays here.
    /// </summary>
    public enum SimEventType : ushort
    {
        None = 0,
        /// <summary>A command was turned away. Value: the command kind; Detail: the <see cref="SimRefusal"/>.</summary>
        CommandRejected = 1,
        /// <summary>TM-11 v2: Day 1, the first open morning a lift serves a run. Value: the season number.</summary>
        SeasonOpened = 2,
        /// <summary>TM-11 v2: Closed (or October 1) ended the season. Value: the season number.</summary>
        SeasonEnded = 3,
        MountainOpened = 4,
        MountainClosed = 5,
        /// <summary>TM-15: the day report at closing. Value: the face day.</summary>
        DayReport = 6,
        /// <summary>A face day began at midnight. Value: the face day.</summary>
        FaceDayBegan = 7,
        /// <summary>A skip started. Value: the target face millisecond; Detail: the <see cref="SkipKind"/>.</summary>
        SkipStarted = 8,
        SkipFinished = 9,
        SkipCancelled = 10,
        /// <summary>SI-18: an alert asks the host to pause; the simulation never pauses itself.</summary>
        PauseRequested = 11,
        /// <summary>A lift's rope started. Entity: the lift's index in the snapshot.</summary>
        LiftStarted = 12,
        LiftStopped = 13,
        /// <summary>Q10 (owner, 2026-11-08): the resort's hours were cut to the day's daylight. Value: open × 100000 + stop seconds.</summary>
        HoursTrimmed = 14,
        /// <summary>SI-15: the season's seed was pinned. Value: the seed.</summary>
        SeasonSeedPinned = 15,
        /// <summary>SI-20: a new design revision arrived. Value: the revision.</summary>
        DesignApplied = 16,
        /// <summary>A label year began on October 1. Value: the new season number.</summary>
        SeasonNumberRolled = 17,
    }

    /// <summary>Why a command was turned away (an event's Detail). Stable numbers.</summary>
    public enum SimRefusal : ushort
    {
        None = 0,
        /// <summary>SI-03 v2: issued against a snapshot more than <see cref="Pacing.StaleCommandTicks"/> old.</summary>
        Stale = 1,
        /// <summary>TM-06 v2, TM-14: no skip of that kind here (the lifts are running, it's already morning, not before the season…).</summary>
        SkipNotAllowed = 2,
        /// <summary>The mountain can't open in summer.</summary>
        NotInSummer = 3,
        /// <summary>The command queue was full.</summary>
        QueueFull = 4,
        /// <summary>Already in that state.</summary>
        NoChange = 5,
        /// <summary>A command the worker doesn't know.</summary>
        Unknown = 6,
    }

    /// <summary>SI-18: one event, 24 bytes: the tick, a value, the entity and the type.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SimEvent
    {
        public long Tick;
        public long Value;
        /// <summary>The entity's index (a lift's in the snapshot), or −1.</summary>
        public int Entity;
        public SimEventType Type;
        public ushort Detail;
    }

    /// <summary>
    /// SI-18: a fixed ring of events, numbered from 0 for the whole run. A snapshot carries the newest
    /// <see cref="Pacing.SnapshotEvents"/> of them with the first one's number, so a reader that skips snapshots still reads
    /// every event in order from the number it last saw; one that falls further behind learns how many it missed
    /// (<see cref="EventReader"/>). Allocated once.
    /// </summary>
    public sealed class EventRing
    {
        readonly SimEvent[] _ring;
        readonly int _mask;

        public EventRing(int size)
        {
            if (size < 1 || (size & (size - 1)) != 0) throw new ArgumentException("The event ring's size must be a power of two.", nameof(size));
            _ring = new SimEvent[size];
            _mask = size - 1;
        }

        /// <summary>Every event ever appended: the next one's number.</summary>
        public long Count { get; private set; }
        public int Capacity => _ring.Length;

        public void Add(long tick, SimEventType type, int entity, long value, ushort detail = 0)
        {
            ref var e = ref _ring[Count & _mask];
            e.Tick = tick;
            e.Type = type;
            e.Entity = entity;
            e.Value = value;
            e.Detail = detail;
            Count++;
        }

        /// <summary>The event numbered <paramref name="seq"/>, if it's still in the ring.</summary>
        public bool TryGet(long seq, out SimEvent e)
        {
            if (seq < 0 || seq >= Count || seq < Count - _ring.Length) { e = default; return false; }
            e = _ring[seq & _mask];
            return true;
        }

        /// <summary>Copies the newest events, at most <c>dest.Length</c>, into <paramref name="dest"/>; returns the first one's number.</summary>
        public long CopyNewest(SimEvent[] dest, out int count)
        {
            long first = Math.Max(0, Math.Max(Count - dest.Length, Count - _ring.Length));
            count = (int)(Count - first);
            for (int i = 0; i < count; i++) dest[i] = _ring[(first + i) & _mask];
            return first;
        }

        internal void Clear() => Count = 0;
    }

    /// <summary>
    /// SI-18's reader side: remembers the last event number seen and reads the newer ones out of each snapshot it's given,
    /// in order. When a snapshot no longer holds the next one, <see cref="Missed"/> counts how many went by unread.
    /// </summary>
    public sealed class EventReader
    {
        long _next;

        /// <summary>Events that went by unread because the reader fell too far behind (an overflow).</summary>
        public long Missed { get; private set; }

        /// <summary>Calls <paramref name="each"/> for every event in <paramref name="s"/> the reader hasn't seen, oldest first.</summary>
        public int Read(SimSnapshot s, Action<SimEvent> each)
        {
            if (s == null) return 0;
            long first = s.FirstEventSeq, end = first + s.EventCount;
            if (_next < first) { Missed += first - _next; _next = first; }
            int n = 0;
            for (long q = _next; q < end; q++, n++) each?.Invoke(s.Events[(int)(q - first)]);
            if (end > _next) _next = end;
            return n;
        }

        /// <summary>The next event number this reader expects.</summary>
        public long Next => _next;
    }
}
