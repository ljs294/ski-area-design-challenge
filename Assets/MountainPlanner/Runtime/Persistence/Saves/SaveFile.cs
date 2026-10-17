using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Persistence.Saves
{
    /// <summary>
    /// One save (task P3-04, 0.3 §5): the design document plus the sparse 64 m terrain and clearing tiles over the
    /// immutable package (GR-09). Only authored data is kept (SH-05); plans are saved with the design (CF-06). On disk
    /// it is <c>&lt;data&gt;/Saves/&lt;packageId&gt;/&lt;saveId&gt;/save.json</c> plus <c>tiles/</c> (<see cref="SaveStore"/>).
    /// </summary>
    public sealed class SaveFile
    {
        /// <summary>The frozen save format. A change bumps this, adds a step and a TestData/formats fixture.</summary>
        public const int CurrentVersion = 1;
        public const string FileName = "save.json";
        public const string BackupName = "save.json.bak";
        /// <summary>The edge of a terrain or clearing tile, in metres of the 1 m core grid (GR-09).</summary>
        public const int TileMetres = 64;

        public static readonly VersionedJson Migrations = new VersionedJson("save", "Version", CurrentVersion);

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
        /// <summary>Height delta tiles, sorted by (Y, X) when written.</summary>
        public List<SaveTile> Terrain { get; set; } = new List<SaveTile>();
        /// <summary>Clearing mask tiles, sorted by (Y, X) when written.</summary>
        public List<SaveTile> Clearing { get; set; } = new List<SaveTile>();

        /// <summary>Set by a read that had to use save.json.bak: why save.json couldn't be read. Never written.</summary>
        public string Recovered { get; internal set; } = "";
    }

    /// <summary>The two kinds of tile a save holds; the number is written in each tile file.</summary>
    public enum SaveTileKind : byte { Terrain = 1, Clearing = 2 }

    /// <summary>
    /// One 64 m tile: its column and row in the core grid (tile x covers metres 64x to 64x + 63) and its cells as task 05's
    /// tile type encodes them (SV3). The save keeps the bytes exactly, so the heights come back byte for byte.
    /// </summary>
    public sealed class SaveTile
    {
        public int X { get; }
        public int Y { get; }
        public byte[] Payload { get; }

        public SaveTile(int x, int y, byte[] payload)
        {
            X = x;
            Y = y;
            Payload = payload ?? new byte[0];
        }
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
