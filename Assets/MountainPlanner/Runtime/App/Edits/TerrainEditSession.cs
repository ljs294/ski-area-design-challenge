using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Edits;
using UnityEngine;

namespace MountainPlanner.App.Edits
{
    /// <summary>
    /// Ties an open mountain's terrain to a design (task P3-05): whenever the design's history moves (a command with
    /// edits, an undo, a redo, a load), the pipeline lays the new edit set over the mountain (TE2). It also runs the
    /// test edit (TE4), a straight graded bench by command, with its yellow preview (TE3), for the demo and the edit
    /// benchmark. Task 08's trail tool drives the same history and pipeline with its own grading.
    /// </summary>
    public sealed class TerrainEditSession : IDisposable
    {
        /// <summary>The test edit: 300 m (about 1,000 ft) long and 100 ft wide (CF-09).</summary>
        public const double BenchLengthMetres = 300, BenchWidthMetres = 30.48;

        readonly OpenedResort _resort;
        readonly List<double> _segments = new List<double>();
        TestBench.Result? _pending;
        (Vector2 Head, Vector2 Tail) _pendingLine;   // local metres from the site's centre

        public DesignHistory History { get; }
        public TerrainEditPipeline Pipeline { get; }
        public PendingContourView Preview { get; }
        /// <summary>The design's earthwork (task 08, TL3): built work folded into terrain edits; ready once primed.</summary>
        public DesignEarthwork Earthwork { get; }
        /// <summary>The earthwork learning the committed design, off the main thread.</summary>
        public Task Priming { get; private set; } = Task.CompletedTask;
        public bool EarthworkReady => Priming.IsCompleted && !Priming.IsFaulted && Earthwork.Primed;
        int _loads;
        /// <summary>The last apply, for captures and the benchmark to wait on.</summary>
        public Task Applying { get; private set; } = Task.CompletedTask;

        TerrainEditSession(OpenedResort resort, DesignHistory history, TerrainEditPipeline pipeline)
        {
            _resort = resort;
            History = history;
            Pipeline = pipeline;
            Preview = new PendingContourView(resort.Root.transform);
            Earthwork = new DesignEarthwork(pipeline.Heights.Source,
                                            new EarthworkSite(pipeline.Heights.Lattice, resort.Frame.Origin.X, resort.Frame.Origin.Y));
            Prime();
            History.Changed += OnChanged;
            Pipeline.LabelsChanged += labels => _resort.ContourLabels = labels;
        }

        /// <summary>
        /// Starts once the mountain is fully painted (its cover, snowpack and forest are in), then lays the design's
        /// current edits over it, if it has any (a loaded save).
        /// </summary>
        public static async Task<TerrainEditSession> StartAsync(OpenedResort resort, DesignHistory history, CancellationToken ct)
        {
            await resort.CoverReady;
            ct.ThrowIfCancellationRequested();
            var pipeline = await TerrainEditPipeline.PrepareAsync(resort.PackageFolder, resort.Manifest, resort.Cache, resort.Frame, resort.Tiles, ct);
            if (resort.Root == null)
            {
                pipeline.Dispose();
                throw new OperationCanceledException();
            }
            if (resort.Forest != null) pipeline.AttachForest(resort.Forest, resort.TreeInstances, resort.TreeRanges);
            pipeline.AttachCliffs(resort.Cliffs);
            if (resort.SnowElevation != null)
                pipeline.AttachSnow(resort.States, resort.SnowElevation, resort.SnowCanopy, resort.Manifest.Crs.GridConvergenceDegrees, resort.Ring);
            var session = new TerrainEditSession(resort, history, pipeline);
            if (!history.Terrain.IsEmpty) await (session.Applying = pipeline.ApplyAsync(history.Terrain, ct));
            return session;
        }

        void Prime()
        {
            _loads = History.Loads;
            var doc = History.Document;
            var terrain = History.Terrain;
            var earthwork = Earthwork;
            Priming = Task.Run(() => earthwork.Prime(doc, terrain));
        }

        void OnChanged(DesignSnapshot snapshot)
        {
            if (History.Loads != _loads) Prime();   // a loaded save: learn its design again
            ClearPreview();
            Applying = Pipeline.ApplyAsync(snapshot.Terrain);
        }

