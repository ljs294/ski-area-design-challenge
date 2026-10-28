using System;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Persistence.Saves
{
    /// <summary>
    /// The design open in the game and where it saves (task P3-04): its <see cref="DesignHistory"/> (task 03; the tools
    /// submit commands to it), the save it came from, and whether anything changed since. Engine-free, so the save rules
    /// are tested in Tests/Core; the game passes in the time and its version. <see cref="Current"/> is the one open area's
    /// design, for the tools, the HUD and the terrain pipeline. Its committed terrain edits live in the history's steps
    /// (task 05, TE2): a loaded save's edits are the history's first, undo and redo move them with the document, and a
    /// save writes <see cref="DesignHistory.Terrain"/>, so nothing keeps a second copy.
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
        /// <summary>The terrain edits a loaded save brought; the history starts from them (<see cref="DesignHistory.Terrain"/>).</summary>
        public TerrainEdits LoadedEdits { get; private set; } = TerrainEdits.Empty;
        /// <summary>Why the save had to come from its backup (shown once), or empty.</summary>
        public string Recovered { get; private set; } = "";

        /// <summary>
        /// The committed terrain edits (heights and clearing) a save writes: the history's current step's (TE2). A test
        /// may stand another source in.
        /// </summary>
        public Func<TerrainEdits> Edits { get; set; }

        /// <summary>Passed to each write's steps (a test or -crashsave stops a save part way).</summary>
        public Action<SaveStep> Step { get; set; }

        DesignDocument _saved;
        TerrainEdits _savedTerrain;
        bool _otherChanges;

        /// <summary>Raised when <see cref="Dirty"/> may have changed (an edit, a save), for the bar's save symbol.</summary>
        public event Action DirtyChanged;

        public OpenDesign(string dataRoot, string packageId, string heightsSha256, DesignDocument document, TerrainEdits edits = null)
        {
            DataRoot = dataRoot;
            PackageId = packageId;
            HeightsSha256 = heightsSha256 ?? "";
            History = new DesignHistory(document);
            if (edits != null && !edits.IsEmpty) History.Load(document, edits);   // the save's edits start the history (TE2)
            Edits = () => History.Terrain;
            _saved = History.Document;
            _savedTerrain = History.Terrain;
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
            var open = new OpenDesign(dataRoot, packageId, heightsSha256, save.Design, save.Edits)
            {
                LoadedEdits = save.Edits, Recovered = save.Recovered, MigratedFrom = save.MigratedFrom,
            };
            if (!save.Autosave)   // opening the autosave slot leaves Ctrl+S to ask for a name (SV1)
            {
                open.SaveFolder = folder;
                open.SaveName = save.Name;
            }
            return open;
        }

        /// <summary>The format version the save had when read (<see cref="SaveFile.CurrentVersion"/> for a new design).</summary>
        public int MigratedFrom { get; private set; } = SaveFile.CurrentVersion;

        /// <summary>
        /// An older save (before v8, sections finding 7) whose built work should be graded once more as this build grades it:
        /// its trails were graded from strokes, and now from their clearings' cells.
        /// </summary>
        public bool NeedsRegrade => MigratedFrom < 8 && !History.Terrain.IsEmpty && !_regraded;
        bool _regraded;

        /// <summary>
        /// Takes the regraded edits (<see cref="NeedsRegrade"/>) in place of the loaded ones, in memory only: the history
        /// starts from them, and the design stays clean, so nothing (an autosave included) writes the file until the
        /// player saves. Opening the save again regrades it again, the same way.
        /// </summary>
        public void Regraded(TerrainEdits edits)
        {
            if (edits == null) throw new ArgumentNullException(nameof(edits));
            _regraded = true;
            History.Load(History.Document, edits);
            _savedTerrain = History.Terrain;
            DirtyChanged?.Invoke();
        }

        /// <summary>Anything changed since the last save or autosave: the bar's save symbol is white, leaving asks (SV2).</summary>
        public bool Dirty => !ReferenceEquals(History.Document, _saved) || !ReferenceEquals(History.Terrain, _savedTerrain) || _otherChanges;

        /// <summary>For changes outside the history: marks the design as needing a save (terrain edits in the history already count).</summary>
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
                Edits = Edits?.Invoke() ?? History.Terrain,
            };
            var terrain = History.Terrain;
            if (!SaveStore.Write(folder, save, Step)) return false;
            _saved = doc;
            _savedTerrain = terrain;
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
