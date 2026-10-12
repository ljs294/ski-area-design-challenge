#nullable enable
using System.Text.RegularExpressions;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// The game's version, taken from git (task P2-10). Release tags are plain <c>vMAJOR.MINOR.PATCH</c>, 0.x while
    /// the game is in development (owner, 2026-10-12): a build of a tagged commit is that version, "0.2.0". Any other
    /// build is a preview that sorts after the tag it grew from, "0.2.0+dev.7.abc1234" (seven commits on), with
    /// ".dirty" when the working tree had uncommitted changes. Before the first tag it's "0.0.0+dev.N.sha".
    /// Older tags that aren't three numbers (v0.1, from the MapLibre game) never count.
    /// </summary>
    public static class ReleaseVersion
    {
        /// <summary>The glob <c>git describe --match</c> takes: three dotted numbers after a v.</summary>
        public const string TagGlob = "v[0-9]*.[0-9]*.[0-9]*";

        static readonly Regex Tag = new Regex(@"^v(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant);
        static readonly Regex Describe = new Regex(@"^(v\d+\.\d+\.\d+)-(\d+)-g([0-9a-f]+)$", RegexOptions.CultureInvariant);

        /// <summary>True for a release tag name such as v0.2.0.</summary>
        public static bool IsTag(string? name) => name != null && Tag.IsMatch(name.Trim());

        /// <summary>
        /// The version from <c>git describe --tags --long --match TagGlob --abbrev=7</c> ("v0.2.0-7-gabc1234"). When
        /// describe found no tag, pass null with the short SHA and the commit count instead.
        /// </summary>
        public static string FromDescribe(string? describe, bool dirty, string shortSha = "unknown", int commits = 0)
        {
            var match = describe == null ? null : Describe.Match(describe.Trim());
            string version;
            if (match != null && match.Success)
            {
                string tag = match.Groups[1].Value.Substring(1);
                int ahead = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (ahead == 0 && !dirty) return tag;
                version = $"{tag}+dev.{ahead}.{match.Groups[3].Value}";
            }
            else version = $"0.0.0+dev.{commits}.{shortSha}";
            return dirty ? version + ".dirty" : version;
        }

        /// <summary>True for a release (a tagged, clean build): no "+" build metadata.</summary>
        public static bool IsRelease(string? version) => !string.IsNullOrEmpty(version) && version!.IndexOf('+') < 0 && version != "0.0.0";

        /// <summary>The tag a release version came from: "0.2.0" → "v0.2.0"; null for previews.</summary>
        public static string? TagOf(string? version) => IsRelease(version) ? "v" + version : null;
    }
}
