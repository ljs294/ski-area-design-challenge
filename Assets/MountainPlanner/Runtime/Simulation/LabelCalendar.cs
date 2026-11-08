using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// TM-07, TM-08 v1, TM-09 and TM-14: the label calendar. A label year runs from October 1 and has no leap day; December 1
    /// to April 1 (121 calendar days) is 42 face days, about 2.88 calendar days each, and the same rate runs through the
    /// rest of the year, so October and November are 21 face days and a label year is <see cref="YearFaceDays"/> (127 at the
    /// defaults). Face day <c>j</c> of a label year starts on calendar day
    /// <c>61 + floor((j − PreSeasonFaceDays) × SeasonCalendarDays / SeasonDays)</c> after October 1. Weeks run weekday,
    /// weekday, weekend from October 1 (they follow the calendar, not the season's opening); a holiday marks the face day
    /// holding its date and the next ones it asks for. Every answer is precomputed whole numbers.
    /// </summary>
    public sealed class LabelCalendar
    {
        /// <summary>Calendar days after October 1 that each month starts, October first (365-day year).</summary>
        static readonly int[] MonthStart = { 0, 31, 61, 92, 123, 151, 182, 212, 243, 273, 304, 335, 365 };
        /// <summary>The calendar month of each label month, October first.</summary>
        static readonly int[] MonthNumber = { 10, 11, 12, 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        /// <summary>December 1, 61 calendar days after October 1.</summary>
        public const int December1 = 61;

        readonly int[] _offset;      // calendar day after October 1 that face day j starts on; one extra entry for the end
        readonly byte[] _monthIndex; // label month of face day j (0 = October)
        readonly byte[] _day;        // its first calendar day of the month
        readonly DayType[] _type;

        /// <summary>Face days in a label year.</summary>
        public int YearFaceDays { get; }
        /// <summary>The face day of the label year that starts on December 1 (21 at the defaults).</summary>
        public int December1FaceDay { get; }
        /// <summary>The first face day starting on or after April 1 (63 at the defaults).</summary>
        public int April1FaceDay { get; }

        public LabelCalendar(Pacing p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            December1FaceDay = p.PreSeasonFaceDays;
            int days = 0;
            while (Offset(days, p) < 365) days++;
            YearFaceDays = days;
            if (Offset(0, p) != 0) throw new FormatException("Pacing: PreSeasonFaceDays must put face day 0 on October 1.");
            _offset = new int[days + 1];
            _monthIndex = new byte[days];
            _day = new byte[days];
            _type = new DayType[days];
            for (int j = 0; j <= days; j++) _offset[j] = j < days ? Offset(j, p) : 365;
            April1FaceDay = -1;
            for (int j = 0; j < days; j++)
            {
                int off = _offset[j];
                int m = 0;
                while (MonthStart[m + 1] <= off) m++;
                _monthIndex[j] = (byte)m;
                _day[j] = (byte)(off - MonthStart[m] + 1);
                _type[j] = p.Week[j % p.Week.Length];
                if (April1FaceDay < 0 && off >= MonthStart[6]) April1FaceDay = j;
            }
            foreach (var h in p.Holidays)
            {
                int m = Array.IndexOf(MonthNumber, h.Month);
                int off = MonthStart[m] + h.Day - 1;
                int j = FaceDayHolding(off);
                for (int k = 0; k < h.FaceDays && j + k < days; k++) _type[j + k] = DayType.Holiday;
            }
        }

        static int Offset(int j, Pacing p) => December1 + FloorDiv((long)(j - p.PreSeasonFaceDays) * p.SeasonCalendarDays, p.SeasonDays);

        static int FloorDiv(long a, long b) => (int)(a >= 0 ? a / b : -((-a + b - 1) / b));

        /// <summary>The face day whose calendar span holds <paramref name="calendarOffset"/> (days after October 1).</summary>
        public int FaceDayHolding(int calendarOffset)
        {
            int j = 0;
            while (j + 1 < YearFaceDays && _offset[j + 1] <= calendarOffset) j++;
            return j;
        }

        /// <summary>The calendar day after October 1 that face day <paramref name="j"/> starts on.</summary>
        public int CalendarOffset(int j) => _offset[j];
        /// <summary>The label month of face day j, 0 = October … 11 = September.</summary>
        public int MonthIndex(int j) => _monthIndex[j];
        /// <summary>The calendar month (1–12) of face day j's first date.</summary>
        public int Month(int j) => MonthNumber[_monthIndex[j]];
        /// <summary>The day of the month of face day j's first date.</summary>
        public int Day(int j) => _day[j];
        /// <summary>Early (1–10), mid (11–20) or late (21–) in the month, by face day j's first date.</summary>
        public MonthThird Third(int j) => _day[j] <= 10 ? MonthThird.Early : _day[j] <= 20 ? MonthThird.Mid : MonthThird.Late;
        public DayType TypeOf(int j) => _type[j];

        /// <summary>The first face day after j in a later third of the month, or <see cref="YearFaceDays"/> (next October 1).</summary>
        public int NextThirdStart(int j)
        {
            int m = _monthIndex[j];
            var t = Third(j);
            for (int k = j + 1; k < YearFaceDays; k++) if (_monthIndex[k] != m || Third(k) != t) return k;
            return YearFaceDays;
        }

        /// <summary>The first face day after j in a later month, or <see cref="YearFaceDays"/> (next October 1).</summary>
        public int NextMonthStart(int j)
        {
            int m = _monthIndex[j];
            for (int k = j + 1; k < YearFaceDays; k++) if (_monthIndex[k] != m) return k;
            return YearFaceDays;
        }

        /// <summary>The calendar year of face day j's first date in label year <paramref name="labelYear"/> (October's year).</summary>
        public int CalendarYear(int labelYear, int j) => _monthIndex[j] <= 2 ? labelYear : labelYear + 1;

        /// <summary>
        /// Face day j's first date as a day of the real calendar year (the sun's date, WX-04). The label has no leap day, so
        /// in a leap year a date from March on moves one day later and February 29 never shows.
        /// </summary>
        public int DayOfYear(int labelYear, int j)
        {
            int year = CalendarYear(labelYear, j);
            int month = Month(j);
            int doy = Day(j);
            for (int m = 1; m < month; m++) doy += DaysInMonth(m);
            if (month > 2 && ViewTime.IsLeapYear(year)) doy++;
            return doy;
        }

        /// <summary>Days in a month of the 365-day label year.</summary>
        public static int DaysInMonth(int month) =>
            month == 2 ? 28 : month == 4 || month == 6 || month == 9 || month == 11 ? 30 : 31;
    }

    /// <summary>TM-08: months in thirds (the weather's tuning rows, WX-07). Values are saved: never renumber.</summary>
    public enum MonthThird : byte { Early = 1, Mid = 2, Late = 3 }
}
