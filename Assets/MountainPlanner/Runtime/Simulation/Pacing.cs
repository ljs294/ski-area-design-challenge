using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SI-01 v1 (owner, 2026-11-07): every pacing number, named, in one versioned table. Rules cite these by name; changing
    /// one is a tuning, never a rule change. The table's contents hash (<see cref="ContentsHash"/>) is in every replay header
    /// and golden (SI-08, SI-22), so a tuning never silently moves a result. <c>-pacing &lt;file&gt;</c> loads an override of
    /// <c>Name = value</c> lines (<see cref="Parse"/>) for playtests. Immutable.
    /// </summary>
    public sealed class Pacing
    {
        /// <summary>The table's layout version: a new or renamed entry bumps it.</summary>
        public const int Version = 2;   // 2: task 4.11's lift operations (LO-02, LO-11)

        public static Pacing Default => _default ??= new Pacing(null);
        static Pacing _default;

        // ---- The tick and the clocks (SI-04, TM-01–TM-03) ----

        /// <summary>SI-04: real milliseconds per motion tick at 1×.</summary>
        public readonly int TickMs;
        /// <summary>TM-02 v2: real seconds a face hour takes from the resort's opening to its stop (90: a 12-minute 8:30–4:30 day).</summary>
        public readonly int DayHourRealSeconds;
        /// <summary>TM-02 v2: real seconds every other face hour takes (15: a 4-minute night at the default hours).</summary>
        public readonly int NightHourRealSeconds;
        /// <summary>TM-03: ticks per real tick at the four speeds, 1× to 8× (the bar's buttons and keys 1–4).</summary>
        public readonly int[] Speeds;
        /// <summary>SI-11 v2: motion ticks one idle step may cover, when no skier exists and no lift runs.</summary>
        public readonly int IdleTickFactor;
        /// <summary>SI-03 v2: a command issued against a snapshot more than this many ticks old is rejected.</summary>
        public readonly int StaleCommandTicks;
        /// <summary>The host's backlog limit, in real ticks: a machine further behind slows down honestly (SI-11).</summary>
        public readonly int MaxBacklogRealTicks;

        // ---- The calendar (TM-07–TM-09, TM-14) ----

        /// <summary>TM-08 v1: face days from December 1 to April 1.</summary>
        public readonly int SeasonDays;
        /// <summary>TM-08 v1: calendar days from December 1 to April 1 (no leap day).</summary>
        public readonly int SeasonCalendarDays;
        /// <summary>TM-14: face days from October 1 to December 1, at the season's rate.</summary>
        public readonly int PreSeasonFaceDays;
        /// <summary>TM-07: the week's day types, from October 1.</summary>
        public readonly DayType[] Week;
        /// <summary>TM-09: the holidays by calendar date, each with the face days it marks.</summary>
        public readonly Holiday[] Holidays;
        /// <summary>Owner, 2026-11-08 (plan question 8): Skip to morning lands this many face minutes before opening.</summary>
        public readonly int SkipLandsBeforeOpenMinutes;

        // ---- Motion (TM-04, TM-05; read from 4.11 and 4.12) ----

        public readonly int ChairSpeedFactor;
        public readonly int SkierSpeedFactor;
        public readonly int RideCompression;
        public readonly int LapsTarget;
        /// <summary>TM-08: the close nudge, as a percentage of built trails open.</summary>
        public readonly int CloseNudgeSharePercent;
        /// <summary>TM-11 v2: face days after the melt-out nudge with no lift serving a run before the season ends.</summary>
        public readonly int SeasonEndDays;

        // ---- Lift operations (LO-02, LO-11; task 4.11) ----

        /// <summary>LO-11 v2 (owner 2026-11-08): the base of the gust at the top station that holds a lift, mph, by kind:
        /// fixed-grip chairs, detachable chairs, gondolas, trams (tows and carpets never hold). A lift's limit is this plus
        /// <see cref="HoldGustPerSeatTenths"/> × its seats.</summary>
        public readonly int[] HoldGustMph;
        /// <summary>LO-11 v2: tenths of a mph each seat adds to the limit, by the same kinds (bigger, heavier chairs ride the
        /// wind better; a fixed grip gains more per seat than a detachable, so it stays ahead at the same size).</summary>
        public readonly int[] HoldGustPerSeatTenths;
        /// <summary>LO-11 v1: each lift's keyed gust factor is 1 ± this, in percent.</summary>
        public readonly int GustJitterPercent;
        /// <summary>LO-11 v1: a held lift resumes once the gust has been this far below its threshold for a full face hour.</summary>
        public readonly int HoldResumeMarginMph;
        /// <summary>LO-11 v2: how long the gust must stay that far below, face minutes (owner 2026-11-08: a half hour).</summary>
        public readonly int HoldResumeFaceMinutes;
        /// <summary>LO-11 v2: a held lift reads the gust this often, face minutes (a running lift reads it once an hour).</summary>
        public readonly int HoldRecheckFaceMinutes;
        /// <summary>LO-02: the most skiers one lift's line holds (its ring is allocated once per design).</summary>
        public readonly int MaxLine;
        /// <summary>SI-16: the most trails a snapshot holds (their open state).</summary>
        public readonly int MaxTrails;

        // ---- The boundary (SI-16–SI-19, BG-02) ----

        /// <summary>SI-18: the event ring's size (a power of two).</summary>
        public readonly int EventRingSize;
        /// <summary>SI-18: the newest events each snapshot carries.</summary>
        public readonly int SnapshotEvents;
        /// <summary>SI-16: the most lifts a snapshot holds (it stays within 64 KB).</summary>
        public readonly int MaxLifts;
        /// <summary>SI-17: a dirty tile is staged at most once this many ticks.</summary>
        public readonly int TileFlushTicks;
        /// <summary>SI-17: the most tiles staged in one tick.</summary>
        public readonly int StageTilesPerTick;
        /// <summary>BG-02 v3: the hour step finishes within this many ticks of the roll.</summary>
        public readonly int HourStepMaxTicks;
        /// <summary>TM-15: face days of history kept (a label year is fewer).</summary>
        public readonly int HistoryDays;

        /// <summary>Face milliseconds one tick covers by day and by night (from the real seconds above).</summary>
        public readonly int DayFaceMsPerTick, NightFaceMsPerTick;

        /// <summary>A keyed hash of the table's canonical text: in every replay header and golden (SI-08).</summary>
        public readonly ulong ContentsHash;

        /// <summary>The table as canonical <c>Name = value</c> lines, in declaration order.</summary>
        public readonly string CanonicalText;

        Pacing(IReadOnlyDictionary<string, string> overrides)
        {
            var values = new List<KeyValuePair<string, string>>(DefaultEntries);
            if (overrides != null)
                foreach (var kv in overrides)
                {
                    int i = values.FindIndex(e => e.Key == kv.Key);
                    if (i < 0) throw new FormatException($"'{kv.Key}' isn't a Pacing entry.");
                    values[i] = new KeyValuePair<string, string>(kv.Key, kv.Value.Trim());
                }
            var map = new Dictionary<string, string>();
            foreach (var kv in values) map[kv.Key] = kv.Value;
            TickMs = Int(map, nameof(TickMs), 1, 10_000);
            DayHourRealSeconds = Int(map, nameof(DayHourRealSeconds), 1, 36_000);
            NightHourRealSeconds = Int(map, nameof(NightHourRealSeconds), 1, 36_000);
            Speeds = Ints(map, nameof(Speeds), 1, 64);
            if (Speeds.Length != 4) throw new FormatException("Speeds needs four entries (the bar's 1–4).");
            IdleTickFactor = Int(map, nameof(IdleTickFactor), 1, 1000);
            StaleCommandTicks = Int(map, nameof(StaleCommandTicks), 1, 100_000);
            MaxBacklogRealTicks = Int(map, nameof(MaxBacklogRealTicks), 1, 100);
            SeasonDays = Int(map, nameof(SeasonDays), 1, 365);
            SeasonCalendarDays = Int(map, nameof(SeasonCalendarDays), 1, 365);
            PreSeasonFaceDays = Int(map, nameof(PreSeasonFaceDays), 0, 365);
            Week = DayTypes(map[nameof(Week)]);
            Holidays = HolidayList(map[nameof(Holidays)]);
            SkipLandsBeforeOpenMinutes = Int(map, nameof(SkipLandsBeforeOpenMinutes), 0, 600);
            ChairSpeedFactor = Int(map, nameof(ChairSpeedFactor), 1, 100);
            SkierSpeedFactor = Int(map, nameof(SkierSpeedFactor), 1, 100);
            RideCompression = Int(map, nameof(RideCompression), 1, 100);
            LapsTarget = Int(map, nameof(LapsTarget), 1, 100);
            CloseNudgeSharePercent = Int(map, nameof(CloseNudgeSharePercent), 0, 100);
            SeasonEndDays = Int(map, nameof(SeasonEndDays), 1, 365);
            HoldGustMph = Ints(map, nameof(HoldGustMph), 1, 500);
            if (HoldGustMph.Length != 4) throw new FormatException("HoldGustMph needs four entries (fixed grip, detachable, gondola, tram).");
            HoldGustPerSeatTenths = Ints(map, nameof(HoldGustPerSeatTenths), 0, 1000);
            if (HoldGustPerSeatTenths.Length != 4) throw new FormatException("HoldGustPerSeatTenths needs four entries (fixed grip, detachable, gondola, tram).");
            GustJitterPercent = Int(map, nameof(GustJitterPercent), 0, 50);
            HoldResumeMarginMph = Int(map, nameof(HoldResumeMarginMph), 0, 100);
            HoldResumeFaceMinutes = Int(map, nameof(HoldResumeFaceMinutes), 1, 24 * 60);
            HoldRecheckFaceMinutes = Int(map, nameof(HoldRecheckFaceMinutes), 1, 60);
            if (60 % HoldRecheckFaceMinutes != 0) throw new FormatException("HoldRecheckFaceMinutes must divide the hour.");
            MaxLine = Int(map, nameof(MaxLine), 16, 1 << 16);
            MaxTrails = Int(map, nameof(MaxTrails), 1, 8192);
            EventRingSize = Int(map, nameof(EventRingSize), 16, 1 << 20);
            if ((EventRingSize & (EventRingSize - 1)) != 0) throw new FormatException("EventRingSize must be a power of two.");
            SnapshotEvents = Int(map, nameof(SnapshotEvents), 1, EventRingSize);
            MaxLifts = Int(map, nameof(MaxLifts), 1, 1024);
            TileFlushTicks = Int(map, nameof(TileFlushTicks), 1, 10_000);
            StageTilesPerTick = Int(map, nameof(StageTilesPerTick), 1, 1024);
            HourStepMaxTicks = Int(map, nameof(HourStepMaxTicks), 1, 10_000);
            HistoryDays = Int(map, nameof(HistoryDays), 1, 4096);

            DayFaceMsPerTick = FaceMsPerTick(DayHourRealSeconds, nameof(DayHourRealSeconds));
            NightFaceMsPerTick = FaceMsPerTick(NightHourRealSeconds, nameof(NightHourRealSeconds));

            var text = new StringBuilder();
            text.Append("Pacing v").Append(Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var kv in values) text.Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
            CanonicalText = text.ToString();
            ContentsHash = Fnv64(Encoding.UTF8.GetBytes(CanonicalText));
        }

        /// <summary>The default table with <c>Name = value</c> lines replacing entries; blank lines and <c>#</c> comments are skipped.</summary>
        public static Pacing Parse(string overrideText)
        {
            var overrides = new Dictionary<string, string>();
            var lines = (overrideText ?? "").Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) throw new FormatException($"Pacing line {n + 1}: expected 'Name = value'.");
                string name = line.Substring(0, eq).Trim();
                if (overrides.ContainsKey(name)) throw new FormatException($"Pacing line {n + 1}: '{name}' is set twice.");
                overrides[name] = line.Substring(eq + 1).Trim();
            }
            return new Pacing(overrides);
        }

        /// <summary>A face hour's ticks by day or night: 900 and 150 at the defaults.</summary>
        public int TicksPerFaceHour(bool day) => 3_600_000 / (day ? DayFaceMsPerTick : NightFaceMsPerTick);

        int FaceMsPerTick(int hourRealSeconds, string name)
        {
            long ticksPerHour = hourRealSeconds * 1000L / TickMs;
            if (ticksPerHour * TickMs != hourRealSeconds * 1000L || 3_600_000 % ticksPerHour != 0)
                throw new FormatException($"{name} must give a whole number of ticks per face hour, each a whole number of face milliseconds.");
            return (int)(3_600_000 / ticksPerHour);
        }

        static readonly KeyValuePair<string, string>[] DefaultEntries =
        {
            E(nameof(TickMs), "100"),
            E(nameof(DayHourRealSeconds), "90"),
            E(nameof(NightHourRealSeconds), "15"),
            E(nameof(Speeds), "1, 2, 4, 8"),
            E(nameof(IdleTickFactor), "10"),
            E(nameof(StaleCommandTicks), "20"),
            E(nameof(MaxBacklogRealTicks), "3"),
            E(nameof(SeasonDays), "42"),
            E(nameof(SeasonCalendarDays), "121"),
            E(nameof(PreSeasonFaceDays), "21"),
            E(nameof(Week), "Weekday, Weekday, Weekend"),
            // Thanksgiving; Christmas (two face days); New Year's; MLK; Presidents' Day; the four spring breaks.
            E(nameof(Holidays), "11-24 x1, 12-24 x2, 01-01 x1, 01-17 x1, 02-18 x1, 03-07 x1, 03-14 x1, 03-21 x1, 03-28 x1"),
            E(nameof(SkipLandsBeforeOpenMinutes), "60"),
            E(nameof(ChairSpeedFactor), "1"),
            E(nameof(SkierSpeedFactor), "2"),
            E(nameof(RideCompression), "8"),
            E(nameof(LapsTarget), "4"),
            E(nameof(CloseNudgeSharePercent), "25"),
            E(nameof(SeasonEndDays), "7"),
            E(nameof(HoldGustMph), "34, 29, 45, 55"),
            E(nameof(HoldGustPerSeatTenths), "30, 15, 0, 0"),
            E(nameof(GustJitterPercent), "10"),
            E(nameof(HoldResumeMarginMph), "10"),
            E(nameof(HoldResumeFaceMinutes), "30"),
            E(nameof(HoldRecheckFaceMinutes), "15"),
            E(nameof(MaxLine), "2048"),
            E(nameof(MaxTrails), "1024"),
            E(nameof(EventRingSize), "4096"),
            E(nameof(SnapshotEvents), "512"),
            E(nameof(MaxLifts), "256"),
            E(nameof(TileFlushTicks), "10"),
            E(nameof(StageTilesPerTick), "32"),
            E(nameof(HourStepMaxTicks), "80"),
            E(nameof(HistoryDays), "256"),
        };

        static KeyValuePair<string, string> E(string name, string value) => new KeyValuePair<string, string>(name, value);

        static int Int(Dictionary<string, string> map, string name, int min, int max)
        {
            if (!int.TryParse(map[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < min || v > max)
                throw new FormatException($"{name} must be a whole number from {min} to {max}.");
            return v;
        }

        static int[] Ints(Dictionary<string, string> map, string name, int min, int max)
        {
            var parts = map[name].Split(',');
            var v = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]) || v[i] < min || v[i] > max)
                    throw new FormatException($"{name} must be whole numbers from {min} to {max}.");
            return v;
        }

        static DayType[] DayTypes(string text)
        {
            var parts = text.Split(',');
            var v = new DayType[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                v[i] = p == "Weekday" ? DayType.Weekday : p == "Weekend" ? DayType.Weekend : throw new FormatException($"Week: '{p}' isn't Weekday or Weekend.");
            }
            if (v.Length == 0) throw new FormatException("Week needs at least one day.");
            return v;
        }

        static Holiday[] HolidayList(string text)
        {
            var list = new List<Holiday>();
            foreach (string raw in text.Split(','))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                // "MM-DD xN"
                var bits = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (bits.Length != 2 || bits[0].Length != 5 || bits[0][2] != '-' || bits[1].Length < 2 || bits[1][0] != 'x')
                    throw new FormatException($"Holidays: '{p}' isn't 'MM-DD xN'.");
                int month = int.Parse(bits[0].Substring(0, 2), CultureInfo.InvariantCulture);
                int day = int.Parse(bits[0].Substring(3, 2), CultureInfo.InvariantCulture);
                int count = int.Parse(bits[1].Substring(1), CultureInfo.InvariantCulture);
                if (month < 1 || month > 12 || day < 1 || day > LabelCalendar.DaysInMonth(month) || count < 1 || count > 14)
                    throw new FormatException($"Holidays: '{p}' isn't a date in a 365-day year with 1–14 face days.");
                list.Add(new Holiday(month, day, count));
            }
            return list.ToArray();
        }

        /// <summary>FNV-1a, 64-bit: the table's and the state's integer hash (no allocation per call).</summary>
        internal static ulong Fnv64(byte[] bytes)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in bytes) { h ^= b; h *= 1099511628211UL; }
            return h;
        }
    }

    /// <summary>TM-07: a face day's type for demand (GU-03) and schedules (LO-09). Values are saved: never renumber.</summary>
    public enum DayType : byte { Weekday = 1, Weekend = 2, Holiday = 3 }

    /// <summary>TM-09: a holiday by calendar date (365-day year) and how many face days it marks.</summary>
    public readonly struct Holiday
    {
        public readonly int Month, Day, FaceDays;
        public Holiday(int month, int day, int faceDays) { Month = month; Day = day; FaceDays = faceDays; }
    }
}
