using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Snow
{
    /// <summary>
    /// Task 4.07 PR A3 (owner, 2026-11-15: "Build it now, soft"): a whale a snow gun builds in calm air, by the written gun
    /// rules, for tests and developers (<c>-gunwhale</c>); guns in the game are task 4.16's, which can reuse it.
    /// <list type="bullet">
    /// <item>SG-03 v1, where the snow lands: an elongated Gaussian down the aim, as long as the throw and a third as wide;
    /// here it's centred half a throw out, with ±2σ spanning that length and width (the snow lands between the gun and its
    /// throw).</item>
    /// <item>SG-05 v1, how much: water (gpm) × 60 × (1 − loss) an hour, as snow's water.</item>
    /// <item>SN-27 v1, how high: <see cref="Settings.CapMetres"/> of snow at most; snow above that spills to the lowest
    /// neighbouring square.</item>
    /// </list>
    /// Built on a 3 ft grid (the fine field's size in SN-03) in whole cm³ of water, so nothing is lost: <see cref="Coarse"/>
    /// sums it into the snow field's squares exactly.
    /// </summary>
    public sealed class GunWhale
    {
        /// <summary>The fine grid's square: 3 ft.</summary>
        public const double FineMetres = 0.9144;

        /// <summary>One US gallon in cm³.</summary>
        public const double GallonCm3 = 3785.411784;

        /// <summary>The gun and its night (SG-03, SG-05, SN-27's numbers).</summary>
        public struct Settings
        {
            /// <summary>Throw (m): 80 ft for a 20 ft stick.</summary>
            public double ThrowMetres;
            /// <summary>Water (gpm): 48 at stage 4.</summary>
            public double Gpm;
            /// <summary>Water lost to sublimation and drift (%): <c>Pacing.GunLoss</c>, 15.</summary>
            public double LossPercent;
            /// <summary>Hours the gun runs a night: 16.</summary>
            public int HoursPerNight;
            /// <summary>The highest snow may stand (m): <c>Pacing.WhaleMax</c>, 26 ft.</summary>
            public double CapMetres;

            public static Settings Stick20 => new Settings { ThrowMetres = 80 * 0.3048, Gpm = 48, LossPercent = 15, HoursPerNight = 16, CapMetres = 26 * 0.3048 };
        }

        /// <summary>The fine grid: <see cref="Size"/> squares each way, its west/south corner at (<see cref="WestMetres"/>,
        /// <see cref="SouthMetres"/>) in the caller's metres; cells run west to east, then south to north.</summary>
        public readonly int Size;
        public readonly double WestMetres, SouthMetres;

        /// <summary>The water in each fine square (cm³).</summary>
        public readonly long[] WaterCm3;

        /// <summary>The water the gun made (cm³): every square's, summed, to the cm³.</summary>
        public readonly long MadeCm3;

        /// <summary>The most water a fine square holds at the cap (cm³), at the snow's density.</summary>
        public readonly long CapCm3;

        /// <summary>The snow's density (kg/m³) the cap and the depths are read at.</summary>
        public readonly int Density;

        GunWhale(int size, double west, double south, long[] water, long made, long cap, int density)
        {
            Size = size; WestMetres = west; SouthMetres = south; WaterCm3 = water; MadeCm3 = made; CapCm3 = cap; Density = density;
        }

        /// <summary>The water one hour of the gun makes (cm³), SG-05.</summary>
        public static long HourCm3(Settings s) => (long)Math.Round(s.Gpm * 60 * (1 - s.LossPercent / 100) * GallonCm3);

        /// <summary>A fine square's snow depth (m) at <see cref="Density"/>.</summary>
        public double DepthMetres(int i, int j) => WaterCm3[j * Size + i] / 1e6 * 1000.0 / Density / (FineMetres * FineMetres);

        /// <summary>
        /// The whale a gun at (<paramref name="gunX"/>, <paramref name="gunZ"/>) aimed along (<paramref name="aimX"/>,
        /// <paramref name="aimZ"/>) builds in <paramref name="nights"/> nights, as snow of <paramref name="density"/> kg/m³.
        /// </summary>
        public static GunWhale Build(double gunX, double gunZ, double aimX, double aimZ, int nights, int density, Settings s)
        {
            if (nights < 0) throw new ArgumentOutOfRangeException(nameof(nights));
            if (density <= 0) throw new ArgumentOutOfRangeException(nameof(density));
            double aimLen = Math.Sqrt(aimX * aimX + aimZ * aimZ);
            double ux = aimLen > 1e-9 ? aimX / aimLen : 1, uz = aimLen > 1e-9 ? aimZ / aimLen : 0;
            double area = FineMetres * FineMetres;
            long cap = (long)Math.Floor(s.CapMetres * area * density / 1000.0 * 1e6);
            long hour = HourCm3(s);
            long made = hour * s.HoursPerNight * nights;
            double sigmaAlong = s.ThrowMetres / 4, sigmaAcross = s.ThrowMetres / 3 / 4;
            // Big enough that nothing spills off: the throw and its tail, plus the radius of a disc of capped squares holding it all.
            double reach = s.ThrowMetres + 4 * sigmaAlong + Math.Sqrt(made / (double)Math.Max(1, cap) / Math.PI) * FineMetres + 4 * FineMetres;
            int half = (int)Math.Ceiling(reach / FineMetres);
            int size = 2 * half;
            double west = gunX - half * FineMetres, south = gunZ - half * FineMetres;

            // SG-03's stencil: the Gaussian's weights in whole parts of one hour's water (largest remainder, ties by index),
            // so each hour lays down exactly the hour's cm³.
            var deposit = new long[size * size];
            if (hour > 0)
            {
                double cx = s.ThrowMetres / 2 * ux, cz = s.ThrowMetres / 2 * uz;
                var weight = new double[size * size];
                double total = 0;
                for (int j = 0; j < size; j++)
                    for (int i = 0; i < size; i++)
                    {
                        double x = west + (i + 0.5) * FineMetres - gunX - cx, z = south + (j + 0.5) * FineMetres - gunZ - cz;
                        double a = (x * ux + z * uz) / sigmaAlong, c = (-x * uz + z * ux) / sigmaAcross;
                        double r2 = a * a + c * c;
                        double w = r2 > 36 ? 0 : Math.Exp(-0.5 * r2);
                        weight[j * size + i] = w;
                        total += w;
                    }
                long given = 0;
                var rest = new List<(double Part, int Cell)>();
                for (int k = 0; k < weight.Length; k++)
                {
                    if (weight[k] <= 0) continue;
                    double share = hour * weight[k] / total;
                    long whole = (long)Math.Floor(share);
                    deposit[k] = whole;
                    given += whole;
                    rest.Add((share - whole, k));
                }
                rest.Sort((p, q) => p.Part != q.Part ? q.Part.CompareTo(p.Part) : p.Cell.CompareTo(q.Cell));
                for (int n = 0; given < hour; n++, given++) deposit[rest[n % rest.Count].Cell]++;
            }

            var water = new long[size * size];
            var over = new Queue<int>();
            int hours = s.HoursPerNight * nights;
            for (int h = 0; h < hours; h++)
            {
                for (int k = 0; k < water.Length; k++)
                {
                    if (deposit[k] == 0) continue;
                    water[k] += deposit[k];
                    if (water[k] > cap) over.Enqueue(k);
                }
                Spill(water, size, cap, over);
            }
            return new GunWhale(size, west, south, water, made, cap, density);
        }

        /// <summary>
        /// SN-27: a square over the cap keeps the cap and the rest runs downhill: to its lowest neighbour with room first,
        /// and across a capped top (flat at the cap) to the nearest squares with room, nearest ring first, lowest first in a
        /// ring, ties by index.
        /// </summary>
        static void Spill(long[] water, int size, long cap, Queue<int> over)
        {
            if (over.Count == 0) return;
            var seen = new int[water.Length];
            int stamp = 0;
            var ring = new List<int>();
            var next = new List<int>();
            while (over.Count > 0)
            {
                int k = over.Dequeue();
                long excess = water[k] - cap;
                if (excess <= 0) continue;
                water[k] = cap;
                stamp++;
                seen[k] = stamp;
                ring.Clear();
                ring.Add(k);
                while (excess > 0)
                {
                    next.Clear();
                    foreach (int c in ring)
                    {
                        int i = c % size, j = c / size;
                        if (i + 1 < size) Visit(c + 1);
                        if (i > 0) Visit(c - 1);
                        if (j + 1 < size) Visit(c + size);
                        if (j > 0) Visit(c - size);
                    }
                    if (next.Count == 0) throw new InvalidOperationException("The whale's grid filled to its edge.");
                    next.Sort((p, q) => water[p] != water[q] ? water[p].CompareTo(water[q]) : p.CompareTo(q));
                    foreach (int n in next)
                    {
                        long moved = Math.Min(excess, cap - water[n]);
                        if (moved <= 0) continue;
                        water[n] += moved;
                        excess -= moved;
                        if (excess == 0) break;
                    }
                    (ring, next) = (next, ring);
                }

                void Visit(int n)
                {
                    if (seen[n] == stamp) return;
                    seen[n] = stamp;
                    next.Add(n);
                }
            }
        }

        /// <summary>The tallest fine square's snow depth (m) at <see cref="Density"/>: the whale's peak as modelled.</summary>
        public double PeakMetres
        {
            get
            {
                long most = 0;
                foreach (long v in WaterCm3) if (v > most) most = v;
                return most / 1e6 * 1000.0 / Density / (FineMetres * FineMetres);
            }
        }

        /// <summary>
        /// The whale as snow depth (m at <see cref="Density"/>) to ADD to each of a field's squares (<see cref="Coarse"/>'s water
        /// over the square): how the game's developer seed lays it on the snow already there (task 4.07 PR A3).
        /// </summary>
        public float[] AddedMetres(double west, double south, double cellMetres, int width, int height, out long outsideCm3)
        {
            var water = Coarse(west, south, cellMetres, width, height, out outsideCm3);
            var add = new float[water.Length];
            double area = cellMetres * cellMetres;
            for (int c = 0; c < add.Length; c++)
                if (water[c] != 0) add[c] = (float)(water[c] / 1e6 * 1000.0 / Density / area);
            return add;
        }

        /// <summary>
        /// The whale's water summed into a field's squares (cm³ each; <paramref name="width"/> × <paramref name="height"/> of
        /// <paramref name="cellMetres"/>, its west/south corner at <paramref name="west"/>, <paramref name="south"/>): each fine
        /// square into the field square holding its centre. <paramref name="outsideCm3"/> is what fell off the field.
        /// </summary>
        public long[] Coarse(double west, double south, double cellMetres, int width, int height, out long outsideCm3)
        {
            var cells = new long[width * height];
            outsideCm3 = 0;
            for (int j = 0; j < Size; j++)
                for (int i = 0; i < Size; i++)
                {
                    long v = WaterCm3[j * Size + i];
                    if (v == 0) continue;
                    double x = WestMetres + (i + 0.5) * FineMetres, z = SouthMetres + (j + 0.5) * FineMetres;
                    int ci = (int)Math.Floor((x - west) / cellMetres), cj = (int)Math.Floor((z - south) / cellMetres);
                    if (ci < 0 || cj < 0 || ci >= width || cj >= height) { outsideCm3 += v; continue; }
                    cells[cj * width + ci] += v;
                }
            return cells;
        }
    }
}
