#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Water;
using Newtonsoft.Json;

namespace MountainPlanner.Persistence
{
    /// <summary>The manifest's water entry (manifest v3, task 4.14, WS-08, LK-10): the file, its hash and counts. Not part of the package id.</summary>
    public sealed class WaterInfo
    {
        public string File { get; set; } = WaterFile.FileName;
        public string Sha256 { get; set; } = "";
        public int Streams { get; set; }
        public int Names { get; set; }
    }

    /// <summary>
    /// A package's water vectors (task 4.14, WS-08, LK-10): water.json beside the manifest. "streams": one OSM waterway line
    /// per entry in a stable order, centre lines to the centimetre; "names": each named OSM water area with a point inside
    /// it. Recorded in <see cref="PackageManifest.Water"/>, never as a layer, so adding it to an area (or a later download)
    /// leaves the package id and every save alone. An area without it uses the rasterised streams at the default width, and
    /// its lakes have no OSM names.
    /// </summary>
    public static class WaterFile
    {
        public const string FileName = "water.json";

        sealed class Named
        {
            public string Name { get; set; } = "";
            /// <summary>x, y in Albers metres, to the centimetre.</summary>
            public double[] Inside { get; set; } = Array.Empty<double>();
        }

        sealed class Line
        {
            public string Way { get; set; } = "";
            public string Class { get; set; } = "";
            public string Name { get; set; } = "";
            public double Width { get; set; }
            /// <summary>"osm" when the width came from OSM's `width` tag, "default" otherwise.</summary>
            public string WidthFrom { get; set; } = "";
            /// <summary>x0, y0, x1, y1, … in Albers metres, to the centimetre.</summary>
            public double[] Points { get; set; } = Array.Empty<double>();
        }

        /// <summary>Writes the streams and water names and records them in the manifest (replacing any earlier ones).</summary>
        public static void Add(string folder, PackageManifest manifest, IEnumerable<StreamLine> streams, IEnumerable<WaterName> names)
        {
            var lines = streams
                .Select(s => new Line
                {
                    Way = s.Way.ToString("x16", CultureInfo.InvariantCulture), Class = s.Class, Name = s.Name, Width = Math.Round(s.WidthMetres, 2),
                    WidthFrom = s.WidthFromOsm ? "osm" : "default",
                    Points = s.Points.SelectMany(p => new[] { Math.Round(p.X, 2), Math.Round(p.Y, 2) }).ToArray(),
                })
                .Where(l => l.Points.Length >= 4)
                .OrderBy(l => l.Way, StringComparer.Ordinal).ThenBy(l => l.Points[0]).ThenBy(l => l.Points[1])
                .ToList();
            var named = names
                .Where(n => !string.IsNullOrWhiteSpace(n.Name))
                .Select(n => new Named { Name = n.Name.Trim(), Inside = new[] { Math.Round(n.Inside.X, 2), Math.Round(n.Inside.Y, 2) } })
                .OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Inside[0]).ThenBy(n => n.Inside[1])
                .ToList();
            var sb = new StringBuilder("{\"streams\":[\n");
            for (int i = 0; i < lines.Count; i++)
                sb.Append(JsonConvert.SerializeObject(lines[i], Settings)).Append(i + 1 < lines.Count ? ",\n" : "\n");
            sb.Append("],\"names\":[\n");
            for (int i = 0; i < named.Count; i++)
                sb.Append(JsonConvert.SerializeObject(named[i], Settings)).Append(i + 1 < named.Count ? ",\n" : "\n");
            sb.Append("]}\n");
            byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
            File.WriteAllBytes(Path.Combine(folder, FileName), bytes);
            manifest.Water = new WaterInfo { File = FileName, Sha256 = Hash(bytes), Streams = lines.Count, Names = named.Count };
        }

        /// <summary>The package's named water areas (LK-10), or none for an area without water.json. Verified against the manifest.</summary>
        public static List<WaterName> ReadNames(string folder, PackageManifest manifest)
        {
            var names = new List<WaterName>();
            var file = Load(folder, manifest);
            foreach (var n in file?.Names ?? new List<Named>())
            {
                if (n.Inside.Length != 2) throw new InvalidDataException($"'{n.Name}' has no point inside it.");
                names.Add(new WaterName { Name = n.Name, Inside = new AlbersPoint(n.Inside[0], n.Inside[1]) });
            }
            return names;
        }

        sealed class Contents
        {
            public List<Line> Streams { get; set; } = new List<Line>();
            public List<Named> Names { get; set; } = new List<Named>();
        }

        static Contents? Load(string folder, PackageManifest manifest)
        {
            var info = manifest.Water;
            if (info == null) return null;
            byte[] bytes = File.ReadAllBytes(Path.Combine(folder, info.File));
            if (Hash(bytes) != info.Sha256) throw new InvalidDataException("water.json doesn't match its hash.");
            return JsonConvert.DeserializeObject<Contents>(Encoding.UTF8.GetString(bytes), Settings);
        }

        /// <summary>The package's streams, or none for an area made before task 4.14. Verified against the manifest.</summary>
        public static List<StreamLine> Read(string folder, PackageManifest manifest)
        {
            var streams = new List<StreamLine>();
            var file = Load(folder, manifest);
            foreach (var l in file?.Streams ?? new List<Line>())
            {
                if (!ulong.TryParse(l.Way, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong way)) throw new InvalidDataException($"'{l.Way}' isn't a way id.");
                var s = new StreamLine { Way = way, Class = l.Class, Name = l.Name, WidthMetres = l.Width, WidthFromOsm = l.WidthFrom == "osm" };
                for (int k = 0; k + 1 < l.Points.Length; k += 2) s.Points.Add(new AlbersPoint(l.Points[k], l.Points[k + 1]));
                streams.Add(s);
            }
            return streams;
        }

        public static bool Has(PackageManifest manifest) => manifest.Water != null;

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
