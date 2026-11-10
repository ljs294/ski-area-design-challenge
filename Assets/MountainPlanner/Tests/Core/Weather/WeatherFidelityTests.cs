using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Engine-free. Task 4.05, the fidelity tests: the frozen rows played over 300 seeds against the record itself, never
    /// against the rows, on what a player feels. Every test runs at every site. A game window of W face days is set against
    /// W × 121 ÷ 42 real days (WX-21: the weather compresses with time).
    ///
    /// <para><b>Gates by rule</b> (the adversarial review, owner-approved 2026-11-09). Twenty winters are a sample, so each
    /// statistic's record value is uncertain: its 95 % range comes from resampling the record's winters (a block bootstrap by
    /// winter, keyed, so it is the same every run). The game's miss is measured in that noise (z: 1.96 is the 95 % edge). Two
    /// tiers, because a perfect model still lands about 1 cell in 100 outside the record's 99 % range by chance:
    /// - every cell sits inside the record's 99.9 % range (|z| ≤ 3.29);
    /// - over every statistic at every site, no more cells sit outside the 99 % range (|z| ≤ 2.58) than chance allows
    ///   (<see cref="FewCellsLeaveTheRecordsRange"/>).
    /// Both are widened by <see cref="SnowyMargin"/> on the side that means more snow (owner, 2026-11-09: "err toward more
    /// snow for tycoon fun"). A floor keeps a band from shrinking below what the statistic can resolve. There are no hand-set
    /// per-statistic gates and nothing per site.</para>
    ///
    /// <para>Every statistic here is something the calibration was fitted to or shaped by, on the same 20 winters, so these
    /// tests alone can't see over-fitting: <see cref="HoldOut_TheRowsPredictWintersTheyNeverSaw"/> is the independent check
    /// (rows built from half the winters, judged on the other half), and <see cref="NoBiasIsSharedAcrossSites"/> catches a
    /// miss every site makes a little of.</para>
    /// </summary>
    [Category("Fidelity")]
    public sealed class WeatherFidelityTests
    {
        const int Seeds = 300, HoldOutSeeds = 150, Draws = 1000, HoldOutDraws = 400;
        /// <summary>The record's 99 % and 99.9 % ranges, in its own noise (z).</summary>
        const double Z99 = 2.576, Z999 = 3.291;
        /// <summary>
        /// The snowy margin (owner, 2026-11-09): on the side that means more snow (deeper pack, less rain, bigger storms, more
        /// powder, later melt-out), the game may sit this much further out, in the record's noise units.
        /// </summary>
        public const double SnowyMargin = 1.0;

        static readonly int[] SnowDays = { 92, 123, 151, 182 };   // January 1, February 1, March 1, April 1 from October 1
        static readonly string[] SnowDayName = { "Jan 1", "Feb 1", "Mar 1", "Apr 1" };
        const int Holiday = 85;          // December 25 from October 1
        const double CoverIn = 1.0;      // a skiable holiday base: 1 in of water (about 4 in of settled snow)
        const double PowderIn = 0.5;     // a powder face day: 0.5 in of water as snow (about 6 in of new snow)
        const double ThawF = 34.0;       // a thawing hour

        static double FaceHour => (double)WeatherTuning.Default.CalendarDays / WeatherTuning.Default.FaceDays;
        static double WindowDays => 10 * FaceHour;   // WX-58's clustering window: 10 face days, in real days

        // ---------------- The shared reference snow model ----------------

        /// <summary>
        /// The reference snow model at zref, the same for the record and the game, so the pack compares like for like. The
        /// snow share of each hour's liquid accumulates; melt is a temperature index with two published refinements:
        /// - the sun's season: the melt factor runs from 0.0011 in of water per °F-hour over 32 °F at the winter solstice to
        ///   0.0044 at the summer one (SNOW-17's MFMIN and MFMAX, 0.3 and 1.2 mm per °C per 6 h), so the low midwinter sun
        ///   melts little;
        /// - the snowpack's temperature (cold content): new snow arrives at the air's temperature, the pack follows the air
        ///   over two days, and melt energy first warms a cold pack to 32 °F. Ice holds 0.0035 in of melt per inch of water
        ///   per °F (its heat capacity over the latent heat, 2.1 ÷ 334 per °C).
        /// Checked against SNOTEL pillows (step 2, 2026-11-09): Stevens Pass April 1, 35 in against the pillow's 37.
        /// </summary>
        sealed class ReferenceSnow
        {
            public const double MeltWinter = 0.0011, MeltSummer = 0.0044, IceHeat = 0.0035, PackFollowsAirHours = 48;
            public double Pack, PackTemp = 32;

            public void Step(double snowIn, double tempF, int dayOfYear, double hours)
            {
                if (snowIn > 0) { PackTemp = (PackTemp * Pack + Math.Min(32, tempF) * snowIn) / (Pack + snowIn); Pack += snowIn; }
                if (Pack <= 0) { Pack = 0; PackTemp = 32; return; }
                if (tempF < 32) PackTemp += (tempF - PackTemp) * Math.Min(1, hours / PackFollowsAirHours);
                double factor = MeltWinter + (MeltSummer - MeltWinter) * (1 - Math.Cos(2 * Math.PI * (dayOfYear + 10) / 365.0)) / 2;
                double energy = factor * hours * Math.Max(0, tempF - 32), cold = Pack * IceHeat * (32 - PackTemp);
                if (energy <= cold) PackTemp += energy / (Pack * IceHeat);
                else { Pack = Math.Max(0, Pack - (energy - cold)); PackTemp = 32; }
            }
        }

        // ---------------- One winter (a record label year, or one played seed) ----------------

        sealed class Yr
        {
            public int Year;
            public readonly List<double> Storms = new List<double>();                         // December–March storm totals (0.01 in)
            public readonly List<double>[] Win = { new List<double>(), new List<double>() };  // 1 and 3 face days of liquid, Dec–Mar
            public readonly List<double> SnowWin = new List<double>();                        // 1 face day of snow water, Dec–Mar
            public readonly double[] Rain = new double[12], Liquid = new double[12];          // by label month
            public readonly List<(int Third, double X, double Y)> SizeTemp = new List<(int, double, double)>(), GapNext = new List<(int, double, double)>();
            public readonly List<double> Cold = new List<double>();                           // December–February hours (°F)
            public int Thaw;                                                                  // of them, hours over ThawF
            public double WetT, WetL, SnowWater, RainOnSnow, NovAprLiquid;                    // October–April (Nov–Apr for the last two)
            public readonly double[] Lag = new double[12];                                    // wind: two lags × (sx, sy, sxy, sxx, syy, n)
            public readonly List<int>[] Clusters = { new List<int>(), new List<int>() };      // storms per 10-face-day window, by half
            public readonly double[] Swe = new double[4];
            public double Peak, Melt, HolidayPack;
        }

        sealed class Pool
        {
            public readonly List<double> Storms = new List<double>(), SnowWin = new List<double>(), Cold = new List<double>();
            public readonly List<double>[] Win = { new List<double>(), new List<double>() };
            public readonly double[] Rain = new double[12], Liquid = new double[12];
            public readonly List<(int Third, double X, double Y)> SizeTemp = new List<(int, double, double)>(), GapNext = new List<(int, double, double)>();
            public int Thaw;
            public double WetT, WetL, RainOnSnow, NovAprLiquid, WindMemory;
            public readonly List<int>[] Clusters = { new List<int>(), new List<int>() };
            public readonly List<double>[] Swe = Enumerable.Range(0, 4).Select(_ => new List<double>()).ToArray();
            public readonly List<double> Peak = new List<double>(), Melt = new List<double>(), Holiday = new List<double>(), Season = new List<double>(), SeasonSnow = new List<double>();
            List<double> _coldSorted;
            public List<double> ColdSorted => _coldSorted ??= Cold.OrderBy(v => v).ToList();
        }

        static Pool PoolOf(IEnumerable<Yr> years)
        {
            var p = new Pool(); var lag = new double[12];
            foreach (var y in years)
            {
                p.Storms.AddRange(y.Storms); p.Win[0].AddRange(y.Win[0]); p.Win[1].AddRange(y.Win[1]); p.SnowWin.AddRange(y.SnowWin);
                for (int m = 0; m < 12; m++) { p.Rain[m] += y.Rain[m]; p.Liquid[m] += y.Liquid[m]; }
                p.SizeTemp.AddRange(y.SizeTemp); p.GapNext.AddRange(y.GapNext); p.Cold.AddRange(y.Cold); p.Thaw += y.Thaw;
                p.WetT += y.WetT; p.WetL += y.WetL; p.RainOnSnow += y.RainOnSnow; p.NovAprLiquid += y.NovAprLiquid;
                for (int i = 0; i < 12; i++) lag[i] += y.Lag[i];
                p.Clusters[0].AddRange(y.Clusters[0]); p.Clusters[1].AddRange(y.Clusters[1]);
                for (int j = 0; j < 4; j++) p.Swe[j].Add(y.Swe[j]);
                p.Peak.Add(y.Peak); p.Melt.Add(y.Melt); p.Holiday.Add(y.HolidayPack);
                p.Season.Add(Enumerable.Range(0, 7).Sum(m => y.Liquid[m])); p.SeasonSnow.Add(y.SnowWater);
            }
            double R(int o)
            {
                double n = lag[o + 5], c = lag[o + 2] / n - lag[o] / n * lag[o + 1] / n;
                return c / Math.Sqrt((lag[o + 3] / n - lag[o] / n * lag[o] / n) * (lag[o + 4] / n - lag[o + 1] / n * lag[o + 1] / n));
            }
            // The record's wind memory over one face hour (2.88 real hours), between its lags 2 and 3; the game's lag of one
            // face hour sits in both slots, so the same line gives it back.
            double r2 = R(0), r3 = R(6);
            p.WindMemory = r2 + (FaceHour - 2) * (r3 - r2);
            return p;
        }

        static double SnowShare(double t, double d) => Psychrometrics.SnowShare(Psychrometrics.WetBulbF(t, Psychrometrics.RelativeHumidity(t, Math.Min(t, d))));
        static bool DecToMar(int third) => third >= 6 && third < 18;
        static bool NovToApr(int third) => third >= 3 && third < 21;
        static bool DecToFeb(int third) => third >= 6 && third < 15;
        static int HalfOf(double day) => day < 92 ? 0 : day + WindowDays <= 212 ? 1 : -1;   // WX-58: Oct–Dec, Jan–Apr

        // ---------------- The record's winters ----------------

        static List<Yr> RecordYears(BlendedRecord b, WeatherRows rows)
        {
            var states = WeatherStates.Find(b);
            var ys = new Dictionary<int, Yr>();
            Yr Y(int y) { if (!ys.TryGetValue(y, out var r)) ys[y] = r = new Yr { Year = y }; return r; }
            int nh = Math.Min(b.HourCount, states.State.Length);
            double T(int k) => float.IsNaN(b.T[k]) ? double.NaN : Psychrometrics.CToF(b.T[k]);
            double D(int k, double t) => float.IsNaN(b.Td[k]) ? t - 2 : Psychrometrics.CToF(b.Td[k]);
            double P(int k) => float.IsNaN(b.P[k]) ? 0 : b.P[k] / 0.254;   // 0.01 in

            foreach (var st in states.Storms)
            {
                var y = Y(states.HourLabelYear(st.Start));
                if (DecToMar(st.Third)) y.Storms.Add(st.TotalMm / 0.254);
                if (!NovToApr(st.Third) || st.TotalMm <= 0) continue;
                double sum = 0; int n = 0;
                for (int k = st.Start; k < st.End && k < nh; k++) if (!double.IsNaN(T(k))) { sum += T(k); n++; }
                if (n > 0) y.SizeTemp.Add((st.Third, Math.Log(st.TotalMm), sum / n));
            }
            foreach (var cy in states.Cycles)
            {
                if (cy.Truncated || cy.FairEnd <= cy.FairStart || cy.Storms.Count == 0 || !NovToApr(cy.Third)) continue;
                double next = states.Storms[cy.Storms[0]].TotalMm;
                if (next > 0) Y(states.HourLabelYear(cy.FairStart)).GapNext.Add((cy.Third, Math.Log(cy.FairEnd - cy.FairStart), Math.Log(next)));
            }
            // Wind memory: non-storm departures from the state and third.
            var z = new double[nh];
            for (int k = 0; k < nh; k++)
            {
                z[k] = double.NaN;
                int third = states.HourThird(k), s = states.State[k], i = third * WeatherRows.States + s;
                if (s == WeatherRows.Storm || float.IsNaN(b.Wind[k]) || rows.WindSpread[i] <= 0 || !NovToApr(third)) continue;
                z[k] = (b.Wind[k] * 22.369362920544 - rows.WindMean[i]) / rows.WindSpread[i];
            }
            for (int j = 0; j < 2; j++)
                for (int k = 0; k + 2 + j < nh; k++)
                {
                    double a = z[k], c = z[k + 2 + j];
                    if (double.IsNaN(a) || double.IsNaN(c)) continue;
                    var l = Y(states.HourLabelYear(k)).Lag; int o = j * 6;
                    l[o] += a; l[o + 1] += c; l[o + 2] += a * c; l[o + 3] += a * a; l[o + 4] += c * c; l[o + 5]++;
                }

            // Each whole label year from its October 1: hours, windows, clusters and the snow on the ground.
            var firstHour = new SortedDictionary<int, int>();
            for (int d = 0; d < states.DayLabelYear.Length; d++)
                if (states.DayOfLabelYear[d] == 0 && !firstHour.ContainsKey(states.DayLabelYear[d])) firstHour[states.DayLabelYear[d]] = d * 24;
            var seasons = new List<Yr>();
            int[] length = { (int)Math.Round(24 * FaceHour), (int)Math.Round(72 * FaceHour) };
            var stormStarts = states.Storms.Select(st => st.Start).ToList();
            foreach (var kv in firstHour)
            {
                int start = kv.Value;
                if (start + 365 * 24 > nh) continue;
                var y = Y(kv.Key);
                for (int j = 0; j < 2; j++)
                    for (int w = 0; (w + 1) * length[j] <= 365 * 24; w++)
                    {
                        double from = w * length[j] / 24.0, to = (w + 1) * length[j] / 24.0;
                        if (from < 61 || to > 182) continue;
                        double sum = 0, snow = 0; bool missing = false;
                        for (int k = start + w * length[j]; k < start + (w + 1) * length[j]; k++)
                        {
                            if (float.IsNaN(b.P[k])) missing = true;
                            sum += P(k);
                            double t = T(k);
                            if (!double.IsNaN(t) && P(k) > 0) snow += P(k) * SnowShare(t, D(k, t));
                        }
                        if (missing) continue;
                        y.Win[j].Add(sum);
                        if (j == 0) y.SnowWin.Add(snow);
                    }
                var windows = new int[(int)(365 / WindowDays) + 1];
                foreach (int s0 in stormStarts) if (s0 >= start && s0 < start + 365 * 24) windows[(int)((s0 - start) / 24 / WindowDays)]++;
                for (int w = 0; w < windows.Length; w++) { int half = HalfOf(w * WindowDays); if (half >= 0) y.Clusters[half].Add(windows[w]); }

                var swe = new double[365];
                var pack = new ReferenceSnow();
                for (int k = start; k < start + 365 * 24; k++)
                {
                    int third = states.HourThird(k), day = (k - start) / 24;
                    double t = T(k);
                    if (!double.IsNaN(t))
                    {
                        double p = P(k), share = p > 0 ? SnowShare(t, D(k, t)) : 0;
                        if (DecToFeb(third)) { y.Cold.Add(t); if (t > ThawF) y.Thaw++; }
                        if (p > 0)
                        {
                            y.Liquid[third / 3] += p; y.Rain[third / 3] += p * (1 - share);
                            if (third < 21) { y.WetT += p * t; y.WetL += p; y.SnowWater += p * share; }
                            if (NovToApr(third)) { y.NovAprLiquid += p; if (pack.Pack >= CoverIn) y.RainOnSnow += p * (1 - share); }
                        }
                        pack.Step(p / 100 * share, t, CivilDay.Date(b.Days[k / 24]).DayOfYear, 1);
                    }
                    if ((k - start + 1) % 24 == 0) swe[day] = pack.Pack;
                }
                AddSeason(y, swe);
                seasons.Add(y);
            }
            return seasons;
        }

        static void AddSeason(Yr y, double[] swe)
        {
            for (int j = 0; j < SnowDays.Length; j++) y.Swe[j] = swe[SnowDays[j]];
            int peakDay = 0;
            for (int day = 0; day < 273; day++) if (swe[day] > swe[peakDay]) peakDay = day;   // to June 30
            y.Peak = swe[peakDay];
            int melt = peakDay;
            while (melt < 364 && swe[melt] > 0.1) melt++;
            y.Melt = melt;
            y.HolidayPack = swe[Holiday];
        }

        // ---------------- The game's winters: one per seed ----------------

        static List<Yr> GameYears(WeatherRows rows, int seeds)
        {
            var result = new List<Yr>();
            var tuning = WeatherTuning.Default;
            var gen = new WeatherGenerator(rows, tuning);
            var h = new WeatherHour();
            int hours = WeatherGenerator.HoursInYear(tuning);
            double fh = FaceHour;
            for (int s = 0; s < seeds; s++)
            {
                var y = new Yr { Year = s };
                gen.Reset(0x4649_4445_0000_0000UL + (ulong)s, 2026);
                int ordinal = -1, stormThird = 0, prevState = -1, fairStart = -1, fairThird = 0, pendingGap = -1, gapThird = 0, stormTotal = 0, clusterStorms = 0;
                double stormTemp = 0, prevZ = double.NaN; int stormN = 0;
                var window = new double[2]; double snowWindow = 0;
                var swe = new double[365];
                var pack = new ReferenceSnow();
                for (int i = 0; i < hours; i++)
                {
                    gen.Step(ref h);
                    double t = h.Temp / 10.0, d = h.Dew / 10.0, p = h.Liquid;
                    int third = h.Third, day = (int)((i + 0.5) * fh / 24);
                    if (h.StormOrdinal != ordinal)
                    {
                        if (ordinal >= 0 && stormN > 0 && NovToApr(stormThird) && stormTotal > 0) y.SizeTemp.Add((stormThird, Math.Log(stormTotal), stormTemp / stormN));
                        ordinal = h.StormOrdinal; stormTemp = 0; stormN = 0;
                        if (ordinal >= 0)
                        {
                            stormThird = third; stormTotal = h.StormTotal; clusterStorms++;
                            if (DecToMar(third)) y.Storms.Add(h.StormTotal);
                            if (pendingGap > 0 && h.StormTotal > 0) y.GapNext.Add((gapThird, Math.Log(pendingGap * fh), Math.Log(h.StormTotal)));
                            pendingGap = -1;
                        }
                    }
                    if (h.State == WeatherRows.Storm && ordinal >= 0) { stormTemp += t; stormN++; }
                    if (h.State == WeatherRows.Fair && prevState != WeatherRows.Fair) { fairStart = i; fairThird = third; }
                    if (h.State != WeatherRows.Fair && prevState == WeatherRows.Fair && fairStart >= 0 && NovToApr(fairThird)) { pendingGap = i - fairStart; gapThird = fairThird; }
                    prevState = h.State;

                    double share = p > 0 ? SnowShare(t, d) : 0;
                    if (DecToFeb(third)) { y.Cold.Add(t); if (t > ThawF) y.Thaw++; }
                    if (p > 0)
                    {
                        y.Liquid[third / 3] += p; y.Rain[third / 3] += p * (1 - share);
                        if (third < 21) { y.WetT += p * t; y.WetL += p; y.SnowWater += p * share; }
                        if (NovToApr(third)) { y.NovAprLiquid += p; if (pack.Pack >= CoverIn) y.RainOnSnow += p * (1 - share); }
                    }

                    int iw = third * WeatherRows.States + h.State;
                    double z = h.State == WeatherRows.Storm || rows.WindSpread[iw] <= 0 || !NovToApr(third) ? double.NaN : (h.Wind - rows.WindMean[iw]) / (double)rows.WindSpread[iw];
                    if (!double.IsNaN(z) && !double.IsNaN(prevZ))
                        for (int o = 0; o < 12; o += 6) { y.Lag[o] += prevZ; y.Lag[o + 1] += z; y.Lag[o + 2] += prevZ * z; y.Lag[o + 3] += prevZ * prevZ; y.Lag[o + 4] += z * z; y.Lag[o + 5]++; }
                    prevZ = z;

                    for (int j = 0; j < 2; j++)
                    {
                        int len = j == 0 ? 24 : 72;
                        window[j] += p;
                        if (j == 0) snowWindow += p * share;
                        if ((i + 1) % len == 0)
                        {
                            double from = (i + 1 - len) * fh / 24, to = (i + 1) * fh / 24;
                            if (from >= 61 && to <= 182) { y.Win[j].Add(window[j]); if (j == 0) y.SnowWin.Add(snowWindow); }
                            window[j] = 0;
                            if (j == 0) snowWindow = 0;
                        }
                    }
                    if ((i + 1) % 240 == 0)
                    {
                        int half = HalfOf((i + 1 - 240) * fh / 24);
                        if (half >= 0) y.Clusters[half].Add(clusterStorms);
                        clusterStorms = 0;
                    }

                    pack.Step(p / 100.0 * share, t, (274 + day - 1) % 365 + 1, fh);
                    int end = (int)((i + 1) * fh / 24) - 1;
                    if (end >= 0 && end < 365) swe[end] = pack.Pack;
                }
                AddSeason(y, swe);
                result.Add(y);
            }
            return result;
        }

        // ---------------- The statistics, their bands and the gate ----------------

        sealed class Stat
        {
            public string Name, Group, Unit;
            public Func<Pool, double> F;
            public bool Ratio;     // a miss as game ÷ record − 1, else game − record
            public double Floor;   // the smallest band half-width the statistic resolves, in miss units
            public bool PerWinter; // a share of winters: its floor is also one winter's worth
            public int Snowy;      // +1: higher is snowier; −1: lower is snowier; 0: neither
        }

        static Stat S(string group, string name, Func<Pool, double> f, bool ratio, double floor, int snowy, string unit = "", bool perWinter = false) =>
            new Stat { Group = group, Name = name, F = f, Ratio = ratio, Floor = floor, Snowy = snowy, Unit = unit, PerWinter = perWinter };

        static readonly Stat[] Stats = BuildStats();

        static Stat[] BuildStats()
        {
            var l = new List<Stat>
            {
                S("tails", "Dec–Mar storm, 90 % point", p => Quantile(p.Storms, 0.9) / 100, true, 0.03, +1, "in"),
                S("tails", "Dec–Mar storm, 99 % point", p => Quantile(p.Storms, 0.99) / 100, true, 0.03, +1, "in"),
                S("tails", "1-face-day liquid, 99 % point", p => Quantile(p.Win[0], 0.99) / 100, true, 0.03, +1, "in"),
                S("tails", "3-face-day liquid, 99 % point", p => Quantile(p.Win[1], 0.99) / 100, true, 0.03, +1, "in"),
            };
            string[] month = { "Oct", "Nov", "Dec", "Jan", "Feb", "Mar", "Apr" };
            for (int m = 1; m <= 6; m++) { int mm = m; l.Add(S("rain", $"rain share, {month[m]}", p => p.Rain[mm] / p.Liquid[mm], false, 0.01, -1)); }
            l.Add(S("snowfall", "Oct–Apr snow water", p => p.SeasonSnow.Average() / 100, true, 0.03, +1, "in"));
            l.Add(S("snowfall", "wet-hour temperature", p => p.WetT / p.WetL, false, 0.3, -1, "°F"));
            l.Add(S("storms", "storm size vs temperature, r", p => Within(p.SizeTemp), false, 0.02, 0));
            l.Add(S("storms", "storm-to-storm temperature spread", p => Within2(p.SizeTemp), true, 0.03, 0, "°F"));
            l.Add(S("cold", "Dec–Feb hours, 1 % point", p => SortedQuantile(p.ColdSorted, 0.01), false, 0.3, 0, "°F"));
            l.Add(S("cold", "Dec–Feb hours, 10 % point", p => SortedQuantile(p.ColdSorted, 0.10), false, 0.3, 0, "°F"));
            l.Add(S("cold", $"Dec–Feb hours over {ThawF} °F", p => (double)p.Thaw / p.Cold.Count, false, 0.005, -1));
            l.Add(S("wind", "wind memory over a face hour", p => p.WindMemory, false, 0.01, 0));
            l.Add(S("dry", "Fair spell vs the next storm, r", p => Within(p.GapNext), false, 0.02, 0));
            l.Add(S("cluster", "clustering, Oct–Dec", p => VarianceOverMean(p.Clusters[0]), true, 0.03, 0));
            l.Add(S("cluster", "clustering, Jan–Apr", p => VarianceOverMean(p.Clusters[1]), true, 0.03, 0));
            for (int j = 0; j < 4; j++) { int jj = j; l.Add(S("pack", $"pack {SnowDayName[j]}", p => p.Swe[jj].Average(), true, 0.03, +1, "in")); }
            l.Add(S("pack", "pack peak", p => p.Peak.Average(), true, 0.03, +1, "in"));
            l.Add(S("pack", "melt-out day (from Oct 1)", p => p.Melt.Average(), false, 2, +1, "d"));
            l.Add(S("pack", $"holidays with ≥ {CoverIn} in of water on Dec 25", p => p.Holiday.Count(v => v >= CoverIn) / (double)p.Holiday.Count, false, 0, +1, perWinter: true));
            l.Add(S("powder", "Nov–Apr liquid as rain on a pack", p => p.RainOnSnow / p.NovAprLiquid, false, 0.01, -1));
            l.Add(S("powder", $"Dec–Mar face days with ≥ {PowderIn} in of snow water", p => p.SnowWin.Count(v => v >= PowderIn * 100) / (double)p.SnowWin.Count, false, 0.01, +1));
            l.Add(S("spread", "Oct–Apr liquid, CV", p => Cv(p.Season), true, 0.03, 0));
            l.Add(S("spread", "Oct–Apr snow water, CV", p => Cv(p.SeasonSnow), true, 0.03, 0));
            l.Add(S("spread", "Apr 1 pack, sd", p => Sd(p.Swe[3]), true, 0.03, 0, "in"));
            return l.ToArray();
        }

        /// <summary>Each statistic's value on resampled records (block bootstrap by winter, keyed): [stat][draw].</summary>
        static double[][] Resample(List<Yr> years, int draws, ulong key)
        {
            var v = Stats.Select(_ => new List<double>()).ToArray();
            for (int r = 0; r < draws; r++)
            {
                var pick = new List<Yr>(years.Count);
                for (int i = 0; i < years.Count; i++) { key = key * 6364136223846793005UL + 1442695040888963407UL; pick.Add(years[(int)((key >> 33) % (ulong)years.Count)]); }
                var pool = PoolOf(pick);
                for (int k = 0; k < Stats.Length; k++) { double x = Stats[k].F(pool); if (!double.IsNaN(x) && !double.IsInfinity(x)) v[k].Add(x); }
            }
            return v.Select(x => x.ToArray()).ToArray();
        }

        sealed class Verdict
        {
            public Stat Stat; public double Game, Record, Lo, Hi, Miss, Half, Z;
            double Margin(int side) => Stat.Snowy == side ? SnowyMargin : 0;
            public double Low => -Z99 - Margin(-1);
            public double High => Z99 + Margin(+1);
            /// <summary>Inside the record's 99 % range (with the snowy margin).</summary>
            public bool Pass => Z >= Low && Z <= High;
            /// <summary>Inside the record's 99.9 % range (with the snowy margin): every cell must be.</summary>
            public bool Hard => Z >= -Z999 - Margin(-1) && Z <= Z999 + Margin(+1);
            public string Line()
            {
                string m = Stat.Ratio ? $"{Miss:+0%;-0%}" : $"{Miss:+0.000;-0.000}", h = Stat.Ratio ? $"±{Half:P0}" : $"±{Half:0.000}";
                return $"{Stat.Name}: {Game:0.000}|{Record:0.000} {Stat.Unit} (record 95 % {Lo:0.000}–{Hi:0.000}), miss {m} (noise {h}), z {Z:+0.0;-0.0} in [{Low:0.0}, {High:+0.0}]{(Hard ? Pass ? "" : "  outside 99 %" : "  FAIL, outside 99.9 %")}";
            }
        }

        static Verdict Judge(int k, Pool game, Pool record, double[] resampled)
        {
            var st = Stats[k];
            double floor = st.PerWinter ? Math.Max(st.Floor, 1.0 / record.Holiday.Count) : st.Floor;
            double g = st.F(game), r = st.F(record);
            double lo = Quantile(resampled.ToList(), 0.025), hi = Quantile(resampled.ToList(), 0.975);
            double half = st.Ratio ? (hi - lo) / 2 / r : (hi - lo) / 2;
            half = Math.Max(floor, double.IsNaN(half) ? 0 : half);
            double miss = st.Ratio ? g / r - 1 : g - r;
            return new Verdict
            {
                Stat = st, Game = g, Record = r, Lo = lo, Hi = hi, Miss = miss, Half = half, Z = miss / (half / 1.96),
            };
        }

        static readonly Dictionary<string, Verdict[]> Cache = new Dictionary<string, Verdict[]>();

        static Verdict[] Verdicts(string site)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(site, out var hit)) return hit;
                var rows = WeatherRowsFile.FromBytes(TestData.Bytes(TestData.Folder("weather/" + site), WeatherRowsFile.FileName));
                var years = RecordYears(WeatherFixtures.Screened(site).Blend, rows);
                var record = PoolOf(years); var game = PoolOf(GameYears(rows, Seeds));
                var resampled = Resample(years, Draws, 0xB007_5EED_0000_0000UL);
                return Cache[site] = Enumerable.Range(0, Stats.Length).Select(k => Judge(k, game, record, resampled[k])).ToArray();
            }
        }

        static void Gate(string site, string group)
        {
            var v = Verdicts(site).Where(x => x.Stat.Group == group).ToList();
            foreach (var x in v) TestContext.Progress.WriteLine($"{site}: {x.Line()}");
            var misses = v.Where(x => !x.Hard).Select(x => x.Line()).ToList();
            Assert.That(misses, Is.Empty, $"{site}: outside the record's 99.9 % range ({group})");
        }

        // ---------------- The tests ----------------

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void StormTailsMatchTheRecord(string site) => Gate(site, "tails");

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void RainShareByMonthMatchesTheRecord(string site) => Gate(site, "rain");

        /// <summary>WX-07 v3(c), moved here: October–April snow water and the temperature while it precipitates.</summary>
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void SnowfallAndWetHoursMatchTheRecord(string site) => Gate(site, "snowfall");

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void BigStormsAreAsWarmAsInTheRecord(string site) => Gate(site, "storms");

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void ColdSnapsAndThawsMatchTheRecord(string site) => Gate(site, "cold");

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void TheWindHoldsAsLongAsInTheRecord(string site) => Gate(site, "wind");

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void ALongDrySpellBringsWhatTheRecordsDid(string site) => Gate(site, "dry");

        /// <summary>WX-58's clustering, moved here: storms per 10-face-day window, variance over mean, by season half.</summary>
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void StormsClusterLikeTheRecord(string site) => Gate(site, "cluster");

        /// <summary>The reference pack on the 1st of January to April, its peak, its melt-out and the holiday cover.</summary>
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void TheSnowOnTheGroundMatchesTheRecord(string site) => Gate(site, "pack");

        /// <summary>Rain on snow and powder days, both ways: too few powder days fails as well as too many.</summary>
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void RainOnSnowAndPowderDaysMatchTheRecord(string site) => Gate(site, "powder");

        /// <summary>WX-19's season-to-season spread, moved here, plus the spread of the April 1 pack.</summary>
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WintersVaryLikeTheRecords(string site) => Gate(site, "spread");

        /// <summary>
        /// The second tier: over every statistic at every site, cells outside the record's 99 % range. A perfect model would
        /// land each there with a chance under 1 %, so the count is a binomial; the gate is its 99 % point (6 of 231 today).
        /// </summary>
        [Test]
        public void FewCellsLeaveTheRecordsRange()
        {
            var all = WeatherFixtures.Sites.SelectMany(site => Verdicts(site).Select(v => (site, v))).ToList();
            var outside = all.Where(x => !x.v.Pass).ToList();
            int budget = BinomialPoint(all.Count, 0.01, 0.99);
            foreach (var (site, v) in outside) TestContext.Progress.WriteLine($"{site}: {v.Line()}");
            TestContext.Progress.WriteLine($"{outside.Count} of {all.Count} cells outside the record's 99 % range (chance allows {budget})");
            Assert.That(outside.Count, Is.LessThanOrEqualTo(budget), "more cells outside the record's 99 % range than chance allows");
        }

        /// <summary>The smallest k with P(X ≤ k) ≥ q for X ~ Binomial(n, p).</summary>
        static int BinomialPoint(int n, double p, double q)
        {
            double term = Math.Pow(1 - p, n), sum = term;
            int k = 0;
            while (sum < q && k < n) { term *= (double)(n - k) / (k + 1) * p / (1 - p); k++; sum += term; }
            return k;
        }

        /// <summary>
        /// A miss every site makes a little of passes each site's gate. Averaged over the seven sites, a statistic's z has a
        /// noise of 1 ÷ √7, so its mean must sit inside 2.58 ÷ √7, with the snowy margin scaled the same way.
        /// </summary>
        [Test]
        public void NoBiasIsSharedAcrossSites()
        {
            var all = WeatherFixtures.Sites.Select(Verdicts).ToArray();
            double root = Math.Sqrt(all.Length);
            var misses = new List<string>();
            for (int k = 0; k < Stats.Length; k++)
            {
                double mean = all.Average(v => v[k].Z);
                int up = all.Count(v => v[k].Miss > 0);
                double low = -(Z99 + (Stats[k].Snowy < 0 ? SnowyMargin : 0)) / root, high = (Z99 + (Stats[k].Snowy > 0 ? SnowyMargin : 0)) / root;
                bool pass = mean >= low && mean <= high;
                string line = $"{Stats[k].Name}: mean z {mean:+0.00;-0.00} in [{low:0.00}, {high:+0.00}], game above the record at {up} of {all.Length}{(pass ? "" : "  FAIL")}";
                TestContext.Progress.WriteLine(line);
                if (!pass) misses.Add(line);
            }
            Assert.That(misses, Is.Empty, "a bias shared by the sites");
        }

        /// <summary>
        /// The independent check: rows calibrated on the odd winters judged against the even ones, then the other way round.
        /// The yardstick is the record itself: the fitted half's own winters judged against the unseen half (the two halves'
        /// luck). An honest model adds its own small miss on the fitted half to that, and two independent misses add in
        /// quadrature, so its typical miss on unseen winters should be about √(yardstick² + fitted miss²). Over-fitting (rows
        /// that carry the fitted winters' luck further than the winters themselves) pushes it above that. The gate allows the
        /// sampling noise of the median itself: twice its bootstrap spread over the statistics (keyed, so it is the same every
        /// run). Printed per statistic; gated on the medians over both halves.
        /// </summary>
        [Category("HoldOut")]
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void HoldOut_TheRowsPredictWintersTheyNeverSaw(string site)
        {
            var screened = WeatherFixtures.Screened(site); var s = screened.Record.Site;
            var points = new ReferencePoints(s.BaseFeet, s.SummitFeet);
            var frozen = WeatherRowsFile.FromBytes(TestData.Bytes(TestData.Folder("weather/" + site), WeatherRowsFile.FileName));
            var years = RecordYears(screened.Blend, frozen).OrderBy(y => y.Year).ToList();
            var inZ = new List<double>(); var outZ = new List<double>(); var baseZ = new List<double>();
            for (int half = 0; half < 2; half++)
            {
                var train = years.Where((y, i) => i % 2 == half).ToList(); var test = years.Where((y, i) => i % 2 != half).ToList();
                var b = screened.Blend;
                foreach (var y in test) b = b.WithoutLabelYear(y.Year);
                var rows = WeatherCalibration.Build(b, screened.Record, points, WeatherTuning.Default).Rows;
                var game = PoolOf(GameYears(rows, HoldOutSeeds));
                Pool pin = PoolOf(train), pout = PoolOf(test);
                var rin = Resample(train, HoldOutDraws, 0x401D_0000_0000_0000UL + (ulong)half);
                var rout = Resample(test, HoldOutDraws, 0x401D_0001_0000_0000UL + (ulong)half);
                for (int k = 0; k < Stats.Length; k++)
                {
                    var a = Judge(k, game, pin, rin[k]); var c = Judge(k, game, pout, rout[k]); var e = Judge(k, pin, pout, rout[k]);
                    if (!double.IsNaN(a.Z)) inZ.Add(Math.Abs(a.Z));
                    if (!double.IsNaN(c.Z)) outZ.Add(Math.Abs(c.Z));
                    if (!double.IsNaN(e.Z)) baseZ.Add(Math.Abs(e.Z));
                    TestContext.Progress.WriteLine($"{site} {(half == 0 ? "odd → even" : "even → odd")}: {Stats[k].Name}: z in {a.Z:+0.0;-0.0}, out {c.Z:+0.0;-0.0}, record half → half {e.Z:+0.0;-0.0}");
                }
            }
            double medIn = Median(inZ), medOut = Median(outZ), medBase = Median(baseZ);
            double expected = Math.Sqrt(medBase * medBase + medIn * medIn), allowed = expected + 2 * MedianSpread(outZ, 0x401D_5EED_0000_0000UL);
            TestContext.Progress.WriteLine($"{site}: typical |z| fitted winters {medIn:F2}, unseen winters {medOut:F2}; the halves' own luck {medBase:F2}, " +
                                           $"so an honest model about {expected:F2} (allowed {allowed:F2})");
            Assert.That(medOut, Is.LessThanOrEqualTo(allowed), $"{site}: the rows miss unseen winters by more than an honest model would (over-fitting)");
        }

        // ---------------- Small statistics ----------------

        static double Quantile(List<double> values, double p) => SortedQuantile(values.OrderBy(v => v).ToList(), p);

        static double SortedQuantile(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return double.NaN;
            double h = Math.Max(0, Math.Min(sorted.Count - 1, p * sorted.Count - 0.5));
            int lo = (int)Math.Floor(h), hi = Math.Min(sorted.Count - 1, lo + 1);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (h - lo);
        }

        static double Median(List<double> v) => Quantile(v, 0.5);

        /// <summary>The bootstrap standard deviation of a list's median (keyed draws).</summary>
        static double MedianSpread(List<double> v, ulong key)
        {
            var m = new List<double>();
            for (int r = 0; r < 1000; r++)
            {
                var pick = new List<double>(v.Count);
                for (int i = 0; i < v.Count; i++) { key = key * 6364136223846793005UL + 1442695040888963407UL; pick.Add(v[(int)((key >> 33) % (ulong)v.Count)]); }
                m.Add(Median(pick));
            }
            return Sd(m);
        }
        static double Sd(List<double> v) { double m = v.Average(); return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / Math.Max(1, v.Count - 1)); }
        static double Cv(List<double> v) => Sd(v) / v.Average();

        static double VarianceOverMean(List<int> counts)
        {
            double mean = counts.Average();
            double variance = counts.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, counts.Count - 1);
            return mean > 0 ? variance / mean : 0;
        }

        /// <summary>The correlation within thirds: each pair less its third's means.</summary>
        static double Within(List<(int Third, double X, double Y)> pairs)
        {
            double sxy = 0, sxx = 0, syy = 0;
            foreach (var g in pairs.GroupBy(p => p.Third))
            {
                if (g.Count() < 2) continue;
                double mx = g.Average(p => p.X), my = g.Average(p => p.Y);
                foreach (var p in g) { sxy += (p.X - mx) * (p.Y - my); sxx += (p.X - mx) * (p.X - mx); syy += (p.Y - my) * (p.Y - my); }
            }
            return sxy / Math.Sqrt(Math.Max(1e-12, sxx * syy));
        }

        /// <summary>The storm-to-storm temperature spread: the standard deviation of storms' temperatures about their third's mean.</summary>
        static double Within2(List<(int Third, double X, double Y)> pairs)
        {
            double ss = 0; int n = 0;
            foreach (var g in pairs.GroupBy(p => p.Third))
            {
                if (g.Count() < 2) continue;
                double my = g.Average(p => p.Y);
                foreach (var p in g) { ss += (p.Y - my) * (p.Y - my); n++; }
            }
            return Math.Sqrt(ss / Math.Max(1, n - 1));
        }
    }
}
