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
        /// <summary>
        /// Task 4.07 PR A2: the worker has the snow's inputs (a clone of the resort's, every edit so far in it), so its snow is
        /// live: edits send patches of the inputs (queued here while the command queue is full, in order) and never depths.
        /// </summary>
        bool _simSnowTerrainSent;
        readonly System.Collections.Generic.Queue<Domain.Snow.SnowTerrainPatch> _simSnowPatches = new System.Collections.Generic.Queue<Domain.Snow.SnowTerrainPatch>();
        Action<Domain.Snow.SnowTerrainPatch> _onPipelineSnowTerrain;

        static bool? _snowSeedWanted;
        /// <summary>
        /// Task 4.07 PR A2: a new game starts on bare ground on October 1. <c>-snowseed</c> (and every capture and benchmark,
        /// unless <c>-snowlapse</c> or <c>-simto</c>) starts it on the opening's static snowpack instead, the mid-winter picture they were made
        /// with, as the live snow's seed.
        /// </summary>
        internal static bool SnowSeedWanted => _snowSeedWanted ??= Array.IndexOf(Environment.GetCommandLineArgs(), "-snowseed") >= 0
                                                                    || SeedMetres != null || GunWhaleWanted.Nights > 0   // task 4.07 PR A3
                                                                    || (Flow.AppFlow.CaptureRun && Array.IndexOf(Environment.GetCommandLineArgs(), "-snowlapse") < 0
                                                                        && Array.IndexOf(Environment.GetCommandLineArgs(), "-simto") < 0);

        bool CoverDone => _resort.CoverReady == null || _resort.CoverReady.IsCompleted;

        /// <summary>The snow's inputs didn't build (the area then plays the static snowpack, as before task 4.07).</summary>
        bool SnowTerrainFailed => CoverDone && _resort.SnowTerrain == null && (_resort.SnowTerrainReady == null || _resort.SnowTerrainReady.IsCompleted);

        /// <summary>The presentation's snow inputs hold every edit there will be before the worker's first hour: a loaded save's
        /// terrain edits are laid over them first (its edit session starts at once), so a reloaded game steps as the one that saved.</summary>
        bool SnowInputsSettled => _edits == null ? !_editsStarting
                                                 : _edits.Pipeline == null || (_edits.Pipeline.HasSnowTerrain && _edits.Pipeline.SnowSettled && _edits.Applying.IsCompleted);

        /// <summary>Some static input the worker must have before its first tick is still building (the host stays held).</summary>
        bool SimInputsMissing =>
            (!_simWeatherTerrainSent && !(CoverDone && _resort.WeatherTerrain == null)) ||
            (!_simWaterSent && !(CoverDone && _resort.WaterBodies == null)) ||
            (!_simSourcesSent && !(CoverDone && _resort.WaterBodies == null)) ||
            (!_simSnowTerrainSent && !SnowTerrainFailed) ||
            SnowNetMissing ||   // task 4.15: a network that fills a pond (WS-11)
            TrailSnowMissing;   // task 4.07 B1 part 2c: a reopened save's trail snow waits for its layout
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
        bool _simWeatherTerrainSent, _simWaterSent, _simSourcesSent, _simHoldReleased, _simHoldSaid;
        bool _simWeatherPointsSent;

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
            if (history != null) { _simSent = history.Current; NoteSkierDesign(design, history.Current.Document); NoteTrailSnow(design, history.Current.Document); }
            Hud?.ClearLive();
            var simSite = new SimSite(site.Latitude, site.Longitude);
            _simWeather = TypicalWeather.For(_resort.Manifest, _resort.PackageFolder, _resort.WeatherBaseFeet, _resort.WeatherSummitFeet);
            _simWeatherPointsSent = _simWeather == null || !_simWeather.Generic || _resort.WeatherBaseFeet > 0;
            _simWeatherTerrainSent = false;
            _simWaterSent = false;
            _simSourcesSent = false;
            ResetSnowNetworkSend();   // task 4.15
            _simSnowTerrainSent = false;
            ResetTrailSnow();
            _simSnowPatches.Clear();
            _simHoldSaid = false;
            var world = RestoredWorld(pacing, design, simSite, _simWeather)
                        ?? new SimWorld(pacing, design, simSite, Math.Max(2, startYear), weather: _simWeather);
            HoldForTrailSnow(world.TrailSnowWaiting);
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
            // Task 4.09: the water bodies come with the same build and follow the same rule (a lake's records wait for them).
            if (_resort.WaterBodies != null)
            {
                world.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, _resort.WaterBodies));
                _simWaterSent = true;
            }
            // Task 4.07 PR A2: the snow's inputs too (built after the snowpack, with a loaded save's edits laid over them).
            bool hold = SimInputsMissing;
            _simHoldReleased = !hold;
            _sim = new SimulationHost(world, new StopwatchTime(), held: hold);
            _simTickSaved = world.Tick;
            _simMarkedChanged = false;
            _simTilesShown = 0;
            FollowSavesForSimulation(OpenDesign.Current);
            _ropes = new RopeClock(pacing);
            _sunClock = new FaceClock();
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
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (!host.Send(SimCommandKind.SaveState, 0, request) || !request.Wait(SaveStateWaitMs))
            {
                Debug.LogWarning("[Simulation] the worker didn't hand over its state in time; the save goes without it. " + request.Error);
                return null;
            }
            LastSaveWaitMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            byte[] bytes = request.Bytes;   // encoded here, on the save's task (task 4.07 PR A2): the worker only copied
            LastSaveEncodeMs = clock.Elapsed.TotalMilliseconds;
            if (bytes == null)
            {
                Debug.LogWarning("[Simulation] the state couldn't be encoded; the save goes without it. " + request.Error);
                return null;
            }
            LastSaveBytes = bytes.Length;
            LastSaveBlocks = request.BlockSizes;
            _simTickSaved = SimSidecar.ReadHeader(bytes).Tick;
            _simMarkedChanged = false;
            return new SimSidecarBytes(bytes, SimSidecar.Version);
        }

        /// <summary>The last save's wait for the worker's copy (a tick boundary and the copy) and its encoding, ms.</summary>
        internal double LastSaveWaitMs, LastSaveEncodeMs;
        internal long LastSaveBytes;
        internal string LastSaveBlocks = "";

        /// <summary>How long a save waits for the worker's state (it comes within a tick; a skip batch is 50 ms).</summary>
        const int SaveStateWaitMs = 2000;

        // ---------- a save's clock waits for its network (finding B2c-5, owner 2026-11-19) ----------

        // A save's simulation is read over the design's network (LO-10, GU-07: the lifts' ways down, the trails' reach), and
        // that network is built off the main thread after the area opens. Read before it lands, the design has no network,
        // so a save with built trails or lifts didn't read ("The trails don't match the design's network") and its clock
        // started over (since task 4.11 PR A). So the start waits for it: a minute at most, then it reads without it.
        const float NetworkWaitSeconds = 60;
        /// <summary>-simnowait: the start as it was before the fix (the check shows the bug and the fix in one build).</summary>
        static readonly bool NoNetworkWait = Array.IndexOf(Environment.GetCommandLineArgs(), "-simnowait") >= 0;
        bool _simStartWaiting, _simStartSun;
        float _simStartFrom;

        /// <summary>From the HUD's wiring: starts the simulation now, or once the opened save's network has landed.</summary>
        void StartSimulationWhenReady(bool sunFromSimulation)
        {
            _simStartSun = sunFromSimulation;
            _simStartFrom = Time.unscaledTime;
            _simStartWaiting = true;
            if (SaveWaitsForNetwork()) Debug.Log("[Simulation] the save's clock waits for its network");
            StartSimulationIfReady();
        }

        /// <summary>Every frame, before the clock runs.</summary>
        void StartSimulationIfReady()
        {
            if (!_simStartWaiting) return;
            bool waits = SaveWaitsForNetwork();
            float waited = Time.unscaledTime - _simStartFrom;
            if (waits && waited < NetworkWaitSeconds) return;
            _simStartWaiting = false;
            if (waits) Debug.LogWarning($"[Simulation] the network didn't come in {NetworkWaitSeconds:F0} s; the save's clock is read without it");
            else if (waited > 0) Debug.Log($"[Simulation] the network came after {waited:F1} s; the save's clock is read over it");
            StartSimulation(_simStartSun);
        }

        /// <summary>An opened save brought a simulation, and its design has built lifts or trails whose network isn't in yet.</summary>
        bool SaveWaitsForNetwork()
        {
            if (OpenDesign.Current?.LoadedSimulation == null || NoNetworkWait) return false;
            var history = _edits?.History ?? OpenDesign.Current?.History;
            var doc = history?.Current.Document;
            if (doc == null || SimGraphFor(doc) != null) return false;
            foreach (var l in doc.Lifts) if (l.Stage == WorkStage.Built) return true;
            foreach (var t in doc.Trails) if (t.Stage == WorkStage.Built) return true;
            return false;
        }

        void StopSimulation()
        {
            _simStartWaiting = false;
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
                if (_simDesignFor.Pipeline != null)
                {
                    _simDesignFor.Pipeline.SnowChanged = null;
                    _simDesignFor.Pipeline.SnowTerrainChanged = null;
                    _simDesignFor.Pipeline.LiveSnow = false;
                }
            }
            _simDesignFor = session;
            if (session == null || _sim == null) return;
            session.History.Changed += SendDesign;
            if (session.Pipeline != null)
            {
                session.Pipeline.SnowChanged = _onPipelineSnow ??= SendSnowpack;
                session.Pipeline.SnowTerrainChanged = _onPipelineSnowTerrain ??= SendSnowTerrainPatch;
                session.Pipeline.LiveSnow = _simSnowTerrainSent;
            }
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
            // Task 4.07 PR A2: live, the worker's snow is its own; before that, the static snowpack goes only as the seed.
            if (_simSnowTerrainSent || !(SnowSeedWanted || SnowTerrainFailed)) return;
            // Task 4.07 PR A3: the developer seed (-snowseedm, -gunwhale) rides on whichever snowpack goes first, so an edit's
            // snowpack (-styletile builds before the opening's goes) doesn't drop it; an even seed holds over a later edit's
            // rectangle too, so the built area isn't left at the static depth.
            bool first = !_simSnowSent;
            if (first) metres = DeveloperSeed(metres, mirror.Width, mirror.Height, mirror.CellMetres);
            else if (SeedMetres != null || _whaleAdd != null)
            {
                // A later edit's rectangle carries the static depth: the seed is laid over it again (the even depth, the whale).
                metres = (float[])metres.Clone();
                for (int j = y; j < y + height; j++)
                    for (int i = x; i < x + width; i++)
                    {
                        int c = j * mirror.Width + i;
                        if (SeedMetres is float even) metres[c] = even;
                        if (_whaleAdd != null) metres[c] += _whaleAdd[c];
                    }
            }
            var p = first
                ? SimSnowpack.FromMetres(metres, mirror.Width, mirror.Height, 0, 0, mirror.Width, mirror.Height)
                : SimSnowpack.FromMetres(metres, mirror.Width, mirror.Height, x, y, width, height);
            // The snow counts as sent only if the worker's queue took it (finding W5): a refused first send is tried again
            // (the opening's every frame, an edit's with its next rectangle); a refused rectangle says so.
            if (!_sim.Send(SimCommandKind.SetSnowpack, 0, p))
            {
                if (!first) Debug.LogWarning("[Simulation] the worker's queue refused a snowpack rectangle: the snow there keeps its old depth");
                return;
            }
            _simSnowSent = true;
            if (first && _whaleAdd != null) _whaleLaid = true;
        }

        /// <summary>Task 4.07 PR A2: an edit's patch of the snow's inputs, to the worker in order (once it has the inputs; before
        /// that, the clone it gets holds the patch).</summary>
        void SendSnowTerrainPatch(Domain.Snow.SnowTerrainPatch patch)
        {
            if (_sim == null || !_simSnowTerrainSent || patch == null) return;
            _simSnowPatches.Enqueue(patch);
            FlushSnowTerrainPatches();
        }

        void FlushSnowTerrainPatches()
        {
            while (_simSnowPatches.Count > 0 && _sim.Send(SimCommandKind.SetSnowTerrain, 0, _simSnowPatches.Peek())) _simSnowPatches.Dequeue();
        }

        /// <summary>
        /// Task 4.07 PR A2: the snow's inputs to the worker once they're built and hold every edit so far (a clone: the pipeline
        /// keeps patching the resort's). Its snow goes live: a new game's bare, a save's as it was, a seed's from the seed.
        /// </summary>
        void SendSnowTerrain()
        {
            if (_edits?.Pipeline is Presentation.Edits.TerrainEditPipeline pipe && !pipe.HasSnowTerrain && _resort.SnowTerrain != null)
                pipe.AttachSnowTerrain(_resort.SnowTerrain);
            if (!_simSnowTerrainSent && _resort.SnowTerrain != null && SnowInputsSettled)
            {
                var clone = _resort.SnowTerrain.Clone();
                _simSnowTerrainSent = _sim.Send(SimCommandKind.SetSnowTerrain, 0, clone);
                if (_simSnowTerrainSent)
                {
                    _simSnowPatches.Clear();
                    if (_simDesignFor?.Pipeline != null) _simDesignFor.Pipeline.LiveSnow = true;
                    Debug.Log($"[Simulation] the snow's inputs go to the worker (hash {clone.Hash:X16}): its snow is live" +
                              (_simSnowSent ? " from the snow it has" : SnowSeedWanted ? ", seeded" : ", bare"));
                }
            }
            if (_simSnowTerrainSent) FlushSnowTerrainPatches();
        }

        float _siteSnowTarget = -1, _nextSiteSnow;

        /// <summary>
        /// Task 4.07 PR A2: the trees' and lifts' white follows the snow on the ground. Once a second, the share of the field
        /// (every 4th cell each way) under 2 in of snow sets a target: none below 15 %, full from 60 %; the white eases to it
        /// over about two seconds, so a storm frosts the forest and a thaw clears it without a jump.
        /// </summary>
        void EaseSiteSnow()
        {
            var field = _resort?.States?.Snow;
            if (field == null || _layers == null) return;
            if (_siteSnowTarget < 0 || Time.unscaledTime >= _nextSiteSnow)
            {
                _nextSiteSnow = Time.unscaledTime + 1;
                int covered = 0, cells = 0;
                for (int j = 2; j < field.Height; j += 4)
                    for (int i = 2; i < field.Width; i += 4, cells++)
                        if (field[i, j] >= 0.05f) covered++;
                float share = cells > 0 ? covered / (float)cells : 0;
                bool first = _siteSnowTarget < 0;
                _siteSnowTarget = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.15f, 0.6f, share));
                if (first) { _layers.SetSiteSnow(_siteSnowTarget); return; }
            }
            float now = _layers.SiteSnow;
            if (now != _siteSnowTarget)
                _layers.SetSiteSnow(Mathf.Abs(_siteSnowTarget - now) < 0.01f ? _siteSnowTarget
                                    : now + (_siteSnowTarget - now) * Mathf.Min(1, Time.unscaledDeltaTime / 2));
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
                ring.ReadSurface(n, out int surfaceOffset, out byte[] surface);
                if (mirror != null && x + w <= mirror.Width && y + h <= mirror.Height) _resort.States.TakeTile(x, y, w, h, cells, offset, surface, surfaceOffset);
            }
            ring.Release(to);
            if (_simTilesShown == 0) Debug.Log($"[Simulation] snow from the worker's staging ring: {to - from} tiles in the first frame");
            _simTilesShown += to - from;
        }

        void SendDesign(DesignSnapshot snapshot)
        {
            if (_sim == null || snapshot.Document == null) return;
            _simSent = snapshot;
            var doc = snapshot.Document;
            // SN-20 v4 (4.07 B1 part 2c): an edit that only switches trails Open, Closed or Auto goes at once on the network
            // already built for the document before it (the same graph, its pieces' switches again), not after the rebuild.
            if (SimGraphFor(doc) == null && _simGraph != null && _simFacts is TrailFacts built
                && SetTrailSwitch.OnlySwitchesDiffer(_simGraphDoc, doc))
            {
                _simGraphDoc = doc;
                _simFacts = built.WithSwitchesOf(doc);
                Debug.Log($"[Simulation] trail switches go to the worker at once (revision {snapshot.Revision})");
            }
            var design = SimDesign.From(snapshot, SimGraphFor(doc), SimFactsFor(doc));
            NoteSkierDesign(design, snapshot.Document);   // the dots' paths come from exactly this (task 4.13)
            NoteTrailSnow(design, snapshot.Document);     // and the trails' fine snow (task 4.07 B1 part 2a)
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
            ShowLakes(s);   // task 4.09 PR B: before the paused return, so a reopened save shows its ice while paused
            ShowPondLevels(s);   // task 4.14 PR B: the same, for the water a pond or reservoir holds
            EaseSiteSnow();
            // SI-17: once the opening's snowpack is in, the worker gets the whole field (unless a save brought its own). Task 4.07
            // PR A2: only as a seed (-snowseed, captures) or when the snow's inputs failed; a new game otherwise starts bare.
            if (!_simSnowSent && !s.SnowSet && _resort.CoverReady != null && _resort.CoverReady.IsCompleted && _resort.States?.Snow != null
                && (SnowSeedWanted || SnowTerrainFailed))
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
            // Task 4.14: the table carries the design's ponds and reservoirs; the first waits for them, a change is sent again.
            bool waterReady = UpdateDesignWater();
            if (!_simWaterSent && _resort.WaterBodies != null && waterReady)
                _simWaterSent = _sim.Send(SimCommandKind.SetWaterBodies, 0, _resort.WaterBodies);
            // Task 4.14 PR B: the sources follow their table in the same queue (their bodies are its indices).
            if (_simWaterSent && !_simSourcesSent && _waterSources != null && waterReady)
                _simSourcesSent = _sim.Send(SimCommandKind.SetWaterSources, 0, _waterSources);
            // Task 4.15: the snowmaking network follows its sources (an outlet fills one by id).
            if (waterReady) UpdateSnowNetwork();
            SendSnowTerrain();
            SendTrailSnow();
            SayTrailSnowBack(s);
            // Released with the inputs, or without one whose build ended with none (it failed): then the area plays B1's height
            // rule, has no lakes or keeps the static snow, and the clock is never left stuck.
            if (!_simHoldReleased && !SimInputsMissing) _simHoldReleased = _sim.Send(SimCommandKind.Release);
            if (s.Held && !_simHoldSaid && (!s.Paused || s.Skip != SkipKind.None) && Hud != null && _hudShown)
            {
                _simHoldSaid = true;
                Hud.Toast("Preparing the weather: the clock starts in a moment");
            }
            // SI-12 v2: time that has passed since the save is the player's progress, so the design asks to be saved (the bar's
            // symbol, leaving, the autosave), as an edit does.
            if (!_simMarkedChanged && s.Tick > _simTickSaved && _simSavesFor != null) { _simSavesFor.MarkChanged(); _simMarkedChanged = true; }
            _ropes.Advance(s, Mathf.Min(0.25f, Time.unscaledDeltaTime), Pacing.Default.TickMs);
            _sunClock.Advance(s, Mathf.Min(0.25f, Time.unscaledDeltaTime), Pacing.Default.TickMs);   // task 4.06
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
            PushSunEveryFrame(_simClock.Now);   // task 4.06
            _simWasPaused = false;
        }

        void PushSun(ViewTime t)
        {
            if (!t.IsValid) return;
            Lighting.SetTime(t);
            _sunPushed = t;
        }

        void OnSimEvent(SimEvent e)
        {
            if (Hud == null || !_hudShown) return;
            if (OnWeatherEvent(e)) return;
            if (OnWaterEvent(e)) return;
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
                // TM-06 v3 / TM-08 v2 / TM-14 v2: every skip works at any time, so the only refusal left is a skip already running.
                case SimEventType.CommandRejected when e.Detail == (ushort)SimRefusal.SkipNotAllowed: Hud.Toast("A skip is already running"); break;
                // Only the player's own skips toast: scripted runs (the lapse's stills) skip quietly.
                case SimEventType.SkipStarted when _playerSkip: Hud.Toast($"Skipping to {SkipLabel(e.Value)}…"); break;
                case SimEventType.SkipFinished when _playerSkip: _playerSkip = false; Hud.Toast($"Skipped to {SkipLabel(e.Value)}"); break;
                case SimEventType.SkipCancelled when _playerSkip: _playerSkip = false; Hud.Toast("Skip cancelled"); break;
                case SimEventType.CommandRejected when e.Detail == (ushort)SimRefusal.NotInSummer: Hud.Toast("The mountain opens again next season"); break;
            }
        }

        LabelCalendar _skipCalendar;
        bool _playerSkip;

        /// <summary>A skip the player asked for (F1), which toasts its start and end.</summary>
        void PlayerSkip(SimCommandKind kind)
        {
            if (_simNow != null && _simNow.Skip == SkipKind.None) _playerSkip = true;
            SendSim(kind);
        }

        /// <summary>A skip's landing as the calendar names it ("December 1"), for its toasts (a few words a skip).</summary>
        string SkipLabel(long faceMs)
        {
            _skipCalendar ??= new LabelCalendar(Pacing.Default);
            int j = (int)(faceMs / SimWorld.DayMs % _skipCalendar.YearFaceDays);
            return System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(_skipCalendar.Month(j)) + " " + _skipCalendar.Day(j);
        }

        /// <summary>The bar's clock from the newest snapshot (10 Hz, from <see cref="UpdateHud"/>).</summary>
        void SetHudClock()
        {
            var s = _simNow;
            if (s == null || !_simClock.Now.IsValid) return;
            Hud.SetClock(_simClock.Now, s.Speed, s.Paused, HudText.SimDay(s.Phase, s.SeasonDay), HudText.SimDate(s.Month, s.Third, s.DayType));
            SetHudWeather();
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
            if (button(1, "Skip to morning")) PlayerSkip(SimCommandKind.SkipToMorning);
            if (button(2, "Next third")) PlayerSkip(SimCommandKind.SkipToNextThird);
            if (button(3, "Next month")) PlayerSkip(SimCommandKind.SkipToNextMonth);
            if (s.Skip != SkipKind.None && button(4, $"Cancel ({s.SkipPermille / 10}%)")) SendSim(SimCommandKind.CancelSkip);
            y += row;
            caption("Season seed");
            if (button(0, s.SeedPinned ? "Seed: pinned" : "Seed: drawn"))
                _sim.Send(s.SeedPinned ? SimCommand.Of(SimCommandKind.UnpinSeasonSeed) : SimCommand.Of(SimCommandKind.PinSeasonSeed, unchecked((long)s.SeasonSeed)));
            y += note(y, 1,
                $"{(s.Phase == SeasonPhase.PreSeason ? "Pre-season" : s.Phase == SeasonPhase.Season ? "Day " + s.SeasonDay : "Summer")} · {s.SeasonSeed:X16} · tick {s.TickMeanMicros} µs (p99 {s.TickP99Micros}) · {s.SustainedPermille / 10}% of speed · {s.OpenSecond / 3600}:{s.OpenSecond / 60 % 60:D2}–{s.StopSecond / 3600}:{s.StopSecond / 60 % 60:D2}{(s.HoursTrimmed ? " (daylight)" : "")}");
            if (s.TrailFineCells > 0)
                y += note(y, 1, $"Trail snow · {s.TrailFineMetres} m squares, {s.TrailFineCells:N0} · mean depth {s.TrailDepthMm / 25.4:F0} in · " +
                                $"{s.TrailPackedPermille / 10}% packed · {s.TrailsGroomedToday} groomed today · {s.TrailRemovedTotal * 64 / 1e6:F1} m³ of water off the 8 m snow");
            if (s.TrailFineCells > 0)
                y += note(y, 1, $"Trails · {s.TrailsCovered} covered, {s.TrailsMarginal} marginal, {s.TrailsBare} bare · {s.TrailsReady} open" +
                                (s.LateSeason ? " · late season (walking allowed)" : ""));
            DrawTrailSwitchRow(ref y, row, caption, button, note);   // task 4.07 B1 part 2c
            DrawWeatherRows(ref y, row, caption, note);
            DrawLiftOpsRows(ref y, row, x0, w, caption, button, note);
            DrawGuestRows(ref y, row, x0, w, caption, button, note);
        }
    }
}
