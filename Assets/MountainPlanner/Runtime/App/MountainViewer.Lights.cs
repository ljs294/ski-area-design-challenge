using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Buildings;
using MountainPlanner.Simulation;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.50 phase 6, slice 3 (owner 2026-11-25): the buildings' lights at night. Once a frame, after the camera moved:
    /// - the two glow globals the building materials read (LiftStructure: <c>_BuildingNight</c> for the lamps' lenses,
    ///   <c>_BuildingDim</c> for the windows), each with the post exposure taken back out, set only when they change;
    /// - each lift's huts in use while the snapshot says it runs (open, scheduled, on hold or downloading; BuildingGlow),
    ///   or always with <c>-hutslit</c> (captures);
    /// - the lamps' halos (LampHaloLayer), one draw, only in the dark.
    /// - the lamps' light on walls, ground and snow (LampLightMap, part 2), rebuilt only when buildings or their use change.
    /// Round 3 (owner 2026-11-26: "still don't love the halos"; see all three): how a lamp's glare shows, by <c>-glare</c>:
    /// - <c>bloom</c>: the lenses burn brighter and a night-only bloom (a second volume over SceneLighting's, its own
    ///   threshold above the moonlit snow) spreads their glow, as most games do; a small dot only for lamps far off. The
    ///   default: the owner picked it from the three on 2026-11-26;
    /// - <c>none</c>: no glare, the bright lens and its pool alone;
    /// - <c>sprite</c>: round 2's lens-sized glare, near and far.
    /// </summary>
    public sealed partial class MountainViewer
    {
        public const string HutsLitFlag = "-hutslit", GlareFlag = "-glare";
        public enum GlareMode { Sprite, Bloom, None }
        /// <summary>Bloom mode: the lenses this much brighter (so they, and not the snow, cross the bloom's threshold); the
        /// bloom's threshold and strength on screen (linear, the exposure taken out); far dots from this distance.</summary>
        const float BloomLensBoost = 3f, BloomThreshold = 2f, BloomIntensity = 0.8f, BloomScatter = 0.7f, FarDotsFrom = 150f;
        GlareMode? _glare;
        Volume _nightBloomVolume;
        Bloom _nightBloom;
        static readonly int BuildingNightId = Shader.PropertyToID("_BuildingNight"), BuildingDimId = Shader.PropertyToID("_BuildingDim");
        float _buildingNight = -1, _buildingDim = -1;
        LampHaloLayer _halos;
        LampLightMap _lampMap;
        bool _halosTried;
        bool? _hutsLit;

        /// <summary>How many halos the last frame drew (tests, the log).</summary>
        public int LampHalosDrawn => _halos?.Count ?? 0;

        void TickBuildingLights()
        {
            float sun = Lighting != null ? (float)Lighting.CurrentLight.SunElevation : 90f;
            float night = SceneLighting.NightWeight(sun);
            float exposure = Lighting != null ? Lighting.Applied.Exposure : 0f;
            float back = Mathf.Pow(2f, -exposure);
            var mode = Glare;
            float n = night * back * (mode == GlareMode.Bloom ? BloomLensBoost : 1f), d = (float)BuildingGlow.Dim(sun) * back;
            TickNightBloom(mode == GlareMode.Bloom ? night : 0f, exposure);
            if (n != _buildingNight) { _buildingNight = n; Shader.SetGlobalFloat(BuildingNightId, n); }
            if (d != _buildingDim) { _buildingDim = d; Shader.SetGlobalFloat(BuildingDimId, d); }

            PushHutsInUse();
            if (SystemInfo.supportsComputeShaders) (_lampMap ??= new LampLightMap()).Tick(night, (float)BuildingGlow.Dim(sun));

            var layer = EnsureHalos();
            if (layer == null) return;
            layer.Begin();
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            if (night <= 0.004f || cam == null || _resort == null || mode == GlareMode.None) return;
            layer.ExposureEv = exposure;
            layer.NearFadeMetres = mode == GlareMode.Bloom ? FarDotsFrom : 0f;
            layer.Gather(night, sun);
            layer.Draw(cam);
        }

        /// <summary>How a lamp's glare shows (<c>-glare bloom|none|sprite</c>; bloom by default).</summary>
        public GlareMode Glare
        {
            get
            {
                if (_glare == null)
                {
                    var args = Environment.GetCommandLineArgs();
                    int i = Array.IndexOf(args, GlareFlag);
                    string v = i >= 0 && i + 1 < args.Length ? args[i + 1].ToLowerInvariant() : "";
                    _glare = v == "sprite" ? GlareMode.Sprite : v == "none" ? GlareMode.None : GlareMode.Bloom;
                }
                return _glare.Value;
            }
        }

        // Bloom mode's night bloom: its own global volume above SceneLighting's (which blooms only at golden hour), on only
        // while it is dark. Its threshold is set on screen, so the post exposure is taken out of it (URP compares before
        // exposure): BloomThreshold on screen sits above lit snow, below the boosted lenses and the brighter windows.
        void TickNightBloom(float night, float exposure)
        {
            if (night <= 0.004f)
            {
                if (_nightBloomVolume != null && _nightBloomVolume.weight != 0) _nightBloomVolume.weight = 0;
                return;
            }
            if (_nightBloomVolume == null)
            {
                var go = new GameObject("Night bloom (task 4.50)") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);
                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.name = "Night bloom";
                _nightBloom = profile.Add<Bloom>(true);
                _nightBloom.scatter.Override(BloomScatter);
                _nightBloomVolume = go.AddComponent<Volume>();
                _nightBloomVolume.isGlobal = true;
                _nightBloomVolume.priority = 50;
                _nightBloomVolume.sharedProfile = profile;
            }
            _nightBloomVolume.weight = 1;
            _nightBloom.intensity.Override(BloomIntensity * night);
            _nightBloom.threshold.Override(Mathf.LinearToGammaSpace(BloomThreshold * Mathf.Pow(2f, -exposure)));
        }

        void PushHutsInUse()
        {
            _hutsLit ??= Array.IndexOf(Environment.GetCommandLineArgs(), HutsLitFlag) >= 0;
            var s = _simNow;
            foreach (var kv in _liftViews)
            {
                var view = kv.Value.View;
                if (view == null) continue;
                bool inUse = _hutsLit.Value;
                if (!inUse && s != null)
                {
                    int i = s.IndexOfLift(kv.Key.Value);
                    inUse = i >= 0 && Runs(s.LiftStatus[i]);
                }
                view.HutsInUse = inUse;
            }
        }

        /// <summary>A lift's operators are at their huts: running, about to open, held for wind or downloading.</summary>
        static bool Runs(LiftOpStatus s) => s == LiftOpStatus.Open || s == LiftOpStatus.Scheduled || s == LiftOpStatus.OnHold || s == LiftOpStatus.Downloading;

        LampHaloLayer EnsureHalos()
        {
            if (_halosTried) return _halos;
            _halosTried = true;
            if (!SystemInfo.supportsComputeShaders) return null;
            var shader = Resources.Load<Shader>(LampHaloLayer.ShaderResource);
            if (shader == null) { Debug.LogWarning("[Lights] the halo shader is missing: " + LampHaloLayer.ShaderResource); return null; }
            _halos = new LampHaloLayer(shader);
            return _halos;
        }

        /// <summary>A capture's light state (its log), so a picture's claim can be checked.</summary>
        string BuildingLightsReport()
        {
            int buildings = 0, inUse = 0;
            var where = new System.Text.StringBuilder();
            foreach (var b in BuildingGlowState.All)
            {
                buildings++;
                if (b.InUse) inUse++;
                var t = b.transform;
                where.Append($"; {b.name} at {t.position.x:F0},{t.position.z:F0} facing {t.eulerAngles.y:F0}° (scale x {t.lossyScale.x:F0})");
            }
            return $"[Lights] glare {Glare}, night {_buildingNight:F3}, dim {_buildingDim:F3} (exposure out), buildings {buildings}, in use {inUse}, halos {LampHalosDrawn}, lamps lighting {_lampMap?.LampCount ?? 0} over {_lampMap?.TileCount ?? 0} tiles{where}";
        }

        void DisposeBuildingLights()
        {
            _halos?.Dispose();
            _halos = null;
            _halosTried = false;
            _lampMap?.Dispose();
            _lampMap = null;
            if (_nightBloomVolume != null)
            {
                var profile = _nightBloomVolume.sharedProfile;
                Destroy(_nightBloomVolume.gameObject);
                if (profile != null) Destroy(profile);
                _nightBloomVolume = null;
                _nightBloom = null;
            }
        }
    }
}
