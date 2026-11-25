using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.World.Lifts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Presentation.Lifts
{
    // Task 4.50 phase 6, slice 2 (owner 2026-11-25): every terminal ships with its operator hut, and a fixed grip's top (drive
    // or return) with its stop gate, pedestal and (raised 5 ft or more) its unload ramp; what each end gets is Domain's
    // TerminalExtras.
    public sealed partial class LiftView
    {
        /// <summary>The imported buildings (Editor BuildingImport) under Resources.</summary>
        public const string BuildingSetName = "BuildingModels";
        /// <summary>Owner 2026-11-23: a hut's front stands about 4 ft clear of the passing chairs (or the terminal).</summary>
        public const float HutClearMetres = 1.219f;
        /// <summary>unloadramp.json: the rope 3,039 mm over the unload level (the ramp's chair_unload socket).</summary>
        const float RopeOverDeck = 3.039f;

        static LiftModelSet _buildings;
        static bool _buildingsTried;

        /// <summary>The buildings' set, loaded once (null when not imported: the lifts draw without their extras).</summary>
        public static LiftModelSet Buildings
        {
            get
            {
                if (!_buildingsTried)
                {
                    _buildingsTried = true;
                    _buildings = Resources.Load<LiftModelSet>(BuildingSetName);
                }
                return _buildings;
            }
            set { _buildings = value; _buildingsTried = true; }
        }

        /// <summary>What each end was given in the last build (tests, captures and the log): the hut's object, and a fixed
        /// grip's top's ramp, gate and pedestal (null where there's none).</summary>
        public sealed class EndPieces
        {
            public EndExtras Extras;
            /// <summary>The terminal they belong to.</summary>
            public GameObject Terminal;
            public GameObject Hut, Ramp, Gate, Pedestal;
            /// <summary>Whether the hut is drawn mirrored (the other hand, its door on the far end from the line).</summary>
            public bool Mirrored;
            /// <summary>How far the top stood over its ground (a fixed grip's top), metres.</summary>
            public double Raise;
        }

        public EndPieces BottomExtras { get; private set; }
        public EndPieces TopExtras { get; private set; }

        GameObject Building(string id)
        {
            if (id == null) return null;
            var set = Buildings;
            return (set != null ? set.Find(id) : null) ?? Model(id);   // the T-bar's hut is a lift model
        }

        /// <summary>Places both ends' extras once the terminals stand (and the top shows the row the rope picked).</summary>
        void PlaceExtras(LiftType type, GameObject bottomGo, GameObject topGo, Func<Vector3, double> height, IHeightSource ground,
                         LocalFrame frame, Vector3 bottom, Vector3 top, LiftLivery livery)
        {
            BottomExtras = TopExtras = null;
            _spawning = PartBottom;
            BottomExtras = PlaceEnd(type, false, bottomGo, height, null, livery);
            // The ramp's 5 ft: the top's level over the ground as it stands (LF-12 v6: a top raised 5 ft or more gets no pad, so
            // its ground is what the build measured; a padded top stands level with its pad, so it shows no ramp).
            double raise = 0;
            if (topGo != null && TerminalExtras.For(type, true).UnloadEnd)
            {
                var t = frame.ToAlbers(top.x, top.z);
                var b = frame.ToAlbers(bottom.x, bottom.z);
                raise = TerminalExtras.UnloadRaise(type, ground, t.X, t.Y, b.X - t.X, b.Y - t.Y, topGo.transform.position.y);
            }
            _spawning = PartTop;
            TopExtras = PlaceEnd(type, true, topGo, height, (float)raise, livery);
        }

        EndPieces PlaceEnd(LiftType type, bool top, GameObject terminal, Func<Vector3, double> height, float? raise, LiftLivery livery)
        {
            var e = TerminalExtras.For(type, top);
            var pieces = new EndPieces { Extras = e, Terminal = terminal, Raise = raise ?? 0 };
            if (terminal == null || e.Hut == HutKind.None) return pieces;
            var rig = terminal.GetComponent<LiftRig>();
            var tr = terminal.transform;
            float g = (float)type.GaugeMetres;

            if (e.Hut == HutKind.Tow)
            {
                // The Lift Lab's reviewed place (LiftLab Mode.Tbar): beside the drive, its door away from the line.
                var hutPrefab = Building(e.HutModel);
                if (hutPrefab != null)
                    pieces.Hut = SpawnExtra(hutPrefab, tr.TransformPoint(new Vector3(4.6f, 0, 2f)), tr.rotation * Quaternion.Euler(0, -90f, 0), false, livery);
                return pieces;
            }

            if (e.UnloadEnd)
            {
                PlaceUnload(type, e, tr, rig, g, height, pieces, livery);
                return pieces;
            }

            // A hut beside the load (bottom) or unload (top) point, on the loaded chairs' side: the bottom's right (+X), the
            // top's left (-X), its glazed front toward them.
            var prefab = Building(e.HutModel);
            if (prefab == null) return pieces;
            float s = top ? -1 : 1;
            var point = rig != null ? rig.HeadSocket(top ? "chair_unload" : "chair_load") : null;
            float pz = point != null ? tr.InverseTransformPoint(point.position).z : 0;
            var end = top ? type.Top : type.Bottom;
            float clear = Mathf.Max(g / 2 + (float)type.ChairHalfWidthMetres, end != null ? (float)end.HalfWidth : 0) + HutClearMetres;
            pieces.Hut = PlaceHut(prefab, tr, s, clear, pz, height, livery, out pieces.Mirrored);
            return pieces;
        }

        /// <summary>
        /// A hut beside the line in the terminal's frame <paramref name="tr"/>: on side <paramref name="s"/> (+1: +X), its
        /// front <paramref name="clear"/> from the line, centred along it at <paramref name="z"/>, turned to face the line
        /// and mirrored where its door would face the other terminal (owner 2026-11-23: the door, landing and stair on the
        /// end away from the line); its floor at the terminal's level or the highest ground under it, never buried.
        /// </summary>
        GameObject PlaceHut(GameObject prefab, Transform tr, float s, float clear, float z, Func<Vector3, double> height, LiftLivery livery, out bool mirrored)
        {
            var box = LocalBox(prefab);
            var door = SocketLocal(prefab, "door");
            // Turned so its +X (the front) points at the line: identity on the -X side, half a turn on the +X side.
            var turn = s > 0 ? Quaternion.Euler(0, 180f, 0) : Quaternion.identity;
            // Along the line its u (+Z) runs -s*m: the door's end must face away from the other terminal (-Z).
            float m = door.HasValue && s * door.Value.z < 0 ? -1 : 1;
            mirrored = m < 0;
            float x = s * (clear + box.max.x);
            float cz = -s * m * box.center.z;
            var local = new Vector3(x, 0, z - cz);
            var rotation = tr.rotation * turn;
            var position = tr.TransformPoint(local);
            position.y = Mathf.Max(tr.position.y, HighestUnder(box, position, rotation, m, height));
            var go = SpawnExtra(prefab, position, rotation, true, livery);
            if (mirrored) go.transform.localScale = new Vector3(1, 1, -1);
            return go;
        }

        /// <summary>A fixed grip's top, drive or return: the ramp (raised 5 ft or more; the Monta drive's wide one), the mini
        /// hut in its hut bay, the pedestal on its deck and the stop gate past the wheel, all in the ramp's frame (the
        /// terminal's turned half round at the unload point).</summary>
        void PlaceUnload(LiftType type, EndExtras e, Transform tr, LiftRig rig, float g, Func<Vector3, double> height, EndPieces pieces, LiftLivery livery)
        {
            var rampPrefab = Building(e.RampModel);
            var spec = TerminalExtras.Spec(e);
            var point = rig != null ? rig.HeadSocket("chair_unload") : null;
            float pz = point != null ? tr.InverseTransformPoint(point.position).z : 0;
            // The ramp's origin: the unload point under the arriving (loaded) rope, on the unload level; its +u past the wheel.
            var origin = tr.TransformPoint(new Vector3(-(float)spec.Gauge / 2, 0, pz));
            var rampTurn = tr.rotation * Quaternion.Euler(0, 180f, 0);
            Vector3 InRamp(Vector3 p) => origin + rampTurn * p;
            bool shown = TerminalExtras.RampShown(e, pieces.Raise) && rampPrefab != null;
            if (shown) pieces.Ramp = SpawnExtra(rampPrefab, origin, rampTurn, true, livery);

            // The hut in the ramp's hut bay (ramp socket "hut"), facing the chairs (-v), as the ramp was built round it.
            var hutPrefab = Building(e.HutModel);
            var hutAt = rampPrefab != null ? SocketLocal(rampPrefab, "hut") : null;
            if (hutPrefab != null && hutAt.HasValue)
            {
                var hutRotation = rampTurn * Quaternion.Euler(0, 180f, 0);
                var p = InRamp(hutAt.Value);
                var door = SocketLocal(hutPrefab, "door");
                // The door's end away from the line: the hut's u runs with the terminal's +Z here, so its door wants -u.
                pieces.Mirrored = door.HasValue && door.Value.z > 0;
                if (!shown)
                {
                    // On the pad, no bay: on its own legs at the level, or over higher ground.
                    var box = LocalBox(hutPrefab);
                    p.y = Mathf.Max(tr.position.y, HighestUnder(box, p, hutRotation, pieces.Mirrored ? -1 : 1, height));
                }
                pieces.Hut = SpawnExtra(hutPrefab, p, hutRotation, true, livery);
                if (pieces.Mirrored) pieces.Hut.transform.localScale = new Vector3(1, 1, -1);
                // In the bay it stands on the ramp's own posts: its skids and step (the legs part) are hidden.
                if (shown) foreach (var r in pieces.Hut.GetComponentsInChildren<Renderer>(true)) if (r.name.Contains("_legs_")) r.enabled = false;
            }

            var pedestal = Building(e.PedestalModel);
            var pedAt = rampPrefab != null ? SocketLocal(rampPrefab, "pedestal") : null;
            if (pedestal != null && pedAt.HasValue) pieces.Pedestal = SpawnExtra(pedestal, InRamp(pedAt.Value), rampTurn, true, livery);

            // The stop gate (unloadramp.json gate): its post on the line's centreline, postClear inside the chair's inner end
            // where it crosses the line round the far side of the wheel; the bar along +u.
            var gate = Building(e.GateModel);
            if (gate != null)
            {
                float u = (float)(TerminalExtras.WheelU(e.Style) + spec.Gauge / 2 - spec.ChairWidth / 2 - TerminalExtras.GatePostClear);
                pieces.Gate = SpawnExtra(gate, InRamp(new Vector3(-(float)spec.Gauge / 2, 0, u)), rampTurn, true, livery);
            }
        }

        GameObject SpawnExtra(GameObject prefab, Vector3 position, Quaternion rotation, bool building, LiftLivery livery)
        {
            var go = Object.Instantiate(prefab, position, rotation, Root.transform);
            _partOf[go] = _spawning;
            if (building) _materials?.ApplyPlain(go);
            else _materials?.Apply(go, livery);
            return go;
        }

        /// <summary>The highest ground under a hut's footprint (its box in its own frame, mirrored by <paramref name="m"/>
        /// along u), on a 4 × 4 grid.</summary>
        static float HighestUnder(Bounds box, Vector3 at, Quaternion rotation, float m, Func<Vector3, double> height)
        {
            float high = float.NegativeInfinity;
            for (int i = 0; i <= 3; i++)
                for (int j = 0; j <= 3; j++)
                {
                    var p = new Vector3(Mathf.Lerp(box.min.x, box.max.x, i / 3f), 0, m * Mathf.Lerp(box.min.z, box.max.z, j / 3f));
                    var w = at + rotation * p;
                    double h = height(w);
                    if (!double.IsNaN(h)) high = Mathf.Max(high, (float)h);
                }
            return high;
        }

        /// <summary>A building's socket in its own frame ("&lt;id&gt;_socket_&lt;name&gt;"), or null.</summary>
        static Vector3? SocketLocal(GameObject prefab, string name)
        {
            if (prefab == null) return null;
            string suffix = "_socket_" + name;
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
                if (t.name.EndsWith(suffix, StringComparison.Ordinal)) return prefab.transform.InverseTransformPoint(t.position);
            return null;
        }

        static readonly Dictionary<GameObject, Bounds> _extraBoxes = new Dictionary<GameObject, Bounds>();

        /// <summary>A prefab's meshes' box in its own frame (its first level of detail and its legs), cached.</summary>
        static Bounds LocalBox(GameObject prefab)
        {
            if (_extraBoxes.TryGetValue(prefab, out var cached)) return cached;
            var root = prefab.transform.worldToLocalMatrix;
            bool any = false;
            var box = new Bounds();
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !mf.name.EndsWith("_LOD0", StringComparison.Ordinal)) continue;
                var b = mf.sharedMesh.bounds;
                var m = root * mf.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    var c = m.MultiplyPoint3x4(new Vector3((k & 1) != 0 ? b.max.x : b.min.x, (k & 2) != 0 ? b.max.y : b.min.y, (k & 4) != 0 ? b.max.z : b.min.z));
                    if (!any) { box = new Bounds(c, Vector3.zero); any = true; }
                    else box.Encapsulate(c);
                }
            }
            _extraBoxes[prefab] = box;
            return box;
        }
    }
}
