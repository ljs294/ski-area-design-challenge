using System;
using System.Threading.Tasks;
using UnityEngine;

namespace MountainPlanner.Presentation.Weather
{
    /// <summary>
    /// Task 4.06: the cloud field Weather.hlsl reads for the sky's clouds, their shadows on the ground, the deck's holes and
    /// the curtains. Made once (no asset, no LFS), on a worker thread so opening the mountain doesn't hitch. R: cumulus cells,
    /// Perlin noise dilated by inverted Worley cells (round, billowing heads with wispy edges), domain-warped, then flattened to
    /// an even histogram so a cover of c clouds a share c of the sky. G: an independent finer field that erodes the edges
    /// (Weather.hlsl turns its tile 37° so the two never line up). Both tile over the unit square.
    /// </summary>
    public static class CloudField
    {
        public const int Size = 512;

        static Task<byte[]> _baking;
        static Texture2D _texture;

        /// <summary>The cells' values (R, 0–1, flattened), row by row; pure, so a test can check the histogram.</summary>
        public static float[] Values(int size = Size) => Channels(size).Item1;

        /// <summary>Both channels (R cells, G detail), flattened, row by row.</summary>
        public static (float[], float[]) Channels(int size)
        {
            var r = new float[size * size];
            var g = new float[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, w = y / (float)size;
                    // Warp the lookup by a coarse field of its own, so the cells billow instead of sitting on a grid.
                    float wx = Perlin(u, w, 3, 2, 11), wy = Perlin(u + 0.37f, w + 0.71f, 3, 2, 12);
                    float pu = u + wx * 0.06f, pw = w + wy * 0.06f;
                    float perlin = Perlin(pu, pw, 4, 5, 1) * 0.5f + 0.5f;
                    float cells = 1 - (0.625f * Worley(pu, pw, 5, 2) + 0.25f * Worley(pu, pw, 10, 3) + 0.125f * Worley(pu, pw, 20, 4));
                    // Perlin-Worley: the noise dilated by the cells, so heads are round and edges stay wispy.
                    r[y * size + x] = (perlin - (cells - 1)) / (2 - cells);
                    g[y * size + x] = 0.6f * (1 - Worley(u, w, 12, 7)) + 0.4f * (Perlin(u, w, 24, 2, 8) * 0.5f + 0.5f);
                }
            Flatten(r);
            Flatten(g);
            return (r, g);
        }

        /// <summary>Starts the bake on a worker thread (once).</summary>
        public static void Bake()
        {
            if (_baking != null) return;
            _baking = Task.Run(() =>
            {
                var (r, g) = Channels(Size);
                var bytes = new byte[r.Length * 2];
                for (int i = 0; i < r.Length; i++)
                {
                    bytes[i * 2] = (byte)Math.Clamp((int)Math.Round(r[i] * 255), 0, 255);
                    bytes[i * 2 + 1] = (byte)Math.Clamp((int)Math.Round(g[i] * 255), 0, 255);
                }
                return bytes;
            });
        }

        /// <summary>The texture once the bake is done (null until then): one, shared by everything that reads the field (made
        /// again only if its owner destroyed it). Call on the main thread.</summary>
        public static Texture2D TryMake()
        {
            if (_texture != null) return _texture;
            Bake();
            if (!_baking.IsCompleted) return null;
            var t = new Texture2D(Size, Size, TextureFormat.RG16, true, true)
            {
                name = "Cloud field", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear,
                hideFlags = HideFlags.DontSave,
            };
            t.SetPixelData(_baking.Result, 0);
            t.Apply(true, true);
            _texture = t;
            return t;
        }

        /// <summary>Replaces each value by its rank (0–1), so the histogram is even.</summary>
        static void Flatten(float[] v)
        {
            var idx = new int[v.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;
            var keys = (float[])v.Clone();
            Array.Sort(keys, idx);
            for (int r = 0; r < idx.Length; r++) v[idx[r]] = r / (float)(idx.Length - 1);
        }

        /// <summary>Gradient noise that tiles over the unit square (about −0.7 to 0.7), octaves from <paramref name="period"/> cells.</summary>
        static float Perlin(float u, float w, int period, int octaves, int salt)
        {
            float sum = 0, amp = 1, norm = 0;
            for (int o = 0; o < octaves; o++)
            {
                sum += amp * Gradient(u * period, w * period, period, salt + o * 31);
                norm += amp;
                amp *= 0.5f;
                period *= 2;
            }
            return sum / norm;
        }

        static float Gradient(float x, float y, int period, int salt)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            float a = Dot(x0, y0, fx, fy, period, salt), b = Dot(x0 + 1, y0, fx - 1, fy, period, salt);
            float c = Dot(x0, y0 + 1, fx, fy - 1, period, salt), d = Dot(x0 + 1, y0 + 1, fx - 1, fy - 1, period, salt);
            float sx = Fade(fx), sy = Fade(fy);
            return Lerp(Lerp(a, b, sx), Lerp(c, d, sx), sy);
        }

        static float Dot(int x, int y, float dx, float dy, int period, int salt)
        {
            float angle = Hash(x, y, period, salt) * 6.2831853f;
            return (float)Math.Cos(angle) * dx + (float)Math.Sin(angle) * dy;
        }

        /// <summary>Distance to the nearest feature point (0–1), one point per cell, tiling with <paramref name="period"/> cells.</summary>
        static float Worley(float u, float w, int period, int salt)
        {
            float x = u * period, y = w * period;
            int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
            float best = 4;
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    int nx = cx + i, ny = cy + j;
                    float px = nx + Hash(nx, ny, period, salt), py = ny + Hash(nx, ny, period, salt + 97);
                    float dx = px - x, dy = py - y;
                    best = Math.Min(best, dx * dx + dy * dy);
                }
            return Math.Min(1, (float)Math.Sqrt(best));
        }

        static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        static float Hash(int x, int y, int period, int salt)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
            uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ (uint)(salt + 1) * 0xCB1AB31Fu;
            h = (h ^ (h >> 15)) * 0x2C1B3C6Du;
            h = (h ^ (h >> 12)) * 0x297A2D39u;
            return (h >> 8) / 16777215f;
        }
    }
}
