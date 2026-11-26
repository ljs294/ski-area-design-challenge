using MountainPlanner.Domain.Sky;
using MountainPlanner.Simulation;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Lights the scene from the clock (0.3 §4.7, task 11): the sun's position from the NOAA algorithm for the
    /// site and the view time (<see cref="SolarPosition"/>, on the resort's grid), the artistic moon at night
    /// (<see cref="SkyLight"/>, A4), and the look (sky colours, sky light and colour grading, which URP bakes
    /// into its LUT) blended from the four presets by the sun's elevation (<see cref="LightingPreset.Look"/>).
    /// Choosing a preset runs the clock to its time over <see cref="BlendSeconds"/>, so the sun sweeps across
    /// the sky. Work happens only when the time changes, and nothing here allocates per frame.
    /// </summary>
    public sealed class SceneLighting : MonoBehaviour
    {
        public Light Sun;
        /// <summary>The sky material (Sky.shader); the scene's skybox.</summary>
        public Material Sky;
        /// <summary>Distant terrain shadows, told whenever the light moves (set when a mountain opens).</summary>
        public FarTerrainShadow FarShadows
        {
            get => _farShadows;
            set { _farShadows = value; value?.SetLight(_toLight); }
        }
        public const float BlendSeconds = 1.5f;
        /// <summary>How far toward each preset's split-toning tints the grading goes.</summary>
        public const float SplitStrength = 0.4f;
        /// <summary>Distance haze (Haze.hlsl): none within the start, easing in to the strongest by the full distance.</summary>
        public const float HazeStart = 3000, HazeFull = 18000, HazeStrongest = 0.35f;
        /// <summary>The day the game opens on: mid-January, the heart of the season.</summary>
        public const int DefaultYear = 2026, DefaultDay = 15;
        /// <summary>Before a mountain opens: Jackson Hole (the demo), with its grid convergence.</summary>
        public const double DefaultLatitude = 43.593, DefaultLongitude = -110.848, DefaultConvergence = 8.95;
        /// <summary>Preset times: dawn this long after sunrise, golden hour this long before sunset, night at this hour.</summary>
        public const double DawnAfterSunrise = 20 * 60, GoldenBeforeSunset = 45 * 60, NightHour = 22;

        public bool HazeOn { get; private set; } = true;
        /// <summary>The view time (iteration 1's clock placeholder): the time scrubber and presets set it.</summary>
        public ManualViewClock Clock { get; private set; }
        public double Latitude { get; private set; } = DefaultLatitude;
        public double Longitude { get; private set; } = DefaultLongitude;
        public double GridConvergence { get; private set; } = DefaultConvergence;
        /// <summary>The preset the current look is closest to.</summary>
        public int Current { get; private set; } = LightingPreset.Noon;
        /// <summary>The light as last applied.</summary>
        public SkyLight CurrentLight { get; private set; }
        public string CurrentName => LightingPreset.All[Current].Name;
        /// <summary>Task 4.06: the weather's grade over the look (<see cref="Weather.WeatherGrade"/>), and the look as last applied.</summary>
        public Weather.WeatherGrade WeatherGrade { get; private set; }
        public LightingPreset Applied { get; private set; } = LightingPreset.All[LightingPreset.Noon];
        /// <summary>
        /// Task 4.06: 0 by day, 1 at night, eased over dusk (sun from 1° to 7° below the horizon), so the weather's grade and its
        /// cloud shadows fade instead of switching when the sun sets.
        /// </summary>
        public static float NightWeight(float sunElevation)
        {
            float t = Mathf.Clamp01((-1f - sunElevation) / 6f);
            return t * t * (3 - 2 * t);
        }

        /// <summary>Below this the sun's shadow passes are skipped (a storm's flat light saves them).</summary>
        public const float LeastShadow = 0.05f;

        /// <summary>Task 4.06: shadows fade to nothing as their strength falls to <see cref="LeastShadow"/>, where they switch off.</summary>
        public static float ShadowFade(float shadowStrength) => Mathf.SmoothStep(0, 1, Mathf.InverseLerp(LeastShadow, 2 * LeastShadow, shadowStrength));

        FarTerrainShadow _farShadows;
        Vector3 _toLight = Vector3.up;
        int _pinnedPreset = LightingPreset.Noon;   // the last preset chosen, until a time is set directly; -1 then
        double _animFrom, _animTo;
        float _anim = 1;
        bool _dirty = true;
        // Task 4.06: seconds past the clock's whole second the sun is drawn at (the simulation's time between snapshots).
        double _fraction;
        int _noonYear, _noonDay;
        double _noonLat = double.NaN, _noonLon, _noonSecond;
        Volume _volume;
        Tonemapping _tone;
        Vignette _vignette;
        /// <summary>The grade over the presets (beauty pass, item 1; <see cref="LookStyle"/>).</summary>
        public LookStyle Style { get; private set; } = LookStyle.All[LookStyle.Default];
        ColorAdjustments _adjust;
        WhiteBalance _white;
        SplitToning _split;
        Bloom _bloom;

        static readonly int ZenithId = Shader.PropertyToID("_Zenith"), HorizonId = Shader.PropertyToID("_Horizon"),
                            BelowId = Shader.PropertyToID("_Below"), SunColorId = Shader.PropertyToID("_SunColor"),
                            DiscId = Shader.PropertyToID("_DiscCos"), StarsId = Shader.PropertyToID("_Stars"),
                            SunDirectionId = Shader.PropertyToID("_SkySunDirection"),
                            HazeId = Shader.PropertyToID("_Haze"), HazeColorId = Shader.PropertyToID("_HazeColor"),
                            SnowZenithId = Shader.PropertyToID("_SnowSkyZenith"), SnowHorizonId = Shader.PropertyToID("_SnowSkyHorizon");

        void Awake()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Lighting preset";
            _tone = profile.Add<Tonemapping>(true);
            _tone.mode.Override(TonemappingMode.Neutral);
            _vignette = profile.Add<Vignette>(true);
            _vignette.intensity.Override(0);
            _vignette.smoothness.Override(0.45f);
            _adjust = profile.Add<ColorAdjustments>(true);
            _white = profile.Add<WhiteBalance>(true);
            _split = profile.Add<SplitToning>(true);
            _bloom = profile.Add<Bloom>(true);
            _bloom.threshold.Override(1.0f);
            _bloom.scatter.Override(0.6f);
            _volume = gameObject.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.sharedProfile = profile;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            EnsureClock();
            Set(_pinnedPreset, instant: true);
            Apply();
            SetHaze(HazeOn);
        }

        void EnsureClock()
        {
            if (Clock != null) return;
            Clock = new ManualViewClock(new ViewTime(DefaultYear, DefaultDay, 12 * 3600));
            Clock.Changed += _ => _dirty = true;
        }

        /// <summary>The opened mountain's place: latitude, longitude and grid convergence (degrees). A chosen preset keeps its time of day there.</summary>
        public void SetSite(double latitude, double longitude, double gridConvergence)
        {
            Latitude = latitude;
            Longitude = longitude;
            GridConvergence = gridConvergence;
            _dirty = true;
            if (_pinnedPreset >= 0) Set(_pinnedPreset, instant: true);
        }

        /// <summary>M: the distance haze on or off (0.5 §4 asks for a toggle).</summary>
        /// <summary>Picks the grade over the presets (`-look name`; beauty pass, item 1).</summary>
        public void SetStyle(int index)
        {
            if (index < 0 || index >= LookStyle.All.Length) return;
            Style = LookStyle.All[index];
            _dirty = true;
        }

        /// <summary>Task 4.06: the weather's grade; re-applies the look only when it moves.</summary>
        public void SetWeather(in Weather.WeatherGrade grade)
        {
            if (grade.Near(WeatherGrade)) return;
            WeatherGrade = grade;
            _dirty = true;
        }

        public void SetHaze(bool on)
        {
            HazeOn = on;
            Shader.SetGlobalVector(HazeId, new Vector4(HazeStart, HazeFull, HazeStrongest, on ? 1 : 0));
        }

        /// <summary>Dawn, noon, golden hour, night, dawn…</summary>
        public void Cycle() => Set((Current + 1) % LightingPreset.All.Length);

        /// <summary>Runs the clock to a preset's time on the current day (forward, through midnight if need be).</summary>
        public void Set(int index, bool instant = false)
        {
            if (index < 0 || index >= LightingPreset.All.Length) return;
            EnsureClock();
            _pinnedPreset = index;
            SetFraction(0);
            var now = Clock.Now;
            double target = PresetSecond(index, now.Year, now.DayOfYear);
            if (instant)
            {
                _anim = 1;
                Clock.Set(now.WithSecondOfDay((int)target));
                return;
            }
            _animFrom = now.SecondOfDay;
            _animTo = target < _animFrom ? target + ViewTime.SecondsPerDay : target;
            _anim = 0;
        }

        /// <summary>Sets the view time directly (the time and date scrubbers, -time).</summary>
        public void SetTime(ViewTime time) => SetTime(time, 0);

        /// <summary>
        /// Task 4.06: the view time and how many seconds past its whole second the sun stands (may be negative), so a running
        /// clock moves the sun, its shadows and the sky every frame instead of in whole seconds.
        /// </summary>
        public void SetTime(ViewTime time, double secondsPast)
        {
            EnsureClock();
            _pinnedPreset = -1;
            _anim = 1;
            SetFraction(secondsPast);
            Clock.Set(time);
        }

        void SetFraction(double secondsPast)
        {
            if (System.Math.Abs(secondsPast - _fraction) < 1e-4) return;
            _fraction = secondsPast;
            _dirty = true;
        }

        /// <summary>A preset's local time (seconds after midnight) on a day at the current site.</summary>
        public double PresetSecond(int index, int year, int dayOfYear)
        {
            var day = new SolarDay(Latitude, Longitude, year, dayOfYear);
            double noon = day.SolarNoon();
            switch (index)
            {
                case LightingPreset.Dawn:
                {
                    double rise = day.Sunrise();
                    return double.IsNaN(rise) ? noon - 3 * 3600 : rise + DawnAfterSunrise;
                }
                case LightingPreset.GoldenHour:
                {
                    double set = day.Sunset();
                    return double.IsNaN(set) ? noon + 3 * 3600 : set - GoldenBeforeSunset;
                }
                case LightingPreset.Night: return NightHour * 3600;
                default: return noon;
            }
        }

        void Update()
        {
            if (_anim < 1)
            {
                _anim = Mathf.Min(1, _anim + Time.unscaledDeltaTime / BlendSeconds);
                float t = _anim * _anim * (3 - 2 * _anim);
                int second = (int)System.Math.Round(_animFrom + (_animTo - _animFrom) * t) % ViewTime.SecondsPerDay;
                Clock.Set(Clock.Now.WithSecondOfDay(second));
            }
            if (_dirty) Apply();
        }

        void Apply()
        {
            _dirty = false;
            var now = Clock.Now;
            GroundLayers.SetSeason(now.DayOfYear, Latitude);   // task 4.28: the grass wears the sun's date (nothing unless the day changed)
            var day = new SolarDay(Latitude, Longitude, now.Year, now.DayOfYear);
            var light = SkyLight.From(day.SunAt(now.SecondOfDay + _fraction), GridConvergence);
            CurrentLight = light;
            var p = LightingPreset.Look((float)light.SunElevation, now.SecondOfDay < SolarNoon(day), out int nearest);
            Current = nearest;
            var s = Style;
            p.SunIntensity *= s.SunScale;
            p.Zenith = Color.Lerp(p.Zenith, p.Zenith * new Color(0.62f, 0.72f, 0.92f), s.DeeperSky);
            p.AmbientSky *= s.AmbientScale;
            p.AmbientEquator *= s.AmbientScale;
            p.AmbientGround *= s.AmbientScale;
            p.Exposure += s.ExposureAdd;
            p.Contrast += s.ContrastAdd;
            p.Saturation += s.SaturationAdd;
            float clearShadow = p.ShadowStrength;
            p = WeatherGrade.Apply(p, NightWeight((float)light.SunElevation));
            Applied = p;
            var toLight = _toLight = new Vector3((float)light.X, (float)light.Y, (float)light.Z);
            if (Sun != null)
            {
                Sun.transform.rotation = Quaternion.LookRotation(-toLight);
                Sun.color = p.SunColor;
                Sun.intensity = p.SunIntensity * (float)light.Strength;
                // No shadow passes for a dark light, or under a deck that leaves no shadow to draw (task 4.06); the shadows fade
                // out before they switch off, so a thickening deck never pops them.
                Sun.shadowStrength = p.ShadowStrength * ShadowFade(p.ShadowStrength);
                Sun.shadows = light.Strength > 0.001 && p.ShadowStrength > LeastShadow ? LightShadows.Soft : LightShadows.None;
            }
            _farShadows?.SetLight(toLight);
            _farShadows?.SetStrength(Mathf.Clamp01(p.ShadowStrength / Mathf.Max(clearShadow, 0.01f)) * ShadowFade(p.ShadowStrength));
            RenderSettings.ambientSkyColor = p.AmbientSky;
            RenderSettings.ambientEquatorColor = p.AmbientEquator;
            RenderSettings.ambientGroundColor = p.AmbientGround;
            if (Sky != null)
            {
                Sky.SetColor(ZenithId, p.Zenith);
                Sky.SetColor(HorizonId, p.Horizon);
                Sky.SetColor(BelowId, p.Below);
                Sky.SetColor(SunColorId, p.SunColor);
                Sky.SetFloat(DiscId, Mathf.Cos(p.DiscDegrees * 0.5f * Mathf.Deg2Rad));
                Sky.SetFloat(StarsId, p.Stars);
            }
            Shader.SetGlobalVector(SunDirectionId, toLight);
            Shader.SetGlobalColor(HazeColorId, p.Horizon);   // far ridges fade toward the sky's horizon
            // Task 4.08 PR B: the sky the snow's shine mirrors (SnowLighting.hlsl), the weather's grey already in it.
            Shader.SetGlobalColor(SnowZenithId, p.Zenith);
            Shader.SetGlobalColor(SnowHorizonId, p.Horizon);
            _adjust.postExposure.Override(p.Exposure);
            _adjust.contrast.Override(p.Contrast);
            _adjust.saturation.Override(p.Saturation);
            _white.temperature.Override(p.Temperature);
            _white.tint.Override(p.Tint);
            // Split toning: mid-grey is no change, so the preset's tints are applied part of the way from grey.
            var grey = new Color(0.5f, 0.5f, 0.5f);
            _split.shadows.Override(Color.Lerp(grey, p.SplitShadows, SplitStrength + s.CoolShadows));
            _tone.mode.Override(s.Tonemapper);
            _vignette.intensity.Override(s.Vignette);
            _split.highlights.Override(Color.Lerp(grey, p.SplitHighlights, SplitStrength));
            _bloom.intensity.Override(p.Bloom);
        }

        /// <summary>Solar noon for the day, cached: it only changes with the day or the site.</summary>
        double SolarNoon(in SolarDay day)
        {
            if (day.Year != _noonYear || day.DayOfYear != _noonDay || day.Latitude != _noonLat || day.Longitude != _noonLon)
            {
                _noonYear = day.Year;
                _noonDay = day.DayOfYear;
                _noonLat = day.Latitude;
                _noonLon = day.Longitude;
                _noonSecond = day.SolarNoon();
            }
            return _noonSecond;
        }

        void OnDestroy()
        {
            if (_volume != null && _volume.sharedProfile != null) Destroy(_volume.sharedProfile);
        }
    }
}
