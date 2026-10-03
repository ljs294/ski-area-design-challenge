using System;

namespace MountainPlanner.Domain.Geo
{
    /// <summary>
    /// The site picker's map maths (0.3 §6.1): Web Mercator world pixels at a fractional zoom (256 px
    /// tiles, the XYZ scheme USGS basemap tiles use), and the site square's outline drawn from its exact
    /// Albers edges. The square is defined in EPSG:6350 and only projected for display, so what the
    /// player sees is what downloads.
    /// </summary>
    public static class SlippyMap
    {
        public const int TileSize = 256;
        public const double MaxLatitude = 85.05112878;

        /// <summary>World size in pixels at a zoom level.</summary>
        public static double WorldPixels(double zoom) => TileSize * Math.Pow(2, zoom);

        /// <summary>A point's world pixel at a zoom (x east, y south, origin at 180°W, 85.05°N).</summary>
        public static (double X, double Y) ToPixels(GeoPoint p, double zoom)
        {
            double size = WorldPixels(zoom);
            double lat = Math.Max(-MaxLatitude, Math.Min(MaxLatitude, p.Latitude)) * Math.PI / 180.0;
            double x = (p.Longitude + 180.0) / 360.0 * size;
            double y = (1 - Math.Log(Math.Tan(lat) + 1 / Math.Cos(lat)) / Math.PI) / 2 * size;
            return (x, y);
        }

        public static GeoPoint FromPixels(double x, double y, double zoom)
        {
            double size = WorldPixels(zoom);
            double lon = x / size * 360.0 - 180.0;
            double n = Math.PI * (1 - 2 * y / size);
            return new GeoPoint(Math.Atan(Math.Sinh(n)) * 180.0 / Math.PI, lon);
        }

        /// <summary>
        /// The core's corners in latitude and longitude, north-west first and clockwise. They are the
        /// exact EPSG:6350 corners, inverse-projected.
        /// </summary>
        public static GeoPoint[] Corners(AlbersBox box) => new[]
        {
            Albers6350.Inverse(new AlbersPoint(box.West, box.North)),
            Albers6350.Inverse(new AlbersPoint(box.East, box.North)),
            Albers6350.Inverse(new AlbersPoint(box.East, box.South)),
            Albers6350.Inverse(new AlbersPoint(box.West, box.South)),
        };

        /// <summary>
        /// A box's outline for drawing: <paramref name="perEdge"/> points along each straight Albers edge,
        /// clockwise from the north-west corner. A straight Albers line is very slightly curved in Web
        /// Mercator, so the edges are walked rather than joined corner to corner.
        /// </summary>
        public static GeoPoint[] Outline(AlbersBox box, int perEdge = 8)
        {
            if (perEdge < 1) throw new ArgumentOutOfRangeException(nameof(perEdge));
            var corners = new[]
            {
                new AlbersPoint(box.West, box.North), new AlbersPoint(box.East, box.North),
                new AlbersPoint(box.East, box.South), new AlbersPoint(box.West, box.South),
            };
            var points = new GeoPoint[4 * perEdge];
            for (int e = 0; e < 4; e++)
            {
                var a = corners[e];
                var b = corners[(e + 1) % 4];
                for (int i = 0; i < perEdge; i++)
                {
                    double t = i / (double)perEdge;
                    points[e * perEdge + i] = Albers6350.Inverse(new AlbersPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                }
            }
            return points;
        }
    }
}
