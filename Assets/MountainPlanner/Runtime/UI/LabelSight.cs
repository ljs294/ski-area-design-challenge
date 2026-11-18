using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Whether the ground hides a label from the camera: the contour labels' test (task 12), shared with the landmark and
    /// run names (owner, 2026-10-20: "labels are visible through terrain").
    /// </summary>
    public static class LabelSight
    {
        /// <summary>True when the ground rises more than 2 m above the sight line from <paramref name="eye"/> to <paramref name="target"/> (16 samples).</summary>
        public static bool Hidden(Vector3 eye, Vector3 target, ITerrainSurface surface)
        {
            if (surface == null) return false;
            for (int k = 1; k < 16; k++)
            {
                var p = Vector3.Lerp(eye, target, k / 16f);
                float ground = surface.SurfaceAt(p.x, p.z);   // task 4.07 PR A3: the snow hides what's behind it too
                if (!float.IsNaN(ground) && ground > p.y + 2) return true;
            }
            return false;
        }
    }
}
