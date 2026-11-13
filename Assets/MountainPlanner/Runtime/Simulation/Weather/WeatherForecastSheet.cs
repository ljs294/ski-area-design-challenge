using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// WX-41 v1: the named weather events the weather step emits (the snow step's, rain on snow, glaze and corn morning, are
    /// 4.07's). The numbers are an event's Detail and a bit in the masks (1 &lt;&lt; kind): saved, never renumber.
    /// </summary>
    public enum WeatherEventKind : byte
    {
        None = 0,
        /// <summary>≥ <c>StormEventLiquid</c> at the summit over two face hours, snow phase there.</summary>
        Storm = 1,
        /// <summary>The storm's drawn zref total is above the third's fitted 75 % storm.</summary>
        BigStorm = 2,
        /// <summary>The season's largest storm so far, when above the third's fitted 95 %.</summary>
        StormOfTheSeason = 3,
        /// <summary>The overnight snow at mid (yesterday's stop to the landing) reached the site's powder day.</summary>
        PowderDay = 4,
        /// <summary>Clear from opening to stop within a face day of a storm's end.</summary>
        Bluebird = 5,
        /// <summary>Rain or mixed at the base for <c>RainEventHours</c>.</summary>
        Rain = 6,
        /// <summary>WX-38's freezing rain at the base.</summary>
        FreezingRain = 7,
        /// <summary>Mid air above <c>ThawTemp</c> for <c>ThawHours</c>, December to February.</summary>
        Thaw = 8,
        /// <summary>After a thaw or rain, mid below <c>RefreezeTemp</c> for <c>RefreezeHours</c>.</summary>
        Refreeze = 9,
        /// <summary>The day's zref high below the row's 10 % daily maximum.</summary>
        ColdSnap = 10,
        /// <summary>A summit gust at or above <c>WindEventGust</c>.</summary>
        WindEvent = 11,
        /// <summary>The base colder than mid by <c>InversionTenths</c>.</summary>
        Inversion = 12,
    }

    /// <summary>WX-05 v3: a day card's words, hedged by lead. Shown only: never saved.</summary>
    public enum ForecastWords : byte
    {
        Unknown = 0, Fair = 1, Unsettled = 2,
        SnowPossible = 3, SnowLikely = 4, Snow = 5,
        RainPossible = 6, RainLikely = 7, Rain = 8,
        Mixed = 9,
    }

    /// <summary>WX-05 v3's pattern outlook line (WX-57).</summary>
    public enum ForecastOutlook : byte { None = 0, Active = 1, Ridge = 2 }

    /// <summary>One hour of tonight or tomorrow on the evening sheet (shown values; 0.1 °F, 0.1 in, 0.1 mph, ‰).</summary>
    public struct ForecastHour
    {
        /// <summary>The face hour (since the world's start) this line is.</summary>
        public long Hour;
        public int TempBase, TempMid, TempSummit, WetBulbBase, WetBulbMid, WetBulbSummit;
        public int SnowBase, SnowMid, SnowSummit;
        public bool RainBase;
        public int Cloud, WindSummit, GustSummit;
    }

    /// <summary>One of the seven day cards (shown values; ranges low–high; 0.1 °F, 0.1 in, 0.01 in, 0.1 mph, ‰).</summary>
    public struct ForecastDay
    {
        /// <summary>The target face day and the lead from the issue to its middle, face hours.</summary>
        public int FaceDay, LeadHours;
        public ForecastWords Words;
        public int ChancePermille;
        public int HighBaseLo, HighBaseHi, LowBaseLo, LowBaseHi, HighSummitLo, HighSummitHi, LowSummitLo, LowSummitHi;
        /// <summary>The single values the ranges are centred on (shown when the day is near).</summary>
        public int HighBase, LowBase, HighSummit, LowSummit;
        public int SnowSummitLo, SnowSummitHi, SnowBaseLo, SnowBaseHi, LiquidLo, LiquidHi;
        /// <summary>The snow line's elevation (ft), or −1 when nothing falls.</summary>
        public int SnowLineFeet;
        public int WindSummit, GustSummit;
        /// <summary>The storm's day is firm (≤ 3 days out); its arrival octant and the octant it turns to, −1 unnamed.</summary>
        public bool StormFirm;
        public int StormOctant, TurnOctant;
        /// <summary>Rain or mixed at the base (the crowd's read, WX-44).</summary>
        public bool RainAtBase;
        /// <summary>
        /// WX-44 v2 (owner 2026-11-13): rain hours in a row at the base inside the fixed ski day (<see cref="MountainPlanner.Domain.Design.DayHours.Default"/>,
        /// never the resort's own hours, WX-05 v3), the only rain the crowd's forecast line reads.
        /// </summary>
        public bool RainWhileOpen;
        /// <summary>The named events the card expects (bits of <see cref="WeatherEventKind"/>).</summary>
        public int Expected;

        public bool Expects(WeatherEventKind k) => (Expected & (1 << (int)k)) != 0;
    }

    /// <summary>
    /// WX-05 v3: the evening sheet, issued at the stop and refreshed at the landing. Fixed size (SI-16): every array is made
    /// once. The forecast isn't saved (WX-32): the world keeps the issue's hour and makes the same sheet again on a load.
    /// </summary>
    public sealed class ForecastSheet
    {
        public const int Days = 7, Hours = 48, Bands = 3;

        /// <summary>False until the first issue.</summary>
        public bool Known;
        /// <summary>The face hour (since the world's start) the sheet was issued at, its face day, and whether it was the evening issue.</summary>
        public long IssueHour = -1;
        public int IssueFaceDay;
        public bool Evening;
        public int HourCount;
        public readonly ForecastHour[] Hourly = new ForecastHour[Hours];
        public int DayCount;
        public readonly ForecastDay[] Cards = new ForecastDay[Days];
        public ForecastOutlook Outlook;
        public bool OutlookFirm;
        /// <summary>The face day the outlook's first new pattern starts on.</summary>
        public int OutlookDay;
        // ---- WX-43's named fields: what the player's automation reads (never the generator) ----
        /// <summary>Tonight's gun window: hours at or below <c>GunWindowWetBulb</c>, base, mid, summit.</summary>
        public readonly int[] GunWindowHours = new int[Bands];
        /// <summary>Tonight's and tomorrow's snow (0.1 in), base, mid, summit.</summary>
        public readonly int[] TonightSnow = new int[Bands], TomorrowSnow = new int[Bands];
        /// <summary>Tomorrow's summit gust (0.1 mph).</summary>
        public int TomorrowSummitGust;
        /// <summary>The next face day the cards expect a thaw, or −1.</summary>
        public int NextThawDay = -1;
        /// <summary>Tomorrow's forecast gust at each lift's top station (0.1 mph), for the lift card (4.11).</summary>
        public readonly int[] LiftGustTenths;

        public ForecastSheet(int maxLifts) { LiftGustTenths = new int[Math.Max(0, maxLifts)]; }

        public void Clear()
        {
            Known = false; IssueHour = -1; IssueFaceDay = 0; Evening = false; HourCount = 0; DayCount = 0;
            Outlook = ForecastOutlook.None; OutlookFirm = false; OutlookDay = 0;
            Array.Clear(Hourly, 0, Hourly.Length); Array.Clear(Cards, 0, Cards.Length);
            Array.Clear(GunWindowHours, 0, Bands); Array.Clear(TonightSnow, 0, Bands); Array.Clear(TomorrowSnow, 0, Bands);
            TomorrowSummitGust = 0; NextThawDay = -1;
            Array.Clear(LiftGustTenths, 0, LiftGustTenths.Length);
        }

        public void CopyFrom(ForecastSheet o)
        {
            Known = o.Known; IssueHour = o.IssueHour; IssueFaceDay = o.IssueFaceDay; Evening = o.Evening;
            HourCount = o.HourCount; DayCount = o.DayCount;
            Array.Copy(o.Hourly, Hourly, Hours); Array.Copy(o.Cards, Cards, Days);
            Outlook = o.Outlook; OutlookFirm = o.OutlookFirm; OutlookDay = o.OutlookDay;
            Array.Copy(o.GunWindowHours, GunWindowHours, Bands); Array.Copy(o.TonightSnow, TonightSnow, Bands); Array.Copy(o.TomorrowSnow, TomorrowSnow, Bands);
            TomorrowSummitGust = o.TomorrowSummitGust; NextThawDay = o.NextThawDay;
            Array.Copy(o.LiftGustTenths, LiftGustTenths, Math.Min(LiftGustTenths.Length, o.LiftGustTenths.Length));
        }

        /// <summary>The card for a face day, or −1.</summary>
        public int CardOf(int faceDay)
        {
            for (int k = 0; k < DayCount; k++) if (Cards[k].FaceDay == faceDay) return k;
            return -1;
        }

        /// <summary>The snow (0.1 in) the hourly lines expect at a band in the next <paramref name="hours"/> from <paramref name="fromHour"/>.</summary>
        public int SnowNext(long fromHour, int hours, int band)
        {
            int sum = 0;
            for (int i = 0; i < HourCount; i++)
            {
                var h = Hourly[i];
                if (h.Hour < fromHour || h.Hour >= fromHour + hours) continue;
                sum += band == 0 ? h.SnowBase : band == 1 ? h.SnowMid : h.SnowSummit;
            }
            return sum;
        }

        public int SizeBytes => 64 + Hours * 64 + Days * 128 + 64 + LiftGustTenths.Length * 4;
    }
}
