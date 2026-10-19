using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// The ground the trail tools shape (task 08): the terrain edit session started when a tool that needs it arms
    /// (P3-05 started it only for test flags and saves with edits), seen by the tools through <see cref="IEarthworkHost"/>.
    /// </summary>
    public sealed partial class MountainViewer
    {
        EarthworkHost _ground;

        /// <summary>The tools' view of the terrain edit session.</summary>
        internal IEarthworkHost Ground => _ground ?? (_ground = new EarthworkHost(this));

        /// <summary>Starts the terrain edit session for the open design if it isn't running.</summary>
        void EnsureTerrainEdits()
        {
            if (_edits != null || _editsStarting || _resort == null) return;
            _editsStarting = true;
            StartCoroutine(StartEditsWhenReady(Array.Empty<string>(), -1, -1));
        }

        // ---------- -trailshots <folder>: the New trail tool driven by script, a picture per step (task 08 review) ----------

        /// <summary>While a scripted capture drives the armed tool, the pointer's own ticks wait.</summary>
        bool _toolScripted;

        /// <summary>
        /// Test trails in local metres (from the site's centre): the stroke path, a width per segment and erased islands;
        /// the same spots as the Domain review pictures (TrailPictures) on Jackson Hole 2 km.
        /// </summary>
        static readonly Dictionary<string, (float[] path, int[] feet, (float x, float z, int ft)[] islands)> TrailSpots =
            new Dictionary<string, (float[], int[], (float, float, int)[])>
            {
                ["sidehill"] = (new float[] { 200, -330, 225, -480, 235, -600, 250, -760 }, new[] { 100, 220, 100 }, new[] { (245f, -540f, 45) }),
                ["ridge"] = (new float[] { 183, 790, 280, 720, 377, 650 }, new[] { 120 }, new (float, float, int)[0]),
                ["gully"] = (new float[] { 271, -78, 360, -160, 449, -242 }, new[] { 120 }, new (float, float, int)[0]),
                ["steep"] = (new float[] { -650, 380, -660, 500, -650, 640 }, new[] { 150 }, new (float, float, int)[0]),
            };

        void StartTrailShots(string[] args)
        {
            int i = Array.IndexOf(args, "-trailshots");
            if (i < 0 || i + 1 >= args.Length) return;
            int s = Array.IndexOf(args, "-trailspot");
            StartCoroutine(TrailShots(args[i + 1], s >= 0 && s + 1 < args.Length ? args[s + 1] : "sidehill"));
        }

        ToolInput GroundInput(Vector2 xz, bool click = false, bool drag = false, bool release = false, bool erase = false)
        {
            float y = _resort.Surface.HeightAt(xz.x, xz.y);
            var g = new Vector3(xz.x, y, xz.y);
            return new ToolInput
            {
                HasGround = !float.IsNaN(y), Ground = g, Point = DesignFrame.ToDesign(g), InCore = _resort.Core.Contains(xz),
                MetresPerPixel = 0.5f, Click = click, Drag = drag, Release = release, Erase = erase,
            };
        }

        IEnumerator WaitTool(NewTrailTool trail)
        {
            for (int i = 0; i < 3000 && (trail.Analysing || _tools.Context.Running > 0); i++)
            {
                _tools.Tick(default);
                yield return null;
            }
            for (int i = 0; i < 3; i++) { _tools.Tick(default); yield return null; }
        }

        IEnumerator Shot(string dir, string name)
        {
            while (WorkBusy) yield return null;
            for (int i = 0; i < 45; i++) yield return null;
            string path = Path.Combine(dir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Debug.Log("[TrailShots] " + path);
        }

        IEnumerator TrailShots(string dir, string spotName)
        {
            Directory.CreateDirectory(dir);
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            if (!TrailSpots.TryGetValue(spotName, out var spot)) { Debug.LogWarning("[TrailShots] unknown spot " + spotName); yield break; }
            Hud.SetToolboxTab("trails");
            Hud.SetToolbox(true);
            _tools.Choose(NewTrailTool.ToolId);
            var trail = (NewTrailTool)_tools.Active;
            _toolScripted = true;
            while (!Ground.Ready) yield return null;

            // Paint: one stroke per segment at its width, then the islands erased.
            int n = spot.path.Length / 2;
            for (int k = 0; k + 1 < n; k++)
            {
                int feet = spot.feet.Length == 1 ? spot.feet[0] : spot.feet[k];
                while (trail.BrushFeet < feet) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushWider });
                while (trail.BrushFeet > feet) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });
                var a = new Vector2(spot.path[2 * k], spot.path[2 * k + 1]);
                var b = new Vector2(spot.path[2 * k + 2], spot.path[2 * k + 3]);
                _tools.Tick(GroundInput(a, click: true, drag: true));
                int steps = Mathf.CeilToInt(Vector2.Distance(a, b) / 5);
                for (int t = 1; t <= steps; t++) _tools.Tick(GroundInput(Vector2.Lerp(a, b, (float)t / steps), drag: true));
                _tools.Tick(GroundInput(b, release: true));
                yield return WaitTool(trail);
            }
            foreach (var (x, z, ft) in spot.islands)
            {
                while (trail.BrushFeet > ft) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });
                _tools.Tick(GroundInput(new Vector2(x, z), click: true, erase: true));
                _tools.Tick(GroundInput(new Vector2(x, z), release: true, erase: true));
                yield return WaitTool(trail);
            }
            yield return Shot(dir, "1-paint");

            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            _tools.Tick(GroundInput(new Vector2(spot.path[0], spot.path[1]), click: true));
            _tools.Tick(GroundInput(new Vector2(spot.path[2 * n - 2], spot.path[2 * n - 1]), click: true));
            yield return WaitTool(trail);
            yield return Shot(dir, "2-natural");
            trail.ChooseGraded(true);
            yield return WaitTool(trail);
            yield return Shot(dir, "3-graded");
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            yield return WaitTool(trail);
            yield return Shot(dir, "4-review");
            var r = _tools.Confirm(MountainPlanner.Domain.Design.WorkStage.Built);
            Debug.Log("[TrailShots] Break ground: " + r);
            yield return WaitTool(trail);
            _toolScripted = false;
            _tools.Close();
            Hud.SetToolbox(false);
            yield return Shot(dir, "5-built");
            Design.Undo();
            yield return Shot(dir, "6-undone");
            Application.Quit();
        }

        sealed class EarthworkHost : IEarthworkHost
        {
            readonly MountainViewer _viewer;
            public EarthworkHost(MountainViewer viewer) { _viewer = viewer; }

            public void Start() => _viewer.EnsureTerrainEdits();
            public bool Ready => _viewer._edits != null && _viewer._edits.EarthworkReady && _viewer._edits.History == _viewer.Design;
            public DesignEarthwork Earthwork => _viewer._edits?.Earthwork;
            public EditedHeights Heights => _viewer._edits?.Pipeline.Heights;
            public void ShowPending(List<double> segments) => _viewer._edits?.ShowPending(segments);
            public void ClearPending() => _viewer._edits?.ClearPreview();
            public double ContourInterval => UnitFormat.ContourIntervalMetres(DisplayUnits.Current);
        }
    }
}
