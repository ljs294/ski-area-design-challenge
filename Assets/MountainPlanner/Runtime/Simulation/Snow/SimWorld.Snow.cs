using System;
using MountainPlanner.Domain.Sky;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.07 PR A2: the natural snow in the world.
    /// - **Inputs** (<see cref="SimCommandKind.SetSnowTerrain"/>, a view-sequence command like the weather's terrain, sent
    ///   again after every load): the first <see cref="SnowTerrain"/> makes the field live (static depths it had become the
    ///   seed); a <see cref="SnowTerrainPatch"/> after earthwork or Clear trees changes the inputs only: the snow stays.
    /// - **The hour** (BG-02 v4): at the roll, after the weather (WX-06's order), the hour's job starts on the snow crew
    ///   with the hour's inputs; it lands at the tick <see cref="Pacing.SnowLandFaceMs"/> later, a boundary every step stops
    ///   on, so every speed and skip lands it at the same tick. Until then the hash, the screen and the save read the
    ///   landed hour (the hash adds that one is in flight). Anything that reads or changes what the job uses joins it
    ///   first; a seed lands it at once (a logged command: the same tick in every replay).
    /// - **The ledger**: each landed hour folds into the face day's; it balances to the µm (<see cref="SnowLedger"/>).
    /// A world that never gets the inputs never goes live and hashes and saves as before.
    /// </summary>
    public sealed partial class SimWorld
    {
        SnowTerrain _snowTerrain;
        SnowHour _snowJob;
        bool _snowRunning;
        /// <summary>The face ms the hour in flight lands at, or −1; and that hour.</summary>
        long _snowLandAt = -1, _snowHour = -1;
        SnowLedger _snowToday, _snowLastDay, _snowPending;
        long _snowPendingWater;

        /// <summary>The snow's inputs the worker holds (its own copy), or null.</summary>
        public SnowTerrain SnowTerrain => _snowTerrain;
        /// <summary>Whether an hour's snow is in flight (started, not landed).</summary>
        public bool SnowInFlight => _snowLandAt >= 0;
        /// <summary>The ledger of the face day so far (landed hours), and of the last whole day.</summary>
        public SnowLedger SnowToday => _snowToday;
        public SnowLedger SnowLastDay => _snowLastDay;

        bool ApplySnowTerrain(object payload)
        {
            switch (payload)
            {
                case SnowTerrain t:
                    if (t.Width < 1 || t.Depth < 1) return false;
                    if (_wxTerrain != null && (t.Width != _wxTerrain.Width || t.Depth != _wxTerrain.Depth)) return false;
                    JoinSnow();
                    _snowTerrain = t;
                    if (!Snow.Live || Snow.Width != t.Width || Snow.Height != t.Depth)
                    {
                        LandSnowNow();
                        RollSnowLedger(FaceDayNow);
                        long before = _snowToday.End;
                        long seeded = Snow.MakeLive(t.Width, t.Depth, new SnowRules(Pacing));
                        _snowToday.Seeded += seeded - before;   // a new size starts bare: what was there leaves as a negative seed
                        _snowToday.End = seeded;
                        RebuildFine(keep: false);
                    }
                    return true;
                case SnowTerrainPatch p:
                    if (_snowTerrain == null) return false;
                    JoinSnow();
                    _snowTerrain.Apply(p);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>SetSnowpack: the static picture, or live a seed (an hour in flight lands first).</summary>
        bool ApplySnowpack(SimSnowpack p)
        {
            if (!Snow.Live) return Snow.Apply(p);
            LandSnowNow();
            long change = Snow.Seed(p);
            if (change == long.MinValue) return false;
            RollSnowLedger(FaceDayNow);
            _snowToday.Seeded += change;
            _snowToday.End += change;
            RebuildFine(keep: false);   // the trails' fine cells take the seeded snow
            return true;
        }

        int FaceDayNow => (int)(FaceMs / DayMs);

        /// <summary>The hour roll's snow (after the weather, WX-06): the job starts with the hour's inputs.</summary>
        void HourStepSnow(long hour)
        {
            if (!Snow.Live || _snowTerrain == null || _wxTerrain == null || _wxFields == null || _wxFields.Hour != hour) return;
            if (_snowTerrain.Width != Snow.Width || _snowTerrain.Depth != Snow.Height || _wxTerrain.Width != Snow.Width) return;
            LandSnowNow();
            HourStepTrails(hour);
            var sun = default(SunVector);
            if (Site.Known)
            {
                int j = _wxHour.HourOfYear / 24, y = _wxHour.LabelYear;
                if (j < Calendar.YearFaceDays)
                {
                    int lat = (int)Math.Round(Site.Latitude * 1_000_000), lon = (int)Math.Round(Site.Longitude * 1_000_000);
                    sun = SunVector.At(lat, lon, Calendar.DayOfYear(y, j), _wxHour.HourOfDay * 3600 + 1800)
                        .ToGrid(_wxTerrain.ConvergenceTenths * 100);
                }
            }
            var table = _waterTable;
            byte[] water = table != null && table.Width == Snow.Width && table.Depth == Snow.Height ? table.Mask : null;
            int seconds = (int)WeatherMath.DivRound(_tuning.CalendarDays * 3600L, _tuning.FaceDays);
            _snowJob ??= new SnowHour(Snow);
            _snowJob.Fine = _fine;
            _snowJob.PackedFloor = Pacing.PackedMeltFloor;
            _snowJob.Launch(_wxFields, _wxTerrain, _snowTerrain, water, Snow.Rules, sun, _wxHour.Clearness, _wxHour.Cloud,
                            seconds, (int)(hour / 24), Pacing.SnowThreads);
            _snowRunning = true;
            _snowHour = hour;
            _snowLandAt = FaceMs + Pacing.SnowLandFaceMs;
            if (Pacing.SnowLandFaceMs == 0) LandSnowNow();
        }

        /// <summary>Waits for the job in flight to finish (it keeps its landing tick); its ledger waits with it.</summary>
        internal void JoinSnow()
        {
            if (!_snowRunning) return;
            _snowJob.Done.Wait();
            _snowRunning = false;
            if (_snowJob.Error != null)
            {
                var e = _snowJob.Error;
                _snowJob.Error = null;
                _snowLandAt = -1;
                throw new InvalidOperationException("The snow's hour failed.", e);
            }
            _snowPending = _snowJob.Ledger;
            _snowPendingWater = _snowJob.WaterOnField;
        }

        /// <summary>The landing tick (or a seed's): the job's result becomes the landed hour.</summary>
        void LandSnowNow()
        {
            if (_snowLandAt < 0) return;
            JoinSnow();
            Snow.Land();
            FineAllDue();   // task 4.08: the hour's fine snow (job, wear, grooming) to the screen
            CollectRunoff();   // WS-04 (task 4.14 PR B): the landed hour's runoff into the sources
            RollSnowLedger((int)(_snowHour / 24));
            _snowToday.Add(_snowPending);
            _snowToday.End = _snowPendingWater;
            _snowPending = default;
            _snowLandAt = -1;
        }

        void RollSnowLedger(int day)
        {
            if (_snowToday.Day == day) return;
            if (_snowToday.Day < day)
            {
                _snowLastDay = _snowToday;
                long water = _snowToday.End;
                _snowToday = new SnowLedger { Day = day, Start = water, End = water };
            }
        }

        /// <summary>The snow's part of the hash past the field's tiles (live only, so older worlds hash as before).</summary>
        void HashSnowLive(ref StateHasher h)
        {
            if (!Snow.Live) return;
            h.Add(_snowLandAt); h.Add(_snowHour);
            _snowToday.AddTo(ref h);
            _snowLastDay.AddTo(ref h);
        }

        /// <summary>The landing as a boundary in today's face ms of the day, or −1.</summary>
        long SnowLandingSod(long sod)
        {
            if (_snowLandAt < 0) return -1;
            long dayStart = FaceMs - sod;
            long land = _snowLandAt - dayStart;
            return land > sod ? land : -1;
        }
    }
}
