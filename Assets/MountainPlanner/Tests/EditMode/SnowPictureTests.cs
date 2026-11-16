using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A2 (SN-29 v2, owner 2026-11-13: the snow map is fluid and smooth, never jagged): the snow drawn through the
    /// real Snow.hlsl. A metre-deep step between two cells and a checkerboard of two types, eight pixels a cell: no two
    /// neighbouring pixels differ by more than a small share of the step (a hard edge would be all of it in one pixel).
    /// </summary>
    public sealed class SnowPictureTests
    {
        const int Cells = 16, PixelsPerCell = 8, Size = Cells * PixelsPerCell;
        static readonly int ProbeRect = Shader.PropertyToID("_ProbeRect");

        static Color[] Draw(SurfaceStates states, int pass)
        {
            var shader = Shader.Find("Hidden/MountainPlanner/Tests/SnowProbe");
            Assert.That(shader, Is.Not.Null, "the probe shader");
            var material = new Material(shader);
            var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var read = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true);
            var was = RenderTexture.active;
            try
            {
                // The window: cells 8 to 24 of the map, away from its edges.
                float m = SurfaceStates.CellMetres;
                material.SetVector(ProbeRect, new Vector4(8 * m, 8 * m, Cells * m, Cells * m));
                Graphics.Blit(null, rt, material, pass);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                read.Apply();
                return read.GetPixels();
            }
            finally
            {
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(read);
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>The largest change between neighbouring pixels, across and down, in one channel (or the colour's largest).</summary>
        static float Largest(Color[] px, System.Func<Color, Color, float> diff)
        {
            float most = 0;
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    var c = px[y * Size + x];
                    if (x + 1 < Size) most = Mathf.Max(most, diff(c, px[y * Size + x + 1]));
                    if (y + 1 < Size) most = Mathf.Max(most, diff(c, px[(y + 1) * Size + x]));
                }
            return most;
        }

        static void SkipWithoutAGpu()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device (not -nographics)");
        }

        [Test]
        public void AMetreDeepStepDrawsAsACurve()
        {
            SkipWithoutAGpu();
            var states = new SurfaceStates(new Rect(0, 0, 32 * SurfaceStates.CellMetres, 32 * SurfaceStates.CellMetres));
            try
            {
                // Bare to the west of column 16, a metre deep from it on.
                const int w = 32, h = 32;
                var mm = new ushort[w * h];
                var surface = new byte[w * h * 4];
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        mm[j * w + i] = (ushort)(i >= 16 ? 1000 : 0);
                        surface[(j * w + i) * 4] = (byte)(i >= 16 ? 1 : 0);
                    }
                states.TakeTile(0, 0, w, h, mm, 0, surface, 0);
                for (int f = 0; f < 8 && states.TilesWaiting > 0; f++) states.Sync();
                var px = Draw(states, 0);
                float low = float.MaxValue, high = 0;
                foreach (var c in px) { low = Mathf.Min(low, c.r); high = Mathf.Max(high, c.r); }
                float depthStep = Largest(px, (a, b) => Mathf.Abs(a.r - b.r));
                float coverStep = Largest(px, (a, b) => Mathf.Abs(a.g - b.g));
                Debug.Log($"[picture] a 1 m step: {low:F3} to {high:F3} m drawn; the largest pixel-to-pixel change " +
                                               $"{depthStep:F3} m, the cover's {coverStep:F3}");
                Assert.That(low, Is.LessThan(0.01f), "bare ground drawn");
                Assert.That(high, Is.GreaterThan(0.99f), "the metre drawn");
                // The B-spline's steepest slope across a step is half of it a cell: 1/16 of it a pixel at eight a cell.
                Assert.That(depthStep, Is.LessThan(0.1f), "no hard edge in the depth");
                Assert.That(coverStep, Is.LessThan(0.6f), "the cover ramps over the first 6 in, never in one pixel");
            }
            finally { states.Dispose(); }
        }

        [Test]
        public void ACheckerboardOfTwoTypesBlendsAcrossEveryBorder()
        {
            SkipWithoutAGpu();
            var states = new SurfaceStates(new Rect(0, 0, 32 * SurfaceStates.CellMetres, 32 * SurfaceStates.CellMetres));
            try
            {
                // Powder (blue) and icy (violet) in 2 × 2-cell squares, all of it snowy.
                const int w = 32, h = 32;
                var mm = new ushort[w * h];
                var surface = new byte[w * h * 4];
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        mm[j * w + i] = 500;
                        surface[(j * w + i) * 4] = (byte)(((i / 2) + (j / 2)) % 2 == 0 ? 1 : 5);
                    }
                states.TakeTile(0, 0, w, h, mm, 0, surface, 0);
                for (int f = 0; f < 8 && states.TilesWaiting > 0; f++) states.Sync();
                var px = Draw(states, 1);
                float colourStep = Largest(px, (a, b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b))));
                // Powder #5AA2EC against icy #6E2F88: a hard border would change a channel by up to 0.45 in one pixel.
                Debug.Log($"[picture] powder and icy in 16 m squares: the largest pixel-to-pixel change {colourStep:F3} " +
                                               "(a hard border: 0.451)");
                Assert.That(colourStep, Is.LessThan(0.06f), "the types blend across their borders");
            }
            finally { states.Dispose(); }
        }

        // Owner's demo 111 (2026-11-15): Snow conditions showed wet granular where the ground looked bare. The layer fades each
        // type to bare by the ground's own cover, so a thaw's last few mm draw as bare ground and a deep pack keeps its colour.
        [Test]
        public void AThawsLastMillimetresDrawAsBareGround()
        {
            SkipWithoutAGpu();
            var states = new SurfaceStates(new Rect(0, 0, 32 * SurfaceStates.CellMetres, 32 * SurfaceStates.CellMetres));
            try
            {
                // Wet granular everywhere: 3 mm to the west of column 16, a metre from it on.
                const int w = 32, h = 32;
                var mm = new ushort[w * h];
                var surface = new byte[w * h * 4];
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        mm[j * w + i] = (ushort)(i >= 16 ? 1000 : 3);
                        surface[(j * w + i) * 4] = 9;
                    }
                states.TakeTile(0, 0, w, h, mm, 0, surface, 0);
                for (int f = 0; f < 8 && states.TilesWaiting > 0; f++) states.Sync();
                var px = Draw(states, 1);
                var bare = InfoLegend.SnowTypeColours[0];
                var wet = InfoLegend.SnowTypeColours[9];
                float Off(Color a, Color b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
                // Columns 8 cells inside each side (window cells 8 to 24: the thin side is window x < 8 cells).
                float thin = 0, deep = 0;
                for (int y = 0; y < Size; y++)
                {
                    thin = Mathf.Max(thin, Off(px[y * Size + 2 * PixelsPerCell], bare));
                    deep = Mathf.Max(deep, Off(px[y * Size + 13 * PixelsPerCell], wet));
                }
                Debug.Log($"[picture] wet granular 3 mm: {thin:F3} off bare; a metre: {deep:F3} off wet granular");
                Assert.That(thin, Is.LessThan(0.02f), "3 mm of wet granular draws as bare ground");
                Assert.That(deep, Is.LessThan(0.02f), "a metre of it keeps its colour");
            }
            finally { states.Dispose(); }
        }
    }
}
