using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MountainPlanner.Persistence.Saves;

namespace MountainPlanner.Persistence
{
    /// <summary>A package's OpenStreetMap water, developed land and roads layers (task P3-16, BB6).</summary>
    public static class WaterAndRoads
    {
        public const string CoreLayer = "osm-core";
        public const string RingLayer = "osm-ring";
        public static readonly string[] LayerIds = { CoreLayer, RingLayer, RoadsFile.LayerId };
        public const string Provider = "OpenStreetMap contributors";
        /// <summary>The provenance layer recorded when water and roads couldn't be had.</summary>
        public const string NoneLayer = "(none)";
        /// <summary>The provenance reason when the player chose Skip water and roads (task P3-16, BB5).</summary>
        public const string SkippedReason = "Skipped by the player while OpenStreetMap was holding the download up.";

        /// <summary>
        /// True when any of the three is missing: downloaded while OpenStreetMap was down or skipped, or before water (some
        /// have roads only) or roads came in.
        /// </summary>
        public static bool Needed(PackageManifest m) => LayerIds.Any(id => m.Layers.All(l => l.Id != id));
    }

    /// <summary>Why an area can't move to a new id just now; the message says so in plain words.</summary>
    public sealed class AreaRefusedException : InvalidOperationException
    {
        public AreaRefusedException(string message) : base(message) { }
    }

    /// <summary>The points a move passes; a test stops at each one to prove the area survives a crash there.</summary>
    public enum RekeyStep { LayersWritten, SavesCopied, RecentAdded, Committed, CleanedUp }

    /// <summary>
    /// Moves an area to a new package id after layers were added to it (task P3-16, BB6: Get water and roads). The id
    /// hashes every layer, so adding one changes it, and the saves (<c>Saves/&lt;id&gt;</c>) and recent.json move with it. The
    /// package folder keeps its name: the library keys on the manifest's id, never on the folder.
    ///
    /// The manifest write is the commit point. Before it, the old area is whole: the new layers aren't listed, the saves'
    /// copy and the recent entry are under an id no package has. After it, the new area is whole, and cleaning up the old
    /// id's saves and entry is all that's left.
    /// </summary>
    public static class AreaRekey
    {
        const string StagingPrefix = ".rekey-";

        /// <summary>Why <paramref name="packageFolder"/> can't move now, or null when it can. Checks only this side (the files).</summary>
        public static string Refusal(string dataRoot, string packageFolder, string oldId)
        {
            string library = LibraryIndex.Refusal(dataRoot);
            if (library != null) return library;
            string recent = RecentResorts.Load(dataRoot).Refusal;
            if (recent.Length > 0) return recent;
            var saves = SaveStore.List(dataRoot, oldId);
            if (saves.Any(s => s.Refusal.Length > 0)) return "One of its saves was made by a newer version of Mountain Planner. Update the game first.";
            foreach (string file in SaveFiles(SaveStore.AreaFolder(dataRoot, oldId)))
                if (Regex.Matches(File.ReadAllText(file), IdPattern(oldId)).Count > 1)
                    return $"A save in it ({Path.GetFileName(Path.GetDirectoryName(file))}) names the area more than once, so it can't be moved safely.";
            return InUse(packageFolder) ? InUseText : null;
        }

        public const string InUseText = "It's open in another window of the game, or one of its files is in use. Close it there first.";

        /// <summary>Another area in the library already has <paramref name="newId"/> (the same contents): the move would make two.</summary>
        public static bool Collides(string dataRoot, string packageFolder, string newId)
        {
            string resorts = ResortLibrary.ResortsFolder(dataRoot);
            return ResortLibrary.FolderWithId(resorts, newId, except: packageFolder) != null;
        }

