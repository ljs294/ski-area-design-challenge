using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Task 4.07 PR A2: what the cursor reads of the snow, as the screen draws it. The depth goes through the same cubic
    /// B-spline over the 4 × 4 cells round the point as Snow.hlsl (SN-29 v2: the readout never disagrees with the picture);
    /// the surface (type, quality, age, density) is the cell under the point's.
    /// </summary>
    public static class SnowCursor
    {
        /// <summary>The snow's depth (metres) at local x, z through the curved filter, or NaN off the field.</summary>
        public static float DepthAt(SurfaceStates states, Rect ring, float x, float z)
        {
            var field = states?.Snow;
            if (field == null) return float.NaN;
            float s = (x - ring.xMin) / field.CellMetres - 0.5f, t = (z - ring.yMin) / field.CellMetres - 0.5f;
            if (s < -1 || t < -1 || s > field.Width || t > field.Height) return float.NaN;
            int i = Mathf.FloorToInt(s), j = Mathf.FloorToInt(t);
            Weights(s - i, out float a0, out float a1, out float a2, out float a3);
            Weights(t - j, out float b0, out float b1, out float b2, out float b3);
            float Row(int y) => a0 * Cell(field, i - 1, y) + a1 * Cell(field, i, y) + a2 * Cell(field, i + 1, y) + a3 * Cell(field, i + 2, y);
            return Mathf.Max(0, b0 * Row(j - 1) + b1 * Row(j) + b2 * Row(j + 1) + b3 * Row(j + 2));
        }

        /// <summary>The surface of the cell under local x, z: its type (SnowType's number), quality (‰), age (face hours) and
        /// density (kg/m³); type −1 off the field.</summary>
        public static (int Type, int Quality, int AgeHours, int Density) SurfaceAt(SurfaceStates states, Rect ring, float x, float z)
        {
            var field = states?.Snow;
            if (field == null) return (-1, 0, 0, 0);
            int i = Mathf.FloorToInt((x - ring.xMin) / field.CellMetres), j = Mathf.FloorToInt((z - ring.yMin) / field.CellMetres);
            if (i < 0 || j < 0 || i >= field.Width || j >= field.Height) return (-1, 0, 0, 0);
            var (type, quality, age, density) = states.SurfaceAt(i, j);
            return (type, quality * 4, age, density * 4);
        }

        // The texture's clamp: a cell past the edge reads the edge's.
        static float Cell(Domain.Snow.SnowDepthField f, int x, int y) => f[Mathf.Clamp(x, 0, f.Width - 1), Mathf.Clamp(y, 0, f.Height - 1)];

        /// <summary>The cubic B-spline's weights for a fraction past the second of four cells (Snow.hlsl's SnowSplineWeights).</summary>
        public static void Weights(float f, out float w0, out float w1, out float w2, out float w3)
        {
            float f2 = f * f, f3 = f2 * f;
            w0 = (1 - 3 * f + 3 * f2 - f3) / 6;
            w1 = (4 - 6 * f2 + 3 * f3) / 6;
            w2 = (1 + 3 * f + 3 * f2 - 3 * f3) / 6;
            w3 = f3 / 6;
        }
    }
}
