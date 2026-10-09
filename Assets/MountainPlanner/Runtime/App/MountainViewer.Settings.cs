using System;
using System.Collections;
using MountainPlanner.Presentation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// The viewer's side of Settings (task P2-05): the saved graphics options at start, the terrain shading they
    /// switch live, and the Auto tree-detail timing (<see cref="TreeDetailTiming"/>).
    /// </summary>
    public sealed partial class MountainViewer
    {
        bool _noFarShadows, _noSkyOcclusion, _timingHeld;
        static bool _commandLineHeld;

        /// <summary>True while the Auto tree-detail timing runs (the benchmark waits for it).</summary>
        public static bool TimingTreeDetail { get; private set; }

        /// <summary>
        /// The player's graphics options, or with -quality the preset alone for this run, uncapped (benchmarks
        /// measure exactly B1). -treedetail auto measures Auto tree detail again on top of either (the acceptance
        /// benchmark: Auto must pick Medium or better and keep p95 within budget).
        /// </summary>
        void ApplyGraphicsSettings(string[] args)
        {
            var options = SettingsStore.CommandLineQuality(args)
                ? GraphicsOptions.For(QualityPresets.FromArgs(args, QualityPresets.Current))
                : QualityPresets.Saved();
            if (SettingsStore.CommandLineQuality(args) && !_commandLineHeld)
            {
                _commandLineHeld = true;   // once per run: the viewer starts again on every scene load
                FramePacing.Hold();
            }
            if (ForceTreeTiming(args)) options = options.WithTrees(TreeDetail.Auto);
            else if (options.Trees == TreeDetail.Auto)
                QualityPresets.SetAutoLodBias(TreeDetailTiming.Remembered(MachineId()));
            QualityPresets.Apply(options);
            FramePacing.Reapply();
            QualityPresets.Applied -= ApplyTerrainShading;
            QualityPresets.Applied += ApplyTerrainShading;
            TreeDetailTiming.Requested = () => { if (_resort != null && !TimingTreeDetail) StartCoroutine(TimeTreeDetail()); };
        }

        static bool ForceTreeTiming(string[] args)
        {
            int i = Array.IndexOf(args, "-treedetail");
            return i >= 0 && i + 1 < args.Length && args[i + 1] == "auto";
        }

        static string MachineId() => TreeDetailTiming.Machine(SystemInfo.graphicsDeviceName, Screen.width, Screen.height);

        /// <summary>Sky occlusion and distant shadows as Settings › Graphics says (Low has them off), unless the command line turned them off.</summary>
        void ApplyTerrainShading()
        {
            if (_farShadows == null) return;
            bool on = QualityPresets.Options.TerrainShading;
            _farShadows.SetEnabled(on && !_noFarShadows);
            _farShadows.SetSkyOcclusion(on && !_noSkyOcclusion);
        }

        /// <summary>
        /// Starts the timing when tree detail is Auto and this PC and screen size have no result yet, or when
        /// -treedetail auto asks. Captures and benchmarks without that flag never time (their frames must be the same every run).
        /// </summary>
        void StartTreeDetailTiming(string[] args)
        {
            bool forced = ForceTreeTiming(args);
            if (QualityPresets.Options.Trees != TreeDetail.Auto) return;
            if (!forced && (SettingsStore.CommandLineQuality(args) || Array.IndexOf(args, "-screenshot") >= 0 || Array.IndexOf(args, "-uicapture") >= 0
                            || Array.IndexOf(args, "-flowcapture") >= 0 || Array.IndexOf(args, "-clip") >= 0 || Array.IndexOf(args, "-pathmovie") >= 0)) return;
            if (!forced && TreeDetailTiming.Remembered(MachineId()) > 0) return;
            StartCoroutine(TimeTreeDetail());
        }

        IEnumerator TimeTreeDetail()
        {
            TimingTreeDetail = true;
            while (Forest == null) yield return null;
            while (!_resort.CoverReady.IsCompleted) yield return null;
            for (int i = 0; i < 30; i++) yield return null;   // the first frames after an open still hitch
            string machine = MachineId();
            var timing = new TreeDetailTiming();
            FramePacing.Hold();
            _timingHeld = true;
            QualitySettings.lodBias = timing.Bias;
            yield return null;
            bool abandoned = false;
            while (true)
            {
                if (QualityPresets.Options.Trees != TreeDetail.Auto) { abandoned = true; break; }   // the player chose a detail meanwhile
                if (timing.Feed(Time.unscaledDeltaTime * 1000f)) break;
                QualitySettings.lodBias = timing.Bias;
                yield return null;
            }
            FramePacing.Release();
            _timingHeld = false;
            TimingTreeDetail = false;
            if (abandoned) yield break;
            TreeDetailTiming.Remember(machine, timing.Chosen);
            QualityPresets.SetAutoLodBias(timing.Chosen);
            Debug.Log($"[MountainViewer] Auto tree detail: LOD bias {timing.Chosen} ({TreeDetailTiming.DetailOf(timing.Chosen)}) on {machine}; " +
                      $"p95 {timing.P95[0]:F1} / {timing.P95[1]:F1} / {timing.P95[2]:F1} / {timing.P95[3]:F1} ms at 3 / 2 / 1.5 / 1.");
        }

        /// <summary>Lets go of what the settings hooked (the viewer is rebuilt on every scene load).</summary>
        void ReleaseSettings()
        {
            QualityPresets.Applied -= ApplyTerrainShading;
            TreeDetailTiming.Requested = null;
            if (_timingHeld) FramePacing.Release();
            _timingHeld = false;
            TimingTreeDetail = false;
        }
    }
}
