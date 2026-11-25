using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Task 4.08 PR B (SN-13 v3): how each snow type takes the light, indexed by the simulation's SnowType. A little colour
    /// tells four families apart (owner, 2026-11-25: fresh, packed and groomed, hard and icy, spring and wet), and inside a
    /// family the type shows in three separate things (owner, 2026-11-24): shine, a glassy reflection of the sky and the sun
    /// (ice); sparkle, a twinkle across the snow (powder, corn); and softness. The surface itself is smooth: no type has a
    /// texture of its own. Snow.hlsl's SnowLookTint, SnowLookShine,
    /// SnowLookSparkle and SnowLookTracks hold the same numbers (SnowLooksTests checks they match), and SnowLighting.hlsl
    /// draws them. The finish (corduroy, moguls, tracks) is drawn over the type.
    /// </summary>
    public static class SnowLooks
    {
        public readonly struct Row
        {
            /// <summary>The tint over the snow layer's albedo, its family's colour: within a fifth of plain snow, never above 1.</summary>
            public readonly Vector3 Tint;
            /// <summary>How softly the sun wraps the surface (0 crisp).</summary>
            public readonly float Soft;
            /// <summary>The shine: how much of the surface can shine, and how glassy it is (0 a broad glow, 1 near a mirror).</summary>
            public readonly float Sheen, Gloss;
            /// <summary>The share of the ground the shiny patches cover, and the faint mottle that survives from far away.</summary>
            public readonly float Patch, Mottle;
            /// <summary>The sparkle: its brightness, the share of crystals that catch the sun, and their size (m).</summary>
            public readonly float Sparkle, Density, Grain;
            /// <summary>How deep skis mark it: powder 1, ice nearly 0.</summary>
            public readonly float Tracks;
            /// <summary>New snow, pristine (softer, more sparkle) for its first FreshHours.</summary>
            public readonly bool Fresh;

            public Row(float r, float g, float b, float soft, float sheen, float gloss, float patch, float mottle,
                       float sparkle, float density, float grain, float tracks, bool fresh = false)
            {
                Tint = new Vector3(r, g, b); Soft = soft; Sheen = sheen; Gloss = gloss; Patch = patch; Mottle = mottle;
                Sparkle = sparkle; Density = density; Grain = grain; Tracks = tracks; Fresh = fresh;
            }
        }

        public static readonly Row[] Rows =
        {
            new Row(1.000f, 1.000f, 1.000f, 0.15f, 0.00f, 0.30f, 0.00f, 0.00f, 0.60f, 0.50f, 0.004f, 0.00f),   // none: the snow before the field is on
            new Row(1.000f, 1.000f, 1.000f, 0.25f, 0.00f, 0.20f, 0.00f, 0.00f, 1.00f, 0.90f, 0.003f, 1.00f, true),   // powder
            new Row(0.985f, 0.988f, 0.992f, 0.12f, 0.04f, 0.35f, 0.30f, 0.01f, 0.45f, 0.50f, 0.004f, 0.35f),   // packed powder
            new Row(0.975f, 0.980f, 0.988f, 0.10f, 0.05f, 0.35f, 0.40f, 0.01f, 0.35f, 0.40f, 0.004f, 0.30f),   // machine groomed
            new Row(0.950f, 0.961f, 0.980f, 0.10f, 0.30f, 0.50f, 0.45f, 0.02f, 0.15f, 0.20f, 0.004f, 0.15f),   // hard packed
            new Row(0.980f, 0.984f, 0.992f, 0.05f, 1.00f, 0.85f, 0.75f, 0.03f, 0.00f, 0.00f, 0.004f, 0.03f),   // icy
            new Row(0.917f, 0.902f, 0.883f, 0.15f, 0.00f, 0.20f, 0.00f, 0.03f, 1.30f, 0.45f, 0.030f, 0.50f),   // corn
            new Row(0.962f, 0.970f, 0.984f, 0.08f, 0.55f, 0.60f, 0.55f, 0.03f, 0.50f, 0.30f, 0.020f, 0.05f),   // frozen granular
            new Row(0.930f, 0.921f, 0.908f, 0.12f, 0.00f, 0.20f, 0.00f, 0.02f, 0.90f, 0.60f, 0.012f, 0.50f),   // loose granular
            new Row(0.950f, 0.948f, 0.944f, 0.10f, 0.30f, 0.70f, 1.00f, 0.04f, 0.00f, 0.00f, 0.004f, 0.50f),   // wet granular
            new Row(0.883f, 0.871f, 0.860f, 0.18f, 0.12f, 0.55f, 1.00f, 0.02f, 0.00f, 0.00f, 0.004f, 0.80f, true),   // wet powder
            new Row(0.990f, 0.995f, 1.000f, 0.10f, 0.03f, 0.35f, 0.30f, 0.00f, 0.35f, 0.60f, 0.002f, 0.80f),   // machine made
        };

        /// <summary>Older snow loses its sparkle and goes a little duller over this many face hours (Snow.hlsl's SnowLookAgeHours).</summary>
        public const float AgeHours = 96;
        /// <summary>New snow stays pristine this many face hours (Snow.hlsl's SnowFreshHours).</summary>
        public const float FreshHours = 12;
    }
}
