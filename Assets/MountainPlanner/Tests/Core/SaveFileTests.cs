using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Saves;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using static MountainPlanner.Tests.DesignTestKit;

namespace MountainPlanner.Tests.Core
{
    /// <summary>
    /// Task P3-04: the save file. Save, quit and reopen give an identical document and identical tiles; a crash mid-save
    /// leaves the last good save; a newer save is refused and never overwritten; the v1 fixture reads unchanged (SH-05,
    /// GR-09, CF-06).
    /// </summary>
    public sealed class SaveFileTests
    {
        const string JacksonHole = "5792676e513f5302";
        const string FixtureUtc = "2026-10-16T18:30:00Z";

        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "mp-saves-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        /// <summary>A stand-in height delta tile: 64 × 64 little-endian float32 metres (task 05 owns the real type).</summary>
        static SaveTile HeightTile(int x, int y, float scale)
        {
            var b = new byte[64 * 64 * 4];
            for (int i = 0; i < 64 * 64; i++)
            {
                float v = scale * ((i % 64) - (i / 64)) * 0.001f;
                BitConverter.GetBytes(v).CopyTo(b, i * 4);
                if (!BitConverter.IsLittleEndian) Array.Reverse(b, i * 4, 4);
            }
            return new SaveTile(x, y, b);
        }

        /// <summary>A stand-in clearing tile: 64 × 64 bytes, 255 where trees are cleared.</summary>
        static SaveTile ClearingTile(int x, int y)
        {
            var b = new byte[64 * 64];
            for (int i = 0; i < b.Length; i++) b[i] = (byte)((i % 64) < 20 ? 255 : 0);
            return new SaveTile(x, y, b);
        }

        /// <summary>The save the v1 fixture holds: the P3-03 scripted design plus a plan (CF-06) and three tiles.</summary>
        static DesignDocument FixtureDesign()
        {
            var h = Scripted();
            AddTrail(h, "Planned glade", P(3000, 2500), P(2600, 100), WorkStage.Plan);
            return h.Document;
        }

        static SaveFile FixtureSave() => new SaveFile
        {
            Name = "Teton plan", SavedUtc = FixtureUtc, GameVersion = "0.2.0", PackageId = JacksonHole,
            HeightsSha256 = "52e1cd2c5c4f1d0bd0f2de3ebd3b0b5b9bb2a2dbd5e8e5d7bd1b2f3a1c4d5e6f",
            Design = FixtureDesign(),
            Terrain = new List<SaveTile> { HeightTile(12, 7, 1), HeightTile(11, 7, -2) },
            Clearing = new List<SaveTile> { ClearingTile(12, 7) },
        };

        static void AssertSame(SaveFile a, SaveFile b)
        {
            Assert.That(DesignBytes.Same(DesignBytes.Encode(a.Design), DesignBytes.Encode(b.Design)), Is.True, "identical document bytes");
            Assert.That(b.Name, Is.EqualTo(a.Name));
            Assert.That(b.Autosave, Is.EqualTo(a.Autosave));
            Assert.That(b.SavedUtc, Is.EqualTo(a.SavedUtc));
            Assert.That(b.PackageId, Is.EqualTo(a.PackageId));
            Assert.That(b.HeightsSha256, Is.EqualTo(a.HeightsSha256));
            void Tiles(List<SaveTile> x, List<SaveTile> y)
            {
                var sx = x.OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
                Assert.That(y.Count, Is.EqualTo(sx.Count));
                for (int i = 0; i < sx.Count; i++)
                {
                    Assert.That((y[i].X, y[i].Y), Is.EqualTo((sx[i].X, sx[i].Y)));
                    Assert.That(y[i].Payload, Is.EqualTo(sx[i].Payload), "identical heights, byte for byte (GR-09)");
                }
            }
            Tiles(a.Terrain, b.Terrain);
            Tiles(a.Clearing, b.Clearing);
        }

        [Test]
        public void SaveQuitAndReopenGivesAnIdenticalDocumentAndHeights()
        {
            var save = FixtureSave();
            string folder = SaveStore.NewSaveFolder(_root, JacksonHole);
            Assert.That(SaveStore.Write(folder, save), Is.True);

            var back = SaveStore.Read(folder);   // a fresh read: nothing shared with the writer but the files
            AssertSame(save, back);
            Assert.That(back.Recovered, Is.Empty);
            Assert.That(back.Design.Trails.Any(t => t.Stage == WorkStage.Plan), Is.True,
                "plans are saved with the design (CF-06)");
        }

