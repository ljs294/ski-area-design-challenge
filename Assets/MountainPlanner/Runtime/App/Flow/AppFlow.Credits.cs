using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// S9 Credits (task P2-07): built when the window opens, from every area in the library and the demo built into the
    /// game (each package's manifest: its attribution lines and provenance, through <see cref="CreditsReader"/>), plus the
    /// game's own sources. All of it is on disk, so the window reads the same offline.
    /// </summary>
    public sealed partial class AppFlow
    {
        /// <summary>The licence texts shipped with the game (the fonts' OFL, Json.NET's MIT notices).</summary>
        const string LicencesResource = ResourceFolder + "Licences";

        /// <summary>Who made the ground textures (CC0, Poly Haven; tools/assets/ground/README.md).</summary>
        internal static readonly string[] TextureAuthors = { "Charlotte Baglioni", "Amal Kumar", "Rob Tuytel", "Dario Barresi", "Rico Cilliers" };

        CreditsPage Credits() => BuildCredits(Areas(), Http.NetworkDisabled, Resources.Load<TextAsset>(LicencesResource)?.text ?? "");

        /// <summary>The credits page for these areas (tests call it with a scratch library).</summary>
        internal static CreditsPage BuildCredits(List<LibraryEntry> areas, bool offline, string licences)
        {
            var page = new CreditsPage { Licences = licences };
            var readable = areas.Where(e => e.Refusal.Length == 0 || e.Bundled).ToList();
            var sources = CreditsReader.Read(readable, (e, ex) => Debug.LogWarning($"[AppFlow] Credits: {e.Name}: {ex.Message}"));
            int count = readable.Count;
            var data = new CreditsSection
            {
                Name = "credits-data", Title = "Your areas' data",
                Note = count == 0 ? "" : count == 1 ? "1 area in the library" : $"{count} areas in the library",
                Empty = "Download an area to see the data it uses.",
            };
            foreach (var s in sources)
            {
                string used = count > 1 && s.Areas.Count == count ? "All your areas" : string.Join(", ", s.Areas);
                string detail = s.Products.Count > 0 ? string.Join("; ", s.Products) + " · " + used : used;
                data.Rows.Add(new CreditRow(s.What, s.Who, s.Licence, detail));
            }
            page.Sections.Add(data);

            // The site picker's own services (MapTileSource, CoverageIndex, NominatimClient).
            var picker = new CreditsSection { Name = "credits-picker", Title = "Maps and search", Note = "while choosing an area" };
            picker.Rows.Add(new CreditRow("Base map", "U.S. Geological Survey, The National Map", "Public domain"));
            picker.Rows.Add(new CreditRow("Lidar coverage", "U.S. Geological Survey, 3D Elevation Program", "Public domain"));
            picker.Rows.Add(new CreditRow("Place search", "Nominatim, © OpenStreetMap contributors", "ODbL"));
            page.Sections.Add(picker);

            page.Sections.Add(new CreditsSection { Name = "credits-art", Title = "Ground textures", Note = "Poly Haven · CC0", Names = string.Join(", ", TextureAuthors) });

            var software = new CreditsSection { Name = "credits-software", Title = "Software and type" };
            software.Rows.Add(new CreditRow("", "Made with Unity 6 and the Universal Render Pipeline", "Unity"));
            software.Rows.Add(new CreditRow("", "Json.NET, James Newton-King", "MIT"));
            software.Rows.Add(new CreditRow("", "Overpass and Overpass Mono, The Overpass Project Authors", "SIL OFL 1.1"));
            page.Sections.Add(software);

            page.Foot = (offline ? "Offline: " : "") + "Everything here comes from the areas on this computer; nothing is fetched.";
            return page;
        }
    }
}
