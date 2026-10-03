using System;
using System.Collections.Generic;
using System.Globalization;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.UI.Picker
{
    /// <summary>
    /// The site picker's state (0.4 S3), free of UI Toolkit so it can be tested directly: the square's
    /// size and centre, the name and whether it is still the suggestion, the estimate, search results
    /// and the offline flag. <see cref="SitePicker"/> renders it and feeds it events.
    /// </summary>
    public sealed class SitePickerModel
    {
        public const double DefaultSizeKm = 4.0;

        public event Action Changed;

        public double SizeKm { get; private set; } = DefaultSizeKm;
        /// <summary>The square, once the player has placed it.</summary>
        public SiteSquare? Square { get; private set; }
        public string Name { get; private set; } = "";
        /// <summary>True while the name is the picker's suggestion, which a newer suggestion may replace.</summary>
        public bool NameIsSuggestion { get; private set; } = true;
        public SiteEstimate? Estimate { get; private set; }
        public bool Offline { get; private set; }
        public bool Searching { get; private set; }
        public string SearchMessage { get; private set; } = "";
        public IReadOnlyList<PlaceResult> Results { get; private set; } = Array.Empty<PlaceResult>();

        /// <summary>Download is possible once the square is placed and named (0.4 S3).</summary>
        public bool CanDownload => Square.HasValue && Name.Trim().Length > 0 && !Offline;

        public string SizeLabel => PickerUnits.Size(SizeKm);

        /// <summary>Sets the size, snapped to 0.1 km and clamped to 2–5 km; the square keeps its centre.</summary>
        public void SetSize(double km)
        {
            double snapped = Math.Round(Math.Max(SiteSquare.MinSizeKm, Math.Min(SiteSquare.MaxSizeKm, km)) * 10) / 10;
            if (snapped == SizeKm) return;
            SizeKm = snapped;
            if (Square.HasValue) Square = SiteSquare.Create(Square.Value.Centre, SizeKm);
            Estimate = null;
            Notify();
        }

        /// <summary>Steps the size by tenths of a kilometre (arrow keys: 1; Shift: 10).</summary>
        public void StepSize(int tenths) => SetSize(SizeKm + tenths * SiteSquare.SizeStepKm);

        /// <summary>Centres the square on a point (a click, or a search result).</summary>
        public void PlaceAt(AlbersPoint centre)
        {
            Square = SiteSquare.Create(centre, SizeKm);
            Estimate = null;
            Notify();
        }

        /// <summary>Moves a placed square by metres east and north (arrow keys on the map).</summary>
        public void Nudge(double east, double north)
        {
            if (!Square.HasValue) return;
            PlaceAt(Square.Value.Centre + (east, north));
        }

        /// <summary>The player typed in the name field. Clearing it lets suggestions back in.</summary>
        public void TypeName(string name)
        {
            name = name ?? "";
            if (name == Name) return;
            Name = name;
            NameIsSuggestion = name.Trim().Length == 0;
            Notify();
        }

        /// <summary>A suggested name; it never replaces a name the player typed.</summary>
        public void Suggest(string name)
        {
            if (!NameIsSuggestion || string.IsNullOrWhiteSpace(name) || name == Name) return;
            Name = name.Trim();
            Notify();
        }

        public void SetEstimate(SiteEstimate estimate)
        {
            Estimate = estimate;
            Notify();
        }

        public void SetOffline(bool offline)
        {
            if (offline == Offline) return;
            Offline = offline;
            if (offline) Searching = false;
            Notify();
        }

        public void BeginSearch()
        {
            Searching = true;
            SearchMessage = "Searching…";
            Results = Array.Empty<PlaceResult>();
            Notify();
        }

        public void ShowResults(IReadOnlyList<PlaceResult> results)
        {
            Searching = false;
            Results = results ?? Array.Empty<PlaceResult>();
            SearchMessage = Results.Count == 0 ? "No places found in the United States." : "";
            Notify();
        }

        /// <summary>The search couldn't run: say so in words, under the field (the map may still work).</summary>
        public void ShowSearchProblem(string message)
        {
            Searching = false;
            Results = Array.Empty<PlaceResult>();
            SearchMessage = message ?? "";
            Notify();
        }

        public void ClearResults()
        {
            if (Results.Count == 0 && SearchMessage.Length == 0) return;
            Results = Array.Empty<PlaceResult>();
            SearchMessage = "";
            Searching = false;
            Notify();
        }

        /// <summary>The hand-off to the download flow. Throws unless <see cref="CanDownload"/>.</summary>
        public PickedSite Choose(bool includeImagery = false)
        {
            if (!CanDownload) throw new InvalidOperationException("Place and name the square first.");
            var estimate = Estimate ?? default;
            return PickedSite.Create(Name, Square.Value.Centre, SizeKm, includeImagery, estimate);
        }

        void Notify() => Changed?.Invoke();
    }
}
