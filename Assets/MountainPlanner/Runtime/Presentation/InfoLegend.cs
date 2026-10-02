using MountainPlanner.Domain.Measure;
using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The info layers' legends (task 12b): the same colours and breaks InfoLayers.hlsl paints with (an EditMode test
    /// reads the shader to keep them equal), and the words the HUD's legend card shows beside them, in the player's
    /// units (task 12b.2). Steepness is always a grade in percent.
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

        /// <summary>Slope angle: the trail-difficulty bands (<see cref="SlopeBands"/>), as a grade in percent.</summary>
        public static readonly float[] SlopeBreaksPercent = { (float)SlopeBands.BluePercent, (float)SlopeBands.BlackPercent, (float)SlopeBands.DoubleBlackPercent };
        public static readonly Color SlopeGreen = new Color(0.106f, 0.541f, 0.298f), SlopeBlue = new Color(0.110f, 0.369f, 0.753f),
                                     SlopeBlack = new Color(0.165f, 0.165f, 0.170f);

        public static readonly Entry[] SlopeAngle =
        {
            new Entry(SlopeGreen, SlopeBands.Names[0], "under 25%"),
            new Entry(SlopeBlue, SlopeBands.Names[1], "25–40%"),
            new Entry(SlopeBlack, SlopeBands.Names[2], "40–60%"),
            new Entry(SlopeBlack, SlopeBands.Names[3], "over 60%", hatched: true),
        };

        /// <summary>Exposure: N, NE, E, SE, S, SW, W, NW from true north, then flat ground (under 10%).</summary>
        public static readonly Color[] ExposureColours =
        {
            new Color(0.231f, 0.420f, 0.820f), new Color(0.180f, 0.620f, 0.690f), new Color(0.345f, 0.690f, 0.400f), new Color(0.835f, 0.745f, 0.255f),
            new Color(0.910f, 0.525f, 0.220f), new Color(0.808f, 0.325f, 0.255f), new Color(0.675f, 0.310f, 0.580f), new Color(0.420f, 0.345f, 0.780f),
        };
        public static readonly Color ExposureFlat = new Color(0.62f, 0.62f, 0.60f);
        public static readonly string[] ExposurePoints = SlopeBands.CompassPoints;

        /// <summary>Snow depth: the colours the shader blends between, at <see cref="UnitFormat.SnowDepthStops"/>.</summary>
        public static readonly Color[] SnowDepthColours =
        {
            new Color(0.545f, 0.455f, 0.345f), new Color(0.890f, 0.925f, 0.960f), new Color(0.600f, 0.765f, 0.910f),
            new Color(0.310f, 0.545f, 0.835f), new Color(0.180f, 0.290f, 0.640f), new Color(0.290f, 0.165f, 0.470f),
        };

        static readonly string[] DepthWords = { "Bare", "Thin", "", "", "", "Deep" };

        /// <summary>The snow-depth rows in the player's units: 0, 6 in, 1 ft 6 in, 3, 6 and 10 ft; or 0, 15, 50 cm, 1, 2, 3 m.</summary>
        public static Entry[] SnowDepth(UnitSystem units)
        {
            double[] stops = UnitFormat.SnowDepthStops(units);
            var rows = new Entry[stops.Length];
            for (int k = 0; k < stops.Length; k++)
                rows[k] = new Entry(SnowDepthColours[k], DepthWords[k], UnitFormat.SnowDepth(stops[k], units) + (k == stops.Length - 1 ? " +" : ""));
            return rows;
        }

        public const string SlopeTitle = "Slope angle", ExposureTitle = "Slope exposure", SnowDepthTitle = "Snow depth";
        public const string SlopeNote = "Grade: rise over run (100% is 45°).";
        public const string ExposureNote = "The way each slope faces, from true north. Flatter than 10% is grey.";
        public const string SnowDepthNote = "Natural snowpack: deeper up high, thinner on sunny south faces, wind-scoured ridges and under trees.";

        public static string ContoursNote(UnitSystem units) => units == UnitSystem.Imperial
            ? "Contours every 40 ft, labelled every 200 ft."
            : "Contours every 10 m, labelled every 50 m.";
    }
}
