using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Water;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.14 (LK-10, owner finding F1; MapLibre's lake and stream panels, re-specified): with no tool armed, the water
    /// under the pointer lights (a dam, then a pond, then a stream line within 8 pixels, then a lake), and a click opens its
    /// card: name (rename with a click or F2), facts, and what can be changed there (a lake's depth and snowmaking switch, a
    /// stream's width, a pond's or dam's Edit and Remove). Named water gets a label on the map. Lifts and trails pick first.
    /// </summary>
    public sealed partial class MountainViewer
    {
        const int WaterHoverSlot = 900, WaterCardSlot = 940, WaterSlots = 32;
        // The hover works on a struct key, so a pointer moving over water allocates nothing (CF-09: demo 41 caught the old
        // "lake:…" strings); the card's text key is made only when a click opens one.
        readonly struct WaterKey : IEquatable<WaterKey>
        {
            public readonly string Kind;   // one of the interned literals below, never built
            public readonly ulong Id;
            public WaterKey(string kind, ulong id) { Kind = kind; Id = id; }
            public bool IsNone => Kind == null;
            public bool Equals(WaterKey o) => ReferenceEquals(Kind, o.Kind) && Id == o.Id;
            public override bool Equals(object o) => o is WaterKey k && Equals(k);
            public override int GetHashCode() => (Kind?.Length ?? 0) * 31 + Id.GetHashCode();
            public override string ToString() => IsNone ? null : Kind + ":" + Id.ToString("x16", CultureInfo.InvariantCulture);
            public static WaterKey From(string key) => Parse(key, out string kind, out ulong id) ? new WaterKey(Interned(kind), id) : default;
            static string Interned(string kind) => kind switch { "dam" => KindDam, "pond" => KindPond, "stream" => KindStream, "lake" => KindLake, _ => null };
        }
        const string KindDam = "dam", KindPond = "pond", KindStream = "stream", KindLake = "lake";

        WaterKey _waterHoverKey, _waterPressKey;
        string _waterCardDrawn;
        Vector2 _waterPress;
        bool _waterPressed, _waterCardWired;
        long _waterCardRevision = -1;
        string _waterCardOpened;   // the card this viewer opened: only that one follows the design
        int _waterHoverLoops, _waterCardLoops;
        readonly Dictionary<WaterKey, List<Vector2[]>> _waterOutlines = new Dictionary<WaterKey, List<Vector2[]>>();
        DesignDocument _waterOutlinesFor;
        LandmarkLabelOverlay _waterLabels;
        DesignDocument _waterLabelsFor;
        object _waterLabelsArea;
        readonly List<(string Name, Vector3 At)> _waterLabelSpots = new List<(string, Vector3)>();

        void UpdateWaterHover()
        {
            if (Hud == null || _resort == null) return;
            WireWaterCard();
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var doc = Design?.Document;
            if (!ReferenceEquals(doc, _waterOutlinesFor)) { _waterOutlines.Clear(); _waterOutlinesFor = doc; _waterHoverKey = default; _waterCardDrawn = null; }
            bool picking = cam != null && mouse != null && !_scriptedRun && doc != null && !_networkOn && !PhotoOpen && (_tools == null || !_tools.Armed)
                           && _hoveredId == 0 && _hoveredTrailRow == 0;
            WaterKey hit = default;
            Vector2 pos = mouse != null ? mouse.position.ReadValue() : default;
            if (picking && !PointerOverUi(pos)) hit = WaterKeyUnder(cam, pos, doc);
            if (!hit.Equals(_waterHoverKey))
            {
                _waterHoverKey = hit;
                _waterHoverLoops = DrawWaterOutline(WaterHoverSlot, _waterHoverLoops, hit, PreviewInk.Line);
            }
            string card = Hud.WaterCardKey;
            if (card != _waterCardDrawn)
            {
                _waterCardDrawn = card;
                _waterCardLoops = DrawWaterOutline(WaterCardSlot, _waterCardLoops, WaterKey.From(card), PreviewInk.Snap);
            }
            // The open card follows the design (an undo, a rename, a change from its own buttons).
            if (card != null && card == _waterCardOpened && Design != null && Design.Revision != _waterCardRevision) ShowWaterCardFor(card);
            UpdateWaterLabels(cam, doc);

            if (!picking) { _waterPressed = false; return; }
            if (mouse.leftButton.wasPressedThisFrame && !PointerOverUi(pos) && !SkierTookPress)
            {
                _waterPressed = !hit.IsNone;
                _waterPress = pos;
                _waterPressKey = hit;
            }
            if (!_waterPressed || !mouse.leftButton.wasReleasedThisFrame) return;
            _waterPressed = false;
            if ((pos - _waterPress).sqrMagnitude <= 36 && hit.Equals(_waterPressKey)) ShowWaterCardFor(hit.ToString());
        }

        void WireWaterCard()
        {
            if (_waterCardWired) return;
            _waterCardWired = true;
            Hud.WaterRenamed = RenameWater;
            Hud.WaterCardAction += WaterCardAction;
        }

        // ---------------- what's under the pointer ----------------

        WaterKey WaterKeyUnder(UnityEngine.Camera cam, Vector2 screen, DesignDocument doc)
        {
            var ray = cam.ScreenPointToRay(screen);
            float along = Camera.GroundAlong(ray, 30000);
            if (float.IsNaN(along)) return default;
            var g = ray.GetPoint(along);
            var p = PointMm.FromMetres(g.x, g.z);
            ulong id = WaterPick.Under(doc, Ground, p);
            if (id != 0) return new WaterKey(doc.FindDam(new DamId(id)) != null ? KindDam : KindPond, id);
            float reach = Mathf.Max(5f, 8 * along * 2 * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, cam.pixelHeight));
            var s = NearestStream(new Vector2(g.x, g.z), reach);
            if (s != null && s.Line.Way != 0) return new WaterKey(KindStream, s.Line.Way);
            var lake = LakeAt(p);
            return lake.HasValue ? new WaterKey(KindLake, lake.Value.Id) : default;
        }

        static bool Parse(string key, out string kind, out ulong id)
        {
            kind = null;
            id = 0;
            int colon = key?.IndexOf(':') ?? -1;
            if (colon < 0) return false;
            kind = key.Substring(0, colon);
            return ulong.TryParse(key.Substring(colon + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out id);
        }

        StreamPath StreamOf(ulong way)
        {
            foreach (var s in Streams() ?? new List<StreamPath>()) if (s.Line.Way == way) return s;
            return null;
        }

        int LakeBody(ulong id)
        {
            var t = LakesOnly;
            if (t == null) return -1;
            int i = t.IndexOf(id);
            return i > WaterBodyTable.Streams && t[i].Kind == WaterBodyKind.Lake ? i : -1;
        }

        // ---------------- outlines ----------------

        /// <summary>Lights <paramref name="key"/>'s outline from preview slot <paramref name="slot"/>; returns the loops drawn (the
        /// ones past them, from an earlier key, are hidden).</summary>
        int DrawWaterOutline(int slot, int before, WaterKey key, PreviewInk ink)
        {
            var preview = Preview;
            if (preview == null) return 0;
            var loops = !key.IsNone ? OutlineOf(key) : null;
            int n = loops == null ? 0 : Math.Min(loops.Count, WaterSlots);
            for (int k = 0; k < n; k++)
            {
                bool closed = !ReferenceEquals(key.Kind, KindStream) && !(ReferenceEquals(key.Kind, KindDam) && k == 0);
                preview.Line(slot + k, loops[k], loops[k].Length, ink, closed);
            }
            for (int k = n; k < before; k++) preview.Hide(slot + k);
            return n;
        }

        List<Vector2[]> OutlineOf(WaterKey key)
        {
            if (_waterOutlines.TryGetValue(key, out var have)) return have;
            var loops = new List<Vector2[]>();
            var doc = Design?.Document;
            ulong id = key.Id;
            if (doc != null)
            {
                switch (key.Kind)
                {
                    case "pond":
                        if (doc.FindPond(new PondId(id)) is Pond pond) loops.Add(pond.Ring.Select(q => new Vector2((float)q.XMetres, (float)q.YMetres)).ToArray());
                        break;
                    case "dam":
                        if (doc.FindDam(new DamId(id)) is Dam dam)
                        {
                            loops.Add(new[] { new Vector2((float)dam.BankA.XMetres, (float)dam.BankA.YMetres), new Vector2((float)dam.BankB.XMetres, (float)dam.BankB.YMetres) });
                            if (Ground != null && Ground.Ready && Ground.Earthwork.DetailOf(id) is DamBuild b && b.Wet != null) loops.AddRange(WaterPreview.Outline(b.Wet, Ground.Earthwork.Site));
                        }
                        break;
                    case "stream":
                        if (StreamOf(id) is StreamPath s) loops.Add(s.Xz);
                        break;
                    case "lake":
                        int body = LakeBody(id);
                        var t = LakesOnly;
                        if (body > 0)
                        {
                            float ox = t.OriginXMm / 1000f, oy = t.OriginYMm / 1000f;
                            const float cell = WaterBodyTable.CellMetres;
                            foreach (var loop in WaterOutline.Trace(0, 0, t.Width - 1, t.Depth - 1, (i, j) => t.BodyAt(j * t.Width + i) == body))
                            {
                                var xz = new Vector2[loop.Length / 2];
                                for (int k = 0; k < xz.Length; k++) xz[k] = new Vector2(ox + loop[2 * k] * cell, oy + loop[2 * k + 1] * cell);
                                loops.Add(xz);
                            }
                            loops.Sort((a, c) => c.Length.CompareTo(a.Length));   // the shore first, then the islands
                        }
                        break;
                }
            }
            _waterOutlines[key] = loops;
            return loops;
        }

        /// <summary>A reservoir's shore: the outline of its wet lattice points, in local metres (rows run south).</summary>
        // ---------------- the card ----------------

        static string Hex(ulong id) => id.ToString("x16", CultureInfo.InvariantCulture);

        void ShowWaterCardFor(string key)
        {
            var doc = Design?.Document;
            _waterCardRevision = Design?.Revision ?? -1;
            if (doc == null || !Parse(key, out string kind, out ulong id) || !(CardFor(doc, kind, id, key) is WaterCardModel m)) { Hud.CloseWaterCardIfOpen(); return; }
            Hud.ShowWaterCard(m);
            _waterCardOpened = key;
        }

        WaterCardModel? CardFor(DesignDocument doc, string kind, ulong id, string key)
        {
            var rows = new List<(string, string)>();
            var acts = new List<(string, string, bool)>();
            switch (kind)
            {
                case "lake":
                {
                    int body = LakeBody(id);
                    if (body < 0) return null;
                    var b = LakesOnly[body];
                    var rec = doc.FindLake(id);
                    string osm = OsmLakeName(id);
                    double depthM = (rec.DepthTenthsFt > 0 ? rec.DepthTenthsFt : b.DepthTenthsFt) / 10.0 * 0.3048;
                    var w = WaterCardRows.Empty();
                    w.LevelMetres = b.SurfaceFeet * 0.3048;
                    w.FullPoolMetres = w.LevelMetres;
                    w.FullPoolNote = "its natural level";
                    w.PercentFull = 100;
                    w.AreaSquareMetres = b.AreaSquareMetres;
                    w.MeanDepthMetres = depthM;
                    w.DepthNote = rec.DepthTenthsFt > 0 ? "yours" : "estimate";
                    w.VolumeCubicMetres = b.AreaSquareMetres * depthM;
                    w.Fills = "naturally";
                    w.Snowmaking = rec.Source ? $"a source · draws down 3 ft at most ({ToolFormat.Water(b.AreaSquareMetres * 0.9144)})" : "not a source";
                    WaterCardRows.Add(rows, w);
                    acts.Add(("source", rec.Source ? "Snowmaking source ✓" : "Use for snowmaking", rec.Source));
                    acts.Add(("shallower", "Depth −1 ft", false));
                    acts.Add(("deeper", "Depth +1 ft", false));
                    if (rec.DepthTenthsFt > 0) acts.Add(("estimate", "Use the estimate", false));
                    if (rec.Name.Length > 0 && osm.Length > 0) acts.Add(("osmname", "Restore the map's name", false));
                    return new WaterCardModel
                    {
                        Key = key, Kind = "Lake", Name = rec.Name.Length > 0 ? rec.Name : osm.Length > 0 ? osm : "Lake", Renamable = true,
                        Rows = rows.ToArray(), Actions = acts.ToArray(),
                        Hint = WaterCardRows.Hint(null, b.Clipped ? "Clipped at the area's edge: these figures count the part inside." : null, osm.Length > 0),
                    };
                }
                case "stream":
                {
                    var s = StreamOf(id);
                    if (s == null) return null;
                    var rec = doc.FindStream(id);
                    double widthM = rec.WidthCm > 0 ? rec.WidthCm / 100.0 : s.Line.WidthMetres;
                    string from = rec.WidthCm > 0 ? "yours" : s.Line.WidthFromOsm ? "from the map" : "the class default";
                    double length = 0;
                    for (int i = 1; i < s.Xz.Length; i++) length += Vector2.Distance(s.Xz[i - 1], s.Xz[i]);
                    double cfs = StreamFlow.BaseCfs(widthM / 0.3048);
                    var dams = doc.Dams.Where(d => d.StreamWay == id).Select(d => d.Name).ToList();
                    // The standing cards' formats: lengths and widths as theirs, a note after a dot, the season after a dot.
                    rows.Add(("Class", s.Line.Class));
                    rows.Add(("Length", ToolFormat.Length(length) + " · in this area"));
                    rows.Add(("Width", $"{ToolFormat.Depth(widthM)} · {from}"));
                    rows.Add(("Flow", $"{ToolFormat.Flow(cfs)} · Dec–Feb {ToolFormat.Flow(cfs * StreamFlow.SeasonFactor(1))}"));
                    rows.Add((WaterCardRows.Snowmaking, dams.Count == 0 ? "dam it to use it" : "through " + string.Join(", ", dams)));
                    acts.Add(("narrower", "Width −1 ft", false));
                    acts.Add(("wider", "Width +1 ft", false));
                    if (rec.WidthCm > 0) acts.Add(("mapwidth", "Use the map's width", false));
                    if (rec.Name.Length > 0 && s.Line.Name.Length > 0) acts.Add(("osmname", "Restore the map's name", false));
                    return new WaterCardModel
                    {
                        Key = key, Kind = "Stream", Name = rec.Name.Length > 0 ? rec.Name : s.Line.Name.Length > 0 ? s.Line.Name : "Unnamed stream", Renamable = true,
                        Rows = rows.ToArray(), Actions = acts.ToArray(),
                        Hint = WaterCardRows.Hint(null, "Every dam on this stream takes its width.", true, "Line"),
                    };
                }
                case "pond":
                {
                    var pond = doc.FindPond(new PondId(id));
                    if (pond == null) return null;
                    var b = Ground != null && Ground.Ready ? Ground.Earthwork.DetailOf(id) as PondBuild : null;
                    var w = Built(pond.FullPoolMm, b, pond.StartsEmpty);
                    w.Fills = pond.Fill == PondFill.Pumped ? "pumped only · from a pump house (coming next)" : "naturally · runoff and any spring";
                    w.StartsEmpty = pond.StartsEmpty;
                    WaterCardRows.Add(rows, w);
                    acts.Add(("edit", "Edit", false));
                    acts.Add(("remove", "Remove", false));
                    return new WaterCardModel { Key = key, Kind = "Pond", Name = pond.Name, Renamable = true, Rows = rows.ToArray(), Actions = acts.ToArray(),
                                                Hint = WaterCardRows.Hint(b?.Warning, null, false) };
                }
                case "dam":
                {
                    var dam = doc.FindDam(new DamId(id));
                    if (dam == null) return null;
                    var b = Ground != null && Ground.Ready ? Ground.Earthwork.DetailOf(id) as DamBuild : null;
                    var s = dam.StreamWay != 0 ? StreamOf(dam.StreamWay) : null;
                    var srec = dam.StreamWay != 0 ? doc.FindStream(dam.StreamWay) : default;
                    string stream = srec.Name.Length > 0 ? srec.Name : s != null && s.Line.Name.Length > 0 ? s.Line.Name : s != null ? "an unnamed stream" : "the streams";
                    var w = Built(dam.FullPoolMm, b, dam.StartsEmpty);
                    w.Fills = "naturally · from " + stream;
                    w.StartsEmpty = dam.StartsEmpty;
                    WaterCardRows.Add(rows, w);
                    if (b != null) rows.Add(("Dam", $"{ToolFormat.Length(b.LengthMetres)} long · up to {ToolFormat.Depth(b.HeightMaxMetres)} high"));
                    rows.Add(("Spillway", dam.Spill == SpillBank.First ? "at the first bank" : "at the far bank"));
                    acts.Add(("edit", "Edit", false));
                    acts.Add(("remove", "Remove", false));
                    if (s != null) acts.Add(("stream", "The stream", false));
                    return new WaterCardModel { Key = key, Kind = "Reservoir", Name = dam.Name, Renamable = true, Rows = rows.ToArray(), Actions = acts.ToArray(),
                                                Hint = WaterCardRows.Hint(b?.Warning, null, false) };
                }
            }
            return null;
        }

        // A pond's or a reservoir's figures for the shared layout: where the water stands and full pool, then what its graded
        // ground holds (WS-01: every pond and dam is a snowmaking source). PR A has no water clock yet: it stands at full pool,
        // or on the floor when it starts empty; PR B moves it.
        static WaterCardRows.Standing Built(int fullPoolMm, WaterBuild b, bool startsEmpty)
        {
            var w = WaterCardRows.Empty();
            w.FullPoolMetres = fullPoolMm / 1000.0;
            w.LevelMetres = startsEmpty ? (b != null ? w.FullPoolMetres - b.MaxDepthMetres : double.NaN) : w.FullPoolMetres;
            w.PercentFull = startsEmpty ? 0 : 100;
            w.Snowmaking = "a source";
            if (b == null) return w;
            w.AreaSquareMetres = b.AreaSquareMetres;
            w.MeanDepthMetres = b.MeanDepthMetres;
            w.MaxDepthMetres = b.MaxDepthMetres;
            w.VolumeCubicMetres = b.VolumeCubicMetres;
            w.CutCubicMetres = b.CutCubicMetres;
            w.FillCubicMetres = b.FillCubicMetres;
            return w;
        }

        string RenameWater(string key, string name)
        {
            var history = Design;
            if (history == null || !Parse(key, out string kind, out ulong id)) return "No design is open";
            var doc = history.Document;
            DesignCommand cmd;
            switch (kind)
            {
                case "lake":
                {
                    var rec = doc.FindLake(id);
                    // Typing the map's name (or nothing) gives the map's back: no name of your own is kept.
                    string mine = name == OsmLakeName(id) ? "" : name;
                    if (mine.Length > 0 && DesignRules.NameProblem(mine) is string p) return p;
                    cmd = new SetLake(history.Revision, id, mine, rec.DepthTenthsFt);
                    break;
                }
                case "stream":
                {
                    var rec = doc.FindStream(id);
                    string mine = name == (StreamOf(id)?.Line.Name ?? "") ? "" : name;
                    if (mine.Length > 0 && DesignRules.NameProblem(mine) is string p) return p;
                    cmd = new SetStream(history.Revision, id, mine, rec.WidthCm);
                    break;
                }
                default: cmd = new RenameWater(history.Revision, id, name); break;
            }
            var r = history.Submit(cmd);
            if (r.Accepted) ShowWaterCardFor(key);
            return r.Accepted ? null : r.Reason;
        }

        void WaterCardAction(string key, string action)
        {
            var history = Design;
            if (history == null || !Parse(key, out string kind, out ulong id)) return;
            var doc = history.Document;
            DesignCommand cmd = null;
            if (kind == "lake")
            {
                var rec = doc.FindLake(id);
                int body = LakeBody(id);
                int depth = rec.DepthTenthsFt > 0 ? rec.DepthTenthsFt : body > 0 ? LakesOnly[body].DepthTenthsFt : 10;
                switch (action)
                {
                    case "source": cmd = new SetLakeSource(history.Revision, id, !rec.Source, rec.DepthTenthsFt); break;
                    case "deeper": cmd = new SetLake(history.Revision, id, rec.Name, Math.Min(PondRules.LakeDepthMaxTenthsFt, depth + 10)); break;
                    case "shallower": cmd = new SetLake(history.Revision, id, rec.Name, Math.Max(1, depth - 10)); break;
                    case "estimate": cmd = new SetLake(history.Revision, id, rec.Name, 0); break;
                    case "osmname": cmd = new SetLake(history.Revision, id, "", rec.DepthTenthsFt); break;
                }
            }
            else if (kind == "stream")
            {
                var rec = doc.FindStream(id);
                var s = StreamOf(id);
                int width = rec.WidthCm > 0 ? rec.WidthCm : (int)Math.Round((s?.Line.WidthMetres ?? 2.4384) * 100);
                switch (action)
                {
                    case "wider": cmd = new SetStream(history.Revision, id, rec.Name, Math.Min(PondRules.WidthMaxCm, width + 30)); break;
                    case "narrower": cmd = new SetStream(history.Revision, id, rec.Name, Math.Max(30, width - 30)); break;
                    case "mapwidth": cmd = new SetStream(history.Revision, id, rec.Name, 0); break;
                    case "osmname": cmd = new SetStream(history.Revision, id, "", rec.WidthCm); break;
                }
            }
            else if (action == "edit" || action == "remove")
            {
                Hud.CloseWaterCardIfOpen();
                Hud.SetToolboxTab("snow");
                Hud.SetToolbox(true);
                if (action == "edit" && _tools != null && _tools.Choose(EditWaterTool.ToolId) && _tools.Active is EditWaterTool edit) edit.Choose(id);
                if (action == "remove" && _tools != null && _tools.Choose(RemoveWaterTool.ToolId) && _tools.Active is RemoveWaterTool remove) remove.Choose(id);
                return;
            }
            else if (action == "stream" && doc.FindDam(new DamId(id)) is Dam dam)
            {
                ShowWaterCardFor("stream:" + Hex(dam.StreamWay));
                return;
            }
            if (cmd == null) return;
            var r = history.Submit(cmd);
            if (!r.Accepted) Hud.Toast(r.Reason);
            ShowWaterCardFor(key);
        }

        // ---------------- names on the map ----------------

        /// <summary>
        /// LK-10: named lakes (yours or OpenStreetMap's), ponds and reservoirs get a label at their middle, with the lifts' and
        /// Areas' on the Labels layer; rebuilt when the design or the area's names change, never per frame.
        /// </summary>
        void UpdateWaterLabels(UnityEngine.Camera cam, DesignDocument doc)
        {
            if (Hud == null || _resort == null || cam == null) return;
            if (!ReferenceEquals(doc, _waterLabelsFor) || !ReferenceEquals(_waterLabelsArea, _osmLakeNames ?? (object)_resort))
            {
                _waterLabelsFor = doc;
                _waterLabelsArea = _osmLakeNames ?? (object)_resort;
                _waterLabelSpots.Clear();
                Vector3 At(float x, float z, float up) => new Vector3(x, _resort.Surface.HeightAt(x, z) + up, z);
                var t = LakesOnly;
                if (t != null && doc != null)
                    for (int i = WaterBodyTable.Streams + 1; i < t.Count; i++)
                    {
                        var b = t[i];
                        if (b.Kind != WaterBodyKind.Lake) continue;
                        string name = doc.FindLake(b.Id).Name;
                        if (name.Length == 0) name = OsmLakeName(b.Id);
                        if (name.Length > 0) _waterLabelSpots.Add((name, At(b.CentroidXMm / 1000f, b.CentroidYMm / 1000f, 4)));
                    }
                if (doc != null)
                {
                    foreach (var pond in doc.Ponds)
                    {
                        float x = 0, z = 0;
                        foreach (var q in pond.Ring) { x += (float)q.XMetres; z += (float)q.YMetres; }
                        _waterLabelSpots.Add((pond.Name, At(x / pond.Ring.Count, z / pond.Ring.Count, 4)));
                    }
                    foreach (var dam in doc.Dams)
                        _waterLabelSpots.Add((dam.Name, At((float)(dam.BankA.XMetres + dam.BankB.XMetres) / 2, (float)(dam.BankA.YMetres + dam.BankB.YMetres) / 2, 8)));
                }
                _waterLabels ??= new LandmarkLabelOverlay(Hud.ContourLabelLayer, _labelPlacement, LabelPlacement.Landmarks,
                                                          nudges: new[] { Vector2.zero, new Vector2(0, -24), new Vector2(0, 24) });
                _waterLabels.SetLandmarks(_waterLabelSpots);
            }
            _waterLabels?.Update(cam, _resort.Surface, _hudShown && !PhotoOpen && Hud.LabelsOn);
        }
    }
}
