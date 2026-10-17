using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.App.Edits;
using MountainPlanner.Domain.Design;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-05 in Unity, on the committed Jackson Hole 2 km test terrain: a test bench previews without touching the
    /// ground (TE3), breaks ground into the touched tiles only, and undo puts every height back bit for bit (GR-09, CF-05);
    /// redo gives the same edited heights again (SH-04); the HUD's readout reads the edited heights (SH-02).
    /// </summary>
    public sealed class TerrainEditsInUnityTests
    {
        string _package;

        static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        [OneTimeSetUp]
        public void CopyTestTerrain()
        {
            string source = Path.Combine(ProjectRoot, "TestData", "jackson-hole-2km");
            if (!Directory.Exists(source)) Assert.Ignore("TestData/jackson-hole-2km is not available.");
            if (new FileInfo(Path.Combine(source, "heights-core.grid")).Length < 1000) Assert.Ignore("Git LFS hasn't fetched the test terrain.");
            _package = Path.Combine(Path.GetTempPath(), "mp-unity-edits-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_package);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(_package, Path.GetFileName(f)));
        }

        [OneTimeTearDown]
        public void Clean()
        {
            Http.NetworkDisabled = false;
            if (_package != null && Directory.Exists(_package)) Directory.Delete(_package, true);
        }

        static IEnumerator AwaitTask(Task task)
        {
            while (!task.IsCompleted) yield return null;
            if (task.IsFaulted) throw task.Exception!.InnerException!;
        }

        static Dictionary<(int, int), float[,]> Heights(OpenedResort resort)
        {
            var all = new Dictionary<(int, int), float[,]>();
            foreach (var kv in resort.Tiles)
            {
                var d = kv.Value.terrainData;
                all[kv.Key] = d.GetHeights(0, 0, d.heightmapResolution, d.heightmapResolution);
            }
            return all;
        }

        /// <summary>The tiles whose heights differ from <paramref name="a"/>, comparing bits.</summary>
        static List<(int, int)> Differ(Dictionary<(int, int), float[,]> a, Dictionary<(int, int), float[,]> b)
        {
            var tiles = new List<(int, int)>();
            foreach (var kv in a)
            {
                var x = kv.Value;
                var y = b[kv.Key];
                bool same = true;
                for (int j = 0; j < x.GetLength(0) && same; j++)
                    for (int i = 0; i < x.GetLength(1); i++)
                        if (BitConverter.SingleToInt32Bits(x[j, i]) != BitConverter.SingleToInt32Bits(y[j, i])) { same = false; break; }
                if (!same) tiles.Add(kv.Key);
            }
            return tiles;
        }

        [UnityTest]
        public IEnumerator ABenchBreaksGroundInTheTouchedTilesAndUndoRestoresEveryHeightExactly()
        {
            Http.NetworkDisabled = true;
            var open = ResortOpener.OpenAsync(_package, null, TerrainDetail.High, null);
            while (!open.IsCompleted) yield return null;
            if (open.IsFaulted) throw open.Exception!.InnerException!;
            var resort = open.Result;
            TerrainEditSession session = null;
            try
            {
                yield return AwaitTask(resort.CoverReady);
                var start = TerrainEditSession.StartAsync(resort, new DesignHistory(DesignDocument.Empty(5)), default);
                yield return AwaitTask(start);
                session = start.Result;
                var natural = Heights(resort);
                float before = session.Pipeline.HeightAtLocal(0, 0);
                Assert.That(before, Is.EqualTo(resort.Surface.HeightAt(0, 0)).Within(0.1f), "the float32 readout agrees with the terrain");

                // Preview: yellow lines, the ground untouched (TE3).
                var dir = session.Downhill(0, 150);
                double previewMs = session.PreviewBench(new Vector2(0, 150), dir);
                TestContext.Progress.WriteLine($"preview {previewMs:F1} ms, {session.Preview.Segments} contour segments");
                Assert.That(session.Preview.Segments, Is.GreaterThan(0), "the pending edit's contours are drawn");
                Assert.That(Differ(natural, Heights(resort)), Is.Empty, "a preview changes no ground");

                // Break ground: one command, its tiles rebuilt.
                Assert.That(session.CommitBench(), Is.True);
                yield return AwaitTask(session.Applying);
                Assert.That(session.Preview.Segments, Is.EqualTo(0), "committed: no longer a proposal (CF-08)");
                var edited = Heights(resort);
                var changed = Differ(natural, edited);
                var r = session.Pipeline.Last;
                TestContext.Progress.WriteLine($"commit: {r.Patches} patches, {r.Posts} posts, {r.Texels} texels; tiles changed {string.Join(" ", changed)}; " +
                                               $"plan {r.PlanMs:F1} ms, total {r.TotalMs:F1} ms");
                Assert.That(changed, Is.Not.Empty, "the bench changed the ground");
                Assert.That(changed.Count, Is.LessThanOrEqualTo(4), "only the tiles under a 300 m bench");
                foreach (var key in changed) Assert.That(resort.Cache.Tiles.Find(t => (t.Column, t.Row) == key).Core, Is.True, "edits stay in the core (SH-06)");
                Assert.That(session.History.Document.Trails.Count, Is.EqualTo(1));

                // Undo: every height back, bit for bit (GR-09, CF-05).
                session.History.Undo();
                yield return AwaitTask(session.Applying);
                Assert.That(Differ(natural, Heights(resort)), Is.Empty, "undo restores byte-identical heights");
                Assert.That(session.History.Document.Trails.Count, Is.EqualTo(0));
                Assert.That(session.Pipeline.HeightAtLocal(0, 0), Is.EqualTo(before), "the readout is back too");

                // Redo: the same edited heights again (SH-04).
                session.History.Redo();
                yield return AwaitTask(session.Applying);
                Assert.That(Differ(edited, Heights(resort)), Is.Empty, "redo gives the same heights");

                // A loaded save: a new session whose history starts from the saved edits lays them over the natural
                // mountain, giving exactly the heights the commit gave (task P3-04's OpenDesign does this on open).
                var saved = session.History.Terrain;
                session.History.Undo();
                yield return AwaitTask(session.Applying);
                Assert.That(Differ(natural, Heights(resort)), Is.Empty);
                session.Dispose();
                var loaded = new DesignHistory(DesignDocument.Empty(5));
                loaded.Load(loaded.Document, saved);
                var again = TerrainEditSession.StartAsync(resort, loaded, default);
                yield return AwaitTask(again);
                session = again.Result;
                Assert.That(Differ(edited, Heights(resort)), Is.Empty, "a save's edits open as they were committed");
            }
            finally
            {
                session?.Dispose();
                resort.CacheLease?.Dispose();
                Object.Destroy(resort.Root);
            }
        }
    }
}
