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
        public const int Version = 15;   // 3: task 4.12's guests (GU-03…GU-18); MaxLine retired for the slot-linked line
                                        // 4: task 4.13's confidence speed (GU-10 v2)
                                        // 5: task 4.05's weather (WX-17 v2, WX-28 v2, WX-29, WX-34 v2, WX-35, WX-36 v2, WX-39)
                                        // 6: task 4.05 PR B2's weather over the map (WX-35, WX-39) and PR C's forecast (WX-05 v3, WX-41)
                                        // 7: task 4.05 PR C's named events (WX-41), alerts (WX-42) and forecast pulls (WX-44)
                                        // 8: task 4.09's lakes (LK-01 v2…LK-08 v2)
                                        // 9: task 4.07 PR A2's natural snow (SN-05 v2…SN-18 v2, WX-59…WX-64)
                                        // 10: task 4.14 PR B's water sources (WS-03 v2, WS-05, WS-07, WS-01's cap; number confirmed with 0.0 at merge)
                                        // 11: task 4.24 PR B's ride down (RD-4; it merged before 4.07 B1, which takes 12, 0.0)
                                        // 12: task 4.15's snowmaking solve (HY-07 PipeClass, HY-08 SolveLatencyTicks and MaxResolves); 0.0: the first of 4.15 and 4.07 B1 to merge takes 12, the other renumbers
                                        // 13: task 4.07 B1 part 2a's trail snow and 2b's readiness (SN-03 v3, SN-14 v3, SN-16 v3, SN-17, SN-20 v3, SN-23 v3, SN-24 v3, SN-30 v2, GU-07 v2; 0.0 2026-11-19: 4.15 merged first and took 12)
                                        // 14: task 4.25's grooming operations (GO-01…GO-12, SN-22 v4, SM-05, SN-27 v2's GreenPushQuality; number cleared by 0.0 2026-11-20; 4.16 may race: the second to merge renumbers)
                                        // 15: task 4.25 PR B's transit and report at opening (GO-15, GO-16 v2, GroomTransitKmh, GroomPassMm, GroomWinchSharePermille, SkipLandsBeforeOpenMinutes 0, GO-03 v2's equal shifts with GroomSwingEndHour retired; cleared by 0.0 2026-11-21)

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
        /// <summary>Owner, 2026-11-08 (plan question 8): Skip to morning lands this many face minutes before opening; 0 since
        /// 2026-11-21 (4.25 PR B: it lands at opening, with the night's grooming report).</summary>
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
        /// <summary>WX-35 (PR B2): the hvf (ft) where each exposure class above 0 starts (7 rising values), and the 7×7 rise (ft) that moves a cell one class.</summary>
        public readonly int[] ExposureHvfFeet;
        public readonly int ExposureRiseFeet;
        /// <summary>WX-35 v2 (PR B2): the climb (ft) that earns the whole windward factor, and the barrier (ft) that casts the whole shadow.</summary>
        public readonly int OrographicClimbFeet, OrographicBarrierFeet;
        /// <summary>WX-39 (PR B2): a valley this deep (ft) turns the wind fully along it, below this hvf (ft) it turns at all; a rise's speed-up (‰ at 300 ft); the lee's factor (‰) behind ground this much (ft) higher upwind.</summary>
        public readonly int SteerValleyFeet, SteerShelterFeet, SteerHillSpeed, SteerCrestLee, SteerLeeFeet;
        /// <summary>WX-05 v3 (PR C): the forecast's lean on the truth (‰) a day out and at a week.</summary>
        public readonly int ForecastLeanDay1, ForecastLeanDay7;
        /// <summary>WX-05 v3 (PR C): the shown temperature's half-range (0.1 °F) a day out and at a week; the precipitation's (‰) the evening before, at 3 days and at a week; a true storm's chance of precipitation (‰) at the same leads.</summary>
        public readonly int[] ForecastTempTenths, ForecastPrecipPermille, ForecastChancePermille;
        /// <summary>WX-05 v3 (PR C): the share of storms whose size busts (‰), and phantom storms as a share of the third's storm rate (‰).</summary>
        public readonly int ForecastBustPermille, ForecastPhantomPermille;
        /// <summary>WX-41 (PR C): the summit gust (mph) that makes a wind event.</summary>
        public readonly int WindEventGust;
        /// <summary>WX-05 v3 (PR C): the storms whose day shows one off at 3 days (‰), and a dry day's chance of precipitation at no skill (‰, × (1 − lean)).</summary>
        public readonly int ForecastTimingPermille, ForecastDryChancePermille;
        /// <summary>WX-05 v3 (PR C): the gun window's wet bulb (0.1 °F; SM-02's AutoOn).</summary>
        public readonly int GunWindowWetBulb;
        /// <summary>WX-41 (PR C): a storm's liquid at the summit over two face hours (0.01 in); rain's face hours at the base.</summary>
        public readonly int StormEventLiquid, RainEventHours;
        /// <summary>WX-41 (PR C): a thaw (mid air above, 0.1 °F, for face hours) and a refreeze (mid air below, for face hours).</summary>
        public readonly int ThawTemp, ThawHours, RefreezeTemp, RefreezeHours;
        /// <summary>WX-41 (PR C): a bluebird's clearness (‰), and the base colder than mid that makes an inversion (0.1 °F).</summary>
        public readonly int BluebirdClearness, InversionTenths;
        /// <summary>WX-42 (PR C): an alert goes out when the forecast's event is this many face days out or fewer.</summary>
        public readonly int AlertLeadDays;
        /// <summary>WX-44 (PR C): the crowd's read of the evening's forecast (‰): rain at the base, a storm with summit gusts above <see cref="ForecastPullStormGust"/> (mph), bluebird, a cold snap.</summary>
        public readonly int ForecastPullRain, ForecastPullStorm, ForecastPullStormGust, ForecastPullBluebird, ForecastPullColdSnap;

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
        /// <summary>
        /// RD-4 (task 4.24 PR B, owner 2026-11-17): what a ride is worth to a band with nothing to ski at the other end —
        /// a gondola up to a hard top to look, or down to a base with nothing for it — thousandths, against a good run's
        /// ~900. 300 (a third of one): some beginners take a gondola up to look and ride back down; most ski their greens.
        /// </summary>
        public readonly int RideDownScore;

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

        // ---- Lakes (LK-01 v2…LK-08 v2; task 4.09) ----

        /// <summary>LK-01 v2: a cell is water at this share (‰) or more; a lake is at least <see cref="LakeMinArea"/> (m²).</summary>
        public readonly int LakeWaterShare, LakeMinArea;
        /// <summary>LK-01 v2: a core whose area ÷ (widest inscribed width)² is above this (0.1) is a river; a lake's core grows back this many cells.</summary>
        public readonly int RiverElongation, LakeGrowCells;
        /// <summary>LK-01 v2: the most lakes a map holds, and the places kept after them for ponds (4.14).</summary>
        public readonly int MaxLakes, MaxPonds;
        /// <summary>LK-02's depth clamp and LK-07's streams depth (0.1 ft).</summary>
        public readonly int LakeDepthMin, LakeDepthMax, StreamsDepth;
        /// <summary>LK-03 v2: the fall lag, calendar °F-days below 32 per foot of depth (0.1).</summary>
        public readonly int LakeLagPerFoot;
        /// <summary>LK-04 v2: Stefan's α (0.01 in per √°F-day), and the snow depth (0.1 in) that halves it.</summary>
        public readonly int IceAlpha, IceSnowInsulation;
        /// <summary>LK-05 v2: snow on ice: its settled density (kg/m³), its cap (0.1 in), its melt (0.0001 in of water per calendar °F-hour) and the rain's (‰ of the rain).</summary>
        public readonly int IceSnowDensity, IceSnowMax, IceSnowMelt, IceRainMelt;
        /// <summary>LK-06 v2: ice lost per calendar thawing °F-day (0.01 in), scaled by the reference depth (0.1 ft) ÷ the body's, within ½–2.</summary>
        public readonly int IceMeltPerDegDay, LakeThawRefDepth;
        /// <summary>LK-07 v2: the streams body's ice cap (0.01 in).</summary>
        public readonly int StreamsIceMax;
        /// <summary>LK-08 v2: open water at or under this much ice (0.01 in); snow-covered at this much snow (0.1 in).</summary>
        public readonly int IceOpenAt, SnowCoveredAt;

        // ---- Natural snow (task 4.07 PR A2: SN-05 v2…SN-18 v2, WX-59…WX-64) ----

        /// <summary>SN-09 v2, WX-63: the snow energy's numbers (<c>SnowTuning</c>): the surface layer and the thin pack (µm of
        /// water); brightness fresh, cold floor, wet floor, bare ground (‰) and the new snow that resets it (µm); its time
        /// constants cold and melting (s); the canopy's shade (‰); turbulent exchange (wind × per 10,000, plus calm, mm/s);
        /// the ground's heat (mW/m²); SN-10's holding capacity (‰) and SN-09's crumb (µm); SN-08 v2's settling (s, × when wet)
        /// toward cold, near freezing and wet densities (kg/m³).</summary>
        public readonly int SnowSurfaceUm, SnowThinUm, AlbedoFresh, AlbedoCold, AlbedoWet, AlbedoGround, AlbedoResetUm,
                            AlbedoColdTau, AlbedoWetTau, CanopyShade, SnowWindPer10k, SnowCalmMms, GroundHeatMw, SnowHoldPermille,
                            SnowCrumbUm, SettleTau, SettleWetFactor, DensityCold, DensityWarm, DensityWet;
        /// <summary>SN-06 v2: the most new snow a full canopy holds back (‰); steep slopes shed from and to these grades (%); the
        /// shed spreads over a run-out fan this long (m), a cell taking at most this much of it (µm of water) an hour. SN-06 v3:
        /// and only while its water stands under this many times (‰) the mean water round it (the cone's height).</summary>
        public readonly int CanopyHolds, ShedStartGrade, ShedFullGrade, ShedRunout, ShedPileMax, ShedPileRatio;
        /// <summary>SN-07 v3: drift above this wind (0.1 mph); the share moved rises over this much more wind to its cap (‰);
        /// it runs this far downwind (m); a cell gives more the higher it stands over its surroundings, all of the share at
        /// twice this rise (dm), and keeps less of what reaches it, none at this rise.</summary>
        public readonly int DriftWind, DriftWindSpan, DriftShareMax, DriftReach, DriftExposedRise;
        /// <summary>SN-14 v2: powder once this much water (µm) has fallen since the last surface event, wet powder above this wet
        /// bulb (0.1 °F); a wet surface refreezes once its skin has been below this (0.01 °C) for these face hours; corn after
        /// this many thaw–refreeze cycles within these face days.</summary>
        public readonly int PowderNewUm, WetPowderWetBulb, RefreezeSkin, SnowRefreezeHours, CornCycles, CornWindowDays;
        /// <summary>SN-16 v2: base quality per type (‰, P PP MG HP IS CO FG LG WG WP), the age term per face hour (‰) and its floor (‰).</summary>
        public readonly int[] QualityBase;
        public readonly int QualityAgePerHour, QualityAgeFloor;
        /// <summary>A seed's density (<c>SetSnowpack</c>, an old save's depths: packed powder, kg/m³).</summary>
        public readonly int SeedDensity;
        /// <summary>BG-02 v4: the hour's snow lands this long after the roll (face ms: a whole number of day and night ticks), on
        /// this many snow threads (the bytes don't depend on it).</summary>
        public readonly int SnowLandFaceMs, SnowThreads;
        /// <summary>WX-60 v2: the sun fades over this much of its elevation's sine (millionths) as it crosses the skyline.</summary>
        public readonly int SkylineFade;

        // ---- Water sources (WS-02…WS-11; task 4.14 PR B) ----

        /// <summary>WS-03 v2: a stream's flow by calendar month (‰ of its base cfs), January first.</summary>
        public readonly int[] InflowSeason;
        /// <summary>WS-05: the intake sits this far under full pool (mm); its head is the level now.</summary>
        public readonly int IntakeDepthMm;
        /// <summary>WS-05: a pond or reservoir stops giving water at this share of its capacity (‰).</summary>
        public readonly int MinPoolPermille;
        /// <summary>WS-05: a lake may be drawn down this far under its natural level (mm).</summary>
        public readonly int LakeDrawdownMm;
        /// <summary>WS-07: open water loses this much a calendar day (µm).</summary>
        public readonly int EvapMicronsPerDay;
        /// <summary>WS-01: the most sources the simulation keeps water for (ponds and dams first, then lakes).</summary>
        public readonly int MaxSources;

        // ---- Snowmaking (task 4.15) ----

        /// <summary>HY-08: ticks from a solve's ask (a network edit, a gun set On, Check network) to its answer, fixed; the
        /// SG-07 re-solves one solve may run.</summary>
        public readonly int SolveLatencyTicks, MaxResolves;
        /// <summary>HY-07: the pipe's pressure class (psi): a warning above 80 %, the pumps throttle above it.</summary>
        public readonly int PipeClass;
        // ---- Trail snow (task 4.07 B1 part 2a: SN-03 v3, SN-14 v3, SN-17, SN-23 v3, SN-24 v3, SN-30 v2) ----

        /// <summary>SN-03 v3: the most fine cells a resort's trails get; the fine size steps 1, 2, 4 m to fit.</summary>
        public readonly int FineCellCap;
        /// <summary>SN-30 v2: a packed fine cell's loss weight at 400 kg/m³ and over (of 1000; 1000 at 200 kg/m³ and under).</summary>
        public readonly int PackedMeltFloor;
        /// <summary>SN-24 v3: water a skier's pass wears off a fine cell (µm), × the grade over the reference (%), clamped to
        /// these bounds (‰).</summary>
        public readonly int WearPerPassUm, WearGradeRef, WearFactorMin, WearFactorMax;
        /// <summary>SN-17: moguls grow on ungroomed cells from this grade (%), by this much (of 255) per 1000 passes.</summary>
        public readonly int MogulGrade, MogulsPerKPasses;
        /// <summary>SN-14 v3: passes that pack powder, harden packed or groomed snow, and break a crust or ice to loose granular.</summary>
        public readonly int PackedPasses, HardPasses, CrustPasses;
        /// <summary>SN-23 v3: a pass evens depth within this reach along the centreline (mm), by this share (‰), and packs toward
        /// this density (kg/m³) by this share of the gap (‰); not where the snow is under this depth or a strip narrower than
        /// this (mm: the groomer would tear it to mud, owner 2026-11-18), nor over this depth; this many groomings in a row
        /// harden it. (4.07's nightly hour went with task 4.25: the shifts say when.)</summary>
        public readonly int GroomReachMm, GroomBlend, GroomedDensity, GroomPack, GroomMinDepthMm, GroomMinWidthMm, GroomMaxDepthMm,
                            GroomsToHard;
        /// <summary>SN-23 v4: a light pass's reach (mm) and blend (‰); it packs as a heavy one does (task 4.25's strengths).</summary>
        public readonly int GroomLightReachMm, GroomLightBlend;
        // ---- Task 4.25: grooming operations (GO-01…GO-12, SN-22 v4, SM-05) ----

        /// <summary>GO-03 v2 (owner 2026-11-21: "each shift the same at 8 hours"): the swing shift starts this long after the lift
        /// stop and the night shift ends this long before the next opening (both 0: the stop to the opening, 16 h at the
        /// default hours); the two split the time between in half. The day shift runs between these face hours (closed trails only).</summary>
        public readonly int GroomSweepMinutes, GroomNightEndsBeforeOpenMinutes, GroomDayStartHour, GroomDayEndHour;
        /// <summary>GO-03: a shift's first and last this many minutes are travel (no work), and each trail costs this many more.</summary>
        public readonly int GroomTravelMinutes, GroomTaskMinutes;
        /// <summary>GO-15 (owner 2026-11-21): a groomer drives between the depot and its trails at full speed, km/h, over the
        /// trails only (cat tracks and work roads help). <see cref="GroomTravelMinutes"/> is left for a design with no network.</summary>
        public readonly int GroomTransitKmh;
        /// <summary>GO-16 v2 (owner 2026-11-21): one pass of the tiller across the snow, mm: a groomer works a run in full-length
        /// passes side by side, and a drive along a run due tonight grooms one pass of it on the way, at the tilling speed.</summary>
        public readonly int GroomPassMm;
        /// <summary>GO-01 (owner round 6), per model (400, 400 W, 800, 800 W): tilling (0.01 acre an hour), pushing (yd³ an
        /// hour), fuel and upkeep ($ a groomer-hour) and price ($, diesel).</summary>
        public readonly int[] GroomTillCentiAcres, GroomPushYd3, GroomFuelUsd, GroomPriceUsd;
        /// <summary>GO-01: the price by drive (diesel, hybrid, electric; ‰) and an electric groomer's minutes a shift on one charge.</summary>
        public readonly int[] GroomDrivePricePermille;
        public readonly int GroomElectricMinutes;
        /// <summary>GO-02: the wider tiller's speed (‰), not on a trail narrower than this (mm).</summary>
        public readonly int GroomTillerPermille, GroomTillerMinWidthMm;
        /// <summary>GO-04: full speed to this grade (%), half to the next; past it only a W (winch), at half, to the last.</summary>
        public readonly int GroomFullSpeedGrade, GroomWinchGrade, GroomWinchMaxGrade;
        /// <summary>Round 3 (owner 2026-11-21: blues shouldn't need a winch): a run needs a W only when at least this share
        /// (permille of its area) is past the winch grade; a smaller steep patch is a slow stretch any groomer works at half speed.</summary>
        public readonly int GroomWinchSharePermille;
        /// <summary>SN-22 v4: Autopilot tills after this much new snow (µm water); on a trail this steep (% mean) it keeps the
        /// powder up to this much, and warns after this many nights of it.</summary>
        public readonly int GroomNewSnowUm, GroomPowderKeepUm, GroomPowderGrade, GroomPowderWarnNights;
        /// <summary>SM-05: a pile this far above the trail's mean depth (mm) is a whale; a whale cures in this many hours.</summary>
        public readonly int WhaleRiseMm, GroomCureHours;
        /// <summary>GO-06: an operator's pay ($ an hour) and the shifts' rates (swing, night, day; ‰).</summary>
        public readonly int GroomPayUsdHour;
        public readonly int[] GroomShiftPayPermille;
        /// <summary>SN-27 v2: a green push's snow rates this share (‰) until it is groomed again.</summary>
        public readonly int GreenPushQuality;
        /// <summary>SN-20 v3 (task 4.07 B1 part 2b): a fine cell is covered at this depth (mm) on a graded trail and on a
        /// natural one; a trail is Covered with a skiable line and this share (‰) of its cells covered.</summary>
        public readonly int CoverGradedMm, CoverNaturalMm, ReadyCover;
        /// <summary>SN-20 v3: the skiable line's width (mm, 4 ft).</summary>
        public readonly int LineWidthMm;
        /// <summary>SN-20 v3, GU-07 v2 (owner 2026-11-18): the late season's walk tolerance L, feet, from the day it starts
        /// to April 1; walking pulls a trail by exp(−(feet ÷ L)²).</summary>
        public readonly int LateWalkStartFt, LateWalkEndFt;
        /// <summary>GU-09: walking speed, mm a second (3 mph), for a Marginal trail's walk.</summary>
        public readonly int WalkSpeedMmS;
        /// <summary>SN-16 v3: quality lost a pass (tenths of ‰) and its floor (‰); the mogul term at full moguls (‰).</summary>
        public readonly int QualityPassTenths, QualityPassFloor, QualityMogulPermille;
        /// <summary>GU-07 v2's taste: experts on powder or moguls, beginners on moguls or ice (‰), moguls counting from this
        /// fine-cell mogul level (0–255).</summary>
        public readonly int TasteExpert, TasteBeginner, TasteMogulMin;

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
            // At most ±50 %: then no cell's factor can reach the 0.1–3.0 clamp after normalising, so the box's mean stays 1.0.
            OrographicWindward = Int(map, nameof(OrographicWindward), 0, 500);
            OrographicSpillover = Int(map, nameof(OrographicSpillover), 0, 500);
            OrographicShadow = Int(map, nameof(OrographicShadow), 0, 500);
            SpilloverReachMetres = Int(map, nameof(SpilloverReachMetres), 0, 10_000);
            WinterLiquid = Int(map, nameof(WinterLiquid), 100, 5000);
            WinterTemp = Int(map, nameof(WinterTemp), -200, 200);
            ForecastSkill = Int(map, nameof(ForecastSkill), 0, 5000);
            ExposureHvfFeet = Ints(map, nameof(ExposureHvfFeet), 0, 10_000);
            if (ExposureHvfFeet.Length != 7) throw new FormatException("ExposureHvfFeet needs seven rising entries, one per class above the first.");
            for (int i = 1; i < ExposureHvfFeet.Length; i++)
                if (ExposureHvfFeet[i] <= ExposureHvfFeet[i - 1]) throw new FormatException("ExposureHvfFeet needs seven rising entries, one per class above the first.");
            ExposureRiseFeet = Int(map, nameof(ExposureRiseFeet), 1, 1000);
            OrographicClimbFeet = Int(map, nameof(OrographicClimbFeet), 1, 20_000);
            OrographicBarrierFeet = Int(map, nameof(OrographicBarrierFeet), 1, 20_000);
            SteerValleyFeet = Int(map, nameof(SteerValleyFeet), 1, 10_000);
            SteerShelterFeet = Int(map, nameof(SteerShelterFeet), 1, 10_000);
            SteerHillSpeed = Int(map, nameof(SteerHillSpeed), 0, 1000);
            SteerCrestLee = Int(map, nameof(SteerCrestLee), 100, 1000);
            SteerLeeFeet = Int(map, nameof(SteerLeeFeet), 1, 10_000);
            ForecastLeanDay1 = Int(map, nameof(ForecastLeanDay1), 0, 1000);
            ForecastLeanDay7 = Int(map, nameof(ForecastLeanDay7), 0, 1000);
            ForecastTempTenths = Ints(map, nameof(ForecastTempTenths), 0, 500);
            if (ForecastTempTenths.Length != 2) throw new FormatException("ForecastTempTenths needs two entries: a day out, then a week.");
            ForecastPrecipPermille = Ints(map, nameof(ForecastPrecipPermille), 0, 1000);
            if (ForecastPrecipPermille.Length != 3) throw new FormatException("ForecastPrecipPermille needs three entries: the evening before, 3 days, a week.");
            ForecastChancePermille = Ints(map, nameof(ForecastChancePermille), 0, 1000);
            if (ForecastChancePermille.Length != 3) throw new FormatException("ForecastChancePermille needs three entries: the evening before, 3 days, a week.");
            ForecastBustPermille = Int(map, nameof(ForecastBustPermille), 0, 1000);
            ForecastPhantomPermille = Int(map, nameof(ForecastPhantomPermille), 0, 1000);
            WindEventGust = Int(map, nameof(WindEventGust), 1, 500);
            ForecastTimingPermille = Int(map, nameof(ForecastTimingPermille), 0, 1000);
            ForecastDryChancePermille = Int(map, nameof(ForecastDryChancePermille), 0, 1000);
            GunWindowWetBulb = Int(map, nameof(GunWindowWetBulb), -400, 400);
            StormEventLiquid = Int(map, nameof(StormEventLiquid), 1, 1000);
            RainEventHours = Int(map, nameof(RainEventHours), 1, 24);
            ThawTemp = Int(map, nameof(ThawTemp), 0, 600);
            ThawHours = Int(map, nameof(ThawHours), 1, 24);
            RefreezeTemp = Int(map, nameof(RefreezeTemp), -200, 400);
            RefreezeHours = Int(map, nameof(RefreezeHours), 1, 24);
            if (RefreezeTemp >= ThawTemp) throw new FormatException("RefreezeTemp must be below ThawTemp.");
            BluebirdClearness = Int(map, nameof(BluebirdClearness), 0, 1000);
            InversionTenths = Int(map, nameof(InversionTenths), 1, 400);
            AlertLeadDays = Int(map, nameof(AlertLeadDays), 1, 7);
            ForecastPullRain = Int(map, nameof(ForecastPullRain), 0, 2000);
            ForecastPullStorm = Int(map, nameof(ForecastPullStorm), 0, 2000);
            ForecastPullStormGust = Int(map, nameof(ForecastPullStormGust), 1, 500);
            ForecastPullBluebird = Int(map, nameof(ForecastPullBluebird), 0, 2000);
            ForecastPullColdSnap = Int(map, nameof(ForecastPullColdSnap), 0, 2000);
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
            RideDownScore = Int(map, nameof(RideDownScore), 0, 1000);
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
            LakeWaterShare = Int(map, nameof(LakeWaterShare), 1, 1000);
            LakeMinArea = Int(map, nameof(LakeMinArea), 1, 100_000_000);
            RiverElongation = Int(map, nameof(RiverElongation), 1, 100_000);
            LakeGrowCells = Int(map, nameof(LakeGrowCells), 1, 64);
            MaxLakes = Int(map, nameof(MaxLakes), 0, 254);
            MaxPonds = Int(map, nameof(MaxPonds), 0, 254 - MaxLakes);
            LakeDepthMin = Int(map, nameof(LakeDepthMin), 1, 10_000);
            LakeDepthMax = Int(map, nameof(LakeDepthMax), LakeDepthMin, 10_000);
            StreamsDepth = Int(map, nameof(StreamsDepth), 1, 10_000);
            LakeLagPerFoot = Int(map, nameof(LakeLagPerFoot), 0, 10_000);
            IceAlpha = Int(map, nameof(IceAlpha), 1, 1000);
            IceSnowInsulation = Int(map, nameof(IceSnowInsulation), 1, 10_000);
            IceSnowDensity = Int(map, nameof(IceSnowDensity), 50, 917);
            IceSnowMax = Int(map, nameof(IceSnowMax), 1, 10_000);
            IceSnowMelt = Int(map, nameof(IceSnowMelt), 0, 10_000);
            IceRainMelt = Int(map, nameof(IceRainMelt), 0, 10_000);
            IceMeltPerDegDay = Int(map, nameof(IceMeltPerDegDay), 0, 10_000);
            LakeThawRefDepth = Int(map, nameof(LakeThawRefDepth), 1, 10_000);
            StreamsIceMax = Int(map, nameof(StreamsIceMax), 1, 100_000);
            IceOpenAt = Int(map, nameof(IceOpenAt), 0, 10_000);
            SnowCoveredAt = Int(map, nameof(SnowCoveredAt), 0, 10_000);

            SnowSurfaceUm = Int(map, nameof(SnowSurfaceUm), 1_000, 1_000_000);
            SnowThinUm = Int(map, nameof(SnowThinUm), 1, 1_000_000);
            AlbedoFresh = Int(map, nameof(AlbedoFresh), 1, 1000);
            AlbedoCold = Int(map, nameof(AlbedoCold), 1, AlbedoFresh);
            AlbedoWet = Int(map, nameof(AlbedoWet), 1, AlbedoCold);
            AlbedoGround = Int(map, nameof(AlbedoGround), 0, 1000);
            AlbedoResetUm = Int(map, nameof(AlbedoResetUm), 1, 1_000_000);
            AlbedoColdTau = Int(map, nameof(AlbedoColdTau), 1, 100_000_000);
            AlbedoWetTau = Int(map, nameof(AlbedoWetTau), 1, 100_000_000);
            CanopyShade = Int(map, nameof(CanopyShade), 0, 1000);
            SnowWindPer10k = Int(map, nameof(SnowWindPer10k), 0, 10_000);
            SnowCalmMms = Int(map, nameof(SnowCalmMms), 0, 1000);
            GroundHeatMw = Int(map, nameof(GroundHeatMw), 0, 100_000);
            SnowHoldPermille = Int(map, nameof(SnowHoldPermille), 0, 1000);
            SnowCrumbUm = Int(map, nameof(SnowCrumbUm), 0, 100_000);
            SettleTau = Int(map, nameof(SettleTau), 1, 100_000_000);
            SettleWetFactor = Int(map, nameof(SettleWetFactor), 1, 100);
            DensityCold = Int(map, nameof(DensityCold), 50, 917);
            DensityWarm = Int(map, nameof(DensityWarm), 50, 917);
            DensityWet = Int(map, nameof(DensityWet), 50, 917);
            CanopyHolds = Int(map, nameof(CanopyHolds), 0, 1000);
            ShedStartGrade = Int(map, nameof(ShedStartGrade), 1, 1000);
            ShedFullGrade = Int(map, nameof(ShedFullGrade), ShedStartGrade + 1, 2000);
            ShedRunout = Int(map, nameof(ShedRunout), 8, 400);
            ShedPileMax = Int(map, nameof(ShedPileMax), 254, 1_000_000);
            ShedPileRatio = Int(map, nameof(ShedPileRatio), 1000, 100_000);
            DriftWind = Int(map, nameof(DriftWind), 0, 2000);
            DriftWindSpan = Int(map, nameof(DriftWindSpan), 1, 2000);
            DriftShareMax = Int(map, nameof(DriftShareMax), 0, 1000);
            DriftReach = Int(map, nameof(DriftReach), 8, 400);
            DriftExposedRise = Int(map, nameof(DriftExposedRise), 0, 127);
            PowderNewUm = Int(map, nameof(PowderNewUm), 1, 100_000);
            WetPowderWetBulb = Int(map, nameof(WetPowderWetBulb), 0, 400);
            RefreezeSkin = Int(map, nameof(RefreezeSkin), -5000, 0);
            SnowRefreezeHours = Int(map, nameof(SnowRefreezeHours), 1, 3);
            CornCycles = Int(map, nameof(CornCycles), 1, 3);
            CornWindowDays = Int(map, nameof(CornWindowDays), 1, 60);
            QualityBase = Ints(map, nameof(QualityBase), 0, 1000);
            if (QualityBase.Length != 10) throw new FormatException("QualityBase needs ten types.");
            QualityAgePerHour = Int(map, nameof(QualityAgePerHour), 0, 1000);
            QualityAgeFloor = Int(map, nameof(QualityAgeFloor), 0, 1000);
            SeedDensity = Int(map, nameof(SeedDensity), 50, 917);
            SnowLandFaceMs = Int(map, nameof(SnowLandFaceMs), 0, 3_600_000);
            SnowThreads = Int(map, nameof(SnowThreads), 1, 16);
            SkylineFade = Int(map, nameof(SkylineFade), 1, 1_000_000);
            InflowSeason = Ints(map, nameof(InflowSeason), 0, 10_000);
            if (InflowSeason.Length != 12) throw new FormatException("InflowSeason needs a value for each month.");
            IntakeDepthMm = Int(map, nameof(IntakeDepthMm), 0, 30_000);
            MinPoolPermille = Int(map, nameof(MinPoolPermille), 0, 1000);
            LakeDrawdownMm = Int(map, nameof(LakeDrawdownMm), 0, 30_000);
            EvapMicronsPerDay = Int(map, nameof(EvapMicronsPerDay), 0, 100_000);
            MaxSources = Int(map, nameof(MaxSources), 0, 254);
            SolveLatencyTicks = Int(map, nameof(SolveLatencyTicks), 0, 100);
            MaxResolves = Int(map, nameof(MaxResolves), 0, 100);
            PipeClass = Int(map, nameof(PipeClass), 100, 5000);
            FineCellCap = Int(map, nameof(FineCellCap), 1, 50_000_000);
            PackedMeltFloor = Int(map, nameof(PackedMeltFloor), 0, 1000);
            WearPerPassUm = Int(map, nameof(WearPerPassUm), 0, 10_000);
            WearGradeRef = Int(map, nameof(WearGradeRef), 1, 200);
            WearFactorMin = Int(map, nameof(WearFactorMin), 0, 10_000);
            WearFactorMax = Int(map, nameof(WearFactorMax), 0, 10_000);
            if (WearFactorMax < WearFactorMin) throw new FormatException("WearFactorMax is under WearFactorMin.");
            MogulGrade = Int(map, nameof(MogulGrade), 0, 400);
            MogulsPerKPasses = Int(map, nameof(MogulsPerKPasses), 0, 255);
            PackedPasses = Int(map, nameof(PackedPasses), 1, 65_535);
            HardPasses = Int(map, nameof(HardPasses), 1, 65_535);
            CrustPasses = Int(map, nameof(CrustPasses), 1, 65_535);
            GroomReachMm = Int(map, nameof(GroomReachMm), 1000, 1_000_000);
            GroomBlend = Int(map, nameof(GroomBlend), 0, 1000);
            GroomedDensity = Int(map, nameof(GroomedDensity), 50, 917);
            GroomPack = Int(map, nameof(GroomPack), 0, 1000);
            GroomMinDepthMm = Int(map, nameof(GroomMinDepthMm), 0, 10_000);
            GroomMinWidthMm = Int(map, nameof(GroomMinWidthMm), 0, 100_000);
            GroomMaxDepthMm = Int(map, nameof(GroomMaxDepthMm), 0, 100_000);
            GroomLightReachMm = Int(map, nameof(GroomLightReachMm), 1000, 1_000_000);
            GroomLightBlend = Int(map, nameof(GroomLightBlend), 0, 1000);
            GroomsToHard = Int(map, nameof(GroomsToHard), 1, 7);
            GroomSweepMinutes = Int(map, nameof(GroomSweepMinutes), 0, 240);
            GroomNightEndsBeforeOpenMinutes = Int(map, nameof(GroomNightEndsBeforeOpenMinutes), 0, 240);
            GroomDayStartHour = Int(map, nameof(GroomDayStartHour), 0, 23);
            GroomDayEndHour = Int(map, nameof(GroomDayEndHour), 0, 23);
            if (GroomDayEndHour <= GroomDayStartHour) throw new FormatException("GroomDayEndHour must be after GroomDayStartHour.");
            GroomTravelMinutes = Int(map, nameof(GroomTravelMinutes), 0, 120);
            GroomTaskMinutes = Int(map, nameof(GroomTaskMinutes), 0, 120);
            GroomTransitKmh = Int(map, nameof(GroomTransitKmh), 1, 100);
            GroomPassMm = Int(map, nameof(GroomPassMm), 1000, 20_000);
            GroomTillCentiAcres = Models(map, nameof(GroomTillCentiAcres), 1, 100_000);
            GroomPushYd3 = Models(map, nameof(GroomPushYd3), 1, 100_000);
            GroomFuelUsd = Models(map, nameof(GroomFuelUsd), 0, 10_000);
            GroomPriceUsd = Models(map, nameof(GroomPriceUsd), 0, 100_000_000);
            GroomDrivePricePermille = Ints(map, nameof(GroomDrivePricePermille), 0, 10_000);
            if (GroomDrivePricePermille.Length != 3) throw new FormatException("GroomDrivePricePermille needs diesel, hybrid and electric.");
            GroomElectricMinutes = Int(map, nameof(GroomElectricMinutes), 1, 1440);
            GroomTillerPermille = Int(map, nameof(GroomTillerPermille), 1000, 3000);
            GroomTillerMinWidthMm = Int(map, nameof(GroomTillerMinWidthMm), 0, 100_000);
            GroomFullSpeedGrade = Int(map, nameof(GroomFullSpeedGrade), 1, 1000);
            GroomWinchGrade = Int(map, nameof(GroomWinchGrade), 1, 1000);
            GroomWinchMaxGrade = Int(map, nameof(GroomWinchMaxGrade), 1, 1000);
            GroomWinchSharePermille = Int(map, nameof(GroomWinchSharePermille), 0, 1000);
            if (GroomWinchGrade < GroomFullSpeedGrade || GroomWinchMaxGrade < GroomWinchGrade) throw new FormatException("The grooming grades must rise.");
            GroomNewSnowUm = Int(map, nameof(GroomNewSnowUm), 0, 1_000_000);
            GroomPowderKeepUm = Int(map, nameof(GroomPowderKeepUm), 0, 1_000_000);
            GroomPowderGrade = Int(map, nameof(GroomPowderGrade), 0, 1000);
            GroomPowderWarnNights = Int(map, nameof(GroomPowderWarnNights), 1, 30);
            WhaleRiseMm = Int(map, nameof(WhaleRiseMm), 1, 100_000);
            GroomCureHours = Int(map, nameof(GroomCureHours), 0, 255);
            GroomPayUsdHour = Int(map, nameof(GroomPayUsdHour), 0, 10_000);
            GroomShiftPayPermille = Ints(map, nameof(GroomShiftPayPermille), 0, 10_000);
            if (GroomShiftPayPermille.Length != 3) throw new FormatException("GroomShiftPayPermille needs swing, night and day.");
            GreenPushQuality = Int(map, nameof(GreenPushQuality), 0, 1000);
            CoverGradedMm = Int(map, nameof(CoverGradedMm), 1, 10_000);
            CoverNaturalMm = Int(map, nameof(CoverNaturalMm), 1, 10_000);
            ReadyCover = Int(map, nameof(ReadyCover), 0, 1000);
            LineWidthMm = Int(map, nameof(LineWidthMm), 100, 100_000);
            LateWalkStartFt = Int(map, nameof(LateWalkStartFt), 1, 100_000);
            LateWalkEndFt = Int(map, nameof(LateWalkEndFt), 1, 100_000);
            WalkSpeedMmS = Int(map, nameof(WalkSpeedMmS), 100, 10_000);
            QualityPassTenths = Int(map, nameof(QualityPassTenths), 0, 10_000);
            QualityPassFloor = Int(map, nameof(QualityPassFloor), 0, 1000);
            QualityMogulPermille = Int(map, nameof(QualityMogulPermille), 0, 1000);
            TasteExpert = Int(map, nameof(TasteExpert), 0, 3000);
            TasteBeginner = Int(map, nameof(TasteBeginner), 0, 3000);
            TasteMogulMin = Int(map, nameof(TasteMogulMin), 0, 255);

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
            E(nameof(SkipLandsBeforeOpenMinutes), "0"),
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
            E(nameof(ExposureHvfFeet), "50, 150, 300, 500, 800, 1200, 1800"),
            E(nameof(ExposureRiseFeet), "8"),
            E(nameof(OrographicClimbFeet), "2000"),
            E(nameof(OrographicBarrierFeet), "2000"),
            E(nameof(SteerValleyFeet), "300"),
            E(nameof(SteerShelterFeet), "1000"),
            E(nameof(SteerHillSpeed), "250"),
            E(nameof(SteerCrestLee), "700"),
            E(nameof(SteerLeeFeet), "150"),
            E(nameof(ForecastLeanDay1), "950"),
            E(nameof(ForecastLeanDay7), "850"),   // owner 2026-11-13: 0.85 keeps WX-54's 5 °F at a week at every site (0.65 missed at six)
            E(nameof(ForecastTempTenths), "20, 40"),
            E(nameof(ForecastPrecipPermille), "200, 400, 600"),
            E(nameof(ForecastChancePermille), "950, 900, 800"),
            E(nameof(ForecastBustPermille), "50"),
            E(nameof(ForecastPhantomPermille), "100"),
            E(nameof(WindEventGust), "45"),
            E(nameof(ForecastTimingPermille), "30"),
            E(nameof(ForecastDryChancePermille), "300"),
            E(nameof(GunWindowWetBulb), "270"),
            E(nameof(StormEventLiquid), "5"),
            E(nameof(RainEventHours), "2"),
            E(nameof(ThawTemp), "340"),
            E(nameof(ThawHours), "3"),
            E(nameof(RefreezeTemp), "280"),
            E(nameof(RefreezeHours), "2"),
            E(nameof(BluebirdClearness), "800"),
            E(nameof(InversionTenths), "40"),
            E(nameof(AlertLeadDays), "2"),
            E(nameof(ForecastPullRain), "500"),
            E(nameof(ForecastPullStorm), "700"),
            E(nameof(ForecastPullStormGust), "40"),
            E(nameof(ForecastPullBluebird), "1100"),
            E(nameof(ForecastPullColdSnap), "700"),
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
            E(nameof(RideDownScore), "300"),
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
            // Lakes (LK-01 v2…LK-08 v2, task 4.09):
            E(nameof(LakeWaterShare), "500"),
            E(nameof(LakeMinArea), "4047"),
            E(nameof(RiverElongation), "120"),
            E(nameof(LakeGrowCells), "6"),
            E(nameof(MaxLakes), "128"),
            E(nameof(MaxPonds), "32"),
            E(nameof(LakeDepthMin), "16"),
            E(nameof(LakeDepthMax), "1600"),
            E(nameof(StreamsDepth), "10"),
            E(nameof(LakeLagPerFoot), "20"),
            E(nameof(IceAlpha), "80"),
            E(nameof(IceSnowInsulation), "60"),
            E(nameof(IceSnowDensity), "250"),
            E(nameof(IceSnowMax), "240"),
            E(nameof(IceSnowMelt), "40"),
            E(nameof(IceRainMelt), "350"),
            E(nameof(IceMeltPerDegDay), "10"),
            E(nameof(LakeThawRefDepth), "200"),
            E(nameof(StreamsIceMax), "600"),
            E(nameof(IceOpenAt), "100"),
            E(nameof(SnowCoveredAt), "10"),
            // Natural snow (task 4.07 PR A2; the snow energy's fitted set, SnowTuning.Default):
            E(nameof(SnowSurfaceUm), "25000"),
            E(nameof(SnowThinUm), "5000"),     // SN-09 v4 (owner, 2026-11-15): was 20000
            E(nameof(AlbedoFresh), "850"),
            E(nameof(AlbedoCold), "700"),
            E(nameof(AlbedoWet), "550"),
            E(nameof(AlbedoGround), "200"),
            E(nameof(AlbedoResetUm), "5000"),
            E(nameof(AlbedoColdTau), "1728000"),
            E(nameof(AlbedoWetTau), "1209600"),
            E(nameof(CanopyShade), "700"),
            E(nameof(SnowWindPer10k), "15"),
            E(nameof(SnowCalmMms), "1"),
            E(nameof(GroundHeatMw), "2000"),
            E(nameof(SnowHoldPermille), "50"),
            E(nameof(SnowCrumbUm), "2540"),
            E(nameof(SettleTau), "2400000"),
            E(nameof(SettleWetFactor), "3"),
            E(nameof(DensityCold), "300"),
            E(nameof(DensityWarm), "380"),
            E(nameof(DensityWet), "450"),
            E(nameof(CanopyHolds), "300"),
            E(nameof(ShedStartGrade), "85"),
            E(nameof(ShedFullGrade), "175"),
            E(nameof(ShedRunout), "64"),
            E(nameof(ShedPileMax), "12700"),
            E(nameof(ShedPileRatio), "3000"),
            E(nameof(DriftWind), "200"),
            E(nameof(DriftWindSpan), "600"),
            E(nameof(DriftShareMax), "300"),
            E(nameof(DriftReach), "100"),
            E(nameof(DriftExposedRise), "5"),
            E(nameof(PowderNewUm), "2540"),
            E(nameof(WetPowderWetBulb), "310"),
            E(nameof(RefreezeSkin), "-222"),
            E(nameof(SnowRefreezeHours), "2"),
            E(nameof(CornCycles), "3"),
            E(nameof(CornWindowDays), "5"),
            E(nameof(QualityBase), "960, 900, 940, 680, 160, 840, 420, 640, 500, 780"),
            E(nameof(QualityAgePerHour), "4"),
            E(nameof(QualityAgeFloor), "600"),
            E(nameof(SeedDensity), "300"),
            E(nameof(SnowLandFaceMs), "312000"),
            E(nameof(SnowThreads), "4"),
            E(nameof(SkylineFade), "52000"),
            E(nameof(InflowSeason), "600,600,1000,1500,1000,1000,1000,1000,1000,800,800,600"),
            E(nameof(IntakeDepthMm), "1829"),
            E(nameof(MinPoolPermille), "100"),
            E(nameof(LakeDrawdownMm), "914"),
            E(nameof(EvapMicronsPerDay), "254"),
            E(nameof(MaxSources), "64"),
            E(nameof(SolveLatencyTicks), "5"),
            E(nameof(MaxResolves), "10"),
            E(nameof(PipeClass), "1000"),
            E(nameof(FineCellCap), "2000000"),
            E(nameof(PackedMeltFloor), "750"),
            E(nameof(WearPerPassUm), "5"),
            E(nameof(WearGradeRef), "20"),
            E(nameof(WearFactorMin), "500"),
            E(nameof(WearFactorMax), "2000"),
            E(nameof(MogulGrade), "40"),
            E(nameof(MogulsPerKPasses), "128"),
            E(nameof(PackedPasses), "200"),
            E(nameof(HardPasses), "800"),
            E(nameof(CrustPasses), "200"),
            E(nameof(GroomReachMm), "15240"),
            E(nameof(GroomBlend), "700"),
            E(nameof(GroomedDensity), "400"),
            E(nameof(GroomPack), "600"),
            E(nameof(GroomMinDepthMm), "305"),
            E(nameof(GroomMinWidthMm), "3962"),
            E(nameof(GroomMaxDepthMm), "3048"),
            E(nameof(GroomLightReachMm), "4572"),
            E(nameof(GroomLightBlend), "300"),
            E(nameof(GroomsToHard), "5"),
            E(nameof(GroomSweepMinutes), "0"),
            E(nameof(GroomNightEndsBeforeOpenMinutes), "0"),
            E(nameof(GroomDayStartHour), "8"),
            E(nameof(GroomDayEndHour), "16"),
            E(nameof(GroomTravelMinutes), "30"),
            E(nameof(GroomTaskMinutes), "10"),
            E(nameof(GroomTransitKmh), "20"),
            E(nameof(GroomPassMm), "5500"),
            E(nameof(GroomTillCentiAcres), "700,670,850,810"),
            E(nameof(GroomPushYd3), "800,760,1100,1050"),
            E(nameof(GroomFuelUsd), "45,47,55,58"),
            E(nameof(GroomPriceUsd), "500000,650000,650000,820000"),
            E(nameof(GroomDrivePricePermille), "1000,1200,1400"),
            E(nameof(GroomElectricMinutes), "300"),
            E(nameof(GroomTillerPermille), "1120"),
            E(nameof(GroomTillerMinWidthMm), "9144"),
            E(nameof(GroomFullSpeedGrade), "40"),
            E(nameof(GroomWinchGrade), "70"),
            E(nameof(GroomWinchMaxGrade), "100"),
            E(nameof(GroomWinchSharePermille), "100"),
            E(nameof(GroomNewSnowUm), "5000"),
            E(nameof(GroomPowderKeepUm), "10000"),
            E(nameof(GroomPowderGrade), "35"),
            E(nameof(GroomPowderWarnNights), "3"),
            E(nameof(WhaleRiseMm), "914"),
            E(nameof(GroomCureHours), "24"),
            E(nameof(GroomPayUsdHour), "32"),
            E(nameof(GroomShiftPayPermille), "1100,1200,1000"),
            E(nameof(GreenPushQuality), "700"),
            E(nameof(CoverGradedMm), "152"),
            E(nameof(CoverNaturalMm), "305"),
            E(nameof(ReadyCover), "900"),
            E(nameof(LineWidthMm), "1219"),
            E(nameof(LateWalkStartFt), "60"),
            E(nameof(LateWalkEndFt), "250"),
            E(nameof(WalkSpeedMmS), "1341"),
            E(nameof(QualityPassTenths), "3"),
            E(nameof(QualityPassFloor), "500"),
            E(nameof(QualityMogulPermille), "300"),
            E(nameof(TasteExpert), "1200"),
            E(nameof(TasteBeginner), "500"),
            E(nameof(TasteMogulMin), "64"),
        };

        static KeyValuePair<string, string> E(string name, string value) => new KeyValuePair<string, string>(name, value);

        static int Int(Dictionary<string, string> map, string name, int min, int max)
        {
            if (!int.TryParse(map[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < min || v > max)
                throw new FormatException($"{name} must be a whole number from {min} to {max}.");
            return v;
        }

        /// <summary>One whole number per groomer model (400, 400 W, 800, 800 W; GO-01).</summary>
        static int[] Models(Dictionary<string, string> map, string name, int min, int max)
        {
            var v = Ints(map, name, min, max);
            if (v.Length != 4) throw new FormatException($"{name} needs the 400, the 400 W, the 800 and the 800 W.");
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
