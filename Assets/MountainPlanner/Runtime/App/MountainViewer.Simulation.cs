using System;
using System.Diagnostics;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using MountainPlanner.UI.Hud;
using MountainPlanner.App.Edits;
using MountainPlanner.Persistence.Saves;
using UnityEngine;
using Debug = UnityEngine.Debug;

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
        /// <summary>Whether the worker's failure has been told to the player (once, not every frame).</summary>
        bool _simFailureShown;
        ViewTime _sunPushed;
        int _shownSimSeasonDay = -1;
        /// <summary>Review movies set the chairs' time themselves (k / fps), never the simulation's.</summary>
        bool _chairsOverride;
        double _chairOverrideSeconds;
        /// <summary>SI-17: the worker has been sent the whole field once (or brought its own from a save).</summary>
        bool _simSnowSent;
        /// <summary>Said once the HUD shows: why a save's simulation didn't come back, or that its pacing differs.</summary>
        string _simNotice;
        OpenDesign _simSavesFor;
        Func<SimSidecarBytes> _simStateForSave;
        /// <summary>The tick the last save (or the opened one) holds; once the clock moves past it the design needs a save.</summary>
        long _simTickSaved;
        bool _simMarkedChanged;
        long _simTilesShown;
        /// <summary>Task 4.05: the weather the worker was given (the area's rows or the typical mountain's), and whether the
        /// snowpack's reference points have followed it (the typical mountain's only).</summary>
        SimWeather _simWeather;
        bool _simWeatherTerrainSent, _simHoldReleased, _simHoldSaid;
        bool _simWeatherPointsSent;

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
            // The open design's history: the edit session (which shares it) often arrives a few frames later, and a save's
            // state must be read over the design it was saved with, or its lifts would lose their ropes.
            var history = _edits?.History ?? OpenDesign.Current?.History;
            var design = history != null
                ? SimDesign.From(history.Current, SimGraphFor(history.Current.Document), SimFactsFor(history.Current.Document))
                : SimDesign.Empty(0);
            if (history != null) { _simSent = history.Current; NoteSkierDesign(design, history.Current.Document); }
            Hud?.ClearLive();
            var simSite = new SimSite(site.Latitude, site.Longitude);
            _simWeather = TypicalWeather.For(_resort.Manifest, _resort.PackageFolder, _resort.WeatherBaseFeet, _resort.WeatherSummitFeet);
            _simWeatherPointsSent = _simWeather == null || !_simWeather.Generic || _resort.WeatherBaseFeet > 0;
            _simWeatherTerrainSent = false;
            _simHoldSaid = false;
            var world = RestoredWorld(pacing, design, simSite, _simWeather)
                        ?? new SimWorld(pacing, design, simSite, Math.Max(2, startYear), weather: _simWeather);
            var mirror = _resort.States?.Snow;
            _simSnowSent = world.Snow.Set && mirror != null && world.Snow.Width == mirror.Width && world.Snow.Height == mirror.Height;
            // Task 4.05 PR B2: the weather's static inputs are built with the snowpack. Already here: the world takes them before
            // its worker starts. Still building: the host starts held, so no tick and no skip can run without them (a reloaded
            // game steps exactly as the one that saved); RunClock sends them and releases it.
            if (_resort.WeatherTerrain != null)
            {
                world.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, _resort.WeatherTerrain));
                _simWeatherTerrainSent = true;
            }
            bool hold = !_simWeatherTerrainSent && _resort.CoverReady != null && !_resort.CoverReady.IsCompleted;
            _simHoldReleased = !hold;
            _sim = new SimulationHost(world, new StopwatchTime(), held: hold);
            _simTickSaved = world.Tick;
            _simMarkedChanged = false;
            _simTilesShown = 0;
            FollowSavesForSimulation(OpenDesign.Current);
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

        /// <summary>
        /// SI-12 v2: the opened save's simulation, if it brought one that reads; else null (a new game starts). A sidecar that
        /// doesn't read is said once and the clock starts over; the design is never lost over it.
        /// </summary>
        SimWorld RestoredWorld(Pacing pacing, SimDesign design, SimSite site, SimWeather weather)
        {
            var saved = OpenDesign.Current?.LoadedSimulation;
            if (saved == null) return null;
            try
            {
                var w = SimSidecar.Read(saved.Bytes, pacing, design, site, out var header, weather);
                if (header.PacingHash != pacing.ContentsHash)
                    _simNotice = "This save's clock ran on another version's pacing; it carries on with this one's.";
                Debug.Log($"[Simulation] restored the save's state: tick {w.Tick}, face day {w.FaceMs / SimWorld.DayMs}, {saved.Bytes.Length} bytes");
                return w;
            }
            catch (Exception e) when (e is System.IO.InvalidDataException || e is NotSupportedException)
            {
                Debug.LogWarning("[Simulation] the save's state didn't read, so the clock starts over: " + e.Message);
                _simNotice = "The save's clock couldn't be read, so it starts over: " + e.Message;
                return null;
            }
        }

        /// <summary>SI-12 v2: each save asks the worker for its state (Ctrl+S, Save as, the autosave).</summary>
        void FollowSavesForSimulation(OpenDesign design)
        {
            if (_simSavesFor != null && ReferenceEquals(_simSavesFor.Simulation, _simStateForSave)) _simSavesFor.Simulation = null;
            _simSavesFor = design;
            if (design == null) return;
            _simStateForSave ??= SimStateForSave;
            design.Simulation = _simStateForSave;
        }

        /// <summary>The worker's state at its next tick boundary, waited for a bounded time; null saves without a simulation.</summary>
        SimSidecarBytes SimStateForSave()
        {
            var host = _sim;
            if (host == null) return null;
            var request = new SimStateRequest();
            if (!host.Send(SimCommandKind.SaveState, 0, request) || !request.Wait(SaveStateWaitMs))
            {
                Debug.LogWarning("[Simulation] the worker didn't hand over its state in time; the save goes without it. " + request.Error);
                return null;
            }
            _simTickSaved = SimSidecar.ReadHeader(request.Bytes).Tick;
            _simMarkedChanged = false;
            return new SimSidecarBytes(request.Bytes, SimSidecar.Version);
        }

        /// <summary>How long a save waits for the worker's state (it comes within a tick; a skip batch is 50 ms).</summary>
        const int SaveStateWaitMs = 2000;

        void StopSimulation()
        {
            FollowSavesForSimulation(null);
            FollowDesignForSimulation(null);
            _sim?.Dispose();
            _sim = null;
            _simNow = null;
            _skierDesign = null;   // the next worker's design rebuilds the dots' paths (task 4.13)
            _skierBuiltAny = false;
            _skierSelected = 0;
        }

        /// <summary>SI-20: every design revision (an edit, undo, redo, an opened save) goes to the worker whole.</summary>
        void FollowDesignForSimulation(TerrainEditSession session)
        {
            if (_simDesignFor != null)
            {
                _simDesignFor.History.Changed -= SendDesign;
                if (_simDesignFor.Pipeline != null) _simDesignFor.Pipeline.SnowChanged = null;
            }
            _simDesignFor = session;
            if (session == null || _sim == null) return;
            session.History.Changed += SendDesign;
            if (session.Pipeline != null) session.Pipeline.SnowChanged = _onPipelineSnow ??= SendSnowpack;
            SendDesign(session.History.Current);
        }

        Action<float[], int, int, int, int> _onPipelineSnow;

        /// <summary>
        /// SI-17: a new snowpack (the ground's snow after an edit) to the worker, which owns the depths; the screen hears back
        /// through the staging ring. The first goes whole, so the worker's field never holds only an edit's rectangle.
        /// </summary>
        void SendSnowpack(float[] metres, int x, int y, int width, int height)
        {
            var mirror = _resort?.States?.Snow;
            if (_sim == null || mirror == null) return;
            var p = _simSnowSent
                ? SimSnowpack.FromMetres(metres, mirror.Width, mirror.Height, x, y, width, height)
                : SimSnowpack.FromMetres(metres, mirror.Width, mirror.Height, 0, 0, mirror.Width, mirror.Height);
            _sim.Send(SimCommandKind.SetSnowpack, 0, p);
            _simSnowSent = true;
        }

        /// <summary>SI-17: the staged tiles the newest snapshot lists, into the screen's field, then their slots back to the worker.</summary>
        void DrainSnowTiles(SimSnapshot s)
        {
            var ring = _sim.Staging;
            long from = ring.Released, to = s.StagedTiles;
            if (to <= from) return;
            var mirror = _resort?.States?.Snow;
            for (long n = from; n < to; n++)
            {
                ring.Read(n, out int x, out int y, out int w, out int h, out int offset, out ushort[] cells);
                if (mirror != null && x + w <= mirror.Width && y + h <= mirror.Height) mirror.CopyMillimetres(cells, offset, x, y, w, h);
            }
            ring.Release(to);
            if (_simTilesShown == 0) Debug.Log($"[Simulation] snow from the worker's staging ring: {to - from} tiles in the first frame");
            _simTilesShown += to - from;
        }

        void SendDesign(DesignSnapshot snapshot)
        {
            if (_sim == null || snapshot.Document == null) return;
            _simSent = snapshot;
            var design = SimDesign.From(snapshot, SimGraphFor(snapshot.Document), SimFactsFor(snapshot.Document));
            NoteSkierDesign(design, snapshot.Document);   // the dots' paths come from exactly this (task 4.13)
            _sim.Send(SimCommandKind.SetDesign, 0, design);
        }

        void SendSim(SimCommandKind kind, long a = 0) => _sim?.Send(kind, a);

        /// <summary>
        /// Task 4.05: Get weather finished for <paramref name="packageFolder"/>. If it's the open area, the worker gets its own
        /// rows, which wait for the next October 1 (WX-32 v2: this winter keeps the weather it started with).
        /// </summary>
        internal void WeatherAdded(string packageFolder)
        {
            if (_sim == null || _resort?.PackageFolder == null || packageFolder == null) return;
            if (!string.Equals(System.IO.Path.GetFullPath(_resort.PackageFolder).TrimEnd('\\', '/'),
                               System.IO.Path.GetFullPath(packageFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return;
            Persistence.PackageManifest m;
            try { m = Persistence.ResortPackage.ReadManifest(packageFolder); }
            catch (Exception e) when (e is System.IO.IOException || e is System.IO.InvalidDataException) { return; }
            var w = TypicalWeather.For(m, packageFolder, _resort.WeatherBaseFeet, _resort.WeatherSummitFeet);
            if (w == null || w.Generic) return;
            _simWeather = w;
            _simWeatherPointsSent = true;
            _sim.Send(SimCommandKind.SetWeather, 0, w);
            Debug.Log($"[Weather] the area's own weather rows go to the worker; they play from the next October 1");
        }

        /// <summary>Once a frame: the newest snapshot moves the chairs, the HUD's clock, the sun (while it runs) and the toasts.</summary>
        void RunClock()
        {
            if (_sim == null || Lighting == null || TitleMode) return;
            // A worker that threw stops the clock and says nothing; say it once, here, where anybody would notice.
            if (_sim.Failure != null && !_simFailureShown)
            {
                _simFailureShown = true;
                Debug.LogException(_sim.Failure);
                Toast("The simulation stopped: " + _sim.Failure.Message);
                Hud?.Toast("The simulation stopped — F1 says what happened");
            }
            var s = _sim.Latest;
            if (s == null) return;
            _simNow = s;
            DrainSnowTiles(s);
            // SI-17: once the opening's snowpack is in, the worker gets the whole field (unless a save brought its own).
            if (!_simSnowSent && !s.SnowSet && _resort.CoverReady != null && _resort.CoverReady.IsCompleted && _resort.States?.Snow != null)
            {
                var mirror = _resort.States.Snow;
                SendSnowpack(mirror.Cells, 0, 0, mirror.Width, mirror.Height);
            }
            if (_simNotice != null && Hud != null && _hudShown) { Hud.Toast(_simNotice); _simNotice = null; }
            // Task 4.05: the typical mountain's base and summit follow from the snowpack's heights once they land (the same
            // rows, so the worker moves its points at once and the weather itself is unchanged).
            if (!_simWeatherPointsSent && _resort.WeatherBaseFeet > 0)
            {
                _simWeatherPointsSent = true;
                _simWeather = _simWeather.WithPoints(_resort.WeatherBaseFeet, _resort.WeatherSummitFeet);
                _sim.Send(SimCommandKind.SetWeather, 0, _simWeather);
            }
            // Task 4.05 PR B2: the weather's static inputs, built with the snowpack, go to the worker before the clock first runs:
            // a clock started before they land waits for them, so a reloaded game steps exactly as the one that saved.
            // A send the queue refuses is tried again next frame; the release follows the inputs in the same queue.
            if (!_simWeatherTerrainSent && _resort.WeatherTerrain != null)
                _simWeatherTerrainSent = _sim.Send(SimCommandKind.SetWeatherTerrain, 0, _resort.WeatherTerrain);
            if (!_simHoldReleased && (_simWeatherTerrainSent || _resort.CoverReady == null || _resort.CoverReady.IsCompleted))
            {
                // Released with the inputs, or without them when the cover build ended with none (it failed): then the area
                // plays B1's height rule, and the clock is never left stuck.
                if (_simWeatherTerrainSent || _resort.WeatherTerrain == null) _simHoldReleased = _sim.Send(SimCommandKind.Release);
            }
            if (s.Held && !_simHoldSaid && (!s.Paused || s.Skip != SkipKind.None) && Hud != null && _hudShown)
            {
                _simHoldSaid = true;
                Hud.Toast("Preparing the weather: the clock starts in a moment");
            }
            // SI-12 v2: time that has passed since the save is the player's progress, so the design asks to be saved (the bar's
            // symbol, leaving, the autosave), as an edit does.
            if (!_simMarkedChanged && s.Tick > _simTickSaved && _simSavesFor != null) { _simSavesFor.MarkChanged(); _simMarkedChanged = true; }
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
        void DrawSimulationRows(ref float y, float row, float x0, float w, Action<string> caption, Func<int, string, bool> button,
                                Func<float, int, string, float> note)
        {
            var s = _simNow;
            if (_sim == null || s == null) return;
            if (_sim.Failure != null)
            {
                caption("Simulation");
                y += note(y, 0, "STOPPED — the simulation thread threw and the clock has stopped. " + _sim.Failure.Message +
                                " · the error log in Esc ▸ Problem report has the whole of it");
                y += row;
            }
            caption("Simulation");
            if (button(0, s.Open ? "Mountain: open" : "Mountain: closed")) SendSim(SimCommandKind.SetMountainOpen, s.Open ? 0 : 1);
            if (button(1, "Skip to morning")) SendSim(SimCommandKind.SkipToMorning);
            if (button(2, "Next third")) SendSim(SimCommandKind.SkipToNextThird);
            if (button(3, "Next month")) SendSim(SimCommandKind.SkipToNextMonth);
            if (s.Skip != SkipKind.None && button(4, $"Cancel ({s.SkipPermille / 10}%)")) SendSim(SimCommandKind.CancelSkip);
            y += row;
            caption("Season seed");
            if (button(0, s.SeedPinned ? "Seed: pinned" : "Seed: drawn"))
                _sim.Send(s.SeedPinned ? SimCommand.Of(SimCommandKind.UnpinSeasonSeed) : SimCommand.Of(SimCommandKind.PinSeasonSeed, unchecked((long)s.SeasonSeed)));
            y += note(y, 1,
                $"{(s.Phase == SeasonPhase.PreSeason ? "Pre-season" : s.Phase == SeasonPhase.Season ? "Day " + s.SeasonDay : "Summer")} · {s.SeasonSeed:X16} · tick {s.TickMeanMicros} µs (p99 {s.TickP99Micros}) · {s.SustainedPermille / 10}% of speed · {s.OpenSecond / 3600}:{s.OpenSecond / 60 % 60:D2}–{s.StopSecond / 3600}:{s.StopSecond / 60 % 60:D2}{(s.HoursTrimmed ? " (daylight)" : "")}");
            DrawWeatherRows(ref y, row, caption, note);
            DrawLiftOpsRows(ref y, row, x0, w, caption, button, note);
            DrawGuestRows(ref y, row, x0, w, caption, button, note);
        }
    }
}
