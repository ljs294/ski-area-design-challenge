using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace MountainPlanner.Persistence
{
    /// <summary>Checks a package end to end before the game opens it (task 05).</summary>
    public static class PackageValidator
    {
        static readonly string[] Required = { "heights-core", "heights-ring" };

        /// <summary>Every problem found, in plain words; empty means the package is sound.</summary>
        public static List<string> Validate(string folder)
        {
            var problems = new List<string>();
            PackageManifest m;
            try { m = ResortPackage.ReadManifest(folder); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is UnauthorizedAccessException)
            {
                problems.Add("The manifest can't be read: " + e.Message);
                return problems;
            }
            if (m.PackageId != ResortPackage.ComputeId(m)) problems.Add("The package id doesn't match its contents.");
            foreach (string id in Required)
                if (m.Layers.All(l => l.Id != id)) problems.Add($"The '{id}' layer is missing.");
            foreach (var layer in m.Layers)
            {
                string path = Path.Combine(folder, layer.File);
                if (!File.Exists(path))
                {
                    problems.Add($"{layer.File} is missing.");
                    continue;
                }
                if (layer.Type == RoadsFile.LayerType)
                {
                    try { RoadsFile.Read(folder, m); }
                    catch (Exception e) when (e is InvalidDataException || e is JsonException || e is IOException) { problems.Add($"{layer.File} is damaged: {e.Message}"); }
                    continue;
                }
                try
                {
                    using (var fs = File.OpenRead(path))
                    {
                        string hash;
                        GridHeader header;
                        if (layer.Type == "float32") hash = GridFile.HashValues(GridFile.ReadFloats(fs, out header));
                        else hash = GridFile.HashValues(GridFile.ReadBytes(fs, out header));
                        if (hash != layer.Sha256) problems.Add($"{layer.File} is corrupt (its contents don't match the manifest).");
                        if (header.Width != layer.Width * layer.Bands || header.Height != layer.Height) problems.Add($"{layer.File} has the wrong size.");
                    }
                }
                catch (InvalidDataException e) { problems.Add($"{layer.File} is damaged: {e.Message}"); }
                catch (EndOfStreamException) { problems.Add($"{layer.File} is truncated."); }
            }
            return problems;
        }
    }

    /// <summary>What one area's folder holds on disk (task P2-04), by what it's for.</summary>
    public struct DiskUse
    {
        /// <summary>The downloaded package: manifest, layers and view state.</summary>
        public long Package;
        /// <summary>The terrain cache this game uses (rebuilt from the package when it's missing).</summary>
        public long Cache;
        /// <summary>Caches of older game versions, and anything an interrupted delete left: what Free space removes.</summary>
        public long OlderCaches;
        /// <summary>Caches of newer game versions, kept for them.</summary>
        public long NewerCaches;

        public long Total => Package + Cache + OlderCaches + NewerCaches;
    }

    /// <summary>One downloaded mountain, as the library screen (S2) lists it.</summary>
    public sealed class LibraryEntry
    {
        public string PackageId { get; set; } = "";
        /// <summary>The name shown: the player's own (view.json, task P2-04), else the one it was downloaded with.</summary>
        public string Name { get; set; } = "";
        /// <summary>The name it was downloaded with (the manifest's Site.Name); the demo and -site look areas up by it.</summary>
        public string OriginalName { get; set; } = "";
        public string Folder { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double SizeKm { get; set; }
        public int TerrainScore { get; set; }
        public int FloraScore { get; set; }
        /// <summary>Everything in the area's folder; 0 until <see cref="ResortLibrary.Measure"/> has run (<see cref="Measured"/>).</summary>
        public long BytesOnDisk { get; set; }
        public DiskUse Disk { get; set; }
        public bool Measured { get; set; }
        public string CreatedUtc { get; set; } = "";
        public bool CacheReady { get; set; }
        /// <summary>Why it can't be opened (a package from a newer version of the game), or empty when it can.</summary>
        public string Refusal { get; set; } = "";
        /// <summary>Why it can't be renamed (its view.json is from a newer version of the game), or empty when it can.</summary>
        public string RenameRefusal { get; set; } = "";
    }

    /// <summary>
    /// &lt;data&gt;/library.json (task 08): the version of the data folder's layout (Resorts/, Downloads/, recent.json).
    /// It holds only the version; the listing is always a scan of the packages themselves. A data folder without one is
    /// v1, as Phase 1 left it. A layout change bumps the version, moves the folders in a migration, then rewrites this
    /// file, so an older game sees the newer number and leaves the library alone.
    /// </summary>
    public sealed class LibraryIndex
    {
        public const int CurrentVersion = 1;
        public const string FileName = "library.json";
        public static readonly VersionedJson Migrations = new VersionedJson("library", nameof(Version), CurrentVersion);

        public int Version { get; set; } = CurrentVersion;

        /// <summary>The layout version of a data folder: 1 when it has no library.json, or an unreadable one.</summary>
        public static int VersionOf(string dataRoot)
        {
            string path = Path.Combine(dataRoot, FileName);
            if (!File.Exists(path)) return VersionedJson.FirstVersion;
            try { return Migrations.VersionOf(VersionedJson.Parse(File.ReadAllText(path))); }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is UnauthorizedAccessException) { return VersionedJson.FirstVersion; }
        }

        /// <summary>Why this game can't use the data folder (a newer game's layout), or null when it can.</summary>
        public static string Refusal(string dataRoot)
        {
            int v = VersionOf(dataRoot);
            return v > CurrentVersion ? new FormatTooNewException(Migrations.Format, v, CurrentVersion).Message : null;
        }

        /// <summary>Writes library.json when the folder has none (the first package added); never over a newer one.</summary>
        public static void Ensure(string dataRoot)
        {
            string path = Path.Combine(dataRoot, FileName);
            if (File.Exists(path)) return;
            Directory.CreateDirectory(dataRoot);
            AtomicFile.WriteJson(path, new LibraryIndex());
        }
    }

    /// <summary>
    /// The library of downloaded mountains: every package folder under &lt;data&gt;/Resorts. The index
    /// is rebuilt from the packages themselves, so it can never disagree with what's on disk.
    /// </summary>
    public static class ResortLibrary
    {
        public static string ResortsFolder(string dataRoot) => Path.Combine(dataRoot, "Resorts");

        /// <summary>The longest name an area can have: the site picker's limit.</summary>
        public const int MaxNameLength = 60;

        /// <summary>
        /// The mountains this game can open. Packages from a newer version of the game go to <paramref name="newer"/>
        /// (name and folder only, with the reason), so the library can show them without misreading them. A data
        /// folder whose layout is newer (<see cref="LibraryIndex.Refusal"/>) lists nothing.
        ///
        /// It reads only the small files. Sizes need every file in every folder, so they come from <see cref="Measure"/>,
        /// which the game runs off the main thread (task P2-04); <paramref name="measure"/> runs it here.
        /// </summary>
        public static List<LibraryEntry> Scan(string dataRoot, List<LibraryEntry> newer = null, bool measure = false)
        {
            var entries = new List<LibraryEntry>();
            string resorts = ResortsFolder(dataRoot);
            if (!Directory.Exists(resorts) || LibraryIndex.Refusal(dataRoot) != null) return entries;
            foreach (string folder in Directory.GetDirectories(resorts))
            {
                if (SafeFolder.IsTrash(folder) || !File.Exists(Path.Combine(folder, ResortPackage.ManifestFile))) continue;
                PackageManifest m;
                try { m = ResortPackage.ReadManifest(folder); }
                catch (FormatTooNewException e)
                {
                    newer?.Add(new LibraryEntry
                    {
                        PackageId = Path.GetFileName(folder), Name = NameHint(folder), Folder = folder, Refusal = e.Message,
                        RenameRefusal = e.Message,
                    });
                    continue;
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException) { continue; }
                var view = ViewState.Load(folder);
                entries.Add(new LibraryEntry
                {
                    PackageId = m.PackageId, Name = view.Name.Length > 0 ? view.Name : m.Site.Name, OriginalName = m.Site.Name,
                    Folder = folder, Latitude = m.Site.Latitude, Longitude = m.Site.Longitude,
                    SizeKm = m.Site.SizeMetres / 1000.0, TerrainScore = m.Quality.Score, FloraScore = m.Flora.Score,
                    CreatedUtc = m.CreatedUtc, CacheReady = TerrainCache.IsCurrent(folder, m), RenameRefusal = view.Refusal,
                });
            }
            newer?.Sort((a, b) => string.CompareOrdinal(a.Folder, b.Folder));
            if (measure)
            {
                Measure(entries);
                if (newer != null) Measure(newer);
            }
            return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.PackageId, StringComparer.Ordinal).ToList();
        }

        /// <summary>Fills in each entry's disk use (<see cref="MeasureFolder"/>). Reads every file's size: keep it off the main thread.</summary>
        public static void Measure(IEnumerable<LibraryEntry> entries)
        {
            foreach (var e in entries) SetDisk(e, MeasureFolder(e.Folder));
        }

        public static void SetDisk(LibraryEntry entry, DiskUse use)
        {
            entry.Disk = use;
            entry.BytesOnDisk = use.Total;
            entry.Measured = true;
        }

        /// <summary>What an area's folder holds, split into the package and its caches by version.</summary>
        public static DiskUse MeasureFolder(string folder, int cacheVersion = TerrainCache.Version)
        {
            var use = new DiskUse();
            if (!Directory.Exists(folder)) return use;
            foreach (string file in Directory.GetFiles(folder))
            {
                try { use.Package += new FileInfo(file).Length; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }
            }
            foreach (string sub in Directory.GetDirectories(folder))
            {
                long bytes = SafeFolder.Bytes(sub);
                if (SafeFolder.IsTrash(sub)) use.OlderCaches += bytes;   // an interrupted delete: Free space sweeps it
                else if (!TerrainCache.TryParseFolder(Path.GetFileName(sub), out int v)) use.Package += bytes;
                else if (v == cacheVersion) use.Cache += bytes;
                else if (v < cacheVersion) use.OlderCaches += bytes;
                else use.NewerCaches += bytes;
            }
            return use;
        }

        /// <summary>
        /// A name as the library keeps it: control characters and runs of spaces become one space, the ends are
        /// trimmed, and it's cut to <see cref="MaxNameLength"/>. Empty means there's no usable name.
        /// </summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            bool space = false;
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            int length = Math.Min(sb.Length, MaxNameLength);
            if (length > 0 && length < sb.Length && char.IsHighSurrogate(sb[length - 1])) length--;   // never half a character
            return sb.ToString(0, length).TrimEnd();
        }

        /// <summary>
        /// Renames an area (task P2-04). The name goes into its view.json; the package stays as downloaded, so its id,
        /// folder and manifest never change. Its original name clears the player's own. False, with nothing written,
        /// when the name is empty, or the area or its view.json is from a newer version of the game (see
        /// <see cref="LibraryEntry.RenameRefusal"/>).
        /// </summary>
        public static bool Rename(LibraryEntry entry, string name)
        {
            string clean = NormalizeName(name);
            if (clean.Length == 0 || entry.Refusal.Length > 0) return false;
            // A view.json that can't be read just now (locked by another program) would load as defaults, and saving
            // those would lose the camera, bookmarks and layers: refuse instead. A damaged one has nothing to lose.
            string viewPath = Path.Combine(entry.Folder, ViewState.FileName);
            try { if (File.Exists(viewPath)) File.ReadAllText(viewPath); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                entry.RenameRefusal = "Its view settings can't be read just now: " + e.Message;
                return false;
            }
            var view = ViewState.Load(entry.Folder);
            if (view.Refusal.Length > 0)
            {
                entry.RenameRefusal = view.Refusal;
                return false;
            }
            view.Name = clean == entry.OriginalName ? "" : clean;
            if (!ViewState.Save(entry.Folder, view)) return false;
            entry.Name = clean;
            return true;
        }

        /// <summary>The name to show for an open package: the player's own (view.json), else the manifest's.</summary>
        public static string DisplayName(string packageFolder, PackageManifest manifest)
        {
            string own = ViewState.Load(packageFolder).Name;
            return own.Length > 0 ? own : manifest.Site.Name;
        }

        /// <summary>
        /// A newer package's name, for its library row only: the Site.Name string if the manifest still has one where
        /// v1 kept it, else the folder name. Nothing else is read from a format this game doesn't know.
        /// </summary>
        static string NameHint(string folder)
        {
            try
            {
                var name = VersionedJson.Parse(File.ReadAllText(Path.Combine(folder, ResortPackage.ManifestFile)))["Site"]?["Name"];
                if (name != null && name.Type == Newtonsoft.Json.Linq.JTokenType.String && ((string)name).Length > 0) return (string)name;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is InvalidCastException || e is InvalidOperationException) { }
            return Path.GetFileName(folder);
        }

        /// <summary>
        /// Moves a freshly built package into the library as &lt;data&gt;/Resorts/&lt;packageId&gt;. If that
        /// package is already there (same id = same content), the new copy is discarded.
        /// </summary>
        public static string Add(string dataRoot, string builtFolder)
        {
            int layout = LibraryIndex.VersionOf(dataRoot);
            if (layout > LibraryIndex.CurrentVersion) throw new FormatTooNewException(LibraryIndex.Migrations.Format, layout, LibraryIndex.CurrentVersion);
            var m = ResortPackage.ReadManifest(builtFolder);
            string target = Path.Combine(ResortsFolder(dataRoot), m.PackageId);
            Directory.CreateDirectory(ResortsFolder(dataRoot));
            LibraryIndex.Ensure(dataRoot);
            // A folder there without a manifest isn't the package (a stub left by an interrupted delete): replace it.
            if (Directory.Exists(target) && !File.Exists(Path.Combine(target, ResortPackage.ManifestFile)) && SafeFolder.TryMoveToTrash(target, out string stub) && stub != null)
                SafeFolder.SweepTrash(ResortsFolder(dataRoot));
            if (Directory.Exists(target))
            {
                if (!string.Equals(Path.GetFullPath(builtFolder).TrimEnd('\\', '/'), Path.GetFullPath(target).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(builtFolder, true);
                return target;
            }
            Directory.Move(builtFolder, target);
            return target;
        }

        /// <summary>
        /// Deletes a mountain (the UI confirms first and names the space freed, 0.4). False, with nothing deleted, while
        /// any game has one of its caches open (<see cref="TerrainCache.Hold"/>) or a file in it; <paramref name="freed"/>
        /// is then 0.
        /// </summary>
        public static bool TryRemove(LibraryEntry entry, out long freed) => TryRemove(entry, out freed, sweep: true);

        /// <summary>
        /// The same, with the slow part optional: with <paramref name="sweep"/> false only the quick, all-or-nothing
        /// rename happens here, and <see cref="SweepTrash"/> (on a worker thread) removes the files. The size freed is
        /// the measured one when there is one (<see cref="LibraryEntry.Measured"/>).
        /// </summary>
        public static bool TryRemove(LibraryEntry entry, out long freed, bool sweep)
        {
            freed = 0;
            if (!Directory.Exists(entry.Folder)) return true;
            foreach (int v in TerrainCache.Versions(entry.Folder))
                if (TerrainCache.InUse(TerrainCache.FolderFor(entry.Folder, v))) return false;
            long bytes = entry.Measured ? entry.BytesOnDisk : MeasureFolder(entry.Folder).Total;
            if (!SafeFolder.TryMoveToTrash(entry.Folder, out _)) return false;
            if (sweep) SweepTrash(Path.GetDirectoryName(Path.GetFullPath(entry.Folder).TrimEnd('\\', '/')));
            freed = bytes;
            return true;
        }

        /// <summary>Removes what deletes left in a folder's ".trash-" folders (the library's Resorts, or a package); returns the bytes.</summary>
        public static long SweepTrash(string folder) => SafeFolder.SweepTrash(folder);

        /// <summary>The bytes in the library's own ".trash-" folders: deletes cut short, which Free space sweeps.</summary>
        public static long LeftoverBytes(string dataRoot)
        {
            string resorts = ResortsFolder(dataRoot);
            if (!Directory.Exists(resorts)) return 0;
            long bytes = 0;
            foreach (string folder in Directory.GetDirectories(resorts, SafeFolder.TrashPrefix + "*")) bytes += SafeFolder.Bytes(folder);
            return bytes;
        }

        /// <summary>
        /// Free space (task P2-04): removes, from every area, the terrain caches of older game versions that no game
        /// has open, and anything an interrupted delete left behind. Packages and the caches this game (or a newer one)
        /// uses stay, so nothing has to be downloaded or rebuilt. Returns the bytes freed.
        /// </summary>
        public static long FreeSpace(string dataRoot)
        {
            string resorts = ResortsFolder(dataRoot);
            if (!Directory.Exists(resorts) || LibraryIndex.Refusal(dataRoot) != null) return 0;
            long freed = SafeFolder.SweepTrash(resorts);
            foreach (string folder in Directory.GetDirectories(resorts))
                if (!SafeFolder.IsTrash(folder) && File.Exists(Path.Combine(folder, ResortPackage.ManifestFile)))
                    freed += TerrainCache.FreeOlderVersions(folder);
            return freed;
        }

        /// <summary>What <see cref="FreeSpace"/> would free, from measured entries (caches still open are counted too).</summary>
        public static long Freeable(IEnumerable<LibraryEntry> entries) => entries.Where(e => e.Measured).Sum(e => e.Disk.OlderCaches);
    }

    /// <summary>
    /// A mountain's view state (0.3 §3): the player's name for it, camera, bookmarks, view time and layers. Small,
    /// versioned JSON. v2 (task P2-04) added <see cref="Name"/>; a v1 file has none, so the area keeps its manifest name.
    /// </summary>
    public sealed class ViewState
    {
        public const int CurrentVersion = 2;
        public const string FileName = "view.json";
        public static readonly VersionedJson Migrations = new VersionedJson("view state", nameof(Version), CurrentVersion,
            v1 => { if (v1["Name"] == null) v1["Name"] = ""; });   // v1 → v2: no name of its own yet

        public int Version { get; set; } = CurrentVersion;

        /// <summary>The player's name for the area (Rename in Manage Areas), or empty for the name it was downloaded with.</summary>
        public string Name { get; set; } = "";

        /// <summary>Set when the file on disk is from a newer version of the game: these are defaults, and Save leaves that file alone.</summary>
        [JsonIgnore] public string Refusal { get; private set; } = "";
        public CameraView Camera { get; set; } = new CameraView();
        public List<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();
        /// <summary>View time (the clock placeholder's ViewTime), local to the resort.</summary>
        public int Year { get; set; } = 2026;
        public int DayOfYear { get; set; } = 32;
        public int SecondOfDay { get; set; } = 11 * 3600;
        public Dictionary<string, bool> Layers { get; set; } = new Dictionary<string, bool>
        {
            ["snow"] = true, ["groundCover"] = true, ["forest"] = true, ["coverMap"] = false,
        };

        public sealed class CameraView
        {
            /// <summary>Local frame metres (x east, z north), orbit target.</summary>
            public double TargetX { get; set; }
            public double TargetZ { get; set; }
            public double Distance { get; set; } = 3500;
            public double Yaw { get; set; } = 200;
            public double Pitch { get; set; } = 35;
        }

        public sealed class Bookmark
        {
            public string Name { get; set; } = "";
            public CameraView Camera { get; set; } = new CameraView();
        }

        static readonly JsonSerializerSettings Json = new JsonSerializerSettings { Formatting = Formatting.Indented };

        /// <summary>Writes view.json; false (and nothing written) when the file there is from a newer version of the game.</summary>
        public static bool Save(string packageFolder, ViewState state)
        {
            string path = Path.Combine(packageFolder, FileName);
            if (Migrations.IsNewer(path)) return false;
            AtomicFile.WriteJson(path, state, Json);
            return true;
        }

        /// <summary>
        /// The saved view, migrated to the current version, or defaults when there's none or it can't be read (never an
        /// error). A newer version's file gives defaults with <see cref="Refusal"/> set.
        /// </summary>
        public static ViewState Load(string packageFolder)
        {
            string path = Path.Combine(packageFolder, FileName);
            if (!File.Exists(path)) return new ViewState();
            try { return Migrations.Read<ViewState>(File.ReadAllText(path), Json); }
            catch (FormatTooNewException e) { return new ViewState { Refusal = e.Message }; }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is UnauthorizedAccessException) { return new ViewState(); }
        }
    }
}
