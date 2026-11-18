using System;
using MountainPlanner.Domain.Snow;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SN-03 v3's fine field (task 4.07 PR B1): the trail snow, one record per fine cell of a <see cref="TrailSnowLayout"/>,
    /// 16 B a cell. The water (µm), the density (1/64 kg/m³), the passes since the last grooming or powder reset, the
    /// moguls (0–255), the type (SN-13), the age (face hours), the flags (packed, groomings in a row, the wet from rain), 3
    /// bytes kept for 4.16's machine snow and one spare.
    /// <para>The 8 m cell above a block stays the physics' cell. The invariant: a block's fine water sums to
    /// <c>bc × Swe8 + r</c> with <c>0 ≤ r &lt; bc</c> (bc fine cells a block), so the 8 m pack is the roll-up
    /// <c>floor(sum ÷ bc)</c> and the remainder, under 1 µm a cell, stays in the fine cells. An hour's gains land on every
    /// fine cell alike; its losses are split by each cell's weight (SN-30 v2's packed factor), exactly.</para>
    /// Allocates only when made; every hour's call writes in place.
    /// </summary>
    internal sealed class FineSnow
    {
        /// <summary>Bytes held per fine cell.</summary>
        public const int BytesPerCell = 16;
        /// <summary>Density's fixed point: 64 per kg/m³ (917 kg/m³ of ice fits a ushort).</summary>
        public const int DensityOne = 64;
        /// <summary>A loss weight's unit (SN-30 v2's factor is 750–1000 of it).</summary>
        public const int WeightOne = 1000;

        // Flags: bit 0 packed (groomed or skied since the last powder reset); bits 1–3 groomings in a row (0–7); bit 4 the
        // wet came from rain.
        public const int Packed = 1, GroomShift = 1, GroomMask = 7 << GroomShift, RainWet = 1 << 4;

        public readonly TrailSnowLayout Layout;
        public readonly int[] Swe;
        public readonly ushort[] Density, Passes;
        public readonly byte[] Moguls, Type, Age, Flags;
        /// <summary>4.16's machine snow: 3 bytes a fine cell, kept zero until then.</summary>
        public readonly byte[] Machine;

        public FineSnow(TrailSnowLayout layout)
        {
            Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            int n = layout.FineCells;
            Swe = new int[n]; Density = new ushort[n]; Passes = new ushort[n];
            Moguls = new byte[n]; Type = new byte[n]; Age = new byte[n]; Flags = new byte[n];
            Machine = new byte[n * 3];
        }

        public int BlockCells => Layout.BlockCells;
        public int Cells => Swe.Length;

        /// <summary>The block's water, µm summed over its fine cells.</summary>
        public long BlockSum(int block)
        {
            long s = 0;
            int f0 = block * BlockCells, f1 = f0 + BlockCells;
            for (int f = f0; f < f1; f++) s += Swe[f];
            return s;
        }

        /// <summary>A fine cell's depth, µm (its water over its density).</summary>
        public long DepthUm(int f) => Density[f] == 0 ? 0 : (long)Swe[f] * 1000 * DensityOne / Density[f];

        /// <summary>
        /// SN-28: every block seeded from the 8 m snow. A fine cell's water is the bilinear blend of the four 8 m cells
        /// round its centre (edges clamp), then the block is scaled to hold exactly <c>bc ×</c> its own cell's water (the
        /// leftover µm one each, in index order). The density, type and age come from the block's cell.
        /// </summary>
        public void Seed(SnowField field)
        {
            if (field.Width != Layout.GridWidth || field.Height != Layout.GridHeight) throw new ArgumentException("The grids differ.");
            int bc = BlockCells, per = Layout.PerSide;
            for (int b = 0; b < Layout.Blocks; b++)
            {
                int cell = Layout.BlockCell[b], ci = cell % field.Width, cj = cell / field.Width;
                long want = (long)bc * field.Pack[cell].Swe;
                int f0 = b * bc;
                long sum = 0;
                for (int k = 0; k < bc; k++)
                {
                    // The fine centre in the 8 m cells' frame, in 1/(2·per) of a cell: (2·fi + 1) of them from the cell's west edge.
                    int u = 2 * (k % per) + 1 - per, v = 2 * (k / per) + 1 - per;   // −per..per from the cell's centre
                    int di = u < 0 ? -1 : 1, dj = v < 0 ? -1 : 1;
                    long wu = Math.Abs(u), wv = Math.Abs(v), den = 2L * per;        // weights toward the neighbour, of den
                    long s00 = SweAt(field, ci, cj), s10 = SweAt(field, ci + di, cj), s01 = SweAt(field, ci, cj + dj), s11 = SweAt(field, ci + di, cj + dj);
                    long blend = ((den - wu) * (den - wv) * s00 + wu * (den - wv) * s10 + (den - wu) * wv * s01 + wu * wv * s11) / (den * den);
                    Swe[f0 + k] = (int)blend;
                    sum += blend;
                }
                // Scale the block to exactly bc × the cell's water.
                if (sum > 0 && sum != want)
                {
                    long got = 0;
                    for (int k = 0; k < bc; k++) { long s = Swe[f0 + k] * want / sum; Swe[f0 + k] = (int)s; got += s; }
                    for (int k = 0; got < want; k = (k + 1) % bc) { Swe[f0 + k]++; got++; }
                }
                else if (sum == 0)
                {
                    long each = want / bc, left = want - each * bc;
                    for (int k = 0; k < bc; k++) Swe[f0 + k] = (int)(each + (k < left ? 1 : 0));
                }
                ref var p = ref field.Pack[cell];
                ushort d = DensityOf(p.Density);
                for (int k = 0; k < bc; k++)
                {
                    int f = f0 + k;
                    Density[f] = Swe[f] > 0 ? d : (ushort)0;
                    Type[f] = Swe[f] > 0 ? field.Type[cell] : (byte)0;
                    Age[f] = field.Age[cell];
                    Flags[f] = (byte)((field.Flags[cell] & SnowField.RainWet) != 0 ? RainWet : 0);
                    Passes[f] = 0; Moguls[f] = 0;
                }
            }
        }

        static long SweAt(SnowField field, int i, int j)
        {
            i = Math.Max(0, Math.Min(field.Width - 1, i));
            j = Math.Max(0, Math.Min(field.Height - 1, j));
            return field.Pack[j * field.Width + i].Swe;
        }

        /// <summary>The pack's density (kg/m³ × 1000) in this field's fixed point.</summary>
        public static ushort DensityOf(int packDensity)
            => (ushort)Math.Max(0, Math.Min(ushort.MaxValue, (long)packDensity * DensityOne / 1000));

        /// <summary>
        /// SN-28: a block folded back into its 8 m cell (its trail removed). The cell keeps the roll-up, so the water lost
        /// is the remainder <c>r</c>, returned in fine µm (under one 8 m µm a cell): the ledger's rounding term.
        /// </summary>
        public long Fold(int block, SnowField field)
        {
            int cell = Layout.BlockCell[block];
            long sum = BlockSum(block), bc = BlockCells;
            field.Pack[cell].Swe = (int)(sum / bc);
            return sum - sum / bc * bc;
        }

        /// <summary>
        /// The 8 m cell's hour shared with its block: every fine cell gains <paramref name="gainUm"/> of new snow at
        /// <paramref name="newDensity"/> (kg/m³ × 1000; mixed by volume), and the block loses <c>bc ×</c>
        /// <paramref name="lossUm"/>, split by each cell's weight (<paramref name="weights"/> from <paramref name="w0"/>, one per cell of the block,
        /// <see cref="WeightOne"/> for unpacked snow), floored, the leftover one µm each in index order, and a cell short of
        /// its share passing the rest on in index order. Exact: the block's sum moves by <c>bc × (gain − loss)</c>.
        /// Returns false (and changes nothing) when the block can't cover the loss.
        /// </summary>
        public bool ShareHour(int block, int gainUm, int newDensity, int lossUm, int[] weights, int w0 = 0)
        {
            int bc = BlockCells, f0 = block * bc;
            if (gainUm < 0 || lossUm < 0 || weights == null || weights.Length < w0 + bc) throw new ArgumentException("A negative hour or too few weights.");
            long total = (long)bc * lossUm;
            if (total > BlockSum(block) + (long)bc * gainUm) return false;
            if (gainUm > 0)
            {
                ushort dn = DensityOf(newDensity);
                for (int f = f0; f < f0 + bc; f++)
                {
                    long depth = DepthUm(f) + (dn == 0 ? 0 : (long)gainUm * 1000 * DensityOne / dn);
                    Swe[f] += gainUm;
                    Density[f] = depth <= 0 ? dn : (ushort)Math.Min(ushort.MaxValue, (long)Swe[f] * 1000 * DensityOne / depth);
                }
            }
            if (total == 0) return true;
            long K = 0;
            for (int k = 0; k < bc; k++) K += Math.Max(0, weights[w0 + k]);
            long taken = 0;
            if (K > 0)
            {
                for (int k = 0; k < bc; k++)
                {
                    long share = total * Math.Max(0, weights[w0 + k]) / K;
                    int f = f0 + k;
                    long take = Math.Min(share, Swe[f]);
                    Swe[f] -= (int)take; taken += take;
                }
            }
            // The floors' leftover (under one µm a weighted cell): a µm each to weighted cells in index order. Then what a
            // dry cell couldn't give, from any cell in index order.
            long left = total - taken;
            for (int k = 0; k < bc && left > 0; k++)
                if (weights[w0 + k] > 0 && Swe[f0 + k] > 0) { Swe[f0 + k]--; left--; }
            for (int k = 0; k < bc && left > 0; k++)
            {
                int f = f0 + k;
                long take = Math.Min(left, Swe[f]);
                Swe[f] -= (int)take; left -= take;
            }
            for (int f = f0; f < f0 + bc; f++) if (Swe[f] == 0) { Density[f] = 0; Type[f] = 0; Flags[f] = 0; Passes[f] = 0; Moguls[f] = 0; }
            return true;
        }

        /// <summary>SN-30 v2's loss weight of a fine cell: <see cref="WeightOne"/>, or the packed factor on a packed cell
        /// (1 at ≤ 200 kg/m³ to <paramref name="packedFloor"/> at ≥ 400).</summary>
        public int Weight(int f, int packedFloor)
        {
            if ((Flags[f] & Packed) == 0) return WeightOne;
            long rho = Density[f] / DensityOne;   // kg/m³
            if (rho <= 200) return WeightOne;
            if (rho >= 400) return packedFloor;
            return (int)(WeightOne - (WeightOne - packedFloor) * (rho - 200) / 200);
        }

        /// <summary>
        /// Settling: unpacked fine cells take the 8 m cell's density ratio for the hour (per mille); packed ones only when
        /// it raises them. Never past ice.
        /// </summary>
        public void Settle(int block, int ratioPermille)
        {
            int bc = BlockCells, f0 = block * bc;
            const int Ice = 917 * DensityOne;
            for (int f = f0; f < f0 + bc; f++)
            {
                if (Swe[f] == 0) continue;
                if ((Flags[f] & Packed) != 0 && ratioPermille < 1000) continue;
                long d = (long)Density[f] * ratioPermille / 1000;
                Density[f] = (ushort)Math.Max(1, Math.Min(Ice, d));
            }
        }

        /// <summary>
        /// The block's roll-up for its 8 m cell: the water <c>floor(sum ÷ bc)</c>, the density (kg/m³ × 1000) that gives the
        /// fine cells' mean depth, and the most common type among cells with snow (ties to the lower type).
        /// </summary>
        public void RollUp(int block, out int swe, out int density, out byte type)
        {
            int bc = BlockCells, f0 = block * bc;
            long sum = 0, depth = 0;
            for (int f = f0; f < f0 + bc; f++) { sum += Swe[f]; depth += DepthUm(f); }
            swe = (int)(sum / bc);
            density = depth == 0 ? 0 : (int)Math.Min(int.MaxValue, sum * 1000 * 1000 / depth);
            int best = -1, bestCount = 0;
            for (int t = 0; t < 16; t++)
            {
                int n = 0;
                for (int f = f0; f < f0 + bc; f++) if (Swe[f] > 0 && Type[f] == t) n++;
                if (n > bestCount) { best = t; bestCount = n; }
            }
            type = (byte)Math.Max(0, best);
        }
    }
}
