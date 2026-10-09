using System;
using System.Collections.Generic;
using System.Linq;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// One data source the credits list (S9, task P2-07): an attribution line from one or more packages' manifests,
    /// split for display, with the products the packages' provenance records for it and the areas that use it.
    /// </summary>
    public sealed class CreditSource
    {
        /// <summary>The manifest's attribution line, exactly as written; empty for a provider with no line of its own.</summary>
        public string Line { get; set; } = "";
        /// <summary>What it gives ("Elevation"), or empty.</summary>
        public string What { get; set; } = "";
        /// <summary>Who to credit, with any ©, as the line says it.</summary>
        public string Who { get; set; } = "";
        /// <summary>The licence, from the line's closing brackets ("Public domain", "CC BY 4.0"), or empty.</summary>
        public string Licence { get; set; } = "";
        /// <summary>The provenance providers matched to this source ("USGS 3DEP").</summary>
        public List<string> Providers { get; } = new List<string>();
        /// <summary>The provenance products, in the order first seen.</summary>
        public List<string> Products { get; } = new List<string>();
        /// <summary>The areas that use it, by the names the library shows.</summary>
        public List<string> Areas { get; } = new List<string>();
    }

    /// <summary>
    /// Builds the credits' data sources from package manifests (S9, task P2-07; 0.4: "data attributions generated from
    /// the installed mountains' manifests"). It only reads: manifests are frozen (0.3 §5) and nothing here changes them.
    ///
    /// Each distinct attribution line becomes one source. Provenance entries join the source whose line names their
    /// provider (or a known short form, such as "USGS 3DEP" for the 3D Elevation Program); a provider no line names
    /// becomes a source of its own, so nothing a package records is ever left out. Provenance for a layer the download
    /// didn't use (Layer "(none)") credits nothing.
    /// </summary>
    public static class CreditsReader
    {
        /// <summary>Provenance providers' short names, and a phrase their attribution line carries.</summary>
        static readonly (string Provider, string Phrase)[] Aliases =
        {
            ("USGS 3DEP", "3D Elevation Program"),
            ("USGS", "Geological Survey"),
            ("ESA", "ESA WorldCover"),
            ("Meta", "Meta and World Resources Institute"),
        };

        /// <summary>The provenance layer value for a source a download tried but didn't use.</summary>
        public const string UnusedLayer = "(none)";

        /// <summary>The sources for every readable area in <paramref name="entries"/>; one that can't be read goes to <paramref name="failed"/> and is skipped.</summary>
        public static List<CreditSource> Read(IEnumerable<LibraryEntry> entries, Action<LibraryEntry, Exception> failed = null)
        {
            var areas = new List<(string, PackageManifest)>();
            foreach (var e in entries)
            {
                try { areas.Add((e.Name, ResortPackage.ReadManifest(e.Folder))); }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { failed?.Invoke(e, ex); }
            }
            return Build(UniqueNames(areas));
        }

        /// <summary>
        /// The sources for these areas, in the order their lines first appear (areas in the order given). Pure: the same
        /// manifests give the same list.
        /// </summary>
        public static List<CreditSource> Build(IEnumerable<(string Name, PackageManifest Manifest)> areas)
        {
            var sources = new List<CreditSource>();
            var byLine = new Dictionary<string, CreditSource>(StringComparer.Ordinal);
            foreach (var (name, m) in areas)
            {
                var mine = new List<CreditSource>();
                foreach (string raw in m.Attribution)
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0) continue;
                    if (!byLine.TryGetValue(line, out var s))
                    {
                        var (what, who, licence) = Split(line);
                        s = new CreditSource { Line = line, What = what, Who = who, Licence = licence };
                        byLine.Add(line, s);
                        sources.Add(s);
                    }
                    AddOnce(s.Areas, name);
                    mine.Add(s);
                }
                foreach (var p in m.Provenance)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.Provider) || p.Layer == UnusedLayer) continue;
                    var s = mine.FirstOrDefault(x => Names(x.Line, p.Provider));
                    if (s == null)
                    {
                        // A provider no line of this package credits: its own source, shared with other areas by name.
                        string key = "provider:" + p.Provider.Trim();
                        if (!byLine.TryGetValue(key, out s))
                        {
                            s = new CreditSource { Who = p.Provider.Trim() };
                            byLine.Add(key, s);
                            sources.Add(s);
                        }
                        AddOnce(s.Areas, name);
                    }
                    AddOnce(s.Providers, p.Provider.Trim());
                    if (!string.IsNullOrWhiteSpace(p.Product)) AddOnce(s.Products, p.Product.Trim());
                }
            }
            return sources;
        }

        /// <summary>
        /// Splits an attribution line for display: "Elevation: U.S. Geological Survey, 3D Elevation Program (public
        /// domain)." gives ("Elevation", "U.S. Geological Survey, 3D Elevation Program", "Public domain"). A part that
        /// isn't there is empty; a line it can't split is all Who.
        /// </summary>
        public static (string What, string Who, string Licence) Split(string line)
        {
            string rest = (line ?? "").Trim();
            if (rest.EndsWith(".", StringComparison.Ordinal)) rest = rest.Substring(0, rest.Length - 1).TrimEnd();
            string licence = "";
            if (rest.EndsWith(")", StringComparison.Ordinal))
            {
                int open = rest.LastIndexOf('(');
                if (open > 0)
                {
                    licence = rest.Substring(open + 1, rest.Length - open - 2).Trim();
                    rest = rest.Substring(0, open).TrimEnd();
                    if (licence.Length > 0 && char.IsLower(licence[0])) licence = char.ToUpperInvariant(licence[0]) + licence.Substring(1);
                }
            }
            string what = "";
            int colon = rest.IndexOf(": ", StringComparison.Ordinal);
            // A short label before the colon ("Water and roads"); a colon later in a long credit stays where it is.
            int copyright = rest.IndexOf('©');
            if (colon > 0 && colon <= 32 && (copyright < 0 || copyright > colon))
            {
                what = rest.Substring(0, colon).Trim();
                rest = rest.Substring(colon + 2).Trim();
            }
            return (what, rest, licence);
        }

        /// <summary>True when the attribution line credits this provenance provider.</summary>
        public static bool Names(string line, string provider)
        {
            if (string.IsNullOrEmpty(line) || string.IsNullOrWhiteSpace(provider)) return false;
            provider = provider.Trim();
            if (line.IndexOf(provider, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var (p, phrase) in Aliases)
                if (string.Equals(p, provider, StringComparison.OrdinalIgnoreCase) && line.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Areas that share a name (two downloads of one mountain) are told apart by their size, "Jackson Hole · 2 km", and
        /// by a number when that's shared too, "Jackson Hole · 5 km (2)". The same package listed twice stays one area.
        /// </summary>
        static List<(string, PackageManifest)> UniqueNames(List<(string Name, PackageManifest Manifest)> areas)
        {
            string Sized((string Name, PackageManifest Manifest) a) =>
                a.Name + " · " + (a.Manifest.Site.SizeMetres / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " km";
            var distinct = areas.GroupBy(a => a.Manifest.PackageId.Length > 0 ? a.Manifest.PackageId : a.Name + "|" + a.GetHashCode(), StringComparer.Ordinal)
                                .Select(g => g.First()).ToList();
            var names = distinct.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var labels = distinct.Select(a => names[a.Name] > 1 ? Sized(a) : a.Name).ToList();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var totals = labels.GroupBy(l => l, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var result = new List<(string, PackageManifest)>();
            for (int i = 0; i < distinct.Count; i++)
            {
                string label = labels[i];
                if (totals[label] > 1)
                {
                    seen.TryGetValue(label, out int n);
                    seen[label] = ++n;
                    if (n > 1) label += $" ({n})";
                }
                result.Add((label, distinct[i].Manifest));
            }
            return result;
        }

        static void AddOnce(List<string> list, string value)
        {
            if (!list.Contains(value)) list.Add(value);
        }
    }
}
