using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MountainPlanner.Domain.Design;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Persistence.Saves
{
    /// <summary>
    /// save.json, version 4 (task P3-04, v1; task P3-09, v2: each lift's terminal levels; v3: each tower's kind, LT44;
    /// task P3-15, v4: the terrain brush strokes; frozen, 0.3 §5). Written by hand in a fixed order, so the same save always gives
    /// the same bytes (SH-04): two-space indents, "\n" line ends, integers only, IDs and the seed as 16-digit hex,
    /// points as [x, y] in whole millimetres from the site's centre on the package grid's axes (TE5; never absolute
    /// EPSG:6350), and every enum as a fixed word that never follows a C# rename. A lift's "Levels" (v2, SK14) are the
    /// terminals' base elevations: <b>absolute</b> elevation in whole millimetres in the package's height datum, unlike
    /// the site-centred points; null where unknown (a lift from a v1 save). A tower's "Kind" (v3) is the head's kind the
    /// player chose: "auto" (the game's choice; every tower of a v1 or v2 save), "support", "hold-down" or "combination".
    /// The design's "Strokes" (v4, TB-14) are the confirmed terrain brush strokes, in ID order. "Kind" is "raise",
    /// "lower", "flatten", "smooth" or "slope"; "RadiusMm", "StepMm" and "LimitMm" are whole millimetres;
    /// "FalloffPermille", "StrengthPermille" and "GradePermille" are thousandths; "TargetMm" and "StartMm" are
    /// <b>absolute</b> elevations in whole millimetres in the package's height datum, like the terminal levels; fields a
    /// kind doesn't use are 0. "Dabs" is [x0, y0, w0, dx1, dy1, w1, …] on one line: the first dab's point in whole mm from
    /// the site's centre (TE5) and each later one's as the change from the one before, each with its weight in thousandths.
    /// <code>
    /// { "Version": 4, "Name", "Autosave", "SavedUtc", "GameVersion", "PackageId", "HeightsSha256",
    ///   "Design": { "Shape": 4, "Seed", "NextOrdinal", "Trails": [...],
    ///               "Lifts": [ { ..., "Towers": [ { "Id", "AlongMm", "HeightMm", "Kind" } ],
    ///                            "Levels": { "BottomMm", "TopMm" } or null } ],
    ///               "Strokes": [ { "Id", "Kind", "RadiusMm", "FalloffPermille", "StepMm", "StrengthPermille", "TargetMm",
    ///                              "StartMm", "GradePermille", "LimitMm", "Dabs": [...] } ] },
    ///   "Edits": { "TileMetres": 64, "Tiles": [ { "Column", "Row", "Sha256" } ] } }
    /// </code>
    /// </summary>
    public static class SaveJson
    {
        /// <summary>A terrain edit tile's place (its <c>EditTileKey</c>) and file, as save.json lists it.</summary>
        public readonly struct TileRef
        {
            public readonly int Column, Row;
            public readonly string Sha256;
            public TileRef(int column, int row, string sha256) { Column = column; Row = row; Sha256 = sha256; }
        }

        // ---------- writing ----------

        public static string Write(SaveFile save, IReadOnlyList<TileRef> edits)
        {
            var sw = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            using (var w = new JsonTextWriter(sw) { Formatting = Formatting.Indented, Indentation = 2, IndentChar = ' ', Culture = CultureInfo.InvariantCulture })
            {
                w.WriteStartObject();
                w.WritePropertyName("Version"); w.WriteValue(SaveFile.CurrentVersion);
                w.WritePropertyName("Name"); w.WriteValue(save.Name ?? "");
                w.WritePropertyName("Autosave"); w.WriteValue(save.Autosave);
                w.WritePropertyName("SavedUtc"); w.WriteValue(save.SavedUtc ?? "");
                w.WritePropertyName("GameVersion"); w.WriteValue(save.GameVersion ?? "");
                w.WritePropertyName("PackageId"); w.WriteValue(save.PackageId ?? "");
                w.WritePropertyName("HeightsSha256"); w.WriteValue(save.HeightsSha256 ?? "");
                w.WritePropertyName("Design"); WriteDesign(w, save.Design);
                w.WritePropertyName("Edits"); WriteTiles(w, edits);
                w.WriteEndObject();
            }
            return sw.ToString() + "\n";
        }

        static void WriteDesign(JsonWriter w, DesignDocument d)
        {
            w.WriteStartObject();
            w.WritePropertyName("Shape"); w.WriteValue(DesignDocument.ShapeVersion);
            w.WritePropertyName("Seed"); w.WriteValue(Hex(d.Seed));
            w.WritePropertyName("NextOrdinal"); w.WriteValue(d.NextOrdinal);
            w.WritePropertyName("Trails");
            w.WriteStartArray();
            foreach (var t in d.Trails) WriteTrail(w, t);
            w.WriteEndArray();
            w.WritePropertyName("Lifts");
            w.WriteStartArray();
            foreach (var l in d.Lifts) WriteLift(w, l);
            w.WriteEndArray();
            w.WritePropertyName("Strokes");
            w.WriteStartArray();
            foreach (var k in d.Strokes) WriteStroke(w, k);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        static void WriteStroke(JsonWriter w, TerrainStroke k)
        {
            var s = k.Settings;
            w.WriteStartObject();
            w.WritePropertyName("Id"); w.WriteValue(Hex(k.Id.Value));
            w.WritePropertyName("Kind"); w.WriteValue(Words.Brush(s.Kind));
            w.WritePropertyName("RadiusMm"); w.WriteValue(s.RadiusMm);
            w.WritePropertyName("FalloffPermille"); w.WriteValue(s.FalloffPermille);
            w.WritePropertyName("StepMm"); w.WriteValue(s.StepMm);
            w.WritePropertyName("StrengthPermille"); w.WriteValue(s.StrengthPermille);
            w.WritePropertyName("TargetMm"); w.WriteValue(s.TargetMm);
            w.WritePropertyName("StartMm"); w.WriteValue(s.StartMm);
            w.WritePropertyName("GradePermille"); w.WriteValue(s.GradePermille);
            w.WritePropertyName("LimitMm"); w.WriteValue(s.LimitMm);
            // [x0, y0, w0, dx1, dy1, w1, …] on one line: the first point absolute, the rest as changes (a stroke holds
            // hundreds of dabs a few metres apart).
            var sb = new StringBuilder("[");
            PointMm last = default;
            for (int i = 0; i < k.Dabs.Count; i++)
            {
                var d = k.Dabs[i];
                long x = i == 0 ? d.At.X : (long)d.At.X - last.X, y = i == 0 ? d.At.Y : (long)d.At.Y - last.Y;
                if (i > 0) sb.Append(", ");
                sb.Append(x.ToString(CultureInfo.InvariantCulture)).Append(", ").Append(y.ToString(CultureInfo.InvariantCulture))
                  .Append(", ").Append(d.WeightPermille.ToString(CultureInfo.InvariantCulture));
                last = d.At;
            }
            w.WritePropertyName("Dabs"); w.WriteRawValue(sb.Append(']').ToString());
            w.WriteEndObject();
        }

        static void WriteTrail(JsonWriter w, Trail t)
        {
            w.WriteStartObject();
            w.WritePropertyName("Id"); w.WriteValue(Hex(t.Id.Value));
            w.WritePropertyName("Name"); w.WriteValue(t.Name ?? "");
            w.WritePropertyName("Stage"); w.WriteValue(Words.Stage(t.Stage));
            w.WritePropertyName("Grading"); w.WriteValue(Words.Grading(t.Settings.Grading));
            w.WritePropertyName("Kind"); w.WriteValue(Words.Kind(t.Settings.Kind));
            w.WritePropertyName("Clearing"); w.WriteValue(Words.Clearing(t.Settings.Clearing));
            w.WritePropertyName("Head"); WriteOptPoint(w, t.Head);
            w.WritePropertyName("Tail"); WriteOptPoint(w, t.Tail);
            w.WritePropertyName("Parts");
            w.WriteStartArray();
            foreach (var part in t.Parts)
            {
                w.WriteStartObject();
                w.WritePropertyName("Strokes");
                w.WriteStartArray();
                foreach (var s in part.Strokes)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("WidthMm"); w.WriteValue(s.WidthMm);
                    w.WritePropertyName("Erase"); w.WriteValue(s.Erase);
                    // [x0, y0, x1, y1, …] on one line: a stroke has hundreds of points.
                    var sb = new StringBuilder("[");
                    for (int i = 0; i < s.Points.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(s.Points[i].X.ToString(CultureInfo.InvariantCulture)).Append(", ").Append(s.Points[i].Y.ToString(CultureInfo.InvariantCulture));
                    }
                    w.WritePropertyName("Points"); w.WriteRawValue(sb.Append(']').ToString());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WritePropertyName("Cuts");
                w.WriteStartArray();
                foreach (var c in part.Cuts)
                {
                    w.WriteStartObject();
                    w.WritePropertyName("At"); WritePoint(w, c.At);
                    w.WritePropertyName("Dir"); w.WriteRawValue(Pair(c.DirX, c.DirY));
                    w.WritePropertyName("KeepAhead"); w.WriteValue(c.KeepAhead);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        static void WriteLift(JsonWriter w, Lift l)
        {
            w.WriteStartObject();
            w.WritePropertyName("Id"); w.WriteValue(Hex(l.Id.Value));
            w.WritePropertyName("Name"); w.WriteValue(l.Name ?? "");
            w.WritePropertyName("Stage"); w.WriteValue(Words.Stage(l.Stage));
            w.WritePropertyName("Catalog"); w.WriteValue(l.CatalogId ?? "");
            w.WritePropertyName("Bottom"); WritePoint(w, l.Bottom);
            w.WritePropertyName("Top"); WritePoint(w, l.Top);
            w.WritePropertyName("TowerMode"); w.WriteValue(Words.Mode(l.TowerMode));
            w.WritePropertyName("Stance"); w.WriteValue(Words.Stance(l.Stance));
            w.WritePropertyName("Towers");
            w.WriteStartArray();
            foreach (var t in l.Towers)
            {
                w.WriteStartObject();
                w.WritePropertyName("Id"); w.WriteValue(Hex(t.Id.Value));
                w.WritePropertyName("AlongMm"); w.WriteValue(t.AlongMm);
                w.WritePropertyName("HeightMm"); w.WriteValue(t.HeightMm);
                w.WritePropertyName("Kind"); w.WriteValue(Words.Kind(t.Kind));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("Levels");
            if (l.Levels is LiftLevels v)
            {
                w.WriteStartObject();
                w.WritePropertyName("BottomMm"); w.WriteValue(v.BottomMm);
                w.WritePropertyName("TopMm"); w.WriteValue(v.TopMm);
                w.WriteEndObject();
            }
            else w.WriteNull();
            w.WriteEndObject();
        }

        static void WriteTiles(JsonWriter w, IReadOnlyList<TileRef> tiles)
        {
            w.WriteStartObject();
            w.WritePropertyName("TileMetres"); w.WriteValue(SaveFile.TileMetres);
            w.WritePropertyName("Tiles");
            w.WriteStartArray();
            foreach (var t in tiles)
            {
                w.WriteStartObject();
                w.WritePropertyName("Column"); w.WriteValue(t.Column);
                w.WritePropertyName("Row"); w.WriteValue(t.Row);
                w.WritePropertyName("Sha256"); w.WriteValue(t.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        static void WritePoint(JsonWriter w, PointMm p) => w.WriteRawValue(Pair(p.X, p.Y));

        static void WriteOptPoint(JsonWriter w, PointMm? p)
        {
            if (p.HasValue) WritePoint(w, p.Value);
            else w.WriteNull();
        }

        static string Pair(int a, int b) => "[" + a.ToString(CultureInfo.InvariantCulture) + ", " + b.ToString(CultureInfo.InvariantCulture) + "]";
        static string Hex(ulong v) => v.ToString("x16", CultureInfo.InvariantCulture);

        // ---------- reading ----------

        /// <summary>
        /// Reads save.json's text: the version first (newer → <see cref="FormatTooNewException"/>), then everything but the
        /// tile bytes. Anything malformed throws <see cref="InvalidDataException"/>; nothing half-reads.
        /// </summary>
        public static SaveFile Read(string json, out List<TileRef> edits)
        {
            JObject o;
            try { o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(json), out _); }
            catch (JsonException e) { throw new InvalidDataException("The save isn't readable JSON: " + e.Message, e); }
            try
            {
                var save = new SaveFile
                {
                    Name = Str(o, "Name"), Autosave = Bool(o, "Autosave"), SavedUtc = Str(o, "SavedUtc"),
                    GameVersion = Str(o, "GameVersion"), PackageId = Str(o, "PackageId"), HeightsSha256 = Str(o, "HeightsSha256"),
                    Design = ReadDesign(Obj(o, "Design")),
                };
                edits = ReadTiles(Obj(o, "Edits"));
                return save;
            }
            catch (Exception e) when (e is FormatException || e is ArgumentException || e is InvalidCastException || e is OverflowException || e is JsonException)
            {
                throw new InvalidDataException("The save is damaged: " + e.Message, e);
            }
        }

        /// <summary>Load Area's line for a save, without decoding the design: name, autosave, time and counts.</summary>
        public static SaveSummary Summary(string json)
        {
            JObject o;
            try { o = SaveFile.Migrations.Upgrade(VersionedJson.Parse(json), out _); }
            catch (JsonException e) { throw new InvalidDataException("The save isn't readable JSON: " + e.Message, e); }
            try
            {
                var d = Obj(o, "Design");
                return new SaveSummary
                {
                    Name = Str(o, "Name"), Autosave = Bool(o, "Autosave"), SavedUtc = Str(o, "SavedUtc"),
                    Trails = Arr(d, "Trails").Count, Lifts = Arr(d, "Lifts").Count,
                };
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is JsonException)
            {
                throw new InvalidDataException("The save is damaged: " + e.Message, e);
            }
        }

        static DesignDocument ReadDesign(JObject d)
        {
            int shape = Int(d, "Shape");
            if (shape != DesignDocument.ShapeVersion) throw new FormatException($"design shape {shape} isn't readable here");
            ulong seed = HexValue(Str(d, "Seed"));
            long next = Long(d, "NextOrdinal");
            var trails = new List<Trail>();
            foreach (var t in Arr(d, "Trails")) trails.Add(ReadTrail(AsObj(t)));
            var lifts = new List<Lift>();
            foreach (var l in Arr(d, "Lifts")) lifts.Add(ReadLift(AsObj(l)));
            var strokes = new List<TerrainStroke>();
            foreach (var k in Arr(d, "Strokes")) strokes.Add(ReadStroke(AsObj(k)));
            return DesignDocument.From(seed, next, trails, lifts, strokes);
        }

        static TerrainStroke ReadStroke(JObject k)
        {
            var settings = new BrushSettings(Words.Brush(Str(k, "Kind")), Int(k, "RadiusMm"), Int(k, "FalloffPermille"), Int(k, "StepMm"),
                Int(k, "StrengthPermille"), Int(k, "TargetMm"), Int(k, "StartMm"), Int(k, "GradePermille"), Int(k, "LimitMm"));
            var flat = Arr(k, "Dabs");
            if (flat.Count % 3 != 0) throw new FormatException("a stroke's dabs aren't triples");
            if (flat.Count / 3 > DesignRules.MaxDabsPerStroke) throw new FormatException("a stroke has too many dabs");
            var dabs = new BrushDab[flat.Count / 3];
            long x = 0, y = 0;
            for (int i = 0; i < dabs.Length; i++)
            {
                x = i == 0 ? IntOf(flat[0]) : checked(x + IntOf(flat[3 * i]));
                y = i == 0 ? IntOf(flat[1]) : checked(y + IntOf(flat[3 * i + 1]));
                dabs[i] = new BrushDab(new PointMm(checked((int)x), checked((int)y)), IntOf(flat[3 * i + 2]));
            }
            return new TerrainStroke(new TerrainStrokeId(HexValue(Str(k, "Id"))), settings, dabs);
        }

        static Trail ReadTrail(JObject t)
        {
            var parts = new List<FootprintPart>();
            foreach (var p in Arr(t, "Parts"))
            {
                var po = AsObj(p);
                var strokes = new List<Stroke>();
                foreach (var s in Arr(po, "Strokes"))
                {
                    var so = AsObj(s);
                    var flat = Arr(so, "Points");
                    if (flat.Count % 2 != 0) throw new FormatException("a stroke has an odd number of coordinates");
                    var points = new PointMm[flat.Count / 2];
                    for (int i = 0; i < points.Length; i++) points[i] = new PointMm(IntOf(flat[2 * i]), IntOf(flat[2 * i + 1]));
                    strokes.Add(new Stroke(Int(so, "WidthMm"), Bool(so, "Erase"), points));
                }
                var cuts = new List<Cut>();
                foreach (var c in Arr(po, "Cuts"))
                {
                    var co = AsObj(c);
                    var dir = Arr(co, "Dir");
                    if (dir.Count != 2) throw new FormatException("a cut's direction isn't two numbers");
                    cuts.Add(new Cut(Point(co, "At"), IntOf(dir[0]), IntOf(dir[1]), Bool(co, "KeepAhead")));
                }
                parts.Add(new FootprintPart(strokes, cuts));
            }
            var settings = new TrailSettings(Words.Grading(Str(t, "Grading")), Words.Kind(Str(t, "Kind")), Words.Clearing(Str(t, "Clearing")));
            return new Trail(new TrailId(HexValue(Str(t, "Id"))), Str(t, "Name"), Words.Stage(Str(t, "Stage")), settings, parts,
                OptPoint(t, "Head"), OptPoint(t, "Tail"));
        }

        static Lift ReadLift(JObject l)
        {
            var towers = new List<Tower>();
            foreach (var x in Arr(l, "Towers"))
            {
                var to = AsObj(x);
                towers.Add(new Tower(new TowerId(HexValue(Str(to, "Id"))), Int(to, "AlongMm"), Int(to, "HeightMm"), Words.TowerKind(Str(to, "Kind"))));
            }
            return new Lift(new LiftId(HexValue(Str(l, "Id"))), Str(l, "Name"), Words.Stage(Str(l, "Stage")), Str(l, "Catalog"),
                Point(l, "Bottom"), Point(l, "Top"), Words.Mode(Str(l, "TowerMode")), Words.Stance(Str(l, "Stance")), towers, Levels(l));
        }

        static LiftLevels? Levels(JObject l)
        {
            var t = Field(l, "Levels");
            if (t.Type == JTokenType.Null) return null;
            var o = AsObj(t);
            return new LiftLevels(Int(o, "BottomMm"), Int(o, "TopMm"));
        }

        static List<TileRef> ReadTiles(JObject o)
        {
            int size = Int(o, "TileMetres");
            if (size != SaveFile.TileMetres) throw new FormatException($"tiles of {size} m aren't readable here");
            var list = new List<TileRef>();
            var seen = new HashSet<long>();
            foreach (var t in Arr(o, "Tiles"))
            {
                var to = AsObj(t);
                var r = new TileRef(Int(to, "Column"), Int(to, "Row"), Str(to, "Sha256"));
                if (!SaveTileFile.IsHash(r.Sha256)) throw new FormatException("a tile's hash isn't 64 hex digits");
                if (!seen.Add(((long)r.Row << 32) | (uint)r.Column)) throw new FormatException("a tile is listed twice");
                list.Add(r);
            }
            return list;
        }

        // Strict accessors: a missing or mistyped field is damage, never a default.

        static JToken Field(JObject o, string name) => o[name] ?? throw new FormatException($"'{name}' is missing");
        static JObject Obj(JObject o, string name) => AsObj(Field(o, name));
        static JObject AsObj(JToken t) => t as JObject ?? throw new FormatException("an object was expected");
        static JArray Arr(JObject o, string name) => Field(o, name) as JArray ?? throw new FormatException($"'{name}' isn't a list");

        static string Str(JObject o, string name)
        {
            var t = Field(o, name);
            if (t.Type != JTokenType.String) throw new FormatException($"'{name}' isn't text");
            return (string)t;
        }

        static bool Bool(JObject o, string name)
        {
            var t = Field(o, name);
            if (t.Type != JTokenType.Boolean) throw new FormatException($"'{name}' isn't true or false");
            return (bool)t;
        }

        static long Long(JObject o, string name)
        {
            var t = Field(o, name);
            if (t.Type != JTokenType.Integer) throw new FormatException($"'{name}' isn't a whole number");
            return (long)t;
        }

        static int Int(JObject o, string name) => checked((int)Long(o, name));

        static int IntOf(JToken t)
        {
            if (t.Type != JTokenType.Integer) throw new FormatException("a coordinate isn't a whole number");
            return checked((int)(long)t);
        }

        static PointMm Point(JObject o, string name)
        {
            var a = Arr(o, name);
            if (a.Count != 2) throw new FormatException($"'{name}' isn't a point");
            return new PointMm(IntOf(a[0]), IntOf(a[1]));
        }

        static PointMm? OptPoint(JObject o, string name) => Field(o, name).Type == JTokenType.Null ? (PointMm?)null : Point(o, name);

        static ulong HexValue(string s)
        {
            if (s.Length != 16) throw new FormatException($"'{s}' isn't a 16-digit hex id");
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) throw new FormatException($"'{s}' isn't a 16-digit hex id");
            return ulong.Parse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        /// <summary>The frozen words for each enum, both ways.</summary>
        static class Words
        {
            public static string Stage(WorkStage v) => v == WorkStage.Plan ? "plan" : v == WorkStage.Built ? "built" : throw Bad(v);
            public static WorkStage Stage(string s) => s == "plan" ? WorkStage.Plan : s == "built" ? WorkStage.Built : throw Unknown(s);
            public static string Grading(TrailGrading v) => v == TrailGrading.Graded ? "graded" : v == TrailGrading.Natural ? "natural" : throw Bad(v);
            public static TrailGrading Grading(string s) => s == "graded" ? TrailGrading.Graded : s == "natural" ? TrailGrading.Natural : throw Unknown(s);

            public static string Kind(TrailKind v) => v switch { TrailKind.Run => "run", TrailKind.Glade => "glade", TrailKind.Zone => "zone", _ => throw Bad(v) };
            public static TrailKind Kind(string s) => s switch { "run" => TrailKind.Run, "glade" => TrailKind.Glade, "zone" => TrailKind.Zone, _ => throw Unknown(s) };

            public static string Clearing(TrailClearing v) => v switch { TrailClearing.Clear => "clear", TrailClearing.Thin => "thin", TrailClearing.None => "none", _ => throw Bad(v) };
            public static TrailClearing Clearing(string s) => s switch { "clear" => TrailClearing.Clear, "thin" => TrailClearing.Thin, "none" => TrailClearing.None, _ => throw Unknown(s) };

            public static string Mode(TowerMode v) => v == TowerMode.Automatic ? "automatic" : v == TowerMode.ByHand ? "by-hand" : throw Bad(v);
            public static TowerMode Mode(string s) => s == "automatic" ? TowerMode.Automatic : s == "by-hand" ? TowerMode.ByHand : throw Unknown(s);
            public static string Stance(TowerStance v) => v == TowerStance.Plumb ? "plumb" : v == TowerStance.Leaned ? "leaned" : throw Bad(v);
            public static TowerStance Stance(string s) => s == "plumb" ? TowerStance.Plumb : s == "leaned" ? TowerStance.Leaned : throw Unknown(s);
            public static string Kind(TowerKind v) => v switch
            {
                Domain.Design.TowerKind.Auto => "auto", Domain.Design.TowerKind.Support => "support",
                Domain.Design.TowerKind.HoldDown => "hold-down", Domain.Design.TowerKind.Combination => "combination", _ => throw Bad(v),
            };
            public static TowerKind TowerKind(string s) => s switch
            {
                "auto" => Domain.Design.TowerKind.Auto, "support" => Domain.Design.TowerKind.Support,
                "hold-down" => Domain.Design.TowerKind.HoldDown, "combination" => Domain.Design.TowerKind.Combination, _ => throw Unknown(s),
            };

            public static string Brush(BrushKind v) => v switch
            {
                BrushKind.Raise => "raise", BrushKind.Lower => "lower", BrushKind.Flatten => "flatten", BrushKind.Smooth => "smooth",
                BrushKind.Slope => "slope", _ => throw Bad(v),
            };
            public static BrushKind Brush(string s) => s switch
            {
                "raise" => BrushKind.Raise, "lower" => BrushKind.Lower, "flatten" => BrushKind.Flatten, "smooth" => BrushKind.Smooth,
                "slope" => BrushKind.Slope, _ => throw Unknown(s),
            };

            static Exception Bad(object v) => new ArgumentException($"{v.GetType().Name} {v} has no saved word.");
            static Exception Unknown(string s) => new FormatException($"'{s}' isn't a known value");
        }
    }
}