        [Test]
        public void SH05_TheSaveHoldsOnlyAuthoredData()
        {
            string folder = SaveStore.NewSaveFolder(_root, JacksonHole);
            SaveStore.Write(folder, FixtureSave());
            var o = JObject.Parse(File.ReadAllText(Path.Combine(folder, SaveFile.FileName)));
            Assert.That(o.Properties().Select(p => p.Name), Is.EqualTo(new[] { "Version", "Name", "Autosave", "SavedUtc", "GameVersion", "PackageId", "HeightsSha256", "Design", "Terrain", "Clearing" }));
            Assert.That(((JObject)o["Design"]).Properties().Select(p => p.Name), Is.EqualTo(new[] { "Shape", "Seed", "NextOrdinal", "Trails", "Lifts" }),
                "no network, stats, ratings, rope or volumes (SH-05); no undo history (DD1)");
            Assert.That(((JObject)o["Design"]["Lifts"][0]).Properties().Select(p => p.Name),
                Is.EqualTo(new[] { "Id", "Name", "Stage", "Catalog", "Bottom", "Top", "TowerMode", "Stance", "Towers" }), "tower positions, heights and stance are authored (SH-05, TW-15)");
        }

        [Test]
        public void TheSameSaveAlwaysGivesTheSameBytes()
        {
            string a = SaveStore.NewSaveFolder(_root, JacksonHole);
            SaveStore.Write(a, FixtureSave());
            string b = Path.Combine(_root, "other");
            SaveStore.Write(b, FixtureSave());
            Assert.That(File.ReadAllBytes(Path.Combine(b, SaveFile.FileName)), Is.EqualTo(File.ReadAllBytes(Path.Combine(a, SaveFile.FileName))));
            Assert.That(Directory.GetFiles(Path.Combine(b, SaveStore.TilesFolder)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal),
                Is.EqualTo(Directory.GetFiles(Path.Combine(a, SaveStore.TilesFolder)).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)));
        }

