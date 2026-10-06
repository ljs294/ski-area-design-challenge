using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// A file written by a newer version of the game (T11, 0.3 §5). It is refused, never misread, and the file is
    /// left as it is: nothing in this version writes over it. An <see cref="IOException"/>, so code that already skips
    /// unreadable files skips these too.
    /// </summary>
    public sealed class FormatTooNewException : IOException
    {
        public string Format { get; }
        public int Found { get; }
        public int Supported { get; }

        public FormatTooNewException(string format, int found, int supported)
            : base($"This {format} was saved by a newer version of Mountain Planner (format {found}; this version reads up to {supported}). Update the game to open it.")
        {
            Format = format;
            Found = found;
            Supported = supported;
        }
    }

    /// <summary>
    /// One frozen JSON format (task 08, T11): its integer version field, the current version, and one migration per
    /// step. A file is read as raw JSON first, so the version comes from the file itself. A missing version is v1 (the
    /// Phase 1 files), never "current". A newer version is refused with <see cref="FormatTooNewException"/>. An older
    /// one is upgraded a step at a time on the raw JSON before it becomes an object, so old shapes never have to
    /// deserialise into new classes.
    /// </summary>
    public sealed class VersionedJson
    {
        /// <summary>The version a file without a version field is read as: the Phase 1 files.</summary>
        public const int FirstVersion = 1;

        public string Format { get; }
        public string VersionField { get; }
        public int Current { get; }
        readonly IReadOnlyList<Action<JObject>> _steps;

        /// <param name="format">What the file is, in words, for messages ("area package", "view state").</param>
        /// <param name="steps">steps[i] turns version i + 1 into version i + 2; the field itself is bumped here.</param>
        public VersionedJson(string format, string versionField, int current, params Action<JObject>[] steps)
        {
            if (current < FirstVersion) throw new ArgumentOutOfRangeException(nameof(current));
            if (steps.Length != current - FirstVersion)
                throw new ArgumentException($"{format}: version {current} needs {current - FirstVersion} migration step(s), not {steps.Length}.", nameof(steps));
            Format = format;
            VersionField = versionField;
            Current = current;
            _steps = steps;
        }

        /// <summary>Parses JSON without letting Newtonsoft turn date-like strings into dates (they must stay as written).</summary>
        public static JObject Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, Culture = CultureInfo.InvariantCulture })
            {
                var token = JToken.ReadFrom(reader);
                while (reader.Read())
                    if (reader.TokenType != JsonToken.Comment) throw new JsonReaderException("Additional text after the JSON object.");
                return token as JObject ?? throw new InvalidDataException("Not a JSON object.");
            }
        }

        /// <summary>The file's own version: <see cref="FirstVersion"/> when the field is missing.</summary>
        public int VersionOf(JObject o)
        {
            var token = o[VersionField];
            if (token == null || token.Type == JTokenType.Null) return FirstVersion;
            if (token.Type != JTokenType.Integer) throw new InvalidDataException($"The {Format}'s {VersionField} isn't a whole number.");
            long v = token.Value<long>();
            if (v < FirstVersion || v > int.MaxValue) throw new InvalidDataException($"The {Format}'s {VersionField} {v} isn't a version.");
            return (int)v;
        }

        /// <summary>
        /// True when the file at <paramref name="path"/> is readable JSON from a newer version; such a file is never
        /// overwritten. An unreadable file, or one whose version is nonsense, isn't newer: saving replaces it.
        /// </summary>
        public bool IsNewer(string path)
        {
            if (!File.Exists(path)) return false;
            try { return VersionOf(Parse(File.ReadAllText(path))) > Current; }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is JsonException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>Brings <paramref name="o"/> up to <see cref="Current"/> in place; <paramref name="from"/> is the version it had.</summary>
        public JObject Upgrade(JObject o, out int from)
        {
            from = VersionOf(o);
            if (from > Current) throw new FormatTooNewException(Format, from, Current);
            for (int v = from; v < Current; v++)
            {
                _steps[v - FirstVersion](o);
                o[VersionField] = v + 1;
            }
            if (o[VersionField] == null) o[VersionField] = Current;
            return o;
        }

        /// <summary>Reads, upgrades and deserialises a file's text.</summary>
        public T Read<T>(string json, JsonSerializerSettings settings, out int from) where T : class
        {
            var o = Upgrade(Parse(json), out from);
            var serializer = JsonSerializer.Create(settings ?? new JsonSerializerSettings());
            serializer.DateParseHandling = DateParseHandling.None;
            return o.ToObject<T>(serializer) ?? throw new InvalidDataException($"Empty {Format}.");
        }

        public T Read<T>(string json, JsonSerializerSettings settings = null) where T : class => Read<T>(json, settings, out _);
    }

    /// <summary>Small JSON records written whole: to a temporary file first, then moved over the old one.</summary>
    static class AtomicFile
    {
        public static void WriteJson(string path, object value, JsonSerializerSettings settings = null)
        {
            string temp = path + ".tmp";
            string json = settings == null ? JsonConvert.SerializeObject(value, Formatting.Indented) : JsonConvert.SerializeObject(value, settings);
            File.WriteAllText(temp, json + "\n", new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }
}
