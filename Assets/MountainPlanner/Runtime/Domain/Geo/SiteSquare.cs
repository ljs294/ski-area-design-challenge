using System;

namespace MountainPlanner.Domain.Geo
{
    /// <summary>
    /// The square a player picks (0.3 §4.2, §6): 2–5 km in 0.1 km steps, exact in Albers metres
    /// around a clicked centre, plus the 3 km surround ring.
    ///
    /// The centre snaps to whole 2 m so that the core's edges fall on S1M's 1 m pixel edges and the
    /// ring's edges fall on its 2 m overview pixel edges: heights are copied, never resampled (T3).
    /// </summary>
    public readonly struct SiteSquare
    {
        public const double MinSizeKm = 2.0;
        public const double MaxSizeKm = 5.0;
        public const double SizeStepKm = 0.1;
        public const double RingMetres = 3000;
        public const double CoreCellMetres = 1;
        public const double RingCellMetres = 2;

        public readonly AlbersPoint Centre;
        public readonly int SizeMetres;

        SiteSquare(AlbersPoint centre, int sizeMetres)
        {
            Centre = centre;
            SizeMetres = sizeMetres;
        }

        /// <summary>
        /// A site around <paramref name="clicked"/>. <paramref name="sizeKm"/> must be 2–5 in 0.1 km
        /// steps; the centre snaps to the nearest 2 m.
        /// </summary>
        public static SiteSquare Create(AlbersPoint clicked, double sizeKm)
        {
            double steps = sizeKm / SizeStepKm;
            if (!(sizeKm >= MinSizeKm - 1e-9 && sizeKm <= MaxSizeKm + 1e-9) || Math.Abs(steps - Math.Round(steps)) > 1e-6)
                throw new ArgumentOutOfRangeException(nameof(sizeKm), sizeKm, "Sites are 2–5 km in 0.1 km steps.");
            int size = (int)Math.Round(steps) * 100;
            var centre = new AlbersPoint(SnapTo2(clicked.X), SnapTo2(clicked.Y));
            return new SiteSquare(centre, size);
        }

        /// <summary>A site around a latitude and longitude.</summary>
        public static SiteSquare Create(GeoPoint clicked, double sizeKm) => Create(Albers6350.Forward(clicked), sizeKm);

        public double SizeKm => SizeMetres / 1000.0;

        /// <summary>The core site, at full 1 m resolution.</summary>
        public AlbersBox Core
        {
            get
            {
                double half = SizeMetres / 2.0;
                return new AlbersBox(Centre.X - half, Centre.Y - half, Centre.X + half, Centre.Y + half);
            }
        }

        /// <summary>The core plus the 3 km surround ring on every side.</summary>
        public AlbersBox Ring => Core.Expand(RingMetres);

        public GridSpec CoreGrid => GridSpec.Covering(Core, CoreCellMetres);
        public GridSpec RingGrid => GridSpec.Covering(Ring, RingCellMetres);

        /// <summary>The resort's local frame, centred on the site.</summary>
        public LocalFrame Frame => new LocalFrame(Centre);

        /// <summary>Scale factors at the site centre (stored in the manifest).</summary>
        public ScaleFactors Scale => Albers6350.ScaleAt(Albers6350.Inverse(Centre).Latitude);

        static double SnapTo2(double v) => Math.Round(v / 2, MidpointRounding.AwayFromZero) * 2;
    }
}
