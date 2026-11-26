using System;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.08 PR A, the simulation's side of the snow look: the fine trail snow reaches the screen's staging ring as the
    /// field holds it; a paint (SimCommandKind.PaintSnow) builds a machine made whale up to SN-27 v2's cap and keeps every
    /// block on its 8 m cell; SN-14 v4 grooms fresh snow to packed powder with the groomed finish until the third night;
    /// machine made snow (SN-13 v2) packs and thaws as powder does.
    /// </summary>
    public sealed class SnowLookSimTests
    {
        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        static TrailSnowLayout OneRun() => TrailSnowWorldTests.Layout(TrailSnowWorldTests.Run(11, 150_000, neverGroom: false));

        [Test]
        public void SL01_TheFineTilesReachTheRingAsTheFieldHoldsThem()
        {
            var w = TrailSnowWorldTests.World(600, OneRun());
            w.JoinSnow();
            var fine = w.FineSnow;
            var tiles = w.FineTiles;
            Assert.That(tiles, Is.Not.Null);
            Assert.That(tiles.Count, Is.GreaterThan(0));
            var ring = new FineStagingRing(tiles.Count + 1);
            int staged = 0;
            for (int k = 0; k < 1000 && staged < tiles.Count; k++) staged += w.StageFine(ring);
            Assert.That(staged, Is.EqualTo(tiles.Count), "every tile with trail snow is staged once");
            Assert.That(w.StageFine(ring), Is.Zero, "and then nothing until the field changes");
            int per = fine.Layout.PerSide, bc = fine.BlockCells, cells = 0, owned = 0;
            for (long n = 0; n < ring.Written; n++)
            {
                ring.Read(n, out int listed, out int size, out ulong layout, out int offset, out ushort[] depth, out byte[] state);
                Assert.That(layout, Is.EqualTo(fine.Layout.Hash()));
                Assert.That(size, Is.EqualTo(tiles.TileFine));
                tiles.Origin(listed, out int cx, out int cy);
                for (int j = 0; j < size; j++)
                    for (int i = 0; i < size; i++)
                    {
                        int at = offset + j * size + i;
                        int ci = cx + i / per, cj = cy + j / per;
                        int b = ci < fine.Layout.GridWidth && cj < fine.Layout.GridHeight ? fine.Layout.BlockOf(cj * fine.Layout.GridWidth + ci) : -1;
                        cells++;
                        if (b < 0)
                        {
                            Assert.That(state[at * 4] & FineStagingRing.Owned, Is.Zero, "no block, no presence");
                            Assert.That(depth[at], Is.Zero);
                            continue;
                        }
                        owned++;
                        int f = b * bc + (j % per) * per + i % per;
                        Assert.That(depth[at], Is.EqualTo(FineStagingRing.DepthMm(fine, f)));
                        Assert.That(state[at * 4] & FineStagingRing.TypeMask, Is.EqualTo(fine.Type[f] & FineStagingRing.TypeMask));
                        Assert.That(state[at * 4 + 1], Is.EqualTo(fine.Moguls[f]));
                    }
            }
            Say($"[ring] {tiles.Count} tiles of {tiles.TileFine} × {tiles.TileFine} fine cells ({fine.Layout.FineMetres} m): {owned} of {cells} cells on blocks");
            Assert.That(owned, Is.EqualTo(fine.Cells), "every fine cell staged exactly once");
        }

        [Test]
        public void SL02_TheRingWaitsForItsReader()
        {
            var w = TrailSnowWorldTests.World(600, OneRun());
            w.JoinSnow();
            var ring = new FineStagingRing(2);
            int first = 0;
            for (int k = 0; k < 4; k++) first += w.StageFine(ring);
            Assert.That(first, Is.EqualTo(Math.Min(2, w.FineTiles.Count)), "a full ring stops the worker");
            ring.Release(ring.Written);
            Assert.That(w.StageFine(ring), Is.EqualTo(Math.Min(2, w.FineTiles.Count - first)), "a released ring takes the rest");
        }

        /// <summary>The v16 fixture's world: v14's (<see cref="TrailSnowWorldTests.GroomSaveWorld"/>) with a 12 ft machine made
        /// whale painted on its run.</summary>
        internal static SimWorld PaintSaveWorld()
        {
            var w = TrailSnowWorldTests.GroomSaveWorld();
            w.ApplyNow(SimCommand.Of(SimCommandKind.PaintSnow, 0, Pile(w, 150, 0, 8, 1.6)));
            Assert.That(w.PaintedCells, Is.GreaterThan(0), "the whale lands on the run");
            return w;
        }

        internal static SimSnowPaint Pile(SimWorld w, double centreX, double centreY, double radius, double waterMetres, int density = 450)
        {
            const int cell = 500;   // mm
            int n = (int)Math.Ceiling(2 * radius * 1000 / cell);
            var water = new int[n * n];
            long west = (long)Math.Round((centreX - radius) * 1000), south = (long)Math.Round((centreY - radius) * 1000);
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double x = (west + (i + 0.5) * cell) / 1000.0 - centreX, y = (south + (j + 0.5) * cell) / 1000.0 - centreY;
                    double r = Math.Sqrt(x * x + y * y) / radius;
                    water[j * n + i] = r >= 1 ? 0 : (int)Math.Round(waterMetres * 1e6 * (1 - r * r));
                }
            return new SimSnowPaint(west, south, cell, n, n, water, density);
        }

        [Test]
        public void SL03_APaintBuildsAMachineMadeWhaleUpToTheCapAndKeepsTheBlocks()
        {
            var w = TrailSnowWorldTests.World(600, OneRun());
            w.JoinSnow();
            var fine = w.FineSnow;
            long before = 0;
            for (int f = 0; f < fine.Cells; f++) before += fine.Swe[f];
            long removedBefore = w.TrailRemovedTotal;
            // A 12 m pile of water at 450 kg/m³ would stand 26 m: far over the cap, so its middle stops at 26 ft.
            w.ApplyNow(SimCommand.Of(SimCommandKind.PaintSnow, 0, Pile(w, 150, 0, 10, 12)));
            long after = 0, deepest = 0;
            int mm = 0;
            for (int f = 0; f < fine.Cells; f++)
            {
                after += fine.Swe[f];
                deepest = Math.Max(deepest, fine.DepthUm(f));
                if (fine.Type[f] == (byte)SnowType.MM) mm++;
            }
            Say($"[paint] {w.PaintedCells} fine cells, {mm} machine made; water +{(after - before) / 1e6:F2} m·cells; deepest {deepest / 1e6:F2} m");
            Assert.That(w.PaintedCells, Is.GreaterThan(0));
            Assert.That(mm, Is.EqualTo(w.PaintedCells));
            Assert.That(deepest, Is.LessThanOrEqualTo(Pacing.Default.WhaleMaxMm * 1000L + 1000), "never past WhaleMax (26 ft)");
            Assert.That(deepest, Is.GreaterThan(Pacing.Default.WhaleMaxMm * 1000L - 5000), "the middle reaches it");
            Assert.That(TrailSnowWorldTests.BlocksOff(w), Is.Zero, "every painted block rolled up onto its 8 m cell");
            long bc = fine.BlockCells;
            long added8 = removedBefore - w.TrailRemovedTotal;   // a paint counts as a negative take, in 8 m µm a cell
            Assert.That(Math.Abs(added8 * bc - (after - before)), Is.LessThan(bc * fine.Layout.Blocks), "the 8 m water follows the fine water");
        }

        /// <summary>The whale stays a whale through the hours after it (the built game drew it flat: finding PA-1).</summary>
        [Test]
        public void SL07_AWhaleOutlivesTheHoursAfterItAndIsStagedAsOne()
        {
            var w = TrailSnowWorldTests.World(600, OneRun());
            w.JoinSnow();
            w.ApplyNow(SimCommand.Of(SimCommandKind.PaintSnow, 0, Pile(w, 150, 0, 10, 12)));
            var fine = w.FineSnow;
            long Deepest() { long d = 0; for (int f = 0; f < fine.Cells; f++) d = Math.Max(d, fine.DepthUm(f)); return d; }
            long atPaint = Deepest();
            var hours = new long[] { 1, 6, 24 };
            long done = 0;
            foreach (long h in hours)
            {
                long end = w.FaceMs + (h - done) * SimWorld.DayMs / 24;
                while (w.FaceMs < end) w.Step(Pacing.Default.IdleTickFactor);
                w.JoinSnow();
                done = h;
                Say($"[whale] {h} h after the paint: deepest fine cell {Deepest() / 1e6:F2} m (at the paint {atPaint / 1e6:F2} m)");
            }
            Assert.That(Deepest(), Is.GreaterThan(atPaint * 0.8), "a day later the whale still stands (it settles a little)");
            var ring = new FineStagingRing(w.FineTiles.Count + 1);
            int staged = 0;
            for (int k = 0; k < 1000 && staged < w.FineTiles.Count; k++) staged += w.StageFine(ring);
            long shown = 0;
            for (long n = 0; n < ring.Written; n++)
            {
                ring.Read(n, out _, out int size, out _, out int offset, out ushort[] depth, out _);
                for (int k = 0; k < size * size; k++) shown = Math.Max(shown, depth[offset + k]);
            }
            Say($"[whale] staged for the screen: deepest {shown / 1000.0:F2} m");
            Assert.That(shown * 1000L, Is.EqualTo(Deepest()).Within(1000), "the screen gets the whale");
        }

        sealed class StillTime : IRealTime { public long Microseconds { get; set; } }

        /// <summary>
        /// Finding PA-1 (the built game, 2026-11-23): a paint while the clock is paused (as after <c>-simto</c>) reaches the
        /// screen's ring without another command: the host keeps coming back while fine tiles are due, as it does for 8 m ones.
        /// </summary>
        [Test]
        public void SL08_APaintWhilePausedReachesTheScreen()
        {
            // Two tiles a pass, so the run's tiles take several passes (the drawn resort has hundreds against 32 a pass).
            var w = TrailSnowWorldTests.World(600, OneRun(), Pacing.Parse("GroomWinchMaxGrade = 1000" + Environment.NewLine + "StageTilesPerTick = 2"));
            w.JoinSnow();
            var host = new SimulationHost(w, new StillTime(), threaded: false);   // a host starts paused
            var ring = host.FineStaging;
            long Drain()
            {
                long deepest = 0;
                for (long n = ring.Released; n < ring.Written; n++)
                {
                    ring.Read(n, out _, out int size, out _, out int offset, out ushort[] depth, out _);
                    for (int k = 0; k < size * size; k++) deepest = Math.Max(deepest, depth[offset + k]);
                }
                ring.Release(ring.Written);
                return deepest;
            }
            // Until the worker would sleep: everything it had is staged.
            for (int k = 0; k < 2000 && host.Service() >= 0; k++) Drain();
            Drain();
            Assert.That(w.FineTiles.Count, Is.GreaterThan(4));
            w.FineAllDue();   // as the hour the paint lands first does: every tile due, the paint's among them
            host.Send(SimCommandKind.PaintSnow, 0, Pile(w, 150, 0, 10, 12));
            long shown = 0;
            int passes = 0;
            for (; passes < 2000; passes++)
            {
                int wait = host.Service();
                shown = Math.Max(shown, Drain());
                if (wait < 0) break;
            }
            Say($"[paused paint] {passes + 1} passes until the worker sleeps; the screen got {shown / 1000.0:F2} m");
            Assert.That(w.PaintedCells, Is.GreaterThan(0));
            Assert.That(shown, Is.GreaterThan(Pacing.Default.WhaleMaxMm - 50), "the whale's tiles reach the screen before the worker sleeps");
            host.Dispose();
        }

        [Test]
        public void SL04_APaintOffTheTrailsIsRefused()
        {
            var w = TrailSnowWorldTests.World(600, OneRun());
            w.JoinSnow();
            w.ApplyNow(SimCommand.Of(SimCommandKind.PaintSnow, 0, Pile(w, -300, 0, 10, 2)));
            Assert.That(w.PaintedCells, Is.Zero);
        }

        [Test]
        public void SL05_GroomingFreshSnowLeavesPackedPowderUntilTheThirdNight()
        {
            var w = TrailSnowWorldTests.World(600, OneRun());
            w.JoinSnow();
            var fine = w.FineSnow;
            int probe = -1;
            for (int f = 0; f < fine.Cells && probe < 0; f++) if (fine.Layout.Owner[f] == 0) probe = f;
            for (int f = 0; f < fine.Cells; f++) if (fine.Layout.Owner[f] == 0) fine.Type[f] = (byte)SnowType.P;
            var seen = new SnowType[6];
            var groomed = new bool[6];
            for (int night = 0; night < 6; night++)
            {
                for (int h = 12; h < 36; h++) w.HourStepTrails((night + 1) * 24 + h);
                seen[night] = (SnowType)fine.Type[probe];
                groomed[night] = (fine.Flags[probe] & FineSnow.GroomMask) != 0;
            }
            Say($"[SN-14 v4] nights: {string.Join(", ", seen)}");
            Assert.That(seen[0], Is.EqualTo(SnowType.PP), "fresh snow grooms to packed powder");
            Assert.That(seen[1], Is.EqualTo(SnowType.PP), "and again");
            Assert.That(seen[2], Is.EqualTo(SnowType.MG), "the third grooming in a row reworks it");
            Assert.That(seen[4], Is.EqualTo(SnowType.HP), "five harden it, as before");
            Assert.That(groomed, Is.All.True, "every night it carries the groomed finish");
        }

        [Test]
        public void SL09_MogulsGrowByTheHoursPassesAndOutlastAPowderReset()
        {
            // SN-17 v2 (task 4.08 PR B): three passes an hour for a hundred hours grow the same moguls as 300 at once (the
            // growth telescopes, so no fraction is lost), and new powder zeroes the passes but keeps the moguls.
            var w = TrailSnowWorldTests.World(600, TrailSnowWorldTests.Layout(TrailSnowWorldTests.Run(11, 150_000, neverGroom: true)));
            w.JoinSnow();
            var fine = w.FineSnow;
            var L = fine.Layout;
            var passes = new int[Pacing.Default.WearBands];
            passes[3] = 3;
            for (int h = 0; h < 100; h++) w.ApplyWear(passes, 1);
            int want = 300 * Pacing.Default.MogulsPerKPasses / 1000, bumped = 0, other = 0, probe = -1;
            for (int f = 0; f < fine.Cells; f++)
            {
                if (L.Owner[f] != 0 || L.Lane[f] != 3 || fine.Swe[f] <= 0) continue;
                if (fine.Moguls[f] == want) { bumped++; if (probe < 0) probe = f; }
                else if (fine.Moguls[f] != 0) other++;
            }
            Say($"[SN-17 v2] {bumped} lane-3 cells at {want} moguls after 100 hours of 3 passes, {other} otherwise");
            Assert.That(bumped, Is.GreaterThan(0), "the steep run grows moguls");
            Assert.That(other, Is.Zero, "every bumped cell grew floor(300 x 0.128), hour by hour");

            // The next storm's powder reset: unskied again, moguls kept (halved only under 6 in of it).
            for (int f = 0; f < fine.Cells; f++) if (L.Owner[f] == 0) fine.Type[f] = (byte)SnowType.PP;
            long end = w.FaceMs + 40 * SimWorld.DayMs;
            int before = fine.Moguls[probe];
            bool reset = false;
            while (!reset && w.FaceMs < end)
            {
                int passes0 = fine.Passes[probe], moguls0 = fine.Moguls[probe];
                w.Step(1);
                w.JoinSnow();
                if (fine.Type[probe] == (byte)SnowType.P || fine.Type[probe] == (byte)SnowType.WP)
                {
                    reset = true;
                    Say($"[SN-17 v2] powder at face day {w.FaceMs / SimWorld.DayMs}: passes {passes0} -> {fine.Passes[probe]}, moguls {moguls0} -> {fine.Moguls[probe]}");
                    Assert.That(fine.Passes[probe], Is.Zero, "a powder reset zeroes the passes");
                    Assert.That(fine.Moguls[probe], Is.InRange(moguls0 / 2, moguls0), "and keeps the moguls (6 in halves them)");
                }
            }
            Assert.That(reset, Is.True, "a storm came within 40 face days");
            Assert.That(before, Is.GreaterThan(0));
        }

        [Test]
        public void SL10_NewSnowClearsLightTracksOnPowderToo()
        {
            // p4-08-findings R18 (task 4.08 PR B): a lane skied only lightly is still powder (20 passes, under PackedPasses), so
            // the next snow doesn't change its 8 m cell's type. The reset is the cell's own new snow all the same: the passes
            // go back to 0, or the old tracks would show at once on the new powder.
            var w = TrailSnowWorldTests.World(600, TrailSnowWorldTests.Layout(TrailSnowWorldTests.Run(11, 150_000, neverGroom: true)));
            w.JoinSnow();
            var fine = w.FineSnow;
            var L = fine.Layout;
            int probe = -1;
            for (int f = 0; f < fine.Cells && probe < 0; f++)
                if (L.Owner[f] == 0 && L.Lane[f] == 3 && fine.Swe[f] > 0) probe = f;
            Assert.That(probe, Is.GreaterThanOrEqualTo(0), "a lane-3 cell with snow");
            var light = new int[Pacing.Default.WearBands];
            light[3] = 20;
            Assert.That(20, Is.LessThan(Pacing.Default.PackedPasses), "light traffic: it stays powder");
            int cleared = 0;
            long end = w.FaceMs + 60 * SimWorld.DayMs;
            while (cleared == 0 && w.FaceMs < end)
            {
                bool Powder() => fine.Type[probe] == (byte)SnowType.P || fine.Type[probe] == (byte)SnowType.WP;
                if (Powder() && fine.Passes[probe] == 0 && (fine.Flags[probe] & FineSnow.Packed) == 0) w.ApplyWear(light, 1);
                byte type0 = fine.Type[probe];
                int passes0 = fine.Passes[probe];
                bool powder0 = Powder();
                w.Step(1);
                w.JoinSnow();
                if (powder0 && passes0 > 0 && fine.Passes[probe] == 0 && fine.Type[probe] == type0)
                {
                    cleared++;
                    Say($"[R18] face day {w.FaceMs / SimWorld.DayMs}: {(SnowType)type0} stayed {(SnowType)fine.Type[probe]}, passes {passes0} -> 0");
                }
            }
            Assert.That(cleared, Is.GreaterThan(0), "new snow on powder cleared the light tracks within 60 face days");
        }

        [Test]
        public void SL06_MachineMadeThawsToWetPowderAndPacksUnderTraffic()
        {
            var f = new SnowField(1, 1);
            f.Pack[0] = new SnowPack { Swe = 50_000, Density = 450_000, Albedo = 800_000 };
            f.Type[0] = (byte)SnowType.MM;
            var r = new SnowRules(Pacing.Default);
            SnowHour.Conditions(r, 0, f, 0, true, 0, 0, 0, 320);
            Assert.That((SnowType)f.Type[0], Is.EqualTo(SnowType.WP), "its first thaw, as powder's");
            Assert.That(r.Quality((int)SnowType.MM, 0), Is.InRange(r.Quality((int)SnowType.WP, 0) + 1, r.Quality((int)SnowType.P, 0) - 1),
                        "between wet powder and powder");
        }
    }
}
