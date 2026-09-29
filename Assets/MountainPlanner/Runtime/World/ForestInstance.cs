using System.Runtime.InteropServices;
using MountainPlanner.Persistence;
using UnityEngine;

namespace MountainPlanner.World
{
    /// <summary>One tree as the GPU forest reads it (matches `Tree` in ForestCull.compute and TreeInstanced.shader).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ForestInstance
    {
        public Vector3 Position;    // trunk base on the ground, world space (models reach a metre below it, so they meet slopes)
        public float Rotation;      // radians about +Y
        public float HeightScale;
        public float WidthScale;
        public uint Prototype;
        public uint Pad;

        /// <summary>
        /// Decodes a tile's cached trees into world-space instances, standing each on the tile's own
        /// heights. Safe on worker threads.
        /// </summary>
        /// <param name="heights">The tile's normalised heights, as <see cref="TerrainTiles.LoadHeights"/> returns them ([z, x], south row first).</param>
        /// <param name="origin">The tile's south-west corner in the local frame, at height 0.</param>
        public static ForestInstance[] Decode(byte[] packed, float[] nativeHeights, float[,] heights, Vector3 origin,
                                              float tileMetres, float heightMin, float heightRange)
        {
            int n = packed.Length / ForestField.BytesPerTree, res = heights.GetLength(0);
            var result = new ForestInstance[n];
            for (int i = 0; i < n; i++)
            {
                ForestField.Decode(packed, i, out float fx, out float fz, out float height, out int prototype, out float rotation, out float width);
                if (prototype >= nativeHeights.Length) prototype %= nativeHeights.Length;
                float gx = fx * (res - 1), gz = fz * (res - 1);
                int x0 = Mathf.Min((int)gx, res - 2), z0 = Mathf.Min((int)gz, res - 2);
                float tx = gx - x0, tz = gz - z0;
                float h = Mathf.Lerp(Mathf.Lerp(heights[z0, x0], heights[z0, x0 + 1], tx), Mathf.Lerp(heights[z0 + 1, x0], heights[z0 + 1, x0 + 1], tx), tz);
                float scale = height / Mathf.Max(1, nativeHeights[prototype]);
                result[i] = new ForestInstance
                {
                    Position = new Vector3(origin.x + fx * tileMetres, heightMin + h * heightRange, origin.z + fz * tileMetres),
                    Rotation = rotation, HeightScale = scale, WidthScale = scale * width, Prototype = (uint)prototype,
                };
            }
            return result;
        }
    }
}
