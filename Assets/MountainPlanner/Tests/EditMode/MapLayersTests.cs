using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // Map layers and info layers (tasks 12 and 12b): each switch sets its shader values or stops a draw on the spot,
    // with nothing else to wait for. Map layers combine; the info layers take turns except Contours, which combine
    // with any; Snow conditions is reserved. MapLayersInUnityTests (PlayMode) proves the frame and the profiler
    // marker on a real resort.
    public sealed class MapLayersTests
    {
        Material _terrain, _edge, _cliff;
        GameObject _forest;

        [SetUp]
        public void Create()
        {
            _terrain = new Material(Shader.Find("MountainPlanner/Terrain"));
            _edge = new Material(Shader.Find("MountainPlanner/DioramaWall"));
            _cliff = new Material(Shader.Find("MountainPlanner/Cliff"));
            _forest = new GameObject("Forest");
        }

        [TearDown]
        public void Destroy()
        {
            Object.DestroyImmediate(_terrain);
            Object.DestroyImmediate(_edge);
            Object.DestroyImmediate(_cliff);
            Object.DestroyImmediate(_forest);
            new MapLayers().Apply();   // the globals back to their defaults for the next test
        }

        MapLayers Bound(out GroundLayers ground, out ForestView view, float convergence = 0)
        {
            ground = new GroundLayers();
            ground.Configure(_terrain, new Rect(-1000, -1000, 2000, 2000));
            view = _forest.AddComponent<ForestView>();
            var layers = new MapLayers();
            layers.Bind(ground, _edge, _cliff, convergence);
            layers.BindForest(view);
            return layers;
        }

        static float InfoView => Shader.GetGlobalFloat("_MP_InfoView");
        static float Contours => Shader.GetGlobalFloat("_MP_Contours");

        [Test]
        public void TheShadersFindTheirLayerProperties()
        {
            Assert.That(_terrain.shader.name, Is.EqualTo("MountainPlanner/Terrain"));
            Assert.That(_terrain.HasFloat("_SnowOn") && _terrain.HasFloat("_Overlay"), "terrain");
            Assert.That(_edge.HasFloat("_SnowOn"), "diorama walls");
            Assert.That(_cliff.HasFloat("_SnowLoad"), "cliff ledges");
        }

        [Test]
        public void SnowSwitchesTheGroundWallsAndLedgesAtOnce()
        {
            var layers = Bound(out var ground, out _);
            Assert.That(_terrain.GetFloat("_SnowOn"), Is.EqualTo(1));

            Assert.That(layers.Toggle(MapLayers.Snow), Is.True);
            Assert.That(layers.SnowOn, Is.False);
            Assert.That(ground.SnowOn, Is.False);
            Assert.That(_terrain.GetFloat("_SnowOn"), Is.EqualTo(0), "the ground's snow");
            Assert.That(_edge.GetFloat("_SnowOn"), Is.EqualTo(0), "the walls' snow cap");
            Assert.That(_cliff.GetFloat("_SnowLoad"), Is.EqualTo(0), "the ledges' snow");

            layers.Toggle(MapLayers.Snow);
            Assert.That(_terrain.GetFloat("_SnowOn") + _edge.GetFloat("_SnowOn") + _cliff.GetFloat("_SnowLoad"), Is.EqualTo(3));
        }

        [Test]
        public void SnowShowsAndHidesTheSnowShellInTheSameCall()
        {
            // Task 4.07 PR A3 round 3: a tile's snow shell is a second Terrain on the tile's own data, drawn with the shell variant.
            var layers = Bound(out var ground, out _);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(64, 100, 64) };
            var tileObject = new GameObject("tile");
            try
            {
                var tile = tileObject.AddComponent<Terrain>();
                tile.terrainData = data;
                tile.materialTemplate = _terrain;
                Assert.That(ground.ShellMaterial, Is.Not.Null);
                Assert.That(ground.ShellMaterial.IsKeywordEnabled(MountainPlanner.World.TerrainTiles.ShellKeyword), "the shell variant");
                Assert.That(ground.ShellMaterial.GetFloat("_ShellOffset"), Is.EqualTo(-1), "a step nearer than the ground");
                // Task 4.07 PR A3 repair: the shell is cut to the ring by its own copy of the bounds; without them it is cut
                // away everywhere (the copy of the ground's material didn't carry them in the built game).
                Assert.That(ground.ShellMaterial.GetVector("_RingBounds"), Is.EqualTo(new Vector4(-1000, -1000, 1000, 1000)), "the shell's own ring bounds");
                Assert.That(ground.ShellMaterial.GetFloatArray("_Tile"), Is.EqualTo(_terrain.GetFloatArray("_Tile")), "and its own tiling");
                Assert.That(ground.ShellMaterial.renderQueue, Is.EqualTo(_terrain.renderQueue - 1), "drawn before the ground (the ground under it is not shaded)");
                var shell = MountainPlanner.World.TerrainTiles.CreateShell(tile, ground.ShellMaterial, MountainPlanner.World.TerrainDetail.High, ground.SnowOn);
                ground.ShellTiles = new[] { tile };
                Assert.That(MountainPlanner.World.TerrainTiles.ShellOf(tile), Is.SameAs(shell));
                Assert.That(shell.terrainData, Is.SameAs(data), "the tile's own data: no copy, edits reach it");
                Assert.That(shell.heightmapPixelError, Is.EqualTo(2 * tile.heightmapPixelError).Or.EqualTo(2 * MountainPlanner.World.TerrainDetailSettings.PixelError(MountainPlanner.World.TerrainDetail.High)), "half the ground's detail");
                Assert.That(MountainPlanner.World.TerrainTiles.IsShell(shell) && !MountainPlanner.World.TerrainTiles.IsShell(tile));
                Assert.That(shell.drawHeightmap, Is.True);
                layers.Toggle(MapLayers.Snow);
                Assert.That(shell.drawHeightmap, Is.False, "off: hidden in the same call as the white");
                Assert.That(tile.drawHeightmap, Is.True, "the ground stays");
                Assert.That(tile.transform.position, Is.EqualTo(Vector3.zero), "and never moves");
                layers.Toggle(MapLayers.Snow);
                Assert.That(shell.drawHeightmap, Is.True);
                layers.Set(MapLayers.CoverMap, true);
                Assert.That(ground.ShellMaterial.GetFloat("_Overlay"), Is.EqualTo(1), "the shell steps aside for the cover map");
            }
            finally
            {
                Object.DestroyImmediate(tileObject);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(ground.ShellMaterial);
            }
        }

        [Test]
        public void TreesStopDrawingWithoutLosingThem()
        {
            var layers = Bound(out _, out var view);
            layers.Toggle(MapLayers.Trees);
            Assert.That(view.enabled, Is.False, "the forest skips its cull and draws");
            Assert.That(view != null, "the forest and its trees are kept");
            layers.Toggle(MapLayers.Trees);
            Assert.That(view.enabled, Is.True);
        }

        [Test]
        public void InfoLayersTakeTurns()
        {
            var layers = Bound(out _, out _);
            Assert.That(InfoView, Is.EqualTo(0));
            layers.Set(MapLayers.SlopeAngle, true);
            Assert.That(InfoView, Is.EqualTo(2));
            layers.Set(MapLayers.Exposure, true);
            Assert.That(layers.IsOn(MapLayers.SlopeAngle), Is.False, "exposure replaces slope angle");
            Assert.That(layers.InfoLayerId, Is.EqualTo(MapLayers.Exposure));
            Assert.That(InfoView, Is.EqualTo(3));
            layers.Toggle(MapLayers.SnowDepth);
            Assert.That(InfoView, Is.EqualTo(4));
            layers.Toggle(MapLayers.SnowDepth);
            Assert.That(InfoView, Is.EqualTo(0));
            Assert.That(layers.InfoLayerId, Is.Null);
        }

        [Test]
        public void ContoursCombineWithAnyInfoLayer()
        {
            var layers = Bound(out _, out _);
            layers.Set(MapLayers.Contours, true);
            layers.Set(MapLayers.SlopeAngle, true);
            layers.Set(MapLayers.Exposure, true);
            Assert.That(layers.ContoursOn && layers.IsOn(MapLayers.Exposure));
            Assert.That(Contours, Is.EqualTo(1));
            Assert.That(MapLayers.IsExclusive(MapLayers.Contours), Is.False);
            Assert.That(new[] { MapLayers.SlopeAngle, MapLayers.Exposure, MapLayers.SnowDepth, MapLayers.SnowConditions }.All(MapLayers.IsExclusive));
        }

        [Test]
        public void TheCoverMapIsADeveloperViewInTheInfoLayersSlot()
        {
            var layers = Bound(out var ground, out _);
            layers.Set(MapLayers.CoverMap, true);
            Assert.That(_terrain.GetFloat("_Overlay"), Is.EqualTo(1));
            Assert.That(ground.OverlayOn && layers.SnowOn, "the overlay hides the snow in the shader; the Snow layer keeps its state");
            Assert.That(InfoView, Is.EqualTo(0), "the cover map has its own switch");
            layers.Set(MapLayers.SlopeAngle, true);
            Assert.That(layers.CoverMapOn, Is.False, "an info layer replaces the cover map");
            Assert.That(_terrain.GetFloat("_Overlay"), Is.EqualTo(0));
            layers.Set(MapLayers.CoverMap, true);
            Assert.That(layers.IsOn(MapLayers.SlopeAngle), Is.False, "and the cover map replaces an info layer");
            Assert.That(MapLayers.InfoIds, Does.Not.Contain(MapLayers.CoverMap), "it isn't a player layer");
        }

        [Test]
        public void SnowConditionsIsInfoViewNine()
        {
            // Task 4.07 PR A2: the live snow's conditions take their turn with the other info layers.
            var layers = Bound(out _, out _);
            Assert.That(MapLayers.IsSwitchable(MapLayers.SnowConditions), Is.True);
            Assert.That(layers.Toggle(MapLayers.SnowConditions), Is.True);
            Assert.That(layers.IsOn(MapLayers.SnowConditions), Is.True);
            Assert.That(layers.Info, Is.EqualTo(MapLayers.InfoView.SnowConditions));
            Assert.That((int)MapLayers.InfoView.SnowConditions, Is.EqualTo(9), "Snow.hlsl and Cliff.shader read 9");
            Assert.That(layers.InfoLayerId, Is.EqualTo(MapLayers.SnowConditions));
            layers.Set(MapLayers.SnowDepth, true);
            Assert.That(layers.IsOn(MapLayers.SnowConditions), Is.False, "one info layer at a time");
            Assert.That(layers.Toggle("ground"), Is.False, "retired ids do nothing");
            Assert.That(MapLayers.MapIds, Is.EqualTo(new[] { MapLayers.Snow, MapLayers.Trees, MapLayers.Lifts, MapLayers.Trails }), "task 09 adds Lifts (LT7), task 08 Trails (TL6)");
            Assert.That(MapLayers.InfoIds, Is.EqualTo(new[] { MapLayers.SlopeAngle, MapLayers.Exposure, MapLayers.SnowDepth, MapLayers.SnowConditions, MapLayers.Traffic, MapLayers.Contours }));
        }

        [Test]
        public void LiftsIsAMapLayerOnByDefault()
        {
            // Task 09 (LT7): Shift+2 shows or hides the lifts; any combination with Snow and Trees.
            var layers = new MapLayers();
            Assert.That(MapLayers.IsSwitchable(MapLayers.Lifts), Is.True);
            Assert.That(MapLayers.IsExclusive(MapLayers.Lifts), Is.False);
            Assert.That(layers.LiftsOn, Is.True);
            Assert.That(layers.Toggle(MapLayers.Lifts), Is.True);
            Assert.That(layers.LiftsOn && layers.IsOn(MapLayers.Lifts), Is.False);
            Assert.That(layers.TreesOn && layers.SnowOn, Is.True, "the others are left alone");
            Assert.That(KeyBindings.Default(GameAction.LayerLifts, 0), Is.EqualTo(new KeyChord(UnityEngine.InputSystem.Key.Digit2, KeyMods.Shift)));
        }

        [Test]
        public void LayersSetBeforeTheResortOpensApplyWhenItBinds()
        {
            var layers = new MapLayers();
            layers.Set(MapLayers.Snow, false);       // -nosnow
            layers.Set(MapLayers.SnowDepth, true);   // -info depth
            layers.Set(MapLayers.Contours, true);    // -contours
            var ground = new GroundLayers();
            ground.Configure(_terrain, new Rect(-1000, -1000, 2000, 2000));
            layers.Bind(ground, _edge, _cliff, 8.95f);
            Assert.That(_terrain.GetFloat("_SnowOn"), Is.EqualTo(0));
            Assert.That(_edge.GetFloat("_SnowOn"), Is.EqualTo(0));
            Assert.That(InfoView, Is.EqualTo(4));
            Assert.That(Contours, Is.EqualTo(1));
            Assert.That(Shader.GetGlobalFloat("_MP_GridConvergence"), Is.EqualTo(8.95f * Mathf.Deg2Rad).Within(1e-6));
        }

        // The legend card's colours are the ones the shader paints with.
        static Color[] ShaderColours(string hlsl, string name)
        {
            var m = Regex.Match(hlsl, name + @"[^=]*=\s*(\{[^;]*\}|float3\([^)]*\))\s*;", RegexOptions.Singleline);
            Assert.That(m.Success, name + " in InfoLayers.hlsl");
            return Regex.Matches(m.Groups[1].Value, @"float3\(([^)]*)\)").Cast<Match>().Select(f =>
            {
                var v = f.Groups[1].Value.Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
                return new Color(v[0], v[1], v[2]);
            }).ToArray();
        }

        [Test]
        public void TheLegendMatchesTheShader()
        {
            string hlsl = File.ReadAllText("Assets/MountainPlanner/Art/Shaders/InfoLayers.hlsl");
            void Same(Color[] shader, Color[] legend, string what)
            {
                Assert.That(legend.Length, Is.EqualTo(shader.Length), what);
                for (int k = 0; k < shader.Length; k++)
                    Assert.That((Vector4)legend[k], Is.EqualTo((Vector4)shader[k]).Using<Vector4>((a, b) => (a - b).magnitude < 1e-4f ? 0 : 1), $"{what} {k}");
            }
            Same(ShaderColours(hlsl, "ExposureColours"), InfoLegend.ExposureColours, "exposure");
            Same(ShaderColours(hlsl, "ExposureFlatColour"), new[] { InfoLegend.ExposureFlat }, "flat");
            Same(ShaderColours(hlsl, "SnowDepthColours"), InfoLegend.SnowDepthColours, "snow depth");
            Same(ShaderColours(hlsl, "SlopeGreenColour"), new[] { InfoLegend.SlopeGreen }, "green");
            Same(ShaderColours(hlsl, "SlopeBlueColour"), new[] { InfoLegend.SlopeBlue }, "blue");
            Same(ShaderColours(hlsl, "SlopeBlackColour"), new[] { InfoLegend.SlopeBlack }, "black");
            StringAssert.Contains("SlopeBlue = 25, SlopeBlack = 40, SlopeDouble = 60;   // percent", hlsl, "the slope breaks, as grades");
            Assert.That(InfoLegend.SlopeBreaksPercent, Is.EqualTo(new[] { (float)SlopeBands.BluePercent, (float)SlopeBands.BlackPercent, (float)SlopeBands.DoubleBlackPercent }));
            StringAssert.Contains("ExposureFlatPercent = " + SlopeBands.FlatPercent + ";", hlsl, "flat ground");
        }

        [Test]
        public void TheShaderFollowsThePlayersUnits()
        {
            var before = DisplayUnits.Current;
            try
            {
                var layers = Bound(out _, out _);
                DisplayUnits.Set(UnitSystem.Imperial, remember: false);
                layers.Apply();
                Assert.That(Shader.GetGlobalFloat("_MP_ContourInterval"), Is.EqualTo(12.192f).Within(1e-4), "contours every 40 ft");
                Assert.That(Shader.GetGlobalFloat("_MP_ContourFine"), Is.EqualTo(6.096f).Within(1e-4), "20 ft close up (BR13)");
                Assert.That(Shader.GetGlobalFloat("_MP_ContourFinest"), Is.EqualTo(3.048f).Within(1e-4), "10 ft closer still");
                Assert.That(InfoLegend.ContoursNote(UnitSystem.Imperial), Does.Contain("down to 10 ft"));
                DisplayUnits.Set(UnitSystem.Metric, remember: false);
                layers.Apply();
                Assert.That(Shader.GetGlobalFloat("_MP_ContourInterval"), Is.EqualTo(10f), "contours every 10 m");
                Assert.That(Shader.GetGlobalFloat("_MP_ContourFine"), Is.EqualTo(5f));
                Assert.That(Shader.GetGlobalFloat("_MP_ContourFinest"), Is.EqualTo(2.5f));
                Assert.That(InfoLegend.SnowDepth(UnitSystem.Imperial).Select(e => e.Figure), Is.EqualTo(new[] { "0 in", "6 in", "1 ft 6 in", "3 ft", "6 ft", "10 ft +" }));
                Assert.That(InfoLegend.SnowDepth(UnitSystem.Metric).Select(e => e.Figure), Is.EqualTo(new[] { "0 cm", "15 cm", "50 cm", "1 m", "2 m", "3 m +" }));
            }
            finally
            {
                DisplayUnits.Set(before, remember: false);
            }
        }
    }
}
