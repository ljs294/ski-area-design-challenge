using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Flora
{
    /// <summary>
    /// The species map (0.3 §4.5, TR3): which tree model stands in for each FIA species code. Species
    /// with their own model map to it; the rest fall back to the nearest look-alike by genus, then by
    /// form (conifer or broadleaf). Model order matches tools/assets/trees/species.json (a test checks).
    /// </summary>
    public static class SpeciesMap
    {
        /// <summary>Model ids in species.json order; a model's index is its position here.</summary>
        public static readonly string[] Models =
        {
            "subalpine_fir", "engelmann_spruce", "douglas_fir", "lodgepole_pine", "mountain_hemlock",
            "quaking_aspen", "paper_birch", "yellow_birch", "sugar_maple", "red_maple", "american_beech",
            "pacific_silver_fir", "western_hemlock", "noble_fir", "krummholz",
        };

        public const int VariantsPerModel = 3;

        /// <summary>FIA species codes with their own model.</summary>
        static readonly Dictionary<int, string> Exact = new Dictionary<int, string>
        {
            [19] = "subalpine_fir", [93] = "engelmann_spruce", [202] = "douglas_fir", [108] = "lodgepole_pine",
            [264] = "mountain_hemlock", [746] = "quaking_aspen", [375] = "paper_birch", [371] = "yellow_birch",
            [318] = "sugar_maple", [316] = "red_maple", [531] = "american_beech",
            [11] = "pacific_silver_fir", [263] = "western_hemlock", [22] = "noble_fir",
        };

        public static IReadOnlyCollection<int> ModelledCodes => Exact.Keys;

        public static int IndexOf(string model) => Array.IndexOf(Models, model);

        /// <summary>The model for an FIA species code: its own, else a look-alike (never fails).</summary>
        public static int ModelFor(int spcd)
        {
            if (Exact.TryGetValue(spcd, out string exact)) return IndexOf(exact);
            return IndexOf(LookAlike(spcd));
        }

        public static bool IsModelled(int spcd) => Exact.ContainsKey(spcd);

        /// <summary>FIA code ranges by genus (FIA species list), to the closest silhouette we have.</summary>
        public static string LookAlike(int spcd)
        {
            if (spcd >= 10 && spcd <= 29) return "subalpine_fir";       // true firs (Abies)
            if (spcd >= 40 && spcd <= 69) return "subalpine_fir";       // cedars, junipers: narrow dark conifers
            if (spcd >= 70 && spcd <= 73) return "subalpine_fir";       // larches (bare in winter, later)
            if (spcd >= 90 && spcd <= 99) return "engelmann_spruce";    // spruces (Picea)
            if (spcd >= 100 && spcd <= 143) return "lodgepole_pine";    // pines (Pinus)
            if (spcd >= 200 && spcd <= 202) return "douglas_fir";       // Douglas-firs
            if (spcd >= 211 && spcd <= 212) return "douglas_fir";       // redwood, giant sequoia
            if (spcd >= 240 && spcd <= 299) return "mountain_hemlock";  // thujas, hemlocks and other conifers
            if (spcd < 300) return "douglas_fir";                       // any other conifer
            if (spcd >= 310 && spcd <= 323) return "sugar_maple";       // maples (Acer)
            if (spcd >= 370 && spcd <= 379) return "paper_birch";       // birches (Betula)
            if (spcd >= 530 && spcd <= 531) return "american_beech";    // beeches
            if (spcd >= 740 && spcd <= 752) return "quaking_aspen";     // cottonwoods and aspens (Populus)
            if (spcd >= 800 && spcd <= 899) return "sugar_maple";       // oaks: broad rounded crowns
            return "quaking_aspen";                                    // any other broadleaf
        }
    }
}
