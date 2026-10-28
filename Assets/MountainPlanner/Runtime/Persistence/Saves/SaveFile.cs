using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Persistence.Saves
{
    /// <summary>
    /// One save (task P3-04, 0.3 §5): the design document plus the sparse 64 m terrain edit tiles (heights and
    /// clearing) over the immutable package (GR-09, TE1). Only authored data is kept (SH-05); plans are saved with the design (CF-06). On disk
    /// it is <c>&lt;data&gt;/Saves/&lt;packageId&gt;/&lt;saveId&gt;/save.json</c> plus <c>tiles/</c> (<see cref="SaveStore"/>).
    /// </summary>
    public sealed class SaveFile
    {
        /// <summary>The frozen save format. A change bumps this, adds a step and a TestData/formats fixture.</summary>
        public const int CurrentVersion = 8;
        public const string FileName = "save.json";
        public const string BackupName = "save.json.bak";
        /// <summary>The edge of a terrain or clearing tile, in metres of the 1 m core grid (GR-09).</summary>
        public const int TileMetres = 64;

        public static readonly VersionedJson Migrations = new VersionedJson("save", "Version", CurrentVersion, UpToTwo, UpToThree, UpToFour, UpToFive, UpToSix, UpToSeven, UpToEight);

        /// <summary>
        /// v1 → v2 (task P3-09): the design's shape 1 → 2, where each lift gains its terminal levels (SK14). A v1 lift's
        /// levels aren't known (they lived in memory only), so they're null and derived from the ground when it opens, as v1 did.
        /// </summary>
        static void UpToTwo(JObject o)
        {
            if (!(o["Design"] is JObject design)) return;
            design["Shape"] = 2;
            if (design["Lifts"] is JArray lifts)
                foreach (var l in lifts)
                    if (l is JObject lift && lift["Levels"] == null) lift["Levels"] = JValue.CreateNull();
        }

        /// <summary>
        /// v2 → v3 (task P3-09, LT44): the design's shape 2 → 3, where each tower gains the kind of head the player chose. A v2
        /// tower's head was always the game's choice, so it's "auto".
        /// </summary>
        static void UpToThree(JObject o)
        {
            if (!(o["Design"] is JObject design)) return;
            design["Shape"] = 3;
            if (design["Lifts"] is JArray lifts)
                foreach (var l in lifts)
                    if (l is JObject lift && lift["Towers"] is JArray towers)
                        foreach (var t in towers)
                            if (t is JObject tower && tower["Kind"] == null) tower["Kind"] = "auto";
        }

        /// <summary>
        /// v3 → v4 (task P3-15, TB-14): the design's shape 3 → 4, which adds the confirmed terrain brush strokes. A v3 design
        /// had none.
        /// </summary>
        static void UpToFour(JObject o)
        {
            if (!(o["Design"] is JObject design)) return;
            design["Shape"] = 4;
            if (design["Strokes"] == null) design["Strokes"] = new JArray();
        }

        /// <summary>
        /// v4 → v5 (task P3-10, NW-15): the design's shape 4 → 5, which holds the Areas the player has named. A v4 save has
        /// named none: its hubs go by their default names, as they did.
        /// </summary>
        static void UpToFive(JObject o)
        {
            if (!(o["Design"] is JObject design)) return;
            design["Shape"] = 5;
            if (design["Areas"] == null) design["Areas"] = new JArray();
        }

        /// <summary>
        /// v5 → v6 (task P3-10, owner NT24): the design's shape 5 → 6, which holds the trails made of two or more sections.
        /// A v5 save has none: every trail is one section, a trail on its own, exactly as it was.
        /// </summary>
        static void UpToSix(JObject o)
        {
            if (!(o["Design"] is JObject design)) return;
            design["Shape"] = 6;
            if (design["TrailLines"] == null) design["TrailLines"] = new JArray();
        }

        /// <summary>
        /// v6 → v7 (task P3-10, owner NT43–NT46): the design's shape 6 → 7, where a shaped trail holds spans of runs between
        /// nodes, and the player's cut points are kept. A v6 trail's sections were whole runs, so each becomes the span from
        /// its head to its tail; there were no cut points.
        /// </summary>
        static void UpToSeven(JObject o)
        {
            if (!(o["Design"] is JObject design)) return;
            design["Shape"] = 7;
            if (design["TrailLines"] is JArray lines)
                foreach (var l in lines)
                {
                    if (!(l is JObject line) || !(line["Sections"] is JArray sections)) continue;
                    var spans = new JArray();
                    foreach (var s in sections)
                        spans.Add(new JObject
                        {
                            ["Piece"] = s.DeepClone(),
                            ["From"] = new JObject { ["Kind"] = "head" },
                            ["To"] = new JObject { ["Kind"] = "tail" },
                            ["FromMm"] = 0,
                            ["ToMm"] = 0,
                        });
                    line.Remove("Sections");
                    line["Spans"] = spans;
                }
            if (design["Cuts"] == null) design["Cuts"] = new JArray();
        }

        /// <summary>
        /// v7 → v8 (task P3-10, sections finding 7): the design's shape 7 → 8, where a trail keeps its clearing's outline
        /// instead of the strokes it was painted with. Each trail's parts are read as v7 did (damage refuses the save),
        /// rasterised once to the clearing's 250 mm cells (<see cref="TrailShape.FromParts"/>) and written as rings; the
        /// conversion is frozen with the format (a pinned hash in the tests).
        /// </summary>
        static void UpToEight(JObject o)
        {
            if (!(o["Design"] is JObject design)) return;
            design["Shape"] = 8;
            if (!(design["Trails"] is JArray trails)) return;
            foreach (var t in trails)
            {
                if (!(t is JObject trail) || trail["Outline"] != null) continue;
                var parts = SaveJson.ReadParts(trail);
                if (DesignRules.PartsProblem(parts) is string problem) throw new FormatException(problem);
                trail["Outline"] = SaveJson.OutlineJson(TrailShape.FromParts(parts));
                trail.Remove("Parts");
            }
        }

        /// <summary>The player's name for it ("Autosave" for the autosave slot).</summary>
        public string Name { get; set; } = "";
        /// <summary>The area's one autosave slot (SV1); it never replaces a named save.</summary>
        public bool Autosave { get; set; }
        /// <summary>When it was saved, ISO 8601 UTC, from the caller: Persistence never reads the clock.</summary>
        public string SavedUtc { get; set; } = "";
        /// <summary>The game that wrote it, for error reports only.</summary>
        public string GameVersion { get; set; } = "";
        /// <summary>The package the tiles sit over; a save never opens on another area.</summary>
        public string PackageId { get; set; } = "";
        /// <summary>The package's core heights hash (its manifest layer), so the deltas never land on other terrain.</summary>
        public string HeightsSha256 { get; set; } = "";
        public DesignDocument Design { get; set; } = DesignDocument.Empty(0);
        /// <summary>The committed terrain edits over the package (task 05's 64 m tiles, TE1): heights and clearing.</summary>
        public TerrainEdits Edits { get; set; } = TerrainEdits.Empty;

        /// <summary>
        /// The format version the file had when read (<see cref="CurrentVersion"/> for a new save). Never written. An older
        /// save's built trails are graded once more when it opens (save v8, sections finding 7).
        /// </summary>
        public int MigratedFrom { get; internal set; } = CurrentVersion;
        /// <summary>Set by a read that had to use save.json.bak: why save.json couldn't be read. Never written.</summary>
        public string Recovered { get; internal set; } = "";
    }

    /// <summary>What Load Area shows for a save, read from save.json without its tiles.</summary>
    public sealed class SaveSummary
    {
        public string Folder { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Autosave { get; set; }
        public string SavedUtc { get; set; } = "";
        public int Trails { get; set; }
        public int Lifts { get; set; }
        /// <summary>Why it can't be opened (a newer version wrote it), or empty when it can.</summary>
        public string Refusal { get; set; } = "";
        /// <summary>True when save.json was unreadable and these come from save.json.bak.</summary>
        public bool FromBackup { get; set; }
    }
}
