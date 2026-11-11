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
        public const int Version = 5;   // 3: task 4.12's guests (GU-03…GU-18); MaxLine retired for the slot-linked line
                                        // 4: task 4.13's confidence speed (GU-10 v2)
                                        // 5: task 4.05's weather (WX-17 v2, WX-28 v2, WX-29, WX-34 v2, WX-35, WX-36 v2, WX-39)

        public static Pacing Default => _default ??= new Pacing(null);
        static Pacing _default;

        /// <summary>GU-05: the four ability bands, which are the four trail colours.</summary>
        public const int Bands = 4;
        /// <summary>
        /// GU-18: the reputations the mountain carries. In Phase 4 an archetype *is* an ability band (GU-05), so there are
        /// four; families, park rats and powder hounds later are new archetype ids, never a new system.
        /// </summary>
        public const int Archetypes = 4;

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
        /// <summary>
        /// GU-10 v2 (owner, 2026-11-09: "an expert on a green should be zooming"): each ability band above the trail's true
        /// band adds this much to the skier's speed, each band below takes it off, in permille, clamped to Min…Max.
        /// </summary>
        public readonly int ConfidenceStepPermille;
        public readonly int ConfidenceMinPermille;
        public readonly int ConfidenceMaxPermille;
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
        /// <summary>LO-11 v2: how long the gust must stay that far below, face minutes (owner 2026-11-08: ten minutes).</summary>
        public readonly int HoldResumeFaceMinutes;
        /// <summary>LO-11 v2: a held lift reads the gust this often, face minutes (a running lift reads it once an hour).</summary>
        public readonly int HoldRecheckFaceMinutes;
        /// <summary>SI-16: the most trails a snapshot holds (their open state).</summary>
        public readonly int MaxTrails;

        // ---- Weather (task 4.05; p4-04-weather-design §5, §6) ----

        /// <summary>WX-17 v2, WX-28 v2: the gust factor by state (‰): in a storm, and otherwise.</summary>
        public readonly int GustFactorStorm, GustFactorFair;
        /// <summary>WX-24 v2: a storm burst's spread, lowest and highest (‰ of the slot's share).</summary>
        public readonly int[] StormBurst;
        /// <summary>WX-29: the cold pool's build per clear calm night hour and its cap (0.1 °F), and its depth (ft, WX-36 v2).</summary>
        public readonly int PoolBuildPerHour, PoolMax, PoolDepthFeet;
        /// <summary>WX-39: the free wind's scale (‰; POWER's smoothed wind runs low in mountains), tuned against how often a fixed-grip holds.</summary>
        public readonly int WindScale;
        /// <summary>WX-39: the wind's factor by exposure class (‰), sheltered valley first, exposed summit last (8 classes).</summary>
        public readonly int[] WindExposure;
        /// <summary>WX-35 v2: the orographic factor's most on a windward face, spillover just past a crest and the snow shadow (‰ change).</summary>
        public readonly int OrographicWindward, OrographicSpillover, OrographicShadow;
        /// <summary>WX-35 v2: how far past a crest the snow still falls as spillover (m).</summary>
        public readonly int SpilloverReachMetres;
        /// <summary>WX-34 v2 (owner Q8): the winter's liquid (‰) and temperature offset (0.1 °F): typical 1000 and 0, lean 750 and +30, big 1250 and −30. Scenario data and dev switches only.</summary>
        public readonly int WinterLiquid, WinterTemp;
        /// <summary>WX-34 v2: the forecast's skill (‰ of the wobble and pull toward normal): sharp 600, normal 1000, poor 1600 (PR C).</summary>
        public readonly int ForecastSkill;

        // ---- Guests: the crowd (GU-03, GU-04, GU-18; task 4.12) ----

        /// <summary>GU-01: the most skiers on the hill at once. Every per-skier array is this long, allocated once.</summary>
        public readonly int SkierCap;
        /// <summary>GU-03: the location's draw, the skiers who would come on a peak day to a mountain they love.</summary>
        public readonly int LocationDraw;
        /// <summary>GU-05: the share of the crowd each archetype is, permille (v1's archetypes are the four ability bands).</summary>
        public readonly int[] ArchetypeMix;
        /// <summary>GU-03: reputation's floor as a demand factor, permille (so a hated mountain still draws a fifth).</summary>
        public readonly int RepFloor;
        /// <summary>GU-18: an archetype's reputation at a new resort, thousandths.</summary>
        public readonly int StartRep;
        /// <summary>GU-18: how far reputation moves toward the day's verdict, permille.</summary>
        public readonly int RepRate;
        /// <summary>GU-18: an archetype needs this many arrivals before its reputation moves at all.</summary>
        public readonly int RepMinSample;
        /// <summary>GU-18: the mean satisfaction that maps to a verdict of 0, thousandths.</summary>
        public readonly int RepSatLow;
        /// <summary>GU-18: the mean satisfaction that maps to a verdict of 1, thousandths.</summary>
        public readonly int RepSatHigh;
        /// <summary>GU-03: the demand factor of a weekday, a weekend day and a holiday, permille.</summary>
        public readonly int[] DayTypeDemand;
        /// <summary>WX-44: the most the morning's new snow lifts demand, permille (4.05 sets the day's value).</summary>
        public readonly int PowderPull;
        /// <summary>GU-03: how many skiers one open acre carries comfortably (the terrain capacity line).</summary>
        public readonly int SkiersPerOpenAcre;

        // ---- Guests: arrivals and the stay (GU-06) ----

        /// <summary>GU-06: the share of the day's arrivals in each equal part of the arrival window, permille.</summary>
        public readonly int[] ArrivalWeights;
        /// <summary>GU-06: the arrival window closes this many face minutes before last chair.</summary>
        public readonly int ArrivalEndsBeforeLastChairMinutes;
        /// <summary>GU-06: the fewest and most laps a skier plans to ski (<see cref="LapsTarget"/> is the mean).</summary>
        public readonly int StayLapsMin, StayLapsMax;
        /// <summary>GU-02: the share of parties of one, two, three and four, permille.</summary>
        public readonly int[] PartyMix;

        // ---- Guests: lines and choosing (GU-07, GU-08, GU-10) ----

        /// <summary>GU-06: a skier who has queued this many real seconds at 1× leaves the line and goes home.</summary>
        public readonly int LeaveWaitSeconds;
        /// <summary>GU-08: a lift wait past this many real seconds at 1× sends a skier to another lift if one is offered.</summary>
        public readonly int BalkWaitSeconds;
        /// <summary>GU-07: the lowest the fit factor falls, permille, so an expert can still ski a green home.</summary>
        public readonly int FitFloor;
        /// <summary>GU-07: what an expert makes of powder and moguls, and a beginner of moguls and ice, permille.</summary>
        public readonly int TasteExpertPowder, TasteBeginnerMoguls;
        /// <summary>GU-10: a skier's speed on a green, blue, black and double black, tenths of a mph.</summary>
        public readonly int[] BandSpeedMphTenths;
        /// <summary>GU-10: the grade each band's base speed assumes, percent (a station steeper than this slows a skier).</summary>
        public readonly int[] BandDesignGradePercent;
        /// <summary>GU-09: walking speed inside an Area, tenths of a mph.</summary>
        public readonly int WalkMphTenths;
        /// <summary>GU-10: skiers enter one trail this many ticks apart at the same node.</summary>
        public readonly int EntrySpacingTicks;
        /// <summary>GU-07: a skier with nothing to pick waits this many ticks before choosing again.</summary>
        public readonly int ChoiceRetryTicks;

        // ---- Guests: satisfaction (GU-13, GU-17) ----

        /// <summary>GU-13: satisfaction at the start of a visit, thousandths.</summary>
        public readonly int StartSatisfaction;
        /// <summary>GU-13: how much of a lap's snow quality a lap carries into satisfaction, permille.</summary>
        public readonly int LapQualityWeight;
        /// <summary>GU-13: satisfaction lost per real minute of a wait over <see cref="WaitFreeMinutes"/>, thousandths.</summary>
        public readonly int WaitPenaltyPerMinute;
        /// <summary>GU-13: the first minutes of a wait cost nothing.</summary>
        public readonly int WaitFreeMinutes;
        /// <summary>GU-17: what a lap on a trail posted easier than it is costs, thousandths.</summary>
        public readonly int MisratePenalty;
        /// <summary>GU-17: what a lap on a trail opened Marginal costs, thousandths (4.07 opens them).</summary>
        public readonly int MarginalPenalty;
        /// <summary>GU-17: the Marginal penalty's factor in the season's shoulders and in its middle.</summary>
        public readonly int[] MarginalPhase;
        /// <summary>GU-17: face days at each end of the season that count as its shoulder.</summary>
        public readonly int ShoulderDays;
        /// <summary>GU-13: what a lap on a segment over its comfortable density costs, thousandths.</summary>
        public readonly int CrowdPenalty;
        /// <summary>GU-11: what being stranded by a closing trail costs, thousandths.</summary>
        public readonly int StrandedPenalty;

        // ---- Guests: the machinery (BG-01, BG-08) ----

        /// <summary>GU-02: segments of a skier's day kept in its day record.</summary>
        public readonly int RecordSegments;
        /// <summary>SN-24: lateral bands a segment's wear counters are split across.</summary>
        public readonly int WearBands;
        /// <summary>BG-01: buckets in the timing wheel that schedules skiers (a power of two).</summary>
        public readonly int WheelBuckets;
        /// <summary>GU-02: the selected skier's record is published at most this often, in ticks.</summary>
        public readonly int InspectTicks;

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
            ConfidenceStepPermille = Int(map, nameof(ConfidenceStepPermille), 0, 1000);
            ConfidenceMinPermille = Int(map, nameof(ConfidenceMinPermille), 100, 1000);
            ConfidenceMaxPermille = Int(map, nameof(ConfidenceMaxPermille), 1000, 4000);
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
            GustFactorStorm = Int(map, nameof(GustFactorStorm), 1000, 3000);
            GustFactorFair = Int(map, nameof(GustFactorFair), 1000, 3000);
            StormBurst = Ints(map, nameof(StormBurst), 0, 5000);
            if (StormBurst.Length != 2 || StormBurst[0] > StormBurst[1]) throw new FormatException("StormBurst needs two entries, lowest then highest.");
            PoolBuildPerHour = Int(map, nameof(PoolBuildPerHour), 0, 100);
            PoolMax = Int(map, nameof(PoolMax), 0, 300);
            PoolDepthFeet = Int(map, nameof(PoolDepthFeet), 1, 5000);
            WindScale = Int(map, nameof(WindScale), 0, 5000);
            WindExposure = Ints(map, nameof(WindExposure), 0, 5000);
            if (WindExposure.Length != 8) throw new FormatException("WindExposure needs eight entries, sheltered valley to exposed summit.");
            OrographicWindward = Int(map, nameof(OrographicWindward), 0, 1000);
            OrographicSpillover = Int(map, nameof(OrographicSpillover), 0, 1000);
            OrographicShadow = Int(map, nameof(OrographicShadow), 0, 1000);
            SpilloverReachMetres = Int(map, nameof(SpilloverReachMetres), 0, 10_000);
            WinterLiquid = Int(map, nameof(WinterLiquid), 100, 5000);
            WinterTemp = Int(map, nameof(WinterTemp), -200, 200);
            ForecastSkill = Int(map, nameof(ForecastSkill), 0, 5000);
            MaxTrails = Int(map, nameof(MaxTrails), 1, 8192);
            SkierCap = Int(map, nameof(SkierCap), 0, 1 << 20);
            LocationDraw = Int(map, nameof(LocationDraw), 0, 1 << 20);
            ArchetypeMix = Ints(map, nameof(ArchetypeMix), 0, 1000);
            if (ArchetypeMix.Length != Archetypes) throw new FormatException($"ArchetypeMix needs {Archetypes} entries (the ability bands).");
            RepFloor = Int(map, nameof(RepFloor), 0, 1000);
            StartRep = Int(map, nameof(StartRep), 0, 1000);
            RepRate = Int(map, nameof(RepRate), 1, 1000);
            RepMinSample = Int(map, nameof(RepMinSample), 1, 100_000);
            RepSatLow = Int(map, nameof(RepSatLow), 0, 1000);
            RepSatHigh = Int(map, nameof(RepSatHigh), 1, 1000);
            if (RepSatHigh <= RepSatLow) throw new FormatException("RepSatHigh must be above RepSatLow.");
            DayTypeDemand = Ints(map, nameof(DayTypeDemand), 0, 10_000);
            if (DayTypeDemand.Length != 3) throw new FormatException("DayTypeDemand needs three entries (weekday, weekend, holiday).");
            PowderPull = Int(map, nameof(PowderPull), 1000, 10_000);
            SkiersPerOpenAcre = Int(map, nameof(SkiersPerOpenAcre), 1, 10_000);
            ArrivalWeights = Ints(map, nameof(ArrivalWeights), 0, 1000);
            if (ArrivalWeights.Length < 1) throw new FormatException("ArrivalWeights needs at least one part.");
            ArrivalEndsBeforeLastChairMinutes = Int(map, nameof(ArrivalEndsBeforeLastChairMinutes), 0, 12 * 60);
            StayLapsMin = Int(map, nameof(StayLapsMin), 1, 200);
            StayLapsMax = Int(map, nameof(StayLapsMax), StayLapsMin, 200);
            PartyMix = Ints(map, nameof(PartyMix), 0, 1000);
            if (PartyMix.Length < 1) throw new FormatException("PartyMix needs at least one size.");
            LeaveWaitSeconds = Int(map, nameof(LeaveWaitSeconds), 1, 100_000);
            BalkWaitSeconds = Int(map, nameof(BalkWaitSeconds), 1, 100_000);
            FitFloor = Int(map, nameof(FitFloor), 0, 1000);
            TasteExpertPowder = Int(map, nameof(TasteExpertPowder), 0, 4000);
            TasteBeginnerMoguls = Int(map, nameof(TasteBeginnerMoguls), 0, 4000);
            BandSpeedMphTenths = Ints(map, nameof(BandSpeedMphTenths), 1, 2000);
            if (BandSpeedMphTenths.Length != Bands) throw new FormatException($"BandSpeedMphTenths needs {Bands} entries.");
            BandDesignGradePercent = Ints(map, nameof(BandDesignGradePercent), 1, 200);
            if (BandDesignGradePercent.Length != Bands) throw new FormatException($"BandDesignGradePercent needs {Bands} entries.");
            WalkMphTenths = Int(map, nameof(WalkMphTenths), 1, 2000);
            EntrySpacingTicks = Int(map, nameof(EntrySpacingTicks), 0, 10_000);
            ChoiceRetryTicks = Int(map, nameof(ChoiceRetryTicks), 1, 10_000);
            StartSatisfaction = Int(map, nameof(StartSatisfaction), 0, 1000);
            LapQualityWeight = Int(map, nameof(LapQualityWeight), 0, 1000);
            WaitPenaltyPerMinute = Int(map, nameof(WaitPenaltyPerMinute), 0, 1000);
            WaitFreeMinutes = Int(map, nameof(WaitFreeMinutes), 0, 1000);
            MisratePenalty = Int(map, nameof(MisratePenalty), 0, 1000);
            MarginalPenalty = Int(map, nameof(MarginalPenalty), 0, 1000);
            MarginalPhase = Ints(map, nameof(MarginalPhase), 0, 100);
            if (MarginalPhase.Length != 2) throw new FormatException("MarginalPhase needs two entries (the shoulders, the middle).");
            ShoulderDays = Int(map, nameof(ShoulderDays), 0, 365);
            CrowdPenalty = Int(map, nameof(CrowdPenalty), 0, 1000);
            StrandedPenalty = Int(map, nameof(StrandedPenalty), 0, 1000);
            RecordSegments = Int(map, nameof(RecordSegments), 1, 4096);
            WearBands = Int(map, nameof(WearBands), 1, 256);
            WheelBuckets = Int(map, nameof(WheelBuckets), 16, 1 << 20);
            if ((WheelBuckets & (WheelBuckets - 1)) != 0) throw new FormatException("WheelBuckets must be a power of two.");
            InspectTicks = Int(map, nameof(InspectTicks), 1, 10_000);
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
            E(nameof(ConfidenceStepPermille), "200"),
            E(nameof(ConfidenceMinPermille), "700"),
            E(nameof(ConfidenceMaxPermille), "1400"),
            E(nameof(LapsTarget), "4"),
            E(nameof(CloseNudgeSharePercent), "25"),
            E(nameof(SeasonEndDays), "7"),
            E(nameof(HoldGustMph), "34, 29, 45, 55"),
            E(nameof(HoldGustPerSeatTenths), "30, 15, 0, 0"),
            E(nameof(GustJitterPercent), "10"),
            E(nameof(HoldResumeMarginMph), "10"),
            E(nameof(HoldResumeFaceMinutes), "10"),   // 4.11 PR B's values; MaxLine retired by 4.12's line lists
            E(nameof(HoldRecheckFaceMinutes), "5"),
            E(nameof(GustFactorStorm), "1600"),
            E(nameof(GustFactorFair), "1300"),
            E(nameof(StormBurst), "500, 1600"),
            E(nameof(PoolBuildPerHour), "10"),
            E(nameof(PoolMax), "80"),
            E(nameof(PoolDepthFeet), "500"),
            E(nameof(WindScale), "850"),
            E(nameof(WindExposure), "400, 550, 700, 850, 1000, 1200, 1400, 1600"),
            E(nameof(OrographicWindward), "400"),
            E(nameof(OrographicSpillover), "100"),
            E(nameof(OrographicShadow), "300"),
            E(nameof(SpilloverReachMetres), "1000"),
            E(nameof(WinterLiquid), "1000"),
            E(nameof(WinterTemp), "0"),
            E(nameof(ForecastSkill), "1000"),
            E(nameof(MaxTrails), "1024"),
            // GU-01…GU-18 (task 4.12). The crowd:
            E(nameof(SkierCap), "3000"),
            E(nameof(LocationDraw), "800"),
            E(nameof(ArchetypeMix), "200, 400, 300, 100"),
            E(nameof(RepFloor), "200"),
            E(nameof(StartRep), "500"),
            E(nameof(RepRate), "100"),
            E(nameof(RepMinSample), "10"),
            E(nameof(RepSatLow), "500"),
            E(nameof(RepSatHigh), "900"),
            E(nameof(DayTypeDemand), "500, 1000, 1200"),
            E(nameof(PowderPull), "1500"),
            E(nameof(SkiersPerOpenAcre), "5"),
            // Arrivals and the stay:
            E(nameof(ArrivalWeights), "380, 300, 160, 90, 40, 20, 10"),
            E(nameof(ArrivalEndsBeforeLastChairMinutes), "45"),
            E(nameof(StayLapsMin), "3"),
            E(nameof(StayLapsMax), "5"),
            E(nameof(PartyMix), "400, 350, 150, 100"),
            // Lines and choosing:
            E(nameof(LeaveWaitSeconds), "240"),
            E(nameof(BalkWaitSeconds), "180"),
            E(nameof(FitFloor), "100"),
            E(nameof(TasteExpertPowder), "1200"),
            E(nameof(TasteBeginnerMoguls), "500"),
            E(nameof(BandSpeedMphTenths), "130, 200, 250, 270"),
            E(nameof(BandDesignGradePercent), "15, 32, 50, 70"),
            E(nameof(WalkMphTenths), "30"),
            E(nameof(EntrySpacingTicks), "10"),
            E(nameof(ChoiceRetryTicks), "50"),
            // Satisfaction:
            E(nameof(StartSatisfaction), "800"),
            E(nameof(LapQualityWeight), "100"),
            E(nameof(WaitPenaltyPerMinute), "50"),
            E(nameof(WaitFreeMinutes), "2"),
            E(nameof(MisratePenalty), "100"),
            E(nameof(MarginalPenalty), "50"),
            E(nameof(MarginalPhase), "1, 2"),
            E(nameof(ShoulderDays), "6"),
            E(nameof(CrowdPenalty), "20"),
            E(nameof(StrandedPenalty), "100"),
            // The machinery:
            E(nameof(RecordSegments), "48"),
            E(nameof(WearBands), "8"),
            E(nameof(WheelBuckets), "4096"),
            E(nameof(InspectTicks), "10"),
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
