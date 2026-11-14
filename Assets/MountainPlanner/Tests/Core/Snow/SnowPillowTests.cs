using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Sky;
using MountainPlanner.Domain.Weather;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A1: the snow energy (SN-09 v2, WX-63) against the measured pack at the six western SNOTEL pillows.
    /// Each winter (October 1 to July 31) is played three ways at the pillow's height, flat, in a forest clearing (open sky
    /// <see cref="ClearingSky"/>, wind × <see cref="ClearingWind"/>, the same for every pillow: no site tuning):
    /// - **gauge**: the record's hours carried to the pillow by the site's rows, the pillow's own gauge water split over
    ///   the day's wet hours, its phase by the game's wet bulb; stepped every real hour;
    /// - **game clock**: the same, but stepped as the game steps it (WX-21): a face hour lasts K = 121 ÷ 42 real hours,
    ///   takes the record's weather at its own hour of the day, and carries the water of its K hours;
    /// - **melt only**: from the measured peak, adding only the snow the pillow itself gains after it, so the melt is
    ///   tested apart from the gauge's catch.
    /// 4.05's reference model (<see cref="WeatherFidelityTests.ReferenceSnow"/>) runs beside the gauge play.
    /// The pass marks (on each pillow's median winter) are the plan's: melt-out within 10 days and the spring melt rate
    /// within 25 % (melt only); January 1 to April 1 water within 15 % at the two midwinter-loss pillows (Stevens Pass,
    /// Tahoe City); the daily error no worse than the reference model's; and the game clock within 10 % of the hourly
    /// play on April 1. Explicit: writes <c>test-results/snow-pillows/&lt;tag&gt;.json</c> (<c>MP_SNOW_TAG</c>, default
    /// "run") for <c>tools/snow-pillows/report.mjs</c>.
    /// </summary>
    [Explicit, Category("SnowReport")]
    public sealed class SnowPillowTests
    {
        public const int ClearingSky = 800, ClearingWind = 600;   // ‰
        /// <summary>The clearing's skyline, as the sine of its angle: open sky = cos² of the skyline, so 0.8 is about 27° (WX-59).</summary>
        static readonly int ClearingSkyline = (int)Math.Round(Math.Sqrt(1 - ClearingSky / 1000.0) * SunVector.One);
        const int SeasonDays = 304;                               // October 1 to July 31
        const double MeltOutIn = 0.2;

        sealed class Winter
        {
            public int Year;
            public double[] Measured = new double[SeasonDays], Gauge = new double[SeasonDays], Game = new double[SeasonDays],
                            Reference = new double[SeasonDays], MeltOnly = new double[SeasonDays];
            public int PeakDay = -1;
            /// <summary>Each day's gauge water (in) and its water-weighted wet bulb at the pillow (°F; NaN on a dry day).</summary>
            public double[] DayInc = new double[SeasonDays], DayWb = new double[SeasonDays];
            /// <summary>The gauge play's ledger by month from October (in of water): snow, rain, runoff, sublimation.</summary>
            public double[] Snow = new double[10], Rain = new double[10], Runoff = new double[10], Sublimated = new double[10];
            /// <summary>Mean fluxes into the snow by month while there is snow (W/m²): sun, longwave, sensible, latent; and the hours.</summary>
            public double[] Sw = new double[10], Lw = new double[10], Sh = new double[10], Lh = new double[10], SnowHours = new double[10];
            /// <summary>Mean air at the pillow by month (°F), and the hours it averages.</summary>
            public double[] AirF = new double[10], AirHours = new double[10], CloudM = new double[10], ClearM = new double[10], ClearHours = new double[10], SurfC = new double[10];
        }

        sealed class Pillow
        {
            public string Site = "", Station = "", Name = "";
            public double Feet;
            public readonly List<Winter> Winters = new List<Winter>();
        }

        [Test]
        public void WriteThePillowCheck()
        {
            var pillows = new List<Pillow>();
            string only = Environment.GetEnvironmentVariable("MP_SNOW_SITE");   // one site while fitting; the report plays all
            foreach (string site in WeatherFixtures.Sites)
            {
                if (!string.IsNullOrEmpty(only) && site != only) continue;
                var (record, _, b) = WeatherFixtures.Screened(site);
                if (record.Snotel.Count == 0) continue;
                var rows = WeatherFixtures.Calibrated(site).Rows;
                var states = WeatherStates.Find(b);
                foreach (var s in record.Snotel)
                    pillows.Add(Measure(site, s, b, rows, states));
            }

            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"clearingSky\":" + ClearingSky + ",\"clearingWind\":" + ClearingWind + ",\"pillows\":[\n");
            var failures = new List<string>();
            for (int i = 0; i < pillows.Count; i++)
            {
                var p = pillows[i];
                var g = Gates(p);
                string line = $"[snow] {p.Site} {p.Station} {p.Name} at {p.Feet:0} ft, {p.Winters.Count} winters: " +
                              $"melt-out {g.MeltOutErr:+0;-0;0} d (melt only), spring rate ×{g.RateRatio:0.00}, " +
                              $"Jan 1 ×{g.Jan1:0.00}, Apr 1 ×{g.Apr1:0.00} (gauge; reference ×{g.RefApr1:0.00}), " +
                              $"daily error {g.Mae:0.00} in vs reference {g.RefMae:0.00}, game clock Apr 1 ×{g.GameVsHour:0.00}";
                Console.WriteLine(line);
                Phase(p);
                if (Math.Abs(g.MeltOutErr) > 10) failures.Add($"{p.Station} melt-out {g.MeltOutErr:+0;-0} d");
                if (Math.Abs(g.RateRatio - 1) > 0.25) failures.Add($"{p.Station} spring rate ×{g.RateRatio:0.00}");
                if (p.Station.StartsWith("791:") || p.Station.StartsWith("809:"))
                {
                    if (Math.Abs(g.Jan1 - 1) > 0.15) failures.Add($"{p.Station} Jan 1 ×{g.Jan1:0.00}");
                    if (Math.Abs(g.Apr1 - 1) > 0.15) failures.Add($"{p.Station} Apr 1 ×{g.Apr1:0.00}");
                }
                if (g.Mae > g.RefMae) failures.Add($"{p.Station} daily error {g.Mae:0.00} > reference {g.RefMae:0.00}");
                if (Math.Abs(g.GameVsHour - 1) > 0.10) failures.Add($"{p.Station} game clock ×{g.GameVsHour:0.00}");

                sb.Append(string.Format(inv, "{{\"site\":\"{0}\",\"station\":\"{1}\",\"name\":\"{2}\",\"feet\":{3:0},\"gates\":{{\"meltOutErr\":{4:0.0},\"rateRatio\":{5:0.000},\"jan1\":{6:0.000},\"apr1\":{7:0.000},\"refApr1\":{8:0.000},\"mae\":{9:0.000},\"refMae\":{10:0.000},\"gameVsHour\":{11:0.000},\"refMeltOutErr\":{12:0.0}}},\"winters\":[",
                    p.Site, p.Station, p.Name.Replace("\"", "'"), p.Feet, g.MeltOutErr, g.RateRatio, g.Jan1, g.Apr1, g.RefApr1, g.Mae, g.RefMae, g.GameVsHour, g.RefMeltOutErr));
                for (int w = 0; w < p.Winters.Count; w++)
                {
                    var x = p.Winters[w];
                    sb.Append(w == 0 ? "\n" : ",\n").Append(string.Format(inv, "{{\"year\":{0},\"peakDay\":{1},", x.Year, x.PeakDay));
                    Series(sb, "measured", x.Measured); sb.Append(','); Series(sb, "gauge", x.Gauge); sb.Append(',');
                    Series(sb, "game", x.Game); sb.Append(','); Series(sb, "reference", x.Reference); sb.Append(',');
                    Series(sb, "meltOnly", x.MeltOnly); sb.Append(',');
                    Series(sb, "ledgerSnow", x.Snow); sb.Append(','); Series(sb, "ledgerRain", x.Rain); sb.Append(',');
                    Series(sb, "ledgerRunoff", x.Runoff); sb.Append(','); Series(sb, "ledgerSublimated", x.Sublimated); sb.Append(',');
                    for (int m = 0; m < 10; m++)
                    {
                        double n = Math.Max(1, x.SnowHours[m]);
                        x.Sw[m] /= n; x.Lw[m] /= n; x.Sh[m] /= n; x.Lh[m] /= n;
                        x.AirF[m] /= Math.Max(1, x.AirHours[m]); x.CloudM[m] /= Math.Max(1, x.AirHours[m]);
                        x.ClearM[m] /= Math.Max(1, x.ClearHours[m]); x.SurfC[m] /= n;
                    }
                    Series(sb, "fluxSun", x.Sw); sb.Append(','); Series(sb, "fluxLongwave", x.Lw); sb.Append(',');
                    Series(sb, "fluxSensible", x.Sh); sb.Append(','); Series(sb, "fluxLatent", x.Lh); sb.Append(',');
                    Series(sb, "airF", x.AirF); sb.Append(','); Series(sb, "cloud", x.CloudM); sb.Append(',');
                    Series(sb, "clearness", x.ClearM); sb.Append(','); Series(sb, "surfaceC", x.SurfC); sb.Append('}');
                }
                sb.Append("]}").Append(i + 1 < pillows.Count ? ",\n" : "\n");
            }
            sb.Append(string.Format(inv, "],\"failures\":[{0}]}}\n", string.Join(",", failures.Select(f => "\"" + f + "\""))));
            string tag = Environment.GetEnvironmentVariable("MP_SNOW_TAG");
            if (string.IsNullOrEmpty(tag)) tag = "run";
            string root = Directory.GetParent(Directory.GetParent(TestData.Folder("weather")).FullName).FullName;
            string dir = Path.Combine(root, "test-results", "snow-pillows");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, tag + ".json"), sb.ToString());
            Console.WriteLine($"[snow] wrote {Path.Combine(dir, tag + ".json")}; {failures.Count} gate(s) missed: {string.Join("; ", failures)}");
            if (string.IsNullOrEmpty(only)) Assert.That(pillows.Count, Is.EqualTo(6), "six western pillows");
            Assert.That(failures, Is.Empty, "the pass marks (see the report)");
        }

        /// <summary>
        /// The rain/snow check: on every day with gauge water onto a pack (both days measured, 1 in or more the day before),
        /// the model's gain against the pillow's, by the day's wet bulb. Where the model calls rain and the pillow gains
        /// like snow, the line is too cold. Then the January 1 split: what the gauge caught to January 1 against the pillow.
        /// </summary>
        static void Phase(Pillow p)
        {
            double[] edges = { 28, 30, 31, 32, 33, 34, 35, 36, 38, 99 };
            var inc = new double[edges.Length]; var model = new double[edges.Length]; var pillow = new double[edges.Length];
            var snowIn = new double[edges.Length]; var days = new int[edges.Length];
            double caught = 0, jan = 0, modelJan = 0; int nj = 0;
            foreach (var w in p.Winters)
            {
                for (int d = 1; d < SeasonDays; d++)
                {
                    if (w.DayInc[d] < 0.2 || double.IsNaN(w.DayWb[d])) continue;
                    if (double.IsNaN(w.Measured[d]) || double.IsNaN(w.Measured[d - 1]) || w.Measured[d - 1] < 1) continue;
                    int bin = 0; while (bin < edges.Length - 1 && w.DayWb[d] >= edges[bin]) bin++;
                    double wb = w.DayWb[d], share = WeatherAir.SnowPermille((int)Math.Round(wb * 10)) / 1000.0;
                    inc[bin] += w.DayInc[d]; snowIn[bin] += w.DayInc[d] * share; days[bin]++;
                    model[bin] += w.Gauge[d] - w.Gauge[d - 1]; pillow[bin] += w.Measured[d] - w.Measured[d - 1];
                }
                if (!double.IsNaN(w.Measured[92]))
                {
                    double c = 0; for (int d = 0; d <= 92; d++) c += w.DayInc[d];
                    caught += c; jan += w.Measured[92]; modelJan += w.Gauge[92]; nj++;
                }
            }
            var line = new StringBuilder($"[phase] {p.Station}: wet bulb bin (days): gauge in → model snow in | model gain vs pillow gain:");
            for (int i = 0; i < edges.Length; i++)
            {
                if (days[i] == 0) continue;
                string lo = i == 0 ? "<" + edges[0] : edges[i - 1] + "–" + (i == edges.Length - 1 ? "" : edges[i].ToString(CultureInfo.InvariantCulture));
                line.Append($"  {lo} °F ({days[i]}): {inc[i]:0.0} → {snowIn[i]:0.0} | {model[i]:0.0} vs {pillow[i]:0.0}");
            }
            Console.WriteLine(line);
            if (nj > 0) Console.WriteLine($"[phase] {p.Station}: to Jan 1, per winter: gauge caught {caught / nj:0.0} in, model pack {modelJan / nj:0.0} in, pillow {jan / nj:0.0} in");
        }

        static void Series(StringBuilder sb, string name, double[] v)
        {
            sb.Append('"').Append(name).Append("\":[");
            for (int i = 0; i < v.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(double.IsNaN(v[i]) ? "null" : v[i].ToString("0.00", CultureInfo.InvariantCulture));
            }
            sb.Append(']');
        }

        // ---------------- The gates over a pillow's winters (medians) ----------------

        struct GateValues { public double MeltOutErr, RefMeltOutErr, RateRatio, Jan1, Apr1, RefApr1, Mae, RefMae, GameVsHour; }

        static double Median(List<double> v) { if (v.Count == 0) return double.NaN; v.Sort(); int n = v.Count; return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2; }

        static int MeltOut(double[] s, int from)
        {
            for (int d = Math.Max(0, from); d < s.Length; d++) if (!double.IsNaN(s[d]) && s[d] <= MeltOutIn) return d;
            return -1;
        }

        /// <summary>Days from the peak to 75 % and to 25 % of it: the spring melt rate is half the peak over that span.</summary>
        static double SpringRate(double[] s, int peakDay, double peak)
        {
            int a = -1, b = -1;
            for (int d = peakDay; d < s.Length; d++)
            {
                if (double.IsNaN(s[d])) continue;
                if (a < 0 && s[d] <= 0.75 * peak) a = d;
                if (s[d] <= 0.25 * peak) { b = d; break; }
            }
            return a < 0 || b <= a ? double.NaN : 0.5 * peak / (b - a);
        }

        static GateValues Gates(Pillow p)
        {
            var meltOut = new List<double>(); var refMeltOut = new List<double>(); var rate = new List<double>();
            var jan = new List<double>(); var apr = new List<double>(); var refApr = new List<double>();
            var mae = new List<double>(); var refMae = new List<double>(); var game = new List<double>();
            const int Jan1 = 92, Apr1 = 182;
            foreach (var w in p.Winters)
            {
                if (w.PeakDay < 0) continue;
                double peak = w.Measured[w.PeakDay];
                int mo = MeltOut(w.Measured, w.PeakDay), mm = MeltOut(w.MeltOnly, w.PeakDay), mr = MeltOut(w.Reference, w.PeakDay);
                if (mo > 0 && mm > 0) meltOut.Add(mm - mo);
                if (mo > 0 && mr > 0) refMeltOut.Add(mr - mo);
                double r0 = SpringRate(w.Measured, w.PeakDay, peak), r1 = SpringRate(w.MeltOnly, w.PeakDay, peak);
                if (r0 > 0 && r1 > 0) rate.Add(r1 / r0);
                if (w.Measured[Jan1] > 1) jan.Add(w.Gauge[Jan1] / w.Measured[Jan1]);
                if (w.Measured[Apr1] > 1) { apr.Add(w.Gauge[Apr1] / w.Measured[Apr1]); refApr.Add(w.Reference[Apr1] / w.Measured[Apr1]); }
                if (w.Gauge[Apr1] > 1) game.Add(w.Game[Apr1] / w.Gauge[Apr1]);
                double e = 0, er = 0; int n = 0;
                for (int d = 0; d < SeasonDays; d++)
                {
                    if (double.IsNaN(w.Measured[d])) continue;
                    e += Math.Abs(w.Gauge[d] - w.Measured[d]); er += Math.Abs(w.Reference[d] - w.Measured[d]); n++;
                }
                if (n > 0) { mae.Add(e / n); refMae.Add(er / n); }
            }
            return new GateValues
            {
                MeltOutErr = Median(meltOut), RefMeltOutErr = Median(refMeltOut), RateRatio = Median(rate), Jan1 = Median(jan), Apr1 = Median(apr),
                RefApr1 = Median(refApr), Mae = Median(mae), RefMae = Median(refMae), GameVsHour = Median(game),
            };
        }

        // ---------------- One pillow ----------------

        /// <summary>The hour's weather at the pillow (the record carried by the rows, as <see cref="WeatherPillowTests"/> carries it).</summary>
        struct HourAt { public double TempF, DewF; public int Wind, Cloud, Clearness; public bool Ok; }

        static HourAt At(BlendedRecord b, RecordStates states, WeatherRows rows, int k, int dz)
        {
            if (k < 0 || k >= b.HourCount || float.IsNaN(b.T[k])) return default;
            int t = states.HourThird(k);
            bool isNight = states.State[k] != WeatherRows.Storm && (k % 24 < 7 || k % 24 >= 17);
            int lapse = isNight ? rows.LapseNight[t] : rows.LapseDay[t];
            double tempF = Psychrometrics.CToF(b.T[k]) - (double)lapse * dz / 10_000;
            double dewF = Math.Min(tempF, Psychrometrics.CToF(float.IsNaN(b.Td[k]) ? b.T[k] : b.Td[k]) - (double)rows.DewLapse[t] * dz / 10_000);
            double wind = float.IsNaN(b.Wind[k]) ? 2 : b.Wind[k];
            return new HourAt
            {
                TempF = tempF, DewF = dewF, Ok = true,
                Wind = (int)Math.Round(wind * 22.369 * ClearingWind / 1000),
                Cloud = float.IsNaN(b.Cloud[k]) ? 500 : (int)Math.Round(Math.Max(0, Math.Min(100, b.Cloud[k])) * 10),   // the record is in percent
                Clearness = float.IsNaN(b.Clearness[k]) ? 0 : (int)Math.Round(Math.Max(0, Math.Min(1, b.Clearness[k])) * 1000),
            };
        }

        static SnowInput Input(in HourAt h, int feet, int latMicro, int lonMicro, int dayOfYear, int hourOfDay, double liquidIn, int seconds)
        {
            int air = (int)Math.Round(h.TempF * 10), dew = (int)Math.Round(h.DewF * 10);
            int wb = WeatherAir.WetBulb(air, dew, feet);
            int water = (int)Math.Round(liquidIn * 25_400);
            int snowShare = water > 0 ? WeatherAir.SnowPermille(wb) : 0;
            int snow = water * snowShare / 1000;
            var sun = SunVector.At(latMicro, lonMicro, dayOfYear, hourOfDay * 3600 + 1800);
            var (beam, diffuse) = SlopeSun.At(sun, sun.AboveHorizon ? h.Clearness : 0, 0, 0, SunVector.One, ClearingSky, sun.Up > ClearingSkyline);
            return new SnowInput
            {
                Seconds = seconds, Air = air, Dew = dew, WetBulb = wb, Feet = feet, Wind = h.Wind, Cloud = h.Cloud,
                Beam = beam, Diffuse = diffuse, OpenSky = ClearingSky, Canopy = 0,
                Snow = snow, SnowDensity = 10_000 / WeatherAir.SnowRatioTenths(wb), Rain = water - snow,
            };
        }

        static Pillow Measure(string site, SnotelSeries s, BlendedRecord b, WeatherRows rows, RecordStates states)
        {
            int feet = (int)Math.Round(s.Station.ElevationFeet);
            int dz = feet - rows.ZrefFeet;
            int lat = (int)Math.Round(s.Station.Latitude * 1e6), lon = (int)Math.Round(s.Station.Longitude * 1e6);
            var p = new Pillow { Site = site, Station = s.Station.Triplet, Name = s.Station.Name, Feet = s.Station.ElevationFeet };
            var k = SnowTuning.Default;
            const int Hour = 3600;
            const int FaceSeconds = 121 * 86_400 / 42 / 24;

            for (int d0 = 0; d0 < states.DayOfLabelYear.Length; d0++)
            {
                if (states.DayOfLabelYear[d0] != 0) continue;
                if ((d0 + SeasonDays) * 24 > b.HourCount) continue;
                var w = new Winter { Year = CivilDay.Date(b.Days[d0]).Year + 1 };

                // The pillow's gauge water, split over each day's wet hours (evenly on a day the record had dry).
                var liquid = new double[SeasonDays * 24];
                bool bad = false; int missingDays = 0;
                for (int d = 0; d < SeasonDays; d++)
                {
                    int civil = b.Days[d0 + d];
                    w.Measured[d] = s.Days.TryGetValue(civil, out var today) && !double.IsNaN(today.Wteq) ? Math.Max(0, today.Wteq) : double.NaN;
                    if (double.IsNaN(w.Measured[d])) missingDays++;
                    double inc = 0;
                    if (s.Days.TryGetValue(civil, out var t1) && !double.IsNaN(t1.Prec))
                    {
                        if (d == 0) inc = Math.Max(0, t1.Prec);
                        else if (s.Days.TryGetValue(civil - 1, out var t0) && !double.IsNaN(t0.Prec)) inc = Math.Max(0, t1.Prec - t0.Prec);
                    }
                    double dayP = 0;
                    for (int h = 0; h < 24; h++) { int kk = (d0 + d) * 24 + h; if (float.IsNaN(b.T[kk])) bad = true; dayP += float.IsNaN(b.P[kk]) ? 0 : b.P[kk]; }
                    for (int h = 0; h < 24; h++)
                    {
                        int kk = (d0 + d) * 24 + h;
                        double share = dayP > 0 ? (float.IsNaN(b.P[kk]) ? 0 : b.P[kk]) / dayP : 1.0 / 24;
                        liquid[d * 24 + h] = inc * share;
                    }
                }
                if (bad || missingDays > 30) continue;
                double peak = 0;
                for (int d = 0; d < SeasonDays; d++) if (!double.IsNaN(w.Measured[d]) && w.Measured[d] > peak) { peak = w.Measured[d]; w.PeakDay = d; }
                if (peak < 2) continue;

                // Gauge, hourly; the reference model beside it.
                var pack = new SnowPack();
                var reference = new WeatherFidelityTests.ReferenceSnow();
                for (int d = 0; d < SeasonDays; d++)
                {
                    var date = CivilDay.Date(b.Days[d0 + d]);
                    double wbW = 0, wbL = 0;
                    for (int h = 0; h < 24; h++)
                    {
                        int kk = (d0 + d) * 24 + h;
                        var at = At(b, states, rows, kk, dz);
                        var x = Input(at, feet, lat, lon, date.DayOfYear, h, liquid[d * 24 + h], Hour);
                        if (liquid[d * 24 + h] > 0) { wbW += x.WetBulb / 10.0 * liquid[d * 24 + h]; wbL += liquid[d * 24 + h]; }
                        var o = SnowEnergy.Step(ref pack, x, k);
                        int month = Math.Min(9, (date.Month + 2) % 12);
                        w.Snow[month] += x.Snow / 25_400.0; w.Rain[month] += x.Rain / 25_400.0;
                        w.Runoff[month] += o.Runoff / 25_400.0; w.Sublimated[month] += o.Sublimated / 25_400.0;
                        w.AirF[month] += at.TempF; w.AirHours[month]++; w.CloudM[month] += at.Cloud / 10.0;
                        if (x.Beam + x.Diffuse > 0) { w.ClearM[month] += at.Clearness / 10.0; w.ClearHours[month]++; }
                        if (pack.Swe > 0) w.SurfC[month] += pack.SurfaceTemp / 100.0;
                        if (pack.Swe > 0)
                        {
                            w.Sw[month] += o.Shortwave / 1000.0; w.Lw[month] += o.Longwave / 1000.0;
                            w.Sh[month] += o.Sensible / 1000.0; w.Lh[month] += o.Latent / 1000.0; w.SnowHours[month]++;
                        }
                        reference.Step(x.Snow / 25_400.0, at.TempF, date.DayOfYear, 1);
                    }
                    w.Gauge[d] = pack.Swe / 25_400.0;
                    w.DayInc[d] = wbL; w.DayWb[d] = wbL > 0 ? wbW / wbL : double.NaN;
                    w.Reference[d] = reference.Pack;
                }

                // The game's clock: a face hour of K real hours at its own hour of the day.
                pack = new SnowPack();
                long realHours = (long)SeasonDays * 24;
                for (long j = 0; ; j++)
                {
                    long h0 = j * 121 / 42, h1 = (j + 1) * 121 / 42;
                    if (h1 > realHours) break;
                    int hourOfDay = (int)(j % 24);
                    // The water of the K real hours, each split into snow and rain by its own hour's wet bulb (the game's weather
                    // makes its storms at its own temperatures; splitting by the sampled hour would turn a storm into a sunny
                    // day's rain), with the new snow's depth summed so its density is the mix's.
                    long snowUm = 0, rainUm = 0, snowDepth = 0;
                    for (long r = h0; r < h1; r++)
                    {
                        if (liquid[r] <= 0) continue;
                        var xr = Input(At(b, states, rows, (int)(d0 * 24L + r), dz), feet, lat, lon, 1, (int)(r % 24), liquid[r], Hour);
                        snowUm += xr.Snow; rainUm += xr.Rain;
                        snowDepth += (long)xr.Snow * 1000 / Math.Max(30, xr.SnowDensity);
                    }
                    // The face hour's weather: the record at this hour of the day, on the real day the face hour falls in (a face
                    // day's hours walk through the K real days it stands for, so every real day is sampled, none averaged).
                    int realDay = (int)(h0 / 24);
                    var at = At(b, states, rows, (d0 + realDay) * 24 + hourOfDay, dz);
                    var date = CivilDay.Date(b.Days[d0 + realDay]);
                    var xg = Input(at, feet, lat, lon, date.DayOfYear, hourOfDay, 0, FaceSeconds);
                    xg.Snow = (int)snowUm; xg.Rain = (int)rainUm;
                    if (snowUm > 0) xg.SnowDensity = (int)(snowUm * 1000 / Math.Max(1, snowDepth));
                    SnowEnergy.Step(ref pack, xg, k);
                    int endDay = (int)(h1 / 24);
                    for (int d = (int)(h0 / 24); d <= Math.Min(SeasonDays - 1, endDay); d++) w.Game[d] = pack.Swe / 25_400.0;
                }

                // Melt only: from the measured peak, with the snow the pillow gains after it.
                pack = new SnowPack
                {
                    Swe = (int)Math.Round(peak * 25_400), SurfaceTemp = -100, DeepTemp = -10_000, Albedo = 750_000, Density = 380_000,
                };
                for (int d = 0; d < SeasonDays; d++) w.MeltOnly[d] = d <= w.PeakDay ? w.Measured[d] : double.NaN;
                for (int d = w.PeakDay + 1; d < SeasonDays; d++)
                {
                    var date = CivilDay.Date(b.Days[d0 + d]);
                    double gain = 0;
                    if (!double.IsNaN(w.Measured[d]) && !double.IsNaN(w.Measured[d - 1])) gain = Math.Max(0, w.Measured[d] - w.Measured[d - 1] - 0.1);
                    for (int h = 0; h < 24; h++)
                    {
                        int kk = (d0 + d) * 24 + h;
                        var at = At(b, states, rows, kk, dz);
                        var x = Input(at, feet, lat, lon, date.DayOfYear, h, 0, Hour);
                        if (gain > 0 && h == 6)
                        {
                            x.Snow = (int)Math.Round(gain * 25_400);
                            x.SnowDensity = 120;
                        }
                        SnowEnergy.Step(ref pack, x, k);
                    }
                    w.MeltOnly[d] = pack.Swe / 25_400.0;
                }
                p.Winters.Add(w);
            }
            return p;
        }
    }
}
