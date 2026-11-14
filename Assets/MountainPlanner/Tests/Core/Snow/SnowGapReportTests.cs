using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Task 4.07 PR A1, the fit round's gap report: why a site's generated weather keeps less snow than its record when
    /// <see cref="SnowEnergy"/> judges both (WX-63's swap). Each site's record is played at the game's clock (a face hour of
    /// K real hours takes the record at its own hour of the day and carries the water of its K hours, as the pillow check's
    /// game play) beside the frozen rows' seasons, both at zref on open flat ground, so the clock is the same on both sides
    /// and only the weather differs. Prints, by month, the snow's ledger (water in, rain, runoff, sublimation), its energy by
    /// term and the mean pack; then the weather's shape, which no 4.05 gate measures: the daily swing, hours above freezing,
    /// clear against cloudy nights, clear days and how long they run, the sun in warm hours. A report, not a gate.
    /// </summary>
    [Explicit, Category("SnowReport")]
    public sealed class SnowGapReportTests
    {
        const int Months = 10, Seeds = 60, OpenWind = 850;   // October to July; ‰ of the zref wind, as the fidelity test
        static readonly string[] MonthNames = { "Oct", "Nov", "Dec", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul" };

        sealed class Side
        {
            public double Winters;
            public readonly double[] Snow = new double[Months], Rain = new double[Months], Runoff = new double[Months], Sub = new double[Months];
            public readonly double[] Sw = new double[Months], Lw = new double[Months], Sh = new double[Months], Lh = new double[Months], SnowSec = new double[Months];
            public readonly double[] Pack = new double[Months], PackN = new double[Months];
            public readonly double[] Swing = new double[Months], SwingN = new double[Months], Above = new double[Months], AboveN = new double[Months];
            public readonly double[] ClearNight = new double[Months], ClearNightN = new double[Months], CloudyNight = new double[Months], CloudyNightN = new double[Months];
            public readonly double[] ClearDay = new double[Months], DayN = new double[Months], Runs = new double[Months], RunN = new double[Months];
            public readonly double[] WarmSun = new double[Months], WarmSunN = new double[Months], Temp = new double[Months], TempN = new double[Months];
            /// <summary>By month and hour of the day: air (°F), dew depression (°F), wind (0.1 mph), and the count.</summary>
            public readonly double[,] HourT = new double[Months, 24], HourDep = new double[Months, 24], HourWind = new double[Months, 24], HourN = new double[Months, 24];
            public void Hour(int m, int h, double t, double d, double wind) { HourT[m, h] += t; HourDep[m, h] += t - Math.Min(t, d); HourWind[m, h] += wind; HourN[m, h]++; }
        }

        /// <summary>The sky within a weather state, by month, state and night/day: cloud (‰) and its spread, clearness (‰),
        /// how the temperature leans with the cloud (°F per 1000 ‰), and the cloud's hour-to-hour memory.</summary>
        sealed class Sky
        {
            public readonly double[,,] N = new double[Months, 4, 2], C = new double[Months, 4, 2], C2 = new double[Months, 4, 2],
                                       T = new double[Months, 4, 2], TC = new double[Months, 4, 2], L = new double[Months, 4, 2], L2 = new double[Months, 4, 2];
            public double LagN, LagA, LagB, LagAB, LagA2, LagB2;
            public void Add(int m, int st, int night, double cloud, double t, double clear)
            {
                N[m, st, night]++; C[m, st, night] += cloud; C2[m, st, night] += cloud * cloud; T[m, st, night] += t; TC[m, st, night] += t * cloud;
                L[m, st, night] += clear; L2[m, st, night] += clear * clear;
            }
            public double Mean(int m, int st, int n) => N[m, st, n] > 0 ? C[m, st, n] / N[m, st, n] : 0;
            public double Sd(int m, int st, int n) => N[m, st, n] > 1 ? Math.Sqrt(Math.Max(0, C2[m, st, n] / N[m, st, n] - Mean(m, st, n) * Mean(m, st, n))) : 0;
            public double ClearSd(int m, int st)
            {
                double n = N[m, st, 0]; if (n < 2) return 0; double mu = L[m, st, 0] / n;
                return Math.Sqrt(Math.Max(0, L2[m, st, 0] / n - mu * mu));
            }
            /// <summary>°F per 1000 ‰ of cloud, within the cell.</summary>
            public double Slope(int m, int st, int n)
            {
                double c = N[m, st, n]; if (c < 30) return 0;
                double mc = C[m, st, n] / c, mt = T[m, st, n] / c, v = C2[m, st, n] / c - mc * mc;
                return v < 1 ? 0 : (TC[m, st, n] / c - mc * mt) / v * 1000;
            }
            public double Lag1 => LagN < 2 ? 0 : (LagAB / LagN - LagA / LagN * (LagB / LagN)) /
                Math.Sqrt(Math.Max(1e-9, (LagA2 / LagN - Math.Pow(LagA / LagN, 2)) * (LagB2 / LagN - Math.Pow(LagB / LagN, 2))));
        }

        [Test]
        public void WriteTheGapReport()
        {
            double k = (double)WeatherTuning.Default.CalendarDays / WeatherTuning.Default.FaceDays;
            int faceSeconds = (int)Math.Round(k * 3600);
            foreach (string site in WeatherFixtures.Sites)
            {
                var (rec, _, b) = WeatherFixtures.Screened(site);
                var rows = SimWeatherTests.Frozen(site);
                int lat = (int)Math.Round(rec.Site.Latitude * 1e6), lon = (int)Math.Round(rec.Site.Longitude * 1e6), feet = rows.ZrefFeet;
                var sky = new Sky();
                var record = Record(b, rows, lat, lon, feet, k, faceSeconds, sky);
                var gsky = new Sky();
                var game = Game(rows, lat, lon, feet, k, faceSeconds, gsky);
                Print(site, record, game);
                PrintHours(site, record, game);
                PrintSky(site, sky, gsky);
            }
        }

        static void Step(Side s, int month, ref SnowPack p, double snowIn, double rainIn, double tF, double dF, int wind, int cloud, int clear,
                         int lat, int lon, int doy, int hod, int seconds, int feet)
        {
            int air = (int)Math.Round(tF * 10), dew = (int)Math.Round(Math.Min(tF, dF) * 10);
            int wb = WeatherAir.WetBulb(air, dew, feet);
            var sun = SunVector.At(lat, lon, doy, hod * 3600 + 1800);
            var (beam, diffuse) = SlopeSun.At(sun, sun.AboveHorizon ? clear : 0, 0, 0, SunVector.One, 1000, true);
            var x = new SnowInput
            {
                Seconds = seconds, Air = air, Dew = dew, WetBulb = wb, Feet = feet, Wind = wind * OpenWind / 1000, Cloud = cloud,
                Beam = beam, Diffuse = diffuse, OpenSky = 1000, Canopy = 0,
                Snow = (int)Math.Round(snowIn * 25_400), SnowDensity = 10_000 / WeatherAir.SnowRatioTenths(wb), Rain = (int)Math.Round(rainIn * 25_400),
            };
            bool had = p.Swe > 0;
            var o = SnowEnergy.Step(ref p, x, SnowTuning.Default);
            s.Snow[month] += snowIn; s.Rain[month] += rainIn; s.Runoff[month] += o.Runoff / 25_400.0; s.Sub[month] += o.Sublimated / 25_400.0;
            if (had)
            {
                s.Sw[month] += o.Shortwave / 1000.0 * seconds; s.Lw[month] += o.Longwave / 1000.0 * seconds;
                s.Sh[month] += o.Sensible / 1000.0 * seconds; s.Lh[month] += o.Latent / 1000.0 * seconds; s.SnowSec[month] += seconds;
            }
            s.Pack[month] += p.Swe / 25_400.0 * seconds; s.PackN[month] += seconds;
            if (sun.AboveHorizon && tF > 32) { s.WarmSun[month] += beam + diffuse; s.WarmSunN[month]++; }
        }

        static int MonthOf(int civil) => Math.Min(9, (CivilDay.Date(civil).Month + 2) % 12);

        /// <summary>The record: its own hours for the weather's shape, and played at the game's clock for the snow.</summary>
        static Side Record(BlendedRecord b, WeatherRows rows, int lat, int lon, int feet, double k, int faceSeconds, Sky sky)
        {
            var s = new Side();
            var states = WeatherStates.Find(b);
            int nh = Math.Min(b.HourCount, states.State.Length);
            double T(int i) => float.IsNaN(b.T[i]) ? double.NaN : Psychrometrics.CToF(b.T[i]);
            double D(int i, double t) => float.IsNaN(b.Td[i]) ? t - 2 : Psychrometrics.CToF(b.Td[i]);
            double P(int i) => float.IsNaN(b.P[i]) ? 0 : b.P[i] / 0.254 / 100;   // in
            int Cloud(int i) => float.IsNaN(b.Cloud[i]) ? 500 : (int)Math.Round(Math.Max(0, Math.Min(100, b.Cloud[i])) * 10);
            int Clear(int i) => float.IsNaN(b.Clearness[i]) ? 0 : (int)Math.Round(Math.Max(0, Math.Min(1, b.Clearness[i])) * 1000);
            int Wind(int i) => float.IsNaN(b.Wind[i]) ? 45 : (int)Math.Round(b.Wind[i] * 22.369);
            double Share(double t, double d) => Psychrometrics.SnowShare(Psychrometrics.WetBulbF(t, Psychrometrics.RelativeHumidity(t, Math.Min(t, d)), feet));

            for (int d0 = 0; d0 < states.DayOfLabelYear.Length; d0++)
            {
                if (states.DayOfLabelYear[d0] != 0) continue;
                int start = d0 * 24;
                if (start + 365 * 24 > nh) continue;
                s.Winters++;
                // The weather's shape, from the record's own hours.
                int run = 0, runMonth = 0;
                for (int d = 0; d < 304; d++)
                {
                    int m = MonthOf(b.Days[d0 + d]);
                    double hi = double.MinValue, lo = double.MaxValue, clearSum = 0; int clearN = 0;
                    for (int h = 0; h < 24; h++)
                    {
                        int i = start + d * 24 + h; double t = T(i);
                        if (double.IsNaN(t)) continue;
                        hi = Math.Max(hi, t); lo = Math.Min(lo, t);
                        s.Above[m] += t > 32 ? 1 : 0; s.AboveN[m]++; s.Temp[m] += t; s.TempN[m]++;
                        s.Hour(m, h, t, D(i, t), Wind(i));
                        bool night = h < 6 || h >= 20;
                        if (night && Cloud(i) < 300) { s.ClearNight[m] += t; s.ClearNightN[m]++; }
                        if (night && Cloud(i) > 700) { s.CloudyNight[m] += t; s.CloudyNightN[m]++; }
                        if (Clear(i) > 0) { clearSum += Clear(i); clearN++; }
                        int st = states.State[i];
                        sky.Add(m, st, night ? 1 : 0, Cloud(i), t, Clear(i));
                        if (i + 1 < nh && states.State[i + 1] == st && !float.IsNaN(b.Cloud[i]) && !float.IsNaN(b.Cloud[i + 1]))
                        {
                            double a = Cloud(i) - sky.Mean(m, st, night ? 1 : 0), c = Cloud(i + 1) - sky.Mean(m, st, night ? 1 : 0);
                            sky.LagN++; sky.LagA += a; sky.LagB += c; sky.LagAB += a * c; sky.LagA2 += a * a; sky.LagB2 += c * c;
                        }
                    }
                    if (hi > lo) { s.Swing[m] += hi - lo; s.SwingN[m]++; }
                    bool clearDay = clearN > 0 && clearSum / clearN >= 700;
                    s.ClearDay[m] += clearDay ? 1 : 0; s.DayN[m]++;
                    if (clearDay) { if (run == 0) runMonth = m; run++; }
                    else if (run > 0) { s.Runs[runMonth] += run; s.RunN[runMonth]++; run = 0; }
                }
                // The snow, at the game's clock.
                var pack = new SnowPack();
                for (int j = 0; ; j++)
                {
                    double h0 = j * k, h1 = (j + 1) * k;
                    if (h1 > 304 * 24) break;
                    int hod = j % 24, kk = start + (int)(h0 / 24) * 24 + hod;
                    double sn = 0, rn = 0;
                    for (int r = (int)Math.Ceiling(h0); r < h1; r++)
                    {
                        int i = start + r; double tr = T(i), pr = P(i);
                        if (pr > 0 && !double.IsNaN(tr)) { double sh = Share(tr, D(i, tr)); sn += pr * sh; rn += pr * (1 - sh); }
                    }
                    double t = T(kk);
                    if (double.IsNaN(t)) continue;
                    int civil = b.Days[kk / 24];
                    Step(s, MonthOf(civil), ref pack, sn, rn, t, D(kk, t), Wind(kk), Cloud(kk), Clear(kk), lat, lon, CivilDay.Date(civil).DayOfYear, hod, faceSeconds, feet);
                }
            }
            return s;
        }

        /// <summary>The frozen rows' seasons: the same statistics over face days (a face day is the game's day cycle).</summary>
        static Side Game(WeatherRows rows, int lat, int lon, int feet, double k, int faceSeconds, Sky count)
        {
            var s = new Side();
            var tuning = WeatherTuning.Default;
            var gen = new WeatherGenerator(rows, tuning);
            var h = new WeatherHour();
            int hours = WeatherGenerator.HoursInYear(tuning);
            for (int seed = 0; seed < Seeds; seed++)
            {
                gen.Reset(0x4741_5052_0000_0000UL + (ulong)seed, 2026);
                s.Winters++;
                var pack = new SnowPack();
                double hi = double.MinValue, lo = double.MaxValue, clearSum = 0; int clearN = 0, run = 0, runMonth = 0;
                for (int i = 0; i < hours; i++)
                {
                    gen.Step(ref h);
                    int day = (int)((i + 0.5) * k / 24);
                    if (day >= 304) break;
                    int doy = (274 + day - 1) % 365 + 1;
                    int m = Math.Min(9, (CivilDay.Date(CivilDay.Of(2025, 10, 1) + day).Month + 2) % 12);
                    double t = h.Temp / 10.0, d = h.Dew / 10.0, p = h.Liquid / 100.0;
                    bool isNight = h.Clearness == 0;
                    int cloudG = h.Cloud, clearG = h.Clearness;
                    count.Add(m, h.State, isNight ? 1 : 0, cloudG, t, clearG);
                    double share = p > 0 ? Psychrometrics.SnowShare(Psychrometrics.WetBulbF(t, Psychrometrics.RelativeHumidity(t, Math.Min(t, d)), feet)) : 0;
                    Step(s, m, ref pack, p * share, p * (1 - share), t, d, h.Wind, cloudG, clearG, lat, lon, doy, h.HourOfDay, faceSeconds, feet);
                    s.Above[m] += t > 32 ? 1 : 0; s.AboveN[m]++; s.Temp[m] += t; s.TempN[m]++;
                    s.Hour(m, h.HourOfDay, t, d, h.Wind);
                    bool night = h.HourOfDay < 6 || h.HourOfDay >= 20;
                    if (night && cloudG < 300) { s.ClearNight[m] += t; s.ClearNightN[m]++; }
                    if (night && cloudG > 700) { s.CloudyNight[m] += t; s.CloudyNightN[m]++; }
                    if (clearG > 0) { clearSum += clearG; clearN++; }
                    hi = Math.Max(hi, t); lo = Math.Min(lo, t);
                    if (h.HourOfDay == 23)
                    {
                        if (hi > lo) { s.Swing[m] += hi - lo; s.SwingN[m]++; }
                        bool clearDay = clearN > 0 && clearSum / clearN >= 700;
                        // A face day stands for K real days of weather: count it K times, and its runs in real days.
                        s.ClearDay[m] += clearDay ? k : 0; s.DayN[m] += k;
                        if (clearDay) { if (run == 0) runMonth = m; run++; }
                        else if (run > 0) { s.Runs[runMonth] += run * k; s.RunN[runMonth]++; run = 0; }
                        hi = double.MinValue; lo = double.MaxValue; clearSum = 0; clearN = 0;
                    }
                }
            }
            return s;
        }

        static void PrintSky(string site, Sky r, Sky g)
        {
            var sb = new StringBuilder($"[sky] {site}: cloud's hour-to-hour memory within a state, record {r.Lag1:0.00}\n");
            string[] states = { "Fair", "Building", "Storm", "Clearing" };
            foreach (int m in new[] { 3, 6 })
                for (int st = 0; st < 4; st++)
                    sb.Append($"[sky]   {MonthNames[m]} {states[st],-8} night cloud {r.Mean(m, st, 1):0}±{r.Sd(m, st, 1):0} | {g.Mean(m, st, 1):0}±{g.Sd(m, st, 1):0}  day cloud {r.Mean(m, st, 0):0}±{r.Sd(m, st, 0):0} | {g.Mean(m, st, 0):0}±{g.Sd(m, st, 0):0}  clearness sd {r.ClearSd(m, st):0} | {g.ClearSd(m, st):0}  °F per full cloud: night {r.Slope(m, st, 1):+0.0;-0.0} | {g.Slope(m, st, 1):+0.0;-0.0}, day {r.Slope(m, st, 0):+0.0;-0.0} | {g.Slope(m, st, 0):+0.0;-0.0}\n");
            Console.Write(sb.ToString());
        }

        static void PrintHours(string site, Side r, Side g)
        {
            var sb = new StringBuilder();
            double Mean(Side x, double[,] v, int m) { double t = 0, n = 0; for (int h = 0; h < 24; h++) { t += v[m, h]; n += x.HourN[m, h]; } return n > 0 ? t / n : double.NaN; }
            sb.Append($"[month] {site} dew depression by month, record | game: " + string.Join("  ", Enumerable.Range(1, 8).Select(m => $"{MonthNames[m]} {Mean(r, r.HourDep, m):0.0}|{Mean(g, g.HourDep, m):0.0}")) + "\n");
            sb.Append($"[month] {site} air by month, record | game: " + string.Join("  ", Enumerable.Range(1, 8).Select(m => $"{MonthNames[m]} {Mean(r, r.HourT, m):0.0}|{Mean(g, g.HourT, m):0.0}")) + "\n");
            foreach (int m in new[] { 3, 5, 6, 7 })
            {
                string Row(Side x, double[,] v, double scale) => string.Join(" ", Enumerable.Range(0, 24).Where(h => h % 3 == 0)
                    .Select(h => (x.HourN[m, h] > 0 ? v[m, h] / x.HourN[m, h] * scale : double.NaN).ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5)));
                double Amp(Side x) { var a = Enumerable.Range(0, 24).Where(h => x.HourN[m, h] > 0).Select(h => x.HourT[m, h] / x.HourN[m, h]).ToArray(); return a.Length == 0 ? double.NaN : a.Max() - a.Min(); }
                sb.Append($"[hours] {site} {MonthNames[m]}: diurnal range of the mean day {Amp(r):0.0} | {Amp(g):0.0} °F\n");
                sb.Append($"[hours]   air at 0,3,..,21 h  record {Row(r, r.HourT, 1)}\n[hours]                       game   {Row(g, g.HourT, 1)}\n");
                sb.Append($"[hours]   dew depression      record {Row(r, r.HourDep, 1)}\n[hours]                       game   {Row(g, g.HourDep, 1)}\n");
                sb.Append($"[hours]   wind mph            record {Row(r, r.HourWind, 0.1)}\n[hours]                       game   {Row(g, g.HourWind, 0.1)}\n");
            }
            Console.Write(sb.ToString());
        }

        static void Print(string site, Side r, Side g)
        {
            var inv = CultureInfo.InvariantCulture;
            string F(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "  —" : v.ToString("0.0", inv).PadLeft(5);
            double Per(double v, double n) => n > 0 ? v / n : double.NaN;
            var sb = new StringBuilder();
            sb.AppendLine($"[gap] {site}: record at the game's clock | game, per winter (in), mean fluxes on snow (W/m²), mean pack (in)");
            sb.AppendLine("[gap]   month  snow in      rain       runoff     sublim     sun        longwave   sensible   latent     pack");
            for (int m = 1; m < 9; m++)
            {
                sb.Append($"[gap]   {MonthNames[m]}  ");
                foreach (var (a, c) in new[] {
                    (Per(r.Snow[m], r.Winters), Per(g.Snow[m], g.Winters)), (Per(r.Rain[m], r.Winters), Per(g.Rain[m], g.Winters)),
                    (Per(r.Runoff[m], r.Winters), Per(g.Runoff[m], g.Winters)), (Per(r.Sub[m], r.Winters), Per(g.Sub[m], g.Winters)),
                    (Per(r.Sw[m], r.SnowSec[m]), Per(g.Sw[m], g.SnowSec[m])), (Per(r.Lw[m], r.SnowSec[m]), Per(g.Lw[m], g.SnowSec[m])),
                    (Per(r.Sh[m], r.SnowSec[m]), Per(g.Sh[m], g.SnowSec[m])), (Per(r.Lh[m], r.SnowSec[m]), Per(g.Lh[m], g.SnowSec[m])),
                    (Per(r.Pack[m], r.PackN[m]), Per(g.Pack[m], g.PackN[m])) })
                    sb.Append(F(a)).Append('|').Append(F(c)).Append("  ");
                sb.AppendLine();
            }
            sb.AppendLine("[gap]   month  swing °F    >32 °F %    clear night cloudy nt  clear days % clear run d  warm sun   mean °F");
            for (int m = 1; m < 9; m++)
            {
                sb.Append($"[gap]   {MonthNames[m]}  ");
                foreach (var (a, c) in new[] {
                    (Per(r.Swing[m], r.SwingN[m]), Per(g.Swing[m], g.SwingN[m])), (100 * Per(r.Above[m], r.AboveN[m]), 100 * Per(g.Above[m], g.AboveN[m])),
                    (Per(r.ClearNight[m], r.ClearNightN[m]), Per(g.ClearNight[m], g.ClearNightN[m])), (Per(r.CloudyNight[m], r.CloudyNightN[m]), Per(g.CloudyNight[m], g.CloudyNightN[m])),
                    (100 * Per(r.ClearDay[m], r.DayN[m]), 100 * Per(g.ClearDay[m], g.DayN[m])), (Per(r.Runs[m], r.RunN[m]), Per(g.Runs[m], g.RunN[m])),
                    (Per(r.WarmSun[m], r.WarmSunN[m]), Per(g.WarmSun[m], g.WarmSunN[m])), (Per(r.Temp[m], r.TempN[m]), Per(g.Temp[m], g.TempN[m])) })
                    sb.Append(F(a)).Append('|').Append(F(c)).Append("  ");
                sb.AppendLine();
            }
            Console.Write(sb.ToString());
        }
    }
}
