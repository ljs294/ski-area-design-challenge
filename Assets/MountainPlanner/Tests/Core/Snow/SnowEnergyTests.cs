using System;
using System.Linq;
using MountainPlanner.Domain.Sky;
using MountainPlanner.Domain.Weather;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A1: the snow energy (WX-59…64, SN-09 v2) and its integer sun. Each test names the rule it holds; the
    /// pillow check against measured snow is SnowPillowTests.
    /// </summary>
    public sealed class SnowEnergyTests
    {
        /// <summary>A face hour in real seconds: K = 121 ÷ 42 calendar hours (WX-21).</summary>
        public const int FaceHourSeconds = 121 * 86_400 / 42 / 24;

        static readonly SnowTuning K = SnowTuning.Default;

        // ---------------- The integer sun ----------------

        [Test]
        public void WX60_TheIntegerSunIsWithinAFifthOfADegreeOfTheFloatSun()
        {
            double worst = 0; string where = "";
            foreach (double lat in new[] { 27.0, 39.6, 43.6, 47.7, 61.2 })
                foreach (double lon in new[] { -122.4, -110.8, -71.2 })
                    for (int day = 1; day <= 365; day += 9)
                        for (int hour = 0; hour < 24; hour++)
                        {
                            var sd = new SolarDay(lat, lon, 2026, day);
                            var f = sd.SunAt(hour * 3600 + 1800);
                            if (f.GeometricElevation < 1) continue;
                            double el = f.GeometricElevation * Math.PI / 180, az = f.Azimuth * Math.PI / 180;
                            double fx = Math.Sin(az) * Math.Cos(el), fy = Math.Cos(az) * Math.Cos(el), fz = Math.Sin(el);
                            var s = SunVector.At((int)Math.Round(lat * 1e6), (int)Math.Round(lon * 1e6), day, hour * 3600 + 1800);
                            double dot = (fx * s.East + fy * s.North + fz * s.Up) / SunVector.One;
                            double angle = Math.Acos(Math.Min(1, dot)) * 180 / Math.PI;
                            if (angle > worst) { worst = angle; where = $"lat {lat} lon {lon} day {day} hour {hour} el {f.GeometricElevation:0.0}"; }
                        }
            Console.WriteLine($"[sun] worst angle between the integer and float sun: {worst:0.000}° at {where}");
            Assert.That(worst, Is.LessThan(0.2), "the snow's sun is the sky's sun (0.2° moves a slope's sun by under 0.4 %)");
        }

        [Test]
        public void WX60_TheSineTableIsTheGeneratorsOutput()
        {
            for (int i = 0; i <= 900; i++)
                Assert.That(SunTables.SinMicro[i], Is.EqualTo((int)Math.Round(Math.Sin(i / 10.0 * Math.PI / 180) * 1e6)).Within(1), $"sin {i / 10.0}°");
            Assert.That(SunTables.DeclinationMilli.Length, Is.EqualTo(366));
            Assert.That(SunTables.EquationOfTimeSeconds.Length, Is.EqualTo(366));
            // Solstices and equinoxes, as the float algorithm has them in 2026.
            Assert.That(SunTables.DeclinationMilli[171] / 1000.0, Is.EqualTo(23.44).Within(0.05), "June 21");
            Assert.That(SunTables.DeclinationMilli[354] / 1000.0, Is.EqualTo(-23.44).Within(0.05), "December 21");
        }

        [Test]
        public void WX62_TheSkyTableIsTheGeneratorsOutput()
        {
            int[] built = SnowEnergy.BuildClearSky(), table = SnowEnergyTables.ClearSkyPermille;
            Assert.That(table.Length, Is.EqualTo(built.Length));
            for (int i = 0; i < table.Length; i++) Assert.That(table[i], Is.EqualTo(built[i]).Within(1), $"dew {i - 80} °F");
            // Brutsaert's sky: humid air radiates more; at 0 °C and a 32 °F dew point about 0.72.
            Assert.That(SnowEnergy.ClearSky(320, 0), Is.EqualTo(721).Within(2));
            Assert.That(SnowEnergy.ClearSky(0, -1000), Is.LessThan(SnowEnergy.ClearSky(200, -1000)));
        }

        // ---------------- Helpers ----------------

        /// <summary>A ripe or cold pack: water (in), its temperature (°C), density (kg/m³).</summary>
        public static SnowPack Pack(double sweIn, double tempC = 0, int density = 350, double liquidShare = 0)
        {
            int swe = (int)Math.Round(sweIn * 25_400);
            int t = (int)Math.Round(tempC * 100);
            int liquid = (int)(swe * liquidShare);
            return new SnowPack
            {
                Swe = swe, Liquid = liquid, SurfaceLiquid = (int)((long)liquid * Math.Min(swe, K.SurfaceUm) / Math.Max(1, swe)),
                SurfaceTemp = t, DeepTemp = t * 100, Albedo = 750_000, Density = density * 1000,
            };
        }

        public static SnowInput Hour(double airF, double dewF, int feet = 9000, double windMph = 5, int cloud = 0, int beam = 0, int diffuse = 0,
                                     int openSky = 1000, int canopy = 0, int seconds = FaceHourSeconds)
        {
            int air = (int)Math.Round(airF * 10), dew = (int)Math.Round(Math.Min(airF, dewF) * 10);
            return new SnowInput
            {
                Seconds = seconds, Air = air, Dew = dew, WetBulb = WeatherAir.WetBulb(air, dew, feet), Feet = feet,
                Wind = (int)Math.Round(windMph * 10), Cloud = cloud, Beam = beam, Diffuse = diffuse, OpenSky = openSky, Canopy = canopy,
            };
        }

        /// <summary>A unit normal (millionths) for a slope of <paramref name="slopeDeg"/> facing <paramref name="aspectDeg"/> (true).</summary>
        public static (int X, int Y, int Z) Normal(double slopeDeg, double aspectDeg)
        {
            double s = slopeDeg * Math.PI / 180, a = aspectDeg * Math.PI / 180;
            return ((int)Math.Round(Math.Sin(s) * Math.Sin(a) * 1e6), (int)Math.Round(Math.Sin(s) * Math.Cos(a) * 1e6), (int)Math.Round(Math.Cos(s) * 1e6));
        }

        /// <summary>
        /// One face day on a slope at 43.6° N (Jackson Hole's latitude), clear: the air runs from <paramref name="lowF"/> at
        /// dawn to <paramref name="highF"/> mid-afternoon. Returns the water lost (µm).
        /// </summary>
        static int ClearDay(ref SnowPack p, int dayOfYear, double lowF, double highF, (int X, int Y, int Z) n, int clearness = 900,
                            bool shaded = false, int openSky = 1000)
        {
            int start = p.Swe;
            for (int h = 0; h < 24; h++)
            {
                double phase = Math.Cos((h - 15) * Math.PI / 12);
                double air = lowF + (highF - lowF) * (phase + 1) / 2;
                var sun = SunVector.At(43_600_000, -110_800_000, dayOfYear, h * 3600 + 1800);
                var (beam, diffuse) = SlopeSun.At(sun, sun.AboveHorizon ? clearness : 0, n.X, n.Y, n.Z, openSky, !shaded);
                var x = Hour(air, air - 12, cloud: 100, beam: beam, diffuse: diffuse, openSky: openSky);
                SnowEnergy.Step(ref p, x, K);
            }
            return start - p.Swe;
        }

        // ---------------- The rules ----------------

        [Test]
        public void WX60_ASouthFaceMeltsFasterThanANorthFace()
        {
            var south = Pack(20, 0); var north = Pack(20, 0); var flat = Pack(20, 0);
            int s = 0, nn = 0, f = 0;
            for (int d = 0; d < 6; d++)
            {
                s += ClearDay(ref south, 75 + d, 22, 40, Normal(30, 180));
                nn += ClearDay(ref north, 75 + d, 22, 40, Normal(30, 0));
                f += ClearDay(ref flat, 75 + d, 22, 40, Normal(0, 0));
            }
            Console.WriteLine($"[aspect] six clear March face days, 22–40 °F: south lost {s / 25_400.0:0.00} in, flat {f / 25_400.0:0.00}, north {nn / 25_400.0:0.00}");
            Assert.That(s, Is.GreaterThan(2 * Math.Max(1, nn)), "a 30° south face loses at least twice the north face's water");
            Assert.That(f, Is.GreaterThan(nn).And.LessThan(s), "flat ground sits between");
        }

        [Test]
        public void WX59_ShadeUnderTheSkylineMeltsLessThanOpenGround()
        {
            var open = Pack(20, 0); var shade = Pack(20, 0);
            int o = 0, sh = 0;
            for (int d = 0; d < 6; d++)
            {
                o += ClearDay(ref open, 80 + d, 24, 42, Normal(15, 160));
                sh += ClearDay(ref shade, 80 + d, 24, 42, Normal(15, 160), shaded: true, openSky: 600);
            }
            Console.WriteLine($"[shade] six clear face days: open lost {o / 25_400.0:0.00} in, under the skyline {sh / 25_400.0:0.00} in");
            Assert.That(sh, Is.LessThan(o / 2), "a cell under its skyline loses less than half the open cell's water");
        }

        [Test]
        public void WX62_AClearNightRefreezesACloudyOneDoesNot()
        {
            var clear = Pack(30, 0, 400, 0.03); var cloudy = Pack(30, 0, 400, 0.03);
            bool clearFroze = false, cloudyFroze = false;
            for (int h = 0; h < 10; h++)
            {
                clearFroze |= SnowEnergy.Step(ref clear, Hour(33, 20, cloud: 0), K).Frozen;
                cloudyFroze |= SnowEnergy.Step(ref cloudy, Hour(33, 33, cloud: 1000), K).Frozen;
            }
            Console.WriteLine($"[night] 33 °F air, 10 face hours: clear surface {clear.SurfaceTemp / 100.0:0.0} °C, cloudy {cloudy.SurfaceTemp / 100.0:0.0} °C");
            Assert.That(clearFroze, Is.True, "a clear night crusts the surface");
            Assert.That(cloudyFroze, Is.False, "under cloud the surface stays soft");
        }

        [Test]
        public void WX63_AColdPackSurvivesAOneAfternoonThaw()
        {
            var p = Pack(25, -8, 300);
            int runoff = 0, melt = 0;
            for (int h = 0; h < 3; h++)
            {
                var sun = SunVector.At(43_600_000, -110_800_000, 20, (12 + h) * 3600 + 1800);
                var n = Normal(0, 0);
                var (beam, diffuse) = SlopeSun.At(sun, 900, n.X, n.Y, n.Z, 1000, true);
                var o = SnowEnergy.Step(ref p, Hour(40, 30, beam: beam, diffuse: diffuse), K);
                runoff += o.Runoff; melt += o.Melt;
            }
            Console.WriteLine($"[cold pack] a 40 °F January afternoon on a −8 °C pack: melt {melt} µm, runoff {runoff} µm, pack {p.PackTemp / 100.0:0.0} °C");
            Assert.That(runoff, Is.Zero, "the cold pack refreezes the afternoon's melt: nothing runs off");
            Assert.That(p.PackTemp, Is.LessThan(0), "the deep pack is still below freezing");
        }

        [Test]
        public void WX63_AWarmHumidWindMeltsHarderThanADryOne()
        {
            var humid = Pack(30, 0, 400); var dry = Pack(30, 0, 400);
            int h = 0, d = 0;
            for (int i = 0; i < 6; i++)
            {
                var a = SnowEnergy.Step(ref humid, Hour(45, 43, windMph: 15, cloud: 1000), K);
                var b = SnowEnergy.Step(ref dry, Hour(45, 5, windMph: 15, cloud: 1000), K);
                h += a.Melt; d += b.Melt;
            }
            Console.WriteLine($"[wind] six overcast face hours at 45 °F, 15 mph: humid melt {h / 25_400.0:0.00} in, dry {d / 25_400.0:0.00} in");
            Assert.That(h, Is.GreaterThan(2 * Math.Max(1, d)), "condensation in a humid wind is the rain-on-snow melt");
        }

        /// <summary>
        /// SN-09 v3 (task 4.07 PR A2, owner 2026-11-14): thin snow is patchy and melts faster the thinner it is, but no depth is a
        /// cliff. v2 took a whole pack under the crumb (0.1 in of water) at the first melt or drop of rain, so two neighbours a few
        /// µm apart came out all or nothing, and the snow line ran along whatever line split them.
        /// </summary>
        [Test]
        public void SN09_AThinPackMeltsFasterButNeverAllOrNothing()
        {
            // A mild hour and a warm, windy one (a face hour melts about 5 mm of water in it): no cliff in either.
            foreach (var hour in new[] { Hour(36, 34, windMph: 8, cloud: 1000), Hour(45, 43, windMph: 12, cloud: 1000) })
            {
                int last = -1, worst = 0;
                for (int start = 500; start <= 8000; start += 100)   // from 0.5 mm (under it the hour's melt takes it all, give or take tens of µm)
                {
                    var p = Pack(start / 25_400.0, 0, 133, 0.11);
                    SnowEnergy.Step(ref p, hour, K);
                    int left = p.Swe;
                    if (last >= 0)
                    {
                        Assert.That(left, Is.GreaterThanOrEqualTo(last), $"{start} µm: more snow never leaves less");
                        worst = Math.Max(worst, left - last);
                    }
                    last = left;
                }
                Console.WriteLine($"[crumb] {hour.Air / 10} °F: what's left moves at most {worst} µm for 100 µm more at the start");
                Assert.That(worst, Is.LessThanOrEqualTo(400), "no jump between neighbouring depths (at most 1 + melt ÷ crumb times)");
            }
            // Still patchy: a thin pack loses more of itself than a thick one in the same hour.
            var thin = Pack(0.03, 0, 250); var thick = Pack(1, 0, 250);
            int t0 = thin.Swe, k0 = thick.Swe;
            SnowEnergy.Step(ref thin, Hour(36, 34, windMph: 8, cloud: 1000), K);
            SnowEnergy.Step(ref thick, Hour(36, 34, windMph: 8, cloud: 1000), K);
            Assert.That(t0 - thin.Swe, Is.GreaterThan(k0 - thick.Swe), "the thin pack melts faster");
        }

        [Test]
        public void SN09_RainAloneDoesNotWipeAColdDusting()
        {
            var p = Pack(0.05, -3, 120);
            var x = Hour(30, 28, cloud: 1000);
            x.Rain = 100;   // a few drops in a mostly-snow hour
            x.Snow = 900;
            SnowEnergy.Step(ref p, x, K);
            Assert.That(p.Swe, Is.GreaterThan(1270), "the dusting keeps its water and the new snow");
        }

        [Test]
        public void SN09_AThinPackStillMeltsOut()
        {
            var p = Pack(0.05, 0, 250);
            int hours = 0;
            while (p.Swe > 0 && hours < 48) { SnowEnergy.Step(ref p, Hour(40, 35, windMph: 6, cloud: 800), K); hours++; }
            Console.WriteLine($"[crumb] 0.05 in of water at 40 °F under cloud melts out in {hours} face hours");
            Assert.That(p.Swe, Is.Zero, "no remnant lingers");
        }

        [Test]
        public void WX63_ADustingOnAClearNightDoesNotSwing()
        {
            var p = Pack(0.2, -2, 100);
            int last = p.SurfaceTemp;
            for (int i = 0; i < 12; i++)
            {
                SnowEnergy.Step(ref p, Hour(10, -5, windMph: 1, cloud: 0), K);
                Assert.That(p.SurfaceTemp, Is.LessThanOrEqualTo(last + 10), $"hour {i}: cooling only (to rounding), no oscillation");
                Assert.That(p.SurfaceTemp, Is.GreaterThan(-4000), "never colder than the clear sky can make it");
                last = p.SurfaceTemp;
            }
        }

        [Test]
        public void WX63_EnergyBalances()
        {
            var p = Pack(12, -5, 250);
            const long ice = 21_000, fusion = 334_000_000;
            long Enthalpy(in SnowPack s)
            {
                int surf = Math.Min(s.Ice, K.SurfaceUm);
                return ice * ((long)surf * s.SurfaceTemp + (long)(s.Ice - surf) * s.DeepTemp / 100) + fusion * s.Liquid;
            }
            long residual = 0, moved = 0;
            for (int i = 0; i < 24 * 20; i++)
            {
                int h = i % 24;
                double air = 20 + 22 * (Math.Cos((h - 15) * Math.PI / 12) + 1) / 2;
                var sun = SunVector.At(43_600_000, -110_800_000, 70 + i / 24, h * 3600 + 1800);
                var n = Normal(20, 200);
                var (beam, diffuse) = SlopeSun.At(sun, sun.AboveHorizon ? 800 : 0, n.X, n.Y, n.Z, 1000, true);
                var x = Hour(air, air - 10, cloud: i / 24 % 3 == 0 ? 900 : 100, beam: beam, diffuse: diffuse);
                if (i % 50 == 7) x.Snow = 2_000;
                long before = Enthalpy(p);
                var o = SnowEnergy.Step(ref p, x, K);
                if (p.Swe <= 0) break;
                long snowHeat = ice * x.Snow * Math.Min(0, SnowEnergy.CentiC(x.WetBulb));
                long expected = before + o.EnergyIn + snowHeat - fusion * o.Runoff;
                residual += Enthalpy(p) - expected;
                moved += Math.Abs(o.EnergyIn);
            }
            Console.WriteLine($"[balance] 20 face days: residual {residual / 1e6:0} J/m² against {moved / 1e6:0} J/m² crossing the surface");
            Assert.That(Math.Abs(residual), Is.LessThan(moved / 100), "the pack's heat balances to within 1 % of what crossed");
        }

        [TestCase(24, 44, 300, 0.0, TestName = "WX63_TheGameClockMeltsLikeTheHour(spring, ripe)")]
        [TestCase(14, 36, 200, -4.0, TestName = "WX63_TheGameClockMeltsLikeTheHour(March, cold pack)")]
        [TestCase(30, 50, 600, 0.0, TestName = "WX63_TheGameClockMeltsLikeTheHour(May, cloudy)")]
        public void WX63_TheGameClockMeltsLikeTheHour(double lowF, double highF, int cloud, double packC)
        {
            // The same day of weather over and over, stepped hourly and in face hours of K real hours: the water lost per real
            // second must agree (the game's clock changes how often the snow is stepped, not how fast it melts).
            double Rate(int seconds, int days)
            {
                var p = Pack(40, packC);
                int start = p.Swe; long t = 0;
                for (int d = 0; d < days; d++)
                    for (int h = 0; h < 24; h++)
                    {
                        double air = lowF + (highF - lowF) * (Math.Cos((h - 15) * Math.PI / 12) + 1) / 2;
                        var sun = SunVector.At(43_600_000, -110_800_000, 100, h * 3600 + 1800);
                        var (beam, diffuse) = SlopeSun.At(sun, sun.AboveHorizon ? 1000 - cloud * 7 / 10 : 0, 0, 0, SunVector.One, 1000, true);
                        var x = Hour(air, air - 8, cloud: cloud, beam: beam, diffuse: diffuse, seconds: seconds);
                        SnowEnergy.Step(ref p, x, K);
                        t += seconds;
                    }
                return (start - p.Swe) / 25_400.0 / (t / 86_400.0);
            }
            double hourly = Rate(3600, 29), game = Rate(FaceHourSeconds, 10);
            Console.WriteLine($"[clock] {lowF}–{highF} °F, cloud {cloud / 10}%: hourly {hourly:0.000} in/day, game clock {game:0.000} in/day (×{game / Math.Max(1e-9, hourly):0.00})");
            Assert.That(game, Is.EqualTo(hourly).Within(0.1 * hourly + 0.005), "within 10 % of the hourly rate");
        }

        [Test]
        public void SN15_ThePhysicsKeepsTheOwnersPowderPatternByAspect()
        {
            // SN-15 (4.07 A1 fit round): the owner's powder-by-aspect table (the warmest daytime air at which powder stays
            // powder) stays the game's powder rule. The surface physics can't be its pass mark: the skin's energy balance
            // keeps powder dry about 10 °F warmer, because sun crust forms below the skin and the pillows rule out the
            // stronger mixing that would close the gap on north faces. What the physics must share is the pattern: on a
            // clear February day at 43.6° N on a 30° slope (fresh snow on a cold pack, the air rising to its high at 3 pm
            // from 18 °F below), the warmest powder day falls from north to south down both sides of the compass, and the
            // north-to-south spread is within 3 °F of the table's. A day keeps its powder while no face hour wets the skin.
            var table = new (string Aspect, double Deg, double F)[]
            {
                ("N", 0, 42), ("NE", 45, 40), ("E", 90, 36), ("SE", 135, 30), ("S", 180, 28), ("SW", 225, 32), ("W", 270, 34), ("NW", 315, 38),
            };
            bool Melts(double aspect, double highF, int moistCentiC = 0)
            {
                var p = Pack(10, -6, 120);
                p.Albedo = K.AlbedoFresh * 1000;
                var n = Normal(30, aspect);
                for (int h = 6; h < 19; h++)
                {
                    double air = highF - 18 + 18 * (Math.Cos((h - 15) * Math.PI / 12) + 1) / 2;
                    var sun = SunVector.At(43_600_000, -110_800_000, 32, h * 3600 + 1800);
                    var (beam, diffuse) = SlopeSun.At(sun, sun.AboveHorizon ? 850 : 0, n.X, n.Y, n.Z, 1000, true);
                    var o = SnowEnergy.Step(ref p, Hour(air, air - 15, cloud: 0, beam: beam, diffuse: diffuse), K);
                    if (o.Melt > 0 || o.Skin >= moistCentiC) return true;
                }
                return false;
            }
            var misses = new System.Collections.Generic.List<string>();
            var found = new System.Collections.Generic.Dictionary<string, double>();
            var line = new System.Text.StringBuilder("[powder] warmest day that keeps powder (table → physics):");
            foreach (var (aspect, deg, f) in table)
            {
                double t = 10;
                while (t < 60 && !Melts(deg, t + 0.5)) t += 0.5;
                double t1 = 10, t2 = 10;
                while (t1 < 60 && !Melts(deg, t1 + 0.5, -100)) t1 += 0.5;
                while (t2 < 60 && !Melts(deg, t2 + 0.5, -200)) t2 += 0.5;
                line.Append($" {aspect} {f:0}→{t:0.0} (−1 °C {t1:0.0}, −2 °C {t2:0.0})");
                found[aspect] = t;
            }
            Console.WriteLine(line);
            string[] east = { "N", "NE", "E", "SE", "S" }, west = { "N", "NW", "W", "SW", "S" };
            foreach (var side in new[] { east, west })
                for (int i = 1; i < side.Length; i++)
                    if (found[side[i]] > found[side[i - 1]]) misses.Add($"{side[i]} {found[side[i]]:0.0} °F is warmer than {side[i - 1]} {found[side[i - 1]]:0.0} °F");
            double spread = found["N"] - found["S"], tableSpread = 42 - 28;
            Console.WriteLine($"[powder] north-to-south spread {spread:0.0} °F (table {tableSpread:0}); physics minus table, mean {table.Average(r => found[r.Aspect] - r.F):+0.0} °F");
            if (Math.Abs(spread - tableSpread) > 3) misses.Add($"north-to-south spread {spread:0.0} °F against the table's {tableSpread:0}");
            Assert.That(misses, Is.Empty, "the owner's powder pattern by aspect");
        }

        [Test]
        public void TheSurfaceTablesAreTheFormulas()
        {
            // Task 4.07 PR A2: the step reads its emission and vapour terms from tables built by the same formulas; every entry,
            // and the fallback past both ends, gives the same number.
            for (int c = -8100; c <= 300; c++)
                Assert.That(SnowEnergy.TablesMatch(c), Is.True, $"{c / 100.0:F2} °C");
        }

        [Test]
        public void BG02_TheStepAllocatesNothing()
        {
            var p = Pack(20, -3);
            var x = Hour(28, 20, beam: 300, diffuse: 80);
            SnowEnergy.Step(ref p, x, K);
            long before;
            try { before = GC.GetAllocatedBytesForCurrentThread(); }
            catch (PlatformNotSupportedException) { Assert.Ignore("needs CoreCLR"); return; }
            for (int i = 0; i < 100_000; i++) { var q = p; SnowEnergy.Step(ref q, x, K); }
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        }
    }
}
