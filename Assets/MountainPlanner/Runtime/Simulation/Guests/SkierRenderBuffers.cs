using System;
using System.Threading;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// GU-16: where every skier is, packed to 16 bytes, for 4.13 to draw. One entry per skier on the hill.
    ///
    /// The segment is an **edge index** into the network the design brought (<see cref="SimNetwork"/>), or a lift index
    /// with <see cref="GuestColumns.LiftMark"/> set; the ticks say when the skier got on and when it gets off, so the
    /// renderer interpolates along that segment's centreline itself and needs nothing from the worker in between. The
    /// same record carries a 3D skier later (LO-06), which is why the lane and the state are in it from the start.
    /// </summary>
    public readonly struct SkierRender
    {
        public readonly ushort Segment;
        public readonly ushort Serial16;
        public readonly uint EntryTick;
        public readonly uint ExitTick;
        public readonly byte Lane;
        public readonly byte State;
        public readonly byte Band;
        public readonly byte Rank;

        public SkierRender(ushort segment, ushort serial16, uint entryTick, uint exitTick, byte lane, byte state, byte band, byte rank)
        {
            Segment = segment; Serial16 = serial16; EntryTick = entryTick; ExitTick = exitTick;
            Lane = lane; State = state; Band = band; Rank = rank;
        }

        /// <summary>Whether <see cref="Segment"/> names a lift rather than a trail segment.</summary>
        public bool OnALift => (Segment & GuestColumns.LiftMark) != 0;
        /// <summary>The lift or edge index the segment names.</summary>
        public int Index => Segment & ~GuestColumns.LiftMark;
    }

    /// <summary>
    /// GU-16, BG-11: the render column, triple-buffered and handed over by a pointer swap once per real tick, *beside*
    /// the 64 KB snapshot rather than inside it. At 3,000 skiers the column is 48 KB and at 30,000 it is 480 KB, so SI-16
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
