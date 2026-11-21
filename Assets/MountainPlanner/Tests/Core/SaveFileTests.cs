using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Measure;
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
            return Unposted(h.Document);   // nothing was posted before v11 (TD-07)
        }

        static SaveFile FixtureSave() => new SaveFile
        {
            Name = "Teton plan", SavedUtc = FixtureUtc, GameVersion = "0.2.0", PackageId = JacksonHole,
            HeightsSha256 = "52e1cd2c5c4f1d0bd0f2de3ebd3b0b5b9bb2a2dbd5e8e5d7bd1b2f3a1c4d5e6f",
            Design = FixtureDesign(),
            // A graded and cleared tile, a graded one, and a cleared-only one (a lift line), as task 05 makes them.
            Edits = Edits(Tile(12, 7, 1, clearing: true), Tile(11, 7, -2), Tile(13, 8, 0, heights: false, clearing: true)),
        };

        /// <summary>
        /// The v13 fixture's first plates: its lifts numbered 1, 2, 3 … in ID order (the fixture was pinned this way when the
        /// migration still numbered them; plates are now optional and older saves open with none).
        /// </summary>
        internal static DesignDocument PlatesInIdOrder(DesignDocument d)
        {
            var lifts = d.Lifts.OrderBy(l => l.Id.Value).Select((l, i) => l.WithPlate((i + 1).ToString())).ToList();
            return DesignDocument.From(d.Seed, d.NextOrdinal, d.Trails, lifts, d.Strokes, d.Areas, d.Lines, d.Cuts, d.Hours);
        }

        /// <summary>
        /// Task 4.25 (save v17, GO-01): a save older than v17 opens with a free fleet of diesel 400s, crews on Autopilot. The
        /// document an older fixture should give is its builder's, plus that grant: as many plain 400s as the save opened with
        /// (their count is checked by <see cref="GR01_OlderSavesOpenWithAFreeFleetSizedToTheirRuns"/>).
        /// </summary>
        static DesignDocument Granted(DesignDocument expected, SaveFile back)
        {
            if (back.MigratedFrom >= 17 || !expected.Grooming.IsEmpty) return expected;
            var fleet = back.Design.Grooming;
            Assert.That(fleet.Crews, Is.EqualTo(GroomCrews.AllAuto), "the grant's crews are on Autopilot");
            var doc = expected;
            foreach (var g in fleet.Groomers)
            {
                Assert.That((g.Model, g.Drive, g.WideTiller), Is.EqualTo((GroomerModel.S400, GroomerDrive.Diesel, false)), "the grant is plain diesel 400s");
                var r = new BuyGroomer(0, GroomerModel.S400, GroomerDrive.Diesel).Apply(doc);
                Assert.That(r.Accepted, Is.True, r.Reason);
                doc = r.Document;
            }
            return doc;
        }

        static void AssertSame(SaveFile a, SaveFile b)
        {
            Assert.That(DesignBytes.Same(DesignBytes.Encode(Granted(a.Design, b)), DesignBytes.Encode(b.Design)), Is.True, "identical document bytes");
            Assert.That(b.Name, Is.EqualTo(a.Name));
            Assert.That(b.Autosave, Is.EqualTo(a.Autosave));
            Assert.That(b.SavedUtc, Is.EqualTo(a.SavedUtc));
            Assert.That(b.PackageId, Is.EqualTo(a.PackageId));
            Assert.That(b.HeightsSha256, Is.EqualTo(a.HeightsSha256));
            Assert.That(b.Sim, Is.EqualTo(a.Sim));
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
            Assert.That(o.Properties().Select(p => p.Name), Is.EqualTo(new[] { "Version", "Name", "Autosave", "SavedUtc", "GameVersion", "PackageId", "HeightsSha256", "Design", "Edits", "Sim" }));
            Assert.That(((JObject)o["Design"]).Properties().Select(p => p.Name), Is.EqualTo(new[] { "Shape", "Seed", "NextOrdinal", "Trails", "Lifts", "Strokes", "Areas", "TrailLines", "Cuts", "Hours", "Ponds", "Dams", "Lakes", "Streams", "Snowmaking", "Grooming" }),
                "no network, stats, ratings, rope or volumes (SH-05); no undo history (DD1)");
            Assert.That(((JObject)o["Design"]["Lifts"][0]).Properties().Select(p => p.Name),
                Is.EqualTo(new[] { "Id", "Name", "Stage", "Catalog", "Bottom", "Top", "TowerMode", "Stance", "Towers", "Levels", "Drive", "Schedule", "Hours", "Plate" }),
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

        /// <summary>
        /// The save the v9 fixture holds (task 15 round 4, owner BR22): the v8 fixture's, plus a stroke painted with Clear trees
        /// off, so a stroke that clears nothing (TB-10 v2, ClearMm 0) is pinned beside the five that clear at 1 ft.
        /// </summary>
        static SaveFile FixtureSaveNine()
        {
            var save = FixtureSaveEight();
            var keep = BrushSettings.RaiseOrLower(false, 9144, 120).WithClearing(false);
            var stroke = new StrokePainting(keep, new[] { new BrushDab(new PointMm(-20_000, -30_000), 1000), new BrushDab(new PointMm(-18_000, -30_500), 1000) });
            var r = new AddTerrainStrokes(0, new[] { stroke }).Apply(save.Design);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        /// <summary>
        /// The save the v10 fixture holds (lift overhaul B2, owner LD9): the v9 fixture's, with its first Sessellift driven at
        /// the bottom, its second made a Monta driven at the top, and a Sessellift Stern Q-4 (bottom drive only) added, so both
        /// drive words and the new catalog id are pinned.
        /// </summary>
        static SaveFile FixtureSaveTen()
        {
            var save = FixtureSaveNine();
            var d = save.Design;
            var first = d.Lifts[0];
            var r = new EditLift(0, first.Id, first.CatalogId, first.Bottom, first.Top, first.TowerMode, first.Stance, first.Towers, null, first.Levels,
                                 LiftEdit.DriveEnd, null, LiftDrive.Bottom).Apply(d);
            Assert.That(r.Accepted, Is.True, r.Reason);
            var second = r.Document.Lifts[1];
            r = new EditLift(0, second.Id, LiftCatalogIds.MontaFg4, second.Bottom, second.Top, second.TowerMode, second.Stance, second.Towers, null,
                             second.Levels, LiftEdit.ChangeType, null, LiftDrive.Top).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new AddLift(0, "Stern Chair", LiftCatalogIds.SesselliftStern4, DesignTestKit.P(-600_000, 0), DesignTestKit.P(-300_000, 600_000),
                            TowerMode.Automatic, TowerStance.Plumb, null, WorkStage.Built, new LiftLevels(2_000_000, 2_150_000)).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        /// <summary>
        /// The save the v11 fixture holds (task 4.21, TD-07): the v10 fixture's, with every posted word pinned: the shaped
        /// Run 1 posted blue, Teton Ridge black, Run 2's own trail green and the first Run 1 piece's double-black, and the plan
        /// "Planned glade" left unposted (null).
        /// </summary>
        static SaveFile FixtureSaveEleven()
        {
            var save = FixtureSaveTen();
            var d = save.Design;
            var first = d.Trails.First(t => t.Name == "Run 1").Id;   // a piece of the shaped Run 1: a run's own sign under a line's
            var trails = d.Trails.Select(t => t.Name == "Run 2" ? t.WithPosted(SlopeBands.Band.Green)
                                            : t.Id.Equals(first) ? t.WithPosted(SlopeBands.Band.DoubleBlack) : t).ToList();
            var lines = d.Lines.Select(l => l.Name == "Run 1" ? l.WithPosted(SlopeBands.Band.Blue)
                                          : l.Name == "Teton Ridge" ? l.WithPosted(SlopeBands.Band.Black) : l).ToList();
            Assert.That(trails.Count(t => t.Posted.HasValue), Is.EqualTo(2), "two runs' own trails posted");
            Assert.That(lines.Count(l => l.Posted.HasValue), Is.EqualTo(2), "two shaped trails posted");
            save.Design = DesignDocument.From(d.Seed, d.NextOrdinal, trails, d.Lifts, d.Strokes, d.Areas, lines, d.Cuts);
            return save;
        }

        /// <summary>
        /// The save the v12 fixture holds (task 4.03, owner 2026-11-08: LO-07 v2, LO-09): the v11 fixture's, with the resort's
        /// hours moved to 9:00, last chair 3:45 and stop 4:00, its first lift on peak days only with its own hours, and no
        /// simulation sidecar (PR B writes one), so the hours, both schedule words and "Sim": null are pinned.
        /// </summary>
        static SaveFile FixtureSaveTwelve()
        {
            var save = FixtureSaveEleven();
            var r = new SetResortHours(0, new DayHours(9 * 3600, 15 * 3600 + 45 * 60, 16 * 3600)).Apply(save.Design);
            Assert.That(r.Accepted, Is.True, r.Reason);
            var first = r.Document.Lifts[0];
            r = new SetLiftSchedule(0, first.Id, LiftSchedule.PeakDays, new DayHours(9 * 3600 + 30 * 60, 15 * 3600, 15 * 3600 + 15 * 60)).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            save.Sim = null;
            return save;
        }

        /// <summary>
        /// The save the v13 fixture holds (task 4.11 PR B, owner 2026-11-08): the v12 fixture's lifts given plates 1, 2 and 3
        /// in ID order, then the second a letter ("A") and the third two digits ("12"), so both kinds of plate are pinned.
        /// </summary>
        static SaveFile FixtureSaveThirteen()
        {
            var save = FixtureSaveTwelve();
            Assert.That(save.Design.Lifts.All(l => l.Plate == ""), Is.True, "built lifts have no plate");
            save.Design = PlatesInIdOrder(save.Design);
            var r = new SetLiftPlate(0, save.Design.Lifts[1].Id, "A").Apply(save.Design);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new SetLiftPlate(0, r.Document.Lifts[2].Id, "12").Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        /// <summary>
        /// The save the v14 fixture holds (task 4.14, PD-08, WS-01): the v13 fixture plus a pond (a spring on it), a dam with
        /// its spillway on the far bank and a width of its own, and a lake switched on with a depth of its own, so every
        /// field is pinned.
        /// </summary>
        static SaveFile FixtureSaveFourteen()
        {
            var save = FixtureSaveThirteen();
            var ring = new[] { new PointMm(100_000, 50_000), new PointMm(140_000, 52_000), new PointMm(138_000, 80_000), new PointMm(104_000, 78_000) };
            var r = new AddPond(0, "Base Pond", ring, 2_031_500, 3_048).Apply(save.Design);
            Assert.That(r.Accepted, Is.True, r.Reason);
            var pond = r.Document.FindPond(new PondId(r.Created[0]));
            r = new EditPond(0, pond.Id, pond.Name, pond.Ring, pond.FullPoolMm, pond.ExcavationMm, 250, PondFill.Pumped, true).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new AddDam(0, "Twin Dam", new PointMm(-200_000, 10_000), new PointMm(-170_000, 12_000), 2_050_250, true, SpillBank.Far, 0x1234_5678_9abc_def0UL, true, 457).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new SetLakeSource(0, 0x0fed_cba9_8765_4321UL, true, 43).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new SetLake(0, 0x0fed_cba9_8765_4321UL, "Moose Lake", 43).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new SetStream(0, 0x1234_5678_9abc_def0UL, "Cody Creek", 460).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        /// <summary>
        /// The save the v15 fixture holds (task 4.15, HY-01): the v14 fixture plus a snowmaking network with every field set: a
        /// pump house on Base Pond (slab, a compressor of its own), a 10 in main with a closed valve at its far end, a hydrant
        /// run, an inline booster facing back toward the house, an intake on the lake, an outlet into the pond, and one gun of
        /// each model, each override.
        /// </summary>
        static SaveFile FixtureSaveFifteen()
        {
            var save = FixtureSaveFourteen();
            var doc = save.Design;
            ulong pond = doc.Ponds[0].Id.Value, lake = 0x0fed_cba9_8765_4321UL;
            CommandResult Ok(CommandResult r, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) { Assert.That(r.Accepted, Is.True, $"line {line}: {r.Reason}"); return r; }
            doc = Ok(new AddPumpHouse(0, "North Pumps", new PointMm(150_000, 60_000), 4_500, pond, HouseFoundation.Slab, 7_500).Apply(doc)).Document;
            var house = doc.Snow.Nodes.Single(n => n.Kind == SnowNodeKind.House);
            var route = new[] { house.At, new PointMm(250_000, 70_000), new PointMm(400_000, 120_000) };
            doc = Ok(new AddSnowPipe(0, "Main A", 10, route, SnowEnd.OnNode(house), SnowEnd.Free(route[2]), 30_480).Apply(doc)).Document;
            var main = doc.Snow.Pipes.Single();
            doc = Ok(new EditSnowPipe(0, main.Id, "Main A", 10, false, true).Apply(doc)).Document;
            // The booster sits midway between the fifth and sixth hydrants along the main.
            var along = doc.Snow.TapsOn(main.Id).Select(t => SnowGeometry.Project(main.Route, t.At).ChainageMm).OrderBy(c => c).ToArray();
            var pumpAt = SnowGeometry.At(main.Route, (along[4] + along[5]) / 2);
            doc = Ok(new AddSnowTap(0, SnowNodeKind.Pump, main.Id, pumpAt, false, new PumpSpec(300, 800, 250, false)).Apply(doc)).Document;
            var lakeRoute = new[] { new PointMm(-50_000, 200_000), new PointMm(250_000, 70_000) };
            doc = Ok(new AddSnowPipe(0, "Lake Line", 6, lakeRoute, SnowEnd.Intake(lakeRoute[0], lake), SnowEnd.OnPipe(main.Id, lakeRoute[1])).Apply(doc)).Document;
            var fillRoute = new[] { new PointMm(400_000, 120_000), new PointMm(120_000, 65_000) };
            var far = doc.Snow.Nodes.First(n => n.At == route[2]);
            doc = Ok(new AddSnowPipe(0, "Pond Fill", 4, fillRoute, SnowEnd.OnNode(far), SnowEnd.Outlet(fillRoute[1], pond)).Apply(doc)).Document;
            var hydrants = doc.Snow.Nodes.Where(n => n.Kind == SnowNodeKind.Hydrant).Take(4).ToArray();
            var models = new[] { GunModel.Stick10, GunModel.Stick20, GunModel.Stick30, GunModel.Ground };
            for (int i = 0; i < hydrants.Length; i++)
                doc = Ok(new AddSnowGuns(0, models[i], new[] { hydrants[i].At }, new[] { 1_000 * (i + 1) }).Apply(doc)).Document;
            var guns = doc.Snow.Guns;
            doc = Ok(new SetGunOverride(0, new[] { guns.Single(g => g.Model == GunModel.Stick10).Id }, GunOverride.On).Apply(doc)).Document;
            doc = Ok(new SetGunOverride(0, new[] { guns.Single(g => g.Model == GunModel.Stick20).Id }, GunOverride.Off).Apply(doc)).Document;
            save.Design = doc;
            return save;
        }

        /// <summary>
        /// The save the v16 fixture holds (task 4.07 B1 part 2c, SN-20 v4): the v15 fixture with one run switched Closed and
        /// one shaped trail switched Open; every other trail and line stays on Auto.
        /// </summary>
        static SaveFile FixtureSaveSixteen()
        {
            var save = FixtureSaveFifteen();
            var doc = save.Design;
            var run = doc.Trails.OrderBy(t => t.Id.Value).First().Id;
            var line = doc.Lines.OrderBy(l => l.Id.Value).First().Id;
            var r = new SetTrailSwitch(0, new[] { run }, null, TrailSwitch.Closed).Apply(doc);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new SetTrailSwitch(0, null, new[] { line }, TrailSwitch.Open).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        /// <summary>
        /// The save the v17 fixture holds (task 4.25, GO-01, GO-05, SN-22 v4): the v16 fixture with its grant (as an older
        /// save opens), one run on Never at High priority, one shaped trail on Only when I order, one more groomer (an 800 W
        /// hybrid with the wider tiller) and the night's crew pinned at 2.
        /// </summary>
        SaveFile FixtureSaveSeventeen()
        {
            var save = FixtureSaveSixteen();
            var granted = SaveStore.Read(CopyOfFixture("v16-save"));
            var doc = Granted(save.Design, granted);
            var run = doc.Trails.OrderBy(t => t.Id.Value).Last().Id;
            var line = doc.Lines.OrderBy(l => l.Id.Value).First().Id;
            var r = new SetTrailGrooming(0, new[] { run }, null, GroomSetting.Never, GroomPriority.High).Apply(doc);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new SetTrailGrooming(0, null, new[] { line }, GroomSetting.WhenOrdered, null).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new BuyGroomer(0, GroomerModel.S800W, GroomerDrive.Hybrid).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            var last = r.Document.Grooming.Groomers[r.Document.Grooming.Groomers.Count - 1].Number;
            r = new FitGroomerTiller(0, last, true).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            r = new SetGroomCrews(0, new GroomCrews(GroomCrews.Auto, 2, GroomCrews.Auto)).Apply(r.Document);
            Assert.That(r.Accepted, Is.True, r.Reason);
            save.Design = r.Document;
            return save;
        }

        string CopyOfFixture(string version) =>
            CopyOf(Path.Combine(TestData.Folder(Path.Combine("formats", version)), "Saves", JacksonHole, "save-001"), "of-" + version + "-" + Guid.NewGuid().ToString("N"));

        [Test]
        public void TheWriterReproducesTheFrozenFixtureByteForByte()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v17-save")), "Saves", JacksonHole, "save-001");
            string folder = Path.Combine(_root, "again");
            SaveStore.Write(folder, FixtureSaveSeventeen());
            Assert.That(File.ReadAllBytes(Path.Combine(folder, SaveFile.FileName)), Is.EqualTo(File.ReadAllBytes(Path.Combine(fixture, SaveFile.FileName))),
                "save.json v17 is frozen: a writer change is a format change (0.3 §5)");
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
            Assert.That(e.Message, Is.EqualTo("This save was saved by a newer version of Mountain Planner (format 18; this version reads up to 17). Update the game to open it."));
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
            Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(Granted(FixtureSave().Design, back))), Is.True);
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
            Assert.That(back.Design.Strokes.Select(k => k.Settings.ClearMm), Is.All.EqualTo(TerrainBrush.ClearMm), "v8 → v9: every stroke cleared at 1 ft (BR22)");
            var run2 = back.Design.Trails.First(t => t.Name == "Run 2");
            Assert.That(run2.Shape.Rings.Count, Is.EqualTo(2), "the island is a hole");
            var trail = (JObject)JObject.Parse(File.ReadAllText(Path.Combine(copy, SaveFile.FileName)))["Design"]["Trails"][0];
            Assert.That(trail.Properties().Select(q => q.Name), Is.EqualTo(new[] { "Id", "Name", "Stage", "Grading", "Kind", "Clearing", "Head", "Tail", "Outline" }),
                "the clearing's outline, not the strokes it was painted with (finding 7)");
        }

        [Test]
        public void TheVersionNineFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v9-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture9");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveNine(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(9));
            Assert.That(back.Design.Strokes.Count(k => !k.Settings.ClearsTrees), Is.EqualTo(1), "one stroke kept its trees (TB-10 v2)");
            var stroke = (JObject)JObject.Parse(File.ReadAllText(Path.Combine(copy, SaveFile.FileName)))["Design"]["Strokes"][0];
            Assert.That(stroke["ClearMm"], Is.Not.Null, "every stroke writes its clearing depth");
        }

        [Test]
        public void TheVersionTwelveFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v12-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture12");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveTwelve(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(12));
            Assert.That(back.Sim, Is.Null, "no simulation saved before PR B");
            Assert.That(back.Design.Hours, Is.EqualTo(new DayHours(9 * 3600, 15 * 3600 + 45 * 60, 16 * 3600)));
            Assert.That(back.Design.Lifts.Select(l => l.Schedule + " " + (l.Hours?.ToString() ?? "-")),
                Is.EqualTo(new[] { "PeakDays 09:30–15:15 (last chair 15:00)", "Daily -", "Daily -" }));
        }

        [Test]
        public void TheVersionThirteenFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v13-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture13");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveThirteen(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(13));
            Assert.That(back.Design.Lifts.Select(l => l.Plate), Is.EqualTo(new[] { "1", "A", "12" }));
        }

        [Test]
        public void TheVersionFourteenFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v14-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture14");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveFourteen(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(14));
            Assert.That(back.Design.Ponds.Select(p => $"{p.Name} {p.Ring.Count} {p.FullPoolMm} {p.ExcavationMm} {p.SpringGpm} {p.Fill} {p.StartsEmpty}"),
                Is.EqualTo(new[] { "Base Pond 4 2031500 3048 250 Pumped True" }));
            Assert.That(back.Design.Dams.Select(d => $"{d.Name} {d.UpstreamLeft} {d.Spill} {d.StreamWay:x16} {d.StartsEmpty}"),
                Is.EqualTo(new[] { "Twin Dam True Far 123456789abcdef0 True" }));
            Assert.That(back.Design.Lakes.Select(k => $"{k.Lake:x16} {k.Name} {k.DepthTenthsFt} {k.Source}"), Is.EqualTo(new[] { "0fedcba987654321 Moose Lake 43 True" }));
            Assert.That(back.Design.Streams.Select(k => $"{k.Way:x16} {k.Name} {k.WidthCm}"), Is.EqualTo(new[] { "123456789abcdef0 Cody Creek 460" }));
        }

        [Test]
        public void TheVersionFifteenFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v15-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture15");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveFifteen(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(15));
            var snow = back.Design.Snow;
            Assert.That(snow.Houses.Select(h => $"{h.Name} {h.BearingCentideg} {h.Foundation} {h.CompressorCfm}"), Is.EqualTo(new[] { "North Pumps 4500 Slab 7500" }));
            Assert.That(snow.Pipes.Select(p => $"{p.Name} {p.DiameterIn} {p.FromClosed} {p.ToClosed}").OrderBy(x => x, StringComparer.Ordinal),
                Is.EqualTo(new[] { "Lake Line 6 False False", "Main A 10 False True", "Pond Fill 4 False False" }));
            var pump = snow.Nodes.Single(n => n.Kind == SnowNodeKind.Pump);
            Assert.That($"{pump.Forward} {pump.Pump.Hp} {pump.Pump.EfficiencyPermille} {pump.Pump.BoostPsi} {pump.Pump.On}", Is.EqualTo("False 300 800 250 False"));
            Assert.That(snow.Nodes.Count(n => n.Kind == SnowNodeKind.Intake), Is.EqualTo(1));
            Assert.That(snow.Nodes.Count(n => n.Kind == SnowNodeKind.Outlet), Is.EqualTo(1));
            Assert.That(snow.Guns.Select(g => $"{g.Model} {g.AimCentideg} {g.Override}"),
                Is.EquivalentTo(new[] { "Stick10 1000 On", "Stick20 2000 Off", "Stick30 3000 Auto", "Ground 4000 Auto" }));
        }

        [Test]
        public void HY01_OlderSavesOpenWithNoSnowmaking()
        {
            // v14 → v15 (task 4.15): an older save held no snowmaking network.
            foreach (string version in new[] { "v1-save", "v7-save", "v13-save", "v14-save" })
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", version)), "Saves", JacksonHole, "save-001");
                var back = SaveJson.Read(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName)), out _);
                Assert.That(back.Design.Snow.IsEmpty, Is.True, version);
            }
        }

        [Test]
        public void TheVersionSixteenFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v16-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture16");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveSixteen(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(16));
            Assert.That(back.Design.Trails.Count(t => t.Switch == TrailSwitch.Closed), Is.EqualTo(1));
            Assert.That(back.Design.Trails.Count(t => t.Switch != TrailSwitch.Auto), Is.EqualTo(1));
            Assert.That(back.Design.Lines.Select(l => l.Switch).Count(s => s == TrailSwitch.Open), Is.EqualTo(1));
            Assert.That(back.Design.Lines.Count(l => l.Switch == TrailSwitch.Closed), Is.Zero);
            var o = JObject.Parse(File.ReadAllText(Path.Combine(copy, SaveFile.FileName)));
            Assert.That(((JArray)o["Design"]["Trails"]).Select(t => (string)t["Switch"]).Distinct().OrderBy(w => w, StringComparer.Ordinal),
                Is.EqualTo(new[] { "auto", "closed" }));
        }

        [Test]
        public void SW00_OlderSavesOpenWithEveryTrailOnAuto()
        {
            // v15 → v16 (task 4.07 B1 part 2c): no switch was saved before v16, so every trail and shaped trail opens on Auto.
            foreach (string version in new[] { "v1-save", "v7-save", "v11-save", "v15-save" })
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", version)), "Saves", JacksonHole, "save-001");
                var back = SaveStore.Read(CopyOf(fixture, "auto-" + version));
                Assert.That(back.Design.Trails.All(t => t.Switch == TrailSwitch.Auto), Is.True, version);
                Assert.That(back.Design.Lines.All(l => l.Switch == TrailSwitch.Auto), Is.True, version);
            }
        }

        [Test]
        public void TheVersionSeventeenFixtureReadsUnchanged()
        {
            string copy = CopyOfFixture("v17-save");
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveSeventeen(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(17));
            var f = back.Design.Grooming;
            Assert.That(f.Groomers[f.Groomers.Count - 1].Model, Is.EqualTo(GroomerModel.S800W));
            Assert.That(f.Groomers[f.Groomers.Count - 1].Drive, Is.EqualTo(GroomerDrive.Hybrid));
            Assert.That(f.Groomers[f.Groomers.Count - 1].WideTiller, Is.True);
            Assert.That(f.Crews, Is.EqualTo(new GroomCrews(GroomCrews.Auto, 2, GroomCrews.Auto)));
            Assert.That(back.Design.Trails.Count(t => t.Grooming.Setting == GroomSetting.Never && t.Grooming.Priority == GroomPriority.High), Is.EqualTo(1));
            Assert.That(back.Design.Lines.Count(l => l.Grooming.Setting == GroomSetting.WhenOrdered), Is.EqualTo(1));
            var o = JObject.Parse(File.ReadAllText(Path.Combine(copy, SaveFile.FileName)));
            Assert.That((string)((JArray)o["Design"]["Grooming"]["Groomers"]).Last["Model"], Is.EqualTo("800w"));
        }

        [Test]
        public void GR01_OlderSavesOpenWithAFreeFleetSizedToTheirRuns()
        {
            // v16 → v17 (task 4.25, GO-01): grooming was free before v17, so an older save opens with ⌈built run acres / 87.5⌉
            // diesel 400s (at least one with any built run), crews on Autopilot, and every trail on Default, Normal. The v1
            // fixture has only a planned glade, so it gets none.
            foreach (string version in new[] { "v1-save", "v7-save", "v11-save", "v16-save" })
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", version)), "Saves", JacksonHole, "save-001");
                var upgraded = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out _);
                double areaMm2 = 0;
                foreach (JObject t in (JArray)upgraded["Design"]["Trails"])
                    if ((string)t["Stage"] == "built" && (string)t["Kind"] == "run") areaMm2 += AreaMm2((JArray)t["Outline"]);
                int want = areaMm2 <= 0 ? 0 : (int)Math.Min(GroomFleet.MaxGroomers, Math.Max(1, Math.Ceiling(areaMm2 / (4_046_856_422.0 * 87.5))));
                var back = SaveStore.Read(CopyOf(fixture, "grant-" + version));
                Assert.That(back.Design.Grooming.Groomers.Count, Is.EqualTo(want), version);
                Assert.That(back.Design.Trails.All(t => t.Grooming.Setting == GroomSetting.Default && t.Grooming.Priority == GroomPriority.Normal), Is.True, version);
                Assert.That(back.Design.Lines.All(l => l.Grooming.Setting == GroomSetting.Default), Is.True, version);
                if (version != "v1-save") Assert.That(want, Is.GreaterThan(0), version + " has built runs");
            }
        }

        /// <summary>An outline's area in mm² (each ring [x0, y0, dx1, dy1, …] by its signed area, holes subtracting), worked
        /// here apart from the migration's own.</summary>
        static double AreaMm2(JArray rings)
        {
            double twice = 0;
            foreach (JArray flat in rings)
            {
                var xs = new List<double>(); var ys = new List<double>();
                double x = 0, y = 0;
                for (int i = 0; i + 1 < flat.Count; i += 2) { x += (long)flat[i]; y += (long)flat[i + 1]; xs.Add(x); ys.Add(y); }
                for (int i = 0; i < xs.Count; i++) { int j = (i + 1) % xs.Count; twice += xs[i] * ys[j] - xs[j] * ys[i]; }
            }
            return Math.Abs(twice) / 2;
        }

        [Test, Explicit("writes the v17 fixture once; never rerun on a frozen fixture")]
        public void WriteVersionSeventeenFixture()
        {
            string folder = Path.Combine(TestData.Folder("formats"), "v17-save", "Saves", JacksonHole, "save-001");
            Assert.That(Directory.Exists(folder), Is.False, "the v17 fixture is frozen");
            SaveStore.Write(folder, FixtureSaveSeventeen());
        }

        [Test, Explicit("writes the v16 fixture once; never rerun on a frozen fixture")]
        public void WriteVersionSixteenFixture()
        {
            string folder = Path.Combine(TestData.Folder("formats"), "v16-save", "Saves", JacksonHole, "save-001");
            Assert.That(Directory.Exists(folder), Is.False, "the v16 fixture is frozen");
            SaveStore.Write(folder, FixtureSaveSixteen());
        }

        [Test, Explicit("writes the v15 fixture once; never rerun on a frozen fixture")]
        public void WriteVersionFifteenFixture()
        {
            string folder = Path.Combine(TestData.Folder("formats"), "v15-save", "Saves", JacksonHole, "save-001");
            Assert.That(Directory.Exists(folder), Is.False, "the v15 fixture is frozen");
            SaveStore.Write(folder, FixtureSaveFifteen());
        }

        [Test]
        public void PD08_OlderSavesOpenWithNoPondsDamsLakesOrStreams()
        {
            // v13 → v14 (task 4.14): an older save held none.
            foreach (string version in new[] { "v1-save", "v7-save", "v12-save", "v13-save" })
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", version)), "Saves", JacksonHole, "save-001");
                var back = SaveJson.Read(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName)), out _);
                Assert.That(back.Design.Ponds, Is.Empty, version);
                Assert.That(back.Design.Dams, Is.Empty, version);
                Assert.That(back.Design.Lakes, Is.Empty, version);
                Assert.That(back.Design.Streams, Is.Empty, version);
            }
        }

        [Test]
        public void OlderSavesOpenWithNoPlates()
        {
            // v12 → v13 (task 4.11 PR B; owner 2026-11-08: plates are optional): an older save's lifts open with no plate.
            foreach (string version in new[] { "v1-save", "v2-save", "v10-save", "v12-save" })
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", version)), "Saves", JacksonHole, "save-001");
                var back = SaveJson.Read(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName)), out _);
                Assert.That(back.Design.Lifts.Count, Is.GreaterThan(0), version);
                Assert.That(back.Design.Lifts.All(l => l.Plate == ""), Is.True, version);
            }
        }

        [Test]
        public void APlateIsOneToThreeLettersOrDigitsAndNoOtherLiftsOwn()
        {
            var d = PlatesInIdOrder(FixtureSaveTwelve().Design);
            var first = d.Lifts[0].Id;
            foreach (string bad in new[] { "1234", "A B", "#1", "é" })
                Assert.That(new SetLiftPlate(0, first, bad).Apply(d).Accepted, Is.False, bad);
            var taken = new SetLiftPlate(0, first, "2").Apply(d);
            Assert.That(taken.Accepted, Is.False, "another lift already has 2");
            Assert.That(taken.Reason, Does.Contain("already has plate 2"));
            Assert.That(new SetLiftPlate(0, first, "a").Apply(d).Accepted, Is.True);
            // Plates are optional (owner 2026-11-08): an empty plate removes it, and any number of lifts may have none.
            var cleared = new SetLiftPlate(0, first, "").Apply(d);
            Assert.That(cleared.Accepted, Is.True, cleared.Reason);
            Assert.That(cleared.Document.FindLift(first).Plate, Is.EqualTo(""));
            Assert.That(new SetLiftPlate(0, first, "").Label, Is.EqualTo("Remove lift plate"));
            Assert.That(new SetLiftPlate(0, first, "").Apply(cleared.Document).Accepted, Is.False, "it already has none");
            var second = new SetLiftPlate(0, d.Lifts[1].Id, "").Apply(cleared.Document);
            Assert.That(second.Accepted, Is.True, "two lifts may both have no plate");
            Assert.That(new SetLiftPlate(0, first, d.Lifts[1].Plate).Apply(second.Document).Accepted, Is.True, "a removed plate is free again");
            // A new lift has no plate, however many lifts do.
            var l = d.Lifts[0];
            var added = new AddLift(0, "New", l.CatalogId, new PointMm(l.Bottom.X + 2_000_000, l.Bottom.Y), new PointMm(l.Top.X + 2_000_000, l.Top.Y),
                l.TowerMode, l.Stance, null, WorkStage.Plan).Apply(second.Document);
            Assert.That(added.Accepted, Is.True, added.Reason);
            Assert.That(added.Document.Lifts.Single(x => x.Name == "New").Plate, Is.EqualTo(""));
        }

        [Test]
        public void EveryOlderSaveOpensWithTheOldHoursAndNoSimulation()
        {
            // v11 → v12 (LO-07 v2, LO-09): before v12 the resort ran 8:30 to 4:30, last chair 4:15, and every lift daily on it.
            foreach (string version in new[] { "v1-save", "v5-save", "v10-save", "v11-save" })
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", version)), "Saves", JacksonHole, "save-001");
                var back = SaveJson.Read(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName)), out _);
                Assert.That(back.Design.Hours, Is.EqualTo(DayHours.Default), version);
                Assert.That(back.Design.Lifts.All(l => l.Schedule == LiftSchedule.Daily && l.Hours == null), Is.True, version);
                Assert.That(back.Sim, Is.Null, version);
            }
        }

        [Test]
        public void TheSimulationSidecarsNameIsChecked()
        {
            string folder = SaveStore.NewSaveFolder(_root, JacksonHole);
            var save = FixtureSave();
            // The writer names the sidecar by its bytes' hash (task 4.03 PR B); save.json's name for it is checked on reading.
            var bytes = new byte[] { 1, 2, 3 };
            save.SimState = new SimSidecarBytes(bytes, 1);
            Assert.That(SaveStore.Write(folder, save), Is.True);
            string path = Path.Combine(folder, SaveFile.FileName);
            Assert.That(SaveJson.Read(File.ReadAllText(path), out _).Sim, Is.EqualTo(new SimSidecarRef(1, SaveTileFile.Hash(bytes))));
            var o = JObject.Parse(File.ReadAllText(path));
            o["Sim"]["Sha256"] = "NOT-HEX";
            Assert.That(() => SaveJson.Read(o.ToString(), out _), Throws.Exception);
        }

        [Test]
        public void TheVersionTenFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v10-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture10");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveTen(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(10));
            Assert.That(back.Design.Lifts.OrderBy(l => l.CatalogId, StringComparer.Ordinal).Select(l => l.CatalogId + " " + l.Drive),
                Is.EqualTo(new[] { "monta-fg4 Top", "sessellift-fgq4 Bottom", "sessellift-stern4 Bottom" }), "the player's drive ends, and the Stern's own (LD9)");
        }

        [Test]
        public void TheVersionElevenFixtureReadsUnchanged()
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v11-save")), "Saves", JacksonHole, "save-001");
            string copy = Path.Combine(_root, "fixture11");
            CopyFolder(fixture, copy);
            var back = SaveStore.Read(copy);
            AssertSame(FixtureSaveEleven(), back);
            Assert.That(back.MigratedFrom, Is.EqualTo(11));
            var o = JObject.Parse(File.ReadAllText(Path.Combine(copy, SaveFile.FileName)));
            Assert.That(((JArray)o["Design"]["Trails"]).Select(t => (string)t["Posted"]).Where(w => w != null).OrderBy(w => w, StringComparer.Ordinal),
                Is.EqualTo(new[] { "double-black", "green" }));
            Assert.That(((JArray)o["Design"]["TrailLines"]).Select(t => (string)t["Posted"]).OrderBy(w => w, StringComparer.Ordinal),
                Is.EqualTo(new[] { "black", "blue" }));
        }

        [Test]
        public void EveryOlderSaveOpensUnposted()
        {
            // v10 → v11 (TD-07): nothing was posted before v11, so every trail and shaped trail of the v1–v10 fixtures opens
            // unposted; the game posts the built ones when it opens (TD-02 v1's band), not the file.
            foreach (string version in new[] { "v1-save", "v6-save", "v7-save", "v10-save" })
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", version)), "Saves", JacksonHole, "save-001");
                var o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out _);
                Assert.That((int)o["Design"]["Shape"], Is.EqualTo(DesignDocument.ShapeVersion), version);   // on to 12 (task 4.03)
                foreach (string list in new[] { "Trails", "TrailLines" })
                    if (o["Design"][list] is JArray items)
                        foreach (JObject x in items)
                            Assert.That(x["Posted"]?.Type, Is.EqualTo(JTokenType.Null), version + " " + list);
                var back = SaveStore.Read(CopyOf(fixture, "unposted-" + version));
                Assert.That(back.Design.Trails.All(t => t.Posted == null) && back.Design.Lines.All(l => l.Posted == null), Is.True, version);
            }
        }

        string CopyOf(string fixture, string name)
        {
            string copy = Path.Combine(_root, name);
            CopyFolder(fixture, copy);
            return copy;
        }

        [Test]
        public void EveryOlderSaveOpensWithItsTypesOwnDriveEnd()
        {
            // v9 → v10 (LD9): before v10 every lift drove at its type's own end, the Sessellift's top and the Monta's bottom.
            foreach (string version in new[] { "v1-save", "v5-save", "v9-save" })
            {
                string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", version)), "Saves", JacksonHole, "save-001");
                var o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(File.ReadAllText(Path.Combine(fixture, SaveFile.FileName))), out _);
                foreach (JObject l in (JArray)o["Design"]["Lifts"])
                    Assert.That((string)l["Drive"], Is.EqualTo(LiftCatalogIds.Maker((string)l["Catalog"]) == "monta" ? "bottom" : "top"), version);
            }
            var monta = JObject.Parse("{ \"Design\": { \"Lifts\": [ { \"Catalog\": \"monta-fg4\" }, { \"Catalog\": \"fixed-triple:monta\" }, { \"Catalog\": \"sessellift-fgq4\" } ] } }");
            typeof(SaveFile).GetMethod("UpToTen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { monta });
            Assert.That(((JArray)monta["Design"]["Lifts"]).Select(l => (string)l["Drive"]), Is.EqualTo(new[] { "bottom", "bottom", "top" }));
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
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(Granted(kv.Value.Design, back))), Is.True, kv.Key);
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
            Assert.That(sha, Is.EqualTo(ConvertedSevenSha), "the v7 fixture converted to v8 (and on to v9)");
        }

        // Re-pinned for shape 9 (task 15 round 4, BR22): the same outlines, the shape number and each stroke's 1 ft clearing
        // depth (v8 → v9) in the bytes; from 8debdb8b…. Shape 10 (LD9): the shape number and each lift's drive end; from e7c1fe4e….
        // Shape 11 (task 4.21, TD-07): the shape number and a not-posted byte for each trail and shaped trail; from c7a67ac0….
        // Shape 12 (task 4.03, LO-07 v2, LO-09): the shape number, the resort's default hours and each lift's daily schedule
        // with no hours of its own; from 1c7c9274…. Shape 13 (task 4.11 PR B): each lift's plate, numbered in ID order; from f5af52b0….
        // Shape 14 (task 4.14): the shape number and no ponds, dams or lake sources; from a5a27845….
        // Shape 15 (task 4.15, HY-01): the shape number and an empty snowmaking network; from f1725337…, owner OK 2026-11-18.
        // Shape 16 (task 4.07 B1 part 2c, SN-20 v4): the shape number and an Auto switch on each trail and shaped trail;
        // from 4c1b9a92…, owner OK pending. Shape 17 (task 4.25, GO-01): the shape number, each trail's and shaped trail's
        // grooming (Default, Normal) and the free fleet the v17 step grants; from 58dff65f…, owner OK 2026-11-20.
        const string ConvertedSevenSha = "002ae590e98bfd12cb48da6dfb7db174821fc12a926f916e30da68717167ff9b";

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
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(Granted(kv.Value.Design, back))), Is.True, kv.Key);
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
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(Granted(kv.Value.Design, back))), Is.True, kv.Key);
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
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(Granted(kv.Value.Design, back))), Is.True, kv.Key);
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
                Assert.That(DesignBytes.Same(DesignBytes.Encode(back.Design), DesignBytes.Encode(Granted(kv.Value.Design, back))), Is.True, kv.Key);
            }
        }

        [Test, Explicit("Regenerates the frozen v14 save fixture")]
        public void WriteTheVersionFourteenFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v14-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveFourteen()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v13 save fixture")]
        public void WriteTheVersionThirteenFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v13-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveThirteen()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v12 save fixture")]
        public void WriteTheVersionTwelveFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v12-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveTwelve()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v11 save fixture")]
        public void WriteTheVersionElevenFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v11-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveEleven()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v10 save fixture")]
        public void WriteTheVersionTenFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v10-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveTen()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
        }

        [Test, Explicit("Regenerates the frozen v9 save fixture")]
        public void WriteTheVersionNineFixture()
        {
            string formats = TestData.Folder("formats");
            string folder = Path.Combine(formats, "v9-save", "Saves", JacksonHole, "save-001");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Assert.That(SaveStore.Write(folder, FixtureSaveNine()), Is.True);
            File.Delete(Path.Combine(folder, SaveFile.BackupName));
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
