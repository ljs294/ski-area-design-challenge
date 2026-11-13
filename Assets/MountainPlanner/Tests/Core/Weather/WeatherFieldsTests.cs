using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Acquisition.Weather.Qc;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05 PR B2: the weather over the map. The integer wet bulb against the psychrometric equation (WX-37 v2);
    /// the static inputs on synthetic terrain (a valley floor is sheltered, pooled and steered along its axis; a ridge's
    /// windward face is wetter than its lee, WX-35, WX-36 v2, WX-39); freezing rain (WX-38 v2); lift gusts from the map; a
    /// save with static inputs reloads to the same future; the budgets (WX-51); and WX-38 v2's lattice test on all seven
    /// recorded sites.
    /// </summary>
    public sealed class WeatherFieldsTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs;

        static WeatherTerrainTuning Tuning => SimWeather.TerrainTuning(P);

        // ---------------- WX-37: the wet bulb ----------------

        /// <summary>
        /// The psychrometric equation (ventilated psychrometer, over water, at <paramref name="hPa"/>), solved by bisection with
        /// Bolton's saturation pressure: written apart from the game's (Magnus, Alduchov and Eskridge), so it checks it.
        /// </summary>
        internal static double PsychrometricWetBulbF(double tempF, double rhPercent, double hPa)
        {
            double t = Psychrometrics.FToC(tempF);
            static double Es(double c) => 6.112 * Math.Exp(17.67 * c / (c + 243.5));   // hPa (Bolton)
            double e = rhPercent / 100 * Es(t);
            double lo = t - 40, hi = t;
            for (int k = 0; k < 80; k++)
            {
                double tw = (lo + hi) / 2;
                double f = Es(tw) - 6.60e-4 * (1 + 0.00115 * tw) * hPa * (t - tw) - e;
                if (f > 0) hi = tw; else lo = tw;
            }
            return Psychrometrics.CToF((lo + hi) / 2);
        }

        /// <summary>
        /// WX-37 v2's check at −10, 0, 20 and 30 °F and RH 20–95 %, at sea level, 6,200 ft and 10,000 ft: the game's wet bulb
        /// (integers) and calibration's (doubles) both within 0.5 °F of the equation, dry air or humid. (v1's Stull read up to
        /// 4 °F warm in dry cold air at 10,000 ft and 0.3–1.1 °F cold in humid air near freezing.)
        /// </summary>
        [Test]
        public void TheWetBulbMatchesThePsychrometricEquation()
        {
            foreach (int feet in new[] { 0, 6200, 10000 })
                foreach (int f in new[] { -10, 0, 20, 30 })
                    foreach (int rh in new[] { 20, 40, 60, 80, 95 })
                    {
                        double reference = PsychrometricWetBulbF(f, rh, Psychrometrics.StandardPressurePa(feet) / 100);
                        int dew = (int)Math.Round(Psychrometrics.CToF(Psychrometrics.DewPointC(Psychrometrics.SaturationPa(Psychrometrics.FToC(f)) * rh / 100)) * 10);
                        int wb = WeatherAir.WetBulb(f * 10, dew, feet);
                        double wbF = Psychrometrics.WetBulbF(f, rh, feet);
                        double gap = wb / 10.0 - reference;
                        TestContext.Progress.WriteLine($"{feet,6} ft {f,4} °F {rh,3} %: game {wb / 10.0,6:F1}, calibration {wbF,6:F2}, equation {reference,6:F2} ({gap:+0.0;-0.0})");
                        Assert.That(wb, Is.LessThanOrEqualTo(f * 10), "never above the air");
                        Assert.That(wb, Is.GreaterThanOrEqualTo(dew), "never below the dew point");
                        Assert.That(gap, Is.InRange(-0.5, 0.5), $"{f} °F at {rh} %, {feet} ft: the game");
                        Assert.That(wbF - reference, Is.InRange(-0.5, 0.5), $"{f} °F at {rh} %, {feet} ft: calibration");
                    }
        }

        /// <summary>The game's integer wet bulb against calibration's double one: −40 to 60 °F, RH 5–99 %, sea level to 14,000 ft.</summary>
        [Test]
        public void TheIntegerWetBulbTracksCalibrations()
        {
            double worst = 0;
            for (int feet = 0; feet <= 14000; feet += 1750)
                for (int t = -400; t <= 600; t += 7)
                    for (int dep = 1; dep <= 400; dep += 13)
                    {
                        int dew = t - dep;
                        double rh = Psychrometrics.RelativeHumidity(t / 10.0, dew / 10.0);
                        if (rh < 5) continue;
                        double gap = Math.Abs(WeatherAir.WetBulb(t, dew, feet) / 10.0 - Psychrometrics.WetBulbF(t / 10.0, rh, feet));
                        worst = Math.Max(worst, gap);
                    }
            TestContext.Progress.WriteLine($"the integer wet bulb's worst gap from calibration's: {worst:F2} °F");
            Assert.That(worst, Is.LessThanOrEqualTo(0.2), "within 0.2 °F: whole tenths, a table linear between whole °F");
            Assert.That(WeatherAir.RhPermille(300, 300), Is.EqualTo(1000));
            Assert.That(WeatherAir.WetBulb(300, 300, 8000), Is.EqualTo(300), "saturated air is its own wet bulb");
            Assert.That(WeatherAir.WetBulb(300, 200, 10000), Is.LessThan(WeatherAir.WetBulb(300, 200, 0)), "thinner air, a colder wet bulb");
        }

        /// <summary>The tables are literals: every runtime (CoreCLR here, Mono in Unity) must read the same bytes.</summary>
        [Test]
        public void TheWetBulbTableIsPinned()
        {
            TestContext.Progress.WriteLine($"wet-bulb table hash: 0x{WeatherAir.TableHash():X16}");
            Assert.That(WeatherAir.TableHash(), Is.EqualTo(TablePin));
        }

        const ulong TablePin = 0x0B9F692F71711F64UL;

        // ---------------- WX-35, WX-36 v2, WX-39: the static inputs ----------------

        /// <summary>
        /// A north–south V valley <paramref name="w"/> × <paramref name="d"/> cells, its floor down the middle column, rising 1 ft per
        /// metre to each side (so the valley walls stand 1,000 ft above the floor 1 km out); the reference cell at (<paramref name="refI"/>, middle).
        /// </summary>
        static WeatherTerrain Valley(int w, int d, int refI, out int[] feet)
        {
            feet = new int[w * d];
            for (int j = 0; j < d; j++)
                for (int i = 0; i < w; i++)
                    feet[j * w + i] = 6000 + Math.Abs(i - w / 2) * WeatherTerrain.CellMetres;
            return WeatherTerrain.Build(w, d, feet, -w / 2 * 8000, -d / 2 * 8000, (0, 0, w, d), d / 2 * w + refI, Tuning);
        }

        static int Steer(WeatherTerrain t, int i, int j) => j / WeatherTerrain.SteerCells * t.SteerWidth + i / WeatherTerrain.SteerCells;

        [Test]
        public void AValleyFloorIsShelteredAndSteeredAlongItsAxis()
        {
            const int w = 400, d = 400;
            var t = Valley(w, d, w / 2 + 30, out _);
            int floor = d / 2 * w + w / 2, ridge = d / 2 * w + w / 2 + 160;
            Assert.That(t.Hvf[floor], Is.LessThanOrEqualTo(1), "the floor is the floor");
            Assert.That(t.Hvf[ridge] * 10, Is.EqualTo(1000).Within(10), "1 km out, 1,000 ft above it");
            Assert.That(t.Exposure[floor], Is.EqualTo(0), "a deep valley floor is the most sheltered class");
            Assert.That(t.Exposure[ridge], Is.GreaterThanOrEqualTo(4));

            int sf = Steer(t, w / 2, d / 2), cells = t.SteerWidth * t.SteerDepth;
            int across = WeatherTerrain.TurnDegrees(t.Axis[sf], t.TurnWeight[sf], 90), along = WeatherTerrain.TurnDegrees(t.Axis[sf], t.TurnWeight[sf], 180);
            TestContext.Progress.WriteLine($"floor: a west wind turns {across}°, a north wind {along}°; speed {t.Speed[6 * cells + sf]}%, ridge {t.Speed[6 * cells + Steer(t, w / 2 + 160, d / 2)]}%");
            Assert.That(Math.Abs(across), Is.GreaterThanOrEqualTo(80), "a cross-valley wind turns to run along the floor");
            Assert.That(Math.Abs(along), Is.LessThanOrEqualTo(4), "a wind along the valley keeps its line");
            Assert.That(t.Speed[6 * cells + sf], Is.LessThan(t.Speed[6 * cells + Steer(t, w / 2 + 160, d / 2)]), "the floor's wind is slower than the slope's");
        }

        static WeatherHour Hour(int temp, int dew, int pool = 0, int liquid = 0, int wind = 200, int direction = 270)
        {
            return new WeatherHour
            {
                Temp = temp, Dew = dew, Lapse = 0, DewLapse = 0, Pool = pool, Liquid = liquid, PrecipGradient = 0,
                Wind = wind, GustFactor = 1500, Direction = direction, StormOrdinal = liquid > 0 ? 0 : -1, StormDirection = direction,
            };
        }

        [Test]
        public void ThePoolCoolsTheFloorAndNotTheRidge()
        {
            const int w = 400, d = 400;
            var t = Valley(w, d, w / 2 + 30, out _);
            int floor = d / 2 * w + w / 2, ridge = d / 2 * w + w / 2 + 160;
            var still = new WeatherFields();
            var pooled = new WeatherFields();
            still.Fill(0, Hour(200, 150), t, 6000, P, 0);
            pooled.Fill(0, Hour(200, 150, pool: 80), t, 6000, P, 0);
            still.At(floor, out var f0); pooled.At(floor, out var f1);
            still.At(ridge, out var r0); pooled.At(ridge, out var r1);
            TestContext.Progress.WriteLine($"hvf_ref {t.HvfRefFeet} ft; floor {f0.Temp / 10.0} → {f1.Temp / 10.0} °F, ridge {r0.Temp / 10.0} → {r1.Temp / 10.0} °F");
            Assert.That(f1.Temp, Is.LessThan(f0.Temp - 20), "the pool cools the floor");
            Assert.That(r1.Temp, Is.GreaterThanOrEqualTo(r0.Temp), "and not the ridge (above the reference pixel's pool it's warmer)");
        }

        [Test]
        public void FreezingRainIsReachable()
        {
            const int w = 400, d = 400;
            var t = Valley(w, d, w / 2 + 30, out _);
            var f = new WeatherFields();
            // 38 °F and near-saturated at zref (it would rain), a deep pool, liquid falling.
            f.Fill(0, Hour(380, 370, pool: 200, liquid: 10), t, 6000, P, 0);
            f.At(d / 2 * w + w / 2, out var floor);
            f.At(d / 2 * w + w / 2 + 160, out var ridge);
            TestContext.Progress.WriteLine($"floor {floor.Temp / 10.0} °F freezing rain {floor.FreezingRain}; ridge {ridge.Temp / 10.0} °F");
            Assert.That(floor.FreezingRain, "warm air over the pooled floor glazes");
            Assert.That(floor.SnowTenthsIn, Is.EqualTo(0));
            Assert.That(ridge.FreezingRain, Is.False, "above the pool it's plain rain");
        }

        /// <summary>A north–south ridge 2,400 ft high in the middle of an 8 × 4 km box.</summary>
        static WeatherTerrain Ridge(out int[] feet)
        {
            const int w = 1024, d = 512;
            feet = new int[w * d];
            for (int j = 0; j < d; j++)
                for (int i = 0; i < w; i++)
                    feet[j * w + i] = 6000 + Math.Max(0, 2400 - Math.Abs(i - w / 2) * 6);
            return WeatherTerrain.Build(w, d, feet, -w / 2 * 8000, -d / 2 * 8000, (0, 0, w, d), d / 2 * w + w / 2, Tuning);
        }

        [Test]
        public void AWindwardFaceIsWetterThanItsLee()
        {
            var t = Ridge(out _);
            int cells = t.CoarseWidth * t.CoarseDepth, o = 6;   // a wind from the west
            double Mean(int x0, int x1)
            {
                double s = 0; int n = 0;
                for (int j = 0; j < t.CoarseDepth; j++) for (int i = x0; i <= x1; i++) { s += t.Orographic[o * cells + j * t.CoarseWidth + i]; n++; }
                return s / n;
            }
            double windward = Mean(4, 6), lee = Mean(11, 13), box = Mean(0, t.CoarseWidth - 1);
            TestContext.Progress.WriteLine($"west wind: windward {windward:F0}‰, far lee {lee:F0}‰, box {box:F0}‰");
            Assert.That(windward, Is.GreaterThan(1000));
            Assert.That(lee, Is.LessThan(1000));
            Assert.That(box, Is.EqualTo(1000).Within(25), "the factor moves the snow, never adds any");

            // And the fields carry it: a storm from the west drops more on the windward face than in the lee.
            var f = new WeatherFields();
            f.Fill(0, Hour(150, 140, liquid: 20), t, 6000, P, 0);
            int row = t.Depth / 2 * t.Width;
            f.At(row + 5 * 64 + 32, out var a);
            f.At(row + 12 * 64 + 32, out var b);
            Assert.That(a.Liquid, Is.GreaterThan(b.Liquid));
        }

        // ---------------- WX-39: lift gusts from the map ----------------

        static (int X, int Y) CellCentreMm(WeatherTerrain t, int i, int j) =>
            (t.OriginXMm + (i * WeatherTerrain.CellMetres + 4) * 1000, t.OriginYMm + (j * WeatherTerrain.CellMetres + 4) * 1000);

        static SimDesign TwoLifts(WeatherTerrain t)
        {
            LiftInput Lift(ulong id, int i, int feet) =>
                new LiftInput(id, true, LiftSchedule.Daily, null, LiftKind.DetachableChair, 4, 6000, 360_000, topFeet: feet, topMm: CellCentreMm(t, i, 200));
            return new SimDesign(1, 1, 77, DayHours.Default, new[] { Lift(1, 200, 7000), Lift(2, 360, 7000) }, 1);
        }

        static void RunTo(SimWorld w, long faceMs, Action<SimWorld> eachHour = null)
        {
            long last = w.HourIndex;
            while (w.FaceMs < faceMs)
            {
                w.Step(P.IdleTickFactor);
                if (w.HourIndex != last) { last = w.HourIndex; eachHour?.Invoke(w); }
            }
        }

        [Test]
        public void ALiftOnTheRidgeGustsMoreThanOneOnTheFloor()
        {
            var t = Valley(400, 400, 230, out _);
            var w = new SimWorld(P, TwoLifts(t), SimSite.None, 2026, weather: SimWeatherTests.Golden());
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, t));
            var s = new SimSnapshot(P);
            int hours = 0, more = 0;
            RunTo(w, 10 * Day, x =>
            {
                x.Write(s);
                if (s.LiftGustTenths[0] == 0 && s.LiftGustTenths[1] == 0) return;
                hours++;
                if (s.LiftGustTenths[1] > s.LiftGustTenths[0]) more++;
            });
            TestContext.Progress.WriteLine($"the ridge lift gusted harder in {more} of {hours} windy hours");
            Assert.That(hours, Is.GreaterThan(100));
            Assert.That(more, Is.GreaterThanOrEqualTo(hours * 95 / 100));
            Assert.That(s.WeatherFields.Hour, Is.EqualTo(w.HourIndex), "the snapshot carries the hour's tables");
            s.WeatherFields.At(t.CellAt(CellCentreMm(t, 360, 200).X, CellCentreMm(t, 360, 200).Y), out var c);
            Assert.That(c.Gust, Is.EqualTo(s.LiftGustTenths[1]), "a lift's gust is its top station's cell");
        }

        [Test]
        public void ASaveWithStaticInputsReloadsToTheSameFuture()
        {
            var t = Valley(400, 400, 230, out _);
            var wx = SimWeatherTests.Golden();
            var a = new SimWorld(P, TwoLifts(t), SimSite.None, 2026, weather: wx);
            a.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, t));
            a.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            RunTo(a, 3 * Day + 5 * SimWorld.HourMs + 777);
            var b = SimSidecar.Read(a.SaveState(), P, a.Design, SimSite.None, out _, wx);
            b.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, t));
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()));
            var ha = new List<ulong>();
            var hb = new List<ulong>();
            RunTo(a, 7 * Day, x => ha.Add(x.HourHash));
            RunTo(b, 7 * Day, x => hb.Add(x.HourHash));
            Assert.That(hb, Is.EqualTo(ha));

            // The static inputs matter: the same world without them steps differently (B1's height rule).
            var c = new SimWorld(P, TwoLifts(t), SimSite.None, 2026, weather: wx);
            c.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            var hc = new List<ulong>();
            RunTo(c, 3 * Day + 5 * SimWorld.HourMs + 777);
            RunTo(c, 7 * Day, x => hc.Add(x.HourHash));
            Assert.That(hc, Is.Not.EqualTo(ha));
        }

        [Test]
        public void TheSunShinesByDayOnly()
        {
            var t = Valley(400, 400, 230, out _);
            var w = new SimWorld(P, TwoLifts(t), new SimSite(43.59, -110.83), 2026, weather: SimWeatherTests.Golden());
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, t));
            var s = new SimSnapshot(P);
            int most = 0;
            RunTo(w, 6 * Day, x =>
            {
                x.Write(s);
                if (x.WeatherNow.HourOfDay < 5 || x.WeatherNow.HourOfDay > 19) Assert.That(s.WeatherFields.Shortwave, Is.EqualTo(0), "night");
                most = Math.Max(most, s.WeatherFields.Shortwave);
            });
            TestContext.Progress.WriteLine($"October's strongest sun over six days: {most} W/m²");
            Assert.That(most, Is.InRange(250, 900));
        }

        // ---------------- WX-51: budgets ----------------

        /// <summary>Rolling hills on the largest box (11 km at 8 m, 1,375² cells).</summary>
        static int[] Hills(int n)
        {
            var feet = new int[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    feet[j * n + i] = 7000 + (int)(1500 * Math.Sin(i * 0.004) * Math.Cos(j * 0.003) + 300 * Math.Sin(i * 0.031 + j * 0.017));
            return feet;
        }

        [Test]
        public void TheStaticInputsFitTheirBudgets()
        {
            const int n = 1375;
            var feet = Hills(n);
            WeatherTerrain.Build(256, 256, Hills(256), 0, 0, (0, 0, 256, 256), 0, Tuning);   // warm up
            var watch = Stopwatch.StartNew();
            var t = WeatherTerrain.Build(n, n, feet, -n * 4000, -n * 4000, (125, 125, n - 125, n - 125), n / 2 * n + n / 2, Tuning);
            watch.Stop();
            long bytes = t.Bin.Length + t.Hvf.Length + t.Exposure.Length + t.Present.Length + t.Orographic.Length * 2L + t.Axis.Length + t.TurnWeight.Length + t.Speed.Length + t.BinHvfFeet.Length * 2;
            TestContext.Progress.WriteLine($"static inputs at 11 km: {bytes / 1048576.0:F1} MB");
            Budget.AtMost(watch.Elapsed.TotalMilliseconds, 1000, "static inputs at 11 km (1,375² cells)");

            var gen = new WeatherGenerator(SimWeatherTests.Golden().Rows, SimWeather.Tuning(P));
            gen.Reset(42, 2026);
            var hours = new WeatherHour[200];
            for (int k = 0; k < hours.Length; k++) gen.Step(ref hours[k]);
            var f = new WeatherFields();
            f.Fill(0, hours[0], t, 7000, P, 0);
            bool counter = true;
            try { GC.GetAllocatedBytesForCurrentThread(); } catch (Exception e) when (e is NotImplementedException || e is PlatformNotSupportedException) { counter = false; }
            long before = counter ? GC.GetAllocatedBytesForCurrentThread() : 0;
            watch.Restart();
            for (int k = 0; k < hours.Length; k++) f.Fill(k, hours[k], t, 7000, P, 0);
            watch.Stop();
            long allocated = counter ? GC.GetAllocatedBytesForCurrentThread() - before : 0;
            Budget.AtMost(watch.Elapsed.TotalMilliseconds / hours.Length, 1, "one face hour's table fill at 11 km");
            Assert.That(allocated, Is.EqualTo(0), "filling allocates nothing");

            // The batch read 4.07 will make over the whole box, in 64-row bands.
            var band = new WeatherCell[64 * n];
            watch.Restart();
            for (int y = 0; y < n; y += 64) f.ReadRows(y, y + 64, band);
            watch.Stop();
            Budget.AtMost(watch.Elapsed.TotalMilliseconds, 250, "the batch read of 1.9 M cells");
            int known = 0;
            for (int k = 0; k < band.Length; k++) if (band[k].Known) known++;
            Assert.That(known, Is.EqualTo(band.Length), "every cell of the hills is known");
        }

        [Test]
        public void AMaskedReadSkipsItsTiles()
        {
            var t = Valley(256, 128, 140, out _);
            var f = new WeatherFields();
            f.Fill(0, Hour(200, 150), t, 6000, P, 0);
            var dest = new WeatherCell[64 * 256];
            var mask = new[] { true, false, false, true, false, false, false, false };   // 4 tiles wide, 2 high
            f.ReadRows(0, 64, dest, mask);
            Assert.That(dest[10].Known, "tile 0");
            Assert.That(dest[70].Known, Is.False, "tile 1 masked");
            Assert.That(dest[200].Known, "tile 3");
        }

        /// <summary>WX-51: the static inputs add no allocation to the hour step (against the world without weather, as B1's test).</summary>
        [Test]
        public void TheWeatherOverTheMapAllocatesNothing()
        {
            var t = Valley(400, 400, 230, out _);
            long Bytes(bool weather)
            {
                var w = new SimWorld(P, TwoLifts(t), SimSite.None, 2026, weather: weather ? SimWeatherTests.Golden() : null);
                if (weather) w.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, t));
                var s = new SimSnapshot(P);
                RunTo(w, 2 * Day);
                w.Step(P.IdleTickFactor, s);
                long before = GC.GetAllocatedBytesForCurrentThread();
                while (w.FaceMs < 6 * Day) w.Step(P.IdleTickFactor, s);
                return GC.GetAllocatedBytesForCurrentThread() - before;
            }
            try { GC.GetAllocatedBytesForCurrentThread(); }
            catch (Exception e) when (e is NotImplementedException || e is PlatformNotSupportedException) { Assert.Ignore("no per-thread allocation counter here"); return; }
            long without = Bytes(false), with = Bytes(true);
            TestContext.Progress.WriteLine($"four face days: {without} B without weather, {with} B with the map's");
            Assert.That(with, Is.LessThanOrEqualTo(without));
        }

        // ---------------- WX-38 v2: the lattice test ----------------

        static bool NovToApr(int month) => month >= 11 || month <= 4;

        /// <summary>
        /// Over the Daymet lattice pixels, the generated November–April liquid (the rows at zref × the elevation factor at each
        /// pixel's own Daymet elevation, 200 seeds) against Daymet's own: the mean within ±10 %, the slope with elevation within
        /// ±30 % (WX-38 v2), at every site. No per-pixel band: Daymet can't tell windward from lee. PR B2 found three sites'
        /// slopes 1.45–2.05 × Daymet's (calibration's plain mean over wet days); WX-38 v3 weights each day by its liquid.
        /// </summary>
        [Test]
        public void TheLatticeTest([ValueSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))] string site)
        {
            var record = WeatherFixtures.Screened(site).Record;
            var rows = SimWeatherTests.Frozen(site);
            var pixels = record.Lattice.Where(p => p.Has("prcp")).ToList();
            if (pixels.Count < 6) Assert.Ignore($"{site}: {pixels.Count} lattice pixels with precipitation");

            // Daymet: each pixel's mean November–April total (0.01 in) over the record's whole label years.
            var daymet = new double[pixels.Count];
            for (int p = 0; p < pixels.Count; p++)
            {
                var px = pixels[p];
                var prcp = px.Series("prcp");
                var byYear = new Dictionary<int, (double Sum, int Days)>();
                for (int k = 0; k < px.Days.Count; k++)
                {
                    int civil = px.Days[k];
                    var date = CivilDay.Date(civil);
                    if (!NovToApr(date.Month) || px.RejectedDays.Contains(civil) || double.IsNaN(prcp[k])) continue;
                    int label = date.Month >= 11 ? date.Year : date.Year - 1;
                    if (label < record.FirstLabelYear || label > record.LastLabelYear) continue;
                    byYear.TryGetValue(label, out var y);
                    byYear[label] = (y.Sum + prcp[k], y.Days + 1);
                }
                var whole = byYear.Values.Where(y => y.Days >= 175).ToList();
                daymet[p] = whole.Count == 0 ? double.NaN : whole.Average(y => y.Sum) / 0.254;
            }

            // The game: 200 seeds of the rows, each wet hour's liquid × the elevation factor at each pixel.
            var feet = pixels.Select(px => (int)Math.Round(px.ElevationMetres / 0.3048)).ToArray();
            var game = new double[pixels.Count];
            var tuning = SimWeather.Tuning(P);
            var gen = new WeatherGenerator(rows, tuning);
            int hours = WeatherGenerator.HoursInYear(tuning);
            var h = new WeatherHour();
            const int seeds = 200;
            for (int s = 0; s < seeds; s++)
            {
                gen.Reset(0x4C41_5454_0000_0000UL + (ulong)s, 2026);
                for (int k = 0; k < hours; k++)
                {
                    gen.Step(ref h);
                    if (h.Liquid <= 0 || h.Third < 3 || h.Third >= 21) continue;
                    for (int p = 0; p < feet.Length; p++) game[p] += (double)h.Liquid * WeatherFields.PrecipFactor(h, feet[p] - rows.ZrefFeet) / 1000;
                }
            }
            for (int p = 0; p < game.Length; p++) game[p] /= seeds;

            var centres = new List<(double X, double Y)>();
            for (int i = 0; i < record.Lattice.Count; i++) if (record.Lattice[i].Has("prcp")) centres.Add(record.LatticeCentres[i]);
            var ok = Enumerable.Range(0, pixels.Count).Where(p => !double.IsNaN(daymet[p])).ToList();
            double dMean = ok.Average(p => daymet[p]), gMean = ok.Average(p => game[p]);
            // As calibration measures the gradient (WX-36 v2's method): on elevation with the pixels' x and y as covariates, so a
            // tilt across the box (a crest to the west) isn't read as elevation.
            double x0 = ok.Average(p => centres[p].X), y0 = ok.Average(p => centres[p].Y);
            var xs = ok.Select(p => new[] { 1, feet[p] / 1000.0, (centres[p].X - x0) / 1000, (centres[p].Y - y0) / 1000 }).ToList();
            double Slope(double[] v) => LeastSquares.Solve(xs, ok.Select(p => v[p]).ToList())[1];   // 0.01 in per 1,000 ft
            double dSlope = Slope(daymet), gSlope = Slope(game);
            double relief = ok.Max(p => feet[p]) - ok.Min(p => feet[p]);
            TestContext.Progress.WriteLine(
                $"{site}: {ok.Count} pixels over {relief:F0} ft; Nov–Apr liquid mean game {gMean / 100:F2} in, Daymet {dMean / 100:F2} in " +
                $"(×{gMean / dMean:F3}); slope game {gSlope / 100:F2} in, Daymet {dSlope / 100:F2} in per 1,000 ft (×{(dSlope == 0 ? double.NaN : gSlope / dSlope):F2})");
            Assert.That(gMean / dMean, Is.InRange(0.9, 1.1), "the mean within ±10 %");
            Assert.That(gSlope, Is.EqualTo(dSlope).Within(Math.Abs(dSlope) * 0.3), "the slope with elevation within ±30 %");
        }
    }
}
