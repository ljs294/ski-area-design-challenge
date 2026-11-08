using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Domain.Edits;
using Newtonsoft.Json;

namespace MountainPlanner.Persistence.Saves
{
    /// <summary>The points a save passes; a test (or -crashsave) stops at one to prove the last good save survives.</summary>
    public enum SaveStep { TilesWritten, TempWritten, Committed, Swept }

    /// <summary>
    /// Saves on disk (task P3-04): <c>&lt;data&gt;/Saves/&lt;packageId&gt;/&lt;saveId&gt;/</c>, outside the package, so the read-only
    /// demo saves like any download and an area's rename never touches them. Each save folder holds:
    /// <code>
    /// save.json       the save (SaveJson, frozen v1)
    /// save.json.bak   the previous good save.json
    /// tiles/          &lt;sha256&gt;.tile files, never overwritten
    /// </code>
    /// A save commits with one <see cref="File.Replace(string, string, string)"/> of save.json, so a crash at any point
    /// leaves the old save or the new one, never half of one.
    /// </summary>
    public static class SaveStore
    {
        public const string FolderName = "Saves";
        public const string TilesFolder = "tiles";
        /// <summary>SI-12 v2: the simulation's sidecars, &lt;sha256&gt;.sim, never overwritten (as tiles/).</summary>
        public const string SimFolder = "sim";
        public const string SimExtension = ".sim";
        public const string AutosaveId = "autosave";
        const string NamedPrefix = "save-";

        public static string Root(string dataRoot) => Path.Combine(dataRoot, FolderName);
        public static string AreaFolder(string dataRoot, string packageId) => Path.Combine(Root(dataRoot), packageId);
        public static string AutosaveFolder(string dataRoot, string packageId) => Path.Combine(AreaFolder(dataRoot, packageId), AutosaveId);

        /// <summary>A new, unused folder for a named save: save-001, save-002, … (folder names never change on rename).</summary>
        public static string NewSaveFolder(string dataRoot, string packageId)
        {
            string area = AreaFolder(dataRoot, packageId);
            for (int n = 1; ; n++)
            {
                string folder = Path.Combine(area, NamedPrefix + n.ToString("000", CultureInfo.InvariantCulture));
                if (!Directory.Exists(folder)) return folder;
            }
        }

        // ---------- writing ----------

        /// <summary>
        /// Writes <paramref name="save"/> into <paramref name="folder"/> atomically:
        /// 1. each new tile to a temporary file, renamed into tiles/ (tiles already there are skipped);
        /// 2. save.json.tmp, flushed to the disk;
        /// 3. File.Replace over save.json, keeping save.json.bak;
        /// 4. tiles neither save.json nor the backup lists are deleted.
        /// False, with nothing written, when save.json is from a newer version of the game (0.3 §5 rule 3).
        /// </summary>
        public static bool Write(string folder, SaveFile save, Action<SaveStep> step = null)
        {
            string path = Path.Combine(folder, SaveFile.FileName);
            if (SaveFile.Migrations.IsNewer(path)) return false;
            string tiles = Path.Combine(folder, TilesFolder);
            Directory.CreateDirectory(tiles);

            var edits = PutTiles(tiles, save.Edits ?? TerrainEdits.Empty);
            save.Sim = PutSim(folder, save.SimState);
            step?.Invoke(SaveStep.TilesWritten);

            string temp = path + ".tmp";
            WriteFlushed(temp, Encoding.UTF8.GetBytes(SaveJson.Write(save, edits)));
            step?.Invoke(SaveStep.TempWritten);

            Commit(temp, path, Path.Combine(folder, SaveFile.BackupName));
            step?.Invoke(SaveStep.Committed);

            Sweep(folder);
            step?.Invoke(SaveStep.Swept);
            return true;
        }

        static List<SaveJson.TileRef> PutTiles(string tiles, TerrainEdits edits)
        {
            var refs = new List<SaveJson.TileRef>();
            foreach (var t in edits.Tiles)   // in key order (row, then column), never empty, never twice (TerrainEdits)
            {
                byte[] bytes = SaveTileFile.Encode(t);
                string hash = SaveTileFile.Hash(bytes);
                string file = Path.Combine(tiles, hash + SaveTileFile.Extension);
                if (!File.Exists(file))
                {
                    string temp = file + ".tmp";
                    WriteFlushed(temp, bytes);
                    File.Move(temp, file);
                }
                refs.Add(new SaveJson.TileRef(t.Key.Column, t.Key.Row, hash));
            }
            return refs;
        }

