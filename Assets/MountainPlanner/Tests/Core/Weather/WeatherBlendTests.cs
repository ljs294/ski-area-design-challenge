using System;
using System.Linq;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Domain.Weather;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Engine-free (tools/domain-tests). Task 4.05: WX-15 v2's blend and WX-55 v2's checks, on both recorded sites.
    public sealed class WeatherBlendTests
    {
        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX55_BlendedDayMatchesDaymet(string site)
        {
            var b = WeatherFixtures.Screened(site).Blend;
            int days = 0;
            for (int d = 0; d < b.DayCount; d++)
            {
                if (b.DayMissing(d)) continue;
                days++;
                float hi = float.MinValue, lo = float.MaxValue;
                double vapour = 0;
                for (int h = 0; h < 24; h++)
                {
                    int k = d * 24 + h;
                    hi = Math.Max(hi, b.T[k]);
                    lo = Math.Min(lo, b.T[k]);
                    vapour += Psychrometrics.SaturationPa(b.Td[k]);
                    Assert.That(b.Td[k], Is.LessThanOrEqualTo(b.T[k] + 1e-4f), "no hour's dew point is above its temperature");
                }
                Assert.That(Math.Abs(hi - b.Tmax[d]) * 1.8, Is.LessThanOrEqualTo(0.1), $"day {d}: max");
                Assert.That(Math.Abs(lo - b.Tmin[d]) * 1.8, Is.LessThanOrEqualTo(0.1), $"day {d}: min");
                // A day whose vapour pressure is above saturation at every hour's capped dew point can't reach it: a few
                // Daymet days do, and those reach as close as the cap allows (counted below, never more than 0.5 %).
                if (Math.Abs(vapour / 24 / b.Vp[d] - 1) > 0.01) Assert.That(SaturatedDay(b, d), $"day {d}: mean vapour pressure");
            }
            Assert.That(days, Is.GreaterThan(7000));
        }

        static bool SaturatedDay(BlendedRecord b, int d)
        {
            double cap = 0;
            for (int h = 0; h < 24; h++) cap += Psychrometrics.SaturationPa(b.T[d * 24 + h]);
            return cap / 24 < b.Vp[d] * 1.01;
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX55_LiquidTotalsMatchDaymet(string site)
        {
            var b = WeatherFixtures.Screened(site).Blend;
            for (int d = 0; d + 1 < b.DayCount; d++)
            {
                if (b.DayMissing(d)) continue;
                double sum = 0;
                for (int h = 0; h < 24; h++) sum += b.P[d * 24 + b.LagHours + h];   // the lagged windows tile the hours
                Assert.That(Math.Abs(sum - b.Prcp[d]) / 25.4, Is.LessThanOrEqualTo(0.01), $"day {d} at lag {b.LagHours}");
            }
            Assert.That(WeatherBlend.Lags, Does.Contain(b.LagHours));
            TestContext.WriteLine($"{site}: lag {b.LagHours} h; r " + string.Join(", ", b.LagCorrelations.Select(kv => $"{kv.Key}: {kv.Value:F3}")) +
                                  $"; fallback shapes {b.FallbackShapeDays}, flat dew {b.FlatDewDays}; wet days shared by humidity {b.HumidShareDays}, evenly {b.EvenShareDays}");
        }

        [Test]
        public void WX15_LeapYearsAlignByDate()
        {
            var b = WeatherFixtures.Screened(WeatherFixtures.JacksonHole).Blend;
            Assert.That(b.Days.Any(CivilDay.IsLeapDay), Is.False, "February 29 is dropped from both sources");
            Assert.That(b.DayCount, Is.EqualTo(365 * 20), "20 label years of 365 days");
            // December 31 of a leap year (Daymet drops it) is filled from December 30.
            int d31 = Array.IndexOf(b.Days, CivilDay.Of(2008, 12, 31)), d30 = Array.IndexOf(b.Days, CivilDay.Of(2008, 12, 30));
            Assert.That(d31, Is.GreaterThan(0));
            Assert.That(b.Tmax[d31], Is.EqualTo(b.Tmax[d30]));
            // March 1 follows February 28 in a leap year, and its Daymet values are March 1's (aligned by date, not index).
            var raw = WeatherFixtures.Screened(WeatherFixtures.JacksonHole).Record.Reference;
            int mar1 = Array.IndexOf(b.Days, CivilDay.Of(2008, 3, 1));
            Assert.That(b.Days[mar1 - 1], Is.EqualTo(CivilDay.Of(2008, 2, 28)));
            Assert.That(b.Tmax[mar1], Is.EqualTo(raw.Series("tmax")[raw.Days.IndexOf(CivilDay.Of(2008, 3, 1))]));
        }
    }
}
