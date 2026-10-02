#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Roads;
using Newtonsoft.Json;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// A package's roads (task 12d): roads.json, recorded in the manifest as layer "roads" with the file's SHA-256.
    /// One line per road, in a stable order, centre lines to the centimetre, so the same roads always give the same
    /// file and the same package id. Packages made before task 12d have no roads layer; they open as before.
    /// </summary>
    public static class RoadsFile
    {
        public const string LayerId = "roads", FileName = "roads.json", LayerType = "roads";

        sealed class Line
        {
            public string Class { get; set; } = "";
            public string Surface { get; set; } = "";
            public double Width { get; set; }
            public string Name { get; set; } = "";
            /// <summary>x0, y0, x1, y1, … in Albers metres, to the centimetre.</summary>
            public double[] Points { get; set; } = Array.Empty<double>();
        }

        /// <summary>Writes the roads and records them in the manifest (replacing any roads layer).</summary>
        public static void Add(string folder, PackageManifest manifest, IEnumerable<Road> roads)
        {
            var lines = roads
                .Select(r => new Line
                {
                    Class = r.Class, Surface = r.Surface == RoadSurface.Paved ? "paved" : "unpaved", Width = Math.Round(r.WidthMetres, 2), Name = r.Name,
                    Points = r.Points.SelectMany(p => new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) }).ToArray(),
                })
                .Where(l => l.Points.Length >= 4)
                .OrderBy(l => l.Points[0]).ThenBy(l => l.Points[1]).ThenBy(l => l.Class, StringComparer.Ordinal).ThenBy(l => l.Name, StringComparer.Ordinal)
                .ToList();
            var sb = new StringBuilder("{\"roads\":[\n");
            for (int i = 0; i < lines.Count; i++)
                sb.Append(JsonConvert.SerializeObject(lines[i], Settings)).Append(i + 1 < lines.Count ? ",\n" : "\n");
            sb.Append("]}\n");
            byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
            File.WriteAllBytes(Path.Combine(folder, FileName), bytes);
            manifest.Layers.RemoveAll(l => l.Id == LayerId);
            manifest.Layers.Add(new LayerInfo
            {
                Id = LayerId, File = FileName, Type = LayerType, Width = lines.Count, Units = "OpenStreetMap roads: centre lines (EPSG:6350 m), class, surface, width (m), name",
                Sha256 = Hash(bytes),
            });
        }

        /// <summary>The package's roads, or none for a package made before task 12d. Verified against the manifest.</summary>
        public static List<Road> Read(string folder, PackageManifest manifest)
        {
            var layer = manifest.Layers.FirstOrDefault(l => l.Id == LayerId);
            if (layer == null) return new List<Road>();
            byte[] bytes = File.ReadAllBytes(Path.Combine(folder, layer.File));
            if (Hash(bytes) != layer.Sha256) throw new InvalidDataException("roads.json doesn't match its hash.");
            var file = JsonConvert.DeserializeAnonymousType(Encoding.UTF8.GetString(bytes), new { Roads = new List<Line>() }, Settings);
            var roads = new List<Road>();
            foreach (var l in file?.Roads ?? new List<Line>())
            {
                var road = new Road { Class = l.Class, Surface = l.Surface == "paved" ? RoadSurface.Paved : RoadSurface.Unpaved, WidthMetres = l.Width, Name = l.Name };
                for (int k = 0; k + 1 < l.Points.Length; k += 2) road.Points.Add(new AlbersPoint(l.Points[k], l.Points[k + 1]));
                roads.Add(road);
            }
            return roads;
        }

        public static bool Has(PackageManifest manifest) => manifest.Layers.Any(l => l.Id == LayerId);

        static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Culture = CultureInfo.InvariantCulture,
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver { NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy() },
        };

        static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
        }
    }
}
