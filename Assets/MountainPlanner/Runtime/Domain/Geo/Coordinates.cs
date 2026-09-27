using System;
using System.Globalization;

namespace MountainPlanner.Domain.Geo
{
    /// <summary>A NAD83(2011) latitude and longitude in degrees (west is negative).</summary>
    public readonly struct GeoPoint : IEquatable<GeoPoint>
    {
        public readonly double Latitude;
        public readonly double Longitude;

        public GeoPoint(double latitude, double longitude)
        {
            if (!(latitude >= -90 && latitude <= 90)) throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be -90 to 90.");
            if (!(longitude >= -180 && longitude <= 180)) throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be -180 to 180.");
            Latitude = latitude;
            Longitude = longitude;
        }

        public bool Equals(GeoPoint other) => Latitude.Equals(other.Latitude) && Longitude.Equals(other.Longitude);
        public override bool Equals(object obj) => obj is GeoPoint other && Equals(other);
        public override int GetHashCode() => Latitude.GetHashCode() * 397 ^ Longitude.GetHashCode();
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:F6}, {1:F6})", Latitude, Longitude);
    }

    /// <summary>A position on the EPSG:6350 (CONUS Albers) grid, in metres: X east, Y north.</summary>
    public readonly struct AlbersPoint : IEquatable<AlbersPoint>
    {
        public readonly double X;
        public readonly double Y;

        public AlbersPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public static AlbersPoint operator +(AlbersPoint p, (double dx, double dy) d) => new AlbersPoint(p.X + d.dx, p.Y + d.dy);

        public double DistanceTo(AlbersPoint other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));

        public bool Equals(AlbersPoint other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object obj) => obj is AlbersPoint other && Equals(other);
        public override int GetHashCode() => X.GetHashCode() * 397 ^ Y.GetHashCode();
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3})", X, Y);
    }

    /// <summary>An axis-aligned box on the Albers grid, in metres.</summary>
    public readonly struct AlbersBox : IEquatable<AlbersBox>
    {
        public readonly double West;
        public readonly double South;
        public readonly double East;
        public readonly double North;

        public AlbersBox(double west, double south, double east, double north)
        {
            if (!(east > west) || !(north > south)) throw new ArgumentException("A box needs east > west and north > south.");
            West = west;
            South = south;
            East = east;
            North = north;
        }

        public double Width => East - West;
        public double Height => North - South;
        public AlbersPoint Centre => new AlbersPoint((West + East) / 2, (South + North) / 2);

        public bool Contains(AlbersPoint p) => p.X >= West && p.X < East && p.Y > South && p.Y <= North;

        public bool Intersects(AlbersBox other) => other.West < East && other.East > West && other.South < North && other.North > South;

        public AlbersBox Expand(double metres) => new AlbersBox(West - metres, South - metres, East + metres, North + metres);

        public bool Equals(AlbersBox other) => West.Equals(other.West) && South.Equals(other.South) && East.Equals(other.East) && North.Equals(other.North);
        public override bool Equals(object obj) => obj is AlbersBox other && Equals(other);
        public override int GetHashCode() => ((West.GetHashCode() * 397 ^ South.GetHashCode()) * 397 ^ East.GetHashCode()) * 397 ^ North.GetHashCode();
        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "[W {0:F1}, S {1:F1}, E {2:F1}, N {3:F1}]", West, South, East, North);
    }
}
