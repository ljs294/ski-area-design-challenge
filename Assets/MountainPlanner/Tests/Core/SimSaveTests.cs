using System;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Persistence.Saves;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Engine-free: these sources also build and run under plain .NET (tools/domain-tests). Task 4.03 PR B: the sidecar
    // (SI-12 v2), the worker's snow and its staging ring (SI-17). Each test names its p4-02 rule.
    public sealed class SimSaveTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs;

        /// <summary>A small field, so the tests stay quick: 150 × 90 cells is 3 × 2 tiles, the last ones partial.</summary>
        const int FieldW = 150, FieldH = 90;

        static SimSnowpack Snowpack(int seed, int x = 0, int y = 0, int w = FieldW, int h = FieldH)
        {
            var mm = new ushort[w * h];
            for (int i = 0; i < mm.Length; i++) mm[i] = (ushort)((i * 37 + seed * 101) % 2000);
            return new SimSnowpack(FieldW, FieldH, x, y, w, h, mm);
        }

        /// <summary>The golden script's world part way through its first season, with snow and a mid-run design change.</summary>
        static SimWorld Played(long untilFaceMs, out List<SeasonRunner.Scripted> rest)
        {
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, Snowpack(1)));
            var script = SeasonRunner.GoldenScript(P);
            SeasonRunner.Run(w, script, untilFaceMs, null);
            rest = script.FindAll(c => c.Tick > w.Tick);
            return w;
        }

        static List<ulong> Hours(SimWorld w, List<SeasonRunner.Scripted> script, long untilFaceMs)
        {
            var hashes = new List<ulong>();
            SeasonRunner.Run(w, script, untilFaceMs, (i, x) => hashes.Add(x));
            return hashes;
        }

        [Test]
        public void SI12_ARestoredSaveHasTheSameFutureAsNotSaving()
        {
            // Saved at 1:00 on Day 8 with the ropes running and the seed pinned; the future holds the third lift, the seed's
            // release, the closing in March, summer and the next October.
            long saveAt = 7 * Day + 13 * SimWorld.HourMs + 1_234_000;
            var a = Played(saveAt, out var rest);
            byte[] bytes = a.SaveState();
            var b = SimSidecar.Read(bytes, P, a.Design, SimSite.None, out var header);
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "the restored state is the saved one");
            Assert.That(header.Tick, Is.EqualTo(a.Tick));
            // Snow mid-way (as an edit sends it) in both, then the rest of the script.
            var snow = SimCommand.Of(SimCommandKind.SetSnowpack, 0, Snowpack(2, 70, 10, 40, 30));
            a.ApplyNow(snow);
            b.ApplyNow(snow);
            long until = 140 * Day;
            Assert.That(Hours(b, rest, until), Is.EqualTo(Hours(a, rest, until)), "hour for hour, the same future");
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()));
        }

        [Test]
        public void SI12_TheSidecarReadsBackToTheSameBytes()
        {
            var a = Played(30 * Day, out _);
            byte[] bytes = a.SaveState();
            var b = SimSidecar.Read(bytes, P, a.Design, SimSite.None, out _);
            Assert.That(b.SaveState(), Is.EqualTo(bytes));
            Assert.That(SimSidecar.VersionOf(bytes), Is.EqualTo(SimSidecar.Version));
        }

        [Test]
        public void SI12_StagedCommandsAreInTheSave()
        {
            // A command staged for the next tick (the host's, while running) is saved as applied: it would apply first anyway.
            var a = Played(10 * Day, out _);
            var b = SimSidecar.Read(a.SaveState(), P, a.Design, SimSite.None, out _);
            a.Stage(SimCommand.Of(SimCommandKind.SetMountainOpen, 0));
            byte[] withStaged = a.SaveState();
            b.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 0));
            Assert.That(SimSidecar.Read(withStaged, P, a.Design, SimSite.None, out _).Hash(), Is.EqualTo(b.Hash()));
        }

        [Test]
        public void SI12_ADamagedSidecarIsRefusedAndANewerOneSaysSo()
        {
            var a = Played(5 * Day, out _);
            byte[] bytes = a.SaveState();
            var flipped = (byte[])bytes.Clone();
            flipped[60] ^= 0x10;   // inside the state
            Assert.Throws<InvalidDataException>(() => SimSidecar.Read(flipped, P, a.Design, SimSite.None, out _));
            Assert.Throws<InvalidDataException>(() => SimSidecar.Read(bytes.AsSpan(0, bytes.Length - 9).ToArray(), P, a.Design, SimSite.None, out _));
            var newer = (byte[])bytes.Clone();
            newer[4] = (byte)(SimSidecar.Version + 1);
            var e = Assert.Throws<NotSupportedException>(() => SimSidecar.Read(newer, P, a.Design, SimSite.None, out _));
            Assert.That(e.Message, Does.Contain("newer"));
            Assert.Throws<InvalidDataException>(() => SimSidecar.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, P, a.Design, SimSite.None, out _));
        }

        [Test]
        public void SI20_ASaveOverAnotherDesignKeepsEachLiftsRopeByID()
        {
            // The save's design isn't the one the state ran with (an edit after the state was taken): lifts keep their ropes.
            var a = Played(70 * Day, out _);
            long rope = a.RunMsOf(101);
            Assert.That(rope, Is.GreaterThan(0));
            var b = SimSidecar.Read(a.SaveState(), P, SeasonRunner.GoldenDesign(9, false), SimSite.None, out _);
            Assert.That(b.RunMsOf(101), Is.EqualTo(rope));
            Assert.That(b.RunMsOf(303), Is.EqualTo(-1), "the third lift isn't in that design");
        }

        [Test]
        public void SI17_TheWorkersSnowIsWholeMillimetresInTheHash()
        {
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            ulong bare = w.Hash();
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, Snowpack(1)));
            Assert.That(w.Snow.Set, Is.True);
            ulong snowy = w.Hash();
            Assert.That(snowy, Is.Not.EqualTo(bare));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(FieldW, FieldH, 149, 89, 1, 1, new ushort[] { 7 })));
            Assert.That(w.Snow[149, 89], Is.EqualTo(7));
            Assert.That(w.Hash(), Is.Not.EqualTo(snowy), "one cell of the last partial tile changes the hash");
            Assert.That(SimSnowpack.ToMillimetres(0.3048f), Is.EqualTo(305));
            Assert.That(SimSnowpack.ToMillimetres(-1), Is.EqualTo(0));
            Assert.That(SimSnowpack.ToMillimetres(float.NaN), Is.EqualTo(0));
            Assert.That(SimSnowpack.ToMillimetres(1e9f), Is.EqualTo(ushort.MaxValue));
        }

        [Test]
        public void SI17_ARectangleOutsideTheFieldIsRefused()
        {
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            long before = w.Events.Count;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(FieldW, FieldH, 140, 0, 20, 1, new ushort[20])));
            Assert.That(w.Events.TryGet(before, out var e) && e.Type == SimEventType.CommandRejected, Is.True);
        }

        [Test]
        public void SI17_TilesReachTheRingRoundRobinAndWaitWhileItsFull()
        {
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, Snowpack(1)));
            Assert.That(w.Snow.TilesDue, Is.EqualTo(6));
            var ring = new TileStagingRing(4);
            Assert.That(w.StageSnow(ring, paused: true), Is.EqualTo(4), "four slots, then the ring is full");
            Assert.That(w.Snow.TilesDue, Is.EqualTo(2));
            Assert.That(w.StageSnow(ring, paused: true), Is.Zero);
            // The reader copies the slots out and hands them back; every cell matches the field.
            var seen = new HashSet<(int, int)>();
            void Drain()
            {
                for (long n = ring.Released; n < ring.Written; n++)
                {
                    ring.Read(n, out int x, out int y, out int tw, out int th, out int offset, out ushort[] cells);
                    seen.Add((x, y));
                    for (int j = 0; j < th; j++)
                        for (int i = 0; i < tw; i++)
                            Assert.That(cells[offset + j * tw + i], Is.EqualTo(w.Snow[x + i, y + j]));
                }
                ring.Release(ring.Written);
            }
            Drain();
            Assert.That(w.StageSnow(ring, paused: true), Is.EqualTo(2));
            Drain();
            Assert.That(seen, Has.Count.EqualTo(6));
            Assert.That(seen, Does.Contain((128, 64)), "the partial corner tile");
            Assert.That(w.Snow.TilesDue, Is.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.Release(ring.Written + 1));
        }

        [Test]
        public void SI17_WhileRunningATileStagesAtMostOnceEveryFlush()
        {
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            var ring = new TileStagingRing(64);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, Snowpack(1)));
            Assert.That(w.StageSnow(ring, paused: false), Is.EqualTo(6));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, Snowpack(2, 0, 0, 10, 10)));
            Assert.That(w.StageSnow(ring, paused: false), Is.Zero, "staged this tick already");
            while (w.Tick < P.TileFlushTicks) w.Step(1);
            Assert.That(w.StageSnow(ring, paused: false), Is.EqualTo(1));
        }

        [Test]
        public void SI17_StagingNeverChangesTheState()
        {
            var a = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            var b = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            foreach (var w in new[] { a, b }) w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, Snowpack(3)));
            a.StageSnow(new TileStagingRing(2), paused: true);
            Assert.That(a.Hash(), Is.EqualTo(b.Hash()));
            Assert.That(a.SaveState(), Is.EqualTo(b.SaveState()));
        }

        [Test]
        public void SI17_TheScreensFieldTakesAStagedTileInMetres()
        {
            var field = new Domain.Snow.SnowDepthField(FieldW, FieldH, 8);
            field.TakeDirty();
            var cells = new ushort[] { 0, 0, 305, 1000, 1, 65535 };
            field.CopyMillimetres(cells, 2, 148, 88, 2, 2);
            Assert.That(field[148, 88], Is.EqualTo(0.305f).Within(1e-6f));
            Assert.That(field[149, 88], Is.EqualTo(1f).Within(1e-6f));
            Assert.That(field[148, 89], Is.EqualTo(0.001f).Within(1e-7f));
            Assert.That(field[149, 89], Is.EqualTo(65.535f).Within(1e-4f));
            Assert.That(field.TakeDirty(), Is.EqualTo((148, 88, 2, 2)));
            Assert.Throws<ArgumentOutOfRangeException>(() => field.CopyMillimetres(cells, 2, 149, 88, 2, 2));
        }

        [Test]
        public void SI08_AWorldWithoutSnowHashesAsBefore()
        {
            // The golden fold predates the snow field: a world that never had snow must not move it.
            Assert.That(SeasonRunner.GoldenFold(P, 20), Is.EqualTo(SeasonRunner.GoldenFold(P, 20, 1)));
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            Assert.That(w.Snow.Width, Is.Zero);
        }

        // ---------- the host ----------

        sealed class FakeTime : IRealTime
        {
            public long Microseconds { get; set; }
        }

        [Test]
        public void SI12_TheHostHandsOverItsStateAtATickBoundary()
        {
            var t = new FakeTime();
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            var h = new SimulationHost(w, t, threaded: false);
            h.Send(SimCommandKind.SetSpeed, 4);
            h.Send(SimCommandKind.SetMountainOpen, 1);
            t.Microseconds += 3_000_000;
            h.Service();
            var request = new SimStateRequest();
            h.Send(SimCommandKind.SaveState, 0, request);
            h.Service();
            Assert.That(request.Wait(0), Is.True);
            var back = SimSidecar.Read(request.Bytes, P, w.Design, SimSite.None, out var header);
            Assert.That(header.Tick, Is.EqualTo(w.Tick));
            Assert.That(back.Hash(), Is.EqualTo(w.Hash()));
            Assert.That(back.Open, Is.True, "the open command before it is in the state");
        }

        [Test]
        public void SI17_TheHostStagesSnowWhilePausedAndSaysHowManyInTheSnapshot()
        {
            var t = new FakeTime();
            var w = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026);
            var h = new SimulationHost(w, t, threaded: false);
            Assert.That(h.Latest.SnowSet, Is.False);
            h.Send(SimCommandKind.SetSnowpack, 0, Snowpack(1));
            int wait = h.Service();
            Assert.That(h.Latest.SnowSet, Is.True);
            Assert.That(h.Latest.StagedTiles, Is.EqualTo(6));
            Assert.That(h.Latest.Tick, Is.Zero, "paused: the snow showed without time passing");
            Assert.That(wait, Is.EqualTo(-1), "nothing due: the worker sleeps until a command");
        }

        // ---------- the save folder ----------

        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "mp-sim-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        static SaveFile SaveWith(byte[] sim) => new SaveFile
        {
            Name = "Sim", SavedUtc = "2026-11-08T12:00:00Z", GameVersion = "test", PackageId = "pkg", HeightsSha256 = "",
            Design = DesignDocument.Empty(5), SimState = sim == null ? null : new SimSidecarBytes(sim, SimSidecar.Version),
        };

        [Test]
        public void SI12_TheSaveKeepsItsSidecarUnderItsHashAndSweepsTheOldOne()
        {
            var a = Played(3 * Day, out _);
            byte[] first = a.SaveState();
            string folder = Path.Combine(_root, "save-001");
            Assert.That(SaveStore.Write(folder, SaveWith(first)), Is.True);
            var back = SaveStore.Read(folder);
            Assert.That(back.SimState.Bytes, Is.EqualTo(first));
            Assert.That(back.Sim?.Sha256, Is.EqualTo(SaveTileFile.Hash(first)));
            Assert.That(back.Recovered, Is.Empty);
            // Two saves later the first sidecar is neither save.json's nor the backup's, so it goes.
            SeasonRunner.Run(a, null, 4 * Day, null);
            byte[] second = a.SaveState();
            SaveStore.Write(folder, SaveWith(second));
            SaveStore.Write(folder, SaveWith(second));
            Assert.That(Directory.GetFiles(Path.Combine(folder, SaveStore.SimFolder)), Has.Length.EqualTo(1));
            Assert.That(SaveStore.Read(folder).SimState.Bytes, Is.EqualTo(second));
        }

        [Test]
        public void SI12_ALostOrDamagedSidecarOpensTheDesignWithoutItsClock()
        {
            var a = Played(3 * Day, out _);
            string folder = Path.Combine(_root, "save-001");
            SaveStore.Write(folder, SaveWith(a.SaveState()));
            string file = Directory.GetFiles(Path.Combine(folder, SaveStore.SimFolder))[0];
            byte[] bytes = File.ReadAllBytes(file);
            bytes[20] ^= 1;
            File.WriteAllBytes(file, bytes);
            var damaged = SaveStore.Read(folder);
            Assert.That((damaged.SimState, damaged.Sim), Is.EqualTo(((SimSidecarBytes)null, (SimSidecarRef?)null)));
            Assert.That(damaged.Recovered, Does.Contain("damaged"));
            File.Delete(file);
            var lost = SaveStore.Read(folder);
            Assert.That(lost.SimState, Is.Null);
            Assert.That(lost.Recovered, Does.Contain("missing"));
            Assert.That(lost.Design.Seed, Is.EqualTo(5UL), "the design still opens");
        }

        [Test]
        public void SI12_ASaveWithoutASimulationWritesNull()
        {
            string folder = Path.Combine(_root, "save-001");
            SaveStore.Write(folder, SaveWith(null));
            Assert.That(File.ReadAllText(Path.Combine(folder, SaveFile.FileName)), Does.Contain("\"Sim\": null"));
            Assert.That(Directory.Exists(Path.Combine(folder, SaveStore.SimFolder)), Is.False);
            Assert.That(SaveStore.Read(folder).SimState, Is.Null);
        }

        [Test]
        public void SI12_OpenDesignAsksTheSimulationAtEachSaveAndHandsTheLoadedOneBack()
        {
            var a = Played(3 * Day, out _);
            byte[] state = a.SaveState();
            var open = OpenDesign.New(_root, "pkg", "");
            int asked = 0;
            open.Simulation = () => { asked++; return new SimSidecarBytes(state, SimSidecar.Version); };
            Assert.That(open.SaveAs("Mine", "2026-11-08T12:00:00Z", "test"), Is.True);
            Assert.That(asked, Is.EqualTo(1));
            var again = OpenDesign.Load(_root, open.SaveFolder, "pkg", "");
            Assert.That(again.LoadedSimulation.Bytes, Is.EqualTo(state));
            Assert.That(OpenDesign.New(_root, "pkg", "").LoadedSimulation, Is.Null);
        }

        // ---------- the frozen format (0.3 §5) ----------

        /// <summary>The fixtures' world: the golden script to 10:00 on Day 4 of its first season, with a small snow field.</summary>
        static byte[] FixtureState()
        {
            var w = Played(3 * Day + 10 * SimWorld.HourMs, out _);
            return w.SaveState();
        }

        /// <summary>v2's world adds lift operations (task 4.11): a line on the first lift, a feeder, a hold on the second.</summary>
        static byte[] FixtureTwo()
        {
            var w = Played(3 * Day + 9 * SimWorld.HourMs, out _);
            w.ApplyNow(SimCommand.Of(SimCommandKind.FeedLine, 0, new LiftFeed(101, 120, 20)));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, 60));
            while (w.FaceMs < 3 * Day + 10 * SimWorld.HourMs) w.Step(1);
            return w.SaveState();
        }

        static string FixtureFile(int version) => Path.Combine(TestData.Folder(Path.Combine("formats", $"v{version}-sim")), "golden.sim");

        /// <summary>v1 (task 4.03) still opens: the v1 → v2 step leaves the lines and chairs empty and keeps the holds.</summary>
        [Test]
        public void TheVersionOneSidecarStillOpens()
        {
            byte[] fixture = File.ReadAllBytes(FixtureFile(1));
            Assert.That(SimSidecar.VersionOf(fixture), Is.EqualTo(1));
            var w = SimSidecar.Read(fixture, P, SeasonRunner.GoldenDesign(1, false), SimSite.None, out var header);
            Assert.That(header.SidecarVersion, Is.EqualTo(1));
            Assert.That((w.Phase, w.SeasonDay, w.Snow.Width, w.Snow.Height), Is.EqualTo((SeasonPhase.Season, 4, FieldW, FieldH)));
            Assert.That(w.RunMsOf(101), Is.GreaterThan(0), "the ropes have run");
            Assert.That((w.LineOf(101), w.LedgerOf(101).riding), Is.EqualTo((0, 0)), "v1 had no lines");
            for (int i = 0; i < 1000; i++) w.Step(1);
            Assert.That(w.StatusOf(101), Is.EqualTo(LiftOpStatus.Open), "and it plays on");
        }

        /// <summary>
        /// v2 (task 4.11) still opens. Its bytes are untouched, so it carries the Pacing table of its own day and the
        /// line layout of its own day: from v4 a line is a chain over skier slots, so v2's queued skiers and riders are
        /// read and let go (counted as having left the line), exactly as v1's absent lines were when v2 added them.
        /// Everything else comes back: the holds, the carriers, the day's lift ledger, the way down.
        /// </summary>
        [Test]
        public void TheVersionTwoSidecarStillOpens()
        {
            byte[] fixture = File.ReadAllBytes(FixtureFile(2));
            Assert.That(SimSidecar.VersionOf(fixture), Is.EqualTo(2));
            var w = SimSidecar.Read(fixture, P, SeasonRunner.GoldenDesign(1, false), SimSite.None, out var header);
            Assert.That(header.SidecarVersion, Is.EqualTo(2));
            Assert.That((w.Phase, w.SeasonDay), Is.EqualTo((SeasonPhase.Season, 4)));
            Assert.That(w.HeldNow(202), Is.True, "the hold came back");
            Assert.That(w.LineOf(101), Is.Zero, "v2's line is read and let go at the v4 step");
            Assert.That(w.LedgerOf(101).leftInLine, Is.GreaterThan(0), "and those skiers are counted as having left it");
            Assert.That(w.SkierCount, Is.Zero, "a save from before the guests opens on an empty hill");
            Assert.That(w.ReputationOf(0), Is.EqualTo(P.StartRep), "with every reputation at its start (GU-18)");
            for (int i = 0; i < 1000; i++) w.Step(1);
            Assert.That(w.Phase, Is.EqualTo(SeasonPhase.Season), "and it plays on");
        }

        /// <summary>v4's world adds the guests (task 4.12): a crowd on the hill, their day, and the reputations.</summary>
        static byte[] FixtureFour()
        {
            var w = Played(3 * Day + 9 * SimWorld.HourMs, out _);
            w.ApplyNow(SimCommand.Of(SimCommandKind.FeedLine, 0, new LiftFeed(101, 120, 20)));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetGust, 60));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SpawnSkiers, 0, new SkierSpawn(1, 40)));
            while (w.FaceMs < 3 * Day + 10 * SimWorld.HourMs) w.Step(1);
            return w.SaveState();
        }

        /// <summary>
        /// The v4 layout is frozen. The fixture was made under Pacing v3 (task 4.12) and stays unedited; from Pacing v4
        /// (task 4.13's confidence speed) a fresh play no longer reaches the same state, so the layout is proved by a round
        /// trip instead: the fixture read and written again gives back its own bytes, all but the header's Pacing hash and
        /// version and the closing state hash, which fold the Pacing table in (SI-22).
        /// </summary>
        [Test]
        public void TheVersionFourSidecarIsFrozen()
        {
            byte[] fixture = File.ReadAllBytes(FixtureFile(4));
            var w = SimSidecar.Read(fixture, P, SeasonRunner.GoldenDesign(1, false), SimSite.None, out var header);
            Assert.That(header.SidecarVersion, Is.EqualTo(4));
            Assert.That(header.PacingVersion, Is.EqualTo(3), "the fixture was made with Pacing v3 and stays unedited");
            byte[] again = w.SaveState();
            Assert.That(again.Length, Is.EqualTo(fixture.Length), "the sidecar v4 is frozen: a change bumps SimSidecar.Version (0.3 §5)");
            for (int i = 0; i < 8; i++) Assert.That(again[i], Is.EqualTo(fixture[i]), "magic and version");
            for (int i = 20; i < fixture.Length - 8; i++)
                if (again[i] != fixture[i]) Assert.Fail($"the sidecar v4 is frozen: byte {i} changed on a round trip (0.3 §5)");
            Assert.That(header.PacingHash, Is.Not.EqualTo(P.ContentsHash), "Pacing v4 is a different table");
            Assert.That((w.Phase, w.SeasonDay), Is.EqualTo((SeasonPhase.Season, 4)));
            Assert.That(w.LineOf(101), Is.GreaterThan(0));
            Assert.That(w.HeldNow(202), Is.True);
        }

        [Test, Explicit("Regenerates the frozen v4 sidecar fixture")]
        public void WriteTheVersionFourSidecarFixture()
        {
            string folder = Path.Combine(TestData.Folder("formats"), "v4-sim");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "golden.sim"), FixtureFour());
        }
    }
}
