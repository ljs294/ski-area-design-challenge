using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.World.Lifts;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// One built fixed-grip lift on the mountain (task 07): the terminals, the towers (base, 1 m mast sections and the
    /// head the rope needs, leaned when the lift stands Leaned), the haul rope as a cable, and the chairs moving round
    /// it at rope speed. The rope comes from <see cref="LiftRope"/> (Domain); this only draws it. Tasks 09 and 11 reuse it
    /// for every lift, so per frame it allocates nothing: the chairs are drawn instanced from preallocated matrices.
    /// The stacking and the station rope follow the Lift Lab's (task P1 lift pilot), which now shares this code.
    /// </summary>
    public sealed partial class LiftView : IDisposable
    {
        public LiftRope Rope { get; private set; }
        public GameObject Root { get; }
        /// <summary>The head chosen for each tower, in order (for captures, tests and the review).</summary>
        public IReadOnlyList<string> Heads => _heads;
        public int ChairCount => _chairCount;

        readonly LiftModelSet _models;
        readonly LiftMaterials _materials;
        readonly Material _cableMaterial;
        readonly List<string> _heads = new List<string>();
        /// <summary>The type's tower kit (G2, LT41): its parts first, the quad's where it has none.</summary>
        string _kit = Quad;
        const string Quad = "sessellift_fgq4_";
        Mesh _cable;

        // The rope loop in world space, in travel order, with the distance walked to each point.
        Vector3[] _loop = Array.Empty<Vector3>();
        float[] _walked = Array.Empty<float>();
        float _loopLength;
        Bounds _bounds;
        int _chairCount;
        float _chairInterval;   // seconds between carriers round the track (LT41)
        CarrierTrack _track;
        /// <summary>The carriers' track (tests and captures).</summary>
        public CarrierTrack Carriers => _track;
        ChairLod[] _chairLods = Array.Empty<ChairLod>();
        float[] _lodDistances = Array.Empty<float>();

        sealed class ChairPart
        {
            public Mesh Mesh;
            public Material[] Materials;
            public Matrix4x4 Local;
            public Matrix4x4[] Matrices;
            public int Count;
            /// <summary>Under the hanger's pivot (the model contract's chair split, finding #3): hangs plumb from the hinge.</summary>
            public bool Hanger;
        }

        /// <summary>The hanger's hinge in the chair's frame (the grip's), when the chair model is split.</summary>
        Vector3 _hinge;
        bool _splitChair;

        sealed class ChairLod { public ChairPart[] Parts; }

        public LiftView(Transform parent, LiftModelSet models, LiftMaterials materials, Material cableMaterial, string name)
        {
            _models = models;
            _materials = materials;
            _cableMaterial = cableMaterial;
            Root = new GameObject(name);
            Root.transform.SetParent(parent, false);
        }

        GameObject Prefab(string id) => (_kit != Quad ? Model(_kit + id) : null) ?? Model(Quad + id);
        GameObject Model(string assetId) => _models != null ? _models.Find(assetId) : null;

        /// <summary>
        /// LF-12: how far a terminal's origin (its load or unload level, where it stands on its pad) lies from its bullwheel
        /// along the line toward the other terminal, in metres, from its rope_*_bw sockets.
        /// </summary>
        public static double OriginAlong(LiftModelSet models, string assetId)
        {
            var prefab = models != null ? models.Find(assetId) : null;
            var rig = prefab != null ? prefab.GetComponent<LiftRig>() : null;
            var l = rig != null ? rig.Socket("rope_left_bw") : null;
            var r = rig != null ? rig.Socket("rope_right_bw") : null;
            if (l == null || r == null) return 0;
            return -(l.localPosition.z + r.localPosition.z) / 2;
        }

        /// <summary>
        /// Where a terminal's rope leaves it toward the line (TW-16): how far along the line from the bullwheel, and how
        /// high above the terminal's origin (on the ground), from its rope_right_bw and rope_right_out sockets.
        /// </summary>
        public static bool RopeExit(LiftModelSet models, string assetId, out float along, out float rise)
        {
            along = rise = 0;
            var prefab = models != null ? models.Find(assetId) : null;
            var rig = prefab != null ? prefab.GetComponent<LiftRig>() : null;
            var bw = rig != null ? rig.Socket("rope_right_bw") : null;
            var exit = rig != null ? rig.Socket("rope_right_out") : null;
            if (bw == null || exit == null) return false;
            along = exit.localPosition.z - bw.localPosition.z;
            rise = exit.localPosition.y;
            return true;
        }

        /// <summary>
        /// A terminal's footprint from its model (CL-09): how far it reaches behind its bullwheel (away from the line) and
        /// in front, and its half width, in metres, from its meshes' bounds in its own frame (+Z toward the other terminal).
        /// </summary>
        public static bool TerminalExtent(LiftModelSet models, string assetId, out float behind, out float ahead, out float halfWidth)
        {
            behind = ahead = halfWidth = 0;
            var prefab = models != null ? models.Find(assetId) : null;
            var rig = prefab != null ? prefab.GetComponent<LiftRig>() : null;
            if (rig == null || rig.Socket("rope_left_bw") == null || rig.Socket("rope_right_bw") == null) return false;
            float bw = (rig.Socket("rope_left_bw").localPosition.z + rig.Socket("rope_right_bw").localPosition.z) / 2;
            var root = prefab.transform.worldToLocalMatrix;
            float minZ = float.MaxValue, maxZ = float.MinValue, maxX = 0;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                var m = root * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var sign = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
                    var corner = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, sign));
                    minZ = Mathf.Min(minZ, corner.z);
                    maxZ = Mathf.Max(maxZ, corner.z);
                    maxX = Mathf.Max(maxX, Mathf.Abs(corner.x));
                }
            }
            if (minZ > maxZ) return false;
            behind = bw - minZ;
            ahead = maxZ - bw;
            halfWidth = maxX;
            return true;
        }

        /// <summary>
        /// Builds the lift: terminals at its bullwheels, towers, the solved rope and the chairs. Heights are the edited
        /// ground (<paramref name="ground"/>, in the package's frame); <paramref name="frame"/> turns design points into
        /// local metres. The livery is the lift's two colours (LT41): the primary on the parts the models mark as livery, the accent on theirs.
        /// </summary>
        /// <remarks>Each terminal stands at the lift's stored level (SK14, save v2): a raised terminal stands on its pier
        /// above the ground (LF-12 v3, pads only cut). A lift without levels (from a v1 save) stands at its origins' ground.</remarks>
        public void Build(Lift lift, IHeightSource ground, LocalFrame frame, LiftLivery livery)
        {
            Clear();
            (double Bottom, double Top)? levels = lift.Levels is LiftLevels v ? (v.BottomMetres, v.TopMetres) : ((double, double)?)null;
            var type = LiftTypes.Measured(LiftType.Of(lift.CatalogId), _models);
            _kit = type.TowerKit;
            var bottom = new Vector3((float)lift.Bottom.XMetres, 0, (float)lift.Bottom.YMetres);
            var top = new Vector3((float)lift.Top.XMetres, 0, (float)lift.Top.YMetres);
            double Height(Vector3 p)
            {
                var a = frame.ToAlbers(p.x, p.z);
                return ground.At(a.X, a.Y);
            }
            var dir = (top - bottom).normalized;
            var right = Vector3.Cross(Vector3.up, dir);   // the loaded rope is on the right looking uphill (LC-02 models)

            // Terminals: each end's model (TW-16 v3: the type says which stands where), facing along the line toward the
            // other end, with its bullwheel's centre on the design point.
            _spawning = PartBottom;
            var ret = PlaceTerminal(Model(type.Bottom.Model), bottom, Quaternion.LookRotation(dir), Height, livery, levels?.Bottom);
            _spawning = PartTop;
            var drive = PlaceTerminal(Model(type.Top.Model), top, Quaternion.LookRotation(-dir), Height, livery, levels?.Top);
            var retRig = ret != null ? ret.GetComponent<LiftRig>() : null;
            var driveRig = drive != null ? drive.GetComponent<LiftRig>() : null;
            // Each terminal's pier runs about 15 m below its 0.00 level, buried in the pad; its footing stands at grade under
            // the pier (the model contract, owner: piers simply tall, no stacking). Models without one skip it.
            _spawning = PartBottom;
            PlaceFooting(ret, Height, livery);
            _spawning = PartTop;
            PlaceFooting(drive, Height, livery);

            // Where the rope leaves each station, along the line and in elevation.
            float S(Vector3 p) => Vector3.Dot(p - bottom, dir);
            var bExit = retRig != null ? retRig.Socket("rope_right_out").position : bottom + dir * 6 + Vector3.up * (float)(Height(bottom) + 3.5);
            var tExit = driveRig != null ? driveRig.Socket("rope_left_out").position : top - dir * 4 + Vector3.up * (float)(Height(top) + 3.04);
            float length = Vector3.Distance(Flat(bottom), Flat(top));
            var b = frame.ToAlbers(bottom.x, bottom.z);
            var t = frame.ToAlbers(top.x, top.z);
            // Each terminal stands on its pad (LF-12 v2), raised by any last resort in its fill; the station towers' foundations
            // stand as high as their terminals need (TW-02 v3, derived as the build derived them).
            Rope = LiftRope.Solve(type, ground, b.X, b.Y, t.X, t.Y, lift.Towers, lift.Stance, S(bExit), bExit.y, length - S(tExit), tExit.y);

            // Towers: the base level on its pier, the mast sections and head along the mast's axis (leaned down the line
            // by the rope's lean, TW-14), the rope at the head's sockets.
            var heads = new List<LiftRig>();
            for (int i = 0; i < Rope.Towers.Count; i++)
            {
                var sol = Rope.Towers[i];
                string head = HeadId(sol.Head);
                _heads.Add(head);
                var at = bottom + dir * (float)sol.BaseS;
                at.y = (float)sol.BaseY;
                var facing = Quaternion.LookRotation(-dir);   // the platform side (+Z) faces the return
                var lean = Quaternion.AngleAxis((float)(sol.Lean * Mathf.Rad2Deg), Vector3.Cross(Vector3.up, -dir));
                _spawning = i;
                var h = SpawnTower(head, at, facing, lean, lift.Towers[i].HeightMm / 1000f, (float)sol.Reveal, livery);
                heads.Add(h != null ? h.GetComponent<LiftRig>() : null);
            }

            _spawning = PartRope;
            // Posable models (the model contract): each train rocks to the rope and the rope runs over its sheaves.
            if (retRig != null && driveRig != null && TryPosedLoop(type, retRig, driveRig, heads, bottom, dir, right, livery)) return;

            // The rope loop: up the right side (loaded), round the drive's bullwheel, down the left, round the return's.
            var loop = new List<Vector3>(4096);
            float half = (float)type.GaugeMetres / 2;
            if (retRig != null) loop.AddRange(StationRope(retRig, "right"));
            AddSpans(loop, Rope.Empty, bottom, dir, right * half, S(bExit), S(tExit), true);   // RP-08: the load it carries
            if (driveRig != null)
            {
                var down = StationRope(driveRig, "left");
                down.Reverse();
                loop.AddRange(down);
                AddWheel(loop, driveRig, driveRig.Socket("rope_left_bw").position, driveRig.Socket("rope_right_bw").position);
                loop.AddRange(StationRope(driveRig, "right"));
            }
            AddSpans(loop, Rope.Empty, bottom, dir, -right * half, S(bExit), S(tExit), false);
            if (retRig != null)
            {
                var back = StationRope(retRig, "left");
                back.Reverse();
                loop.AddRange(back);
                AddWheel(loop, retRig, retRig.Socket("rope_left_bw").position, retRig.Socket("rope_right_bw").position);
            }
            SetLoop(loop);
            BuildCable((float)type.RopeDiameterMetres / 2);
            PrepareChairs(Prefab("chair"), livery, type, Track(type, retRig, driveRig, bottom, dir));
        }

        /// <summary>
        /// Findings #2 and #6 (task 07): with posable models, both rope sides are posed train by train, from the return's
        /// hold-down row over every tower's head to the drive's support row (<see cref="RopeOverSheaves.PoseLine"/>), the
        /// pivots turned to match, and the loop built through the sheaves' contact points and the spans between them.
        /// Returns false (and changes nothing) when any model isn't posable.
        /// </summary>
        bool TryPosedLoop(LiftType type, LiftRig retRig, LiftRig driveRig, List<LiftRig> heads, Vector3 bottom, Vector3 dir, Vector3 right, LiftLivery livery)
        {
            // Our right (the loaded, uphill rope) is the return's right, and the towers' and the drive's left: they face down the line.
            string[] up = { "r", "l", "l" }, down = { "l", "r", "r" };
            // Every tower's head must turn; a terminal row that can't (the Monta return's fixed row until its rebuild) is
            // posed round as a fixed sheave at its rope exit (LT33), and said so in the build log.
            RigidNote = "";
            foreach (var sides in new[] { up, down })
                foreach (var h in heads) if (!TrainPosing.Posable(h, sides[1])) return false;
            double rho = LiftType.SheaveRadiusMetres + type.RopeDiameterMetres / 2;
            TrainPosing.Train End(LiftRig rig, string side, bool above, string which)
            {
                if (TrainPosing.Posable(rig, side)) return TrainPosing.Read(rig, side, bottom, dir, right);
                if (!RigidNote.Contains(which)) RigidNote += (RigidNote.Length > 0 ? ", " : "") + which;
                return TrainPosing.Fixed(rig, side, above, rho, bottom, dir, right);
            }
            var loop = new List<Vector3>(4096);
            for (int side = 0; side < 2; side++)
            {
                var sides = side == 0 ? up : down;
                var trains = new List<TrainPosing.Train> { End(retRig, sides[0], type.Bottom.Row == TerminalRow.Support, "the bottom terminal's row") };
                var above = new List<bool> { type.Bottom.Row == TerminalRow.Support };   // a hold-down row holds the rope down
                var turn = new List<int> { 0 };
                for (int i = 0; i < heads.Count; i++)
                {
                    trains.Add(TrainPosing.Read(heads[i], sides[1], bottom, dir, right));
                    // The drawn (empty, RP-08) rope's own bend over the head picks its side: turning up, beneath (a c8's
                    // hold-down row); turning down, on top (finding r4 #2).
                    double bend = Rope.Towers[i].BendEmpty;
                    above.Add(bend <= 0);
                    turn.Add(bend > 0 ? 1 : bend < 0 ? -1 : 0);
                    bool support = TowerForces.IsSupport(Rope.Towers[i].Head);
                    if (side == 0 && (support && bend > 0 || TowerForces.IsHoldDown(Rope.Towers[i].Head) && bend < 0 || Rope.Towers[i].Head == LiftRope.Head.TooSharp))
                    {
                        SideMismatches++;
                        MismatchNote += $" #{i} {Rope.Towers[i].Head} at {Rope.Towers[i].BaseS / 0.3048:F0} ft (bend {bend * 180 / Math.PI:F1}°/{Rope.Towers[i].BendLoaded * 180 / Math.PI:F1}° loaded)";
                    }
                }
                // The drive's four sheaves equalise a little (owner: "Keep it 4 sheaves, but allow them to equalize a
                // little"): within the model's stops, beam ±10° and rockers ±5°.
                trains.Add(End(driveRig, sides[2], type.Top.Row == TerminalRow.Support, "the top terminal's row"));
                above.Add(type.Top.Row == TerminalRow.Support);
                turn.Add(0);
                if (trains.Exists(t => t == null)) return false;
                var trees = trains.ConvertAll(t => t.Tree);
                var holds = trains.ConvertAll(t => t.HoldRow);
                double y0 = trees[0].Sheaves[0].AxleW;
                // RP-08 (owner, round 3): the rope hangs at the load it carries; the chairs are empty, so both sides take the
                // empty sag. The checks keep both load cases.
                double w = type.EmptyNewtonsPerMetre;
                var posed = RopeOverSheaves.PoseLine(trees, above, holds, rho, type.CounterweightNewtons, w, y0, true, 3, turn);
                for (int i = 0; i < trains.Count; i++) TrainPosing.Apply(trains[i], posed.Poses[i], dir);
                PoseError = Math.Max(PoseError, posed.TangentError);

                // The side's rope up the line, in s order: each train's contacts, and the spans between them.
                var points = new List<Vector3>();
                for (int i = 0; i < trains.Count; i++)
                {
                    var pose = posed.Poses[i];
                    var lateral = right * trains[i].Lateral;
                    for (int k = 0; k < pose.ContactS.Length; k++)
                        if (pose.Touched[k]) points.Add(bottom + dir * (float)pose.ContactS[k] + Vector3.up * (float)pose.ContactW[k] + lateral);
                    if (i + 1 < trains.Count)
                    {
                        var sp = posed.Spans[i];
                        var lat2 = right * ((trains[i].Lateral + trains[i + 1].Lateral) / 2);
                        int steps = Mathf.Max(2, Mathf.CeilToInt((float)(sp.To - sp.From) / 2f));
                        for (int k = 1; k < steps; k++)
                        {
                            double sv = sp.From + (sp.To - sp.From) * k / steps;
                            points.Add(bottom + dir * (float)sv + Vector3.up * (float)sp.Y(sv) + lat2);
                        }
                    }
                }
                // A detachable's station rope climbs to its bend sheaves before it runs level out (G2).
                void Bend(LiftRig rig, string sideName) { var b = rig.Socket($"rope_{sideName}_bend"); if (b != null) loop.Add(b.position); }
                if (side == 0)
                {
                    loop.Add(retRig.Socket("rope_right_bw").position);
                    Bend(retRig, "right");
                    loop.AddRange(points);
                    Bend(driveRig, "left");
                    loop.Add(driveRig.Socket("rope_left_bw").position);
                    AddWheel(loop, driveRig, driveRig.Socket("rope_left_bw").position, driveRig.Socket("rope_right_bw").position);
                }
                else
                {
                    loop.Add(driveRig.Socket("rope_right_bw").position);
                    Bend(driveRig, "right");
                    points.Reverse();
                    loop.AddRange(points);
                    Bend(retRig, "left");
                    loop.Add(retRig.Socket("rope_left_bw").position);
                    AddWheel(loop, retRig, retRig.Socket("rope_left_bw").position, retRig.Socket("rope_right_bw").position);
                }
            }
            // G8: a pivot marked to follow another (the D6's walkways, pinned to their crossarm's hanger) takes that pivot's
            // world turn, whatever its own parents do, so the walkway turns with its train.
            foreach (var rig in heads) Follow(rig);
            Posed = true;
            SetLoop(loop);
            BuildCable((float)type.RopeDiameterMetres / 2);
            PrepareChairs(Prefab("chair"), livery, type, Track(type, retRig, driveRig, bottom, dir));
            return true;
        }

        /// <summary>Whether the trains were posed to the rope (posable models), and the largest tangent mismatch (radians).</summary>
        public bool Posed { get; private set; }
        /// <summary>The terminal rows posed as fixed (no pivots, LT33), for the build log; empty when every train turned.</summary>
        public string RigidNote { get; private set; } = "";
        /// <summary>Heads whose type disagrees with the drawn rope's bend over them (or too sharp to carry it): reported.</summary>
        public int SideMismatches { get; private set; }
        public string MismatchNote { get; private set; } = "";
        public double PoseError { get; private set; }

        static Vector3 Flat(Vector3 p) => new Vector3(p.x, 0, p.z);

        string HeadId(LiftRope.Head head)
        {
            // TooSharp is drawn as the kit's biggest support head: the D6's b10, the quads' b8 (until TW-06 is resolved).
            if (head == LiftRope.Head.TooSharp && _kit != Quad && Model(_kit + "tower_b10") != null) return "tower_b10";
            switch (head)
            {
                case LiftRope.Head.S4: return "tower_s4";
                case LiftRope.Head.S6: return "tower_s6";
                case LiftRope.Head.D8: return "tower_d8";
                case LiftRope.Head.C8: return "tower_c8";
                case LiftRope.Head.S10: return "tower_s10";   // the wide kit's (G6, LT41)
                case LiftRope.Head.B10: return "tower_b10";
                case LiftRope.Head.D10: return "tower_d10";
                default: return "tower_b8";   // B8, and TooSharp drawn as the biggest support until task 09 reports it
            }
        }

        GameObject PlaceTerminal(GameObject prefab, Vector3 bullwheel, Quaternion rotation, Func<Vector3, double> height, LiftLivery livery,
                                 double? level = null)
        {
            if (prefab == null) return null;
            var rig = prefab.GetComponent<LiftRig>();
            var l = rig != null ? rig.Socket("rope_left_bw") : null;
            var r = rig != null ? rig.Socket("rope_right_bw") : null;
            var centre = l != null && r != null ? (l.localPosition + r.localPosition) / 2 : Vector3.zero;
            centre.y = 0;
            var origin = bullwheel - rotation * centre;
            origin.y = (float)(level ?? height(origin));
            return Spawn(prefab, origin, rotation, livery);
        }

        void PlaceFooting(GameObject terminal, Func<Vector3, double> height, LiftLivery livery)
        {
            // A footing at grade under every pier: "pier_top", or "pier_top_<n>" for a terminal on several (the Monta's, LT36).
            var footing = Prefab("terminal_footing");
            var rig = terminal != null ? terminal.GetComponent<LiftRig>() : null;
            if (footing == null || rig == null || rig.Sockets == null) return;
            foreach (var top in rig.Sockets)
            {
                if (top == null || top.name.IndexOf("_socket_pier_top", StringComparison.Ordinal) < 0) continue;
                var at = new Vector3(top.position.x, 0, top.position.z);
                at.y = (float)height(at);
                Spawn(footing, at, terminal.transform.rotation, livery);
            }
        }

        GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, LiftLivery livery)
        {
            var go = Object.Instantiate(prefab, position, rotation, Root.transform);
            _partOf[go] = _spawning;
            _materials?.Apply(go, livery);
            return go;
        }

        /// <summary>
        /// A tower: the base on the ground (level, or leaned with the mast, TW-14), as many 1 m mast sections as fit and the
        /// head, so the rope passes the head <paramref name="steel"/> (the stored 5 ft step) from the mast's foot along its
        /// axis; the base rides up or down by the remainder (under half a metre), as a footing would, and up by a station
        /// tower's foundation <paramref name="reveal"/> (TW-02 v3).
        /// </summary>
        GameObject SpawnTower(string headId, Vector3 position, Quaternion facing, Quaternion lean, float steel, float reveal, LiftLivery livery)
        {
            var head = Prefab(headId);
            var mast = Prefab("tower_mast");
            // A leaned mast stands on the leaned base for its angle (TW-14: 2.5° steps); until those are imported, the plumb one.
            lean.ToAngleAxis(out float degrees, out _);
            int tenths = Mathf.RoundToInt(degrees / 2.5f) * 25;
            var foundation = (tenths > 0 ? Prefab($"tower_base_l{tenths:000}") : null) ?? Prefab("tower_base");
            if (head == null || mast == null || foundation == null) return null;
            bool leanedBase = !foundation.name.EndsWith("tower_base", StringComparison.Ordinal);
            var footLocal = foundation.GetComponent<LiftRig>().Socket("mast_foot").localPosition;
            float foot = footLocal.y;
            float ropeOnHead = head.GetComponent<LiftRig>().Socket("rope_right").localPosition.y;
            int sections = Mathf.Max(1, Mathf.RoundToInt(steel - foot - ropeOnHead));
            float rise = steel - (foot + sections + ropeOnHead) + reveal;
            var basePos = position + Vector3.up * rise;
            Spawn(foundation, basePos, facing, livery);
            var axis = lean * Vector3.up;
            var turned = lean * facing;
            var mastFoot = leanedBase ? basePos + facing * footLocal : basePos + Vector3.up * foot;
            for (int i = 0; i < sections; i++)
                Spawn(mast, mastFoot + axis * i, turned, livery);
            return Spawn(head, mastFoot + axis * sections, turned, livery);
        }

        /// <summary>
        /// The tower in hand (F4, LT38): one tower by itself as <see cref="Build"/> stands one (base, mast sections, the
        /// head), its foot at <see cref="Root"/>'s origin, the platform side (+Z) toward the bottom terminal, leaned
        /// <paramref name="leanRadians"/> down the line. Whatever was built before goes.
        /// </summary>
        public void BuildLoneTower(LiftRope.Head head, float leanRadians, float steel, LiftLivery livery, string kit = Quad)
        {
            Clear();
            _kit = kit ?? Quad;
            var at = Root.transform;
            var (position, rotation) = (at.position, at.rotation);
            at.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var lean = Quaternion.AngleAxis(leanRadians * Mathf.Rad2Deg, Vector3.right);
            SpawnTower(HeadId(head), Vector3.zero, Quaternion.identity, lean, steel, 0, livery);
            at.SetPositionAndRotation(position, rotation);
        }

        /// <summary>
        /// One side's rope in a station, from the bullwheel out to where it leaves (the Lift Lab's rope up the line): level
        /// to the hold-down row where the terminal has one (rope_*_hold), round the rope's circle under it to rope_*_out.
        /// </summary>
        static List<Vector3> StationRope(LiftRig rig, string side)
        {
            var along = rig.transform.forward;
            var exit = rig.Socket($"rope_{side}_out").position;
            var hold = rig.Socket($"rope_{side}_hold");
            var points = new List<Vector3> { rig.Socket($"rope_{side}_bw").position };
            if (hold != null)
            {
                var h0 = hold.position;
                float d = Vector3.Dot(exit - h0, along), h = exit.y - h0.y;
                if (Mathf.Abs(h) > 1e-4f)
                {
                    float r = (d * d + h * h) / (2f * h);
                    float theta = Mathf.Asin(Mathf.Clamp(d / r, -1f, 1f));
                    const int steps = 12;
                    for (int k = 0; k <= steps; k++)
                    {
                        float a = theta * k / steps;
                        points.Add(h0 + along * (r * Mathf.Sin(a)) + Vector3.up * (r * (1f - Mathf.Cos(a))));
                    }
                }
            }
            // A detachable's station (G2): the rope climbs in the wheel's plane to its bend sheaves, then runs level out.
            var bend = rig.Socket($"rope_{side}_bend");
            if (hold == null && bend != null) points.Add(bend.position);
            points.Add(exit);
            return points;
        }

        /// <summary>The spans between the stations, every 2 m, offset to one rope's side; up or down the line.</summary>
        static void AddSpans(List<Vector3> loop, IReadOnlyList<LiftRope.Span> spans, Vector3 bottom, Vector3 dir, Vector3 side,
                             float from, float to, bool up)
        {
            int steps = Mathf.Max(2, Mathf.CeilToInt((to - from) / 2f));
            for (int k = 1; k < steps; k++)
            {
                float f = up ? (float)k / steps : 1f - (float)k / steps;
                float s = Mathf.Lerp(from, to, f);
                var p = bottom + dir * s + side;
                p.y = (float)LiftRope.RopeAt(spans, s);
                loop.Add(p);
            }
        }

        /// <summary>Half a turn round a bullwheel, behind it (away from the line), from one rope to the other.</summary>
        static void AddWheel(List<Vector3> loop, LiftRig rig, Vector3 from, Vector3 to)
        {
            var centre = (from + to) / 2;
            var r = from - centre;
            var back = -rig.transform.forward;
            float sign = Vector3.Dot(Vector3.Cross(r, back), Vector3.up) >= 0 ? 1 : -1;
            const int steps = 24;
            for (int k = 1; k < steps; k++)
                loop.Add(centre + Quaternion.AngleAxis(sign * 180f * k / steps, Vector3.up) * r);
        }

        void SetLoop(List<Vector3> loop)
        {
            if (loop.Count > 1 && (loop[0] - loop[loop.Count - 1]).sqrMagnitude > 1e-6f) loop.Add(loop[0]);
            _loop = loop.ToArray();
            _walked = new float[_loop.Length];
            for (int i = 1; i < _loop.Length; i++) _walked[i] = _walked[i - 1] + Vector3.Distance(_loop[i - 1], _loop[i]);
            _loopLength = _walked.Length > 0 ? _walked[_walked.Length - 1] : 0;
            _bounds = new Bounds(_loop.Length > 0 ? _loop[0] : Vector3.zero, Vector3.zero);
            foreach (var p in _loop) _bounds.Encapsulate(p);
            _bounds.Expand(new Vector3(8, 8, 8));   // the chairs hang below the rope
        }

        /// <summary>
        /// The cable: a six-sided tube along the loop, its rings stored as the rope's centre plus the outward direction,
        /// so the LiftCable shader can keep it at least about a pixel wide however far away it is.
        /// </summary>
        void BuildCable(float radius)
        {
            const int sides = 6;
            int n = _loop.Length;
            if (n < 2 || _cableMaterial == null) return;
            var vertices = new Vector3[n * sides];
            var normals = new Vector3[n * sides];
            var uv = new Vector2[n * sides];
            var indices = new int[(n - 1) * sides * 6];
            for (int i = 0; i < n; i++)
            {
                var tangent = (_loop[Mathf.Min(n - 1, i + 1)] - _loop[Mathf.Max(0, i - 1)]).normalized;
                var a = Vector3.Cross(tangent, Vector3.up);
                if (a.sqrMagnitude < 1e-6f) a = Vector3.right;
                a.Normalize();
                var bb = Vector3.Cross(a, tangent);
                for (int s = 0; s < sides; s++)
                {
                    float ang = s * Mathf.PI * 2 / sides;
                    var outward = a * Mathf.Cos(ang) + bb * Mathf.Sin(ang);
                    int v = i * sides + s;
                    vertices[v] = _loop[i] - Root.transform.position;
                    normals[v] = outward;
                    uv[v] = new Vector2(radius, _walked[i]);
                }
            }
            int at = 0;
            for (int i = 0; i + 1 < n; i++)
                for (int s = 0; s < sides; s++)
                {
                    int a0 = i * sides + s, a1 = i * sides + (s + 1) % sides, b0 = a0 + sides, b1 = a1 + sides;
                    indices[at++] = a0; indices[at++] = b0; indices[at++] = a1;
                    indices[at++] = a1; indices[at++] = b0; indices[at++] = b1;
                }
            _cable = new Mesh { name = "Haul rope", indexFormat = IndexFormat.UInt32 };
            _cable.vertices = vertices;
            _cable.normals = normals;
            _cable.uv = uv;
            _cable.triangles = indices;
            var bounds = _cable.bounds;
            bounds.Expand(4);
            _cable.bounds = bounds;
            var go = new GameObject("Haul rope");
            go.transform.SetParent(Root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = _cable;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _cableMaterial;
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        /// <summary>Takes the chair's LODs apart into meshes and materials for instanced drawing, and sizes the matrices.</summary>
        static void Follow(LiftRig rig)
        {
            if (rig == null || rig.PivotFollows == null) return;
            for (int i = 0; i < rig.PivotFollows.Length && i < rig.Pivots.Length; i++)
            {
                int f = rig.PivotFollows[i];
                if (f >= 0 && rig.Pivots[i] != null && rig.Pivots[f] != null) rig.Pivots[i].rotation = rig.Pivots[f].rotation;
            }
        }

        /// <summary>
        /// The carriers' track (LT41): a detachable's runs up the loaded rope, round the top station's rail, down the other
        /// rope and round the bottom station's (the terminals' <c>chairs</c> paths); a fixed grip's is the rope loop.
        /// </summary>
        CarrierTrack Track(LiftType type, LiftRig bottomRig, LiftRig topRig, Vector3 bottom, Vector3 dir)
        {
            float v = (float)type.SpeedMetresPerSecond;
            var bottomPath = bottomRig != null ? bottomRig.Path("chairs") : null;
            var topPath = topRig != null ? topRig.Path("chairs") : null;
            if (!type.Detachable || bottomPath == null || topPath == null || bottomPath.Length < 2 || topPath.Length < 2)
                return CarrierTrack.Uniform(_loop, v);
            var bottomRail = new List<Vector3>(bottomPath.Length);
            foreach (var q in bottomPath) bottomRail.Add(bottomRig.transform.TransformPoint(q));
            var topRail = new List<Vector3>(topPath.Length);
            foreach (var q in topPath) topRail.Add(topRig.transform.TransformPoint(q));
            float S(Vector3 q) => Vector3.Dot(q - bottom, dir);
            float from = float.MinValue, to = float.MaxValue;
            foreach (var q in bottomRail) from = Mathf.Max(from, S(q));
            foreach (var q in topRail) to = Mathf.Min(to, S(q));
            // The rope between the rails' ends, each side in the loop's (travel) order: up the right, down the left.
            var right = Vector3.Cross(Vector3.up, dir);
            var up = new List<Vector3>();
            var down = new List<Vector3>();
            foreach (var q in _loop)
            {
                float s = S(q);
                if (s <= from || s >= to) continue;
                (Vector3.Dot(q - bottom, right) > 0 ? up : down).Add(q);
            }
            if (up.Count < 2 || down.Count < 2) return CarrierTrack.Uniform(_loop, v);
            // Each rail taken from the end nearest the rope that arrives at it.
            void Orient(List<Vector3> rail, Vector3 arriving)
            {
                if (Vector3.Distance(rail[rail.Count - 1], arriving) < Vector3.Distance(rail[0], arriving)) rail.Reverse();
            }
            Orient(topRail, up[up.Count - 1]);
            Orient(bottomRail, down[down.Count - 1]);
            return CarrierTrack.Detachable(up, topRail, down, bottomRail, v, (float)type.AlightSpeed(v), (float)type.BoardSpeed(v), (float)type.TurnSpeed(v),
                                           (float)LiftType.RampAccel);
        }

        void PrepareChairs(GameObject chair, LiftLivery livery, LiftType type, CarrierTrack track)
        {
            _chairCount = 0;
            _track = track;
            if (chair == null || track == null || track.LoopTime <= 0) return;
            // LT41: the carriers come round one loading interval apart, so they bunch where they're slow (a detachable's
            // stations) and spread where they're fast; on a fixed grip that's the spacing.
            _chairCount = Mathf.Max(1, Mathf.RoundToInt(track.LoopTime / (float)type.IntervalSeconds));
            _chairInterval = track.LoopTime / _chairCount;
            var instance = Object.Instantiate(chair);
            _materials?.Apply(instance, livery);
            var group = instance.GetComponent<LODGroup>();
            var root = instance.transform.worldToLocalMatrix;
            var rig = instance.GetComponent<LiftRig>();
            var hingePivot = rig != null ? rig.Pivot("hanger") : null;
            _splitChair = hingePivot != null;
            _hinge = _splitChair ? instance.transform.InverseTransformPoint(hingePivot.position) : Vector3.zero;
            if (group != null)
            {
                var lods = group.GetLODs();
                _chairLods = new ChairLod[lods.Length];
                _lodDistances = new float[lods.Length];
                float size = group.size;
                for (int i = 0; i < lods.Length; i++)
                {
                    var parts = new List<ChairPart>();
                    foreach (var r in lods[i].renderers)
                    {
                        var mf = r != null ? r.GetComponent<MeshFilter>() : null;
                        if (mf == null || mf.sharedMesh == null) continue;
                        bool hanger = false;
                        for (var x = r.transform; x != null && x != instance.transform; x = x.parent)
                            if (x.name.EndsWith("_pivot_hanger", StringComparison.Ordinal)) hanger = true;
                        // A hanger part is kept relative to its hinge, so it can hang plumb from wherever the grip puts it.
                        var local = root * r.transform.localToWorldMatrix;
                        if (hanger) local = Matrix4x4.Translate(-_hinge) * local;
                        parts.Add(new ChairPart
                        {
                            Mesh = mf.sharedMesh, Materials = InstancedCopies(r.sharedMaterials), Local = local,
                            Matrices = new Matrix4x4[_chairCount], Hanger = hanger,
                        });
                    }
                    _chairLods[i] = new ChairLod { Parts = parts.ToArray() };
                    // Distance at which the chair's screen height falls below this LOD's threshold (60° view, lod bias).
                    float h = Mathf.Max(lods[i].screenRelativeTransitionHeight, 1e-4f);
                    _lodDistances[i] = size / (2f * Mathf.Tan(30f * Mathf.Deg2Rad) * h) * QualitySettings.lodBias;
                }
            }
            Gone(instance);
        }

        readonly List<Material> _instanced = new List<Material>();

        Material[] InstancedCopies(Material[] source)
        {
            var copies = new Material[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == null) continue;
                var m = source[i];
                if (!m.enableInstancing)
                {
                    m = new Material(m) { enableInstancing = true };
                    _instanced.Add(m);
                }
                copies[i] = m;
            }
            return copies;
        }

        /// <summary>
        /// Draws the chairs for this frame: each at (index × spacing + speed × time) round the loop (RP-07), hanging plumb
        /// from its grip and facing the way it travels, at the LOD its distance from the camera calls for. No allocation.
        /// </summary>
        public void DrawChairs(Camera camera, double seconds)
        {
            if (_chairCount == 0 || _chairLods.Length == 0 || camera == null) return;
            foreach (var lod in _chairLods)
                foreach (var p in lod.Parts) p.Count = 0;
            var eye = camera.transform.position;
            float loopTime = _track.LoopTime;
            float shift = (float)(seconds % loopTime);
            var points = _track.Points;
            var walked = _track.Walked;
            int seg = 1;
            for (int k = 0; k < _chairCount; k++)
            {
                float t = (_chairInterval * k + shift) % loopTime;
                float d = _track.DistanceAt(t, ref seg);
                float span = walked[seg] - walked[seg - 1];
                var a = points[seg - 1];
                var b = points[seg];
                var p = span > 0 ? Vector3.Lerp(a, b, (d - walked[seg - 1]) / span) : a;
                float dist = Vector3.Distance(eye, p);
                int level = 0;
                while (level < _lodDistances.Length && dist > _lodDistances[level]) level++;
                if (level >= _chairLods.Length) continue;   // past the last LOD: culled, as the LODGroup would
                var flat = new Vector3(b.x - a.x, 0, b.z - a.z);
                var rotation = flat.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(flat) : Quaternion.identity;
                var world = Matrix4x4.TRS(p, rotation, Vector3.one);
                // A split chair (finding #3): the grip lies along the rope; the hanger hangs plumb from its hinge.
                var grip = world;
                var hang = world;
                if (_splitChair)
                {
                    var along = b - a;
                    var gripRot = along.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(along) : rotation;
                    grip = Matrix4x4.TRS(p, gripRot, Vector3.one);
                    hang = Matrix4x4.TRS(p + gripRot * _hinge, rotation, Vector3.one);
                }
                foreach (var part in _chairLods[level].Parts)
                    part.Matrices[part.Count++] = (part.Hanger ? hang : grip) * part.Local;
            }
            foreach (var lod in _chairLods)
                foreach (var part in lod.Parts)
                {
                    if (part.Count == 0) continue;
                    for (int sub = 0; sub < part.Mesh.subMeshCount && sub < part.Materials.Length; sub++)
                    {
                        if (part.Materials[sub] == null) continue;
                        var rp = new RenderParams(part.Materials[sub]) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true, worldBounds = _bounds };
                        if (_highlighted) rp.renderingLayerMask |= LiftOutline.Layer;   // the hovered lift's chairs glow too (LT23)
                        Graphics.RenderMeshInstanced(rp, part.Mesh, sub, part.Matrices, part.Count);
                    }
                }
        }

        void Clear()
        {
            if (Root != null)   // at quit the scene may have destroyed it first
                for (int i = Root.transform.childCount - 1; i >= 0; i--) Gone(Root.transform.GetChild(i).gameObject);
            if (_cable != null) Gone(_cable);
            _cable = null;
            foreach (var m in _instanced) Gone(m);
            _instanced.Clear();
            _heads.Clear();
            Posed = false;
            PoseError = 0;
            _chairLods = Array.Empty<ChairLod>();
            _chairCount = 0;
            ForgetPick();
        }

        // Destroy in play, at once outside it (EditMode tests and editor tools build lifts too).
        static void Gone(Object o)
        {
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }

        public void Dispose()
        {
            Clear();
            if (Root != null) Gone(Root);
        }
    }
}
