using System;
using System.IO;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>One elevation's weather this hour, for the snapshot (the readout, later the HUD): never in the state's hash.</summary>
    public struct WeatherSample
    {
        public int Feet;
        /// <summary>Air and dew point (0.1 °F), wet bulb (0.1 °F), snow share of the liquid (‰).</summary>
        public int Temp, Dew, WetBulb, SnowPermille;
        /// <summary>Liquid this face hour (0.01 in) and the snow it makes (0.1 in).</summary>
        public int Liquid, SnowTenthsIn;
        /// <summary>Wind and gust at this height's exposure (0.1 mph).</summary>
        public int Wind, Gust;
    }

    /// <summary>
    /// Task 4.05 PR B1: the weather in the world. Each face hour the generator (WX-21…34, <see cref="WeatherGenerator"/>)
    /// makes the hour at zref; the hour step then writes each lift's top-station gust (LO-11, WX-39) and the reference points'
    /// samples (WX-35). The weather is a function of the rows, the season seed and the face hour alone (WX-33): it never
    /// reads the design, the snow or the skiers, and a skip runs every hour, so a skipped night is a played one.
    /// - **October 1** (WX-56): the generator resets under the season's seed. A seed changed mid-year, a load and a new game
    ///   re-run it from October 1 to the hour (<see cref="WeatherGenerator.RunTo"/>, under 50 ms), so the hour after is the
    ///   hour that seed gives.
    /// - **Rows that change** (WX-32 v2, plan decision 2): an area that gets its weather after a save keeps playing the rows
    ///   the save ran with until the next October 1, so this winter's character never changes under the player; then the
    ///   new rows take over. Only reference points that move (the same rows) apply at once.
    /// - **No weather at all** (tests, the title): the stand-in hour, 23 °F and light wind, no gust.
    /// </summary>
    public sealed partial class SimWorld
    {
        readonly WeatherTuning _tuning;
        /// <summary>The weather this label year plays, or null (the stand-in).</summary>
        SimWeather _weather;
        /// <summary>Rows that arrived mid-year: they take over at the next October 1.</summary>
        SimWeather _weatherNext;
        WeatherGenerator _gen;
        /// <summary>What the generator was last reset with (WX-56); a different seed or year re-runs it.</summary>
        ulong _genSeed;
        int _genYear = int.MinValue;
        WeatherHour _wxHour;
        bool _wxHourSet;
        // ---- the snapshot's samples (worked out from the hour; not state) ----
        WeatherSample _wxBase, _wxMid, _wxSummit;

        /// <summary>The weather playing now, or null.</summary>
        public SimWeather Weather => _weather;
        /// <summary>Rows waiting for October 1.</summary>
        public SimWeather WeatherNext => _weatherNext;
        /// <summary>The last face hour's weather at zref (default before the first hour).</summary>
        public WeatherHour WeatherNow => _wxHour;
        public bool WeatherKnown => _wxHourSet;

        /// <summary>The weather a world starts with or adopts when it had none: a new generator, run to the hour at the next step.</summary>
        void AdoptWeather(SimWeather w)
        {
            _weather = w;
            _gen = w == null ? null : new WeatherGenerator(w.Rows, _tuning);
            _genYear = int.MinValue;
            _wxHourSet = false;
            _wxHour = default;
        }

        /// <summary>The <see cref="SimCommandKind.SetWeather"/> command: see the class's rules.</summary>
        bool ApplyWeather(SimWeather w)
        {
            if (w == null) return false;
            if (_weather == null) { AdoptWeather(w); return true; }
            if (w.RowsHash == _weather.RowsHash)
            {
                // The same rows: the reference points move at once (the generator never reads them); nothing waits.
                _weather = w;
                _weatherNext = null;
                SampleWeather();
                return true;
            }
            _weatherNext = w;
            return true;
        }

        /// <summary>The hour step's weather (WX-06's call order: first).</summary>
        void HourStepWeather(long hour)
        {
            _weatherHour = hour;
            if (_weather == null)
            {
                _weatherTenthsF = 230;   // 23 °F, light wind: the stand-in hour (no rows)
                _weatherWindMph = 5;
                Array.Clear(_weatherGustTenths, 0, _weatherGustTenths.Length);
                return;
            }
            long yearHours = (long)Calendar.YearFaceDays * 24;
            int labelYear = StartYear + (int)(hour / yearHours);
            int hourOfYear = (int)(hour % yearHours);
            if (hourOfYear == 0 && _weatherNext != null)
            {
                AdoptWeather(_weatherNext);
                _weatherNext = null;
            }
            if (_genYear != labelYear || _genSeed != SeasonSeed || _gen.Hour != hourOfYear)
            {
                _gen.RunTo(SeasonSeed, labelYear, hourOfYear);
                _genSeed = SeasonSeed;
                _genYear = labelYear;
            }
            _gen.Step(ref _wxHour);
            _wxHourSet = true;
            _weatherTenthsF = _wxHour.Temp;
            _weatherWindMph = _wxHour.Wind / 10;
            for (int i = 0; i < _liftCount; i++)
            {
                int top = Design.Lifts[i].TopFeet;
                _weatherGustTenths[i] = GustTenthsAt(top > 0 ? top : _weather.SummitFeet);
            }
            SampleWeather();
        }

        /// <summary>WX-39 (B1): the hour's gust at a height, 0.1 mph: the free wind × the height's exposure × the gust factor.</summary>
        int GustTenthsAt(int feet)
        {
            long wind = (long)_wxHour.Wind * _weather.ExposureAt(Pacing, feet) / 1000;
            return (int)Math.Min(5000, wind * _wxHour.GustFactor / 1000);
        }

        void SampleWeather()
        {
            if (_weather == null || !_wxHourSet) return;
            _wxBase = SampleAt(_weather.BaseFeet);
            _wxMid = SampleAt(_weather.MidFeet);
            _wxSummit = SampleAt(_weather.SummitFeet);
        }

        WeatherSample SampleAt(int feet)
        {
            var p = WeatherPoint.At(_wxHour, _weather.Rows.ZrefFeet, feet);
            long wind = (long)_wxHour.Wind * _weather.ExposureAt(Pacing, feet) / 1000;
            return new WeatherSample
            {
                Feet = feet, Temp = p.Temp, Dew = p.Dew, WetBulb = (int)Math.Round(p.WetBulbF * 10), SnowPermille = (int)Math.Round(p.SnowShare * 1000),
                Liquid = p.Liquid, SnowTenthsIn = p.SnowTenthsIn, Wind = (int)wind, Gust = GustTenthsAt(feet),
            };
        }

        void WriteWeather(SimSnapshot s)
        {
            s.WeatherKnown = _wxHourSet;
            s.WeatherGeneric = _weather != null && _weather.Generic;
            s.WeatherWaiting = _weatherNext != null;
            s.WeatherHour = _wxHour;
            s.WeatherBase = _wxBase; s.WeatherMid = _wxMid; s.WeatherSummit = _wxSummit;
            for (int i = 0; i < _liftCount; i++) s.LiftGustTenths[i] = _weatherGustTenths[i];
        }

        /// <summary>SI-08: the weather's part of the hash. The generator's state is a function of what's hashed here (WX-33).</summary>
        void HashWeather(ref StateHasher h)
        {
            HashWeather(ref h, _weather);
            HashWeather(ref h, _weatherNext);
            if (_weather == null) return;
            h.Add(_genSeed); h.Add(_genYear); h.Add(_gen.Hour);
        }

        static void HashWeather(ref StateHasher h, SimWeather w)
        {
            if (w == null) { h.Add(0); return; }
            h.Add(w.RowsHash); h.Add(w.Generic ? 1 : 0); h.Add(w.BaseFeet); h.Add(w.SummitFeet);
        }

        // ================= the sidecar (v5) =================

        /// <summary>
        /// v5's weather block: the rows playing (and any waiting for October 1), each with its reference points, then what the
        /// generator was reset with and the hour it has reached. The generator's own state isn't written: it is the hour
        /// <see cref="WeatherGenerator.RunTo"/> reaches from October 1 (WX-56), so a reader rebuilds it exactly.
        /// </summary>
        void WriteWeatherState(SidecarWriter o)
        {
            WriteWeather(o, _weather);
            WriteWeather(o, _weatherNext);
            if (_weather == null) return;
            o.U64(_genSeed); o.I32(_genYear); o.I32(_gen.Hour);
        }

        static void WriteWeather(SidecarWriter o, SimWeather w)
        {
            o.Bool(w != null);
            if (w == null) return;
            o.Bool(w.Generic); o.I32(w.BaseFeet); o.I32(w.SummitFeet);
            o.U64(w.RowsHash);
            w.Rows.Visit(new RowsOut(o));
        }

        void ReadWeatherState(SidecarReader r)
        {
            _weather = ReadWeather(r);
            _weatherNext = ReadWeather(r);
            _gen = null;
            _wxHourSet = false;
            _wxHour = default;
            if (_weather == null) return;
            _gen = new WeatherGenerator(_weather.Rows, _tuning);
            _genSeed = r.U64(); _genYear = r.I32();
            int at = r.I32();
            if (at < 0 || at > Calendar.YearFaceDays * 24 + 1) throw new InvalidDataException("The weather's hour is out of range.");
            if (_genYear != int.MinValue && (_genYear < StartYear || _genYear > 9100)) throw new InvalidDataException("The weather's year is out of range.");
            if (_genYear == int.MinValue || at == 0) return;   // never stepped: the next hour runs it to the hour
            _gen.RunTo(_genSeed, _genYear, at - 1);
            _gen.Step(ref _wxHour);
            _wxHourSet = true;
            SampleWeather();
        }

        static SimWeather ReadWeather(SidecarReader r)
        {
            if (!r.Bool()) return null;
            bool generic = r.Bool();
            int baseFeet = r.I32(), summitFeet = r.I32();
            ulong hash = r.U64();
            var rows = new WeatherRows();
            rows.Visit(new RowsIn(r));
            var w = new SimWeather(rows, generic, baseFeet, summitFeet);
            if (w.RowsHash != hash) throw new InvalidDataException("The save's weather rows don't add up to their hash.");
            return w;
        }

        /// <summary>A save from before the weather (v1–v4): the weather the area has now, from October 1 of the save's year.</summary>
        internal void StartWeatherForOlderSave(SimWeather w) => AdoptWeather(w);

        /// <summary>A v5 save opened over the area's weather now: as <see cref="SimCommandKind.SetWeather"/>, but not a command
        /// (like the design a save opens over, SI-20), so a reload's future is the saved one's.</summary>
        internal void UseWeatherAtLoad(SimWeather w)
        {
            if (w != null) ApplyWeather(w);
        }

        sealed class RowsOut : IWeatherRowsVisitor
        {
            readonly SidecarWriter _o;
            public RowsOut(SidecarWriter o) { _o = o; }
            public void Int(ref int v) => _o.I32(v);
            public void Ints(int[] a) { _o.I32(a.Length); foreach (int v in a) _o.I32(v); }
            public void Longs(long[] a) { _o.I32(a.Length); foreach (long v in a) _o.I64(v); }
        }

        sealed class RowsIn : IWeatherRowsVisitor
        {
            readonly SidecarReader _r;
            public RowsIn(SidecarReader r) { _r = r; }
            public void Int(ref int v) => v = _r.I32();
            public void Ints(int[] a)
            {
                if (_r.I32() != a.Length) throw new InvalidDataException("The save's weather rows have the wrong shape.");
                for (int i = 0; i < a.Length; i++) a[i] = _r.I32();
            }
            public void Longs(long[] a)
            {
                if (_r.I32() != a.Length) throw new InvalidDataException("The save's weather rows have the wrong shape.");
                for (int i = 0; i < a.Length; i++) a[i] = _r.I64();
            }
        }
    }
}
