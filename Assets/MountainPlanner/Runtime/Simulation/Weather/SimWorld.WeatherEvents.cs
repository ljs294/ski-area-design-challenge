using System;
using System.IO;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.05 PR C, WX-41 v1: the named weather events, detected each face hour from the reference points' samples alone
    /// (WX-35: never the hidden state, never a lift), so the same seed names the same weather whatever the player builds. A
    /// lasting event (storm, rain, freezing rain, thaw, wind, inversion) raises <see cref="SimEventType.WeatherEventBegan"/>
    /// and later <see cref="SimEventType.WeatherEventEnded"/>; a moment (big storm, storm of the season, powder day, bluebird,
    /// refreeze, cold snap) raises only the first. The running counts are state: hashed and saved (sidecar v6).
    /// </summary>
    public sealed partial class SimWorld
    {
        /// <summary>The lasting events under way now (bits of <see cref="WeatherEventKind"/>).</summary>
        int _evActive;
        /// <summary>The summit's liquid the hour before when it snowed there (0.01 in), for the two-hour storm window.</summary>
        int _evSummitLast;
        int _evRainRun, _evThawRun, _evColdRun;
        /// <summary>A thaw or rain happened since the last refreeze.</summary>
        bool _evNeedsRefreeze;
        /// <summary>The storm last seen (year × 1000 + ordinal, −1 none), the season's biggest so far (0.01 in), and the face hour the last storm ended.</summary>
        int _evStormKey = -1, _evSeasonBiggest;
        long _evStormEnded = long.MinValue / 2;
        /// <summary>The overnight snow at mid since the stop (0.1 in), and whether the night's window is open (stop to landing).</summary>
        int _evOvernightMid;
        bool _evOvernightOpen;
        /// <summary>Today's zref high (0.1 °F) and the resort's hours' clearness so far (sum, hours).</summary>
        int _evDayHigh = int.MinValue, _evClearSum, _evClearHours;
        /// <summary>WX-42: the target face day each alert kind last went out for (the dedupe key and its one-day cooldown).</summary>
        readonly int[] _alertLastDay = NewAlertDays();
        /// <summary>The overnight snow the last landing read (0.1 in): the HUD's "new snow overnight". Saved with the rest.</summary>
        int _evLastOvernight;

        const int EventKinds = 13;

        static int[] NewAlertDays()
        {
            var a = new int[EventKinds];
            for (int i = 0; i < a.Length; i++) a[i] = int.MinValue / 2;
            return a;
        }

        public int WeatherEventsNow => _evActive;
        /// <summary>WX-44: today's two weather lines of the demand (‰), as the landing set them.</summary>
        internal int PowderPullToday => _powderPull;
        internal int ForecastPullToday => _forecastPull;
        public int OvernightSnowMid => _evLastOvernight;

        void ResetWeatherEvents()
        {
            _evActive = 0; _evSummitLast = 0; _evRainRun = _evThawRun = _evColdRun = 0; _evNeedsRefreeze = false;
            _evStormKey = -1; _evSeasonBiggest = 0; _evStormEnded = long.MinValue / 2;
            _evOvernightMid = 0; _evOvernightOpen = false; _evDayHigh = int.MinValue; _evClearSum = _evClearHours = 0;
            _evLastOvernight = 0;
            for (int i = 0; i < _alertLastDay.Length; i++) _alertLastDay[i] = int.MinValue / 2;
        }

        /// <summary>After the hour's weather and samples (HourStepWeather): the hour's events.</summary>
        void StepWeatherEvents(long hour)
        {
            if (_weather == null || !_wxHourSet) return;
            var rows = _weather.Rows;
            var h = _wxHour;
            int faceDay = (int)(hour / 24);
            int hourOfDay = (int)(hour % 24);
            if (h.HourOfYear == 0) { _evSeasonBiggest = 0; }
            if (hourOfDay == 0) { _evDayHigh = int.MinValue; _evClearSum = 0; _evClearHours = 0; }

            // Storm: ≥ StormEventLiquid at the summit over two face hours, snow phase there.
            int summit = _wxSummit.SnowPermille >= 500 ? _wxSummit.Liquid : 0;
            Lasting(WeatherEventKind.Storm, summit + _evSummitLast >= Pacing.StormEventLiquid, faceDay);
            _evSummitLast = summit;

            // Big storm and storm of the season: when a new storm starts, by its drawn zref total against the third's fits.
            if (h.StormOrdinal >= 0)
            {
                int key = h.LabelYear * 1000 + h.StormOrdinal;
                if (key != _evStormKey)
                {
                    _evStormKey = key;
                    int t = h.Third;
                    if (h.StormTotal > rows.Quantile(t, WeatherRows.StormTotal, QuantileIndex(7500)))
                        Moment(WeatherEventKind.BigStorm, h.StormTotal);
                    if (h.StormTotal > rows.Quantile(t, WeatherRows.StormTotal, QuantileIndex(9500)) && h.StormTotal > _evSeasonBiggest)
                        Moment(WeatherEventKind.StormOfTheSeason, h.StormTotal);
                    _evSeasonBiggest = Math.Max(_evSeasonBiggest, h.StormTotal);
                }
            }
            if (h.StormOrdinal >= 0 || h.Liquid > 0) _evStormEnded = hour;   // the last wet hour so far (bluebird's "after a storm")

            // Rain: rain or mixed at the base for RainEventHours.
            bool rain = _wxBase.Liquid > 0 && _wxBase.SnowPermille < 1000 && !_wxBase.FreezingRain;
            _evRainRun = rain ? _evRainRun + 1 : 0;
            if (_evRainRun >= Pacing.RainEventHours) _evNeedsRefreeze = true;
            Lasting(WeatherEventKind.Rain, rain && (_evRainRun >= Pacing.RainEventHours || IsActive(WeatherEventKind.Rain)), faceDay);

            // Freezing rain: WX-38's glaze at the base.
            Lasting(WeatherEventKind.FreezingRain, _wxBase.FreezingRain && _wxBase.Liquid > 0, faceDay);

            // Thaw: mid above ThawTemp for ThawHours, December to February.
            int month = Calendar.Month(faceDay % Calendar.YearFaceDays);
            bool winter = month == 12 || month == 1 || month == 2;
            bool warm = _wxMid.Temp > Pacing.ThawTemp;
            _evThawRun = warm ? _evThawRun + 1 : 0;
            bool thaw = warm && winter && (_evThawRun >= Pacing.ThawHours || IsActive(WeatherEventKind.Thaw));
            if (thaw) _evNeedsRefreeze = true;
            Lasting(WeatherEventKind.Thaw, thaw, faceDay);

            // Refreeze: after a thaw or rain, mid below RefreezeTemp for RefreezeHours.
            _evColdRun = _wxMid.Temp < Pacing.RefreezeTemp ? _evColdRun + 1 : 0;
            if (_evNeedsRefreeze && _evColdRun >= Pacing.RefreezeHours) { _evNeedsRefreeze = false; Moment(WeatherEventKind.Refreeze, faceDay); }

            // Wind event and inversion.
            Lasting(WeatherEventKind.WindEvent, _wxSummit.Gust >= Pacing.WindEventGust * 10, faceDay);
            Lasting(WeatherEventKind.Inversion, _wxMid.Temp - _wxBase.Temp >= Pacing.InversionTenths, faceDay);

            // Cold snap: the day's zref high below the row's 10 % daily maximum, judged at the day's last hour.
            _evDayHigh = Math.Max(_evDayHigh, h.Temp);
            if (hourOfDay == 23 && _evDayHigh < rows.MaxQ10[h.Third]) Moment(WeatherEventKind.ColdSnap, faceDay);

            // The night's snow at mid (WX-41's powder window) and the resort's hours' sky (bluebird).
            if (_evOvernightOpen) _evOvernightMid += _wxMid.SnowTenthsIn;
            long sod = (long)hourOfDay * HourMs;
            if (sod >= _todayOpen * 1000L && sod < _todayStop * 1000L) { _evClearSum += h.Clearness; _evClearHours++; }
        }

        bool IsActive(WeatherEventKind k) => (_evActive & (1 << (int)k)) != 0;

        void Lasting(WeatherEventKind k, bool now, int faceDay)
        {
            bool was = IsActive(k);
            if (now == was) return;
            if (now) { _evActive |= 1 << (int)k; Events.Add(Tick, SimEventType.WeatherEventBegan, -1, faceDay, (ushort)k); }
            else { _evActive &= ~(1 << (int)k); Events.Add(Tick, SimEventType.WeatherEventEnded, -1, faceDay, (ushort)k); }
        }

        void Moment(WeatherEventKind k, long value) => Events.Add(Tick, SimEventType.WeatherEventBegan, -1, value, (ushort)k);

        /// <summary>At the stop: the day's bluebird, then the night's powder window opens.</summary>
        void WeatherAtStop(long faceMs)
        {
            if (_weather == null) return;
            int faceDay = (int)(faceMs / DayMs);
            long openHour = (long)faceDay * 24 + _todayOpen / 3600;
            if (_evClearHours > 0 && _evClearSum / _evClearHours > Pacing.BluebirdClearness && openHour - _evStormEnded <= 24 && !IsActive(WeatherEventKind.Storm))
                Moment(WeatherEventKind.Bluebird, faceDay);
            _evOvernightOpen = true;
            _evOvernightMid = 0;
        }

        /// <summary>At the landing (or the opening, when a game starts there): the night's snow is read, a powder day named, the crowd's lines set.</summary>
        void WeatherAtLanding(int faceDay)
        {
            if (_weather != null)
            {
                _evLastOvernight = _evOvernightOpen ? _evOvernightMid : 0;
                int p = _weather.Rows.PowderDayTenthsIn;
                if (_evOvernightOpen && p > 0 && _evOvernightMid >= p) Moment(WeatherEventKind.PowderDay, _evOvernightMid);
            }
            WeatherPullForDay(faceDay);
            _evOvernightOpen = false;
        }

        void HashWeatherEvents(ref StateHasher h)
        {
            h.Add(_evActive); h.Add(_evSummitLast); h.Add(_evRainRun); h.Add(_evThawRun); h.Add(_evColdRun); h.Add(_evNeedsRefreeze ? 1 : 0);
            h.Add(_evStormKey); h.Add(_evSeasonBiggest); h.Add(_evStormEnded); h.Add(_evOvernightMid); h.Add(_evOvernightOpen ? 1 : 0);
            h.Add(_evDayHigh); h.Add(_evClearSum); h.Add(_evClearHours); h.Add(_evLastOvernight);
            foreach (int d in _alertLastDay) h.Add(d);
            h.Add(_fcIssueHour); h.Add(_fcEvening ? 1 : 0);
        }

        // ================= the sidecar (v6) =================

        /// <summary>v6's block, after the weather's: the forecast's issue (the sheet is made again from it) and the events' counts.</summary>
        void WriteWeatherEventState(SidecarWriter o)
        {
            o.I64(_fcIssueHour); o.Bool(_fcEvening);
            o.I32(_evActive); o.I32(_evSummitLast); o.I32(_evRainRun); o.I32(_evThawRun); o.I32(_evColdRun); o.Bool(_evNeedsRefreeze);
            o.I32(_evStormKey); o.I32(_evSeasonBiggest); o.I64(_evStormEnded); o.I32(_evOvernightMid); o.Bool(_evOvernightOpen);
            o.I32(_evDayHigh); o.I32(_evClearSum); o.I32(_evClearHours); o.I32(_evLastOvernight);
            o.I32(_alertLastDay.Length);
            foreach (int d in _alertLastDay) o.I32(d);
        }

        void ReadWeatherEventState(SidecarReader r)
        {
            _fcIssueHour = r.I64(); _fcEvening = r.Bool();
            if (_fcIssueHour < -1) throw new InvalidDataException("The forecast's issue is out of range.");
            _evActive = r.I32(); _evSummitLast = r.I32(); _evRainRun = r.I32(); _evThawRun = r.I32(); _evColdRun = r.I32(); _evNeedsRefreeze = r.Bool();
            _evStormKey = r.I32(); _evSeasonBiggest = r.I32(); _evStormEnded = r.I64(); _evOvernightMid = r.I32(); _evOvernightOpen = r.Bool();
            _evDayHigh = r.I32(); _evClearSum = r.I32(); _evClearHours = r.I32(); _evLastOvernight = r.I32();
            int n = r.I32();
            if (n != _alertLastDay.Length) throw new InvalidDataException("The save's alert days have the wrong shape.");
            for (int i = 0; i < n; i++) _alertLastDay[i] = r.I32();
            _fcStale = true;
        }

        /// <summary>A save from before PR C (v1–v5): no events under way, and a forecast issued at the next stop or landing.</summary>
        void StartWeatherEventsForOlderSave()
        {
            ResetWeatherEvents();
            _fcIssueHour = -1;
            _fcEvening = false;
            _fcStale = false;
        }
    }
}
