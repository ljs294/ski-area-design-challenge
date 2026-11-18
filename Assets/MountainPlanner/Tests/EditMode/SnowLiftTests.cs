using MountainPlanner.Domain.Water;
using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A3 (snow lifts the ground): the terrain shader's lift, drawn through the real Snow.hlsl, against its C#
    /// twin (<see cref="SnowCursor.LiftAt"/>) that the cursor, the skiers and the camera stand on; and where the ground doesn't
    /// lift: before the live snow's first full pass, on lakes and ponds, round lift stations and at the ring's edge.
    /// </summary>
    public sealed class SnowLiftTests
    {
        const int Cells = 48, Window = 16, PixelsPerCell = 8, Size = Window * PixelsPerCell;
        static readonly int ProbeRect = Shader.PropertyToID("_ProbeRect");
        static float M => SurfaceStates.CellMetres;
        static Rect Ring => new Rect(0, 0, Cells * M, Cells * M);

        static void SkipWithoutAGpu()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device (not -nographics)");
        }

        /// <summary>The probe's pass over a window of <see cref="Window"/> cells from cell (<paramref name="x0"/>, <paramref name="y0"/>).</summary>
        static Color[] Draw(int pass, int x0, int y0)
        {
            var shader = Shader.Find("Hidden/MountainPlanner/Tests/SnowProbe");
            Assert.That(shader, Is.Not.Null, "the probe shader");
            var material = new Material(shader);
            var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var read = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true);
            var was = RenderTexture.active;
            try
            {
                material.SetVector(ProbeRect, new Vector4(x0 * M, y0 * M, Window * M, Window * M));
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

        /// <summary>A field from the live snow's tiles (one tile covers it), so its first pass is done and the lift switches on.</summary>
        static SurfaceStates Live(System.Func<int, int, float> metres)
        {
            var states = new SurfaceStates(Ring);
            var mm = new ushort[Cells * Cells];
            for (int j = 0; j < Cells; j++)
                for (int i = 0; i < Cells; i++) mm[j * Cells + i] = (ushort)Mathf.RoundToInt(metres(i, j) * 1000);
            states.TakeTile(0, 0, Cells, Cells, mm, 0, new byte[Cells * Cells * 4], 0);
            for (int f = 0; f < 8 && states.TilesWaiting > 0; f++) states.Sync();
            states.Sync();
            return states;
        }

        /// <summary>A pile 4 m tall over a 1 m blanket, off centre so the slopes run both ways.</summary>
        static float Pile(int i, int j)
        {
            float dx = (i - 22.5f) / 5f, dy = (j - 25f) / 3f;
            return 1 + 4 * Mathf.Exp(-(dx * dx + dy * dy));
        }

        [Test]
        public void TheGroundLiftsWhereTheCursorSaysItDoes()
        {
            SkipWithoutAGpu();
            var states = Live(Pile);
            try
            {
                Assert.That(states.LiftOn, Is.True, "the first full pass switched the lift on");
                var lift = Draw(2, 16, 16);
                var where = Draw(3, 16, 16);
                float worst = 0, worstSlope = 0, top = 0;
                for (int k = 0; k < lift.Length; k++)
                {
                    float x = where[k].r, z = where[k].g;
                    float cpu = SnowCursor.LiftAt(states, Ring, x, z);
                    worst = Mathf.Max(worst, Mathf.Abs(lift[k].r - cpu) / (1 + cpu));
                    top = Mathf.Max(top, lift[k].r);
                    const float h = 0.05f;
                    float sx = (SnowCursor.LiftAt(states, Ring, x + h, z) - SnowCursor.LiftAt(states, Ring, x - h, z)) / (2 * h);
                    float sz = (SnowCursor.LiftAt(states, Ring, x, z + h) - SnowCursor.LiftAt(states, Ring, x, z - h)) / (2 * h);
                    worstSlope = Mathf.Max(worstSlope, Mathf.Max(Mathf.Abs(lift[k].g - sx), Mathf.Abs(lift[k].b - sz)));
                }
                Debug.Log($"[lift] the pile drawn to {top:F2} m; GPU against C#: {worst * 100:F2} cm per (1 m + lift), slope {worstSlope:F4}");
                Assert.That(top, Is.GreaterThan(3.5f), "the pile stands up");
                // Hardware filtering rounds its weights to 1/256 of a texel: a few mm per metre of change between cells.
                Assert.That(worst, Is.LessThan(0.01f), "the GPU's lift is the cursor's, within 1 cm per metre");
                Assert.That(worstSlope, Is.LessThan(0.01f), "and its slope, within 1 %");
            }
            finally { states.Dispose(); }
        }

        [Test]
        public void NothingLiftsBeforeTheFirstPassOrWithTheLiftOff()
        {
            var states = new SurfaceStates(Ring);
            try
            {
                // The opening's static snowpack, before any live tile: the field holds it, the ground doesn't stand on it.
                var static2m = new float[Cells * Cells];
                for (int c = 0; c < static2m.Length; c++) static2m[c] = 2;
                states.Snow.CopyFrom(static2m);
                states.Sync();
                Assert.That(states.LiftOn, Is.False, "no live tiles yet");
                Assert.That(states.FirstPassDone, Is.False);
                Assert.That(SnowCursor.LiftAt(states, Ring, Cells * M / 2, Cells * M / 2), Is.Zero, "the cursor's lift waits too");
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                    foreach (var c in Draw(2, 16, 16)) Assert.That(c.r, Is.Zero, "the shader's lift waits too");
            }
            finally { states.Dispose(); }

            var live = Live((i, j) => 2);
            try
            {
                Assert.That(SnowCursor.LiftAt(live, Ring, Cells * M / 2, Cells * M / 2), Is.EqualTo(2).Within(1e-4f), "on after the pass");
                live.LiftOn = false;
                Assert.That(SnowCursor.LiftAt(live, Ring, Cells * M / 2, Cells * M / 2), Is.Zero, "switched off");
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                    foreach (var c in Draw(2, 16, 16)) Assert.That(c.r, Is.Zero, "the shader's too");
            }
            finally { live.Dispose(); }
        }

        /// <summary>
        /// Findings W1 and W6 (owner, 2026-11-17: "there's no whale"): the height the game DRAWS for a gun whale, through the
        /// curve the snow shell and the cursor share. The gun's model is a 26 ft ridge about 20 ft wide; on the field's 8 m
        /// squares, smoothed, it draws as a lower, broader mound. This pins how much lower, so a change that flattens it
        /// further (or a log that calls a square's mean "drawn") fails here.
        /// </summary>
        [Test]
        public void AGunWhaleDrawsAsAMoundMetresTall()
        {
            const float Blanket = 0.4f;
            float m = M;
            // The gun in the field's middle, aimed along +x and along +z, on a square's edge and on its middle.
            foreach (var (gx, gz, ax, az) in new[] { (24 * m, 24 * m + 4, 1f, 0f), (24 * m + 4, 24 * m, 0f, 1f), (24 * m, 24 * m, 1f, 0f) })
            {
                var whale = MountainPlanner.Domain.Snow.GunWhale.Build(gx, gz, ax, az, 3, 300, MountainPlanner.Domain.Snow.GunWhale.Settings.Stick20);
                var added = whale.AddedMetres(0, 0, m, Cells, Cells, out long outside);
                Assert.That(outside, Is.Zero, "the whole pile on the field");
                float square = 0;
                foreach (float a in added) square = Mathf.Max(square, a);
                var states = Live((i, j) => Blanket + added[j * Cells + i]);
                try
                {
                    float peak = 0;
                    for (float z = 0; z < Cells * m; z += 1)
                        for (float x = 0; x < Cells * m; x += 1) peak = Mathf.Max(peak, SnowCursor.LiftAt(states, Ring, x, z));
                    float above = peak - Blanket;
                    TestContext.Progress.WriteLine($"[gun whale] aimed ({ax}, {az}): modelled {whale.PeakMetres / 0.3048:F1} ft, fullest square {square / 0.3048f:F1} ft, drawn {above / 0.3048f:F1} ft above the snow round it");
                    Assert.That(above, Is.GreaterThan(3.05f), "the drawn mound stands more than 10 ft above the snow round it");
                    Assert.That(above, Is.LessThanOrEqualTo(square + 0.01f), "and never above the fullest square");
                    Assert.That(above, Is.GreaterThan(0.6f * square), "smoothing keeps most of the fullest square's height");
                }
                finally { states.Dispose(); }
            }
        }

        [Test]
        public void TheSnowLayerShowsAndHidesTheShellAtOnce()
        {
            var states = Live((i, j) => 2);
            var layers = new MapLayers();
            try
            {
                float mid = Cells * M / 2;
                layers.BindSnow(states);
                Assert.That(states.LiftScale, Is.EqualTo(1), "on: the shell stands on the full depth");
                Assert.That(SnowCursor.LiftAt(states, Ring, mid, mid), Is.EqualTo(2).Within(1e-4f));
                int version = states.LiftVersion;
                layers.Set(MapLayers.Snow, false);
                Assert.That(states.SnowShown, Is.False, "the layer tells the snow");
                states.Sync();
                Assert.That(states.LiftScale, Is.Zero, "off at once: no ease (owner, 2026-11-17)");
                Assert.That(states.LiftOn, Is.False);
                Assert.That(states.LiftVersion, Is.GreaterThan(version), "the lines over the snow are laid again");
                Assert.That(SnowCursor.LiftAt(states, Ring, mid, mid), Is.Zero, "the cursor and skiers on the earth");
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                    foreach (var c in Draw(2, 16, 16)) Assert.That(c.r, Is.Zero, "the shell's lift is gone");
                layers.Set(MapLayers.Snow, true);
                states.Sync();
                Assert.That(SnowCursor.LiftAt(states, Ring, mid, mid), Is.EqualTo(2).Within(1e-4f), "back at once");
            }
            finally { states.Dispose(); }
        }

        /// <summary>
        /// Finding A3-6: the shell's culling boxes are padded by the deepest lift the snow has held, and the pad stays on a
        /// tile's data through a height edit (Unity keeps an override through height changes, so each sync re-pads).
        /// Whether Unity culls by the pad is measured in the built game (<c>-flatpad</c>, <c>-noshellpad</c>).
        /// </summary>
        [Test]
        public void TheShellsCullingPadFollowsTheDeepestLiftAndSurvivesAnEdit()
        {
            var states = Live(Pile);
            try
            {
                float deepest = 0;
                foreach (var lift in states.Lift) deepest = Mathf.Max(deepest, lift);
                Assert.That(deepest, Is.GreaterThan(4.5f), "the pile's peak lifts");
                Assert.That(states.LiftCeiling, Is.EqualTo(deepest).Within(1e-6f), "the ceiling is the deepest lift cell");
            }
            finally { states.Dispose(); }

            var data = new TerrainData { heightmapResolution = 129 };
            data.size = new Vector3(128, 200, 128);
            try
            {
                var flat = new float[129, 129];
                for (int j = 0; j < 129; j++) for (int i = 0; i < 129; i++) flat[j, i] = 0.25f;
                data.SetHeights(0, 0, flat);
                var before = data.GetPatchMinMaxHeights();
                World.TerrainTiles.PadShellBounds(data, 6);
                Assert.That(World.TerrainTiles.ShellPadOf(data), Is.EqualTo(6f));
                var bump = new float[9, 9];
                for (int j = 0; j < 9; j++) for (int i = 0; i < 9; i++) bump[j, i] = 0.6f;
                World.TerrainTiles.SetHeightsRegion(data, 60, 60, bump);   // re-pads; an override of the wrong size would log an error
                Assert.That(World.TerrainTiles.ShellPadOf(data), Is.EqualTo(6f), "the pad stays through the edit");
                var after = data.GetPatchMinMaxHeights();
                Assert.That(after.Length, Is.EqualTo(before.Length));
                float top = 0;
                foreach (var e in after) top = Mathf.Max(top, e.max);
                Assert.That(top, Is.EqualTo(0.6f).Within(1e-4f), "Unity reports the true heights, never the pad, so re-padding never compounds");
            }
            finally { Object.DestroyImmediate(data); }
        }

        [Test]
        public void LakesStationsAndTheRingsEdgeStayOnTheEarth()
        {
            var states = Live((i, j) => 2);
            try
            {
                // A lake over cells 10–14 each way, the streams in cell (34, 34).
                var mask = new byte[Cells * Cells];
                for (int j = 10; j <= 14; j++)
                    for (int i = 10; i <= 14; i++) mask[j * Cells + i] = 2;
                mask[34 * Cells + 34] = 1;
                var bodies = new[]
                {
                    new WaterBody(WaterBodyKind.Streams, 0, 1, -1, 0, 0, 1000, 0, 10, false),
                    new WaterBody(WaterBodyKind.Lake, 0xA, 25, -1, 0, 0, 1000, 6000, 50, false),
                };
                var table = WaterBodyTable.Of(Cells, Cells, 0, 0, mask, bodies, 4);
                Assert.That(states.SetBodies(table, LakeSurfaces.Dilated(table)), Is.True);
                // A terminal at (300, 120) facing north: 6 m behind its bullwheel, 4 ahead, 3 either side.
                states.SetStations(new[]
                {
                    new SurfaceStates.Footprint { Centre = new Vector2(300, 120), Along = Vector2.up, Behind = 6, Ahead = 4, HalfWidth = 3 },
                });
                float L(float x, float z) => SnowCursor.LiftAt(states, Ring, x, z);
                Assert.That(L(12.5f * M, 12.5f * M), Is.Zero, "the lake's middle");
                Assert.That(L(10.5f * M, 12.5f * M), Is.Zero, "the lake's edge cell");
                Assert.That(L(34.5f * M, 34.5f * M), Is.EqualTo(2).Within(1e-4f), "the streams keep their snow");
                for (float a = -6; a <= 4; a += 1)
                    for (float s = -3; s <= 3; s += 1)
                        Assert.That(L(300 + s, 120 + a), Is.Zero, $"the station's footprint at {s}, {a}");
                Assert.That(L(300, 120 + 4 + 40), Is.EqualTo(2).Within(1e-4f), "40 m up the line: the snow again");
                Assert.That(L(1, Cells * M / 2), Is.Zero, "the ring's edge");
                Assert.That(L(Cells * M / 2, Cells * M - 1), Is.Zero, "the ring's far edge");
                Assert.That(L(SurfaceStates.EdgeFadeMetres + 2 * M, Cells * M / 2), Is.EqualTo(2).Within(1e-4f), "full past the fade");
                // Everything the cursor reads is what went to the GPU: the lift cells are the depth × the keep.
                for (int c = 0; c < states.Lift.Length; c++)
                    Assert.That(states.Lift[c], Is.EqualTo(states.Snow.Cells[c] * states.Keep[c]), $"cell {c}");
            }
            finally { states.Dispose(); }
        }
    
        [Test]
        public void TheLiftLinesFollowTheSnowWithoutAllocating()
        {
            // The relay as the snow changes moves the lines' points (LiftMapLines.Drape); the Development benchmark with
            // 3,000 guests caught 27 KB every 15 s when it rebuilt them.
            var root = new GameObject("lift lines test");
            var lines = new MountainPlanner.Presentation.Lifts.LiftMapLines(root.transform);
            try
            {
                var towers = new System.Collections.Generic.List<Vector3> { new Vector3(0, 0, 100), new Vector3(0, 0, 200) };
                lines.Set(7, new Vector3(0, 0, 0), new Vector3(0, 0, 300), towers, (x, z) => 100);
                Assert.That(lines.LineStart(7).Value.y, Is.EqualTo(102).Within(1e-4f), "laid on the earth, 2 m over it");
                float depth = 3;
                System.Func<float, float, float> onSnow = (x, z) => 100 + depth;
                lines.Drape(onSnow);
                Assert.That(lines.LineStart(7).Value.y, Is.EqualTo(105).Within(1e-4f), "on 3 m of snow");
                for (int i = 0; i < 10; i++) lines.Drape(onSnow);   // warm
                long before = System.GC.GetTotalMemory(false);
                for (int i = 0; i < 300; i++) { depth = i * 0.01f; lines.Drape(onSnow); }
                long grew = System.GC.GetTotalMemory(false) - before;
                Assert.That(lines.LineStart(7).Value.y, Is.EqualTo(102 + 2.99f).Within(1e-3f));
                Assert.That(grew, Is.LessThanOrEqualTo(0), "300 drapes allocate nothing");
            }
            finally { Object.DestroyImmediate(root); }   // edit mode: Dispose's Object.Destroy would log an error
        }
    }
}
