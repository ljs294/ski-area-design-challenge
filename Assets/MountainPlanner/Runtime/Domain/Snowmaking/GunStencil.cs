using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>
    /// SG-03 v1 (task 4.15, for 4.16's deposit): where a gun's snow lands, as a stencil of cell offsets and integer weights
    /// summing to exactly <see cref="Total"/>, so the tick adds rate × weight with no transcendental call (SI-09).
    /// - **Shape.** An elongated Gaussian down the aim: length = the throw, width = the throw ÷ 3. Its centre stands half the
    ///   throw out; σ along is a quarter of the length and σ across a quarter of the width, and the stencil keeps the cells
    ///   inside two σ (the length and the width).
    /// - **Wind.** The centre moves downwind wind (mph) × 2 ft and the width grows 20 % per 10 mph; above
    ///   <see cref="DefaultWindLimitMph"/> the gun stops (the snow blows off the trail): <see cref="Stopped"/>.
    /// - **When.** Built only when the aim, the model or the hour's wind bucket (<see cref="WindBucketMph"/>; direction to the
    ///   nearest 10°) changes, at most once a face hour: <see cref="Key"/> says when.
    /// Bearings are centidegrees clockwise from north (+Y); the wind's is where it blows to. Offsets are cells east (+X) and
    /// north (+Y) of the gun's cell.
    /// </summary>
    public sealed class GunStencil
    {
        public const int Total = 1 << 16;
        public const int WindBucketMph = 5, DirectionBucketCentideg = 1000, DefaultWindLimitMph = 25;
        const double FtPerMm = 1 / 304.8;

        public readonly int[] Dx, Dy, Weight;
        public readonly bool Stopped;
        public int Count => Weight.Length;

        GunStencil(int[] dx, int[] dy, int[] w, bool stopped) { Dx = dx; Dy = dy; Weight = w; Stopped = stopped; }

        /// <summary>The inputs a stencil depends on, bucketed: equal keys give the same stencil.</summary>
        public static long Key(GunSpec spec, int aimCentideg, int windMph, int windToCentideg)
        {
            int bucket = Math.Max(0, windMph) / WindBucketMph;
            int dir = bucket == 0 ? 0 : Norm(windToCentideg + DirectionBucketCentideg / 2) / DirectionBucketCentideg;
            return ((long)spec.Model * 36_000 + Norm(aimCentideg)) * 1_000 * 64 + bucket * 64 + dir;
        }

        /// <summary>Builds the stencil for a gun of <paramref name="spec"/> on cells of <paramref name="cellMm"/>.</summary>
        public static GunStencil Build(GunSpec spec, int aimCentideg, int windMph, int windToCentideg, int cellMm = 1000,
                                       int windLimitMph = DefaultWindLimitMph)
        {
            if (cellMm <= 0) throw new ArgumentOutOfRangeException(nameof(cellMm));
            windMph = Math.Max(0, windMph);
            if (windMph > windLimitMph) return new GunStencil(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), true);
            // The wind as the stencil sees it: its bucket's speed and its direction to the nearest 10°.
            int mph = windMph / WindBucketMph * WindBucketMph;
            int windDir = mph == 0 ? 0 : Norm(windToCentideg + DirectionBucketCentideg / 2) / DirectionBucketCentideg * DirectionBucketCentideg;

            double cellFt = cellMm * FtPerMm;
            double length = spec.ThrowFt, width = spec.ThrowFt / 3.0 * (1 + 0.02 * mph);
            double sAlong = length / 4, sAcross = width / 4;
            double aim = Norm(aimCentideg) / 18_000.0 * Math.PI, wind = windDir / 18_000.0 * Math.PI;
            double ax = Math.Sin(aim), ay = Math.Cos(aim);                 // down the aim
            double cx = ax * length / 2 + Math.Sin(wind) * mph * 2;        // the centre, ft east and north of the gun
            double cy = ay * length / 2 + Math.Cos(wind) * mph * 2;

            int reach = (int)Math.Ceiling((Math.Max(2 * sAlong, 2 * sAcross) + Math.Sqrt(cx * cx + cy * cy)) / cellFt) + 1;
            var dx = new List<int>(); var dy = new List<int>(); var raw = new List<double>();
            double sum = 0;
            for (int j = -reach; j <= reach; j++)
                for (int i = -reach; i <= reach; i++)
                {
                    double px = i * cellFt - cx, py = j * cellFt - cy;
                    double u = (px * ax + py * ay) / sAlong, v = (px * ay - py * ax) / sAcross;   // along, across in σ
                    double r2 = u * u + v * v;
                    if (r2 > 4) continue;
                    double w = Math.Exp(-r2 / 2);
                    dx.Add(i); dy.Add(j); raw.Add(w); sum += w;
                }
            if (raw.Count == 0) { dx.Add((int)Math.Round(cx / cellFt)); dy.Add((int)Math.Round(cy / cellFt)); raw.Add(1); sum = 1; }

            // Whole weights summing to Total: floors, then the remainder to the largest fractions (ties by order).
            int n = raw.Count;
            var weight = new int[n];
            var frac = new double[n];
            long given = 0;
            for (int k = 0; k < n; k++)
            {
                double exact = raw[k] / sum * Total;
                weight[k] = (int)Math.Floor(exact);
                frac[k] = exact - weight[k];
                given += weight[k];
            }
            var order = new int[n];
            for (int k = 0; k < n; k++) order[k] = k;
            Array.Sort(order, (a, b) => { int c = frac[b].CompareTo(frac[a]); return c != 0 ? c : a.CompareTo(b); });
            for (int k = 0; given < Total; k = (k + 1) % n) { weight[order[k]]++; given++; }
            return new GunStencil(dx.ToArray(), dy.ToArray(), weight, false);
        }

        static int Norm(int centideg) => ((centideg % 36_000) + 36_000) % 36_000;
    }
}
