using System;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Persistence.Saves
{
    /// <summary>
    /// The design open in the game and where it saves (task P3-04): its <see cref="DesignHistory"/> (task 03; the tools
    /// submit commands to it), the save it came from, and whether anything changed since. Engine-free, so the save rules
    /// are tested in Tests/Core; the game passes in the time and its version. <see cref="Current"/> is the one open area's
    /// design, for the tools, the HUD and the terrain pipeline (task 05 fills <see cref="TerrainTiles"/> and
    /// <see cref="ClearingTiles"/>, and applies a loaded save's tiles).
    /// </summary>
    public sealed class OpenDesign
    {
        /// <summary>The open area's design, or null on the title or in a run without the game's flow.</summary>
        public static OpenDesign Current { get; set; }

        public string DataRoot { get; }
        public string PackageId { get; }
        public string HeightsSha256 { get; }
        public DesignHistory History { get; }

        /// <summary>The named save Ctrl+S writes to, or null until the first Save names one (SV1).</summary>
        public string SaveFolder { get; private set; }
        public string SaveName { get; private set; } = "";
        /// <summary>The tiles a loaded save brought, for task 05 to lay over the package's heights and forest.</summary>
        public IReadOnlyList<SaveTile> LoadedTerrain { get; private set; } = Array.Empty<SaveTile>();
        public IReadOnlyList<SaveTile> LoadedClearing { get; private set; } = Array.Empty<SaveTile>();
        /// <summary>Why the save had to come from its backup (shown once), or empty.</summary>
        public string Recovered { get; private set; } = "";

        /// <summary>Task 05's current height delta tiles; none until terrain edits exist.</summary>
        public Func<IReadOnlyList<SaveTile>> TerrainTiles { get; set; } = () => Array.Empty<SaveTile>();
        /// <summary>Task 05's current clearing tiles; none until clearing exists.</summary>
        public Func<IReadOnlyList<SaveTile>> ClearingTiles { get; set; } = () => Array.Empty<SaveTile>();

        DesignDocument _saved;
        bool _otherChanges;

        /// <summary>Raised when <see cref="Dirty"/> may have changed (an edit, a save), for the bar's save symbol.</summary>
        public event Action DirtyChanged;

        public OpenDesign(string dataRoot, string packageId, string heightsSha256, DesignDocument document)
        {
            DataRoot = dataRoot;
            PackageId = packageId;
            HeightsSha256 = heightsSha256 ?? "";
            History = new DesignHistory(document);
            _saved = document;
            History.Changed += _ => DirtyChanged?.Invoke();
        }

        /// <summary>
        /// A new design on an area (Load Area's New design, or an area with no saves): empty, with a seed keyed to the area
        /// so IDs stay deterministic (SH-04).
        /// </summary>
        public static OpenDesign New(string dataRoot, string packageId, string heightsSha256) =>
            new OpenDesign(dataRoot, packageId, heightsSha256, DesignDocument.Empty(SeedFor(packageId)));

        /// <summary>
        /// Opens a save. Throws <see cref="FormatTooNewException"/> for a newer save, and <see cref="InvalidDataException"/>
        /// for a damaged one or one made on other terrain, so the caller can say why and open nothing.
        /// </summary>
        public static OpenDesign Load(string dataRoot, string folder, string packageId, string heightsSha256)
        {
            var save = SaveStore.Read(folder);
            if (!string.Equals(save.PackageId, packageId, StringComparison.Ordinal))
                throw new InvalidDataException($"This save belongs to another area ({save.PackageId}).");
            if (!string.IsNullOrEmpty(heightsSha256) && !string.IsNullOrEmpty(save.HeightsSha256) && save.HeightsSha256 != heightsSha256)
                throw new InvalidDataException("This save was made on different terrain for this area, so its edits can't be laid over it.");
            var open = new OpenDesign(dataRoot, packageId, heightsSha256, save.Design)
            {
                LoadedTerrain = save.Terrain, LoadedClearing = save.Clearing, Recovered = save.Recovered,
            };
            if (!save.Autosave)   // opening the autosave slot leaves Ctrl+S to ask for a name (SV1)
            {
                open.SaveFolder = folder;
                open.SaveName = save.Name;
            }
            return open;
        }

        /// <summary>Anything changed since the last save or autosave: the bar's save symbol is white, leaving asks (SV2).</summary>
        public bool Dirty => !ReferenceEquals(History.Document, _saved) || _otherChanges;

        /// <summary>For changes outside the document (task 05's terrain edits): marks the design as needing a save.</summary>
        public void MarkChanged()
        {
            _otherChanges = true;
            DirtyChanged?.Invoke();
        }

        /// <summary>Whether Save can write straight away, or must ask for a name first.</summary>
        public bool HasName => SaveFolder != null;

        /// <summary>Ctrl+S once named: writes over this design's own save. False when it can't (a newer save, no name).</summary>
        public bool Save(string utc, string gameVersion)
        {
            if (SaveFolder == null) return false;
            return WriteTo(SaveFolder, SaveName, false, utc, gameVersion);
        }

        /// <summary>The first Save, or Save as: a new named save folder, which Ctrl+S then writes to.</summary>
        public bool SaveAs(string name, string utc, string gameVersion)
        {
            string folder = SaveStore.NewSaveFolder(DataRoot, PackageId);
            if (!WriteTo(folder, name, false, utc, gameVersion)) return false;
            SaveFolder = folder;
            SaveName = name;
            return true;
        }

        /// <summary>The area's one Autosave slot, only when something changed; it never touches a named save (SV1, SV2).</summary>
        public bool Autosave(string utc, string gameVersion)
        {
            if (!Dirty) return false;
            return WriteTo(SaveStore.AutosaveFolder(DataRoot, PackageId), "Autosave", true, utc, gameVersion);
        }

        bool WriteTo(string folder, string name, bool autosave, string utc, string gameVersion)
        {
            var doc = History.Document;
            var save = new SaveFile
            {
                Name = name, Autosave = autosave, SavedUtc = utc, GameVersion = gameVersion ?? "",
                PackageId = PackageId, HeightsSha256 = HeightsSha256, Design = doc,
                Terrain = new List<SaveTile>(TerrainTiles?.Invoke() ?? Array.Empty<SaveTile>()),
                Clearing = new List<SaveTile>(ClearingTiles?.Invoke() ?? Array.Empty<SaveTile>()),
            };
            if (!SaveStore.Write(folder, save)) return false;
            _saved = doc;
            _otherChanges = false;
            DirtyChanged?.Invoke();
            return true;
        }

        /// <summary>The design seed for an area: a keyed hash of its package id, so a new design always starts the same.</summary>
        public static ulong SeedFor(string packageId)
        {
            ulong h = 0xcbf29ce484222325UL;   // FNV-1a over the id's characters
            foreach (char c in packageId ?? "") { h ^= c; h *= 0x100000001b3UL; }
            return DesignKey.Hash(h, 0x5A7E, 0);
        }
    }
}
