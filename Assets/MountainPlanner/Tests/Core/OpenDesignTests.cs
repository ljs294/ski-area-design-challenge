using System;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Persistence.Saves;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests.Core
{
    /// <summary>Task P3-04: the open design's save rules (SV1, SV2): named saves, the Autosave slot, unsaved changes.</summary>
    public sealed class OpenDesignTests
    {
        const string Area = "5792676e513f5302", Heights = "abc123";
        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "mp-open-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void ANewDesignIsCleanUntilSomethingChanges()
        {
            var d = OpenDesign.New(_root, Area, Heights);
            Assert.That(d.Dirty, Is.False);
            Assert.That(d.HasName, Is.False, "the first Save asks for a name");
            Assert.That(d.History.Document.Seed, Is.EqualTo(OpenDesign.New(_root, Area, Heights).History.Document.Seed), "the same area always starts the same (SH-04)");
            int raised = 0;
            d.DirtyChanged += () => raised++;
            AddLift(d.History, "Chair 1", P(0, 0), P(1000, 2000));
            Assert.That(d.Dirty, Is.True);
            Assert.That(raised, Is.EqualTo(1));
            d.History.Undo();
            Assert.That(d.Dirty, Is.False, "undoing back to the saved document leaves nothing to save");
        }

        sealed class Rolling : IHeightSource
        {
            public double At(double x, double y) => 2000 + 0.25 * (y - 4_200_000) + 6 * Math.Sin(x / 37.0) * Math.Cos(y / 53.0);
        }

        [Test]
        public void AnOlderSaveIsRegradedOnceTheSameWayAndOnlyWrittenWhenSaved()
        {
            // Save v8 (sections finding 7; Master Planner's conditions): opening a v7 save grades its built work afresh
            // from the ground, deterministically; nothing is written until the player saves; the v8 save then opens as is.
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v7-save")), "Saves", Area, "save-001");
            string copy = Path.Combine(_root, "Saves", Area, "save-001");
            foreach (string f in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(copy, Path.GetRelativePath(fixture, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(f, to);
            }
            byte[] before = File.ReadAllBytes(Path.Combine(copy, SaveFile.FileName));
            var lattice = new EditLattice(500_000, 4_200_000);
            var site = new EarthworkSite(lattice, 503_000, 4_197_000);

            var first = OpenDesign.Load(_root, copy, Area, "");
            var second = OpenDesign.Load(_root, copy, Area, "");
            Assert.That(first.MigratedFrom, Is.EqualTo(7));
            Assert.That(first.NeedsRegrade, Is.True);
            var a = DesignEarthwork.Regrade(new Rolling(), site, first.History.Document);
            var b = DesignEarthwork.Regrade(new Rolling(), site, second.History.Document);
            Assert.That(TerrainEditBytes.Encode(b), Is.EqualTo(TerrainEditBytes.Encode(a)), "the same save regrades to the same heights, byte for byte");
            Assert.That(a.IsEmpty, Is.False);

            first.Regraded(a);
            Assert.That(first.NeedsRegrade, Is.False, "once");
            Assert.That(first.History.Terrain, Is.SameAs(a));
            Assert.That(first.Dirty, Is.False, "an autosave writes nothing for it");
            Assert.That(first.Autosave("2026-10-28T10:00:00Z", "0.3.0"), Is.False);
            Assert.That(File.ReadAllBytes(Path.Combine(copy, SaveFile.FileName)), Is.EqualTo(before), "the old file is untouched until the player saves");

            Assert.That(first.Save("2026-10-28T10:05:00Z", "0.3.0"), Is.True);
            var again = OpenDesign.Load(_root, copy, Area, "");
            Assert.That(again.MigratedFrom, Is.EqualTo(SaveFile.CurrentVersion));
            Assert.That(again.NeedsRegrade, Is.False, "saved as v8, it opens with no further regrade");
            Assert.That(TerrainEditBytes.Encode(again.History.Terrain), Is.EqualTo(TerrainEditBytes.Encode(a)));
            Assert.That(DesignBytes.Same(DesignBytes.Encode(again.History.Document), DesignBytes.Encode(first.History.Document)), Is.True);
        }

        [Test]
        public void SaveAsThenSaveWritesOneNamedSave()
        {
            var d = OpenDesign.New(_root, Area, Heights);
            AddLift(d.History, "Chair 1", P(0, 0), P(1000, 2000));
            Assert.That(d.Save("2026-10-16T10:00:00Z", "0.3.0"), Is.False, "no name yet");
            Assert.That(d.SaveAs("My plan", "2026-10-16T10:00:00Z", "0.3.0"), Is.True);
            Assert.That(d.Dirty, Is.False);
            AddTrail(d.History, "Run 1", P(1000, 2000), P(0, 0), WorkStage.Plan);
            Assert.That(d.Save("2026-10-16T10:05:00Z", "0.3.0"), Is.True);

            var list = SaveStore.List(_root, Area);
            Assert.That(list.Select(s => (s.Name, s.Trails, s.Lifts)), Is.EqualTo(new[] { ("My plan", 1, 1) }));
            var back = OpenDesign.Load(_root, list[0].Folder, Area, Heights);
            Assert.That(DesignBytes.Same(DesignBytes.Encode(back.History.Document), DesignBytes.Encode(d.History.Document)), Is.True);
            Assert.That(back.SaveName, Is.EqualTo("My plan"));
            Assert.That(back.HasName, Is.True, "Ctrl+S goes on writing to the save it was opened from");
            Assert.That(back.History.CanUndo, Is.False, "the undo history isn't saved (DD1)");
        }

        [Test]
        public void AutosaveWritesOnlyItsOwnSlotAndOnlyWhenChanged()
        {
            var d = OpenDesign.New(_root, Area, Heights);
            Assert.That(d.Autosave("2026-10-16T10:00:00Z", "0.3.0"), Is.False, "nothing changed");
            AddLift(d.History, "Chair 1", P(0, 0), P(1000, 2000));
            d.SaveAs("Named", "2026-10-16T10:00:00Z", "0.3.0");
            AddLift(d.History, "Chair 2", P(2000, 0), P(3000, 2000));
            Assert.That(d.Autosave("2026-10-16T10:10:00Z", "0.3.0"), Is.True);
            Assert.That(d.Dirty, Is.False);

            var list = SaveStore.List(_root, Area);
            Assert.That(list.Select(s => (s.Name, s.Autosave, s.Lifts)), Is.EqualTo(new[] { ("Autosave", true, 2), ("Named", false, 1) }),
                "the named save keeps its one lift (SV1)");
            Assert.That(SaveStore.Newest(_root, Area).Name, Is.EqualTo("Autosave"), "Continue opens the newest save");

            var fromAuto = OpenDesign.Load(_root, list[0].Folder, Area, Heights);
            Assert.That(fromAuto.HasName, Is.False, "the Autosave slot asks for a name on Ctrl+S");
        }

        [Test]
        public void TerrainEditsCountAsChanges()
        {
            var d = OpenDesign.New(_root, Area, Heights);
            d.MarkChanged();
            Assert.That(d.Dirty, Is.True);
            var edits = TerrainEdits.From(new[] { new TerrainEditTile.Builder(new EditTileKey(3, 4)).SetDeltaMm(10, 20, -1500).SetCleared(5, 5, true).Build() });
            d.Edits = () => edits;
            d.SaveAs("Graded", "2026-10-16T10:00:00Z", "0.3.0");
            Assert.That(d.Dirty, Is.False);
            var back = OpenDesign.Load(_root, d.SaveFolder, Area, Heights);
            Assert.That(back.LoadedEdits.DeltaMmAt(3 * 64 + 10, 4 * 64 + 20), Is.EqualTo(-1500));
            Assert.That(back.LoadedEdits.IsClearedAt(3 * 64 + 5, 4 * 64 + 5), Is.True);
            back.SaveAs("Again", "2026-10-16T10:01:00Z", "0.3.0");   // the loaded edits are the history's, so they're kept
            Assert.That(OpenDesign.Load(_root, back.SaveFolder, Area, Heights).LoadedEdits.Count, Is.EqualTo(1));
        }

        [Test]
        public void TE2_TheHistoryHoldsTheSavesEditsAndUndoMovesThemWithTheDocument()
        {
            var d = OpenDesign.New(_root, Area, Heights);
            var edits = TerrainEdits.From(new[] { new TerrainEditTile.Builder(new EditTileKey(2, 2)).SetDeltaMm(1, 1, 700).Build() });
            var add = new AddTrail(d.History.Revision, "Bench", TrailSettings.GradedRun,
                                   TrailShape.FromStrokes(null, new[] { new Stroke(DesignUnits.FeetToMm(100), false, new[] { new PointMm(0, 0), new PointMm(0, -300_000) }) }),
                                   new PointMm(0, 0), new PointMm(0, -300_000), WorkStage.Built);
            Assert.That(d.History.Submit(add, edits).Accepted, Is.True);
            Assert.That(d.Dirty, Is.True);
            d.SaveAs("Bench", "2026-10-17T10:00:00Z", "0.3.0");
            Assert.That(d.Dirty, Is.False);

            var back = OpenDesign.Load(_root, d.SaveFolder, Area, Heights);
            Assert.That(back.History.Terrain.DeltaMmAt(2 * 64 + 1, 2 * 64 + 1), Is.EqualTo(700), "the save's edits start the history");
            Assert.That(back.Dirty, Is.False, "a fresh load is clean");
            Assert.That(back.History.CanUndo, Is.False, "the history starts at the save (DD1)");

            // A terrain-only change (a load of other edits) counts as a change too.
            back.History.Load(back.History.Document, TerrainEdits.Empty);
            Assert.That(back.Dirty, Is.True);

            // Undo takes the edits back with the trail, and the save then holds none.
            d.History.Undo();
            Assert.That(d.History.Terrain.IsEmpty, Is.True);
            Assert.That(d.Dirty, Is.True);
            d.Save("2026-10-17T10:01:00Z", "0.3.0");
            Assert.That(OpenDesign.Load(_root, d.SaveFolder, Area, Heights).History.Terrain.IsEmpty, Is.True);
        }

        [Test]
        public void ASaveNeverOpensOnAnotherAreaOrOtherTerrain()
        {
            var d = OpenDesign.New(_root, Area, Heights);
            d.SaveAs("Here", "2026-10-16T10:00:00Z", "0.3.0");
            Assert.Throws<InvalidDataException>(() => OpenDesign.Load(_root, d.SaveFolder, "someotherarea", Heights));
            Assert.Throws<InvalidDataException>(() => OpenDesign.Load(_root, d.SaveFolder, Area, "different"));
        }
    }
}
