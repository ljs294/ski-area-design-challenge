using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// A resort package's manifest.json (0.3 §5): immutable source data, written once. Everything the
    /// game needs to open a resort offline, including provenance and attribution text.
    /// </summary>
    public sealed class PackageManifest
    {
        public const int CurrentFormat = 1;

        public int FormatVersion { get; set; } = CurrentFormat;

        /// <summary>A content hash of the site and layer values; identical inputs give an identical id.</summary>
        public string PackageId { get; set; } = "";

        public SiteInfo Site { get; set; } = new SiteInfo();
        public CrsInfo Crs { get; set; } = new CrsInfo();
        public List<LayerInfo> Layers { get; set; } = new List<LayerInfo>();
        public QualityInfo Quality { get; set; } = new QualityInfo();
        public List<ProvenanceInfo> Provenance { get; set; } = new List<ProvenanceInfo>();

        /// <summary>The species the species layers index (1-based; 0 means no tree).</summary>
        public List<SpeciesInfo> Species { get; set; } = new List<SpeciesInfo>();
        public List<string> Attribution { get; set; } = new List<string>();

        /// <summary>When the package was built. Not part of the package id.</summary>
        public string CreatedUtc { get; set; } = "";

        public string Tool { get; set; } = "";
    }

    public sealed class SiteInfo
    {
        public string Name { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double CentreX { get; set; }
        public double CentreY { get; set; }
        public int SizeMetres { get; set; }
        public int RingMetres { get; set; }
    }

    public sealed class CrsInfo
    {
        public int Epsg { get; set; } = 6350;
        public string VerticalDatum { get; set; } = "NAVD88 (GEOID18), metres";
        public double ScaleParallel { get; set; }
        public double ScaleMeridian { get; set; }
        public double GridConvergenceDegrees { get; set; }
    }

    public sealed class LayerInfo
    {
        public string Id { get; set; } = "";
        public string File { get; set; } = "";
        public string Type { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public double West { get; set; }
        public double North { get; set; }
        public double CellSize { get; set; }
        /// <summary>Values per cell, interleaved along each row (the file is Width × Bands wide).</summary>
        public int Bands { get; set; } = 1;
        /// <summary>What a value means, e.g. "canopy height in 0.25 m steps".</summary>
        public string Units { get; set; } = "";
        /// <summary>SHA-256 of the uncompressed values.</summary>
        public string Sha256 { get; set; } = "";
        public double Min { get; set; }
        public double Max { get; set; }
    }

    public sealed class QualityInfo
    {
        public int Score { get; set; }
        public string OneLiner { get; set; } = "";
        public Dictionary<string, double> SourceShares { get; set; } = new Dictionary<string, double>();
    }

    public sealed class SpeciesInfo
    {
        public int Index { get; set; }
        public int Spcd { get; set; }
        public string CommonName { get; set; } = "";
        /// <summary>Share of the ring's sampled biomass, 0–1.</summary>
        public double ShareOfBiomass { get; set; }
    }

    public sealed class ProvenanceInfo
    {
        public string Layer { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Product { get; set; } = "";
        public List<string> Items { get; set; } = new List<string>();
    }

    /// <summary>Writes and reads a resort package folder: manifest.json plus one grid file per layer.</summary>
    public static class ResortPackage
    {
        public const string ManifestFile = "manifest.json";

        static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Culture = CultureInfo.InvariantCulture,
            FloatFormatHandling = FloatFormatHandling.Symbol,
        };

        /// <summary>Writes a float32 layer and records it in the manifest.</summary>
        public static void AddLayer(string folder, PackageManifest manifest, string id, GridHeader header, float[] values)
        {
            string file = id + ".grid";
            using (var fs = File.Create(Path.Combine(folder, file))) GridFile.Write(fs, header, values);
            float min = float.MaxValue, max = float.MinValue;
            foreach (float v in values)
            {
                if (float.IsNaN(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            manifest.Layers.RemoveAll(l => l.Id == id);
            manifest.Layers.Add(new LayerInfo
            {
                Id = id, File = file, Type = "float32", Width = header.Width, Height = header.Height,
                West = header.West, North = header.North, CellSize = header.CellSize,
                Sha256 = GridFile.HashValues(values), Min = min, Max = max,
            });
        }

        /// <summary>Writes a uint8 layer (class codes, scaled heights, band-interleaved tables).</summary>
        public static void AddLayer(string folder, PackageManifest manifest, string id, GridHeader header, byte[] values, int bands = 1, string units = "")
        {
            if (header.Width % bands != 0) throw new ArgumentException("The file width must be a whole number of cells × bands.");
            string file = id + ".grid";
            using (var fs = File.Create(Path.Combine(folder, file))) GridFile.Write(fs, header, values);
            byte min = 255, max = 0;
            foreach (byte v in values)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            manifest.Layers.RemoveAll(l => l.Id == id);
            manifest.Layers.Add(new LayerInfo
            {
                Id = id, File = file, Type = "uint8", Width = header.Width / bands, Height = header.Height, Bands = bands, Units = units,
                West = header.West, North = header.North, CellSize = header.CellSize,
                Sha256 = GridFile.HashValues(values), Min = min, Max = max,
            });
        }

        /// <summary>
        /// The package id: SHA-256 over the format, the site definition and every layer's hash, in
        /// layer-id order. Timestamps and tool versions are excluded, so re-runs give the same id.
        /// </summary>
        public static string ComputeId(PackageManifest m)
        {
            var sb = new StringBuilder();
            sb.Append(m.FormatVersion).Append('|')
              .Append(m.Site.CentreX.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(m.Site.CentreY.ToString("R", CultureInfo.InvariantCulture)).Append('|')
              .Append(m.Site.SizeMetres).Append('|').Append(m.Site.RingMetres);
            foreach (var layer in m.Layers.OrderBy(l => l.Id, StringComparer.Ordinal))
                sb.Append('|').Append(layer.Id).Append('=').Append(layer.Sha256);
            foreach (var sp in m.Species.OrderBy(x => x.Index))
                sb.Append("|s").Append(sp.Index).Append('=').Append(sp.Spcd);
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(16);
                for (int i = 0; i < 8; i++) hex.Append(h[i].ToString("x2"));
                return hex.ToString();
            }
        }

        public static void WriteManifest(string folder, PackageManifest manifest)
        {
            manifest.PackageId = ComputeId(manifest);
            manifest.Layers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            File.WriteAllText(Path.Combine(folder, ManifestFile), JsonConvert.SerializeObject(manifest, Json) + "\n", new UTF8Encoding(false));
        }

        public static PackageManifest ReadManifest(string folder)
        {
            string path = Path.Combine(folder, ManifestFile);
            var manifest = JsonConvert.DeserializeObject<PackageManifest>(File.ReadAllText(path), Json)
                           ?? throw new InvalidDataException("Empty manifest.");
            if (manifest.FormatVersion != PackageManifest.CurrentFormat)
                throw new InvalidDataException($"Package format {manifest.FormatVersion} is not supported.");
            return manifest;
        }

        /// <summary>Reads a uint8 layer and verifies it against the manifest's hash.</summary>
        public static byte[] ReadByteLayer(string folder, PackageManifest manifest, string id, out GridHeader header)
        {
            var layer = manifest.Layers.FirstOrDefault(l => l.Id == id) ?? throw new KeyNotFoundException($"No layer '{id}'.");
            byte[] values;
            using (var fs = File.OpenRead(Path.Combine(folder, layer.File))) values = GridFile.ReadBytes(fs, out header);
            if (GridFile.HashValues(values) != layer.Sha256) throw new InvalidDataException($"Layer '{id}' doesn't match its hash.");
            return values;
        }

        /// <summary>Reads a float32 layer and verifies it against the manifest's hash.</summary>
        public static float[] ReadLayer(string folder, PackageManifest manifest, string id, out GridHeader header)
        {
            var layer = manifest.Layers.FirstOrDefault(l => l.Id == id) ?? throw new KeyNotFoundException($"No layer '{id}'.");
            float[] values;
            using (var fs = File.OpenRead(Path.Combine(folder, layer.File))) values = GridFile.ReadFloats(fs, out header);
            if (GridFile.HashValues(values) != layer.Sha256) throw new InvalidDataException($"Layer '{id}' doesn't match its hash.");
            return values;
        }
    }
}
