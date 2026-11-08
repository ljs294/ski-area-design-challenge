using System;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// LO-07 v2 (owner, 2026-11-08, task 4.03; design shape 11): a day's hours on the face clock, as seconds after local
    /// midnight: the opening, the last chair and the stop. The resort's own hours are the player's to choose, within daylight
    /// (TM-02 v2: the day rate runs from opening to stop, so a longer day plays longer); a lift may carry its own (4.11 reads
    /// them). Whole minutes only, opening before last chair, last chair no later than the stop.
    /// </summary>
    public readonly struct DayHours : IEquatable<DayHours>
    {
        public const int SecondsPerDay = 86_400;

        /// <summary>8:30 AM open, 4:15 PM last chair, 4:30 PM stop (LO-07, the HUD mockup).</summary>
        public static readonly DayHours Default = new DayHours(8 * 3600 + 30 * 60, 16 * 3600 + 15 * 60, 16 * 3600 + 30 * 60);

        public readonly int OpenSecond;
        public readonly int LastChairSecond;
        public readonly int StopSecond;

        public DayHours(int openSecond, int lastChairSecond, int stopSecond)
        {
            OpenSecond = openSecond;
            LastChairSecond = lastChairSecond;
            StopSecond = stopSecond;
        }

        /// <summary>Why these hours can't be a day's, or null.</summary>
        public string Problem
        {
            get
            {
                if (OpenSecond % 60 != 0 || LastChairSecond % 60 != 0 || StopSecond % 60 != 0) return "hours must be whole minutes";
                if (OpenSecond < 0 || StopSecond > SecondsPerDay) return "hours must fall within the day";
                if (!(OpenSecond < LastChairSecond && LastChairSecond <= StopSecond)) return "the opening must come before the last chair, and the last chair no later than the stop";
                return null;
            }
        }

        public bool Equals(DayHours o) => OpenSecond == o.OpenSecond && LastChairSecond == o.LastChairSecond && StopSecond == o.StopSecond;
        public override bool Equals(object obj) => obj is DayHours o && Equals(o);
        public override int GetHashCode() => (OpenSecond * 397 ^ LastChairSecond) * 397 ^ StopSecond;
        public static bool operator ==(DayHours a, DayHours b) => a.Equals(b);
        public static bool operator !=(DayHours a, DayHours b) => !a.Equals(b);
        public override string ToString() => $"{Clock(OpenSecond)}–{Clock(StopSecond)} (last chair {Clock(LastChairSecond)})";

        static string Clock(int s) => $"{s / 3600:D2}:{s / 60 % 60:D2}";
    }

    /// <summary>LO-09 (owner, 2026-11-07; design shape 11): when a lift runs. Values are saved: never renumber.</summary>
    public enum LiftSchedule : byte
    {
        /// <summary>Every operating day.</summary>
        Daily = 1,
        /// <summary>Weekend and holiday day types only (TM-07, TM-09); parked on weekdays.</summary>
        PeakDays = 2,
    }
}
