using System;
using System.Text;

namespace MountainPlanner.Domain.Geo
{
    /// <summary>Spherical Web Mercator (EPSG:3857) and Bing-style quadkeys: the Meta/WRI canopy tiles' grid.</summary>
    public static class WebMercator
    {
        public const double HalfWorld = 20037508.342789244;
        const double Radius = 6378137.0;

        public static (double X, double Y) Forward(GeoPoint p) =>
            (p.Longitude * HalfWorld / 180.0, Math.Log(Math.Tan((90.0 + p.Latitude) * Math.PI / 360.0)) * Radius);

        public static GeoPoint Inverse(double x, double y) =>
            new GeoPoint(Math.Atan(Math.Sinh(y / Radius)) * 180.0 / Math.PI, x / HalfWorld * 180.0);

        /// <summary>The tile column and row holding a point at a zoom level (row 0 at the top).</summary>
        public static (int X, int Y) Tile(GeoPoint p, int zoom)
        {
            int n = 1 << zoom;
            double lat = p.Latitude * Math.PI / 180.0;
            return ((int)Math.Floor((p.Longitude + 180.0) / 360.0 * n),
                    (int)Math.Floor((1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * n));
        }

        public static string QuadKey(int tileX, int tileY, int zoom)
        {
            var sb = new StringBuilder(zoom);
            for (int i = zoom; i > 0; i--)
            {
                int mask = 1 << (i - 1);
                sb.Append((char)('0' + ((tileX & mask) != 0 ? 1 : 0) + ((tileY & mask) != 0 ? 2 : 0)));
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Maps the cell centres of an Albers grid into another coordinate system quickly: the exact
    /// transform on a lattice every <see cref="Step"/> cells, bilinear in between. Projections are
    /// smooth, so over 16 m the interpolation error is far below a millimetre, and a 25-million-cell
    /// core reprojects in well under a second instead of half a minute.
    /// </summary>
    public sealed class GridReprojector
    {
        public const int Step = 16;

        readonly GridSpec _grid;
        readonly int _latticeColumns;
        readonly double[] _x, _y;
        readonly int _row0, _rows;

        /// <param name="rowStart">First grid row covered (bands keep memory small).</param>
        public GridReprojector(GridSpec grid, int rowStart, int rowCount, Func<AlbersPoint, (double X, double Y)> transform)
        {
            _grid = grid;
            _row0 = rowStart;
            _rows = rowCount;
            _latticeColumns = (grid.Columns - 1) / Step + 2;
            int latticeRows = (rowCount - 1) / Step + 2;
            _x = new double[_latticeColumns * latticeRows];
            _y = new double[_latticeColumns * latticeRows];
            for (int lr = 0; lr < latticeRows; lr++)
                for (int lc = 0; lc < _latticeColumns; lc++)
                {
                    var p = new AlbersPoint(grid.West + (lc * Step + 0.5) * grid.CellSize, grid.North - (rowStart + lr * Step + 0.5) * grid.CellSize);
                    var (x, y) = transform(p);
                    _x[lr * _latticeColumns + lc] = x;
                    _y[lr * _latticeColumns + lc] = y;
                }
        }

        /// <summary>The transformed centre of grid cell (column, row); row is absolute in the grid.</summary>
        public (double X, double Y) At(int column, int row)
        {
            int r = row - _row0;
            if (r < 0 || r >= _rows) throw new ArgumentOutOfRangeException(nameof(row));
            int lc = column / Step, lr = r / Step;
            double fx = (column - lc * Step) / (double)Step, fy = (r - lr * Step) / (double)Step;
            int i = lr * _latticeColumns + lc;
            double Lerp(double[] a) =>
                (a[i] * (1 - fx) + a[i + 1] * fx) * (1 - fy) + (a[i + _latticeColumns] * (1 - fx) + a[i + _latticeColumns + 1] * fx) * fy;
            return (Lerp(_x), Lerp(_y));
        }
    }
}