        /// <summary>The fall line at a local point: the downhill direction on the edited ground (x east, z north).</summary>
        public Vector2 Downhill(float x, float z)
        {
            var h = Pipeline.Heights;
            var p = _resort.Frame.ToAlbers(x, z);
            double east = h.At(p.X + 5, p.Y) - h.At(p.X - 5, p.Y), north = h.At(p.X, p.Y + 5) - h.At(p.X, p.Y - 5);
            var d = new Vector2((float)-east, (float)-north);
            return d.sqrMagnitude > 1e-6f ? d.normalized : new Vector2(0, -1);
        }

        /// <summary>
        /// Grades the test bench from a local head point along a direction, as a pending edit: computes it, draws its
        /// changed contours in yellow and returns the milliseconds that took (CF-09's preview). The ground is unchanged.
        /// </summary>
        public double PreviewBench(Vector2 head, Vector2 direction)
        {
            var watch = Stopwatch.StartNew();
            var d = direction.normalized;
            var a = _resort.Frame.ToAlbers(head.x, head.y);
            var b = _resort.Frame.ToAlbers(head.x + d.x * (float)BenchLengthMetres, head.y + d.y * (float)BenchLengthMetres);
            var current = Pipeline.Heights;
            var result = TestBench.Build(current, a.X, a.Y, b.X, b.Y, BenchWidthMetres);
            var pending = new EditedHeights(current.Source, current.Lattice, result.Edits);
            PendingContours.Build(current, pending, result.Tiles, UnitFormat.ContourIntervalMetres(DisplayUnits.Current), _segments);
            Preview.Show(_segments, _resort.Frame);
            _pending = result;
            _pendingLine = (head, head + d * (float)BenchLengthMetres);
            return watch.Elapsed.TotalMilliseconds;
        }

        public bool HasPending => _pending.HasValue;

        /// <summary>Draws a tool's pending contours (task 08), from <see cref="PendingContours"/> segments in package metres.</summary>
        public void ShowPending(List<double> segments) => Preview.Show(segments, _resort.Frame);

        public void ClearPreview()
        {
            _pending = null;
            Preview.Clear();
        }

        /// <summary>
        /// Break ground on the pending bench: one command (a built, graded trail along the bench's line) with its edits, so
        /// undo takes back both (CF-05). Returns false if nothing is pending or the command is rejected.
        /// </summary>
        public bool CommitBench(string name = null)
        {
            if (!_pending.HasValue) return false;
            var result = _pending.Value;
            // Design points are millimetres from the site's centre on the package grid's axes: absolute EPSG:6350
            // millimetres overflow an int (Jackson Hole's northing is about 2,500 km).
            var (a, b) = _pendingLine;
            var head = PointMm.FromMetres(a.x, a.y);
            var tail = PointMm.FromMetres(b.x, b.y);
            int width = DesignUnits.FeetToMm(100);
            var parts = new[] { new FootprintPart(new[] { new Stroke(width, false, new[] { head, tail }) }, Array.Empty<Cut>()) };
            var command = new AddTrail(History.Revision, name ?? $"Test bench {History.Document.Trails.Count + 1}", TrailSettings.GradedRun,
                                       parts, head, tail, WorkStage.Built);
            var r = History.Submit(command, result.Edits);
            if (!r.Accepted) UnityEngine.Debug.LogWarning($"[TerrainEdits] the test bench was rejected: {r}");
            return r.Accepted;
        }

        /// <summary>
        /// Captures only (-regrade, task 08 review): grades the open design afresh with this build's rules and shows the
        /// result, so a save made by an older build can be judged as this one would grade it. Display only: the history,
        /// and so the save, keep the edits they had.
        /// </summary>
        public void RegradeForCapture()
        {
            var doc = History.Document;
            var site = Earthwork.Site;
            var source = Pipeline.Heights.Source;
            var work = Task.Run(() =>
            {
                var fresh = new DesignEarthwork(source, site);
                fresh.Prime(DesignDocument.Empty(doc.Seed), TerrainEdits.Empty);
                return fresh.Rebuild(doc, TerrainEdits.Empty).Edits;
            });
            Applying = work.ContinueWith(t => Pipeline.ApplyAsync(t.Result), TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
            UnityEngine.Debug.Log($"[TerrainEdits] regrading {doc.Trails.Count} trails with this build's rules (display only)");
        }

        public void Dispose()
        {
            History.Changed -= OnChanged;
            Preview.Dispose();
            Pipeline.Dispose();
        }
    }
}
