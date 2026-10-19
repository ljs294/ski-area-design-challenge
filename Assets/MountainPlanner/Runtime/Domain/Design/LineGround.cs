using System;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The ground under a lift line (TW-08 v2, proxy P1), sampled once every metre from the bottom bullwheel: at the
    /// centreline, and the highest ground under each rope's chairs, at the rope and at both edges of the chair (TW-01 v2).
    /// The uphill (loaded) rope is on the right looking up the line, the downhill rope on the left. Placement samples it once
    /// and every solve reads it.
    /// </summary>
    public sealed class LineGround
    {
        public double LengthMetres { get; }
        /// <summary>Samples at s = 0, 1, 2 … metres.</summary>
        public int Count => Centre.Length;
        public double[] Centre { get; }
        public double[] UnderUp { get; }
        public double[] UnderDown { get; }

        LineGround(double length, int count)
        {
            LengthMetres = length;
            Centre = new double[count];
            UnderUp = new double[count];
            UnderDown = new double[count];
        }

        public static LineGround Sample(IHeightSource ground, LiftType type, double bx, double by, double tx, double ty)
        {
            if (ground == null) throw new ArgumentNullException(nameof(ground));
            double dx = tx - bx, dy = ty - by, length = Math.Sqrt(dx * dx + dy * dy);
            double ux = dx / length, uy = dy / length, rx = uy, ry = -ux;   // right of uphill
            double half = type.GaugeMetres / 2, chair = type.ChairHalfWidthMetres;
            double near = half - chair, far = half + chair;
            var line = new LineGround(length, (int)Math.Floor(length) + 1);
            for (int j = 0; j < line.Count; j++)
            {
                double x = bx + ux * j, y = by + uy * j;
                line.Centre[j] = ground.At(x, y);
                line.UnderUp[j] = Math.Max(ground.At(x + rx * near, y + ry * near),
                                  Math.Max(ground.At(x + rx * half, y + ry * half), ground.At(x + rx * far, y + ry * far)));
                line.UnderDown[j] = Math.Max(ground.At(x - rx * near, y - ry * near),
                                    Math.Max(ground.At(x - rx * half, y - ry * half), ground.At(x - rx * far, y - ry * far)));
            }
            return line;
        }

        /// <summary>The highest ground under either rope's chairs at <paramref name="s"/> (the nearest sample).</summary>
        public double UnderChairs(double s)
        {
            int j = Math.Max(0, Math.Min(Count - 1, (int)Math.Round(s)));
            return Math.Max(UnderUp[j], UnderDown[j]);
        }
    }
}
