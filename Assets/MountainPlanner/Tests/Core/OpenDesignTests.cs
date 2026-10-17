using System;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
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
            d.TerrainTiles = () => new[] { new SaveTile(3, 4, new byte[] { 1, 2, 3 }) };
            d.SaveAs("Graded", "2026-10-16T10:00:00Z", "0.3.0");
            Assert.That(d.Dirty, Is.False);
            var back = OpenDesign.Load(_root, d.SaveFolder, Area, Heights);
            Assert.That(back.LoadedTerrain.Single().Payload, Is.EqualTo(new byte[] { 1, 2, 3 }));
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
