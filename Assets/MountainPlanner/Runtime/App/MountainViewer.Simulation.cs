using System;
using System.Diagnostics;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI.Hud;
using MountainPlanner.App.Edits;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.03: the simulation runs on its own thread (<see cref="SimulationHost"/>) and the view reads its snapshots. The
    /// bar's buttons and keys 1–4 send commands (1×, 2×, 4×, 8×); the sun follows the face clock while it runs and moves freely
    /// while paused (owner, 2026-11-08: question 7); chairs follow each lift's rope (<see cref="RopeClock"/>); every design
    /// revision goes to the worker as a <see cref="SimDesign"/> (SI-20). A new game starts on October 1 at the opening.
    /// </summary>
    public sealed partial class MountainViewer
    {
        SimulationHost _sim;
        readonly SimGameClock _simClock = new SimGameClock();
        RopeClock _ropes;
        readonly EventReader _simEvents = new EventReader();
        Action<SimEvent> _onSimEvent;
        SimSnapshot _simNow;
        TerrainEditSession _simDesignFor;
        bool _simWasPaused = true;
        ViewTime _sunPushed;
        int _shownSimSeasonDay = -1;
        /// <summary>Review movies set the chairs' time themselves (k / fps), never the simulation's.</summary>
        bool _chairsOverride;
        double _chairOverrideSeconds;

        /// <summary>How far the face clock runs ahead of the sun before the sun catches up (an eighth of a degree).</summary>
        const int SunPushSeconds = 30;

        /// <summary>App's real time for the worker: the one stopwatch the simulation is paced by (never inside its state).</summary>
        sealed class StopwatchTime : IRealTime
        {
            readonly Stopwatch _watch = Stopwatch.StartNew();
            public long Microseconds => _watch.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
        }

        /// <summary>What tests read of the clock: the newest snapshot's pause, speed and time.</summary>
        internal sealed class ClockFace
        {
            readonly MountainViewer _v;
            internal ClockFace(MountainViewer v) { _v = v; }
            public bool Paused => _v._sim?.Latest?.Paused ?? true;
            public int Speed => _v._sim?.Latest?.Speed ?? 1;
            public ViewTime Now => _v._simClock.Now;
            public SimSnapshot Snapshot => _v._sim?.Latest;
        }

        ClockFace _clockFace;
        internal ClockFace Clock => _sim == null ? null : _clockFace ??= new ClockFace(this);

        /// <summary>Starts the worker for the open mountain (from <see cref="WireHud"/>), paused on October 1 at the opening.</summary>
        void StartSimulation(bool sunFromSimulation)
        {
            StopSimulation();
            var site = _resort.Manifest.Site;
            var pacing = Pacing.Default;
            var now = Lighting.Clock.Now;
            // The label year whose October 1 the game starts on: this winter's.
            int startYear = now.DayOfYear >= 274 ? now.Year : now.Year - 1;
            var design = _edits != null ? SimDesign.From(_edits.History.Current) : SimDesign.Empty(0);
            var world = new SimWorld(pacing, design, new SimSite(site.Latitude, site.Longitude), Math.Max(2, startYear));
            _sim = new SimulationHost(world, new StopwatchTime());
            _ropes = new RopeClock(pacing);
            _onSimEvent ??= OnSimEvent;
            _simWasPaused = true;
            _shownSimSeasonDay = -1;
            _simDesignFor = null;
            FollowDesignForSimulation(_edits);
            var first = _sim.Latest;
            _simClock.Feed(first);
            _simEvents.Read(first, null);   // the start's own events are not news
            if (sunFromSimulation) PushSun(_simClock.Now);
            else _sunPushed = Lighting.Clock.Now;
        }

        void StopSimulation()
        {
            FollowDesignForSimulation(null);
            _sim?.Dispose();
            _sim = null;
            _simNow = null;
        }

        /// <summary>SI-20: every design revision (an edit, undo, redo, an opened save) goes to the worker whole.</summary>
        void FollowDesignForSimulation(TerrainEditSession session)
        {
            if (_simDesignFor != null) _simDesignFor.History.Changed -= SendDesign;
            _simDesignFor = session;
            if (session == null || _sim == null) return;
            session.History.Changed += SendDesign;
            SendDesign(session.History.Current);
        }

        void SendDesign(DesignSnapshot snapshot)
        {
            if (_sim == null || snapshot.Document == null) return;
            _sim.Send(SimCommandKind.SetDesign, 0, SimDesign.From(snapshot));
        }

        void SendSim(SimCommandKind kind, long a = 0) => _sim?.Send(kind, a);

        /// <summary>Once a frame: the newest snapshot moves the chairs, the HUD's clock, the sun (while it runs) and the toasts.</summary>
        void RunClock()
        {
            if (_sim == null || Lighting == null || TitleMode) return;
            var s = _sim.Latest;
            if (s == null) return;
            _simNow = s;
            _ropes.Advance(s, Mathf.Min(0.25f, Time.unscaledDeltaTime), Pacing.Default.TickMs);
            _simEvents.Read(s, _onSimEvent);
            _simClock.Feed(s);
            if (s.Phase == SeasonPhase.Season && s.SeasonDay != _shownSimSeasonDay)
            {
                if (_shownSimSeasonDay >= 0 && Hud != null && _hudShown) Hud.Toast(HudText.Day(s.SeasonDay) + " begins");
                _shownSimSeasonDay = s.SeasonDay;
            }
            bool paused = s.Paused && s.Skip == SkipKind.None;
            if (paused)
            {
                // Question 7: while paused the light is the player's (the F1 slider, photo mode); play snaps it back.
                _simWasPaused = true;
                return;
            }
            var now = _simClock.Now;
            if (_simWasPaused || Math.Abs(SecondsBetween(now, _sunPushed)) >= SunPushSeconds) PushSun(now);
            _simWasPaused = false;
        }

        void PushSun(ViewTime t)
        {
            if (!t.IsValid) return;
            Lighting.SetTime(t);
            _sunPushed = t;
        }

        static long SecondsBetween(ViewTime a, ViewTime b) => a.Year == b.Year
            ? (long)(a.DayOfYear - b.DayOfYear) * ViewTime.SecondsPerDay + a.SecondOfDay - b.SecondOfDay
            : 366L * ViewTime.SecondsPerDay;

        void OnSimEvent(SimEvent e)
        {
            if (Hud == null || !_hudShown) return;
            switch (e.Type)
            {
                case SimEventType.SeasonOpened: Hud.Toast("The season opens: Day 1"); break;
                case SimEventType.DayReport when _simNow != null && _simNow.LastDayReport.IsValid:
                {
                    // TM-15's report at closing, in a line until 4.17 draws the sheet (once a face day, so the words may allocate).
                    var r = _simNow.LastDayReport;
                    int minutes = 0, ran = 0;
                    for (int i = 0; i < r.LiftCount; i++) { minutes += r.RunMinutes[i]; if (r.RunMinutes[i] > 0) ran++; }
                    string day = r.Phase == SeasonPhase.Season ? HudText.Day(r.SeasonDay) : "The day";
                    Hud.Toast($"{day} closed: {ran} of {r.LiftCount} lifts ran, {minutes / 60}h {minutes % 60:D2}m in all{(r.Trimmed ? " (hours cut to daylight)" : "")}");
                    break;
                }
                case SimEventType.SeasonEnded: Hud.Toast("The season is over"); break;
                case SimEventType.MountainOpened: Hud.Toast("The mountain is open"); break;
                case SimEventType.MountainClosed: Hud.Toast("The mountain is closed"); break;
                case SimEventType.CommandRejected when e.Detail == (ushort)SimRefusal.SkipNotAllowed: Hud.Toast("Can't skip from here"); break;
                case SimEventType.CommandRejected when e.Detail == (ushort)SimRefusal.NotInSummer: Hud.Toast("The mountain opens again next season"); break;
            }
        }

        /// <summary>The bar's clock from the newest snapshot (10 Hz, from <see cref="UpdateHud"/>).</summary>
        void SetHudClock()
        {
            var s = _simNow;
            if (s == null || !_simClock.Now.IsValid) return;
            Hud.SetClock(_simClock.Now, s.Speed, s.Paused, HudText.SimDay(s.Phase, s.SeasonDay), HudText.SimDate(s.Month, s.Third, s.DayType));
        }

        /// <summary>A lift's chairs' time: its rope's travel from the simulation, or the review movie's own step.</summary>
        double ChairSeconds(LiftId id) => _chairsOverride ? _chairOverrideSeconds : _ropes != null ? _ropes.Seconds(id.Value) : 0;

        /// <summary>The F1 panel's Simulation rows: open or closed, the skips, the seed and the tick's cost (BG-01).</summary>
        void DrawSimulationRows(ref float y, float row, float x0, float w, Action<string> caption, Func<int, string, bool> button)
        {
            var s = _simNow;
            if (_sim == null || s == null) return;
            caption($"Simulation ({(s.Phase == SeasonPhase.PreSeason ? "pre-season" : s.Phase == SeasonPhase.Season ? "day " + s.SeasonDay : "summer")})");
            if (button(0, s.Open ? "Mountain: open" : "Mountain: closed")) SendSim(SimCommandKind.SetMountainOpen, s.Open ? 0 : 1);
            if (button(1, "Skip to morning")) SendSim(SimCommandKind.SkipToMorning);
            if (button(2, "Next third")) SendSim(SimCommandKind.SkipToNextThird);
            if (button(3, "Next month")) SendSim(SimCommandKind.SkipToNextMonth);
            if (s.Skip != SkipKind.None && button(4, $"Cancel ({s.SkipPermille / 10}%)")) SendSim(SimCommandKind.CancelSkip);
            y += row;
            caption("Season seed");
            if (button(0, s.SeedPinned ? "Seed: pinned" : "Seed: drawn"))
                _sim.Send(s.SeedPinned ? SimCommand.Of(SimCommandKind.UnpinSeasonSeed) : SimCommand.Of(SimCommandKind.PinSeasonSeed, unchecked((long)s.SeasonSeed)));
            GUI.Label(new Rect(x0 + w + 6, y + 4, 4 * w, row),
                $"{s.SeasonSeed:X16} · tick {s.TickMeanMicros} µs (p99 {s.TickP99Micros}) · {s.SustainedPermille / 10}% of speed · {s.OpenSecond / 3600}:{s.OpenSecond / 60 % 60:D2}–{s.StopSecond / 3600}:{s.StopSecond / 60 % 60:D2}{(s.HoursTrimmed ? " (daylight)" : "")}");
        }
    }
}
