using System;
using System.Threading;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// GU-16 v2 (task 4.13): where every skier is, packed to 24 bytes, for the screen to draw. One entry per skier on the
    /// hill, in slot order.
    ///
    /// The segment is an **edge index** into the network the design brought (<see cref="SimNetwork"/>), or a lift index
    /// with <see cref="GuestColumns.LiftMark"/> set. What <see cref="EntryTick"/> and <see cref="ExitTick"/> hold depends
    /// on the state:
    /// - **skiing or walking:** the day-relative ticks it got on and gets off (add <see cref="SkierRenderBuffers.Frame.DayStartTick"/>
    ///   for the absolute tick), so the renderer interpolates along the segment's centreline itself;
    /// - **queued (LO-02):** entry is the join tick, exit is 0, and <see cref="Rank"/> is its place in the FIFO line (0 is
    ///   next to board); <see cref="Lane"/> is unused there, because the line's column is the rank modulo the seats;
    /// - **riding (LO-06):** the rope travel (ms of motion) at boarding and at release, cut to 32 bits; compare them
    ///   with the snapshot's <c>RunMs</c> by wrap-safe subtraction.
    /// <see cref="Slot"/> is the worker's skier slot, stable while the skier is on the hill, so a renderer can keep
    /// per-skier easing state by slot (the column itself is packed and its indices shift whenever someone leaves).
    /// <see cref="Serial"/> is the whole serial <c>InspectSkier</c> takes. A skier with neither an edge nor a lift (choosing,
    /// arriving) has <see cref="Segment"/> 0 and <see cref="Node"/> says where it is: read the state before the segment.
    /// </summary>
    public readonly struct SkierRender
    {
        public readonly uint Serial;
        public readonly ushort Segment;
        public readonly ushort Rank;
        public readonly ushort Slot;
        /// <summary>The network node the skier stands at or last left (<see cref="NoNode"/> for none): where a choosing
        /// skier waits, and where a walker across an Area (GU-09) set out from.</summary>
        public readonly ushort Node;
        public readonly uint EntryTick;
        public readonly uint ExitTick;
        public readonly byte Lane;
        public readonly byte State;
        public readonly byte Band;
        /// <summary>GU-13's satisfaction, 0–255 for 0–1 (the dots' Satisfaction colour mode).</summary>
        public readonly byte Satisfaction;

        public const ushort NoNode = 0xFFFF;

        public SkierRender(uint serial, ushort segment, ushort rank, ushort slot, ushort node, uint entryTick, uint exitTick, byte lane, byte state, byte band, byte satisfaction = 0)
        {
            Serial = serial; Segment = segment; Rank = rank; Slot = slot; Node = node; EntryTick = entryTick; ExitTick = exitTick;
            Lane = lane; State = state; Band = band; Satisfaction = satisfaction;
        }

        /// <summary>Whether <see cref="Segment"/> names a lift rather than a trail segment.</summary>
        public bool OnALift => (Segment & GuestColumns.LiftMark) != 0;
        /// <summary>The lift or edge index the segment names.</summary>
        public int Index => Segment & ~GuestColumns.LiftMark;
    }

    /// <summary>
    /// GU-16, BG-11: the render column, triple-buffered and handed over by a pointer swap once per real tick, *beside*
    /// the 64 KB snapshot rather than inside it. At 3,000 skiers the column is 72 KB and at 30,000 it is 720 KB, so SI-16
    /// could never have carried it; the snapshot carries this buffer's tick and count instead, and the renderer reads the
    /// array in place. Nothing here is written after it is published.
    /// </summary>
    public sealed class SkierRenderBuffers
    {
        /// <summary>One published column: the skiers, how many of them, and the tick they are described at.</summary>
        public sealed class Frame
        {
            public readonly SkierRender[] Skiers;
            public int Count;
            public long Tick;
            /// <summary>The design revision the segment indices belong to: a renderer holding an older one must wait.</summary>
            public long Revision;
            /// <summary>The absolute tick the day's records count from: a skier's entry and exit are relative to it.</summary>
            public long DayStartTick;
            /// <summary>SN-31 (view only): per network edge, the skiers on it now, its passes over the rolling face hour and today.</summary>
            public int[] EdgeOn = Array.Empty<int>(), EdgeLastHour = Array.Empty<int>(), EdgeToday = Array.Empty<int>();
            public int EdgeCount;
            public Frame(int cap) { Skiers = new SkierRender[cap]; }
        }

        readonly Frame[] _frames;
        int _back, _front = 2, _middle = 1;
        const int Fresh = 1 << 30;

        public SkierRenderBuffers(int cap)
        {
            cap = Math.Max(1, cap);
            _frames = new[] { new Frame(cap), new Frame(cap), new Frame(cap) };
        }

        /// <summary>The worker's frame to fill.</summary>
        public Frame Back => _frames[_back];

        public void Publish()
        {
            int previous = Interlocked.Exchange(ref _middle, _back | Fresh);
            _back = previous & ~Fresh;
        }

        /// <summary>The newest published column, or the one the reader already holds (null before the first).</summary>
        public Frame Latest
        {
            get
            {
                if ((Volatile.Read(ref _middle) & Fresh) != 0)
                {
                    int previous = Interlocked.Exchange(ref _middle, _front);
                    _front = previous & ~Fresh;
                }
                return _front >= 0 && _front < _frames.Length ? _frames[_front] : null;
            }
        }
    }
}
