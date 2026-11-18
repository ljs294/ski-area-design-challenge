using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The lattice points a pond or reservoir holds water over at full pool (task 4.14, PD-09): a box of 1 m lattice points
    /// and a bit for each. Derived from the graded ground, never saved. The water mesh and the water-body table read it.
    /// </summary>
    public sealed class WaterMask
    {
        public PointRect Box { get; }
        public int Count { get; private set; }
        readonly ulong[] _bits;
        readonly int _width;

        public WaterMask(PointRect box)
        {
            Box = box;
            _width = box.IsEmpty ? 0 : box.MaxColumn - box.MinColumn + 1;
            long n = box.IsEmpty ? 0 : (long)_width * (box.MaxRow - box.MinRow + 1);
            _bits = new ulong[(n + 63) / 64];
        }

        public void Set(int column, int row)
        {
            if (!Box.Contains(column, row)) throw new ArgumentOutOfRangeException(nameof(column));
            long at = (long)(row - Box.MinRow) * _width + (column - Box.MinColumn);
            ulong bit = 1UL << (int)(at & 63);
            if ((_bits[at >> 6] & bit) != 0) return;
            _bits[at >> 6] |= bit;
            Count++;
        }

        public bool Has(int column, int row)
        {
            if (!Box.Contains(column, row)) return false;
            long at = (long)(row - Box.MinRow) * _width + (column - Box.MinColumn);
            return (_bits[at >> 6] & (1UL << (int)(at & 63))) != 0;
        }

        /// <summary>Every wet point, row by row (stable order).</summary>
        public IEnumerable<(int Column, int Row)> Points()
        {
            if (Box.IsEmpty) yield break;
            for (int r = Box.MinRow; r <= Box.MaxRow; r++)
                for (int c = Box.MinColumn; c <= Box.MaxColumn; c++)
                    if (Has(c, r)) yield return (c, r);
        }

        /// <summary>The tight box round the wet points (empty when none).</summary>
        public PointRect Tight()
        {
            var t = PointRect.Empty;
            foreach (var (c, r) in Points()) t = t.Include(c, r);
            return t;
        }
    }

    /// <summary>The ground numbers a pond or dam's tool and card show (PD-04, PD-06, PD-09), all metric; the UI formats.</summary>
    public abstract class WaterBuild
    {
        /// <summary>Its writes over the ground the fold handed it (fill and cut, absolute deltas over the source).</summary>
        public EarthworkWrites Writes { get; internal set; }
        public double CutCubicMetres { get; internal set; }
        public double FillCubicMetres { get; internal set; }
        /// <summary>PD-09: Σ (full pool − graded height) × 1 m² over the wet points.</summary>
        public double VolumeCubicMetres { get; internal set; }
        /// <summary>The water's area at full pool, square metres (the wet points; a pond's ring area is <see cref="PondBuild.RingSquareMetres"/>).</summary>
        public double AreaSquareMetres { get; internal set; }
        public double MaxDepthMetres { get; internal set; }
        public double MeanDepthMetres => AreaSquareMetres > 0 ? VolumeCubicMetres / AreaSquareMetres : 0;
        public double FullPoolMetres { get; internal set; }
        public double CrestMetres { get; internal set; }
        public int ClearedCells { get; internal set; }
        public WaterMask Wet { get; internal set; }
        /// <summary>What stops it being built here (the berm too tall, water past the edge…), or null.</summary>
        public string Problem { get; internal set; }
        /// <summary>PD-11: an amber note that doesn't stop a Build (more earth moved than water held), or null.</summary>
        public string Warning { get; internal set; }
        /// <summary>The earth it moves, cubic metres: a pond's cut and fill, a dam's fill.</summary>
        public double EarthCubicMetres => CutCubicMetres + FillCubicMetres;

        internal static string Feet(double metres) => Math.Round(metres / 0.3048).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " ft";
    }
}
