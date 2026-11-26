using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Buildings;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.50 phase 6, slice 3 (owner 2026-11-25): the buildings' lights at night. Once a frame, after the camera moved:
    /// - the two glow globals the building materials read (LiftStructure: <c>_BuildingNight</c> for the lamps' lenses,
    ///   <c>_BuildingDim</c> for the windows), each with the post exposure taken back out, set only when they change;
    /// - each lift's huts in use while the snapshot says it runs (open, scheduled, on hold or downloading; BuildingGlow),
    ///   or always with <c>-hutslit</c> (captures);
    /// - the lamps' halos (LampHaloLayer), one draw, only in the dark.
    /// Nothing lights the ground (owner: pools of light wait for a later slice).
    /// </summary>
    public sealed partial class MountainViewer
    {
        public const string HutsLitFlag = "-hutslit";
        static readonly int BuildingNightId = Shader.PropertyToID("_BuildingNight"), BuildingDimId = Shader.PropertyToID("_BuildingDim");
        float _buildingNight = -1, _buildingDim = -1;
        LampHaloLayer _halos;
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
            float n = night * back, d = (float)BuildingGlow.Dim(sun) * back;
            if (n != _buildingNight) { _buildingNight = n; Shader.SetGlobalFloat(BuildingNightId, n); }
            if (d != _buildingDim) { _buildingDim = d; Shader.SetGlobalFloat(BuildingDimId, d); }

            PushHutsInUse();

            var layer = EnsureHalos();
            if (layer == null) return;
            layer.Begin();
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            if (night <= 0.004f || cam == null || _resort == null) return;
            layer.ExposureEv = exposure;
            layer.Gather(night, sun);
            layer.Draw(cam);
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
            return $"[Lights] night {_buildingNight:F3}, dim {_buildingDim:F3} (exposure out), buildings {buildings}, in use {inUse}, halos {LampHalosDrawn}{where}";
        }

        void DisposeBuildingLights()
        {
            _halos?.Dispose();
            _halos = null;
            _halosTried = false;
        }
    }
}