        /// <summary>The sidecar into sim/ under its own hash (once: a file already there is the same bytes), or null for none.</summary>
        static SimSidecarRef? PutSim(string folder, SimSidecarBytes sim)
        {
            if (sim == null) return null;
            string hash = SaveTileFile.Hash(sim.Bytes);
            string dir = Path.Combine(folder, SimFolder);
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, hash + SimExtension);
            if (!File.Exists(file))
            {
                string temp = file + ".tmp";
                WriteFlushed(temp, sim.Bytes);
                File.Move(temp, file);
            }
            return new SimSidecarRef(sim.SidecarVersion, hash);
        }

        /// <summary>Writes a whole file and flushes it through the OS cache, so a power cut can't leave it half on disk.</summary>
        internal static void WriteFlushed(string path, byte[] bytes)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
        }

        /// <summary>
        /// Moves <paramref name="temp"/> over <paramref name="path"/> in one step (NTFS replaces the name atomically), keeping
        /// the old file as <paramref name="backup"/> when given. There is never a moment without a file at <paramref name="path"/>.
        /// </summary>
        internal static void Commit(string temp, string path, string backup)
        {
            if (File.Exists(path)) File.Replace(temp, path, backup, true);
            else File.Move(temp, path);
        }

        /// <summary>Deletes tiles and temporary files no good save.json in the folder refers to.</summary>
        static void Sweep(string folder)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);
            var keepSim = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in new[] { SaveFile.FileName, SaveFile.BackupName })
            {
                var (tiles, sim) = Listed(Path.Combine(folder, name));
                foreach (string hash in tiles) keep.Add(hash);
                if (sim != null) keepSim.Add(sim);
            }
            SweepFolder(Path.Combine(folder, TilesFolder), SaveTileFile.Extension, keep);
            SweepFolder(Path.Combine(folder, SimFolder), SimExtension, keepSim);
        }

        static void SweepFolder(string dir, string extension, HashSet<string> keep)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string file in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(file);
                bool listed = name.EndsWith(extension, StringComparison.Ordinal) && keep.Contains(name.Substring(0, name.Length - extension.Length));
                if (listed) continue;
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
        }

        /// <summary>The tiles and the sidecar a save.json lists (none when it can't be read: its files then go).</summary>
        static (List<string> Tiles, string Sim) Listed(string path)
        {
            if (!File.Exists(path)) return (new List<string>(), null);
            try
            {
                var save = SaveJson.Read(File.ReadAllText(path), out var edits);
                return (edits.Select(t => t.Sha256).ToList(), save.Sim?.Sha256);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { return (new List<string>(), null); }
        }

        // ---------- reading ----------

        /// <summary>
        /// Reads the save in <paramref name="folder"/>, tiles included and checked against their hashes. When save.json is
        /// unreadable or a tile is missing or damaged, the backup is read instead and <see cref="SaveFile.Recovered"/> says
        /// why. A save from a newer version throws <see cref="FormatTooNewException"/> and is never read from its backup.
        /// Nothing good at all throws <see cref="InvalidDataException"/>.
        /// </summary>
        public static SaveFile Read(string folder)
        {
            string path = Path.Combine(folder, SaveFile.FileName);
            string backup = Path.Combine(folder, SaveFile.BackupName);
            Exception first;
            try { return ReadOne(folder, path); }
            catch (FormatTooNewException) { throw; }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { first = e; }
            if (!File.Exists(backup)) throw first as InvalidDataException ?? new InvalidDataException(first.Message, first);
            try
            {
                var save = ReadOne(folder, backup);
                save.Recovered = File.Exists(path) ? first.Message : "save.json was missing.";
                return save;
            }
            catch (FormatTooNewException) { throw; }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                throw new InvalidDataException($"The save can't be read ({first.Message}), nor its backup ({e.Message}).", first);
            }
        }

        static SaveFile ReadOne(string folder, string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("save.json is missing.", path);
            var save = SaveJson.Read(File.ReadAllText(path), out var edits);
            save.Edits = ReadTiles(folder, edits);
            ReadSim(folder, save);
            return save;
        }

        /// <summary>
        /// The save's sidecar, checked against its hash. A missing or damaged one doesn't lose the design: the save opens
        /// without its simulation (the clock starts over) and <see cref="SaveFile.Recovered"/> says so.
        /// </summary>
        static void ReadSim(string folder, SaveFile save)
        {
            if (!(save.Sim is SimSidecarRef sim)) return;
            string file = Path.Combine(folder, SimFolder, sim.Sha256 + SimExtension);
            string why;
            if (!File.Exists(file)) why = "The simulation's state was missing, so the clock starts over.";
            else
            {
                byte[] bytes = File.ReadAllBytes(file);
                if (SaveTileFile.Hash(bytes) == sim.Sha256) { save.SimState = new SimSidecarBytes(bytes, sim.SidecarVersion); return; }
                why = "The simulation's state was damaged, so the clock starts over.";
            }
            save.Sim = null;
            save.Recovered = why;
        }

        static TerrainEdits ReadTiles(string folder, List<SaveJson.TileRef> refs)
        {
            var list = new List<TerrainEditTile>(refs.Count);
            foreach (var r in refs)
            {
                string file = Path.Combine(folder, TilesFolder, r.Sha256 + SaveTileFile.Extension);
                if (!File.Exists(file)) throw new InvalidDataException($"Tile ({r.Column}, {r.Row}) is missing.");
                byte[] bytes = File.ReadAllBytes(file);
                if (SaveTileFile.Hash(bytes) != r.Sha256) throw new InvalidDataException($"Tile ({r.Column}, {r.Row}) is damaged.");
                var tile = SaveTileFile.Decode(bytes);
                if (tile.Key.Column != r.Column || tile.Key.Row != r.Row) throw new InvalidDataException($"Tile ({r.Column}, {r.Row}) holds another place.");
                list.Add(tile);
            }
            return TerrainEdits.From(list);
        }

        /// <summary>
        /// The saves of one area for Load Area, newest first (then by folder). A save from a newer version is listed with
        /// its <see cref="SaveSummary.Refusal"/>; a folder with nothing readable is left out.
        /// </summary>
        public static List<SaveSummary> List(string dataRoot, string packageId)
        {
            var list = new List<SaveSummary>();
            string area = AreaFolder(dataRoot, packageId);
            if (!Directory.Exists(area)) return list;
            foreach (string folder in Directory.GetDirectories(area))
            {
                if (SafeFolder.IsTrash(folder)) continue;
                var s = Summarise(folder);
                if (s != null) list.Add(s);
            }
            return list.OrderByDescending(s => s.SavedUtc, StringComparer.Ordinal).ThenBy(s => s.Folder, StringComparer.Ordinal).ToList();
        }

        static SaveSummary Summarise(string folder)
        {
            string path = Path.Combine(folder, SaveFile.FileName);
            foreach (string file in new[] { path, Path.Combine(folder, SaveFile.BackupName) })
            {
                if (!File.Exists(file)) continue;
                try
                {
                    var s = SaveJson.Summary(File.ReadAllText(file));
                    s.Folder = folder;
                    s.FromBackup = file != path;
                    return s;
                }
                catch (FormatTooNewException e)
                {
                    return new SaveSummary { Folder = folder, Name = NameHint(file), Refusal = e.Message, SavedUtc = "" };
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { }
            }
            return null;
        }

        /// <summary>A newer save's name, if its JSON is plain enough to read one; else its folder.</summary>
        static string NameHint(string file)
        {
            try { return (string)VersionedJson.Parse(File.ReadAllText(file))["Name"] ?? Path.GetFileName(Path.GetDirectoryName(file)); }
            catch (Exception e) when (e is IOException || e is JsonException || e is InvalidDataException || e is InvalidCastException) { return Path.GetFileName(Path.GetDirectoryName(file)); }
        }

        /// <summary>The newest save of an area that this version can open, or null (Continue, task P3-04).</summary>
        public static SaveSummary Newest(string dataRoot, string packageId) => List(dataRoot, packageId).FirstOrDefault(s => s.Refusal.Length == 0);

        public static int Count(string dataRoot, string packageId) => List(dataRoot, packageId).Count;

        // ---------- rename and delete ----------

        /// <summary>Renames a save (its name inside save.json; the folder stays). False when it can't be read or is newer.</summary>
        public static bool Rename(string folder, string name)
        {
            SaveFile save;
            try { save = Read(folder); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { return false; }
            save.Name = name;
            return Write(folder, save);
        }

        /// <summary>Deletes one save, all or nothing (renamed aside first). False, untouched, when a file in it is open.</summary>
        public static bool Delete(string folder) => SafeFolder.TryDelete(folder);

        /// <summary>Deletes every save of an area (Manage Areas › Delete, with the package). True when none are left.</summary>
        public static bool DeleteArea(string dataRoot, string packageId) => SafeFolder.TryDelete(AreaFolder(dataRoot, packageId));
    }
}
