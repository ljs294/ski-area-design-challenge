using System;
using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>The four quality presets (0.4 S8; task 15). Medium is the minimum-spec target (T13), High the reference.</summary>
    public enum QualityPreset { Low, Medium, High, Ultra }

    /// <summary>
    /// One Unity quality level per preset (ProjectSettings/QualitySettings.asset), each with its own URP asset in
    /// Assets/Settings (anti-aliasing, render scale, shadow distance, cascades and resolution, soft shadows) and its
    /// own LOD bias, which sets how far the trees keep their detailed models. On top of that, each preset picks the
    /// terrain's detail (<see cref="TerrainDetail"/>) and whether the terrain's sky occlusion and distant shadows
    /// (<see cref="FarTerrainShadow"/>) are on.
    /// </summary>
    public static class QualityPresets
    {
        /// <summary>The preset applied last (High until something else is applied).</summary>
        public static QualityPreset Current { get; private set; } = QualityPreset.High;

        /// <summary>The quality level's name in QualitySettings, which is the preset's name.</summary>
        public static string LevelName(QualityPreset preset) => preset.ToString();

        public static TerrainDetail Terrain(QualityPreset preset) => preset switch
        {
            QualityPreset.Low => TerrainDetail.Low,
            QualityPreset.Medium => TerrainDetail.Medium,
            QualityPreset.Ultra => TerrainDetail.Ultra,
            _ => TerrainDetail.High,
        };

        /// <summary>Sky occlusion from the terrain (beauty pass, item 2) and distant terrain shadows: off on Low only.</summary>
        public static bool TerrainShading(QualityPreset preset) => preset != QualityPreset.Low;

        /// <summary>Reads -quality low|medium|high|ultra from the command line; <paramref name="fallback"/> when it's absent or unknown.</summary>
        public static QualityPreset FromArgs(string[] args, QualityPreset fallback = QualityPreset.High)
        {
            int i = Array.IndexOf(args, "-quality");
            return i >= 0 && i + 1 < args.Length && Enum.TryParse(args[i + 1], true, out QualityPreset p) ? p : fallback;
        }

        /// <summary>Switches Unity to the preset's quality level (and so its URP asset and LOD bias). Call before a mountain opens.</summary>
        public static void Apply(QualityPreset preset)
        {
            string name = LevelName(preset);
            int level = Array.IndexOf(QualitySettings.names, name);
            if (level < 0)
            {
                Debug.LogWarning($"[QualityPresets] No quality level named {name}; keeping {QualitySettings.names[QualitySettings.GetQualityLevel()]}.");
                return;
            }
            if (QualitySettings.GetQualityLevel() != level) QualitySettings.SetQualityLevel(level, applyExpensiveChanges: true);
            Current = preset;
        }
    }
}