        /// <summary>
        /// Moves the area: <paramref name="manifest"/> already lists the new layers (their files written), and is written here
        /// as the commit point. Throws <see cref="AreaRefusedException"/> when the area became busy before the commit, with
        /// the old area left as it was. Returns the new id.
        /// </summary>
        public static string Move(string dataRoot, string packageFolder, PackageManifest manifest, string oldId, Action<RekeyStep> step = null)
        {
            string newId = ResortPackage.ComputeId(manifest);
            if (newId == oldId) return oldId;
            string saves = SaveStore.Root(dataRoot);
            string staging = Path.Combine(saves, StagingPrefix + newId), target = SaveStore.AreaFolder(dataRoot, newId);

            // 1. The saves, copied byte for byte under the new id; only each save's top-level PackageId changes. Its Version
            //    stays, so an older save still migrates (and regrades) as it opens.
            string old = SaveStore.AreaFolder(dataRoot, oldId);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);   // a move cut short before
            if (Directory.Exists(target) && !SafeFolder.TryDelete(target)) throw new AreaRefusedException(InUseText);   // nobody's saves: no package has that id
            if (Directory.Exists(old))
            {
                Copy(old, staging, oldId, newId);
                Directory.Move(staging, target);
            }
            step?.Invoke(RekeyStep.SavesCopied);

            // 2. recent.json: the new id, opened when the old one was (the old entry goes after the commit).
            var recent = RecentResorts.Load(dataRoot);
            if (recent.Refusal.Length > 0) throw new AreaRefusedException(recent.Refusal);
            if (recent.Opened.TryGetValue(oldId, out string when))
            {
                recent.Opened[newId] = when;
                AtomicFile.WriteJson(Path.Combine(dataRoot, RecentResorts.FileName), recent);
            }
            step?.Invoke(RekeyStep.RecentAdded);

            // 3. The commit point.
            if (InUse(packageFolder)) throw new AreaRefusedException(InUseText);
            ResortPackage.WriteManifest(packageFolder, manifest);
            step?.Invoke(RekeyStep.Committed);

            // 4. Cleaning up the old id; anything left here is harmless (no package has it).
            recent = RecentResorts.Load(dataRoot);
            if (recent.Refusal.Length == 0 && recent.Opened.Remove(oldId))
                AtomicFile.WriteJson(Path.Combine(dataRoot, RecentResorts.FileName), recent);
            SaveStore.DeleteArea(dataRoot, oldId);
            step?.Invoke(RekeyStep.CleanedUp);
            return newId;
        }

        static bool InUse(string packageFolder) =>
            TerrainCache.Versions(packageFolder).Any(v => TerrainCache.InUse(TerrainCache.FolderFor(packageFolder, v)));

        static string IdPattern(string id) => "\"PackageId\"\\s*:\\s*\"" + Regex.Escape(id) + "\"";

        /// <summary>save.json and save.json.bak in each of an area's saves.</summary>
        static string[] SaveFiles(string area) => !Directory.Exists(area) ? Array.Empty<string>()
            : Directory.GetDirectories(area).Where(d => !SafeFolder.IsTrash(d))
                       .SelectMany(d => new[] { Path.Combine(d, SaveFile.FileName), Path.Combine(d, SaveFile.BackupName) })
                       .Where(File.Exists).ToArray();

        static void Copy(string from, string to, string oldId, string newId)
        {
            Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from))
                if (!SafeFolder.IsTrash(dir)) Copy(dir, Path.Combine(to, Path.GetFileName(dir)), oldId, newId);
            foreach (string file in Directory.GetFiles(from))
            {
                string name = Path.GetFileName(file), dest = Path.Combine(to, name);
                if (name.EndsWith(".tmp", StringComparison.Ordinal)) continue;   // a write cut short
                if (name != SaveFile.FileName && name != SaveFile.BackupName)
                {
                    File.Copy(file, dest);
                    continue;
                }
                // UTF-8 decodes and encodes back to the same bytes.
                string text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                // Only the id's characters change; the key's spacing stays as written.
                var m = Regex.Match(text, IdPattern(oldId));
                string patched = !m.Success ? text
                    : text.Substring(0, m.Index + m.Length - oldId.Length - 1) + newId + text.Substring(m.Index + m.Length - 1);
                SaveStore.WriteFlushed(dest, Encoding.UTF8.GetBytes(patched));
            }
        }
    }
}
