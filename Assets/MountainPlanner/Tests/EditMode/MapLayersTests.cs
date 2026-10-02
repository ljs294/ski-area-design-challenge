using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // Map layers (task 12): each switch sets its shader values or stops a draw on the spot, with nothing else
    // to wait for; Ground cover is always on and Imagery is reserved. MapLayersInUnityTests (PlayMode) proves
    // the frame and the profiler marker on a real resort.
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
        }

        MapLayers Bound(out GroundLayers ground, out ForestView view)
        {
            ground = new GroundLayers();
            ground.Configure(_terrain, new Rect(-1000, -1000, 2000, 2000));
            view = _forest.AddComponent<ForestView>();
            var layers = new MapLayers();
            layers.Bind(ground, _edge, _cliff);
            layers.BindForest(view);
            return layers;
        }

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
        public void TheCoverMapLeavesTheSnowLayerAlone()
        {
            var layers = Bound(out var ground, out _);
            layers.Set(MapLayers.Cover, true);
            Assert.That(_terrain.GetFloat("_Overlay"), Is.EqualTo(1));
            Assert.That(ground.OverlayOn, Is.True);
            Assert.That(layers.SnowOn, Is.True, "the overlay hides the snow in the shader; the Snow layer keeps its state");
            Assert.That(_terrain.GetFloat("_SnowOn"), Is.EqualTo(1));
            layers.Set(MapLayers.Cover, false);
            Assert.That(_terrain.GetFloat("_Overlay"), Is.EqualTo(0));
        }

        [Test]
        public void TheForestStopsDrawingWithoutLosingItsTrees()
        {
            var layers = Bound(out _, out var view);
            layers.Toggle(MapLayers.Forest);
            Assert.That(view.enabled, Is.False, "the forest skips its cull and draws");
            Assert.That(view != null, "the forest and its trees are kept");
            layers.Toggle(MapLayers.Forest);
            Assert.That(view.enabled, Is.True);
        }

        [Test]
        public void GroundCoverIsAlwaysOnAndImageryIsReserved()
        {
            var layers = Bound(out _, out _);
            Assert.That(layers.IsOn(MapLayers.Ground), Is.True);
            Assert.That(layers.Toggle(MapLayers.Ground), Is.False);
            Assert.That(layers.IsOn(MapLayers.Ground), Is.True);
            Assert.That(layers.IsOn(MapLayers.Imagery), Is.False);
            Assert.That(layers.Toggle(MapLayers.Imagery), Is.False);
            Assert.That(MapLayers.IsSwitchable(MapLayers.Snow) && MapLayers.IsSwitchable(MapLayers.Forest) && MapLayers.IsSwitchable(MapLayers.Cover));
            Assert.That(MapLayers.IsSwitchable(MapLayers.Ground) || MapLayers.IsSwitchable(MapLayers.Imagery), Is.False);
        }

        [Test]
        public void LayersSetBeforeTheResortOpensApplyWhenItBinds()
        {
            var layers = new MapLayers();
            layers.Set(MapLayers.Snow, false);    // -nosnow
            layers.Set(MapLayers.Cover, true);    // -covermap
            var ground = new GroundLayers();
            ground.Configure(_terrain, new Rect(-1000, -1000, 2000, 2000));
            layers.Bind(ground, _edge, _cliff);
            Assert.That(_terrain.GetFloat("_SnowOn"), Is.EqualTo(0));
            Assert.That(_terrain.GetFloat("_Overlay"), Is.EqualTo(1));
            Assert.That(_edge.GetFloat("_SnowOn"), Is.EqualTo(0));
        }
    }
}
