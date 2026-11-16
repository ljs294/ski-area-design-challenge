using System.Threading.Tasks;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.Presentation.Weather
{
    /// <summary>
    /// Task 4.06: puts the snapshot's weather on screen. Each frame it reads the newest snapshot (never the worker's state),
    /// eases <see cref="WeatherLook"/> in face time, and pushes one block of globals for Weather.hlsl (haze, the deck, the valley
    /// sea, the curtains, the clouds and their shadows), the grade for <see cref="SceneLighting"/> and the wind for
    /// <see cref="ForestWind"/>. Nothing here allocates per frame. A pinned look (<c>-wxlook</c>, for shots and the benchmark)
    /// replaces the snapshot's and snaps the arcs' own looks, so a shot shows the look it names.
    /// </summary>
    public sealed class WeatherView
    {
        /// <summary>The fog's clear radius around the camera (m), its most (0–1), and the share left while a tool or info view is on.</summary>
        public const float ClearRadius = 150, MostFog = 0.92f, OverviewMostFog = 0.8f, ToolFog = 0.25f;
        /// <summary>The haze's scale height (m): it halves every this far above the target, so the valleys fill first.</summary>
        public const float HazeHeight = 1100;
        /// <summary>How strongly the haze glows toward the sun on a broken day (0–1).</summary>
        public const float SunGlow = 0.35f;
        /// <summary>Inside the deck (m), how ragged its base is (m), and how dark its base is against the valley fog's colour.</summary>
        public const float DeckVisibility = 90, Raggedness = 220, DeckBase = 0.72f;
        /// <summary>The valley sea: the most it hides (0–1) and how ragged its top is (m). The cold snap's ice haze in the valleys.</summary>
        public const float SeaMost = 0.97f, SeaRaggedness = 60, IceHaze = 0.2f;
        /// <summary>From the overview a broken deck (cover under about 0.75) is at most this fogged where it's densest, rising to
        /// <see cref="WeatherLook.Tune.SlabMost"/> as it closes, so a building or clearing sky veils the mountain rather than
        /// whitening it.</summary>
        public const float BrokenDeckMost = 0.35f;
        /// <summary>Snow curtains (B6): the visibility in a shower at 1 mm/h (m), the depth they're judged over from the overview (m),
        /// the share of the cloud that falls (its densest cores), and how much of what falls hangs in them from up high.</summary>
        public const float CurtainAt1Mm = 1500, CurtainDepth = 1200, ShowerShare = 0.6f, CurtainShare = 0.85f;
        /// <summary>The snow-line veil (F5): the band's height (m) and how much darker the wet ground below it is (more in rain).</summary>
        public const float SnowLineBand = 45, WetDarkening = 0.18f, RainWetDarkening = 0.32f;
        /// <summary>The cloud field's tiles (m). The large tile and the valley sea's (a fifth of the drift on a tile of twice the
        /// size) divide <see cref="DriftWrap"/>; the small and huge tiles are read turned, so no wrap divides them, and
        /// <see cref="DriftCorrection"/> carries the whole drift into their lookups instead (task 4.06, F8: the wrap jumped them).
        /// The huge tile moves the cover about so the large one never shows its repeat.</summary>
        public const float LargeTile = 7000, SmallTile = 1800, HugeTile = 40000, DriftWrap = 315000, CoverVariation = 0.6f;
        /// <summary>Cloud shadows: the darkest a cloud makes the sun's light, and how fast they drift per mph of summit wind (m/s).</summary>
        public const float ShadowDepth = 0.8f, DriftPerMph = 1.4f;
        /// <summary>How fast the arcs' own looks come and go (per real second).</summary>
        public const float ArcEasePerSecond = 0.25f;
        /// <summary>Spindrift (B9): the summit wind (mph) it starts at and is full at. Gust patches over the far forest: the same.</summary>
        public const float SpindriftFrom = 20, SpindriftFull = 40, GustFrom = 14, GustFull = 40, GustWrap = 2100;
        /// <summary>The bluebird's sparkle: how far from the camera it shows (m).</summary>
        public const float GlintReach = 600;
        /// <summary>The front (B7): a cover change this big crosses the map over <see cref="FrontSeconds"/> of real time, <see cref="FrontWidth"/> wide, from <see cref="FrontReach"/> upwind to as far downwind.
        /// Task 4.06: never longer than <see cref="FrontHourShare"/> of the face hour's real length (at least <see cref="FrontLeastSeconds"/>),
        /// and a front still crossing finishes before another starts, so a fast clock never sends it back upwind.</summary>
        public const float FrontChange = 0.15f, FrontSeconds = 30f, FrontWidth = 2500f, FrontReach = 14000f, FrontHourShare = 0.8f, FrontLeastSeconds = 3f;

        static readonly int ParamsId = Shader.PropertyToID("_WxParams"), HazeId = Shader.PropertyToID("_WxHaze"),
                            DeckId = Shader.PropertyToID("_WxDeck"), ValleyId = Shader.PropertyToID("_WxValley"),
                            CurtainId = Shader.PropertyToID("_WxCurtain"), FogColorId = Shader.PropertyToID("_WxFogColor"),
                            FogLowId = Shader.PropertyToID("_WxFogLow"), SunId = Shader.PropertyToID("_WxSun"),
                            LightId = Shader.PropertyToID("_WxLight"), CloudId = Shader.PropertyToID("_WxCloud"),
                            CloudScaleId = Shader.PropertyToID("_WxCloudScale"), CloudTexId = Shader.PropertyToID("_WxCloudTex"),
                            FrontId = Shader.PropertyToID("_WxFront"), FrontOldId = Shader.PropertyToID("_WxFrontOld"),
                            FrontScaleId = Shader.PropertyToID("_WxFrontScale"), SnowLineId = Shader.PropertyToID("_WxSnowLine"),
                            PixelId = Shader.PropertyToID("_WxPixel"), GustId = Shader.PropertyToID("_WxGust"),
                            GlintId = Shader.PropertyToID("_WxGlint"), SunRunId = Shader.PropertyToID("_WxSunRun"),
                            SunDirId = Shader.PropertyToID("_SkySunDirection"), VolumeId = Shader.PropertyToID("_WxVolume"),
                            DeckVolId = Shader.PropertyToID("_WxDeckVol"), DeckShapeId = Shader.PropertyToID("_WxDeckShape"),
                            DriftTurnId = Shader.PropertyToID("_WxDriftTurn");

        /// <summary>The marched deck: the visibility inside a cell (m), how tall a broken sky's tallest cell is (a share of the
        /// deck's depth; a closed sky fills it), the longest stretch marched (m), and the most a broken deck hides seen from
        /// over it (so the overview looks down through real clouds, never at a lid).</summary>
        public const float CellVisibility = 250, BrokenCellHeight = 0.5f, DeckMarch = 15000, BrokenFromAbove = 0.45f;
        /// <summary>Task 4.06 (F3): the most a closed marched deck hides seen from over it, so the mountain still reads under it.</summary>
        public const float ClosedFromAbove = 0.45f;

        /// <summary>The rays: the sun's share of the haze's light (shadowed air loses it) and how far they're stepped (m).</summary>
        public const float RayShare = 0.6f, RayReach = 12000;
        /// <summary>The beams: the visibility of the air that scatters the sun toward the eye at its most (m), under a broken sky
        /// or a low sun; a clear midday and an overcast have none.</summary>
        public const float BeamVisibility = 90000;
        /// <summary><c>-wxdebug</c>: the pass shows the sun's reach through the air as grey instead of the weather.</summary>
        public bool VolumeDebug;
        /// <summary>How the air is drawn (<c>-wxvolume off|rays|full</c>); null: by quality (Low off, Medium rays, High up full).</summary>
        public WeatherVolumeMode? VolumeMode { get; set; }
        /// <summary>The screen pass, once a mode that needs it is asked for.</summary>
        public WeatherVolume Volume => _volume;
        WeatherVolume _volume;
        /// <summary>The weather's shader keywords (Weather.hlsl). The fog's and the glint's are on only while they'd show, since
        /// their code alone costs the terrain time; both are on for the first <see cref="WarmFrames"/> frames, invisibly (their
        /// globals are still zero), so the driver builds those shaders while the mountain opens rather than when the first storm
        /// fogs. A class of their own, made on first use in <see cref="Update"/>: the viewer makes its WeatherView in a field
        /// initializer, inside a MonoBehaviour's constructor, where Unity forbids GlobalKeyword.Create (the editor and the
        /// Development game throw; the release game doesn't check).</summary>
        static class Keywords
        {
            public static readonly UnityEngine.Rendering.GlobalKeyword Air = UnityEngine.Rendering.GlobalKeyword.Create("WX_AIR"),
                                                                       Glint = UnityEngine.Rendering.GlobalKeyword.Create("WX_GLINT"),
                                                                       Veil = UnityEngine.Rendering.GlobalKeyword.Create("WX_VEIL"),
                                                                       // The pass draws the air and skips what fogs itself (lifts, cables, skier figures).
                                                                       Keep = UnityEngine.Rendering.GlobalKeyword.Create("WX_KEEP");

            // An explicit static constructor drops beforefieldinit, so the runtime may not make these early.
            static Keywords() { }
        }
        public const int WarmFrames = 30;
        /// <summary>The glint shows within <see cref="GlintReach"/> of the camera, so from further than this it's off.</summary>
        public const float GlintCameraReach = 800;

        readonly WeatherLook.Easer _easer = new WeatherLook.Easer();
        int _warm = WarmFrames;
        readonly Precipitation _precip = new Precipitation();
        readonly WindRibbons _spindrift = new WindRibbons();
        readonly CloudShadowMap _shadowMap = new CloudShadowMap();
        WeatherTerrain _spindriftFor;
        bool _snapSpindrift;
        float _ceilingLift;
        Task<Vector3[]> _spindriftPoints;
        float _gustRun;
        Texture2D _clouds;
        Vector2 _drift;
        // The whole drift, never wrapped (task 4.06, F8): the turned tiles' lookups take what the wrap left out.
        double _driftX, _driftY;
        float _fogScale = 1, _big, _rain, _building, _clearing, _bluebird, _thaw, _coldSnap;
        float _frontAt = -1, _frontCover, _frontDeck, _frontCurtain, _lastTargetCover = -1, _deckNow, _curtainNow;
        long _lastFaceMs = -1;
        float _faceMsPerSecond;
        Vector2 _frontWay, _frontCentre;
        bool _pinned, _snapArcs, _on;
        WeatherLook.State _pin;
        WeatherLook.Arc _pinArc;

        /// <summary>The look on screen and its arc.</summary>
        public WeatherLook.State Shown => _easer.Shown;
        public WeatherLook.Arc Arc { get; private set; }
        public bool On => _on;
        /// <summary>The cloud field is baked (on a worker thread at the first weather): until then the sky is the clear day's.</summary>
        public bool Ready => _clouds != null;
        /// <summary>The effects switch (Weather effects: Full/Reduced/Minimal arrives with PR B); off shows the clear day.</summary>
        public bool Enabled { get; set; } = true;
        /// <summary>Bisect switches for cost runs (<c>-nofog</c>, <c>-nocloudshadow</c>, <c>-noprecip</c>).</summary>
        public bool NoFog, NoCloudShadow, NoPrecipitation;
        /// <summary>More bisect switches (<c>-nograde</c>, <c>-noglint</c>, <c>-nogust</c>, <c>-noskyclouds</c>, <c>-nowind</c>).</summary>
        public bool NoGrade, NoGlint, NoGust, NoSkyClouds, NoWind;
        /// <summary>The clouds' shadow map's size (0: by quality) and how often it's redrawn (<c>-wxmapsize</c>, <c>-wxmapevery</c>).</summary>
        public int ShadowMapSize { get; set; }
        public int ShadowMapEvery { get => _shadowMap.Every; set => _shadowMap.Every = Mathf.Max(1, value); }
        public Precipitation Precipitation => _precip;
        /// <summary>The ridges' spindrift (its emitters arrive a moment after the area's static inputs), and where it streams from.</summary>
        public WindRibbons Spindrift => _spindrift;
        public Vector3[] SpindriftPoints { get; private set; } = System.Array.Empty<Vector3>();
        /// <summary>The area's static weather inputs (the opener's), so the spindrift needn't wait for the clock to move an hour.</summary>
        public WeatherTerrain Area { get; set; }
        /// <summary>
        /// Settings › Weather › Cloud ceiling (task 4.06): the lowest the clouds may sit while it's on, in metres (the area's
        /// highest ground plus <see cref="WeatherOptions.CeilingClearance"/>); negative infinity before an area opens.
        /// </summary>
        public float CeilingFloor { get; set; } = float.NegativeInfinity;

        /// <summary>Pins a look (<c>-wxlook</c>): the snapshot's weather is ignored until <see cref="Unpin"/>.</summary>
        public void Pin(in WeatherLook.State look, WeatherLook.Arc arc)
        {
            _pinned = true; _pin = look; _pinArc = arc; _snapArcs = true; _snapSpindrift = true;
            _easer.Step(look, long.MinValue + 1, 0, 0, snap: true);
            _frontAt = -1;
        }

        public void Unpin() => _pinned = false;
        public bool Pinned => _pinned;

        /// <summary>
        /// One frame. <paramref name="thin"/>: a tool or an info view is on, so the fog thins and nothing hides the work.
        /// <paramref name="gridConvergence"/> turns the weather's true directions onto the map's grid.
        /// </summary>
        public void Update(SimSnapshot s, int zrefFeet, Camera camera, Vector3 target, float cameraDistance, bool thin, float realSeconds,
                           double gridConvergence, SceneLighting lighting, ForestWind wind)
        {
            bool have = _pinned || (s != null && s.WeatherKnown);
            if (have && _clouds == null)
            {
                _clouds = CloudField.TryMake();
                if (_clouds != null) Shader.SetGlobalTexture(CloudTexId, _clouds);
            }
            if (!Enabled || !have || _clouds == null)
            {
                if (_on) Off(lighting, wind);
                return;
            }

            WeatherLook.Arc arc;
            bool skipping = !_pinned && s.Skip != SkipKind.None;
            if (_pinned) { arc = _pinArc; _easer.Step(_pin, long.MinValue + 1, 0, realSeconds, snap: false); }
            else
            {
                var cell = default(WeatherCell);
                var terrain = s.WeatherFields.Terrain;
                if (terrain != null && s.WeatherFields.Hour >= 0)
                    s.WeatherFields.At(terrain.CellAt((long)Mathf.Round(target.x * 1000f), (long)Mathf.Round(target.z * 1000f)), out cell);
                var h = s.WeatherHour;
                var look = WeatherLook.From(h, s.WeatherBase, s.WeatherMid, s.WeatherSummit, cell, zrefFeet);
                arc = WeatherLook.ArcOf(h, s.WeatherSummit, s.WeatherEvents);
                long hourKey = s.FaceMs / 3_600_000;   // the face hour, counted on through the new year (task 4.06)
                _easer.Step(look, hourKey, s.FaceMs % 3_600_000, realSeconds, snap: skipping);
            }
            Arc = arc;
            var w = _easer.Shown;
            // Settings › Weather (task 4.06): the player's choices, each under its bisect switch.
            var clouds = WeatherOptions.Clouds;
            bool noSky = NoSkyClouds || clouds == CloudLook.Off;
            bool noPrecip = NoPrecipitation || !WeatherOptions.Shows(WeatherPart.Falling);
            bool noShadow = NoCloudShadow || clouds == CloudLook.Off || !WeatherOptions.Shows(WeatherPart.CloudShadows);
            bool noGrade = NoGrade || !WeatherOptions.Shows(WeatherPart.StormLight);
            bool noGlint = NoGlint || !WeatherOptions.Shows(WeatherPart.Sparkle);
            bool noWind = NoWind || !WeatherOptions.Shows(WeatherPart.TreeSway);
            bool noRays = !WeatherOptions.Shows(WeatherPart.Rays);
            float fogShare = WeatherOptions.FogPercent / (float)WeatherOptions.MaxFog;
            // The ceiling lifts the deck over the highest ground; turning it on eases the clouds up rather than jumping them
            // (a skip, a pinned look or the first weather snaps).
            float lift = CeilingLift(w.DeckBase, WeatherOptions.Ceiling ? CeilingFloor : float.NegativeInfinity);
            bool snapLift = skipping || _snapArcs || !_on;
            _ceilingLift = snapLift ? lift : Mathf.Lerp(_ceilingLift, lift, 1 - Mathf.Exp(-Mathf.Max(0, realSeconds) / WeatherLook.Tune.PanEaseSeconds));
            w.DeckBase += _ceilingLift;
            _on = true;
            float night = lighting != null ? SceneLighting.NightWeight((float)lighting.CurrentLight.SunElevation) : 0, day = 1 - night;

            // The arcs with looks of their own come and go slowly (a pinned look snaps them); the rest follow the eased numbers.
            float k = _snapArcs || skipping ? 1 : ArcEasePerSecond * Mathf.Max(0, realSeconds);
            _snapArcs = false;
            _big = Ease(_big, arc == WeatherLook.Arc.BigStorm, k);
            _rain = Ease(_rain, arc == WeatherLook.Arc.Rain, k);
            _building = Ease(_building, arc == WeatherLook.Arc.Building, k);
            _clearing = Ease(_clearing, arc == WeatherLook.Arc.Clearing, k);
            _bluebird = Ease(_bluebird, arc == WeatherLook.Arc.Bluebird, k);
            _thaw = Ease(_thaw, arc == WeatherLook.Arc.Thaw, k);
            _coldSnap = Ease(_coldSnap, arc == WeatherLook.Arc.ColdSnap, k);
            _fogScale = Mathf.MoveTowards(_fogScale, thin ? ToolFog : 1, 2f * Mathf.Max(0, realSeconds));

            var grade = new WeatherGrade
            {
                Overcast = Smooth(0.7f, 1f, w.Cover),
                Storm = Smooth(0.1f, 3f, w.Rate),
                BigStorm = _big, Rain = _rain, Building = _building, Clearing = _clearing,
                Bluebird = _bluebird, Thaw = _thaw, ColdSnap = _coldSnap,
            };
            if (lighting != null) lighting.SetWeather(noGrade ? default : grade);
            float aloft = Smooth(WeatherLook.Tune.VeilFrom, WeatherLook.Tune.VeilFull, cameraDistance);

            // The haze: the visibility past the clear day's at the target's height, thinning upward, so a clear day adds nothing.
            // From up high most of what falls hangs in the curtains, not evenly, so showers stand out from the gaps between them.
            float even = w.Rate > 0.05f && !noPrecip ? WeatherLook.Visibility(w.Cover, w.Rate * (1 - CurtainShare * aloft), w.Snow) : w.Visibility;
            // The screen pass (Medium up) can shadow the air, so with it a low sun gets a light haze even on a clear day, for the
            // ridges' shadows to fall across; by mid-morning it's gone.
            var mode = VolumeMode ?? ModeFor(WeatherVolume.ModeFor(QualityPresets.Current), clouds);
            if (mode != WeatherVolumeMode.Off && _volume == null) _volume = new WeatherVolume();
            bool volume = mode != WeatherVolumeMode.Off && _volume.Available && !NoFog;
            float elevation = lighting != null ? (float)lighting.CurrentLight.SunElevation : 45;
            float lowSun = volume ? (1 - Smooth(4, 16, elevation)) * Smooth(-2, 2, elevation) : 0;
            float clearest = Mathf.Lerp(WeatherLook.Tune.ClearVisibility, WeatherLook.Tune.LowSunVisibility, lowSun);
            float vis = WeatherLook.ViewVisibility(Mathf.Min(Mathf.Max(w.Visibility, even), clearest), cameraDistance);
            float haze = Mathf.Max(0, 3.912f * (1f / vis - 1f / WeatherLook.Tune.ClearVisibility));
            haze *= fogShare;   // Settings › Weather › Fog and haze
            if (haze < 1e-7f) haze = 0;

            // The deck: lit, holed, ragged; from the overview a structured veil over the mountain, never a lid.
            float deckThick = WeatherLook.Tune.DeckThickness;
            float deckCap = Mathf.Lerp(BrokenDeckMost, WeatherLook.Tune.SlabMost, Smooth(0.75f, 1f, w.Cover));
            float deck = w.Deck > 0.001f ? 3.912f / WeatherLook.SlabViewVisibility(DeckVisibility / w.Deck, deckThick, cameraDistance, deckCap) : 0;
            float curtain = 0;
            if (w.Rate > 0.05f && !noPrecip)
            {
                float showerVis = CurtainAt1Mm * Mathf.Pow(1f / Mathf.Max(w.Rate, 0.1f), 0.7f);
                curtain = 3.912f / WeatherLook.SlabViewVisibility(showerVis, CurtainDepth, cameraDistance);
            }
            // The valley sea (an inversion's fog), or the cold snap's thin ice haze.
            float sea = WeatherOptions.Shows(WeatherPart.ValleyFog) ? Mathf.Max(w.Fog, IceHaze * _coldSnap) : 0;
            float seaDensity = sea > 0.001f ? 3.912f / (WeatherLook.Tune.FogVisibility / sea) : 0;

            // A new hour that changes the sky a lot sends a front across from upwind: ahead of it the last hour's cover, deck
            // and curtains, behind it the new ones.
            float targetCover = _easer.Target.Cover;
            if (!_pinned && s != null) TrackFaceRate(s.FaceMs, realSeconds);
            if (_lastTargetCover >= 0 && Mathf.Abs(targetCover - _lastTargetCover) >= FrontChange && !_pinned && !skipping && _frontAt < 0)
            {
                float way = (_easer.Target.Direction + 180f + (float)gridConvergence) * Mathf.Deg2Rad;
                _frontWay = new Vector2(Mathf.Sin(way), Mathf.Cos(way));
                _frontCentre = new Vector2(target.x, target.z);
                _frontCover = w.Cover; _frontDeck = _deckNow; _frontCurtain = _curtainNow;
                _frontAt = 0;
            }
            _lastTargetCover = targetCover;
            _deckNow = deck; _curtainNow = curtain;
            float deckOld = deck, curtainOld = curtain, coverOld = w.Cover;
            if (_frontAt >= 0)
            {
                _frontAt += Mathf.Max(0, realSeconds) / FrontCrossSeconds(_faceMsPerSecond);
                if (_frontAt >= 1) _frontAt = -1;
                else
                {
                    // In the drifted field's coordinates (the shaders' q + _WxCloud.zw), so the front crosses the target, wherever
                    // the drift has got to (task 4.06).
                    float along = Vector2.Dot(_frontCentre, _frontWay) + Mathf.Lerp(-FrontReach, FrontReach, _frontAt) - Vector2.Dot(_drift, _frontWay);
                    Shader.SetGlobalVector(FrontId, new Vector4(_frontWay.x, _frontWay.y, along, 1f / FrontWidth));
                    deckOld = _frontDeck; curtainOld = _frontCurtain; coverOld = _frontCover;
                }
            }
            if (_frontAt < 0) Shader.SetGlobalVector(FrontId, Vector4.zero);
            float deckMost = Mathf.Max(deck, deckOld), curtainMost = Mathf.Max(curtain, curtainOld);
            Shader.SetGlobalVector(FrontOldId, new Vector4(coverOld, 0, 0, 0));
            Shader.SetGlobalVector(FrontScaleId, new Vector4(deckMost > 0 ? deck / deckMost : 1, deckMost > 0 ? deckOld / deckMost : 1,
                                                             curtainMost > 0 ? curtain / curtainMost : 1, curtainMost > 0 ? curtainOld / curtainMost : 1));

            // The snow line shows only while something falls: in all-rain the whole mountain is wet, in all-snow the line is below it.
            float line = noPrecip ? 0 : Smooth(0.05f, 1f, w.Rate);
            Shader.SetGlobalVector(SnowLineId, new Vector4(w.SnowLine, line, SnowLineBand, Mathf.Lerp(WetDarkening, RainWetDarkening, _rain)));

            // Nothing in the air that would show (a clear day): the shaders skip the fog's work entirely.
            bool air = haze > 0 || deckMost > 0 || seaDensity > 0 || curtainMost > 0 || line > 0;
            float most = Mathf.Lerp(MostFog, OverviewMostFog, aloft);
            Shader.SetGlobalVector(ParamsId, new Vector4(1, NoFog || !air ? 0 : _fogScale, ClearRadius, most));
            bool warming = _warm > 0;
            if (warming) _warm--;
            // With the screen pass the surfaces draw no air: only the snow line's veil and the closed deck's flat light, if any.
            bool airOn = !NoFog && air && _fogScale > 0;
            Shader.SetKeyword(Keywords.Air, warming || (airOn && !volume));
            Shader.SetKeyword(Keywords.Veil, airOn && volume && (line > 0 || w.Cover > 0.7f));
            Shader.SetKeyword(Keywords.Keep, airOn && volume);
            if (_volume != null)
            {
                _volume.Active = airOn && volume;
                _volume.Steps = QualityPresets.Current >= QualityPreset.High ? WeatherVolume.StepsHigh : WeatherVolume.StepsMedium;
                _volume.ShowReach = VolumeDebug;
            }
            float rays = volume && !noRays ? RayShare * (1 - grade.Overcast) * Smooth(-1, 3, elevation) : 0;
            float broken = Mathf.Clamp01(Mathf.Max(lowSun, 4 * w.Cover * (1 - w.Cover) * Smooth(0.05f, 0.25f, w.Cover)));
            float beam = NoFog ? 0 : 3.912f / BeamVisibility * broken * (rays / RayShare) * _fogScale;
            if (beam < 1e-7f) beam = 0;
            // The marched deck (High up): solid cells under a broken sky; as the sky closes, from the overview it thins to the
            // veil the flat deck is (never a lid), and a tool or an info view thins it like the rest of the air.
            bool marched = volume && mode == WeatherVolumeMode.Full && w.Cover > 0.03f && !noSky && _fogScale > 0;
            float eyeY = camera != null ? camera.transform.position.y : target.y + cameraDistance;
            // Task 4.06 (F2, F3): "from over it" counts from the deck's top, so inside the cloud it's solid all the way up.
            float veil = Mathf.Max(Smooth(0.7f, 0.95f, w.Cover), Smooth(-200, 400, eyeY - (w.DeckBase + deckThick))) * aloft;
            float marchedCap = Mathf.Lerp(BrokenFromAbove, ClosedFromAbove, Smooth(0.75f, 1f, w.Cover));
            float deckHides = Mathf.Lerp(1, marchedCap, veil) * _fogScale;
            int deckSteps = QualityPresets.Current >= QualityPreset.Ultra ? WeatherVolume.DeckStepsUltra : WeatherVolume.DeckStepsHigh;
            Shader.SetGlobalVector(DeckVolId, new Vector4(3.912f / CellVisibility, deckHides, deckSteps, marched ? 1 : 0));
            Shader.SetGlobalVector(DeckShapeId, new Vector4(Mathf.Lerp(BrokenCellHeight, 1, Smooth(0.6f, 0.95f, w.Cover)), DeckMarch, 0, 0));
            if (_volume != null)
            {
                _volume.Active = volume && (airOn || beam > 0 || marched);
                _volume.Rays = rays > 0 && (haze > 0 || seaDensity > 0 || beam > 0);
                _volume.Deck = marched;
            }
            Shader.SetGlobalVector(VolumeId, new Vector4(marched ? 2 : volume ? 1 : 0, rays, RayReach, beam));
            Shader.SetGlobalVector(HazeId, new Vector4(haze, target.y, 1f / HazeHeight, SunGlow * (1 - grade.Overcast) * day));
            Shader.SetGlobalVector(DeckId, new Vector4(noSky ? -100000 : w.DeckBase, w.DeckBase + deckThick, deckMost, Raggedness));
            Shader.SetGlobalVector(ValleyId, new Vector4(sea > 0.001f ? w.FogTop : -100000f, seaDensity, SeaMost, SeaRaggedness));
            float fall = Mathf.Lerp(6f, 1f, w.Snow), across = Mathf.Min(1.5f, w.Wind * 0.447f * 0.6f / fall);
            float toward = (w.Direction + 180f + (float)gridConvergence) * Mathf.Deg2Rad;
            Shader.SetGlobalVector(CurtainId, new Vector4(curtainMost, ShowerShare, -Mathf.Sin(toward) * across, -Mathf.Cos(toward) * across));

            // The fog's colours: aloft the graded horizon (so the fog is never lighter than the sky behind it), in the valleys
            // darker and a touch cooler, darker still in a storm. Never tinted far from grey: a storm reads grey, not navy.
            var horizon = (lighting != null ? lighting.Applied.Horizon : new Color(0.86f, 0.92f, 0.97f)).linear;
            float lowShade = 0.78f - 0.1f * grade.Storm;
            var low = new Color(horizon.r * lowShade * 0.98f, horizon.g * lowShade * 0.99f, horizon.b * lowShade * 1.01f);
            Shader.SetGlobalVector(FogColorId, new Vector4(horizon.r, horizon.g, horizon.b, 1f / 1500f));
            Shader.SetGlobalVector(FogLowId, new Vector4(low.r, low.g, low.b, target.y - 300));
            var sun = lighting != null && lighting.Sun != null ? lighting.Sun.color.linear * lighting.Sun.intensity : Color.white;
            Shader.SetGlobalVector(SunId, new Vector4(sun.r, sun.g, sun.b, day));
            // The light inside a cloud (task 4.06, F1, F4): see CloudLight.
            var ambient = ((RenderSettings.ambientSkyColor + RenderSettings.ambientEquatorColor) * 0.5f).linear;
            var cloudLight = CloudLight(horizon, sun, ambient, grade.Overcast);
            Shader.SetGlobalVector(LightId, new Vector4(cloudLight.r, cloudLight.g, cloudLight.b, DeckBase));

            // The clouds drift with the wind in real time (a skip or 8× never streaks them). Their shadows are deep under a broken
            // sky, fade as the deck closes into flat light, and fade with the dusk (no shadow work at all when they'd not show).
            var moved = new Vector2(Mathf.Sin(toward), Mathf.Cos(toward)) * (w.Wind * DriftPerMph * Mathf.Max(0, realSeconds));
            _driftX += moved.x;
            _driftY += moved.y;
            _drift = new Vector2((float)Wrap(_driftX), (float)Wrap(_driftY));
            Shader.SetGlobalVector(DriftTurnId, DriftCorrection(_driftX, _driftY));
            float shadow = noShadow ? 0 : ShadowDepth * (1 - grade.Overcast) * Smooth(0.03f, 0.15f, w.Cover) * day;
            if (shadow < 0.002f) shadow = 0;
            Shader.SetGlobalVector(CloudId, new Vector4(w.Cover, shadow, -_drift.x, -_drift.y));
            float soft = 0.06f + 0.05f * Smooth(0, 2500, w.DeckBase - target.y);
            Shader.SetGlobalVector(CloudScaleId, new Vector4(1f / LargeTile, 1f / SmallTile, w.DeckBase, soft));
            float pixel = camera != null ? 2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, camera.pixelHeight) : 0.001f;
            Shader.SetGlobalVector(PixelId, new Vector4(pixel, LargeTile / CloudField.Size, 1f / HugeTile, CoverVariation));
            if (shadow > 0 || (rays > 0 && w.Cover > 0.03f))   // the rays read the clouds' map too
            {
                // The shadows' map, centred where the sun's ray from the target meets the deck (after the globals it reads).
                var toSun = ((Vector3)Shader.GetGlobalVector(SunDirId)).normalized;
                var run = new Vector2(toSun.x, toSun.z) / Mathf.Max(toSun.y, 0.12f);
                Shader.SetGlobalVector(SunRunId, new Vector4(run.x, run.y, 1, 0));
                var centre = new Vector2(target.x, target.z) + run * Mathf.Max(0, w.DeckBase - target.y);
                int size = ShadowMapSize > 0 ? ShadowMapSize : QualityPresets.Current >= QualityPreset.High ? CloudShadowMap.SizeHigh : CloudShadowMap.SizeLow;
                var eye = camera != null ? camera.transform.position : target + Vector3.up * cameraDistance;
                var shift = centre - new Vector2(target.x, target.z);
                if (!_shadowMap.Draw(centre, new Vector3(eye.x + shift.x, eye.z + shift.y, eye.y - target.y), size))
                {
                    Shader.SetGlobalVector(CloudId, new Vector4(w.Cover, 0, -_drift.x, -_drift.y));
                    Shader.SetGlobalVector(SunRunId, Vector4.zero);
                }
            }
            else Shader.SetGlobalVector(SunRunId, Vector4.zero);

            float towardGrid = (w.Direction + 180f + (float)gridConvergence) % 360f;
            if (!noWind) wind?.SetWeather(w.Wind, towardGrid);
            else wind?.ClearWeather();   // the trees back to the player's own wind
            _precip.PerShell = QualityPresets.Current == QualityPreset.Low ? Precipitation.PerShellLow
                             : QualityPresets.Current == QualityPreset.Medium ? Precipitation.PerShellMedium : Precipitation.PerShellHigh;
            var sky = ((RenderSettings.ambientSkyColor + RenderSettings.ambientEquatorColor) * 0.5f).linear;
            _precip.Draw(camera, w, towardGrid, frozen: skipping, off: thin || noPrecip, realSeconds,
                         sun, sky, cameraDistance, _rain);

            // Spindrift off the exposed ridges in a gale (not in the rain or a thaw, and not while a tool is on), and gust
            // patches running over the far forest: the overview's wind cues. Both in real time; a skip freezes the spindrift.
            var area = Area ?? s?.WeatherFields.Terrain;
            if (area != null && area != _spindriftFor)
            {
                _spindriftFor = area;
                _spindriftPoints = FindSpindrift(area);
            }
            if (_spindriftPoints != null && _spindriftPoints.IsCompleted)
            {
                if (_spindriftPoints.Status == TaskStatus.RanToCompletion)
                {
                    SpindriftPoints = _spindriftPoints.Result;
                    var e = Weather.Spindrift.Emitters(SpindriftPoints);
                    _spindrift.SetEmitters(e, e.Length);
                    Debug.Log($"[Weather] spindrift streams from {e.Length} ridge points");
                }
                else Debug.LogWarning("[Weather] spindrift: the ridge search failed: " + _spindriftPoints.Exception?.GetBaseException().Message);
                _spindriftPoints = null;
            }
            float windMs = w.Wind * 0.447f;
            var downwind = new Vector2(Mathf.Sin(toward), Mathf.Cos(toward));
            float drift = Smooth(SpindriftFrom, SpindriftFull, w.Wind) * (1 - _rain) * (1 - _thaw) * (thin || noPrecip || !WeatherOptions.Shows(WeatherPart.Spindrift) ? 0 : 1);
            if (_snapSpindrift && _spindrift.Count > 0) { _spindrift.Snap(drift); _snapSpindrift = false; }   // a pinned look shows at once
            _spindrift.Draw(camera, downwind * windMs, Mathf.Clamp01(w.Gust - 1), drift, realSeconds, skipping, sun, sky);
            _gustRun = Mathf.Repeat(_gustRun + windMs * 0.8f * Mathf.Max(0, realSeconds), GustWrap);
            Shader.SetGlobalVector(GustId, new Vector4(downwind.x, downwind.y, NoGust ? 0 : Smooth(GustFrom, GustFull, w.Wind), _gustRun));
            // The bluebird's sparkle (a cold snap's dry snow too) in the sun: 4.07 refines it with the snow's real age.
            float glint = Mathf.Max(_bluebird, 0.7f * _coldSnap) * (1 - grade.Overcast) * day;
            bool glints = glint > 0.01f && !noGlint && cameraDistance < GlintCameraReach;
            Shader.SetKeyword(Keywords.Glint, warming || glints);
            Shader.SetGlobalVector(GlintId, new Vector4(glints ? glint : 0, GlintReach, 0, 0));
        }

        static double Wrap(double v) => v - System.Math.Floor(v / DriftWrap) * DriftWrap;

        /// <summary>
        /// Task 4.06, Settings › Weather › Clouds: how the clouds are drawn for the tier's own mode. 3D marches the deck on any
        /// tier; Flat never does (the rays stay where the tier has them); Auto and Off keep the tier's (Off hides the clouds).
        /// </summary>
        public static WeatherVolumeMode ModeFor(WeatherVolumeMode tier, CloudLook clouds) =>
            clouds == CloudLook.ThreeD ? WeatherVolumeMode.Full
            : clouds == CloudLook.Flat && tier == WeatherVolumeMode.Full ? WeatherVolumeMode.Rays : tier;

        /// <summary>Task 4.06, the cloud ceiling: how far the deck's base must rise to sit at <paramref name="floor"/> or above.</summary>
        public static float CeilingLift(float deckBase, float floor) => float.IsNegativeInfinity(floor) ? 0 : Mathf.Max(0, floor - deckBase);

        /// <summary>
        /// Task 4.06 (F8): what the turned tiles' lookups add for the drift the wrap took out (the shaders see the drift wrapped at
        /// <see cref="DriftWrap"/>; the turned tiles don't repeat there). xy: the small tile's UV, zw: the huge tile's. Worked in
        /// double from the whole drift, so the clouds never jump.
        /// </summary>
        public static Vector4 DriftCorrection(double totalX, double totalY)
        {
            // The field is read at q - drift; the shaders have q - wrapped, so the turned reads take -(turn(drift - wrapped)).
            double kx = totalX - Wrap(totalX), ky = totalY - Wrap(totalY);
            const double a = 0.7986f, b = 0.6018f;   // WxTurn's, as the shader has them (float)
            double sx = (kx * a - ky * b) / SmallTile, sy = (kx * b + ky * a) / SmallTile;
            double hx = (ky * a - kx * b) / HugeTile, hy = (ky * b + kx * a) / HugeTile;   // the huge tile is read turned from q.yx
            return new Vector4((float)-Frac(sx), (float)-Frac(sy), (float)-Frac(hx), (float)-Frac(hy));
        }

        static double Frac(double v) => v - System.Math.Floor(v);

        /// <summary>
        /// Task 4.06 (F1, F4, F5): the light scattered inside a cloud. By day it's the haze's own colour (the graded horizon), so
        /// walking into a cloud keeps the brightness outside it instead of dropping to the blue sky light; at night and dusk the
        /// moon and the sky light it as brightly as they light the snow. Mostly grey: clouds are white-grey, never sky-blue.
        /// </summary>
        public static Color CloudLight(Color horizon, Color sun, Color ambient, float overcast)
        {
            var c = new Color(Mathf.Max(horizon.r, 0.55f * sun.r + 1.2f * ambient.r), Mathf.Max(horizon.g, 0.55f * sun.g + 1.2f * ambient.g),
                              Mathf.Max(horizon.b, 0.55f * sun.b + 1.2f * ambient.b));
            float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            return Color.Lerp(c, new Color(l, l, l), 0.5f + 0.3f * Mathf.Clamp01(overcast));
        }

        /// <summary>Task 4.06: the real seconds a front takes to cross, for a clock running this many face ms per real second.</summary>
        public static float FrontCrossSeconds(float faceMsPerSecond)
        {
            if (faceMsPerSecond <= 0) return FrontSeconds;
            return Mathf.Clamp(FrontHourShare * 3_600_000f / faceMsPerSecond, FrontLeastSeconds, FrontSeconds);
        }

        // How fast the face clock runs (face ms per real second), eased over a few seconds; a jump (a skip, a load) is ignored.
        void TrackFaceRate(long faceMs, float realSeconds)
        {
            if (_lastFaceMs >= 0 && realSeconds > 0)
            {
                long moved = faceMs - _lastFaceMs;
                if (moved >= 0 && moved < 3_600_000)
                    _faceMsPerSecond = Mathf.Lerp(_faceMsPerSecond, moved / realSeconds, 1 - Mathf.Exp(-realSeconds / 2f));
            }
            _lastFaceMs = faceMs;
        }

        void Off(SceneLighting lighting, ForestWind wind)
        {
            _on = false;
            _lastTargetCover = -1;   // turned back on, the sky starts where it is rather than sending a front
            wind?.ClearWeather();
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetGlobalVector(VolumeId, Vector4.zero);
            Shader.SetKeyword(Keywords.Air, false);
            Shader.SetKeyword(Keywords.Veil, false);
            Shader.SetKeyword(Keywords.Keep, false);
            if (_volume != null) _volume.Active = false;
            Shader.SetKeyword(Keywords.Glint, false);
            Shader.SetGlobalVector(FrontId, Vector4.zero);
            _frontAt = -1;
            if (lighting != null) lighting.SetWeather(default);
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetKeyword(Keywords.Air, false);
            Shader.SetKeyword(Keywords.Glint, false);
            Shader.SetKeyword(Keywords.Veil, false);
            Shader.SetKeyword(Keywords.Keep, false);
            _volume?.Dispose();
            _volume = null;
            _precip.Dispose();
            _spindrift.Dispose();
            _shadowMap.Dispose();
            if (_clouds != null) Object.Destroy(_clouds);
            _clouds = null;
        }

        static float Ease(float v, bool on, float k) => Mathf.MoveTowards(v, on ? 1 : 0, k);

        // The ridge search on its own thread: on the pool it can queue behind the opener's and the tiles' work for minutes. A
        // method of its own because a lambda that captures a local makes its closure where the local is declared: written
        // inline in Update, that cost 32 bytes of garbage every frame, even on the frames that never start a search.
        static Task<Vector3[]> FindSpindrift(WeatherTerrain area) =>
            Task.Factory.StartNew(() => Weather.Spindrift.Points(area), System.Threading.CancellationToken.None,
                                  TaskCreationOptions.LongRunning, TaskScheduler.Default);

        static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }
    }
}
