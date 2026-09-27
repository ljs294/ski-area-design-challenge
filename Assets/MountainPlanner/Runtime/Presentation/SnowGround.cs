using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// A placeholder snow ground layer until ground cover (task 07) and the style tile (task 08): an
    /// almost-white, cool-shaded texture generated at start-up, so the terrain reads as a snowy mountain.
    /// </summary>
    public static class SnowGround
    {
        public static TerrainLayer CreateLayer()
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "SnowPlaceholder", wrapMode = TextureWrapMode.Repeat };
            var lit = new Color(0.957f, 0.969f, 0.980f);   // snow lit, #F4F7FA (0.5 §5)
            var shade = new Color(0.86f, 0.89f, 0.93f);
            var pixels = new Color[size * size];
            var rng = new System.Random(7);
            for (int i = 0; i < pixels.Length; i++)
            {
                float n = (float)rng.NextDouble();
                pixels[i] = Color.Lerp(shade, lit, 0.75f + 0.25f * n);
            }
            tex.SetPixels(pixels);
            tex.Apply(true, true);
            return new TerrainLayer { diffuseTexture = tex, tileSize = new Vector2(6, 6), smoothness = 0.35f, name = "SnowPlaceholder" };
        }
    }
}