        [Test]
        public void TheWriterReproducesTheFrozenFixtureByteForByte()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v1-save")), "Saves", JacksonHole, "save-001");
            string folder = Path.Combine(_root, "again");
            SaveStore.Write(folder, FixtureSave());
            Assert.That(File.ReadAllBytes(Path.Combine(folder, SaveFile.FileName)), Is.EqualTo(File.ReadAllBytes(Path.Combine(fixture, SaveFile.FileName))),
                "save.json v1 is frozen: a writer change is a format change (0.3 §5)");
            foreach (string tile in Directory.GetFiles(Path.Combine(fixture, SaveStore.TilesFolder)))
                Assert.That(File.ReadAllBytes(Path.Combine(folder, SaveStore.TilesFolder, Path.GetFileName(tile))), Is.EqualTo(File.ReadAllBytes(tile)));
        }

        [Test]
        public void TheVersionOneFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v1-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture");
            CopyFolder(fixture, copy);
            AssertSame(FixtureSave(), SaveStore.Read(copy));
            var listed = SaveStore.List(Path.Combine(TestData.Folder(Path.Combine("formats", "v1-save"))), JacksonHole);
            Assert.That(listed.Select(s => (s.Name, s.Trails, s.Lifts, s.SavedUtc)), Is.EqualTo(new[] { ("Teton plan", 4, 2, FixtureUtc) }));
        }

        [TestCase(SaveStep.TilesWritten)]
        [TestCase(SaveStep.TempWritten)]
        public void ACrashMidSaveLeavesTheLastGoodSave(SaveStep crashAt)
        {
            string folder = SaveStore.NewSaveFolder(_root, JacksonHole);
            var good = FixtureSave();
            SaveStore.Write(folder, good);

            var next = FixtureSave();
            next.Name = "Half written";
            next.Design = DesignDocument.Empty(7);
            next.Terrain = new List<SaveTile> { HeightTile(40, 40, 9) };
            Assert.Throws<IOException>(() => SaveStore.Write(folder, next, s => { if (s == crashAt) throw new IOException("power cut"); }));

            var back = SaveStore.Read(folder);
            AssertSame(good, back);
            Assert.That(back.Recovered, Is.Empty, "save.json itself is the last good save: no backup needed");

            // The next save clears what the crash left.
            Assert.That(SaveStore.Write(folder, next), Is.True);
            AssertSame(next, SaveStore.Read(folder));
            Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal),
                Is.EqualTo(new[] { SaveFile.FileName, SaveFile.BackupName }), "no temporary files left");
        }

        [Test]
        public void ACrashRightAfterTheCommitKeepsTheNewSave()
        {
            string folder = SaveStore.NewSaveFolder(_root, JacksonHole);
            SaveStore.Write(folder, FixtureSave());
            var next = FixtureSave();
            next.Name = "Committed";
            Assert.Throws<IOException>(() => SaveStore.Write(folder, next, s => { if (s == SaveStep.Committed) throw new IOException("power cut"); }));
            Assert.That(SaveStore.Read(folder).Name, Is.EqualTo("Committed"));
        }

        [Test]
        public void ADamagedSaveFallsBackToTheBackup()
        {
            string folder = SaveStore.NewSaveFolder(_root, JacksonHole);
            var first = FixtureSave();
            SaveStore.Write(folder, first);
            var second = FixtureSave();
            second.Name = "Second";
            second.Terrain = new List<SaveTile> { HeightTile(1, 1, 5) };
            SaveStore.Write(folder, second);

            File.WriteAllText(Path.Combine(folder, SaveFile.FileName), "{ \"Version\": 1, \"Name\": ");   // torn by a disk error
            var back = SaveStore.Read(folder);
            AssertSame(first, back);
            Assert.That(back.Recovered, Does.Contain("JSON"));
            Assert.That(SaveStore.List(_root, JacksonHole).Single().FromBackup, Is.True);
        }

        [Test]
        public void AMissingOrDamagedTileFallsBackToTheBackup()
        {
            string folder = SaveStore.NewSaveFolder(_root, JacksonHole);
            var first = FixtureSave();
            SaveStore.Write(folder, first);
            var second = FixtureSave();
            second.Terrain = new List<SaveTile> { HeightTile(3, 3, 4) };
            SaveStore.Write(folder, second);
            string only = Path.Combine(folder, SaveStore.TilesFolder, SaveTileFile.Hash(SaveTileFile.Encode(SaveTileKind.Terrain, second.Terrain[0])) + SaveTileFile.Extension);
            var bytes = File.ReadAllBytes(only);
            bytes[bytes.Length - 1] ^= 1;
            File.WriteAllBytes(only, bytes);

            var back = SaveStore.Read(folder);
            AssertSame(first, back);
            Assert.That(back.Recovered, Does.Contain("damaged"));
        }

        [Test]
        public void AFutureVersionSaveIsRefusedClearlyAndNeverOverwritten()
        {
            string folder = SaveStore.NewSaveFolder(_root, JacksonHole);
            SaveStore.Write(folder, FixtureSave());
            string path = Path.Combine(folder, SaveFile.FileName);
            var o = JObject.Parse(File.ReadAllText(path));
            o["Version"] = 2;
            File.WriteAllText(path, o.ToString());
            byte[] before = File.ReadAllBytes(path);

            var e = Assert.Throws<FormatTooNewException>(() => SaveStore.Read(folder));
            Assert.That(e.Message, Is.EqualTo("This save was saved by a newer version of Mountain Planner (format 2; this version reads up to 1). Update the game to open it."));
            Assert.That(SaveStore.Write(folder, FixtureSave()), Is.False);
            Assert.That(SaveStore.Rename(folder, "Mine now"), Is.False);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before), "nothing is written over a newer save (0.3 §5 rule 3)");
            var listed = SaveStore.List(_root, JacksonHole).Single();
            Assert.That(listed.Refusal, Is.EqualTo(e.Message));
            Assert.That(listed.Name, Is.EqualTo("Teton plan"));
            Assert.That(SaveStore.Newest(_root, JacksonHole), Is.Null, "Continue skips a save it can't open");
        }

        [Test]
        public void LoadAreaListsAnAreasSavesNewestFirst()
        {
            void Save(string folder, string name, string utc, bool auto = false)
            {
                var s = FixtureSave();
                s.Name = name;
                s.SavedUtc = utc;
                s.Autosave = auto;
                SaveStore.Write(folder, s);
            }
            Save(SaveStore.NewSaveFolder(_root, JacksonHole), "Older", "2026-10-14T09:00:00Z");
            Save(SaveStore.NewSaveFolder(_root, JacksonHole), "Newer", "2026-10-16T09:00:00Z");
            Save(SaveStore.AutosaveFolder(_root, JacksonHole), "Autosave", "2026-10-15T09:00:00Z", auto: true);
            Save(SaveStore.NewSaveFolder(_root, "otherarea"), "Elsewhere", "2026-10-16T10:00:00Z");

            var list = SaveStore.List(_root, JacksonHole);
            Assert.That(list.Select(s => s.Name), Is.EqualTo(new[] { "Newer", "Autosave", "Older" }));
            Assert.That(list.Select(s => s.Autosave), Is.EqualTo(new[] { false, true, false }));
            Assert.That(list.Select(s => Path.GetFileName(s.Folder)), Is.EqualTo(new[] { "save-002", "autosave", "save-001" }));
            Assert.That(SaveStore.Newest(_root, JacksonHole).Name, Is.EqualTo("Newer"));
            Assert.That(SaveStore.List(_root, "nothing-here"), Is.Empty);
        }

        [Test]
        public void RenameKeepsTheDesignAndTiles()
        {
            string folder = SaveStore.NewSaveFolder(_root, JacksonHole);
            var save = FixtureSave();
            SaveStore.Write(folder, save);
            Assert.That(SaveStore.Rename(folder, "Renamed"), Is.True);
            var back = SaveStore.Read(folder);
            save.Name = "Renamed";
            AssertSame(save, back);
            Assert.That(Path.GetFileName(folder), Is.EqualTo("save-001"), "the folder never moves");
        }

        [Test]
        public void DeleteRemovesOneSaveOrAWholeArea()
        {
            string a = SaveStore.NewSaveFolder(_root, JacksonHole);
            SaveStore.Write(a, FixtureSave());
            string b = SaveStore.NewSaveFolder(_root, JacksonHole);
            SaveStore.Write(b, FixtureSave());
            Assert.That(SaveStore.Delete(a), Is.True);
            Assert.That(SaveStore.List(_root, JacksonHole).Select(s => s.Folder), Is.EqualTo(new[] { b }));
            Assert.That(SaveStore.DeleteArea(_root, JacksonHole), Is.True);
            Assert.That(Directory.Exists(SaveStore.AreaFolder(_root, JacksonHole)), Is.False);
        }

        [Test]
        public void TilesAreCheckedForKindAndPlace()
        {
            var bytes = SaveTileFile.Encode(SaveTileKind.Clearing, ClearingTile(2, 3));
            Assert.Throws<InvalidDataException>(() => SaveTileFile.Decode(bytes, SaveTileKind.Terrain));
            var t = SaveTileFile.Decode(bytes, SaveTileKind.Clearing);
            Assert.That((t.X, t.Y), Is.EqualTo((2, 3)));
            Assert.Throws<InvalidDataException>(() => SaveTileFile.Decode(bytes.Take(bytes.Length - 1).ToArray(), SaveTileKind.Clearing));
            Assert.Throws<ArgumentException>(() => SaveStore.Write(Path.Combine(_root, "dup"), new SaveFile { Terrain = new List<SaveTile> { HeightTile(1, 1, 1), HeightTile(1, 1, 2) } }));
        }

        [Test]
        public void SavesLeaveThePhaseTwoLibraryAlone()
        {
            string source = TestData.Folder(Path.Combine("formats", "v1-library"));
            CopyFolder(source, _root);
            var before = ResortLibrary.Scan(_root).Select(e => (e.PackageId, e.Name)).ToList();
            SaveStore.Write(SaveStore.NewSaveFolder(_root, JacksonHole), FixtureSave());
            Assert.That(LibraryIndex.VersionOf(_root), Is.EqualTo(1), "a Saves folder moves nothing: library.json stays v1");
            Assert.That(ResortLibrary.Scan(_root).Select(e => (e.PackageId, e.Name)), Is.EqualTo(before));
        }

        /// <summary>Regenerates TestData/formats/v1-save. Only before the first merge: after it, v1 is frozen.</summary>
        [Test, Explicit("Regenerates the frozen v1 save fixture")]
        public void WriteTheVersionOneFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v1-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSave()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        static void CopyFolder(string source, string target)
        {
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(target, file.Substring(source.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(file, to);
            }
        }
    }
}
