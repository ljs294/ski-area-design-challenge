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
    }

    /// <summary>
    /// The library of downloaded mountains: every package folder under &lt;data&gt;/Resorts. The index
    /// is rebuilt from the packages themselves, so it can never disagree with what's on disk.
    /// </summary>
    public static class ResortLibrary
    {
        public static string ResortsFolder(string dataRoot) => Path.Combine(dataRoot, "Resorts");

        public static List<LibraryEntry> Scan(string dataRoot)
        {
            var entries = new List<LibraryEntry>();
            string resorts = ResortsFolder(dataRoot);
            if (!Directory.Exists(resorts)) return entries;
            foreach (string folder in Directory.GetDirectories(resorts))
            {
                if (!File.Exists(Path.Combine(folder, ResortPackage.ManifestFile))) continue;
                PackageManifest m;
                try { m = ResortPackage.ReadManifest(folder); }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException) { continue; }
                entries.Add(new LibraryEntry
                {
                    PackageId = m.PackageId, Name = m.Site.Name, Folder = folder, Latitude = m.Site.Latitude, Longitude = m.Site.Longitude,
                    SizeKm = m.Site.SizeMetres / 1000.0, TerrainScore = m.Quality.Score, FloraScore = m.Flora.Score,
                    BytesOnDisk = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
                    CreatedUtc = m.CreatedUtc, CacheReady = TerrainCache.IsCurrent(folder, m),
                });
            }
            return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.PackageId, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Moves a freshly built package into the library as &lt;data&gt;/Resorts/&lt;packageId&gt;. If that
        /// package is already there (same id = same content), the new copy is discarded.
        /// </summary>
        public static string Add(string dataRoot, string builtFolder)
        {
            var m = ResortPackage.ReadManifest(builtFolder);
            string target = Path.Combine(ResortsFolder(dataRoot), m.PackageId);
            Directory.CreateDirectory(ResortsFolder(dataRoot));
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

        public int Version { get; set; } = CurrentVersion;
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

        public static void Save(string packageFolder, ViewState state)
        {
            string path = Path.Combine(packageFolder, FileName), temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(state, Json) + "\n", new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>The saved view, or defaults when there's none or it can't be read (never an error).</summary>
        public static ViewState Load(string packageFolder)
        {
            string path = Path.Combine(packageFolder, FileName);
            if (!File.Exists(path)) return new ViewState();
            try
            {
                var s = JsonConvert.DeserializeObject<ViewState>(File.ReadAllText(path));
                return s == null || s.Version > CurrentVersion ? new ViewState() : s;
            }
            catch (JsonException) { return new ViewState(); }
        }
    }
}
