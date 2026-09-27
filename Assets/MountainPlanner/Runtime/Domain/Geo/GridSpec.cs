using System;
using System.Globalization;

namespace MountainPlanner.Domain.Geo
{
    /// <summary>
    /// A north-up raster on the Albers grid: row 0 is the northern edge, column 0 the western edge,
    /// matching GeoTIFF and S1M. Cells are square; cell (c, r) spans
    /// [West + c·Cell, West + (c+1)·Cell) × (North − (r+1)·Cell, North − r·Cell].
    /// </summary>
    public readonly struct GridSpec : IEquatable<GridSpec>
    {
        public readonly double West;
        public readonly double North;
        public readonly double CellSize;
        public readonly int Columns;
        public readonly int Rows;

        public GridSpec(double west, double north, double cellSize, int columns, int rows)
        {
            if (!(cellSize > 0)) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
            if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns), columns, "Columns must be positive.");
            if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows), rows, "Rows must be positive.");
            West = west;
            North = north;
            CellSize = cellSize;
            Columns = columns;
            Rows = rows;
        }

        /// <summary>The grid that exactly covers a box whose sides are whole multiples of the cell size.</summary>
        public static GridSpec Covering(AlbersBox box, double cellSize)
        {
            double cols = box.Width / cellSize, rows = box.Height / cellSize;
            int c = (int)Math.Round(cols), r = (int)Math.Round(rows);
            if (Math.Abs(cols - c) > 1e-6 || Math.Abs(rows - r) > 1e-6)
                throw new ArgumentException($"The box {box} is not a whole number of {cellSize} m cells.");
            return new GridSpec(box.West, box.North, cellSize, c, r);
        }

        public double East => West + Columns * CellSize;
        public double South => North - Rows * CellSize;
        public long CellCount => (long)Columns * Rows;
        public AlbersBox Bounds => new AlbersBox(West, South, East, North);

        /// <summary>The centre of cell (column, row).</summary>
        public AlbersPoint CellCentre(int column, int row) =>
            new AlbersPoint(West + (column + 0.5) * CellSize, North - (row + 0.5) * CellSize);

        /// <summary>The cell holding a point; false when the point is outside the grid.</summary>
        public bool TryCellAt(AlbersPoint p, out int column, out int row)
        {
            column = (int)Math.Floor((p.X - West) / CellSize);
            row = (int)Math.Floor((North - p.Y) / CellSize);
            return column >= 0 && column < Columns && row >= 0 && row < Rows;
        }

        /// <summary>Row-major index of cell (column, row), north row first.</summary>
        public long Index(int column, int row) => (long)row * Columns + column;

        /// <summary>True when both grids' cell edges line up, so data moves between them without resampling.</summary>
        public bool IsAlignedWith(GridSpec other)
        {
            if (Math.Abs(CellSize - other.CellSize) > 1e-9) return false;
            double dx = (West - other.West) / CellSize, dy = (North - other.North) / CellSize;
            return Math.Abs(dx - Math.Round(dx)) < 1e-6 && Math.Abs(dy - Math.Round(dy)) < 1e-6;
        }

        public bool Equals(GridSpec other) =>
            West.Equals(other.West) && North.Equals(other.North) && CellSize.Equals(other.CellSize) && Columns == other.Columns && Rows == other.Rows;
        public override bool Equals(object obj) => obj is GridSpec other && Equals(other);
        public override int GetHashCode() => (((West.GetHashCode() * 397 ^ North.GetHashCode()) * 397 ^ CellSize.GetHashCode()) * 397 ^ Columns) * 397 ^ Rows;
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "{0}×{1} @ {2} m from (W {3:F1}, N {4:F1})", Columns, Rows, CellSize, West, North);
    }
}
