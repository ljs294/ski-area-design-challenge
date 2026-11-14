#nullable enable
using System;
using System.Collections.Generic;

namespace MountainPlanner.Acquisition.Updates
{
    /// <summary>One file attached to a release: its name, size in bytes and download address.</summary>
    public sealed class ReleaseAsset
    {
        public string Name = "", Url = "";
        public long Size;
        /// <summary>GitHub's own "sha256:…" digest, lower-case hex without the prefix, or "" when it gave none.</summary>
        public string Sha256 = "";
    }

    /// <summary>
    /// What a release offers the updater (task 4.23, UP4): its version, its page, the game zip
    /// (<c>SkiAreaDesignChallenge-0.3.2-win64.zip</c>) and that zip's <c>.sha256</c> file, which release.ps1 writes and
    /// every release since v0.3.0 carries. Read from GitHub's <c>/releases/latest</c> or <c>/releases/tags/v…</c> answer,
    /// or from a local feed's <c>latest.json</c> in the same shape (demos and tests).
    /// </summary>
    public sealed class ReleaseInfo
    {
        public const string ProductFile = "SkiAreaDesignChallenge";

        public GameVersion Version;
        public string PageUrl = "";
        public ReleaseAsset Zip = new ReleaseAsset(), Sha = new ReleaseAsset();
        public bool Draft, Prerelease;

        /// <summary>The zip's name for a version: SkiAreaDesignChallenge-0.3.2-win64.zip (release-lib.ps1 Get-ZipName).</summary>
        public static string ZipName(GameVersion v) => $"{ProductFile}-{v.Number}-win64.zip";

        /// <summary>The one folder at the top of the zip: SkiAreaDesignChallenge-0.3.2 (release-lib.ps1 Get-ReleaseName).</summary>
        public static string TopFolder(GameVersion v) => $"{ProductFile}-{v.Number}";

        /// <summary>
        /// Reads one release from GitHub's JSON. Throws <see cref="FormatException"/> when the tag isn't vX.Y.Z or the
        /// zip or its .sha256 is missing: such a release can't be installed, so it's never offered.
        /// </summary>
        public static ReleaseInfo Parse(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object?> root)) throw new FormatException("The release answer isn't a JSON object.");
            return FromObject(root);
        }

        /// <summary>
        /// GitHub's /releases list (the launcher's version picker, R4): every release that can be installed, newest first.
        /// Drafts, pre-releases and releases without a vX.Y.Z tag, the zip or its .sha256 are left out.
        /// </summary>
        public static List<ReleaseInfo> ParseList(string json)
        {
            if (!(MiniJson.Parse(json) is List<object?> items)) throw new FormatException("The releases answer isn't a JSON list.");
            var found = new List<ReleaseInfo>();
            foreach (var item in items)
            {
                if (!(item is Dictionary<string, object?> one)) continue;
                ReleaseInfo info;
                try { info = FromObject(one); }
                catch (FormatException) { continue; }
                if (info.Draft || info.Prerelease || found.Exists(f => f.Version.CompareTo(info.Version) == 0)) continue;
                found.Add(info);
            }
            found.Sort((a, b) => b.Version.CompareTo(a.Version));
            return found;
        }

        static ReleaseInfo FromObject(Dictionary<string, object?> root)
        {
            string tag = Text(root, "tag_name");
            if (!GameVersion.TryParse(tag, out var version) || version.Build.Length > 0 || !tag.StartsWith("v", StringComparison.Ordinal))
                throw new FormatException($"The release tag '{tag}' isn't vX.Y.Z.");
            var info = new ReleaseInfo
            {
                Version = version,
                PageUrl = Text(root, "html_url"),
                Draft = root.TryGetValue("draft", out var d) && d is bool db && db,
                Prerelease = root.TryGetValue("prerelease", out var p) && p is bool pb && pb,
            };
            string zip = ZipName(version), sha = zip + ".sha256";
            ReleaseAsset? zipAsset = null, shaAsset = null;
            if (root.TryGetValue("assets", out var list) && list is List<object?> assets)
                foreach (var item in assets)
                {
                    if (!(item is Dictionary<string, object?> a)) continue;
                    var asset = new ReleaseAsset
                    {
                        Name = Text(a, "name"),
                        Url = Text(a, "browser_download_url"),
                        Size = a.TryGetValue("size", out var s) && s is double size ? (long)size : 0,
                        Sha256 = Digest(Text(a, "digest")),
                    };
                    if (string.Equals(asset.Name, zip, StringComparison.OrdinalIgnoreCase)) zipAsset = asset;
                    else if (string.Equals(asset.Name, sha, StringComparison.OrdinalIgnoreCase)) shaAsset = asset;
                }
            if (zipAsset == null || zipAsset.Url.Length == 0) throw new FormatException($"Release {tag} has no {zip}.");
            if (shaAsset == null || shaAsset.Url.Length == 0) throw new FormatException($"Release {tag} has no {sha}.");
            info.Zip = zipAsset;
            info.Sha = shaAsset;
            return info;
        }

        static string Text(Dictionary<string, object?> map, string key) =>
            map.TryGetValue(key, out var v) && v is string s ? s : "";

        static string Digest(string digest)
        {
            const string prefix = "sha256:";
            if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
            string hex = digest.Substring(prefix.Length).ToLowerInvariant();
            return ShaFile.IsHex64(hex) ? hex : "";
        }
    }
}
