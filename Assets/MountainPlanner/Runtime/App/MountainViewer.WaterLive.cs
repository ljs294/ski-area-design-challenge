using System;
using System.Globalization;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.14 PR B (WS-02, WS-05): the water the simulation keeps, on screen. Each pond's and reservoir's water moves down
    /// to the level it holds (once a face hour, by its transform); an open source card follows the snapshot at most twice a
    /// second, rebuilt only when a figure it shows changed; the Test draw buttons send <see cref="SimCommandKind.SetTestDraw"/>;
    /// a source at its floor, spilling or at a lake's limit says so once a face day (events 41–43).
    /// </summary>
    public sealed partial class MountainViewer
    {
        const int TestDrawStepGpm = 250;
        const float WaterLiveSeconds = 0.5f;
        float _nextWaterLive;
        long _waterLiveKey = long.MinValue;
        long _pondLevelHour = -1;
        int _pondLevelCount = -1;

        /// <summary>The snapshot's row for source <paramref name="id"/>, or −1 (no snapshot yet, or it isn't a source).</summary>
        static int SourceRow(SimSnapshot s, ulong id)
        {
            if (s == null) return -1;
            for (int i = 0; i < s.SourceCount; i++) if (s.SourceIds[i] == id) return i;
            return -1;
        }

        /// <summary>A source card's live figures: where the water stands, what it holds of its whole, its rates and test draw.</summary>
        void LiveWater(ref WaterCardRows.Standing w, ulong id, System.Collections.Generic.List<(string, string, bool)> acts)
        {
            var s = _simNow;
            int i = SourceRow(s, id);
            if (i < 0) return;
            const double M3PerGallon = 0.003785411784;
            long held = s.SourceHeldGallons[i], cap = s.SourceCapacityGallons[i], floor = s.SourceFloorGallons[i];
            byte flags = s.SourceFlags[i];
            if (!double.IsNaN(w.FullPoolMetres)) w.LevelMetres = w.FullPoolMetres - s.SourceBelowFullMicrons[i] / 1e6;
            w.LevelNote = held == 0 ? "empty" : (flags & SimWorld.SourceFrozen) != 0 ? "iced over" : (flags & SimWorld.SourceSpilling) != 0 ? "spilling" : null;
            w.PercentFull = cap > 0 ? held * 100.0 / cap : 0;
            if (double.IsNaN(w.VolumeCubicMetres)) w.VolumeCubicMetres = cap * M3PerGallon;
            w.StoredCubicMetres = held * M3PerGallon;
            w.InGpm = s.SourceInGpm[i];
            w.OutGpm = s.SourceOutGpm[i];
            w.TestDrawGpm = s.SourceTestGpm[i];
            w.MakingLeft = MakingLeft(held, floor, s.SourceOutGpm[i] - s.SourceInGpm[i], (flags & SimWorld.SourceIsLake) != 0);
            int test = s.SourceTestGpm[i];
            acts.Add(("drawless", $"Test draw −{TestDrawStepGpm}", false));
            acts.Add(("drawmore", $"Test draw +{TestDrawStepGpm}", false));
            if (test > 0) acts.Add(("drawoff", "Stop the test draw", false));
        }

        /// <summary>WS-05 (owner Q4): the days its water lasts at the net draw, and the date, "12 days · to Jan 9".</summary>
        string MakingLeft(long held, long floor, int netOutGpm, bool lake)
        {
            if (held <= floor) return lake ? "none · at the 3 ft limit" : "none · at the minimum pool";
            if (netOutGpm <= 0) return "not drawing down";
            double days = (held - floor) / (netOutGpm * 1440.0);
            if (days >= 365) return "over a year";
            string count = days < 1 ? "under a day" : $"{Math.Floor(days):0} day{(Math.Floor(days) == 1 ? "" : "s")}";
            var now = _simClock.Now;
            if (!now.IsValid) return count;
            var date = new DateTime(now.Year, 1, 1).AddDays(now.DayOfYear - 1 + now.SecondOfDay / 86400.0 + days);
            return count + " · to " + date.ToString("MMM d", CultureInfo.InvariantCulture);
        }

        void SendTestDraw(ulong id, int stepGpm)
        {
            int i = SourceRow(_simNow, id);
            if (_sim == null || i < 0) { Hud?.Toast("The water clock hasn't started yet"); return; }
            int gpm = stepGpm == 0 ? 0 : Mathf.Clamp(_simNow.SourceTestGpm[i] + stepGpm, 0, TestDraw.MaxGpm);
            _sim.Send(SimCommandKind.SetTestDraw, 0, new TestDraw(id, gpm));
            _waterLiveKey = long.MinValue;   // the card follows as soon as the worker has it
            _nextWaterLive = 0;
        }

        /// <summary>The open card, from the newest snapshot: rebuilt only when a figure it shows moved (≤ 2 Hz).</summary>
        void UpdateWaterLive()
        {
            var s = _simNow;
            if (Hud == null || s == null || Time.unscaledTime < _nextWaterLive) return;
            _nextWaterLive = Time.unscaledTime + WaterLiveSeconds;
            string card = Hud.WaterCardKey;
            if (card == null || card != _waterCardOpened || !Parse(card, out _, out ulong id)) return;
            int i = SourceRow(s, id);
            long key = i < 0 ? -1 : unchecked(((s.SourceHeldGallons[i] / 1000) * 31 + s.SourceInGpm[i]) * 31 + s.SourceOutGpm[i]) * 31
                                    + s.SourceTestGpm[i] * 7 + s.SourceFlags[i] + _simClock.Now.DayOfYear * 1009L;
            if (key == _waterLiveKey) return;
            _waterLiveKey = key;
            ShowWaterCardFor(card);
        }

        /// <summary>WS-02: each pond's and reservoir's water at the level it holds, once a face hour (or when the sources change).</summary>
        void ShowPondLevels(SimSnapshot s)
        {
            if (_pondWater == null || s == null) return;
            long hour = s.FaceDay * 24L + s.FaceMs / SimWorld.HourMs;
            if (hour == _pondLevelHour && s.SourceCount == _pondLevelCount) return;
            _pondLevelHour = hour;
            _pondLevelCount = s.SourceCount;
            for (int i = 0; i < s.SourceCount; i++)
            {
                if ((s.SourceFlags[i] & SimWorld.SourceIsLake) != 0) continue;   // owner Q5: lakes stay at their natural level
                _pondWater.SetLevel(s.SourceIds[i], s.SourceBelowFullMicrons[i] / 1e6f, s.SourceHeldGallons[i] == 0);
            }
        }

        /// <summary>Until the worker has a new pond or reservoir, one that starts empty isn't drawn full.</summary>
        void HideEmptyStarts(DesignDocument doc)
        {
            if (_pondWater == null || doc == null) return;
            foreach (var p in doc.Ponds) if (p.StartsEmpty && SourceRow(_simNow, p.Id.Value) < 0) _pondWater.SetLevel(p.Id.Value, 0, true);
            foreach (var d in doc.Dams) if (d.StartsEmpty && SourceRow(_simNow, d.Id.Value) < 0) _pondWater.SetLevel(d.Id.Value, 0, true);
            _pondLevelHour = -1;
        }

        /// <summary>Events 41–43 as toasts (owner Q7: the worker says each at most once a face day per source).</summary>
        bool OnWaterEvent(SimEvent e)
        {
            if (e.Type != SimEventType.SourceAtMinPool && e.Type != SimEventType.SourceSpilling && e.Type != SimEventType.SourceAtDrawdownLimit) return false;
            var s = _simNow;
            string name = s != null && e.Entity >= 0 && e.Entity < s.SourceCount ? SourceName(s.SourceIds[e.Entity]) : "A source";
            switch (e.Type)
            {
                case SimEventType.SourceAtMinPool: Hud.Toast($"{name} is down to its minimum pool: snowmaking can't draw more", AlertStripe); break;
                case SimEventType.SourceAtDrawdownLimit: Hud.Toast($"{name} is drawn down 3 ft, its limit: snowmaking can't draw more", AlertStripe); break;
                default: Hud.Toast($"{name} is full and spilling", WeatherStripe); break;
            }
            return true;
        }

        string SourceName(ulong id)
        {
            var doc = Design?.Document;
            if (doc == null) return "A source";
            if (doc.FindPond(new PondId(id)) is Pond p) return p.Name;
            if (doc.FindDam(new DamId(id)) is Dam d) return d.Name;
            string name = doc.FindLake(id).Name;
            if (name.Length == 0) name = OsmLakeName(id);
            return name.Length > 0 ? name : "A lake";
        }
    }
}
