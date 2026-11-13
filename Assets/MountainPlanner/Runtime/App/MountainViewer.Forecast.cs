using System;
using System.Text;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using MountainPlanner.UI.Hud;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.05 PR C's light touch on screen (owner, 2026-11-13): the weather line under the clock (now at base · summit, and
    /// the forecast's snow in the next 24 hours), toasts for the alerts (WX-42), the named events (WX-41) and the wind holds
    /// (LO-11), and the evening sheet in the dev readout. The real weather bug and the Weather tab are 4.06's and 4.17's.
    /// Everything reads the newest snapshot at the HUD's bounded rate; text is made only when what it says changes.
    /// </summary>
    public sealed partial class MountainViewer
    {
        int _wxLineBase = int.MinValue, _wxLineSummit = int.MinValue, _wxLineSnow = int.MinValue;
        UnitSystem _wxLineUnits = (UnitSystem)(-1);

        /// <summary>The bar's weather line (10 Hz, from <see cref="SetHudClock"/>): rebuilt only when a shown figure moves.</summary>
        void SetHudWeather()
        {
            var s = _simNow;
            if (Hud == null || s == null || !s.WeatherKnown) return;
            var units = DisplayUnits.Current;
            bool metric = units == UnitSystem.Metric;
            int b = HudText.WholeDegrees(s.WeatherBase.Temp, metric), t = HudText.WholeDegrees(s.WeatherSummit.Temp, metric);
            long hour = s.FaceMs / SimWorld.HourMs;
            int snow = s.Forecast.Known ? HudText.WholeSnow(s.Forecast.SnowNext(hour + 1, 24, 1), metric) : -1;
            if (b == _wxLineBase && t == _wxLineSummit && snow == _wxLineSnow && units == _wxLineUnits) return;
            _wxLineBase = b; _wxLineSummit = t; _wxLineSnow = snow; _wxLineUnits = units;
            Hud.SetWeather(HudText.WeatherLine(b, t, snow, metric), HudText.WeatherTip(b, t, snow, metric));
        }

        static readonly Color AlertStripe = new Color(0.86f, 0.62f, 0.2f);
        static readonly Color WeatherStripe = new Color(0.45f, 0.62f, 0.8f);

        /// <summary>The weather's sim events as toasts (from <see cref="OnSimEvent"/>; a few a face day, so the words may allocate).</summary>
        bool OnWeatherEvent(SimEvent e)
        {
            var s = _simNow;
            switch (e.Type)
            {
                case SimEventType.WeatherAlert:
                {
                    var kind = (WeatherEventKind)e.Detail;
                    string when = WhenWords((int)e.Value, s);
                    string text = kind switch
                    {
                        WeatherEventKind.BigStorm => $"Big storm coming {when}",
                        WeatherEventKind.Rain => $"Rain coming {when}",
                        WeatherEventKind.FreezingRain => $"Freezing rain possible {when}",
                        WeatherEventKind.Thaw => $"Thaw coming {when}",
                        WeatherEventKind.ColdSnap => $"Cold snap coming {when}",
                        WeatherEventKind.WindEvent => $"High wind coming {when}{LiftsOverTheirHold(s)}",
                        _ => null,
                    };
                    if (text != null) Hud.Toast(text, AlertStripe);
                    return true;
                }
                case SimEventType.WeatherEventBegan:
                {
                    string text = (WeatherEventKind)e.Detail switch
                    {
                        WeatherEventKind.Storm => "It's snowing on the mountain",
                        WeatherEventKind.BigStorm => "A big storm is arriving",
                        WeatherEventKind.StormOfTheSeason => "The storm of the season",
                        WeatherEventKind.PowderDay => $"Powder day: {SnowWords((int)e.Value)} overnight",
                        WeatherEventKind.Bluebird => "A bluebird day",
                        WeatherEventKind.Rain => "Rain at the base",
                        WeatherEventKind.FreezingRain => "Freezing rain at the base",
                        WeatherEventKind.Thaw => "A thaw: the snow is going soft",
                        WeatherEventKind.Refreeze => "A refreeze: the soft snow is setting up icy",
                        WeatherEventKind.ColdSnap => "A cold snap",
                        WeatherEventKind.WindEvent => "High wind at the summit",
                        _ => null,   // an inversion is a tag on the hourly strip (WX-46), not news
                    };
                    if (text != null) Hud.Toast(text, WeatherStripe);
                    return true;
                }
                case SimEventType.LiftHeld when s != null:
                    Hud.Toast($"{SnapshotLiftName(s, e.Entity)} is on wind hold: gusts {e.Value} mph", AlertStripe);
                    return true;
                case SimEventType.LiftResumed when s != null:
                    Hud.Toast($"{SnapshotLiftName(s, e.Entity)} is running again", WeatherStripe);
                    return true;
            }
            return false;
        }

        static string WhenWords(int faceDay, SimSnapshot s)
        {
            int ahead = s == null ? 2 : faceDay - s.FaceDay;
            return ahead <= 0 ? "today" : ahead == 1 ? "tomorrow" : $"in {ahead} days";
        }

        static string SnowWords(int tenthsIn)
        {
            var units = DisplayUnits.Current;
            return units == UnitSystem.Imperial ? $"{tenthsIn / 10.0:0.#} in" : $"{tenthsIn * 0.254:0} cm";
        }

        string SnapshotLiftName(SimSnapshot s, int index)
        {
            if (index < 0 || index >= s.LiftCount) return "A lift";
            return Design?.Current.Document.FindLift(new LiftId(s.LiftIds[index]))?.Name ?? "A lift";
        }

        /// <summary>The lifts whose top station's forecast gust tomorrow reaches their hold line (WX-42's named lifts).</summary>
        string LiftsOverTheirHold(SimSnapshot s)
        {
            if (s == null || !s.Forecast.Known) return "";
            var names = new StringBuilder();
            int n = 0;
            for (int i = 0; i < s.LiftCount && i < s.Forecast.LiftGustTenths.Length; i++)
            {
                if (!_holdMph.TryGetValue(s.LiftIds[i], out int hold) || hold < 0) continue;
                if (s.LiftStatus[i] == LiftOpStatus.Planned || s.Forecast.LiftGustTenths[i] / 10 < hold) continue;
                if (n++ > 0) names.Append(", ");
                names.Append(SnapshotLiftName(s, i));
            }
            return n == 0 ? "" : $": {names} may hold";
        }

        /// <summary>Whether tomorrow's forecast gust at the lift's top reaches its hold line (the card's "Holds likely").</summary>
        static bool HoldLikely(SimSnapshot s, int i, int holdMph) =>
            holdMph >= 0 && s.Forecast.Known && i < s.Forecast.LiftGustTenths.Length && s.Forecast.LiftGustTenths[i] / 10 >= holdMph;

        // ---------- the dev readout's evening sheet ----------

        static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        void ForecastReadout(StringBuilder t, SimSnapshot s)
        {
            var f = s.Forecast;
            t.Append("\n\nForecast: ");
            if (!f.Known) { t.Append("issued at the stop (4:30 PM)"); return; }
            t.Append(f.Evening ? "the evening sheet of day " : "the morning refresh of day ").Append(f.IssueFaceDay);
            t.Append($" · tonight's gun window (hours ≤ 27 °F wet bulb) base {f.GunWindowHours[0]}, mid {f.GunWindowHours[1]}, summit {f.GunWindowHours[2]}");
            t.Append($" · snow tonight {f.TonightSnow[1] / 10.0:F1} in, tomorrow {f.TomorrowSnow[1] / 10.0:F1} in at mid · tomorrow's summit gust {f.TomorrowSummitGust / 10} mph");
            if (f.NextThawDay >= 0) t.Append($" · next thaw day {f.NextThawDay}");
            if (f.Outlook != ForecastOutlook.None)
                t.Append(f.Outlook == ForecastOutlook.Active
                    ? (f.OutlookFirm ? "\nActive pattern: storms lining up" : "\nAn active pattern likely later in the week")
                    : (f.OutlookFirm ? "\nA ridge is building: dry and settled" : "\nA ridge likely later in the week"));
            for (int k = 0; k < f.DayCount; k++)
            {
                var c = f.Cards[k];
                t.Append($"\n  day {c.FaceDay}: {Words(c.Words)} {c.ChancePermille / 10}% · base {Range(c.HighBaseLo, c.HighBaseHi, c.LeadHours)}/{Range(c.LowBaseLo, c.LowBaseHi, c.LeadHours)} °F · summit {Range(c.HighSummitLo, c.HighSummitHi, c.LeadHours)}/{Range(c.LowSummitLo, c.LowSummitHi, c.LeadHours)} °F");
                if (c.SnowSummitHi > 0) t.Append($" · snow {Inches(c.SnowSummitLo)}–{Inches(c.SnowSummitHi)} in up top, {Inches(c.SnowBaseLo)}–{Inches(c.SnowBaseHi)} at the base");
                if (c.SnowLineFeet > 0) t.Append($" · snow line {c.SnowLineFeet:N0} ft");
                t.Append($" · summit wind {c.WindSummit / 10} mph, gusts {c.GustSummit / 10}");
                if (c.StormFirm && c.StormOctant >= 0) t.Append($" · from the {Octant(c.StormOctant)}{(c.TurnOctant != c.StormOctant && c.TurnOctant >= 0 ? $", turning {Octant(c.TurnOctant)}" : "")}");
                if (c.Expected != 0)
                {
                    t.Append(" · expects");
                    foreach (WeatherEventKind kind in Enum.GetValues(typeof(WeatherEventKind)))
                        if (kind != WeatherEventKind.None && c.Expects(kind)) t.Append(' ').Append(kind);
                }
            }
            t.Append($"\nThe crowd's weather lines today: powder ×{s.PowderPull / 1000.0:F2}, forecast ×{s.ForecastPull / 1000.0:F2}");
            if (s.OvernightSnowMid > 0) t.Append($" · {s.OvernightSnowMid / 10.0:F1} in overnight at mid");
            if (s.WeatherEvents != 0)
            {
                t.Append(" · under way:");
                foreach (WeatherEventKind kind in Enum.GetValues(typeof(WeatherEventKind)))
                    if (kind != WeatherEventKind.None && (s.WeatherEvents & (1 << (int)kind)) != 0) t.Append(' ').Append(kind);
            }
        }

        /// <summary>Snow from 0.1 in: tenths under 10 in ("0.4"), whole inches above ("14").</summary>
        static string Inches(int tenths) => tenths < 100 ? (tenths / 10.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : (tenths / 10).ToString();

        static string Range(int lo, int hi, int leadHours) =>
            leadHours <= 36 ? $"{(lo + hi) / 20}" : $"{lo / 10}–{hi / 10}";

        static string Octant(int o) => o switch { 0 => "north", 1 => "northeast", 2 => "east", 3 => "southeast", 4 => "south", 5 => "southwest", 6 => "west", _ => "northwest" };

        static string Words(ForecastWords w) => w switch
        {
            ForecastWords.Fair => "fair",
            ForecastWords.Unsettled => "unsettled",
            ForecastWords.SnowPossible => "unsettled, snow possible",
            ForecastWords.SnowLikely => "snow likely",
            ForecastWords.Snow => "snow",
            ForecastWords.RainPossible => "unsettled, rain possible",
            ForecastWords.RainLikely => "rain likely",
            ForecastWords.Rain => "rain",
            ForecastWords.Mixed => "rain and snow",
            _ => "—",
        };
    }
}
