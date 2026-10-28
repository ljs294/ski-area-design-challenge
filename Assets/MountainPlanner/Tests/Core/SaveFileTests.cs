using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
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

        /// <summary>A graded tile (TE1): whole-mm height changes, a sloped cut and fill, with or without clearing.</summary>
        static TerrainEditTile Tile(int column, int row, int scaleMm, bool heights = true, bool clearing = false)
        {
            var b = new TerrainEditTile.Builder(new EditTileKey(column, row));
            for (int j = 0; j < 64; j++)
                for (int i = 0; i < 64; i++)
                {
                    if (heights) b.SetDeltaMm(i, j, scaleMm * (i - j));
                    if (clearing && i < 20) b.SetCleared(i, j, true);
                }
            return b.Build();
        }

        static TerrainEdits Edits(params TerrainEditTile[] tiles) => TerrainEdits.From(tiles);

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
            // A graded and cleared tile, a graded one, and a cleared-only one (a lift line), as task 05 makes them.
            Edits = Edits(Tile(12, 7, 1, clearing: true), Tile(11, 7, -2), Tile(13, 8, 0, heights: false, clearing: true)),
        };

        static void AssertSame(SaveFile a, SaveFile b)
        {
            Assert.That(DesignBytes.Same(DesignBytes.Encode(a.Design), DesignBytes.Encode(b.Design)), Is.True, "identical document bytes");
            Assert.That(b.Name, Is.EqualTo(a.Name));
            Assert.That(b.Autosave, Is.EqualTo(a.Autosave));
            Assert.That(b.SavedUtc, Is.EqualTo(a.SavedUtc));
            Assert.That(b.PackageId, Is.EqualTo(a.PackageId));
            Assert.That(b.HeightsSha256, Is.EqualTo(a.HeightsSha256));
            Assert.That(TerrainEditBytes.Encode(b.Edits), Is.EqualTo(TerrainEditBytes.Encode(a.Edits)), "identical heights and clearing, byte for byte (GR-09)");
            foreach (var t in a.Edits.Tiles)
                for (int j = 0; j < 64; j += 7)
                    for (int i = 0; i < 64; i += 5)
                    {
                        int column = t.Key.FirstColumn + i, row = t.Key.FirstRow + j;
                        Assert.That(b.Edits.DeltaMmAt(column, row), Is.EqualTo(a.Edits.DeltaMmAt(column, row)));
                        Assert.That(b.Edits.IsClearedAt(column, row), Is.EqualTo(a.Edits.IsClearedAt(column, row)));
                    }
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
            Assert.That(o.Properties().Select(p => p.Name), Is.EqualTo(new[] { "Version", "Name", "Autosave", "SavedUtc", "GameVersion", "PackageId", "HeightsSha256", "Design", "Edits" }));
            Assert.That(((JObject)o["Design"]).Properties().Select(p => p.Name), Is.EqualTo(new[] { "Shape", "Seed", "NextOrdinal", "Trails", "Lifts", "Strokes", "Areas", "TrailLines", "Cuts" }),
                "no network, stats, ratings, rope or volumes (SH-05); no undo history (DD1)");
            Assert.That(((JObject)o["Design"]["Lifts"][0]).Properties().Select(p => p.Name),
                Is.EqualTo(new[] { "Id", "Name", "Stage", "Catalog", "Bottom", "Top", "TowerMode", "Stance", "Towers", "Levels" }),
                "tower positions, heights and stance are authored (SH-05, TW-15), and the terminals' levels (SK14, v2)");
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

        /// <summary>The save the v2 fixture holds (task 09): the v1 fixture's, its first lift with terminal levels, the top one
        /// raised 3 ft on its pier (LF-08 v2). Levels are absolute whole millimetres, unlike the site-centred points.</summary>
        static SaveFile FixtureSaveTwo()
        {
            var save = FixtureSave();
            var lift = save.Design.Lifts[0];
            save.Design = save.Design.PutLift(lift.WithLevels(new LiftLevels(2_012_345, 2_187_654 + 914)));
            return save;
        }

        /// <summary>The save the v3 fixture holds (LT44): the v2 fixture's, the towers of its lift that has them one of each kind
        /// the player can choose (the first a hold-down, the second a support, the third a combination).</summary>
        static SaveFile FixtureSaveThree()
        {
            var save = FixtureSaveTwo();
            var lift = save.Design.Lifts.First(l => l.Towers.Count > 0);
            var kinds = new[] { TowerKind.HoldDown, TowerKind.Support, TowerKind.Combination };
            var towers = lift.Towers.Select((t, i) => i < kinds.Length ? t.WithKind(kinds[i]) : t).ToList();
            save.Design = save.Design.PutLift(lift.WithTowers(towers));
            return save;
        }

        /// <summary>The save the v4 fixture holds (task 15, TB-14): the v3 fixture's, plus a session of one stroke of each
        /// kind (Raise, Lower, Flatten, Smooth, Slope), so every field and the delta-coded dabs are pinned.</summary>
        static SaveFile FixtureSaveFour()
        {
            var save = FixtureSaveThree();
            BrushDab D(int x, int y, int w = 1000) => new BrushDab(new PointMm(x, y), w);
            var strokes = new[]
            {
                new StrokePainting(BrushSettings.RaiseOrLower(false, 9144, 81), new[] { D(12_000, -40_000), D(13_500, -40_250), D(13_500, -40_250), D(15_000, -40_500, 500) }),
                new StrokePainting(BrushSettings.RaiseOrLower(true, 6096, 40), new[] { D(-8_000, 22_000), D(-8_000, 24_286) }),
                new StrokePainting(BrushSettings.Flatten(12192, 175, 2_034_118), new[] { D(30_000, 30_000, 700), D(32_286, 30_000, 700) }),
                new StrokePainting(BrushSettings.Smooth(15240, 350), new[] { D(-30_000, -5_000) }),
                new StrokePainting(BrushSettings.Slope(7620, 350, new PointMm(0, 0), 2_041_000, new PointMm(100_000, -2_000), 2_029_000), new[] { D(0, 0), D(1_905, 0), D(3_810, -12), D(5_715, -40) }),
            };
            var r = new AddTerrainStrokes(1, strokes).Apply(save.Design);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        /// <summary>The save the v5 fixture holds (task P3-10, NW-15): the v4 fixture's, plus an Area the player named.</summary>
        static SaveFile FixtureSaveFive()
        {
            var save = FixtureSaveFour();
            var lifts = save.Design.Lifts;
            var ends = new[] { new AreaEnd(lifts[0].Id, true), new AreaEnd(lifts[lifts.Count - 1].Id, true) };
            var r = new RenameArea(0, new AreaId(0), ends, "Teton Village").Apply(save.Design);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        /// <summary>
        /// The save the v6 fixture holds (task P3-10, owner NT24), as it reads in v7: the v5 fixture's, with "Lower Run 1"
        /// taken back into Run 1 (renamed so) and split once more, Run 1 a trail of the three whole runs. Built with today's
        /// commands the way B1's did it, so the frozen v6 file still compares.
        /// </summary>
        static SaveFile FixtureSaveSix()
        {
            var save = FixtureSaveFive();
            var run1 = save.Design.Trails.First(t => t.Name == "Run 1").Id;
            var lower = save.Design.Trails.First(t => t.Name == "Lower Run 1").Id;
            var doc = new RenameTrail(0, lower, "Run 1").Apply(save.Design).Document;
            var split = new SplitTrail(0, lower, DesignTestKit.P(510, 755), DesignTestKit.Ft(-980), DesignTestKit.Ft(-1490), "Run 1").Apply(doc);
            Assert.That(split.Accepted, Is.True, split.Reason);
            var lowest = new TrailId(split.Created[0]);
            var line = new TrailLine(TrailLineId.Of(doc.Seed, run1), "Run 1", new[] { Span.Whole(run1), Span.Whole(lower), Span.Whole(lowest) });
            var r = new ShapeTrails(0, new[] { line }).Apply(split.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        /// <summary>
        /// The save the v7 fixture holds (task P3-10, owner NT43–NT46): the v6 fixture's, plus a cut point and a shaped
        /// trail whose spans end at every kind of anchor (a run's head and tail, a run landing, a crossing, a lift station,
        /// a cut), with their places along the runs.
        /// </summary>
        static SaveFile FixtureSaveSeven()
        {
            var save = FixtureSaveSix();
            var d = save.Design;
            var run2 = d.Trails.First(t => t.Name == "Run 2").Id;
            var run1 = d.Trails.First(t => t.Name == "Run 1").Id;
            var lift = d.Lifts[0].Id;
            var cut = new TrailCut(run2, DesignTestKit.P(2200, 900));
            var spans = new[]
            {
                new Span(run2, Anchor.Head, Anchor.Crosses(run1, 0), 0, 243_840),
                new Span(run2, Anchor.Lands(run1, false), Anchor.Cut(cut.At), 243_840, 500_000),
                new Span(run2, Anchor.Station(lift, true), Anchor.Tail, 600_000, 0),
            };
            var r = new ShapeTrails(0, new[] { new TrailLine(TrailLines.FreshId(d, run2), "Teton Ridge", spans) }, null, new[] { cut }).Apply(d);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        /// <summary>
        /// The save the v8 fixture holds (task P3-10, sections finding 7): the v7 fixture's, with Run 2 repainted round an
        /// unpainted island, so a clearing with a hole (a clockwise ring inside its outer one) is pinned.
        /// </summary>
        static SaveFile FixtureSaveEight()
        {
            var save = FixtureSaveSeven();
            var run2 = save.Design.Trails.First(t => t.Name == "Run 2");
            var mid = new PointMm((run2.Head.Value.X + run2.Tail.Value.X) / 2, (run2.Head.Value.Y + run2.Tail.Value.Y) / 2);
            var island = new Stroke(DesignUnits.FeetToMm(40), true, new[] { mid });
            var r = new RepaintTrail(0, run2.Id, TrailShape.FromStrokes(run2.Shape, new[] { island })).Apply(save.Design);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        [Test]
        public void TheWriterReproducesTheFrozenFixtureByteForByte()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v8-save")), "Saves", JacksonHole, "save-001");
            string folder = Path.Combine(_root, "again");
            SaveStore.Write(folder, FixtureSaveEight());
            Assert.That(File.ReadAllBytes(Path.Combine(folder, SaveFile.FileName)), Is.EqualTo(File.ReadAllBytes(Path.Combine(fixture, SaveFile.FileName))),
                "save.json v8 is frozen: a writer change is a format change (0.3 §5)");
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
            next.Edits = Edits(Tile(40, 40, 9));
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
            second.Edits = Edits(Tile(1, 1, 5));
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
            second.Edits = Edits(Tile(3, 3, 4));
            SaveStore.Write(folder, second);
            string only = Path.Combine(folder, SaveStore.TilesFolder, SaveTileFile.Hash(SaveTileFile.Encode(second.Edits.Tiles[0])) + SaveTileFile.Extension);
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
            o["Version"] = SaveFile.CurrentVersion + 1;
            File.WriteAllText(path, o.ToString());
            byte[] before = File.ReadAllBytes(path);

            var e = Assert.Throws<FormatTooNewException>(() => SaveStore.Read(folder));
            Assert.That(e.Message, Is.EqualTo("This save was saved by a newer version of Mountain Planner (format 9; this version reads up to 8). Update the game to open it."));
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
        public void TileFilesRoundTripAndRefuseDamage()
        {
            foreach (var tile in new[] { Tile(2, 3, 7), Tile(-1, 4, 3, clearing: true), Tile(5, -6, 0, heights: false, clearing: true) })
            {
                var bytes = SaveTileFile.Encode(tile);
                var back = SaveTileFile.Decode(bytes);
                Assert.That(back, Is.EqualTo(tile), $"{tile} reads back equal");
                Assert.That(SaveTileFile.Encode(back), Is.EqualTo(bytes), "and writes the same bytes");
                Assert.Throws<InvalidDataException>(() => SaveTileFile.Decode(bytes.Take(bytes.Length - 1).ToArray()), "a short tile");
            }
            Assert.That(SaveTileFile.Encode(Tile(0, 0, 1)).Length, Is.EqualTo(16 + 4096 * 4), "heights only: a header and 4,096 whole-mm changes");
            Assert.That(SaveTileFile.Encode(Tile(0, 0, 0, heights: false, clearing: true)).Length, Is.EqualTo(16 + 64 * 8), "clearing only: 64 words");

            var tooDeep = SaveTileFile.Encode(Tile(0, 0, 1));
            BitConverter.GetBytes(TerrainEditTile.DeltaLimitMm + 1).CopyTo(tooDeep, 16);   // past the ±1 km limit (TE1)
            Assert.Throws<InvalidDataException>(() => SaveTileFile.Decode(tooDeep));
            var zeros = SaveTileFile.Encode(Tile(0, 0, 1));
            for (int k = 16; k < zeros.Length; k++) zeros[k] = 0;   // a heights part of all zeros isn't canonical
            Assert.Throws<InvalidDataException>(() => SaveTileFile.Decode(zeros));
            Assert.Throws<ArgumentException>(() => SaveTileFile.Encode(TerrainEditTile.Empty(new EditTileKey(0, 0))));
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
        [Test]
        public void TheVersionTwoFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v2-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture2");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveTwo(), back);
            Assert.That(back.Design.Lifts[0].Levels, Is.EqualTo(new LiftLevels(2_012_345, 2_188_568)));
            Assert.That(back.Design.Lifts[1].Levels, Is.Null);
        }

        [Test]
        public void AVersionOneSaveMigratesToTwoWithItsLiftsLevelsUnknown()
        {
            // 0.3 §5: the v1 fixture, unedited, opens through the migration; its document is the v1 one, every lift's levels
            // null (they lived in memory only before save v2) and derived from the ground when it opens, as before.
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v1-save")), "Saves", JacksonHole, "save-001");
            var o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out int from);
            Assert.That(from, Is.EqualTo(1));
            Assert.That((int)o["Version"], Is.EqualTo(SaveFile.CurrentVersion));
            Assert.That((int)o["Design"]["Shape"], Is.EqualTo(DesignDocument.ShapeVersion));
            Assert.That(((JArray)o["Design"]["Lifts"]).All(l => l["Levels"].Type == JTokenType.Null), Is.True);
            string copy = Path.Combine(_root, "v1");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(FixtureSave().Design)), Is.True);
            Assert.That(back.Design.Lifts.All(l => l.Levels == null), Is.True);
        }

        [Test]
        public void TheVersionThreeFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v3-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture3");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveThree(), back);
            Assert.That(back.Design.Lifts.First(l => l.Towers.Count > 0).Towers.Select(t => t.Kind),
                        Is.EqualTo(new[] { TowerKind.HoldDown, TowerKind.Support, TowerKind.Combination }));
        }

        [Test]
        public void AVersionTwoSaveMigratesToThreeWithEveryTowerAuto()
        {
            // 0.3 §5: the v2 fixture, unedited, opens through the migration; every tower's kind is the game's choice, as it was.
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v2-save")), "Saves", JacksonHole, "save-001");
            var o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out int from);
            Assert.That(from, Is.EqualTo(2));
            Assert.That((int)o["Version"], Is.EqualTo(SaveFile.CurrentVersion));
            Assert.That((int)o["Design"]["Shape"], Is.EqualTo(DesignDocument.ShapeVersion));
            Assert.That(((JArray)o["Design"]["Lifts"]).SelectMany(l => (JArray)l["Towers"]).All(t => (string)t["Kind"] == "auto"), Is.True);
            string copy = Path.Combine(_root, "v2");
            CopyFolder(fixture, copy);
            Assert.That(SaveStore.Read(copy).Design.Lifts.SelectMany(l => l.Towers).All(t => t.Kind == TowerKind.Auto), Is.True);
        }

        [Test]
        public void TheVersionFourFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v4-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture4");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveFour(), back);
            Assert.That(back.Design.Strokes.Select(k => k.Settings.Kind).OrderBy(k => k),
                        Is.EqualTo(new[] { BrushKind.Raise, BrushKind.Lower, BrushKind.Flatten, BrushKind.Smooth, BrushKind.Slope }));
        }

        [Test]
        public void TheVersionFiveFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v5-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture5");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveFive(), back);
            Assert.That(back.Design.Areas.Select(a => a.Name), Is.EqualTo(new[] { "Teton Village" }));
            Assert.That(back.Design.Areas[0].Ends.Count, Is.EqualTo(2));
            Assert.That(back.Design.Strokes.Count, Is.EqualTo(5));
        }

        [Test]
        public void TheVersionSixFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v6-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture6");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveSix(), back);
            Assert.That(back.Design.Lines.Count, Is.EqualTo(1));
            Assert.That(back.Design.Lines[0].Name, Is.EqualTo("Run 1"));
            Assert.That(back.Design.Lines[0].Spans.Count, Is.EqualTo(3));
            Assert.That(back.Design.Lines[0].Spans.All(s => s.From.Kind == AnchorKind.Head && s.To.Kind == AnchorKind.Tail), Is.True, "v6 sections were whole runs");
            Assert.That(SaveJson.Summary(File.ReadAllText(Path.Combine(copy, SaveFile.FileName))).Trails, Is.EqualTo(back.Design.Trails.Count - 2),
                "Load Area counts trails, not sections");
        }

        [Test]
        public void TheVersionSevenFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v7-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture7");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveSeven(), back);
            Assert.That(back.Design.Cuts.Count, Is.EqualTo(1));
            Assert.That(back.Design.Lines.Count, Is.EqualTo(2));
            var kinds = back.Design.Lines.SelectMany(l => l.Spans).SelectMany(s => new[] { s.From.Kind, s.To.Kind }).Distinct().OrderBy(k => k);
            Assert.That(kinds, Is.EqualTo(new[] { AnchorKind.Head, AnchorKind.Tail, AnchorKind.Lands, AnchorKind.Crosses, AnchorKind.Station, AnchorKind.Cut }));
        }

        [Test]
        public void TheVersionEightFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v8-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture8");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveEight(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(8), "a v8 save isn't regraded when it opens");
            var run2 = back.Design.Trails.First(t => t.Name == "Run 2");
            Assert.That(run2.Shape.Rings.Count, Is.EqualTo(2), "the island is a hole");
            var trail = (JObject)JObject.Parse(File.ReadAllText(Path.Combine(copy, SaveFile.FileName)))["Design"]["Trails"][0];
            Assert.That(trail.Properties().Select(q => q.Name), Is.EqualTo(new[] { "Id", "Name", "Stage", "Grading", "Kind", "Clearing", "Head", "Tail", "Outline" }),
                "the clearing's outline, not the strokes it was painted with (finding 7)");
        }

        [Test]
        public void EveryOlderSaveMigratesToEightWithOutlines()
        {
            // 0.3 §5 (task P3-10, sections finding 7): the v1–v7 fixtures, unedited, open through v7 → v8: every trail's
            // strokes become its clearing's outline, and the document is the one its own version's builder makes (whose
            // trails are painted with the same strokes), so the conversion keeps every cell.
            var expected = new Dictionary<string, SaveFile>
            {
                ["v1-save"] = FixtureSave(), ["v2-save"] = FixtureSaveTwo(), ["v3-save"] = FixtureSaveThree(), ["v4-save"] = FixtureSaveFour(),
                ["v5-save"] = FixtureSaveFive(), ["v6-save"] = FixtureSaveSix(), ["v7-save"] = FixtureSaveSeven(),
            };
            foreach (var kv in expected)
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", kv.Key)), "Saves", JacksonHole, "save-001");
                var o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out int from);
                Assert.That(from, Is.LessThan(8), kv.Key);
                Assert.That((int)o["Version"], Is.EqualTo(SaveFile.CurrentVersion), kv.Key);
                Assert.That((int)o["Design"]["Shape"], Is.EqualTo(DesignDocument.ShapeVersion), kv.Key);
                foreach (JObject t in (JArray)o["Design"]["Trails"])
                {
                    Assert.That(t["Parts"], Is.Null, kv.Key);
                    Assert.That(((JArray)t["Outline"]).Count, Is.GreaterThan(0), kv.Key);
                }
                string copy = Path.Combine(_root, "up8-" + kv.Key);
                CopyFolder(fixture, copy);
                var back = SaveStore.Read(copy);
                Assert.That(back.MigratedFrom, Is.EqualTo(from), kv.Key + ": an older save is regraded once when it opens");
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(kv.Value.Design)), Is.True, kv.Key);
            }
        }

        [Test]
        public void TheVersionSevenConversionIsFrozen()
        {
            // The v7 → v8 step rasterises strokes once; its result is frozen with the format, so a change to the strokes'
            // rule or the outline's writer shows here (0.3 §5).
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v7-save")), "Saves", JacksonHole, "save-001");
            var back = SaveJson.Read(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName)), out _);
            string sha = BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(DesignBytes.Encode(back.Design))).Replace("-", "").ToLowerInvariant();
            Assert.That(sha, Is.EqualTo(ConvertedSevenSha), "the v7 fixture converted to v8");
        }

        const string ConvertedSevenSha = "8debdb8b23b7e52db05e33f72d5da6a97d09e03101bea69ecd4c6ed6f174f768";

        [Test]
        public void ADamagedOlderTrailRefusesTheSave()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v7-save")), "Saves", JacksonHole, "save-001");
            var o = JObject.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName)));
            ((JObject)o["Design"]["Trails"][0]["Parts"][0]["Strokes"][0])["WidthMm"] = 1;
            Assert.Throws<InvalidDataException>(() => SaveJson.Read(o.ToString(), out _), "a v7 stroke the rules refuse is damage, not a clearing");
        }

        [Test]
        public void EveryOlderSaveMigratesToSevenWithWholeRunSpans()
        {
            // 0.3 §5 (task P3-10, NT43): the v1–v6 fixtures, unedited, open through v6 → v7: no cut points, and a v6
            // trail's sections become whole-run spans; otherwise the same document as their own version read.
            var expected = new Dictionary<string, SaveFile>
            {
                ["v1-save"] = FixtureSave(), ["v2-save"] = FixtureSaveTwo(), ["v3-save"] = FixtureSaveThree(), ["v4-save"] = FixtureSaveFour(),
                ["v5-save"] = FixtureSaveFive(), ["v6-save"] = FixtureSaveSix(),
            };
            foreach (var kv in expected)
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", kv.Key)), "Saves", JacksonHole, "save-001");
                var o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out _);
                Assert.That((int)o["Version"], Is.EqualTo(SaveFile.CurrentVersion), kv.Key);
                Assert.That((int)o["Design"]["Shape"], Is.EqualTo(DesignDocument.ShapeVersion), kv.Key);
                Assert.That(((JArray)o["Design"]["Cuts"]).Count, Is.EqualTo(0), kv.Key);
                string copy = Path.Combine(_root, "up7-" + kv.Key);
                CopyFolder(fixture, copy);
                var back = SaveStore.Read(copy);
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(kv.Value.Design)), Is.True, kv.Key);
            }
        }

        [Test]
        public void EveryOlderSaveMigratesToSixWithEveryTrailOnItsOwn()
        {
            // 0.3 §5 (task P3-10, NT24): the v1–v5 fixtures, unedited, open through v5 → v6 with no trail of several
            // sections, so every trail is the one section it was, and otherwise the same document as their own version read.
            var expected = new Dictionary<string, SaveFile>
            {
                ["v1-save"] = FixtureSave(), ["v2-save"] = FixtureSaveTwo(), ["v3-save"] = FixtureSaveThree(), ["v4-save"] = FixtureSaveFour(),
                ["v5-save"] = FixtureSaveFive(),
            };
            foreach (var kv in expected)
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", kv.Key)), "Saves", JacksonHole, "save-001");
                var o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out _);
                Assert.That((int)o["Version"], Is.EqualTo(SaveFile.CurrentVersion), kv.Key);
                Assert.That((int)o["Design"]["Shape"], Is.EqualTo(DesignDocument.ShapeVersion), kv.Key);
                Assert.That(((JArray)o["Design"]["TrailLines"]).Count, Is.EqualTo(0), kv.Key);
                string copy = Path.Combine(_root, "up6-" + kv.Key);
                CopyFolder(fixture, copy);
                var back = SaveStore.Read(copy);
                Assert.That(back.Design.Lines, Is.Empty, kv.Key);
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(kv.Value.Design)), Is.True, kv.Key);
            }
        }

        [Test]
        public void EveryOlderSaveMigratesToFiveWithNoAreasNamed()
        {
            // 0.3 §5 (task P3-10, NW-15): the v1–v4 fixtures, unedited, open through v4 → v5 with no Areas named (their hubs go
            // by their default names, as they did) and otherwise the same document as their own version read.
            var expected = new Dictionary<string, SaveFile>
            {
                ["v1-save"] = FixtureSave(), ["v2-save"] = FixtureSaveTwo(), ["v3-save"] = FixtureSaveThree(), ["v4-save"] = FixtureSaveFour(),
            };
            foreach (var kv in expected)
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", kv.Key)), "Saves", JacksonHole, "save-001");
                var o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out _);
                Assert.That((int)o["Version"], Is.EqualTo(SaveFile.CurrentVersion), kv.Key);
                Assert.That((int)o["Design"]["Shape"], Is.EqualTo(DesignDocument.ShapeVersion), kv.Key);
                Assert.That(((JArray)o["Design"]["Areas"]).Count, Is.EqualTo(0), kv.Key);
                string copy = Path.Combine(_root, "up5-" + kv.Key);
                CopyFolder(fixture, copy);
                var back = SaveStore.Read(copy);
                Assert.That(back.Design.Areas, Is.Empty, kv.Key);
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(kv.Value.Design)), Is.True, kv.Key);
            }
        }

        [Test]
        public void EveryOlderSaveMigratesToFourWithNoStrokes()
        {
            // 0.3 §5: the v1, v2 and v3 fixtures, unedited, open through v3 → v4 with an empty stroke list and otherwise the
            // same document as their own version read.
            var expected = new Dictionary<string, SaveFile> { ["v1-save"] = FixtureSave(), ["v2-save"] = FixtureSaveTwo(), ["v3-save"] = FixtureSaveThree() };
            foreach (var kv in expected)
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", kv.Key)), "Saves", JacksonHole, "save-001");
                var o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out _);
                Assert.That((int)o["Version"], Is.EqualTo(SaveFile.CurrentVersion), kv.Key);
                Assert.That((int)o["Design"]["Shape"], Is.EqualTo(DesignDocument.ShapeVersion), kv.Key);
                Assert.That(((JArray)o["Design"]["Strokes"]).Count, Is.EqualTo(0), kv.Key);
                string copy = Path.Combine(_root, "up-" + kv.Key);
                CopyFolder(fixture, copy);
                var back = SaveStore.Read(copy);
                Assert.That(back.Design.Strokes, Is.Empty, kv.Key);
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(kv.Value.Design)), Is.True, kv.Key);
            }
        }

        [Test, Explicit("Regenerates the frozen v8 save fixture")]
        public void WriteTheVersionEightFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v8-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveEight()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v7 save fixture")]
        public void WriteTheVersionSevenFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v7-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveSeven()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v6 save fixture")]
        public void WriteTheVersionSixFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v6-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveSix()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v5 save fixture")]
        public void WriteTheVersionFiveFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v5-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveFive()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v4 save fixture")]
        public void WriteTheVersionFourFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v4-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveFour()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v3 save fixture")]
        public void WriteTheVersionThreeFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v3-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveThree()), Is.True);
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
