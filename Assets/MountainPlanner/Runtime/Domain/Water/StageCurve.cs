using System;

namespace MountainPlanner.Domain.Water
{
    /// <summary>
    /// WS-02 v2 (task 4.14 PR B): a source's stage–storage curve, the water it holds at each 0.1 ft below full pool. A
    /// pond's or a reservoir's comes from its graded bowl, point by point (<see cref="Builder"/>, fed by the earthwork's own
    /// wet loop); a lake's is a prism (LK-02's area × depth). Stored water is whole gallons (SI-07); levels are micrometres
    /// below full pool, so every lookup is integer and the same everywhere.
    /// </summary>
    public sealed class StageCurve
    {
        /// <summary>One step: 0.1 ft.</summary>
        public const int StepMicrons = 30_480;
        public const double GallonsPerCubicMetre = 264.172052358148;

        /// <summary>Gallons held with the water j steps below full pool: [0] is the capacity, the last is 0 (the floor).</summary>
        readonly long[] _gallons;
        /// <summary>The water's area, square metres, j steps below full pool.</summary>
        readonly int[] _area;

        StageCurve(long[] gallons, int[] area) { _gallons = gallons; _area = area; }

        public long CapacityGallons => _gallons[0];
        public int Steps => _gallons.Length - 1;
        /// <summary>The floor: the deepest point below full pool, micrometres (to the step).</summary>
        public long DepthMicrons => (long)Steps * StepMicrons;
        public long GallonsAtStep(int j) => _gallons[Math.Max(0, Math.Min(Steps, j))];
        public int AreaAtStep(int j) => _area[Math.Max(0, Math.Min(Steps, j))];

        /// <summary>How far below full pool the water stands when it holds <paramref name="gallons"/> (0 = full).</summary>
        public long BelowFullMicrons(long gallons)
        {
            if (gallons >= _gallons[0]) return 0;
            if (gallons <= 0) return DepthMicrons;
            // _gallons falls with j: the last step still holding more than the water.
            int lo = 0, hi = Steps;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_gallons[mid] > gallons) lo = mid; else hi = mid;
            }
            long span = _gallons[lo] - _gallons[hi];
            long into = span > 0 ? (_gallons[lo] - gallons) * StepMicrons / span : 0;
            return (long)lo * StepMicrons + into;
        }

        /// <summary>The water held when it stands <paramref name="belowMicrons"/> below full pool (the inverse, linear in a step).</summary>
        public long GallonsBelowFull(long belowMicrons)
        {
            if (belowMicrons <= 0) return _gallons[0];
            if (belowMicrons >= DepthMicrons) return 0;
            int j = (int)(belowMicrons / StepMicrons);
            long into = belowMicrons - (long)j * StepMicrons;
            return _gallons[j] - (_gallons[j] - _gallons[j + 1]) * into / StepMicrons;
        }

        /// <summary>The water's area at the level that holds <paramref name="gallons"/>, square metres.</summary>
        public int AreaAt(long gallons) => AreaAtStep((int)(BelowFullMicrons(gallons) / StepMicrons));

        /// <summary>LK-02's prism: the same area all the way down to the lake's mean depth.</summary>
        public static StageCurve Prism(long areaSquareMetres, long depthMicrons)
        {
            if (areaSquareMetres < 0 || depthMicrons < 0) throw new ArgumentOutOfRangeException(nameof(areaSquareMetres));
            int steps = (int)Math.Max(1, (depthMicrons + StepMicrons - 1) / StepMicrons);
            var gallons = new long[steps + 1];
            var area = new int[steps + 1];
            for (int j = 0; j <= steps; j++)
            {
                long below = Math.Max(0, depthMicrons - (long)j * StepMicrons);
                gallons[j] = (long)Math.Round(areaSquareMetres * (below / 1e6) * GallonsPerCubicMetre);
                area[j] = j < steps ? (int)Math.Min(int.MaxValue, areaSquareMetres) : 0;
            }
            return new StageCurve(gallons, area);
        }

        /// <summary>
        /// Gathers a bowl's wet points, 1 m² each, by their depth below full pool, then gives the exact curve: with n and the
        /// depth sum per step bin, the water j steps down is Σ over the deeper bins of (sum − j·step·n).
        /// </summary>
        public sealed class Builder
        {
            long[] _count = new long[64];
            double[] _sum = new double[64];
            int _bins;

            /// <summary>One wet point <paramref name="depthMetres"/> under full pool.</summary>
            public void Add(double depthMetres)
            {
                if (!(depthMetres > 0)) return;
                int k = (int)(depthMetres * 1e6 / StepMicrons);
                if (k >= _count.Length)
                {
                    int n = Math.Max(k + 1, _count.Length * 2);
                    Array.Resize(ref _count, n);
                    Array.Resize(ref _sum, n);
                }
                _count[k]++;
                _sum[k] += depthMetres;
                if (k + 1 > _bins) _bins = k + 1;
            }

            public StageCurve Build()
            {
                int steps = Math.Max(1, _bins);
                var gallons = new long[steps + 1];
                var area = new int[steps + 1];
                long n = 0;
                double sum = 0;
                for (int j = steps; j >= 0; j--)
                {
                    if (j < _bins) { n += _count[j]; sum += _sum[j]; }
                    double cubic = sum - j * (StepMicrons / 1e6) * n;
                    gallons[j] = j == steps ? 0 : (long)Math.Round(Math.Max(0, cubic) * GallonsPerCubicMetre);
                    area[j] = (int)Math.Min(int.MaxValue, n);
                }
                for (int j = steps - 1; j >= 0; j--) if (gallons[j] < gallons[j + 1]) gallons[j] = gallons[j + 1];   // never rises going down
                return new StageCurve(gallons, area);
            }
        }
    }
}
