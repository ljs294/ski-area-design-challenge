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
        /// <summary>The cloud field's tiles (m): both divide <see cref="DriftWrap"/>, so the drift wraps without a jump; the huge
        /// tile moves the cover about so the large one never shows its repeat.</summary>
        public const float LargeTile = 7000, SmallTile = 1800, HugeTile = 40000, DriftWrap = 63000, CoverVariation = 0.6f;
        /// <summary>Cloud shadows: the darkest a cloud makes the sun's light, and how fast they drift per mph of summit wind (m/s).</summary>
        public const float ShadowDepth = 0.8f, DriftPerMph = 1.4f;
        /// <summary>How fast the arcs' own looks come and go (per real second).</summary>
        public const float ArcEasePerSecond = 0.25f;
        /// <summary>Spindrift (B9): the summit wind (mph) it starts at and is full at. Gust patches over the far forest: the same.</summary>
        public const float SpindriftFrom = 20, SpindriftFull = 40, GustFrom = 14, GustFull = 40, GustWrap = 2100;
        /// <summary>The bluebird's sparkle: how far from the camera it shows (m).</summary>
        public const float GlintReach = 600;
        /// <summary>The front (B7): a cover change this big crosses the map over <see cref="FrontSeconds"/> of real time, <see cref="FrontWidth"/> wide, from <see cref="FrontReach"/> upwind to as far downwind.</summary>
        public const float FrontChange = 0.15f, FrontSeconds = 30f, FrontWidth = 2500f, FrontReach = 14000f;

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
                            SunDirId = Shader.PropertyToID("_SkySunDirection");

        /// <summary>The fog's and the glint's shader keywords (Weather.hlsl): on only while they'd show, since their code alone
        /// costs the terrain time. Both are on for the first <see cref="WarmFrames"/> frames, invisibly (their globals are still
        /// zero), so the driver builds those shaders while the mountain opens rather than when the first storm fogs.</summary>
        static readonly UnityEngine.Rendering.GlobalKeyword AirKeyword = UnityEngine.Rendering.GlobalKeyword.Create("WX_AIR"),
                                                            GlintKeyword = UnityEngine.Rendering.GlobalKeyword.Create("WX_GLINT");
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
        Task<Vector3[]> _spindriftPoints;
        float _gustRun;
        Texture2D _clouds;
        Vector2 _drift;
        float _fogScale = 1, _big, _rain, _building, _clearing, _bluebird, _thaw, _coldSnap;
        float _frontAt = -1, _frontCover, _frontDeck, _frontCurtain, _lastTargetCover = -1, _deckNow, _curtainNow;
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
                if (_on) Off(lighting);
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
                long hourKey = (long)h.LabelYear * 10000 + h.HourOfYear;
                _easer.Step(look, hourKey, s.FaceMs % 3_600_000, realSeconds, snap: skipping);
            }
            Arc = arc;
            var w = _easer.Shown;
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
            if (lighting != null) lighting.SetWeather(NoGrade ? default : grade);
            float aloft = Smooth(WeatherLook.Tune.VeilFrom, WeatherLook.Tune.VeilFull, cameraDistance);

            // The haze: the visibility past the clear day's at the target's height, thinning upward, so a clear day adds nothing.
            // From up high most of what falls hangs in the curtains, not evenly, so showers stand out from the gaps between them.
            float even = w.Rate > 0.05f && !NoPrecipitation ? WeatherLook.Visibility(w.Cover, w.Rate * (1 - CurtainShare * aloft), w.Snow) : w.Visibility;
            float vis = WeatherLook.ViewVisibility(Mathf.Max(w.Visibility, even), cameraDistance);
            float haze = Mathf.Max(0, 3.912f * (1f / vis - 1f / WeatherLook.Tune.ClearVisibility));
            if (haze < 1e-7f) haze = 0;

            // The deck: lit, holed, ragged; from the overview a structured veil over the mountain, never a lid.
            float deckThick = WeatherLook.Tune.DeckThickness;
            float deckCap = Mathf.Lerp(BrokenDeckMost, WeatherLook.Tune.SlabMost, Smooth(0.75f, 1f, w.Cover));
            float deck = w.Deck > 0.001f ? 3.912f / WeatherLook.SlabViewVisibility(DeckVisibility / w.Deck, deckThick, cameraDistance, deckCap) : 0;
            float curtain = 0;
            if (w.Rate > 0.05f && !NoPrecipitation)
            {
                float showerVis = CurtainAt1Mm * Mathf.Pow(1f / Mathf.Max(w.Rate, 0.1f), 0.7f);
                curtain = 3.912f / WeatherLook.SlabViewVisibility(showerVis, CurtainDepth, cameraDistance);
            }
            // The valley sea (an inversion's fog), or the cold snap's thin ice haze.
            float sea = Mathf.Max(w.Fog, IceHaze * _coldSnap);
            float seaDensity = sea > 0.001f ? 3.912f / (WeatherLook.Tune.FogVisibility / sea) : 0;

            // A new hour that changes the sky a lot sends a front across from upwind: ahead of it the last hour's cover, deck
            // and curtains, behind it the new ones.
            float targetCover = _easer.Target.Cover;
            if (_lastTargetCover >= 0 && Mathf.Abs(targetCover - _lastTargetCover) >= FrontChange && !_pinned && !skipping)
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
                _frontAt += Mathf.Max(0, realSeconds) / FrontSeconds;
                if (_frontAt >= 1) _frontAt = -1;
                else
                {
                    float along = Vector2.Dot(_frontCentre, _frontWay) + Mathf.Lerp(-FrontReach, FrontReach, _frontAt);
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
            float line = NoPrecipitation ? 0 : Smooth(0.05f, 1f, w.Rate);
            Shader.SetGlobalVector(SnowLineId, new Vector4(w.SnowLine, line, SnowLineBand, Mathf.Lerp(WetDarkening, RainWetDarkening, _rain)));

            // Nothing in the air that would show (a clear day): the shaders skip the fog's work entirely.
            bool air = haze > 0 || deckMost > 0 || seaDensity > 0 || curtainMost > 0 || line > 0;
            float most = Mathf.Lerp(MostFog, OverviewMostFog, aloft);
            Shader.SetGlobalVector(ParamsId, new Vector4(1, NoFog || !air ? 0 : _fogScale, ClearRadius, most));
            bool warming = _warm > 0;
            if (warming) _warm--;
            Shader.SetKeyword(AirKeyword, warming || (!NoFog && air && _fogScale > 0));
            Shader.SetGlobalVector(HazeId, new Vector4(haze, target.y, 1f / HazeHeight, SunGlow * (1 - grade.Overcast) * day));
            Shader.SetGlobalVector(DeckId, new Vector4(NoSkyClouds ? -100000 : w.DeckBase, w.DeckBase + deckThick, deckMost, Raggedness));
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
            // The light the deck is lit by: the sky's ambient, drained toward grey as the deck closes (a closed deck lights itself).
            var ambient = ((RenderSettings.ambientSkyColor + RenderSettings.ambientEquatorColor) * 0.5f).linear;
            float al = 0.2126f * ambient.r + 0.7152f * ambient.g + 0.0722f * ambient.b;
            ambient = Color.Lerp(ambient, new Color(al, al, al), 0.6f * grade.Overcast);
            Shader.SetGlobalVector(LightId, new Vector4(ambient.r, ambient.g, ambient.b, DeckBase));

            // The clouds drift with the wind in real time (a skip or 8× never streaks them). Their shadows are deep under a broken
            // sky, fade as the deck closes into flat light, and fade with the dusk (no shadow work at all when they'd not show).
            _drift += new Vector2(Mathf.Sin(toward), Mathf.Cos(toward)) * (w.Wind * DriftPerMph * Mathf.Max(0, realSeconds));
            _drift.x = Mathf.Repeat(_drift.x, DriftWrap);
            _drift.y = Mathf.Repeat(_drift.y, DriftWrap);
            float shadow = NoCloudShadow ? 0 : ShadowDepth * (1 - grade.Overcast) * Smooth(0.03f, 0.15f, w.Cover) * day;
            if (shadow < 0.002f) shadow = 0;
            Shader.SetGlobalVector(CloudId, new Vector4(w.Cover, shadow, -_drift.x, -_drift.y));
            float soft = 0.06f + 0.05f * Smooth(0, 2500, w.DeckBase - target.y);
            Shader.SetGlobalVector(CloudScaleId, new Vector4(1f / LargeTile, 1f / SmallTile, w.DeckBase, soft));
            float pixel = camera != null ? 2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, camera.pixelHeight) : 0.001f;
            Shader.SetGlobalVector(PixelId, new Vector4(pixel, LargeTile / CloudField.Size, 1f / HugeTile, CoverVariation));
            if (shadow > 0)
            {
                // The shadows' map, centred where the sun's ray from the target meets the deck (after the globals it reads).
                var toSun = ((Vector3)Shader.GetGlobalVector(SunDirId)).normalized;
                var run = new Vector2(toSun.x, toSun.z) / Mathf.Max(toSun.y, 0.12f);
                Shader.SetGlobalVector(SunRunId, new Vector4(run.x, run.y, 0, 0));
                var centre = new Vector2(target.x, target.z) + run * Mathf.Max(0, w.DeckBase - target.y);
                int size = ShadowMapSize > 0 ? ShadowMapSize : QualityPresets.Current >= QualityPreset.High ? CloudShadowMap.SizeHigh : CloudShadowMap.SizeLow;
                var eye = camera != null ? camera.transform.position : target + Vector3.up * cameraDistance;
                var shift = centre - new Vector2(target.x, target.z);
                if (!_shadowMap.Draw(centre, new Vector3(eye.x + shift.x, eye.z + shift.y, eye.y - target.y), size))
                    Shader.SetGlobalVector(CloudId, new Vector4(w.Cover, 0, -_drift.x, -_drift.y));
            }

            float towardGrid = (w.Direction + 180f + (float)gridConvergence) % 360f;
            if (!NoWind) wind?.SetWeather(w.Wind, towardGrid);
            _precip.PerShell = QualityPresets.Current == QualityPreset.Low ? Precipitation.PerShellLow
                             : QualityPresets.Current == QualityPreset.Medium ? Precipitation.PerShellMedium : Precipitation.PerShellHigh;
            var sky = ((RenderSettings.ambientSkyColor + RenderSettings.ambientEquatorColor) * 0.5f).linear;
            _precip.Draw(camera, w, towardGrid, frozen: skipping, off: thin || NoPrecipitation, realSeconds,
                         sun, sky, cameraDistance, _rain);

            // Spindrift off the exposed ridges in a gale (not in the rain or a thaw, and not while a tool is on), and gust
            // patches running over the far forest: the overview's wind cues. Both in real time; a skip freezes the spindrift.
            var area = Area ?? s?.WeatherFields.Terrain;
            if (area != null && area != _spindriftFor)
            {
                _spindriftFor = area;
                // Its own thread: on the pool it can queue behind the opener's and the tiles' work for minutes.
                _spindriftPoints = Task.Factory.StartNew(() => Weather.Spindrift.Points(area), System.Threading.CancellationToken.None,
                                                         TaskCreationOptions.LongRunning, TaskScheduler.Default);
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
            float drift = Smooth(SpindriftFrom, SpindriftFull, w.Wind) * (1 - _rain) * (1 - _thaw) * (thin || NoPrecipitation ? 0 : 1);
            if (_snapSpindrift && _spindrift.Count > 0) { _spindrift.Snap(drift); _snapSpindrift = false; }   // a pinned look shows at once
            _spindrift.Draw(camera, downwind * windMs, Mathf.Clamp01(w.Gust - 1), drift, realSeconds, skipping, sun, sky);
            _gustRun = Mathf.Repeat(_gustRun + windMs * 0.8f * Mathf.Max(0, realSeconds), GustWrap);
            Shader.SetGlobalVector(GustId, new Vector4(downwind.x, downwind.y, NoGust ? 0 : Smooth(GustFrom, GustFull, w.Wind), _gustRun));
            // The bluebird's sparkle (a cold snap's dry snow too) in the sun: 4.07 refines it with the snow's real age.
            float glint = Mathf.Max(_bluebird, 0.7f * _coldSnap) * (1 - grade.Overcast) * day;
            bool glints = glint > 0.01f && !NoGlint && cameraDistance < GlintCameraReach;
            Shader.SetKeyword(GlintKeyword, warming || glints);
            Shader.SetGlobalVector(GlintId, new Vector4(glints ? glint : 0, GlintReach, 0, 0));
        }

        void Off(SceneLighting lighting)
        {
            _on = false;
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetKeyword(AirKeyword, false);
            Shader.SetKeyword(GlintKeyword, false);
            Shader.SetGlobalVector(FrontId, Vector4.zero);
            _frontAt = -1;
            if (lighting != null) lighting.SetWeather(default);
        }

        public void Dispose()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            Shader.SetKeyword(AirKeyword, false);
            Shader.SetKeyword(GlintKeyword, false);
            _precip.Dispose();
            _spindrift.Dispose();
            _shadowMap.Dispose();
            if (_clouds != null) Object.Destroy(_clouds);
            _clouds = null;
        }

        static float Ease(float v, bool on, float k) => Mathf.MoveTowards(v, on ? 1 : 0, k);

        static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }
    }
}
