using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// A moment in the viewing calendar: year, day of year (1-based) and second of day. Unity's .NET
    /// profile has no <c>DateOnly</c>, and wall-clock types are banned here, so this stays a plain
    /// value (docs/plans/phase0-0.3-technical-architecture.md §10).
    /// </summary>
    public readonly struct ViewTime : IEquatable<ViewTime>
    {
        public const int SecondsPerDay = 86_400;

        public readonly int Year;
        public readonly int DayOfYear;
        public readonly int SecondOfDay;

        public ViewTime(int year, int dayOfYear, int secondOfDay)
        {
            if (year < 1 || year > 9999) throw new ArgumentOutOfRangeException(nameof(year), year, "Year must be 1-9999.");
            if (dayOfYear < 1 || dayOfYear > DaysInYear(year))
                throw new ArgumentOutOfRangeException(nameof(dayOfYear), dayOfYear, $"Day must be 1-{DaysInYear(year)} in {year}.");
            if (secondOfDay < 0 || secondOfDay >= SecondsPerDay)
                throw new ArgumentOutOfRangeException(nameof(secondOfDay), secondOfDay, "Second must be 0-86399.");
            Year = year;
            DayOfYear = dayOfYear;
            SecondOfDay = secondOfDay;
        }

        /// <summary>False only for <c>default(ViewTime)</c>; every constructed value is valid.</summary>
        public bool IsValid => DayOfYear != 0;

        public static bool IsLeapYear(int year) => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

        public static int DaysInYear(int year) => IsLeapYear(year) ? 366 : 365;

        /// <summary>The same day at another time; <paramref name="secondOfDay"/> must be 0-86399.</summary>
        public ViewTime WithSecondOfDay(int secondOfDay) => new ViewTime(Year, DayOfYear, secondOfDay);

        public bool Equals(ViewTime other) =>
            Year == other.Year && DayOfYear == other.DayOfYear && SecondOfDay == other.SecondOfDay;

        public override bool Equals(object obj) => obj is ViewTime other && Equals(other);

        public override int GetHashCode() => (Year * 367 + DayOfYear) * SecondsPerDay + SecondOfDay;

        public static bool operator ==(ViewTime a, ViewTime b) => a.Equals(b);

        public static bool operator !=(ViewTime a, ViewTime b) => !a.Equals(b);

        public override string ToString() =>
            $"{Year:D4}-{DayOfYear:D3} {SecondOfDay / 3600:D2}:{SecondOfDay / 60 % 60:D2}:{SecondOfDay % 60:D2}";
    }
}
