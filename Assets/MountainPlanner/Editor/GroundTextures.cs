using System;
using System.IO;
using MountainPlanner.Domain.Cover;
using UnityEditor;
using UnityEngine;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Generates the terrain's ground textures (0.5 §2, style tile): tileable procedural albedo with a
    /// height map in alpha, plus matching normal maps, for the six layers in terrain-slot order (snow,
    /// forest floor, grass, rock, developed, ice). Saved as two Texture2DArray assets the terrain shader
    /// samples. Deterministic (keyed hash noise), so regenerating gives identical textures.
    /// </summary>
    public static class GroundTextures
    {
        public const int Size = 512;
        public const string AlbedoPath = "Assets/MountainPlanner/Art/Terrain/GroundAlbedo.asset";
        public const string NormalPath = "Assets/MountainPlanner/Art/Terrain/GroundNormals.asset";
        public const string PreviewPath = "Assets/MountainPlanner/Art/Terrain/GroundPreview.png";
        public static readonly string[] Names = { "Snow", "Forest floor", "Grass", "Rock", "Developed", "Ice" };
        /// <summary>Normal-map strength per layer.</summary>
        static readonly float[] Bump = { 0.7f, 3f, 2.5f, 5f, 2f, 1.5f };

        [MenuItem("Mountain Planner/Generate Ground Textures")]
        public static void Generate()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AlbedoPath));
            var albedo = new Texture2DArray(Size, Size, Names.Length, TextureFormat.RGBA32, true, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8, name = "GroundAlbedo" };
            var normals = new Texture2DArray(Size, Size, Names.Length, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8, name = "GroundNormals" };
            var preview = new Texture2D(Size * Names.Length / 2, Size, TextureFormat.RGBA32, false);
            for (int layer = 0; layer < Names.Length; layer++)
            {
                var colour = new Color32[Size * Size];
                var height = new float[Size * Size];
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        var (c, h) = Sample(layer, x, y);
                        height[y * Size + x] = h;
                        colour[y * Size + x] = new Color(c.r, c.g, c.b, h);
                    }
                albedo.SetPixels32(colour, layer, 0);
                normals.SetPixels32(NormalsFrom(height, Bump[layer]), layer, 0);
                for (int y = 0; y < Size; y += 2)
                    for (int x = 0; x < Size; x += 2)
                        preview.SetPixel(layer * Size / 2 + x / 2, y / 2 + Size / 4, colour[y * Size + x]);
            }
            albedo.Apply(true, false);
            normals.Apply(true, false);
            Save(albedo, AlbedoPath);
            Save(normals, NormalPath);
            File.WriteAllBytes(PreviewPath, preview.EncodeToPNG());
            AssetDatabase.ImportAsset(PreviewPath);
            Debug.Log($"[GroundTextures] {Names.Length} layers at {Size}² → {AlbedoPath}, {NormalPath}");
        }

        static void Save(UnityEngine.Object asset, string path)
        {
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
        }

        // ---- the six looks ---------------------------------------------------------------------

        static (Color Colour, float Height) Sample(int layer, int x, int y)
        {
            float u = (float)x / Size, v = (float)y / Size;
            switch (layer)
            {
                case 0: // snow: bright, cool; soft wind ripples and fine grain (0.5 §2)
                {
                    // Wind ripples, strongly warped and faded in patches, so they never read as regular stripes.
                    float ripple = 0.5f + 0.5f * Mathf.Sin((v * 9 + 2.2f * Fbm(u, v, 4, 3, 11)) * 2 * Mathf.PI);
                    ripple *= Mathf.SmoothStep(0.35f, 0.75f, Fbm(u, v, 3, 2, 14));
                    float h = 0.45f + 0.3f * Fbm(u, v, 6, 4, 12) + 0.08f * ripple + 0.06f * Grain(x, y, 13);
                    var shade = new Color(0.84f, 0.88f, 0.94f);
                    var lit = new Color(0.965f, 0.975f, 0.985f);
                    return (Color.Lerp(shade, lit, 0.55f + 0.45f * h), Mathf.Clamp01(h));
                }
                case 1: // forest floor: dark umber needles and soil, twigs
                {
                    float soil = Fbm(u, v, 8, 4, 21);
                    float needles = Strokes(u, v, 64, 0.35f, 22);
                    float h = 0.3f + 0.35f * soil + 0.35f * needles;
                    var c = Color.Lerp(new Color(0.16f, 0.12f, 0.08f), new Color(0.3f, 0.23f, 0.15f), soil);
                    c = Color.Lerp(c, new Color(0.42f, 0.3f, 0.18f), needles * 0.7f);
                    return (c, Mathf.Clamp01(h));
                }
                case 2: // grass / meadow: muted, winter-dormant straw with blade streaks
                {
                    float streak = 0.65f * Streaks(u, v, 64, 6, 31) + 0.35f * Streaks(u, v, 128, 12, 33);
                    float patch = Fbm(u, v, 5, 3, 32);
                    float h = 0.35f + 0.45f * streak + 0.2f * patch;
                    var c = Color.Lerp(new Color(0.46f, 0.43f, 0.28f), new Color(0.64f, 0.6f, 0.4f), streak);
                    c = Color.Lerp(c, new Color(0.42f, 0.45f, 0.3f), patch * 0.4f);
                    return (c, Mathf.Clamp01(h));
                }
                case 3: // rock / alpine: cool grey granite with a strata hint, speckles and cracks (#6E6A66)
                {
                    float body = Fbm(u, v, 6, 5, 41);
                    float strata = 0.5f + 0.5f * Mathf.Sin((v * 7 + 0.6f * Fbm(u, v, 3, 2, 42)) * 2 * Mathf.PI);
                    float cracks = Ridged(u, v, 8, 43);
                    float speck = Grain(x, y, 44);
                    float h = 0.25f + 0.45f * body + 0.2f * strata - 0.25f * cracks + 0.05f * speck;
                    var c = Color.Lerp(new Color(0.34f, 0.33f, 0.32f), new Color(0.5f, 0.49f, 0.47f), body);
                    c = Color.Lerp(c, new Color(0.56f, 0.52f, 0.48f), strata * 0.25f);
                    c *= 1 - 0.35f * cracks;
                    c += new Color(0.06f, 0.06f, 0.06f) * (speck - 0.5f);
                    return (c, Mathf.Clamp01(h));
                }
                case 4: // developed: neutral grey-brown, a packed gravel / paved texture
                {
                    float gravel = Grain(x, y, 51) * 0.6f + Fbm(u, v, 32, 2, 52) * 0.4f;
                    float wear = Fbm(u, v, 4, 3, 53);
                    var c = Color.Lerp(new Color(0.38f, 0.36f, 0.33f), new Color(0.52f, 0.49f, 0.45f), gravel);
                    c = Color.Lerp(c, new Color(0.44f, 0.41f, 0.37f), wear * 0.5f);
                    return (c, Mathf.Clamp01(0.3f + 0.5f * gravel));
                }
                default: // ice: frozen lake, blue-grey with pale pressure cracks
                {
                    float cracks = Ridged(u, v, 5, 61);
                    float cloud = Fbm(u, v, 3, 3, 62);
                    var c = Color.Lerp(new Color(0.55f, 0.66f, 0.72f), new Color(0.7f, 0.79f, 0.84f), cloud);
                    c = Color.Lerp(c, new Color(0.9f, 0.94f, 0.97f), cracks * 0.8f);
                    return (c, Mathf.Clamp01(0.2f + 0.15f * cloud + 0.2f * cracks));
                }
            }
        }

        // ---- tileable noise (keyed hash; the period divides the texture) --------------------------

        static float Lattice(int x, int y, int period, int salt) =>
            (float)((CoverNoise.Lattice((ulong)salt * 0x9E3779B97F4A7C15UL, ((x % period) + period) % period, ((y % period) + period) % period) + 1) * 0.5);

        static float Value(float u, float v, int period, int salt)
        {
            float x = u * period, y = v * period;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float tx = x - x0, ty = y - y0;
            tx = tx * tx * (3 - 2 * tx);
            ty = ty * ty * (3 - 2 * ty);
            float a = Mathf.Lerp(Lattice(x0, y0, period, salt), Lattice(x0 + 1, y0, period, salt), tx);
            float b = Mathf.Lerp(Lattice(x0, y0 + 1, period, salt), Lattice(x0 + 1, y0 + 1, period, salt), tx);
            return Mathf.Lerp(a, b, ty);
        }

        static float Fbm(float u, float v, int period, int octaves, int salt)
        {
            float sum = 0, amp = 0.5f, norm = 0;
            for (int o = 0; o < octaves; o++, period *= 2, amp *= 0.5f)
            {
                sum += amp * Value(u, v, Math.Max(1, period), salt + o * 7);
                norm += amp;
            }
            return sum / norm;
        }

        /// <summary>Anisotropic value noise: fine across (x), long along (y), like grass blades. Tileable.</summary>
        static float Streaks(float u, float v, int periodX, int periodY, int salt)
        {
            float x = u * periodX, y = v * periodY;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float tx = x - x0, ty = y - y0;
            ty = ty * ty * (3 - 2 * ty);
            float L(int i, int j) => (float)((CoverNoise.Lattice((ulong)salt * 0x9E3779B97F4A7C15UL, ((i % periodX) + periodX) % periodX, ((j % periodY) + periodY) % periodY) + 1) * 0.5);
            float a = Mathf.Lerp(L(x0, y0), L(x0 + 1, y0), tx), b = Mathf.Lerp(L(x0, y0 + 1), L(x0 + 1, y0 + 1), tx);
            return Mathf.Lerp(a, b, ty);
        }

        /// <summary>Thin crack lines where noise crosses 0.5.</summary>
        static float Ridged(float u, float v, int period, int salt)
        {
            float n = Fbm(u, v, period, 4, salt);
            return Mathf.Pow(Mathf.Clamp01(1 - Mathf.Abs(n - 0.5f) * 14), 2);
        }

        static float Grain(int x, int y, int salt) => Lattice(x, y, Size, salt);

        /// <summary>Short random strokes (needles, twigs) in a tileable grid of cells.</summary>
        static float Strokes(float u, float v, int cells, float density, int salt)
        {
            float best = 0;
            int cx = Mathf.FloorToInt(u * cells), cy = Mathf.FloorToInt(v * cells);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int gx = cx + dx, gy = cy + dy;
                    if (Lattice(gx, gy, cells, salt) > density) continue;
                    float px = (gx + Lattice(gx, gy, cells, salt + 1)) / cells, py = (gy + Lattice(gx, gy, cells, salt + 2)) / cells;
                    float angle = Lattice(gx, gy, cells, salt + 3) * Mathf.PI;
                    float len = 0.6f / cells * (0.6f + Lattice(gx, gy, cells, salt + 4));
                    var d = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    var p = new Vector2(u - px, v - py);
                    float t = Mathf.Clamp(Vector2.Dot(p, d), -len, len);
                    float dist = (p - d * t).magnitude * cells;
                    best = Mathf.Max(best, Mathf.Clamp01(1 - dist * 12));
                }
            return best;
        }

        static Color32[] NormalsFrom(float[] height, float strength)
        {
            var result = new Color32[height.Length];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    float l = height[y * Size + (x + Size - 1) % Size], r = height[y * Size + (x + 1) % Size];
                    float d = height[((y + Size - 1) % Size) * Size + x], u = height[((y + 1) % Size) * Size + x];
                    var n = new Vector3((l - r) * strength, (d - u) * strength, 1).normalized;
                    result[y * Size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1);
                }
            return result;
        }
    }
}
