using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.08 PR A: <see cref="SimCommandKind.PaintSnow"/>'s payload, a grid of new snow water (µm of SWE, a depth of
    /// water, so independent of the grid's cell) laid over the map: the style tile's and the checks' whales (snow-rendering
    /// design §7 S2). The presentation builds it (from <see cref="Domain.Snow.GunWhale"/>'s pile) and never touches it after
    /// sending. Only the fine trail snow takes it; cells off the trails are skipped.
    /// </summary>
    public sealed class SimSnowPaint
    {
        /// <summary>The grid's south-west corner (design mm) and its cell (mm).</summary>
        public readonly long WestMm, SouthMm;
        public readonly int CellMm, Width, Height;
        /// <summary>Width × Height water, µm, west to east then south to north.</summary>
        public readonly int[] WaterUm;
        /// <summary>The new snow's density, kg/m³.</summary>
        public readonly int DensityKg;

        public SimSnowPaint(long westMm, long southMm, int cellMm, int width, int height, int[] waterUm, int densityKg)
        {
            if (cellMm <= 0 || width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (waterUm == null || waterUm.Length != width * height) throw new ArgumentException("One water a cell.", nameof(waterUm));
            if (densityKg < 50 || densityKg > 917) throw new ArgumentOutOfRangeException(nameof(densityKg));
            WestMm = westMm; SouthMm = southMm; CellMm = cellMm; Width = width; Height = height; WaterUm = waterUm; DensityKg = densityKg;
        }

        /// <summary>The water at design mm x, y (the cell it falls in), or 0 off the grid.</summary>
        public int At(long x, long y)
        {
            long i = Math.DivRem(x - WestMm, CellMm, out long ri), j = Math.DivRem(y - SouthMm, CellMm, out long rj);
            if (ri < 0) i--;
            if (rj < 0) j--;
            return i < 0 || j < 0 || i >= Width || j >= Height ? 0 : WaterUm[j * Width + i];
        }
    }

    public sealed partial class SimWorld
    {
        /// <summary>Fine cells the last paint reached (tests, the check's log).</summary>
        internal int PaintedCells { get; private set; }

        /// <summary>
        /// SN-13 v2, SN-27 v2 (task 4.08): new machine made snow on the fine trail cells under the paint. Each cell takes the
        /// paint's water at its centre, mixed by volume at the paint's density, up to a pile of <see cref="Pacing.WhaleMaxMm"/>;
        /// it turns machine made, age 0, unpacked and ungroomed (a whale must cure before a push, SM-05 v2). The blocks roll up
        /// onto their 8 m cells (the water counts in the trails' term, as a negative take) and both go to the screen. Refused
        /// without fine trail snow, and when no cell takes any.
        /// </summary>
        bool ApplyPaintSnow(object payload)
        {
            if (!(payload is SimSnowPaint p) || _fine == null || _trailLayout == null) return false;
            LandSnowNow();
            JoinSnow();
            var L = _trailLayout;
            var fine = _fine;
            const int cellMm = Domain.Snow.SurfaceGrid.CellMetres * 1000;
            long east = p.WestMm + (long)p.CellMm * p.Width, north = p.SouthMm + (long)p.CellMm * p.Height;
            int i0 = (int)Math.Max(0, (p.WestMm - L.OriginXMm) / cellMm), i1 = (int)Math.Min(L.GridWidth - 1, (east - L.OriginXMm) / cellMm);
            int j0 = (int)Math.Max(0, (p.SouthMm - L.OriginYMm) / cellMm), j1 = (int)Math.Min(L.GridHeight - 1, (north - L.OriginYMm) / cellMm);
            if (i1 < i0 || j1 < j0) return false;
            int bc = fine.BlockCells, painted = 0;
            ushort dn = FineSnow.DensityOf(p.DensityKg * 1000);
            long capUm = (long)Pacing.WhaleMaxMm * 1000;
            Array.Clear(_blockTouched, 0, _blockTouched.Length);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int b = L.BlockOf(j * L.GridWidth + i);
                    if (b < 0) continue;
                    for (int f = b * bc; f < b * bc + bc; f++)
                    {
                        L.CentreMm(f, out long x, out long y);
                        long add = p.At(x, y);
                        if (add <= 0) continue;
                        long depth = fine.DepthUm(f), room = capUm - depth;
                        if (room <= 0) continue;
                        long addDepth = add * 1000 * FineSnow.DensityOne / dn;
                        if (addDepth > room) { add = room * dn / (1000L * FineSnow.DensityOne); addDepth = room; }
                        if (add <= 0) continue;
                        long swe = fine.Swe[f] + add;
                        if (swe > int.MaxValue) continue;
                        fine.Swe[f] = (int)swe;
                        fine.Density[f] = (ushort)Math.Max(1, Math.Min(917 * FineSnow.DensityOne, swe * 1000 * FineSnow.DensityOne / (depth + addDepth)));
                        fine.Type[f] = (byte)SnowType.MM;
                        fine.Age[f] = 0; fine.Passes[f] = 0; fine.Moguls[f] = 0;
                        fine.Flags[f] = (byte)(fine.Flags[f] & ~(FineSnow.Packed | FineSnow.GroomMask | FineSnow.PushedGreen | FineSnow.RainWet));
                        _blockTouched[b] = true;
                        painted++;
                    }
                }
            PaintedCells = painted;
            if (painted == 0) return false;
            RollUpPainted();
            Snow.Refresh(i0, j0, i1, j1);
            FineDue(i0, j0, i1, j1);
            return true;
        }

        /// <summary>The painted blocks onto their 8 m cells: the roll-up's water, density and type (a paint is new snow on top, so
        /// the block speaks for its cell even unpacked).</summary>
        void RollUpPainted()
        {
            var fine = _fine;
            var field = Snow.Field;
            for (int b = 0; b < _blockTouched.Length; b++)
            {
                if (!_blockTouched[b]) continue;
                int c = _trailLayout.BlockCell[b];
                fine.RollUp(b, out int swe, out int density, out byte type);
                long before = field.Pack[c].Swe;
                if (before <= 0) field.Seed(c, 1, Snow.Rules.SeedDensity);   // a bare cell gets a snowpack's temperatures first
                ref var pk = ref field.Pack[c];
                pk.Swe = swe;
                if (density > 0) pk.Density = density;
                pk.Albedo = Math.Max(pk.Albedo, Pacing.AlbedoFresh * 1000);   // new snow on top
                field.Type[c] = type;
                field.Age[c] = 0;
                _trailRemovedToday += before - pk.Swe;
                _trailRemovedTotal += before - pk.Swe;
            }
        }
    }
}
