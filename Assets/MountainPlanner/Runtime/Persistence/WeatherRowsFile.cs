using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// <c>weather.rows</c> (WX-17 v2, 0.3 §5): the calibration rows as bytes, frozen like the manifest. Layout, little-endian:
    /// the magic "MPWR", the version (int32), then every field in <see cref="WeatherRows.Visit"/>'s order: a scalar as int32,
    /// an array as its length (int32) then its values (int32, or int64 for the Q32 rows). The file's SHA-256 (hex) is
    /// <see cref="WeatherInfo.RowsSha256"/>. A new field, a resized array or a changed unit bumps <see cref="Version"/> and
    /// adds a <c>TestData/formats</c> fixture; the v1 fixture stays unedited.
    /// </summary>
    public static class WeatherRowsFile
    {
        public const string FileName = "weather.rows";
        public const int Version = 1;
        static readonly byte[] Magic = { (byte)'M', (byte)'P', (byte)'W', (byte)'R' };

        public static byte[] ToBytes(WeatherRows rows)
        {
            if (rows == null) throw new ArgumentNullException(nameof(rows));
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    w.Write(Magic);
                    w.Write(Version);
                    rows.Visit(new Writer(w));
                }
                return ms.ToArray();
            }
        }

        /// <summary>Reads rows written by <see cref="ToBytes"/>; throws <see cref="InvalidDataException"/> on a bad magic,
        /// an unknown version, a resized array, or bytes left over.</summary>
        public static WeatherRows FromBytes(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var rows = new WeatherRows();
            using (var ms = new MemoryStream(bytes, false))
            using (var r = new BinaryReader(ms, Encoding.UTF8, true))
            {
                try
                {
                    byte[] magic = r.ReadBytes(4);
                    if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                        throw new InvalidDataException($"{FileName} has no MPWR header.");
                    int version = r.ReadInt32();
                    if (version != Version) throw new InvalidDataException($"{FileName} is version {version}; this build reads version {Version}.");
                    rows.Visit(new Reader(r));
                }
                catch (EndOfStreamException)
                {
                    throw new InvalidDataException($"{FileName} is truncated.");
                }
                if (ms.Position != ms.Length) throw new InvalidDataException($"{FileName} has {ms.Length - ms.Position} bytes past its last field.");
            }
            return rows;
        }

        /// <summary>Writes the rows into a package folder; returns the file's SHA-256 (hex) for the manifest.</summary>
        public static string Write(string folder, WeatherRows rows)
        {
            byte[] bytes = ToBytes(rows);
            string path = Path.Combine(folder, FileName);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
            return Sha256(bytes);
        }

        /// <summary>Reads the rows from a package folder and checks them against the manifest's hash (if one is given).</summary>
        public static WeatherRows Read(string folder, string expectedSha256 = null)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(folder, FileName));
            if (!string.IsNullOrEmpty(expectedSha256) && !string.Equals(Sha256(bytes), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{FileName} is corrupt (its contents don't match the manifest).");
            return FromBytes(bytes);
        }

        public static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(bytes);
                var sb = new StringBuilder(64);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        sealed class Writer : IWeatherRowsVisitor
        {
            readonly BinaryWriter _w;
            public Writer(BinaryWriter w) => _w = w;
            public void Int(ref int value) => _w.Write(value);
            public void Ints(int[] values) { _w.Write(values.Length); foreach (int v in values) _w.Write(v); }
            public void Longs(long[] values) { _w.Write(values.Length); foreach (long v in values) _w.Write(v); }
        }

        sealed class Reader : IWeatherRowsVisitor
        {
            readonly BinaryReader _r;
            public Reader(BinaryReader r) => _r = r;
            public void Int(ref int value) => value = _r.ReadInt32();
            public void Ints(int[] values) { Length(values.Length); for (int i = 0; i < values.Length; i++) values[i] = _r.ReadInt32(); }
            public void Longs(long[] values) { Length(values.Length); for (int i = 0; i < values.Length; i++) values[i] = _r.ReadInt64(); }

            void Length(int expected)
            {
                int n = _r.ReadInt32();
                if (n != expected) throw new InvalidDataException($"{FileName} has an array of {n} where this version has {expected}.");
            }
        }
    }
}
