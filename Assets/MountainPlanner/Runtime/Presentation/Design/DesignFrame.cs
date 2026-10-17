using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// Design millimetres are the area's local frame (x east, z north, in metres from the frame's origin, SH-03) times
    /// 1,000 (P3-06): the native grid's absolute coordinates would overflow the document's ±1,000 km range.
    /// </summary>
    public static class DesignFrame
    {
        public static PointMm ToDesign(Vector3 local) => PointMm.FromMetres(local.x, local.z);
        public static PointMm ToDesign(Vector2 localXz) => PointMm.FromMetres(localXz.x, localXz.y);
        public static Vector2 ToLocal(PointMm p) => new Vector2((float)p.XMetres, (float)p.YMetres);

        /// <summary>
        /// A brush stroke's two edges, half its width either side of its path (the mockup's dashed edge), into
        /// <paramref name="left"/> and <paramref name="right"/>, which must hold the stroke's points.
        /// </summary>
        public static void Edges(IReadOnlyList<PointMm> points, float halfWidthMetres, Vector2[] left, Vector2[] right)
        {
            int n = points.Count;
            for (int i = 0; i < n; i++)
            {
                var p = ToLocal(points[i]);
                var d = ToLocal(points[Mathf.Min(n - 1, i + 1)]) - ToLocal(points[Mathf.Max(0, i - 1)]);
                var normal = d.sqrMagnitude > 1e-6f ? new Vector2(-d.y, d.x).normalized : Vector2.up;
                left[i] = p + normal * halfWidthMetres;
                right[i] = p - normal * halfWidthMetres;
            }
        }
    }
}
