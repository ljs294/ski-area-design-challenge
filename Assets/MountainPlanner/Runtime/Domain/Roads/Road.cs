using System.Collections.Generic;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Domain.Roads
{
    /// <summary>Whether a road is sealed (asphalt, concrete) or not (gravel, dirt, tracks).</summary>
    public enum RoadSurface { Paved = 0, Unpaved = 1 }

    /// <summary>
    /// One road from OpenStreetMap (task 12d): its centre line in Albers metres, class, surface, width and name. The
    /// package keeps them so the game can draw roads now (asphalt or gravel on the ground) and build road meshes, labels
    /// and the road tool later. Data © OpenStreetMap contributors, ODbL.
    /// </summary>
    public sealed class Road
    {
        /// <summary>The OSM highway class, e.g. "secondary", "residential", "track".</summary>
        public string Class = "";
        public RoadSurface Surface;
        public double WidthMetres;
        public string Name = "";
        public List<AlbersPoint> Points = new List<AlbersPoint>();
    }

    /// <summary>The rules that turn OSM tags into a road (task 12d): which ways count, their surface and width.</summary>
    public static class RoadRules
    {
        /// <summary>Carriageway widths in metres by highway class (OSM `width` wins when it's sensible).</summary>
        public static readonly IReadOnlyDictionary<string, double> Widths = new Dictionary<string, double>
        {
            ["motorway"] = 20, ["trunk"] = 14, ["primary"] = 11, ["secondary"] = 9, ["tertiary"] = 7.5,
            ["motorway_link"] = 7, ["trunk_link"] = 7, ["primary_link"] = 7, ["secondary_link"] = 6.5, ["tertiary_link"] = 6,
            ["unclassified"] = 6, ["residential"] = 6.5, ["living_street"] = 5, ["service"] = 4.5, ["track"] = 3.5,
        };

        static readonly HashSet<string> PavedSurfaces = new HashSet<string>
        {
            "paved", "asphalt", "concrete", "concrete:plates", "concrete:lanes", "paving_stones", "sett", "chipseal", "metal", "wood", "brick",
        };

        static readonly HashSet<string> UnpavedSurfaces = new HashSet<string>
        {
            "unpaved", "gravel", "fine_gravel", "compacted", "dirt", "earth", "ground", "grass", "sand", "mud", "pebblestone", "rock", "grass_paver",
        };

        /// <summary>Whether a highway class is a road we keep (paths, footways and cycleways are not).</summary>
        public static bool IsRoad(string highway) => Widths.ContainsKey(highway);

        /// <summary>
        /// The surface: the `surface` tag when it says; otherwise tracks are unpaved and every other class paved.
        /// </summary>
        public static RoadSurface SurfaceOf(string highway, string surface)
        {
            if (PavedSurfaces.Contains(surface)) return RoadSurface.Paved;
            if (UnpavedSurfaces.Contains(surface)) return RoadSurface.Unpaved;
            return highway == "track" ? RoadSurface.Unpaved : RoadSurface.Paved;
        }
    }
}
