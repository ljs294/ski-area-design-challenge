using System.Collections.Generic;
using MountainPlanner.UI.Tools;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Task 4.14 (LK-10, owner 2026-11-17: "all the data fields consistent"): the one layout every standing-water card
    /// uses, lake, pond and reservoir alike: the same labels in the same order, each in the same format. A row that doesn't
    /// apply is left out (a lake has no Starts and no Earthwork), never renamed or moved. The dam's own rows follow.
    /// </summary>
    public static class WaterCardRows
    {
        public const string WaterLevel = "Water level", FullPool = "Full pool", PercentFull = "Percent full", Area = "Area", Depth = "Depth", Volume = "Volume", Fills = "Fills",
                            Starts = "Starts", Snowmaking = "Snowmaking", Earthwork = "Earthwork";

        /// <summary>The order, for tests and for anyone adding a row.</summary>
        public static readonly string[] Order = { WaterLevel, FullPool, PercentFull, Area, Depth, Volume, Fills, Starts, Snowmaking, Earthwork };

        /// <summary>One standing water's figures; NaN (or null text) leaves its row out.</summary>
        public struct Standing
        {
            public double LevelMetres;        // where the water stands now
            public string LevelNote;          // "empty", say
            public double FullPoolMetres;     // the highest it fills (a lake's natural level)
            public string FullPoolNote;
            public double PercentFull;        // stored over capacity, 0–100
            public double AreaSquareMetres;
            public double MeanDepthMetres, MaxDepthMetres;
            public string DepthNote;          // "estimate" or "yours" (a lake's), else null
            public double VolumeCubicMetres;
            public string Fills;
            public bool? StartsEmpty;         // null: no Starts row (a lake)
            public string Snowmaking;
            public double CutCubicMetres, FillCubicMetres;   // NaN: no Earthwork row
        }

        public static void Add(List<(string, string)> rows, in Standing s)
        {
            if (!double.IsNaN(s.LevelMetres)) rows.Add((WaterLevel, ToolFormat.Elevation(s.LevelMetres) + Note(s.LevelNote)));
            if (!double.IsNaN(s.FullPoolMetres)) rows.Add((FullPool, ToolFormat.Elevation(s.FullPoolMetres) + Note(s.FullPoolNote)));
            if (!double.IsNaN(s.PercentFull)) rows.Add((PercentFull, System.Math.Round(s.PercentFull).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%"));
            if (!double.IsNaN(s.AreaSquareMetres)) rows.Add((Area, ToolFormat.Area(s.AreaSquareMetres)));
            if (!double.IsNaN(s.MeanDepthMetres))
            {
                string depth = "mean " + ToolFormat.Depth(s.MeanDepthMetres);
                if (!double.IsNaN(s.MaxDepthMetres)) depth += " · deepest " + ToolFormat.Depth(s.MaxDepthMetres);
                if (!string.IsNullOrEmpty(s.DepthNote)) depth += " · " + s.DepthNote;
                rows.Add((Depth, depth));
            }
            if (!double.IsNaN(s.VolumeCubicMetres)) rows.Add((Volume, $"{ToolFormat.Water(s.VolumeCubicMetres)} · {ToolFormat.Gallons(s.VolumeCubicMetres)}"));
            if (!string.IsNullOrEmpty(s.Fills)) rows.Add((Fills, s.Fills));
            if (s.StartsEmpty.HasValue) rows.Add((Starts, s.StartsEmpty.Value ? "empty" : "full"));
            if (!string.IsNullOrEmpty(s.Snowmaking)) rows.Add((Snowmaking, s.Snowmaking));
            if (!double.IsNaN(s.CutCubicMetres) && !double.IsNaN(s.FillCubicMetres))
                rows.Add((Earthwork, $"cut {ToolFormat.Volume(s.CutCubicMetres)} · fill {ToolFormat.Volume(s.FillCubicMetres)}"));
        }

        static string Note(string note) => string.IsNullOrEmpty(note) ? "" : " · " + note;

        /// <summary>
        /// Every card's hint in one order: a warning first, then how to rename, then anything the card adds, then the map's
        /// credit.
        /// </summary>
        public static string Hint(string warning, string extra, bool mapCredit, string credited = "Name")
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(warning)) parts.Add(warning);
            parts.Add("Click the name to rename it.");
            if (!string.IsNullOrEmpty(extra)) parts.Add(extra);
            if (mapCredit) parts.Add(credited + " © OpenStreetMap contributors.");
            return string.Join(" ", parts);
        }

        /// <summary>A fresh Standing with every figure left out, to fill in.</summary>
        public static Standing Empty() => new Standing
        {
            LevelMetres = double.NaN, FullPoolMetres = double.NaN, PercentFull = double.NaN, AreaSquareMetres = double.NaN, MeanDepthMetres = double.NaN, MaxDepthMetres = double.NaN,
            VolumeCubicMetres = double.NaN, CutCubicMetres = double.NaN, FillCubicMetres = double.NaN,
        };
    }
}
