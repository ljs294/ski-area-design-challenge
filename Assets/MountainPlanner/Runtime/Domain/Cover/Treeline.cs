using System;

namespace MountainPlanner.Domain.Cover
{
    /// <summary>
    /// Krummholz at the treeline (0.3 §4.5, task 09): each site's treeline is read from its own canopy map, as
    /// the elevation where the forest ends (the 98th percentile of forest elevations within 1.5 km), worked out
    /// every 250 m and blended between. It counts only where open land rises above it; a forested summit has no
    /// treeline. In the 150 m below it, trees turn gradually into krummholz, and all of them in the top 50 m.
    /// </summary>
    public static class Treeline
    {
        public const double BandMetres = 150, FullMetres = 50;
        public const double BlockMetres = 250, ReachMetres = 1500;
        public const double Percentile = 0.98;
        /// <summary>At least this share of the window must be open ground above the forest line for it to be a treeline.</summary>
        public const double MinOpenAbove = 0.03;
        /// <summary>Fewer forest cells than this in the window: no estimate.</summary>
        public const int MinForestCells = 200;
        /// <summary>
        /// The rotation (1/256 turns about +Y) that points a model's +Z downwind: the game's prevailing wind blows
        /// toward 67.5° east of north (ForestWind), and a rotation θ turns +Z to (sin θ, cos θ).
        /// </summary>
        public const int DownwindRotation = 48;

        /// <summary>The krummholz model's variants (tools/assets/trees): a low mat, a flag tree and a cushion.</summary>
        public const int Mat = 0, FlagTree = 1, Cushion = 2;
        /// <summary>
        /// Each variant's height at scale 1 (metres; checked against the imported models in EditMode). Krummholz
        /// stays within 15% of it: scaling a 0.87 m mat to 3.5 m would stretch it 10 m downwind.
        /// </summary>
        public static readonly double[] NativeHeights = { 0.87, 3.22, 1.52 };
        public const double SizeJitter = 0.15;
        /// <summary>
        /// Mats lie flat on the ground, so they grow only on gentler slopes: on a 25° slope a 3 m mat floats 1.5 m
        /// off the ground at its downhill tail, or buries most of its foliage uphill (tree-build audit). Steeper
        /// ground grows cushions instead.
        /// </summary>
        public const double MatMaxSlopeDegrees = 13;

        /// <summary>Per variant, the lowest and highest height in 0.25 m steps within <see cref="SizeJitter"/> of its native height.</summary>
        public static int[] HeightCodes()
        {
            var codes = new int[NativeHeights.Length * 2];
            for (int v = 0; v < NativeHeights.Length; v++)
            {
                double h = NativeHeights[v];
                int lo = (int)Math.Ceiling(h * (1 - SizeJitter) / 0.25 - 1e-9), hi = (int)Math.Floor(h * (1 + SizeJitter) / 0.25 + 1e-9);
                if (lo > hi) lo = hi = Math.Max(1, (int)Math.Round(h / 0.25));
                codes[2 * v] = lo;
                codes[2 * v + 1] = hi;
            }
            return codes;
        }

        /// <summary>
        /// The treeline over a grid of cells (row-major, rows from the south): per cell its elevation (NaN where
        /// the cell has no data) and whether it holds forest. Returns each cell's chance (0–255) of growing
        /// krummholz, and the median treeline of the blocks that have one (NaN when none).
        /// </summary>
        public static byte[] Krummholz(float[] elevation, bool[] forest, int columns, int rows, double cellMetres, out double medianTreeline)
        {
            int block = Math.Max(1, (int)Math.Round(BlockMetres / cellMetres)), reach = (int)Math.Round(ReachMetres / cellMetres);
            int bx = (columns + block - 1) / block, by = (rows + block - 1) / block;
            var line = new double[bx * by];
            var histogram = new int[5000];
            for (int j = 0; j < by; j++)
                for (int i = 0; i < bx; i++)
                {
                    line[j * bx + i] = double.NaN;
                    int ci = i * block + block / 2, cj = j * block + block / 2;
                    int i0 = Math.Max(0, ci - reach), i1 = Math.Min(columns - 1, ci + reach), j0 = Math.Max(0, cj - reach), j1 = Math.Min(rows - 1, cj + reach);
                    Array.Clear(histogram, 0, histogram.Length);
                    int forested = 0, cells = 0;
                    for (int y = j0; y <= j1; y++)
                        for (int x = i0; x <= i1; x++)
                        {
                            float e = elevation[y * columns + x];
                            if (float.IsNaN(e)) continue;
                            cells++;
                            if (!forest[y * columns + x]) continue;
                            forested++;
                            histogram[Math.Max(0, Math.Min(histogram.Length - 1, (int)e))]++;
                        }
                    if (forested < MinForestCells) continue;
                    int target = (int)Math.Ceiling(Percentile * forested), running = 0, metres = 0;
                    for (; metres < histogram.Length; metres++)
                        if ((running += histogram[metres]) >= target) break;
                    int openAbove = 0;
                    for (int y = j0; y <= j1; y++)
                        for (int x = i0; x <= i1; x++)
                        {
                            float e = elevation[y * columns + x];
                            if (!float.IsNaN(e) && e > metres + 1 && !forest[y * columns + x]) openAbove++;
                        }
                    if (openAbove >= MinOpenAbove * cells) line[j * bx + i] = metres + 0.5;
                }

            var valid = new System.Collections.Generic.List<double>();
            foreach (double v in line) if (!double.IsNaN(v)) valid.Add(v);
            valid.Sort();
            medianTreeline = valid.Count > 0 ? valid[valid.Count / 2] : double.NaN;

            var chance = new byte[columns * rows];
            if (valid.Count == 0) return chance;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                {
                    float e = elevation[y * columns + x];
                    if (float.IsNaN(e) || !forest[y * columns + x]) continue;
                    // Bilinear between block centres, over the blocks that have a treeline.
                    double fx = (x - block / 2.0) / block, fy = (y - block / 2.0) / block;
                    int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
                    double tx = fx - x0, ty = fy - y0, weight = 0, sum = 0, total = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int px = Math.Max(0, Math.Min(bx - 1, x0 + (k & 1))), py = Math.Max(0, Math.Min(by - 1, y0 + (k >> 1)));
                        double w = ((k & 1) == 1 ? tx : 1 - tx) * ((k >> 1) == 1 ? ty : 1 - ty);
                        total += w;
                        double v = line[py * bx + px];
                        if (double.IsNaN(v)) continue;
                        weight += w;
                        sum += w * v;
                    }
                    if (weight <= 0) continue;
                    double treeline = sum / weight;
                    double ramp = (e - (treeline - BandMetres)) / (BandMetres - FullMetres);
                    ramp = ramp < 0 ? 0 : ramp > 1 ? 1 : ramp;
                    double known = Math.Min(1, 2 * weight / total);   // fades out only where most of the blend has no treeline
                    chance[y * columns + x] = (byte)Math.Round(255 * ramp * known);
                }
            return chance;
        }
    }
}
