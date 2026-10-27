using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// The lift tool's catchment preview (task P3-10, NW-14, owner NT16: "just like placing service buildings in Cities:
    /// Skylines 2"; NT20): while New lift or Edit lift is armed, the hub each terminal in hand or placed would make or
    /// join is drawn as it would be with it, grown before the click; the other stations fainter; the built trails it
    /// would gather light up in their own colours. The tool's window says what the terminal in hand joins (an Area, a
    /// shared base or a transfer, and the trails). Rebuilt only when a terminal moves half a metre or the built work
    /// changes, never per frame.
    /// </summary>
    public sealed partial class MountainViewer
    {
        NetworkView _catchment;
        string _catchmentNote;
        long _catchmentKey = long.MinValue;
        object _catchmentDoc;
        bool _catchmentRetry;
        readonly List<Vector2> _catchPoints = new List<Vector2>(4);
        readonly List<bool> _catchBottoms = new List<bool>(4);
        readonly List<Trail> _catchTrails = new List<Trail>();
        Vector2? _catchShot;   // -networkshots: a bottom terminal held here, no tool armed

        void TickCatchment(UnityEngine.Camera cam)
        {
            string id = _tools != null && _tools.Armed ? _tools.ActiveId : null;
            bool armed = (id == NewLiftTool.ToolId || id == EditLiftTool.ToolId || _catchShot.HasValue) && !_networkOn && _resort != null && _netGeometry != null && _networkDoc != null;
            if (!armed)
            {
                if (_catchment != null) _catchment.Visible = false;
                _catchmentNote = null;
                _catchmentKey = long.MinValue;
                return;
            }

            // The terminals: the ones in hand (their ghosts), then the draft's own (placed, or Edit lift's).
            _catchPoints.Clear();
            _catchBottoms.Clear();
            int inHand = -1;
            for (int g = 0; g < 2; g++)
            {
                var ghost = _ghosts[g];
                if (ghost == null || !ghost.Shown) continue;
                AddCatchPoint(new Vector2(ghost.Bullwheel.x, ghost.Bullwheel.z), g == 0);
                inHand = _catchPoints.Count - 1;   // the top while aiming, else the bottom
            }
            if (_catchShot.HasValue && inHand < 0)
            {
                AddCatchPoint(_catchShot.Value, true);
                inHand = 0;
            }
            if (_draftLift != null)
            {
                AddCatchPoint(DesignFrame.ToLocal(_draftLift.Bottom), true);
                AddCatchPoint(DesignFrame.ToLocal(_draftLift.Top), false);
            }

            long key = _hiddenLiftId.GetHashCode();
            foreach (var p in _catchPoints) key = key * 1000003 + Mathf.RoundToInt(p.x * 2) * 65537L + Mathf.RoundToInt(p.y * 2);
            if (key != _catchmentKey || !ReferenceEquals(_catchmentDoc, _networkDoc) || _catchmentRetry)
            {
                _catchmentKey = key;
                _catchmentDoc = _networkDoc;
                BuildCatchment(inHand);
            }
            _catchment.Visible = _liftsShown;
            if (cam != null) _catchment.Follow(cam);
        }

        void AddCatchPoint(Vector2 p, bool bottom)
        {
            foreach (var q in _catchPoints) if ((q - p).sqrMagnitude < 1) return;   // a ghost standing on the draft's own end
            _catchPoints.Add(p);
            _catchBottoms.Add(bottom);
        }

        void BuildCatchment(int inHand)
        {
            _catchment ??= new NetworkView(_resort.Root.transform, "Lift catchment");
            var doc = _networkDoc;
            var site = _netSite;
            var surface = _resort.Surface;
            Vector3 Drape(double latticeX, double latticeY)
            {
                float x = (float)(latticeX - site.OriginX), z = (float)(latticeY - site.OriginY);
                float y = surface.HeightAt(x, z);
                return new Vector3(x, float.IsNaN(y) ? 0 : y + 2, z);
            }

            // NT20: the stations as they'd be with the terminals in hand, each its own end of one draft lift (so its two
            // ends never share a station); the hubs they join are drawn grown, the rest faint.
            LiftId? editing = _hiddenLiftId != 0 ? new LiftId(_hiddenLiftId) : (LiftId?)null;
            var before = DesignStations.Of(doc, editing);
            var extra = new List<StationMember>(_catchPoints.Count);
            for (int i = 0; i < _catchPoints.Count; i++) extra.Add(new StationMember(DraftLift, _catchBottoms[i], DesignFrame.ToDesign(_catchPoints[i])));
            var after = DesignStations.Of(doc, editing, extra);
            var areas = new List<NetworkView.StationArea>();
            var mine = new List<Station>();
            foreach (var st in after)
            {
                bool draft = false;
                foreach (var m in st.Members) if (m.Lift == DraftLift) draft = true;
                if (draft) mine.Add(st);
                else areas.Add(StationShape(st, site, Drape, false));
            }
            foreach (var st in mine) areas.Add(StationShape(st, site, Drape, true));

            // The trails each grown hub would gather; asked again next frame while a network build holds the geometry.
            _catchmentRetry = false;
            var gathered = new SortedDictionary<ulong, Trail>();
            string note = null;
            var beforeAreas = DesignAreas.Of(doc, before);
            foreach (var st in mine)
            {
                _catchTrails.Clear();
                if (_netGeometry == null || !_netGeometry.TryTrailsWithin(doc, st.Hull, _catchTrails)) _catchmentRetry = _netGeometry != null;
                foreach (var t in _catchTrails) gathered[t.Id.Value] = t;
                if (inHand < 0) continue;
                var at = DesignFrame.ToDesign(_catchPoints[inHand]);
                foreach (var m in st.Members)
                    if (m.Lift == DraftLift && m.Bottom == _catchBottoms[inHand] && m.At.DistanceSquared(at) == 0)
                        note = CatchmentNote(doc, st, beforeAreas, _catchBottoms[inHand], _catchTrails);
            }
            _catchmentNote = note;

            var segments = new List<NetworkView.Segment>();
            if (_netGeometry != null && !_catchmentRetry)
                foreach (var t in gathered.Values)
                {
                    var line = _netGeometry.CentrelineOf(t);
                    double rating = double.NaN;
                    if (_trailLayer != null && _trailLayer.TryGetFacts(t.Id, out var f)) rating = f.Rating;
                    segments.Add(new NetworkView.Segment { Edge = segments.Count, Trail = t.Id.Value, Points = Stretch(line, 0, double.MaxValue, Drape),
                                                           Own = NetworkView.RatingColour(rating), Served = true });
                }
            _catchment.SetCatchment(segments.ToArray(), areas.ToArray());
        }

        /// <summary>The terminals in hand, as one lift no built lift shares an ID with.</summary>
        static readonly LiftId DraftLift = new LiftId(ulong.MaxValue);

        /// <summary>
        /// The window's line about the terminal in hand (NT14, NT16, NT20): in an Area's hub it joins that Area; beside
        /// another lift's other end it's a transfer, beside the same end a shared base or top; then the trails the hub gathers.
        /// </summary>
        static string CatchmentNote(DesignDocument doc, Station hub, List<Area> areas, bool bottom, List<Trail> trails)
        {
            var same = new List<string>();
            var other = new List<string>();
            string area = null;
            foreach (var m in hub.Members)
            {
                if (m.Lift == DraftLift) continue;
                string name = doc.FindLift(m.Lift)?.Name ?? "a lift";
                var list = m.Bottom == bottom ? same : other;
                if (!list.Contains(name)) list.Add(name);
                if (area != null) continue;
                foreach (var a in areas)
                    foreach (var o in a.Station.Members)
                        if (o.Lift == m.Lift && o.Bottom == m.Bottom) area = a.Name;
            }
            string joins = area != null ? $"Joins {area}"
                : other.Count > 0 ? $"Transfer {(bottom ? "from" : "to")} {string.Join(", ", other)}"
                : same.Count > 0 ? $"Shares a {(bottom ? "base" : "top")} with {string.Join(", ", same)}" : null;
            string gathers = null;
            if (trails.Count > 0)
            {
                var names = new List<string>(trails.Count);
                foreach (var t in trails) names.Add(t.Name);
                gathers = (joins != null ? "gathers " : "Gathers ") + string.Join(", ", names);
            }
            if (joins == null && gathers == null) return "No trail within 100 ft yet";
            return joins == null ? gathers : gathers == null ? joins : joins + " · " + gathers;
        }

        void DisposeCatchment()
        {
            _catchment?.Dispose();
            _catchment = null;
            _catchmentNote = null;
            _catchmentKey = long.MinValue;
        }
    }
}
