using System.Collections.Generic;
using System.Text.RegularExpressions;
using MountainPlanner.Domain.Terrain;
using MountainPlanner.Persistence;

namespace MountainPlanner.UI.Flow
{
    /// <summary>One scored line on the quality card: "Terrain 71 / 100 Fair", a caveat, and the manifest's one-liner.</summary>
    public sealed class QualityLine
    {
        public string Label = "";
        public int Score;
        public QualityBand Band;
        public string Word = "";
        public string Caveat = "";
        public string Detail = "";
    }

    /// <summary>
    /// The S5 quality card (0.4 S5, T18), read from a package manifest: one line per scored part of the
    /// data. The number and its band word always appear together.
    /// </summary>
    public sealed class QualityCardViewModel
    {
        public string Title { get; private set; } = "";
        public string Place { get; private set; } = "";
        public readonly List<QualityLine> Lines = new List<QualityLine>();

        public static QualityCardViewModel From(PackageManifest m, bool justDownloaded)
        {
            var vm = new QualityCardViewModel
            {
                Title = justDownloaded ? $"{m.Site.Name} is ready" : m.Site.Name,
                Place = LibraryViewModel.Place(m.Site.Latitude, m.Site.Longitude) + " · " + LibraryViewModel.Km(m.Site.SizeMetres / 1000.0),
            };
            vm.Lines.Add(Line("Terrain", m.Quality.Score, m.Quality.OneLiner));
            if (m.Flora != null && (m.Flora.Score > 0 || m.Flora.OneLiner.Length > 0))
                vm.Lines.Add(Line("Flora", m.Flora.Score, m.Flora.OneLiner));
            // ── Further quality lines go here ──────────────────────────────────────────────────────────
            // Add a Line(...) per new scored part of the manifest; the card lays out whatever is in Lines.
            // (A forest-calibration line was planned here and dropped in the 2026-10-02 scope cut.)
            return vm;
        }

        public static QualityLine Line(string label, int score, string detail)
        {
            var band = QualityBands.Of(score);
            return new QualityLine
            {
                Label = label, Score = score, Band = band, Word = QualityBands.Word(band),
                Caveat = QualityBands.Caveat(band), Detail = WithoutScore(detail ?? ""),
            };
        }

        /// <summary>The manifest's one-liner repeats the score ("Terrain quality 30/100: 100% 3DEP 10 m"); the card already shows it.</summary>
        public static string WithoutScore(string oneLiner) => Regex.Replace(oneLiner, @"^\s*\w+ quality \d+\s*/\s*100\s*:\s*", "");
    }
}
