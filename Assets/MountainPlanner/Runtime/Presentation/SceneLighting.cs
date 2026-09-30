using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Applies a <see cref="LightingPreset"/> to the scene (0.5 §4, style tile): the sun's direction, colour
    /// and shadows, the sky (Sky.shader), the sky light every shader takes its shadowed side from (trilight
    /// ambient), and the colour grading in a global volume (URP bakes it into its LUT). Switching presets
    /// blends over <see cref="BlendSeconds"/>. Nothing here allocates per frame.
    /// </summary>
    public sealed class SceneLighting : MonoBehaviour
    {
        public Light Sun;
        /// <summary>The sky material (Sky.shader); the scene's skybox.</summary>
        public Material Sky;
        public const float BlendSeconds = 1.5f;
        /// <summary>How far toward each preset's split-toning tints the grading goes.</summary>
        public const float SplitStrength = 0.4f;
        /// <summary>Distance haze (Haze.hlsl): none within the start, easing in to the strongest by the full distance.</summary>
        public const float HazeStart = 3000, HazeFull = 18000, HazeStrongest = 0.35f;
        public bool HazeOn { get; private set; } = true;

        public int Current { get; private set; } = 1;   // noon
        LightingPreset _from, _shown;
        float _blend = 1;
        Volume _volume;
        ColorAdjustments _adjust;
        WhiteBalance _white;
        SplitToning _split;
        Bloom _bloom;

        static readonly int ZenithId = Shader.PropertyToID("_Zenith"), HorizonId = Shader.PropertyToID("_Horizon"),
                            BelowId = Shader.PropertyToID("_Below"), SunColorId = Shader.PropertyToID("_SunColor"),
                            DiscId = Shader.PropertyToID("_DiscCos"), StarsId = Shader.PropertyToID("_Stars"),
                            SunDirectionId = Shader.PropertyToID("_SkySunDirection"),
                            HazeId = Shader.PropertyToID("_Haze"), HazeColorId = Shader.PropertyToID("_HazeColor");

        public string CurrentName => LightingPreset.All[Current].Name;

        void Awake()
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Lighting preset";
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);
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
            _shown = LightingPreset.All[Current];
            Apply(_shown);
            SetHaze(HazeOn);
        }

        /// <summary>M: the distance haze on or off (0.5 §4 asks for a toggle).</summary>
        public void SetHaze(bool on)
        {
            HazeOn = on;
            Shader.SetGlobalVector(HazeId, new Vector4(HazeStart, HazeFull, HazeStrongest, on ? 1 : 0));
        }

        /// <summary>Dawn, noon, golden hour, night, dawn…, blended.</summary>
        public void Cycle() => Set((Current + 1) % LightingPreset.All.Length);

        public void Set(int index, bool instant = false)
        {
            if (index < 0 || index >= LightingPreset.All.Length) return;
            _from = _shown;
            Current = index;
            _blend = instant ? 1 : 0;
            if (instant) Apply(_shown = LightingPreset.All[index]);
        }

        void Update()
        {
            if (_blend >= 1) return;
            _blend = Mathf.Min(1, _blend + Time.unscaledDeltaTime / BlendSeconds);
            float t = _blend * _blend * (3 - 2 * _blend);
            Apply(_shown = LightingPreset.Lerp(_from, LightingPreset.All[Current], t));
        }

        void Apply(in LightingPreset p)
        {
            var toSun = p.ToSun;
            if (Sun != null)
            {
                Sun.transform.rotation = Quaternion.LookRotation(-toSun);
                Sun.color = p.SunColor;
                Sun.intensity = p.SunIntensity;
                Sun.shadowStrength = p.ShadowStrength;
            }
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
            Shader.SetGlobalVector(SunDirectionId, toSun);
            Shader.SetGlobalColor(HazeColorId, p.Horizon);   // far ridges fade toward the sky's horizon
            _adjust.postExposure.Override(p.Exposure);
            _adjust.contrast.Override(p.Contrast);
            _adjust.saturation.Override(p.Saturation);
            _white.temperature.Override(p.Temperature);
            _white.tint.Override(p.Tint);
            // Split toning: mid-grey is no change, so the preset's tints are applied part of the way from grey.
            var grey = new Color(0.5f, 0.5f, 0.5f);
            _split.shadows.Override(Color.Lerp(grey, p.SplitShadows, SplitStrength));
            _split.highlights.Override(Color.Lerp(grey, p.SplitHighlights, SplitStrength));
            _bloom.intensity.Override(p.Bloom);
        }

        void OnDestroy()
        {
            if (_volume != null && _volume.sharedProfile != null) Destroy(_volume.sharedProfile);
        }
    }
}
