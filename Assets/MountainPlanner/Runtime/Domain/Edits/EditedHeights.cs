using System;

namespace MountainPlanner.Domain.Edits
{
    /// <summary>Heights as one function of position (metres in the package grid, NAVD88): the package's, or edited ones.</summary>
    public interface IHeightSource
    {
        double At(double x, double y);
    }

    /// <summary>
    /// The 1 m corner lattice the edit tiles sit on (GR-09): point column c and row r lie at
    /// (<see cref="West"/> + c, <see cref="North"/> − r), the same points as the terrain cache's 1 m heightmaps.
    /// Cell (c, r) is the square east and south of point (c, r).
    /// </summary>
    public readonly struct EditLattice
    {
        public readonly double West;
        public readonly double North;

        public EditLattice(double west, double north)
        {
            West = west;
            North = north;
        }

        public double X(int column) => West + column;
        public double Y(int row) => North - row;

        /// <summary>The cell holding (x, y); on an edge, the cell east or south of it.</summary>
        public void CellAt(double x, double y, out int column, out int row)
        {
            column = (int)Math.Floor(x - West);
            row = (int)Math.Floor(North - y);
        }
    }

    /// <summary>
    /// The heights every rule reads (SH-02): the package's float32 heights plus the committed edits. At a lattice point
    /// it's the source there plus the point's change; between points the changes are bilinear, so where nothing changed
    /// it equals the source exactly. Thread-safe when the source is.
    /// </summary>
    public sealed class EditedHeights : IHeightSource
    {
        public IHeightSource Source { get; }
        public EditLattice Lattice { get; }
        public TerrainEdits Edits { get; }

        public EditedHeights(IHeightSource source, EditLattice lattice, TerrainEdits edits)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Lattice = lattice;
            Edits = edits ?? throw new ArgumentNullException(nameof(edits));
        }

        /// <summary>The edited height at a lattice point.</summary>
        public double AtPoint(int column, int row) => AtPost(Lattice.X(column), Lattice.Y(row), column, row);

        /// <summary>
        /// The edited height at lattice point (column, row), with the source read at (x, y): a caller that places the
        /// point its own way (the cache's tile West + i) gets exactly the cache's source value plus the change.
        /// </summary>
        public double AtPost(double x, double y, int column, int row) => Source.At(x, y) + Edits.DeltaMmAt(column, row) / 1000.0;

        public double At(double x, double y)
        {
            double h = Source.At(x, y);
            if (Edits.IsEmpty) return h;
            double fx = x - Lattice.West, fy = Lattice.North - y;
            int c = (int)Math.Floor(fx), r = (int)Math.Floor(fy);
            double tx = fx - c, ty = fy - r;
            int d00 = Edits.DeltaMmAt(c, r), d10 = Edits.DeltaMmAt(c + 1, r), d01 = Edits.DeltaMmAt(c, r + 1), d11 = Edits.DeltaMmAt(c + 1, r + 1);
            if ((d00 | d10 | d01 | d11) == 0) return h;
            double top = d00 + (d10 - d00) * tx, bottom = d01 + (d11 - d01) * tx;
            return h + (top + (bottom - top) * ty) / 1000.0;
        }

        /// <summary>Whether the cell under (x, y) is cleared (CL-05).</summary>
        public bool IsCleared(double x, double y)
        {
            Lattice.CellAt(x, y, out int c, out int r);
            return Edits.IsClearedAt(c, r);
        }
    }
}
