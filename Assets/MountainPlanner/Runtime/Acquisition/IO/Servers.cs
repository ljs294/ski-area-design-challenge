#nullable enable
using System;
using System.Collections.Generic;
using MountainPlanner.Acquisition.Providers;

namespace MountainPlanner.Acquisition.IO
{
    /// <summary>
    /// The players' names for the servers a download uses (task P3-16, BB5): progress, waits and held-up alerts say
    /// "USGS elevation isn't answering", never a host name, "AWS" or "the server".
    /// </summary>
    public static class Servers
    {
        public const string UsgsElevation = "USGS elevation";
        public const string EsaLandCover = "ESA land cover";
        public const string MetaCanopy = "Meta forest canopy";
        public const string UsdaSpecies = "USDA tree species";
        public const string OpenStreetMap = "OpenStreetMap";

        static readonly Dictionary<string, string> ByHost = Build();

        static Dictionary<string, string> Build()
        {
            var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Host(S1mTiles.Bucket)] = UsgsElevation,
                [Host(Dep3Service.Service)] = UsgsElevation,
                [Host(WorldCoverTiles.Bucket)] = EsaLandCover,
                [Host(CanopyTiles.Bucket)] = MetaCanopy,
                [Host(BigmapService.Service)] = UsdaSpecies,
            };
            foreach (string mirror in OsmFeatures.Servers) hosts[Host(mirror)] = OpenStreetMap;
            return hosts;
        }

        /// <summary>The name for <paramref name="hostOrUrl"/> (a host or a whole URL); an unknown host is named as it is.</summary>
        public static string NameOf(string hostOrUrl)
        {
            string host = Uri.TryCreate(hostOrUrl, UriKind.Absolute, out var uri) ? uri.Host : hostOrUrl;
            return ByHost.TryGetValue(host, out string? name) ? name : host;
        }

        static string Host(string url) => new Uri(url).Host;
    }
}
