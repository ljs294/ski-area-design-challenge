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

        // ---------- Snow conditions (task 4.07 PR A2, info view 9) ----------

        /// <summary>
        /// Each snow type's colour (indexed by the simulation's SnowType; Snow.hlsl's SnowTypeColours, the same numbers): four
        /// families, fresh blue, groomed and packed teal, hard and icy violet, spring amber, lighter and darker inside each.
        /// </summary>
        public static readonly Color[] SnowTypeColours =
        {
            new Color(0.545f, 0.455f, 0.345f),   // bare
            new Color(0.353f, 0.635f, 0.925f),   // powder
            new Color(0.169f, 0.710f, 0.522f),   // packed powder
            new Color(0.086f, 0.541f, 0.369f),   // machine groomed
            new Color(0.718f, 0.478f, 0.788f),   // hard packed
            new Color(0.431f, 0.184f, 0.533f),   // icy
            new Color(0.961f, 0.753f, 0.290f),   // corn
            new Color(0.604f, 0.310f, 0.690f),   // frozen granular
            new Color(0.929f, 0.631f, 0.000f),   // loose granular
            new Color(0.769f, 0.498f, 0.000f),   // wet granular
            new Color(0.173f, 0.455f, 0.776f),   // wet powder
        };

        /// <summary>The snow types' names, as the readout says them (indexed as <see cref="SnowTypeColours"/>).</summary>
        public static readonly string[] SnowTypeNames =
        {
            "Bare", "Powder", "Packed powder", "Machine groomed", "Hard packed", "Icy", "Corn", "Frozen granular", "Loose granular",
            "Wet granular", "Wet powder",
        };

        /// <summary>
        /// Under this depth the Snow conditions readout says "Bare" (with "traces of" the type): the ground's snow covers fully
        /// from <see cref="SurfaceStates.FullCoverMetres"/>, and the layer fades each type to bare by the same cover, so below
        /// half of it the point reads as bare ground (owner's demo 111, 2026-11-15).
        /// </summary>
        public const float SnowBareBelowMetres = SurfaceStates.FullCoverMetres / 2;

        /// <summary>True when a point with snow of <paramref name="type"/> (SnowType, 0 bare) <paramref name="metres"/> deep reads as
        /// bare ground with traces of it.</summary>
        public static bool SnowReadsBare(int type, float metres) => type > 0 && metres < SnowBareBelowMetres;

        /// <summary>The legend's rows: the types the natural snow makes (grooming and skiing bring the rest, task 4.07 PR B), by
        /// family, with the family's name as the figure on its first row.</summary>
        public static readonly Entry[] SnowConditions =
        {
            new Entry(SnowTypeColours[1], "Powder", "Fresh"),
            new Entry(SnowTypeColours[10], "Wet powder", ""),
            new Entry(SnowTypeColours[2], "Packed powder", "Packed"),
            new Entry(SnowTypeColours[7], "Frozen granular", "Hard"),
            new Entry(SnowTypeColours[5], "Icy", ""),
            new Entry(SnowTypeColours[6], "Corn", "Spring"),
            new Entry(SnowTypeColours[9], "Wet granular", ""),
            new Entry(SnowTypeColours[0], "Bare", ""),
        };

        static readonly string[] QualityWords = { "Poor", "Fair", "Good", "Excellent" };

        /// <summary>The readout's word for a snow quality (‰): under 400 Poor, then Fair, Good, and Excellent from 800.</summary>
        public static int SnowQualityStep(int permille) => permille >= 800 ? 3 : permille >= 600 ? 2 : permille >= 400 ? 1 : 0;
        public static string SnowQualityWord(int permille) => QualityWords[SnowQualityStep(permille)];

        /// <summary>The surface's age in the player's days (face hours ÷ 24; the simulation counts to 255 hours).</summary>
        public static string SnowAge(int faceHours) =>
            faceHours >= 255 ? "10+ days old" : faceHours < 24 ? "under a day old" : faceHours < 48 ? "1 day old" : faceHours / 24 + " days old";

        public const string SnowConditionsTitle = "Snow conditions";
        public const string SnowConditionsNote = "The surface the snow has now. Point at the ground for its depth, quality and age.";

        // ---------- Traffic (SN-31, task 4.13 PR C) ----------

        /// <summary>Traffic's five steps, quiet to packed: cool to hot, bright enough to read on the dark relief.</summary>
        public static readonly Color[] TrafficColours =
        {
            new Color(0.290f, 0.420f, 0.600f), new Color(0.380f, 0.690f, 0.560f), new Color(0.920f, 0.800f, 0.300f),
            new Color(0.950f, 0.520f, 0.180f), new Color(0.880f, 0.200f, 0.170f),
        };
        public static readonly string[] TrafficWords = { "Quiet", "Light", "Busy", "Crowded", "Packed" };

        /// <summary>A run's step: skiers an hour per 100 ft of its width at or over each (none below the first is Quiet).</summary>
        public static readonly int[] RunBreaks = { 15, 40, 80, 150 };
        /// <summary>A lift's step: how full its chairs ran over the hour, in percent of what it can carry, at or over each.</summary>
        public static readonly int[] LoadBreaksPercent = { 25, 50, 75, 90 };

        /// <summary>The step (0–4) a value falls in, against four rising breaks.</summary>
        public static int Step(float value, int[] breaks)
        {
            int k = 0;
            while (k < breaks.Length && value >= breaks[k]) k++;
            return k;
        }

        /// <summary>The runs' rows: the words and the skiers an hour per 100 ft of width (30 m in metric).</summary>
        public static Entry[] TrafficRuns => Rows(RunBreaks, "");
        /// <summary>The lifts' rows: how full the chairs ran.</summary>
        public static Entry[] TrafficLifts => Rows(LoadBreaksPercent, "%");

        static Entry[] Rows(int[] breaks, string unit)
        {
            var rows = new Entry[TrafficColours.Length];
            for (int k = 0; k < rows.Length; k++)
            {
                string figure = k == 0 ? "under " + breaks[0] + unit
                              : k == rows.Length - 1 ? breaks[k - 1] + unit + "+"
                              : breaks[k - 1] + "–" + breaks[k] + unit;
                rows[k] = new Entry(TrafficColours[k], TrafficWords[k], figure);
            }
            return rows;
        }

        public const string TrafficTitle = "Traffic";
        public static string TrafficRunsHead(UnitSystem units) =>
            units == UnitSystem.Imperial ? "Runs: skiers an hour per 100 ft of width" : "Runs: skiers an hour per 30 m of width";
        public const string TrafficLiftsHead = "Lifts: how full the chairs run";
        public const string TrafficNote = "A circle at a lift's bottom grows with its line: the line and the wait beside it.";

        public const string SlopeTitle = "Slope angle", ExposureTitle = "Slope exposure", SnowDepthTitle = "Snow depth";
        public const string SlopeNote = "Grade: rise over run (100% is 45°).";
        public const string ExposureNote = "The way each slope faces, from true north; flatter than 10% is grey.";
        public const string SnowDepthNote = "The snow on the ground: it builds storm by storm, blows off ridges into hollows, settles and melts.";

        public static string ContoursNote(UnitSystem units) => units == UnitSystem.Imperial
            ? "Contours every 40 ft, down to 10 ft up close; labelled every 200 ft."
            : "Contours every 10 m, down to 2.5 m up close; labelled every 50 m.";
    }
}
