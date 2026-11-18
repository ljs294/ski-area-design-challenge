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
            return Smooth(field.Cells, field.Width, field.Height, field.CellMetres, ring, x, z);
        }

        /// <summary>
        /// Task 4.07 PR A3: how far the ground is lifted at local x, z (metres), as the terrain shader lifts it: the same curve
        /// over <see cref="SurfaceStates.Lift"/>, × <see cref="SurfaceStates.LiftScale"/>. 0 while the lift is off, and off the field.
        /// </summary>
        public static float LiftAt(SurfaceStates states, Rect ring, float x, float z)
        {
            if (states == null || !states.LiftOn) return 0;
            var field = states.Snow;
            float d = Smooth(states.Lift, field.Width, field.Height, field.CellMetres, ring, x, z);
            return float.IsNaN(d) ? 0 : d * states.LiftScale;
        }

        /// <summary>The cubic B-spline over a field's cells (clamped at its edges, as the textures are), or NaN past them.</summary>
        static float Smooth(float[] cells, int width, int height, float cellMetres, Rect ring, float x, float z)
        {
            float s = (x - ring.xMin) / cellMetres - 0.5f, t = (z - ring.yMin) / cellMetres - 0.5f;
            if (s < -1 || t < -1 || s > width || t > height) return float.NaN;
            int i = Mathf.FloorToInt(s), j = Mathf.FloorToInt(t);
            Weights(s - i, out float a0, out float a1, out float a2, out float a3);
            Weights(t - j, out float b0, out float b1, out float b2, out float b3);
            float Row(int y)
            {
                int r = Mathf.Clamp(y, 0, height - 1) * width;
                return a0 * cells[r + Mathf.Clamp(i - 1, 0, width - 1)] + a1 * cells[r + Mathf.Clamp(i, 0, width - 1)]
                     + a2 * cells[r + Mathf.Clamp(i + 1, 0, width - 1)] + a3 * cells[r + Mathf.Clamp(i + 2, 0, width - 1)];
            }
            return Mathf.Max(0, b0 * Row(j - 1) + b1 * Row(j) + b2 * Row(j + 1) + b3 * Row(j + 2));
        }

        /// <summary>The surface of the cell under local x, z: its type (SnowType's number), quality (‰), age (face hours) and
        /// density (kg/m³); type −1 off the field.</summary>
        public static (int Type, int Quality, int AgeHours, int Density) ConditionsAt(SurfaceStates states, Rect ring, float x, float z)
        {
            var field = states?.Snow;
            if (field == null) return (-1, 0, 0, 0);
            int i = Mathf.FloorToInt((x - ring.xMin) / field.CellMetres), j = Mathf.FloorToInt((z - ring.yMin) / field.CellMetres);
            if (i < 0 || j < 0 || i >= field.Width || j >= field.Height) return (-1, 0, 0, 0);
            var (type, quality, age, density) = states.ConditionsAt(i, j);
            return (type, quality * 4, age, density * 4);
        }

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
