using System;
using System.Threading;
using MountainPlanner.Domain.Snow;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.08 PR A: the fine trail snow's staging ring, the twin of <see cref="TileStagingRing"/>. The worker copies a due
    /// 64 m tile of the fine field (<see cref="FineTiles"/>) into the next free slot: per fine cell, the depth in whole mm and
    /// four state bytes (<see cref="Pack"/>); cells with no block are all zero. The snapshot says how many slots are written
    /// (<see cref="SimSnapshot.FineStagedTiles"/>); the main thread copies each into the screen's atlas and hands it back.
    /// A slot also carries the layout's hash, so the screen drops a tile staged for a layout it no longer draws.
    /// Allocated once: 32 slots of up to 64 × 64 fine cells (24 KB each).
    /// </summary>
    public sealed class FineStagingRing
    {
        public const int DefaultSlots = 32;
        /// <summary>The largest tile's cells (64 m at 1 m).</summary>
        public const int SlotCells = 64 * 64;

        // State byte 0: the type (SN-13, bits 0–3), the cell has a block (bit 7), a groomed finish (bit 6: groomed since the
        // last powder reset), a green push (bit 5, SN-27 v2). Byte 1: the moguls (0–255). Byte 2: the passes since the last
        // grooming or powder reset, ÷ 4 (to 255). Byte 3: the age, face hours.
        public const int TypeMask = 15, Owned = 128, Groomed = 64, Green = 32;

        readonly ushort[] _depth;
        readonly byte[] _state;
        readonly int[] _tile, _size;
        readonly ulong[] _layout;
        public readonly int Slots;
        long _written, _released;

        public FineStagingRing(int slots = DefaultSlots)
        {
            if (slots < 1) throw new ArgumentOutOfRangeException(nameof(slots));
            Slots = slots;
            _depth = new ushort[slots * SlotCells];
            _state = new byte[slots * SlotCells * 4];
            _tile = new int[slots]; _size = new int[slots]; _layout = new ulong[slots];
        }

        public long Written => Volatile.Read(ref _written);
        public long Released => Volatile.Read(ref _released);

        /// <summary>A fine cell's four state bytes, as staged.</summary>
        internal static void Pack(FineSnow fine, int f, byte[] into, int at)
        {
            if (fine.Swe[f] <= 0) { into[at] = Owned; into[at + 1] = 0; into[at + 2] = 0; into[at + 3] = 0; return; }
            int flags = fine.Flags[f];
            int b0 = (fine.Type[f] & TypeMask) | Owned;
            if ((flags & FineSnow.GroomMask) != 0) b0 |= Groomed;
            if ((flags & FineSnow.PushedGreen) != 0) b0 |= Green;
            into[at] = (byte)b0;
            into[at + 1] = fine.Moguls[f];
            into[at + 2] = (byte)Math.Min(255, fine.Passes[f] / 4);
            into[at + 3] = fine.Age[f];
        }

        /// <summary>A fine cell's depth in whole mm (to 65.535 m).</summary>
        internal static ushort DepthMm(FineSnow fine, int f)
        {
            if (fine.Swe[f] <= 0) return 0;
            long mm = (fine.DepthUm(f) + 500) / 1000;
            return mm >= ushort.MaxValue ? ushort.MaxValue : (ushort)mm;
        }

        /// <summary>The worker: listed tile <paramref name="listed"/> of <paramref name="tiles"/> into the next slot, or false when
        /// every slot is still the reader's.</summary>
        internal bool TryWrite(FineTiles tiles, FineSnow fine, int listed)
        {
            long n = _written;
            if (n - Volatile.Read(ref _released) >= Slots) return false;
            int s = (int)(n % Slots), size = tiles.TileFine, at = s * SlotCells;
            Array.Clear(_depth, at, size * size);
            Array.Clear(_state, at * 4, size * size * 4);
            Span<int> blocks = stackalloc int[64], fx = stackalloc int[64], fy = stackalloc int[64];
            int count = tiles.BlocksOf(listed, blocks, fx, fy);
            int per = fine.Layout.PerSide, bc = per * per;
            for (int k = 0; k < count; k++)
            {
                int f0 = blocks[k] * bc;
                for (int j = 0; j < per; j++)
                    for (int i = 0; i < per; i++)
                    {
                        int f = f0 + j * per + i, c = at + (fy[k] + j) * size + fx[k] + i;
                        _depth[c] = DepthMm(fine, f);
                        Pack(fine, f, _state, c * 4);
                    }
            }
            _tile[s] = listed; _size[s] = size; _layout[s] = tiles.Layout.Hash();
            Volatile.Write(ref _written, n + 1);
            return true;
        }

        /// <summary>The reader: the slot holding write number <paramref name="n"/>: its listed tile, its side in fine cells, the
        /// layout it was staged for, and where its depths (one a cell, rows of <paramref name="size"/>) and state bytes (four a
        /// cell, from <paramref name="offset"/> × 4) start.</summary>
        public void Read(long n, out int listed, out int size, out ulong layout, out int offset, out ushort[] depth, out byte[] state)
        {
            int s = (int)(n % Slots);
            listed = _tile[s]; size = _size[s]; layout = _layout[s];
            offset = s * SlotCells;
            depth = _depth; state = _state;
        }

        /// <summary>The reader hands back every slot before write number <paramref name="through"/>.</summary>
        public void Release(long through)
        {
            if (through > Volatile.Read(ref _written)) throw new ArgumentOutOfRangeException(nameof(through), "That slot isn't written yet.");
            if (through > _released) Volatile.Write(ref _released, through);
        }
    }
}
