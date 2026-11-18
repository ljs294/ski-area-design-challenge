using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.14's Snowmaking tools without a scene (the ground's fold is Core's PondDamTests): the tray's five live tools,
    /// the Pond tool's shoreline (PD-01: points, Backspace, close; refused across a stream), Edit opening a pond's own tool,
    /// Remove's two clicks and undo (PD-08), and the Water source card's depth (LK-02).
    /// </summary>
    public sealed class WaterToolTests
    {
        const string TestPrefix = "MountainPlannerTest.P4-14.";

        sealed class Slope : ITerrainSurface
        {
            public Rect Footprint => new Rect(-2000, -2000, 4000, 4000);
            public float HeightAt(float x, float z) => Footprint.Contains(new Vector2(x, z)) ? 2000 + 0.15f * z : float.NaN;
            public float LiftAt(float x, float z) => 0;
        }

        sealed class NullPreview : IToolPreview
        {
            public event Action Cleared;
            public void Line(int slot, Vector2[] xz, int count, PreviewInk ink, bool closed = false) { }
            public void Ring(int slot, Vector2 centre, float radiusMetres, PreviewInk ink) { }
            public void Dot(int slot, Vector2 at, PreviewInk ink) { }
            public void Hide(int slot) { }
            public void HideFrom(int slot) { }
            public void Clear() => Cleared?.Invoke();
        }

        DesignHistory _design;
        ToolHost _host;

        [SetUp]
        public void SetUp()
        {
            SettingsStore.Prefix = TestPrefix;
            KeyBindings.Reload();
            KeyBindings.ClearClaims();
            _design = new DesignHistory(DesignDocument.Empty(0x5EED_0414));
            _host = new ToolHost(_ => new ToolContext(_design, new NullPreview(), new Slope(), new Rect(-1500, -1500, 3000, 3000)), _ => { });
        }

        [TearDown]
        public void TearDown()
        {
            _host.Close();
            KeyBindings.ClearClaims();
            SettingsStore.Prefix = SettingsStore.DefaultPrefix;
            KeyBindings.Reload();
        }

        static ToolInput At(float x, float z, bool click = false)
        {
            var g = new Vector3(x, 2000 + 0.15f * z, z);
            return new ToolInput { HasGround = true, Ground = g, Point = DesignFrame.ToDesign(g), InCore = true, Click = click, MetresPerPixel = 0.5f };
        }

        static readonly Vector2[] Square = { new Vector2(0, 0), new Vector2(40, 0), new Vector2(40, 40), new Vector2(0, 40) };

        PondId AddPond()
        {
            var ring = new PointMm[Square.Length];
            for (int i = 0; i < ring.Length; i++) ring[i] = PointMm.FromMetres(Square[i].x, Square[i].y);
            var r = _design.Submit(new AddPond(_design.Revision, "Pond 1", ring, 2_010_000, 3048), null);
            Assert.That(r.Accepted, Is.True, r.Reason);
            return new PondId(r.Created[0]);
        }

        [Test]
        public void TheSnowmakingTrayHasFiveLiveTools()
        {
            foreach (var id in new[] { PondTool.ToolId, DamTool.ToolId, WaterSourceTool.ToolId, EditWaterTool.ToolId, RemoveWaterTool.ToolId })
                Assert.That(ToolCatalog.IsLive(id), Is.True, id);
            Assert.That(ToolCatalog.IsLive("gun"), Is.False, "4.15's tools stay greyed");
        }

        [Test]
        public void PD01_TheShorelineTakesPointsBackspaceAndClosesOnTheFirst()
        {
            _host.Choose(PondTool.ToolId);
            var pond = (PondTool)_host.Active;
            foreach (var p in Square) { _host.Tick(At(p.x, p.y, click: true)); _host.Tick(default); }
            Assert.That(pond.Points, Is.EqualTo(4));
            _host.Tick(new ToolInput { Keys = ToolKeys.UndoStroke });
            Assert.That(pond.Points, Is.EqualTo(3), "Backspace takes the last point back");
            _host.Tick(At(0, 40, click: true));
            _host.Tick(default);
            Assert.That(pond.HasWork, Is.True);
            _host.Tick(At(0.5f, 0.5f, click: true));   // on the first point: closes
            Assert.That(pond.Points, Is.EqualTo(4), "closing adds no point");
            Assert.That(_host.Escape(), Is.True, "Esc steps back from the pool to the shoreline");
            Assert.That(pond.Points, Is.EqualTo(4));
        }

        [Test]
        public void PD01_AShorelineAcrossAStreamIsRefused()
        {
            _host.Choose(PondTool.ToolId);
            var pond = (PondTool)_host.Active;
            _host.Context.WaterUnder = p => p.XMetres > 15 && p.XMetres < 20 ? "Twin Creek" : null;
            foreach (var p in Square) { _host.Tick(At(p.x, p.y, click: true)); _host.Tick(default); }
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(_host.Context.Window?.Message ?? "", Does.Contain("Twin Creek"), "dam it instead");
            Assert.That(pond.HasWork, Is.True, "the points stay to move");
        }

        [Test]
        public void PD12_ADamStartsBesideAStream()
        {
            _host.Choose(DamTool.ToolId);
            var dam = (DamTool)_host.Active;
            _host.Context.StreamDistance = p => p.XMetres > 100 ? double.PositiveInfinity : Math.Abs(p.XMetres);
            _host.Tick(At(200, 0, click: true));
            _host.Tick(default);
            Assert.That(_host.Context.Window?.Message ?? "", Does.Contain("beside a stream"));
            Assert.That(dam.HasWork, Is.False, "no first bank away from a stream");
            _host.Tick(At(60, 0, click: true));   // 60 m: within 250 ft
            _host.Tick(default);
            // Past PD-12: with no earthwork host here, the tool waits on the ground instead.
            Assert.That(_host.Context.Window?.Message ?? "", Does.Contain("Reading the ground"), "beside one, the click goes through");
        }

        [Test]
        public void PD08_RemoveTakesTwoClicksAndUndoPutsItBack()
        {
            var id = AddPond();
            _host.Choose(RemoveWaterTool.ToolId);
            var tool = (RemoveWaterTool)_host.Active;
            _host.Tick(At(20, 20, click: true));
            Assert.That(tool.Armed, Is.EqualTo(id.Value), "the first click selects it");
            Assert.That(_design.Document.Ponds.Count, Is.EqualTo(1));
            _host.Tick(default);
            _host.Tick(At(20, 20, click: true));
            Assert.That(_design.Document.Ponds.Count, Is.EqualTo(0), "the second click removes it");
            Assert.That(_design.Undo(), Is.True);
            Assert.That(_design.Document.Ponds.Count, Is.EqualTo(1), "one Ctrl+Z puts it back");
            _host.Tick(At(200, 200, click: true));
            Assert.That(tool.Armed, Is.EqualTo(0UL), "dry ground: nothing selected");
        }

        [Test]
        public void EditOpensThePondsOwnTool()
        {
            AddPond();
            _host.Choose(EditWaterTool.ToolId);
            var tool = (EditWaterTool)_host.Active;
            _host.Tick(At(300, 300, click: true));
            Assert.That(tool.Inner, Is.Null);
            _host.Tick(At(20, 20, click: true));
            Assert.That(tool.Inner, Is.InstanceOf<PondTool>());
            Assert.That(((PondTool)tool.Inner).Points, Is.EqualTo(4));
            Assert.That(_host.Escape(), Is.True);
            Assert.That(tool.Inner, Is.Null, "Esc goes back to picking");
        }

        [Test]
        public void LK02_TheSourceCardTakesTheEstimateThenYourDepth()
        {
            _host.Choose(WaterSourceTool.ToolId);
            var tool = (WaterSourceTool)_host.Active;
            _host.Context.LakeAt = p => p.XMetres > 500 ? new LakeUnder(0xABCD, "", 120, 40_000) : (LakeUnder?)null;
            _host.Tick(At(600, 0, click: true));
            Assert.That(tool.Lake, Is.EqualTo(0xABCDUL));
            Assert.That(tool.DepthTenths, Is.EqualTo(0), "the estimate until you change it");
            _host.Tick(new ToolInput { Keys = ToolKeys.BrushWider });
            Assert.That(tool.DepthTenths, Is.EqualTo(130), "] a foot deeper");
            _host.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });
            Assert.That(tool.DepthTenths, Is.EqualTo(0), "back on the estimate");
            AddPond();
            _host.Tick(At(20, 20, click: true));
            Assert.That(tool.Lake, Is.EqualTo(0UL), "a pond is a source already");
        }

        // Owner 2026-11-17: every water card shows its facts the same way: one order, one label and one format per fact; a
        // lake leaves out only what it can't have (Starts, Earthwork).
        [Test]
        public void LK10_EveryStandingWaterCardUsesTheSameRows()
        {
            List<string> Labels(UI.WaterCardRows.Standing w)
            {
                var rows = new List<(string, string)>();
                UI.WaterCardRows.Add(rows, w);
                return rows.ConvertAll(r => r.Item1);
            }
            var built = UI.WaterCardRows.Empty();
            built.LevelMetres = 581; built.FullPoolMetres = 581; built.PercentFull = 100; built.AreaSquareMetres = 2400; built.MeanDepthMetres = 2.1;
            built.MaxDepthMetres = 3; built.VolumeCubicMetres = 5100; built.Fills = "naturally"; built.StartsEmpty = false;
            built.Snowmaking = "a source"; built.CutCubicMetres = 3260; built.FillCubicMetres = 4560;
            var lake = UI.WaterCardRows.Empty();
            lake.LevelMetres = 460; lake.FullPoolMetres = 460; lake.PercentFull = 100; lake.AreaSquareMetres = 344_800; lake.MeanDepthMetres = 8.5; lake.DepthNote = "estimate";
            lake.VolumeCubicMetres = 2_930_000; lake.Fills = "naturally"; lake.Snowmaking = "not a source";
            Assert.That(Labels(built), Is.EqualTo(UI.WaterCardRows.Order), "a pond or reservoir shows every row, in order");
            var lakeOrder = new List<string>(UI.WaterCardRows.Order);
            lakeOrder.Remove(UI.WaterCardRows.Starts);
            lakeOrder.Remove(UI.WaterCardRows.Earthwork);
            Assert.That(Labels(lake), Is.EqualTo(lakeOrder), "a lake: the same rows, less Starts and Earthwork");
            var rows = new List<(string, string)>();
            UI.WaterCardRows.Add(rows, lake);
            Assert.That(rows.Find(r => r.Item1 == "Depth").Item2, Does.StartWith("mean "), "depth reads the same on every card");
            Assert.That(rows[0].Item1, Is.EqualTo("Water level"), "where the water stands now comes first");
            Assert.That(rows[1].Item1, Is.EqualTo("Full pool"), "then the highest it fills");
            Assert.That(rows[2], Is.EqualTo(("Percent full", "100%")), "then how full it is");
            Assert.That(rows.Find(r => r.Item1 == "Volume").Item2, Does.Contain("acre-ft").And.Contain("gal"));
            Assert.That(UI.WaterCardRows.Hint("Too much earth.", null, true), Is.EqualTo("Too much earth. Click the name to rename it. Name © OpenStreetMap contributors."));
        }
    }
}
