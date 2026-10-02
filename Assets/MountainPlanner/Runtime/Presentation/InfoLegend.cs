using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The info layers' legends (task 12b): the same colours and breaks InfoLayers.hlsl paints with (an EditMode test
    /// reads the shader to keep them equal), and the words the HUD's legend card shows beside them.
    /// </summary>
    public static class InfoLegend
    {
        public readonly struct Entry
        {
            public readonly Color Colour;
            public readonly string Label, Figure;
            /// <summary>White hatching over the swatch (double black).</summary>
            public readonly bool Hatched;

            public Entry(Color colour, string label, string figure, bool hatched = false)
            {
                Colour = colour;
                Label = label;
                Figure = figure;
                Hatched = hatched;
            }
        }

        /// <summary>Slope angle: the trail-difficulty bands (game-ui-direction.md), in degrees and as a grade.</summary>
        public static readonly float[] SlopeBreaksDegrees = { 14, 22, 30 };
        public static readonly Color SlopeGreen = new Color(0.106f, 0.541f, 0.298f), SlopeBlue = new Color(0.110f, 0.369f, 0.753f),
                                     SlopeBlack = new Color(0.165f, 0.165f, 0.170f);

        public static readonly Entry[] SlopeAngle =
        {
            new Entry(SlopeGreen, "Easiest", "under 14°"),
            new Entry(SlopeBlue, "More difficult", "14–22°"),
            new Entry(SlopeBlack, "Most difficult", "22–30°"),
            new Entry(SlopeBlack, "Experts only", "over 30°", hatched: true),
        };

        /// <summary>Exposure: N, NE, E, SE, S, SW, W, NW from true north, then flat ground (under 5°).</summary>
        public static readonly Color[] ExposureColours =
        {
            new Color(0.231f, 0.420f, 0.820f), new Color(0.180f, 0.620f, 0.690f), new Color(0.345f, 0.690f, 0.400f), new Color(0.835f, 0.745f, 0.255f),
            new Color(0.910f, 0.525f, 0.220f), new Color(0.808f, 0.325f, 0.255f), new Color(0.675f, 0.310f, 0.580f), new Color(0.420f, 0.345f, 0.780f),
        };
        public static readonly Color ExposureFlat = new Color(0.62f, 0.62f, 0.60f);
        public static readonly string[] ExposurePoints = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        public const float ExposureFlatDegrees = 5;

        /// <summary>Snow depth: colour stops (metres) the shader blends between.</summary>
        public static readonly float[] SnowDepthStops = { 0, 0.15f, 0.5f, 1, 2, 3 };
        public static readonly Color[] SnowDepthColours =
        {
            new Color(0.545f, 0.455f, 0.345f), new Color(0.890f, 0.925f, 0.960f), new Color(0.600f, 0.765f, 0.910f),
            new Color(0.310f, 0.545f, 0.835f), new Color(0.180f, 0.290f, 0.640f), new Color(0.290f, 0.165f, 0.470f),
        };

        public static readonly Entry[] SnowDepth =
        {
            new Entry(SnowDepthColours[0], "Bare", "0 cm"),
            new Entry(SnowDepthColours[1], "Thin", "15 cm"),
            new Entry(SnowDepthColours[2], "", "50 cm"),
            new Entry(SnowDepthColours[3], "", "1 m"),
            new Entry(SnowDepthColours[4], "", "2 m"),
            new Entry(SnowDepthColours[5], "Deep", "3 m +"),
        };

        public const string SlopeTitle = "Slope angle", ExposureTitle = "Slope exposure", SnowDepthTitle = "Snow depth";
        public const string SlopeNote = "As a grade: 14° is 25%, 22° is 40%, 30° is 58%.";
        public const string ExposureNote = "The way each slope faces, from true north. Flatter than 5° is grey.";
        public const string SnowDepthNote = "Natural snowpack: deeper up high, thinner on sunny south faces, wind-scoured ridges and under trees.";
        public const string ContoursNote = "Contours every 10 m, heavier every 50 m.";
    }
}
