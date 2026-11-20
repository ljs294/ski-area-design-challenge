using System;
using System.IO;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// The trail snow as the sidecar holds it (task 4.07 B1 part 2c): every fine cell's record (<see cref="FineSnow"/>), and
    /// which 8 m cell each block of them lies on, with no layout. A reopened game takes it back over the layout the game
    /// sends: a block on the same 8 m cell at the same fine size keeps its cells.
    /// </summary>
    internal sealed class FinePlanes
    {
        public int FineMetres, PerSide;
        /// <summary>The 8 m cell under each block, ascending.</summary>
        public int[] BlockCell;
        public int[] Swe;
        public ushort[] Density, Passes;
        public byte[] Moguls, Type, Age, Flags, Machine;

        public int BlockCells => PerSide * PerSide;
        public int Blocks => BlockCell.Length;
        public int Cells => Swe.Length;

        public FinePlanes(int fineMetres, int perSide, int[] blockCell)
        {
            FineMetres = fineMetres; PerSide = perSide; BlockCell = blockCell;
            int n = blockCell.Length * perSide * perSide;
            Swe = new int[n]; Density = new ushort[n]; Passes = new ushort[n];
            Moguls = new byte[n]; Type = new byte[n]; Age = new byte[n]; Flags = new byte[n];
            Machine = new byte[n * 3];
        }

        /// <summary>A copy of <paramref name="f"/>'s cells (the worker's part of a save: the encoding runs elsewhere).</summary>
        public static FinePlanes Of(FineSnow f)
        {
            var L = f.Layout;
            var p = new FinePlanes(L.FineMetres, L.PerSide, (int[])L.BlockCell.Clone());
            Array.Copy(f.Swe, p.Swe, p.Cells); Array.Copy(f.Density, p.Density, p.Cells); Array.Copy(f.Passes, p.Passes, p.Cells);
            Array.Copy(f.Moguls, p.Moguls, p.Cells); Array.Copy(f.Type, p.Type, p.Cells); Array.Copy(f.Age, p.Age, p.Cells);
            Array.Copy(f.Flags, p.Flags, p.Cells); Array.Copy(f.Machine, p.Machine, p.Machine.Length);
            return p;
        }

        /// <summary>The block on an 8 m cell, or −1.</summary>
        public int BlockOf(int cell)
        {
            int b = Array.BinarySearch(BlockCell, cell);
            return b >= 0 ? b : -1;
        }

        /// <summary>The block's water, µm summed over its fine cells.</summary>
        public long BlockSum(int block)
        {
            long s = 0;
            int f0 = block * BlockCells, f1 = f0 + BlockCells;
            for (int f = f0; f < f1; f++) s += Swe[f];
            return s;
        }

        /// <summary>SN-03 v3's invariant against the 8 m cell's water: <c>bc × swe8 ≤ sum &lt; bc × (swe8 + 1)</c>.</summary>
        public bool AddsUp(int block, long swe8)
        {
            long sum = BlockSum(block), bc = BlockCells;
            return sum >= bc * swe8 && sum < bc * (swe8 + 1);
        }
    }

    /// <summary>
    /// The sidecar's trail snow (task 4.07 B1 part 2c), a frozen format with the sidecar (0.3 §5). Deterministic and plain
    /// (no zip), so the v12 fixture compares byte for byte:
    /// <code>
    /// u8 1 (this codec)  i32 fine metres  i32 per side  i32 blocks
    /// the blocks' 8 m cells, ascending: each the step from the last (from −1) as a short number
    /// per block: 3 bytes of modes (2 bits a plane, plane 0 lowest), then each plane's data in plane order
    /// u64 a check over every value
    /// </code>
    /// The ten planes are the water, density, passes, moguls, type, age, flags and 4.16's three machine bytes. A plane's
    /// mode is the cheapest of: 0 every cell zero; 1 every cell one value (the value); 2 runs in cell order (the count, then
    /// each value and length); 3 cell by cell (the water as its difference from the median-edge guess from the cells to its
    /// west, south and south-west within the block; the others as the difference from the cell before). Numbers are the
    /// sidecar's short numbers (zigzag, 7 bits a byte). Ties go to the lower mode.
    /// </summary>
    internal static class FineSnowCodec
    {
        public const int Version = 1;
        const int Planes = 10;
        /// <summary>The most blocks a sidecar may hold (a 22 km site's every 8 m cell).</summary>
        const int MaxBlocks = 8_000_000;

        static long Get(FinePlanes p, int f, int plane)
        {
            switch (plane)
            {
                case 0: return p.Swe[f];
                case 1: return p.Density[f];
                case 2: return p.Passes[f];
                case 3: return p.Moguls[f];
                case 4: return p.Type[f];
                case 5: return p.Age[f];
                case 6: return p.Flags[f];
                default: return p.Machine[f * 3 + plane - 7];
            }
        }

        static void Set(FinePlanes p, int f, int plane, long v)
        {
            if (v < 0 || v > Max(plane)) throw new InvalidDataException($"A trail snow value is out of range (plane {plane}: {v}).");
            switch (plane)
            {
                case 0: p.Swe[f] = (int)v; break;
                case 1: p.Density[f] = (ushort)v; break;
                case 2: p.Passes[f] = (ushort)v; break;
                case 3: p.Moguls[f] = (byte)v; break;
                case 4: p.Type[f] = (byte)v; break;
                case 5: p.Age[f] = (byte)v; break;
                case 6: p.Flags[f] = (byte)v; break;
                default: p.Machine[f * 3 + plane - 7] = (byte)v; break;
            }
        }

        static long Max(int plane) =>
            plane == 0 ? int.MaxValue
            : plane == 1 || plane == 2 ? ushort.MaxValue
            : plane == 4 ? 15
            : plane == 6 ? 0x3F
            : byte.MaxValue;

        static int VarLen(long v)
        {
            ulong z = unchecked((ulong)((v << 1) ^ (v >> 63)));
            int n = 1;
            while (z >= 0x80) { z >>= 7; n++; }
            return n;
        }

        /// <summary>LOCO-I's median-edge guess of a block's cell <paramref name="k"/> from its west, south and south-west.</summary>
        static long Guess(FinePlanes p, int f0, int k, int per)
        {
            int x = k % per, y = k / per;
            long a = x > 0 ? p.Swe[f0 + k - 1] : 0, b = y > 0 ? p.Swe[f0 + k - per] : 0, c = x > 0 && y > 0 ? p.Swe[f0 + k - per - 1] : 0;
            if (x == 0) return b;
            if (y == 0) return a;
            long hi = Math.Max(a, b), lo = Math.Min(a, b);
            return c >= hi ? lo : c <= lo ? hi : a + b - c;
        }

        static long Residual(FinePlanes p, int plane, int f0, int k, int per) =>
            plane == 0 ? p.Swe[f0 + k] - Guess(p, f0, k, per)
            : Get(p, f0 + k, plane) - (k == 0 ? 0 : Get(p, f0 + k - 1, plane));

        public static void Write(SidecarWriter o, FinePlanes p)
        {
            o.U8(Version);
            o.I32(p.FineMetres); o.I32(p.PerSide); o.I32(p.Blocks);
            long prev = -1;
            foreach (int c in p.BlockCell) { o.Var(c - prev); prev = c; }
            int bc = p.BlockCells, per = p.PerSide;
            var modes = new int[Planes];
            for (int b = 0; b < p.Blocks; b++)
            {
                int f0 = b * bc;
                int packed = 0;
                for (int plane = 0; plane < Planes; plane++)
                {
                    modes[plane] = Choose(p, plane, f0, bc, per);
                    packed |= modes[plane] << (2 * plane);
                }
                o.U8(packed); o.U8(packed >> 8); o.U8(packed >> 16);
                for (int plane = 0; plane < Planes; plane++)
                {
                    switch (modes[plane])
                    {
                        case 1: o.Var(Get(p, f0, plane)); break;
                        case 2:
                        {
                            int runs = 0;
                            for (int k = 0; k < bc; k++) if (k == 0 || Get(p, f0 + k, plane) != Get(p, f0 + k - 1, plane)) runs++;
                            o.Var(runs);
                            for (int k = 0; k < bc;)
                            {
                                long v = Get(p, f0 + k, plane);
                                int j = k + 1;
                                while (j < bc && Get(p, f0 + j, plane) == v) j++;
                                o.Var(v); o.Var(j - k);
                                k = j;
                            }
                            break;
                        }
                        case 3:
                            for (int k = 0; k < bc; k++) o.Var(Residual(p, plane, f0, k, per));
                            break;
                    }
                }
            }
            o.U64(Check(p));
        }

        /// <summary>The cheapest mode for one plane of one block (ties to the lower).</summary>
        static int Choose(FinePlanes p, int plane, int f0, int bc, int per)
        {
            long first = Get(p, f0, plane);
            bool same = true;
            for (int k = 1; k < bc && same; k++) same = Get(p, f0 + k, plane) == first;
            if (same) return first == 0 ? 0 : 1;
            int runs = 0, runCost = 0, cellCost = 0;
            for (int k = 0; k < bc;)
            {
                long v = Get(p, f0 + k, plane);
                int j = k + 1;
                while (j < bc && Get(p, f0 + j, plane) == v) j++;
                runs++;
                runCost += VarLen(v) + VarLen(j - k);
                k = j;
            }
            runCost += VarLen(runs);
            for (int k = 0; k < bc; k++) cellCost += VarLen(Residual(p, plane, f0, k, per));
            return runCost <= cellCost ? 2 : 3;
        }

        /// <summary>
        /// Reads what <see cref="Write"/> wrote. Throws <see cref="InvalidDataException"/> on anything that doesn't add up
        /// (a cut-short block, bad runs, a value out of range, the cells out of order, the check).
        /// </summary>
        public static FinePlanes Read(SidecarReader r)
        {
            int version = r.U8();
            if (version != Version) throw new InvalidDataException($"The trail snow's codec {version} isn't one this build reads.");
            int metres = r.I32(), per = r.I32();
            if ((metres != 1 && metres != 2 && metres != 4) || per * metres != 8) throw new InvalidDataException("The trail snow's fine size is out of range.");
            int blocks = r.Count(MaxBlocks);
            var cells = new int[blocks];
            long prev = -1;
            for (int b = 0; b < blocks; b++)
            {
                long step = r.Var();
                if (step < 1 || prev + step > int.MaxValue) throw new InvalidDataException("The trail snow's blocks are out of order.");
                prev += step;
                cells[b] = (int)prev;
            }
            var p = new FinePlanes(metres, per, cells);
            int bc = p.BlockCells;
            for (int b = 0; b < blocks; b++)
            {
                int f0 = b * bc;
                int packed = r.U8() | r.U8() << 8 | r.U8() << 16;
                if (packed >> (2 * Planes) != 0) throw new InvalidDataException("A trail snow block's modes are out of range.");
                for (int plane = 0; plane < Planes; plane++)
                {
                    int mode = (packed >> (2 * plane)) & 3;
                    switch (mode)
                    {
                        case 0: for (int k = 0; k < bc; k++) Set(p, f0 + k, plane, 0); break;
                        case 1: { long v = r.Var(); for (int k = 0; k < bc; k++) Set(p, f0 + k, plane, v); break; }
                        case 2:
                        {
                            long runs = r.Var();
                            if (runs < 1 || runs > bc) throw new InvalidDataException("A trail snow block's runs are out of range.");
                            int k = 0;
                            for (long run = 0; run < runs; run++)
                            {
                                long v = r.Var(), len = r.Var();
                                if (len < 1 || k + len > bc) throw new InvalidDataException("A trail snow block's runs don't fill it.");
                                for (int j = 0; j < len; j++) Set(p, f0 + k + j, plane, v);
                                k += (int)len;
                            }
                            if (k != bc) throw new InvalidDataException("A trail snow block's runs don't fill it.");
                            break;
                        }
                        default:
                            for (int k = 0; k < bc; k++)
                            {
                                long d = r.Var();
                                long guess = plane == 0 ? Guess(p, f0, k, per) : k == 0 ? 0 : Get(p, f0 + k - 1, plane);
                                Set(p, f0 + k, plane, guess + d);
                            }
                            break;
                    }
                }
            }
            if (r.U64() != Check(p)) throw new InvalidDataException("The trail snow doesn't match its check.");
            return p;
        }

        /// <summary>FNV-style over every value of every plane, with the block cells and the size.</summary>
        static ulong Check(FinePlanes p)
        {
            ulong h = 14695981039346656037UL;
            unchecked
            {
                h = (h ^ (ulong)p.FineMetres) * 1099511628211UL;
                foreach (int c in p.BlockCell) h = (h ^ (ulong)c) * 1099511628211UL;
                for (int plane = 0; plane < Planes; plane++)
                    for (int f = 0; f < p.Cells; f++) h = (h ^ (ulong)Get(p, f, plane)) * 1099511628211UL;
            }
            return h;
        }
    }
}
