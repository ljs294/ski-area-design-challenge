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

    /// <summary>One downloaded mountain, as the library screen (S2) lists it.</summary>
    public sealed class LibraryEntry
    {
        public string PackageId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double SizeKm { get; set; }
        public int TerrainScore { get; set; }
        public int FloraScore { get; set; }
        public long BytesOnDisk { get; set; }
        public string CreatedUtc { get; set; } = "";
        public bool CacheReady { get; set; }
        /// <summary>Why it can't be opened (a package from a newer version of the game), or empty when it can.</summary>
        public string Refusal { get; set; } = "";
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

        /// <summary>
        /// The mountains this game can open. Packages from a newer version of the game go to <paramref name="newer"/>
        /// (name, folder and size only, with the reason), so the library can show them without misreading them. A
        /// data folder whose layout is newer (<see cref="LibraryIndex.Refusal"/>) lists nothing.
        /// </summary>
        public static List<LibraryEntry> Scan(string dataRoot, List<LibraryEntry> newer = null)
        {
            var entries = new List<LibraryEntry>();
            string resorts = ResortsFolder(dataRoot);
            if (!Directory.Exists(resorts) || LibraryIndex.Refusal(dataRoot) != null) return entries;
            foreach (string folder in Directory.GetDirectories(resorts))
            {
                if (!File.Exists(Path.Combine(folder, ResortPackage.ManifestFile))) continue;
                PackageManifest m;
                try { m = ResortPackage.ReadManifest(folder); }
                catch (FormatTooNewException e)
                {
                    newer?.Add(new LibraryEntry
                    {
                        PackageId = Path.GetFileName(folder), Name = NameHint(folder), Folder = folder, Refusal = e.Message,
                        BytesOnDisk = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
                    });
                    continue;
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException) { continue; }
                entries.Add(new LibraryEntry
                {
                    PackageId = m.PackageId, Name = m.Site.Name, Folder = folder, Latitude = m.Site.Latitude, Longitude = m.Site.Longitude,
                    SizeKm = m.Site.SizeMetres / 1000.0, TerrainScore = m.Quality.Score, FloraScore = m.Flora.Score,
                    BytesOnDisk = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
                    CreatedUtc = m.CreatedUtc, CacheReady = TerrainCache.IsCurrent(folder, m),
                });
            }
            newer?.Sort((a, b) => string.CompareOrdinal(a.Folder, b.Folder));
            return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.PackageId, StringComparer.Ordinal).ToList();
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
            if (Directory.Exists(target))
            {
                if (!string.Equals(Path.GetFullPath(builtFolder).TrimEnd('\\', '/'), Path.GetFullPath(target).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(builtFolder, true);
                return target;
            }
            Directory.Move(builtFolder, target);
            return target;
        }

        /// <summary>Deletes a mountain (the UI confirms first and names the space freed, 0.4).</summary>
        public static long Remove(LibraryEntry entry)
        {
            long bytes = entry.BytesOnDisk;
            if (Directory.Exists(entry.Folder)) Directory.Delete(entry.Folder, true);
            return bytes;
        }
    }

    /// <summary>A mountain's view state (0.3 §3): camera, bookmarks, view time and layers. Small, versioned JSON.</summary>
    public sealed class ViewState
    {
        public const int CurrentVersion = 1;
        public const string FileName = "view.json";
        public static readonly VersionedJson Migrations = new VersionedJson("view state", nameof(Version), CurrentVersion);

        public int Version { get; set; } = CurrentVersion;

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
