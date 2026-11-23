using System;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Simulation;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.08 PR A: the fine trail snow on the screen. A world with one groomed run and a 26 ft machine made whale painted
    /// on it (<see cref="SimCommandKind.PaintSnow"/>) stages its 8 m and fine tiles as the game's host does; the screen's
    /// <see cref="FineSnowAtlas"/> takes them. The shader's lift (the real Snow.hlsl, through the probe) matches its CPU twin
    /// and stands the whale within a foot of its 26 ft; the page table and states follow the staged tiles; the skiers'
    /// placement job stands on the same surface.
    /// </summary>
    public sealed class FineSnowAtlasTests
    {
        static readonly int ProbeRect = Shader.PropertyToID("_ProbeRect");
        const int Size = 128;
        const float WhaleX = 150, WhaleZ = 0;

        static Rect Ring => new Rect(TrailSnowWorldTests.Origin / 1000f, TrailSnowWorldTests.Origin / 1000f,
                                     TrailSnowWorldTests.W * SurfaceStates.CellMetres, TrailSnowWorldTests.D * SurfaceStates.CellMetres);

        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        /// <summary>The painted world's snow, staged and drained into a new <see cref="SurfaceStates"/> as the game does.</summary>
        static SurfaceStates Painted(out SimWorld world, out TrailSnowLayout layout)
        {
            layout = TrailSnowWorldTests.Layout(TrailSnowWorldTests.Run(11, 150_000, neverGroom: false));
            var w = TrailSnowWorldTests.World(600, layout);
            w.JoinSnow();
            w.ApplyNow(SimCommand.Of(SimCommandKind.PaintSnow, 0, SnowLookSimTests.Pile(w, WhaleX, WhaleZ, 10, 12)));
            Assert.That(w.PaintedCells, Is.GreaterThan(0), "the whale lands on the run");
            world = w;
            var states = new SurfaceStates(Ring);
            // The 8 m field: every tile the worker stages, into the screen's mirror.
            var ring = new TileStagingRing();
            for (int k = 0; k < 400; k++)
            {
                int n = w.StageSnow(ring, true);
                for (long t = ring.Released; t < ring.Written; t++)
                {
                    ring.Read(t, out int x, out int y, out int tw, out int th, out int offset, out ushort[] cells);
                    ring.ReadSurface(t, out int so, out byte[] surface);
                    states.TakeTile(x, y, tw, th, cells, offset, surface, so);
                }
                ring.Release(ring.Written);
                if (n == 0 && k > 4) break;
            }
            // The fine tiles, into the atlas bound to the layout sent.
            var fine = new FineStagingRing(w.FineTiles.Count + 1);
            int staged = 0;
            for (int k = 0; k < 1000 && staged < w.FineTiles.Count; k++) staged += w.StageFine(fine);
            Assert.That(states.Fine.Bind(layout), Is.True, "the layout is the snow map's grid");
            for (long t = 0; t < fine.Written; t++)
            {
                fine.Read(t, out int listed, out int size, out ulong hash, out int offset, out ushort[] depth, out byte[] state);
                states.Fine.Take(listed, size, hash, depth, state, offset);
            }
            fine.Release(fine.Written);
            for (int f = 0; f < 400 && (states.TilesWaiting > 0 || states.Fine.SlotsWaiting > 0); f++) states.Sync();
            states.Sync();
            Assert.That(states.LiftOn, Is.True, "the first full pass switched the lift on");
            Assert.That(states.Fine.SlotsWaiting, Is.Zero, "every fine slot went up");
            return states;
        }

        /// <summary>The probe's pass over a window (local metres: west, south, size).</summary>
        static Color[] Draw(int pass, float west, float south, float metres)
        {
            var shader = Shader.Find("Hidden/MountainPlanner/Tests/SnowProbe");
            Assert.That(shader, Is.Not.Null, "the probe shader");
            var material = new Material(shader);
            var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var read = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true);
            var was = RenderTexture.active;
            try
            {
                material.SetVector(ProbeRect, new Vector4(west, south, metres, metres));
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
                UnityEngine.Object.DestroyImmediate(read);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void TheWhaleDrawsWhereTheCursorSaysWithinAFootOfItsHeight()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device (not -nographics)");
            var states = Painted(out _, out var layout);
            try
            {
                var lift = Draw(2, WhaleX - 24, WhaleZ - 24, 48);
                var where = Draw(3, WhaleX - 24, WhaleZ - 24, 48);
                float worst = 0, worstSlope = 0, top = 0, cpuTop = 0;
                int over = 0;
                var worstAt = Vector4.zero;
                for (int k = 0; k < lift.Length; k++)
                {
                    float x = where[k].r, z = where[k].g;
                    float cpu = states.LiftAt(x, z);
                    float e = Mathf.Abs(lift[k].r - cpu) / (1 + cpu);
                    if (e > worst) { worst = e; worstAt = new Vector4(x, z, lift[k].r, cpu); }
                    if (e > 0.01f) over++;
                    top = Mathf.Max(top, lift[k].r);
                    cpuTop = Mathf.Max(cpuTop, cpu);
                    const float h = 0.05f;
                    float sx = (states.LiftAt(x + h, z) - states.LiftAt(x - h, z)) / (2 * h);
                    float sz = (states.LiftAt(x, z + h) - states.LiftAt(x, z - h)) / (2 * h);
                    worstSlope = Mathf.Max(worstSlope, Mathf.Max(Mathf.Abs(lift[k].g - sx), Mathf.Abs(lift[k].b - sz)));
                }
                float cap = Pacing.Default.WhaleMaxMm / 1000f;
                Say($"[fine lift] {layout.FineMetres} m cells; whale drawn to {top:F2} m ({top / 0.3048f:F1} ft), CPU {cpuTop:F2} m, cap {cap:F2} m; " +
                    $"GPU against C#: {worst * 100:F2} cm per (1 m + lift), slope {worstSlope:F4}; worst at ({worstAt.x:F2}, {worstAt.y:F2}) GPU {worstAt.z:F3} C# {worstAt.w:F3}; {over} of {lift.Length} pixels over 1 cm");
                Assert.That(top, Is.GreaterThan(cap - 0.3048f), "the 26 ft whale draws within a foot of 26 ft");
                Assert.That(top, Is.LessThanOrEqualTo(cap + 0.6f + 0.05f), "and not over it (the seed's 0.6 m under it)");
                Assert.That(worst, Is.LessThan(0.01f), "the GPU's lift is the cursor's, within 1 cm per metre");
                Assert.That(worstSlope, Is.LessThan(0.02f), "and its slope, within 2 %");
            }
            finally { states.Dispose(); }
        }

        [Test]
        public void ThePagesAndStatesFollowTheStagedTiles()
        {
            var states = Painted(out var world, out var layout);
            try
            {
                var atlas = states.Fine;
                var fine = world.FineSnow;
                Assert.That(atlas.TilesTaken, Is.EqualTo(world.FineTiles.Count), "every tile taken once");
                int per = layout.PerSide, bc = layout.BlockCells, checkedCells = 0;
                float cell = layout.FineMetres;
                for (int b = 0; b < layout.Blocks; b += 7)
                {
                    int c = layout.BlockCell[b];
                    int ci = c % layout.GridWidth, cj = c / layout.GridWidth;
                    for (int q = 0; q < bc; q += 5)
                    {
                        int f = b * bc + q;
                        float x = Ring.xMin + ci * SurfaceStates.CellMetres + (q % per + 0.5f) * cell;
                        float z = Ring.yMin + cj * SurfaceStates.CellMetres + (q / per + 0.5f) * cell;
                        var st = atlas.StateAt(x, z);
                        Assert.That(st.Type, Is.EqualTo(fine.Type[f] & FineStagingRing.TypeMask), "the type under each fine cell");
                        Assert.That(st.Moguls, Is.EqualTo(fine.Moguls[f]));
                        checkedCells++;
                    }
                }
                Assert.That(atlas.StateAt(WhaleX, WhaleZ).Type, Is.EqualTo((int)SnowType.MM), "machine made under the whale");
                // Off the run (its 30 m are round x = 150 m): no block, the 8 m snow alone.
                float offX = WhaleX - 120;
                Assert.That(atlas.StateAt(offX, WhaleZ).Type, Is.EqualTo(-1), "no fine cell off the trails");
                float l8 = 0.6f;
                Assert.That(atlas.LiftAt(offX, WhaleZ, l8, 1), Is.EqualTo(l8), "the 8 m lift unchanged there");
                Say($"[pages] {atlas.TilesTaken} tiles of {atlas.Tiles.TileFine} × {atlas.Tiles.TileFine}; {checkedCells} cells checked");
            }
            finally { states.Dispose(); }
        }

        [Test]
        public void TheSkiersStandOnTheDrawnFineSnow()
        {
            var states = Painted(out _, out _);
            var atlas = states.Fine;
            var tiles = atlas.Tiles;
            int T = tiles.TileFine;
            var snow = new NativeArray<float>(states.Lift, Allocator.TempJob);
            var fineLift = new NativeArray<float>(tiles.Count * T * T, Allocator.TempJob);
            var page = new NativeArray<int>(tiles.Slot, Allocator.TempJob);
            try
            {
                for (int s = 0; s < tiles.Count; s++) atlas.CopySlotLift(s, fineLift, s * T * T);
                var job = new SkierPlaceJob
                {
                    Snow = snow, SnowScale = states.LiftScale, SnowOrigin = new float2(Ring.xMin, Ring.yMin), SnowCell = SurfaceStates.CellMetres,
                    SnowW = states.Snow.Width, SnowH = states.Snow.Height,
                    FineLift = fineLift, FinePage = page, FineT = T, FineTilesX = tiles.TilesX, FineTilesY = tiles.TilesY, FineMetres = atlas.FineMetres,
                };
                float worst = 0;
                for (float z = WhaleZ - 30; z <= WhaleZ + 30; z += 0.7f)
                    for (float x = WhaleX - 30; x <= WhaleX + 30; x += 0.7f)
                        worst = Mathf.Max(worst, Mathf.Abs(job.GroundLift(x, z) * states.LiftScale - states.LiftAt(x, z)));
                Say($"[skiers] the placement job against the drawn lift: {worst * 1000:F1} mm at worst");
                Assert.That(worst, Is.LessThan(0.005f), "a skier stands on the drawn whale, within 5 mm");
            }
            finally
            {
                snow.Dispose(); fineLift.Dispose(); page.Dispose();
                states.Dispose();
            }
        }
    }
}
