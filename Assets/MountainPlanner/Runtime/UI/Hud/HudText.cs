using System;
using System.Collections.Generic;
using MountainPlanner.Simulation;

namespace MountainPlanner.UI.Hud
{
    /// <summary>
    /// The bar's words for the time and the date, each made once and kept (task P2-02), so the running clock allocates
    /// nothing however fast it goes (0.3 §8): "10:30" and ":47" for every minute and second, "AM"/"PM", "Day 137" and
    /// "Thu, Jan 15".
    /// The game's calendar is the mockup's: day 1 is 1 September, so a season's days run from September into the next
    /// year. Dates carry no year.
    /// </summary>
    public static class HudText
    {
        static readonly string[] Minutes12 = new string[24 * 60], Minutes24 = new string[24 * 60], SecondsText = new string[60];
        static readonly Dictionary<int, string> Days = new Dictionary<int, string>(), Dates = new Dictionary<int, string>();
        static readonly string[] WeekDays = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        public const string Am = "AM", Pm = "PM";

        /// <summary>"10:30" (12-hour, as the mockup: "05:27") or "10:30"/"17:05" (24-hour) for a second of the day.</summary>
        public static string HoursMinutes(int secondOfDay, bool twentyFour = false)
        {
            int minute = Math.Max(0, Math.Min(24 * 60 - 1, secondOfDay / 60));
            var table = twentyFour ? Minutes24 : Minutes12;
            if (table[minute] == null)
            {
                int h = minute / 60, m = minute % 60;
                table[minute] = twentyFour ? $"{h:D2}:{m:D2}" : $"{(h + 11) % 12 + 1:D2}:{m:D2}";
            }
            return table[minute];
        }

        /// <summary>Task 4.11 PR B: an hour of the day as the mockup's hhmm writes it, "9:00 AM", "3:45 PM" (or "15:45").</summary>
        public static string ClockOfDay(int secondOfDay, bool twentyFour = false)
        {
            int minute = Math.Max(0, Math.Min(24 * 60 - 1, secondOfDay / 60));
            int h = minute / 60, m = minute % 60;
            return twentyFour ? $"{h:D2}:{m:D2}" : $"{(h + 11) % 12 + 1}:{m:D2} {(h < 12 ? Am : Pm)}";
        }

        /// <summary>LO-04 v1: a wait as m:ss ("7:30"), or h:mm:ss past an hour.</summary>
        public static string Clock(double seconds)
        {
            int s = (int)Math.Max(0, Math.Round(seconds));
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:D2}:{s % 60:D2}" : $"{s / 60}:{s % 60:D2}";
        }

        /// <summary>":47"</summary>
        public static string Seconds(int secondOfDay)
        {
            int s = ((secondOfDay % 60) + 60) % 60;
            return SecondsText[s] ??= $":{s:D2}";
        }

        public static string AmPm(int secondOfDay) => secondOfDay < 12 * 3600 ? Am : Pm;

        static readonly Dictionary<int, string> SimDates = new Dictionary<int, string>();
        static readonly string[] ThirdWords = { "", "early", "mid", "late" };
        static readonly string[] DayTypeWords = { "", "Weekday", "Weekend", "Holiday" };
        public const string PreSeason = "Pre-season", SummerText = "Summer";

        /// <summary>The bar's day cell from the simulation (TM-08, TM-14): "Day 12" in the season, else "Pre-season" or "Summer".</summary>
        public static string SimDay(SeasonPhase phase, int seasonDay) =>
            phase == SeasonPhase.Season ? Day(seasonDay) : phase == SeasonPhase.Summer ? SummerText : PreSeason;

        /// <summary>The bar's date cell from the label calendar (TM-07, TM-09): "mid-Dec · Weekend".</summary>
        public static string SimDate(int month, MonthThird third, DayType type)
        {
            int key = month * 100 + (int)third * 10 + (int)type;
            if (SimDates.TryGetValue(key, out var text)) return text;
            int m = Math.Max(1, Math.Min(12, month));
            text = $"{ThirdWords[(int)third]}-{Months[m - 1]} · {DayTypeWords[(int)type]}";
            SimDates[key] = text;
            return text;
        }

        /// <summary>The season's day: 1 on 1 September, counting on through the winter.</summary>
        public static int SeasonDay(int year, int dayOfYear)
        {
            int sep1 = DayOfYear(year, 9, 1);
            return dayOfYear >= sep1 ? dayOfYear - sep1 + 1 : ViewTime.DaysInYear(year - 1) - DayOfYear(year - 1, 9, 1) + 1 + dayOfYear;
        }

        /// <summary>"Day 137"</summary>
        public static string Day(int seasonDay)
        {
            if (!Days.TryGetValue(seasonDay, out var text)) Days[seasonDay] = text = "Day " + seasonDay;
            return text;
        }

        /// <summary>"Thu, Jan 15"</summary>
        public static string Date(int year, int dayOfYear)
        {
            int key = year * 400 + dayOfYear;
            if (Dates.TryGetValue(key, out var text)) return text;
            int month = 1, day = dayOfYear;
            while (day > DaysInMonth(year, month)) day -= DaysInMonth(year, month++);
            text = $"{WeekDays[WeekDay(year, dayOfYear)]}, {Months[month - 1]} {day}";
            Dates[key] = text;
            return text;
        }

        static int DaysInMonth(int year, int month) => month == 2 ? (ViewTime.IsLeapYear(year) ? 29 : 28) : month == 4 || month == 6 || month == 9 || month == 11 ? 30 : 31;

        static int DayOfYear(int year, int month, int day)
        {
            int n = day;
            for (int m = 1; m < month; m++) n += DaysInMonth(year, m);
            return n;
        }

        /// <summary>0 = Sunday, by the Gregorian calendar (1 January 2026 was a Thursday).</summary>
        static int WeekDay(int year, int dayOfYear)
        {
            int y = year - 1;
            long days = 365L * y + y / 4 - y / 100 + y / 400 + dayOfYear;   // days since 31 Dec of year 0 (a Sunday, proleptic)
            return (int)(days % 7);
        }
    }
}
