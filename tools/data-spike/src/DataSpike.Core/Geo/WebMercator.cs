using System;
using System.Text;

namespace MountainPlanner.DataSpike.Geo
{
    /// <summary>Spherical Web Mercator (EPSG:3857) and Bing-style quadkeys, used by the Meta/WRI canopy tiles.</summary>
    public static class WebMercator
    {
        public const double HalfWorld = 20037508.342789244;

        public static void Forward(double latDeg, double lonDeg, out double x, out double y)
        {
            x = lonDeg * HalfWorld / 180.0;
            y = Math.Log(Math.Tan((90.0 + latDeg) * Math.PI / 360.0)) * 6378137.0;
        }

        public static void Inverse(double x, double y, out double latDeg, out double lonDeg)
        {
            lonDeg = x / HalfWorld * 180.0;
            latDeg = Math.Atan(Math.Sinh(y / 6378137.0)) * 180.0 / Math.PI;
        }

        /// <summary>Tile column/row at a zoom level (row 0 at the top).</summary>
        public static void Tile(double latDeg, double lonDeg, int zoom, out int tileX, out int tileY)
        {
            int n = 1 << zoom;
            tileX = (int)Math.Floor((lonDeg + 180.0) / 360.0 * n);
            double latRad = latDeg * Math.PI / 180.0;
            tileY = (int)Math.Floor((1 - Math.Log(Math.Tan(latRad) + 1 / Math.Cos(latRad)) / Math.PI) / 2 * n);
        }

        public static string QuadKey(int tileX, int tileY, int zoom)
        {
            var sb = new StringBuilder(zoom);
            for (int i = zoom; i > 0; i--)
            {
                int mask = 1 << (i - 1);
                int digit = ((tileX & mask) != 0 ? 1 : 0) + ((tileY & mask) != 0 ? 2 : 0);
                sb.Append((char)('0' + digit));
            }
            return sb.ToString();
        }
    }
}
