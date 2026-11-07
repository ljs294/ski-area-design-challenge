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
        /// <summary>The surface tow's kit (the Monta T-bar, v0.3.2): masts leaned across the line, heads level, its own parts.</summary>
        const string TowKit = "monta_tbar_";
        /// <summary>D11 (monta_tbar.json tower.lean): every tow mast leans 8° across the line toward the loaded track.</summary>
        const float TowLeanDegrees = 8f;
        bool Tow => _kit == TowKit;
        Mesh _cable;
        Mesh _apron;   // a carpet's skirts and stations run down to the ground (owner 2026-11-06)

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

        /// <summary>A terminal's bullwheel, spun with the rope (task P3-16, BB2).</summary>
        struct Wheel
        {
            public Transform Pivot;
            public Quaternion Rest;
            public Vector3 Axis;
            /// <summary>+1 or -1: the way round the rope runs, about the pivot's own axis.</summary>
            public float Sign;
        }

        Wheel[] _wheels = Array.Empty<Wheel>();
        /// <summary>The bullwheels' turn rate, radians a second: rope speed over the pitch radius.</summary>
        float _wheelRate;

        /// <summary>A sheave the rope runs over, spun with it (owner, 2026-11-06: "ensure the sheaves on the tower are
        /// rotating properly").</summary>
        struct RopeSheave
        {
            public Transform Pivot;
            public Quaternion Rest;
            public Vector3 Axis;
            /// <summary>Radians a second about <see cref="Axis"/>, signed: rope speed over the groove's radius, the way the
            /// rope runs over it.</summary>
            public float Rate;
            /// <summary>Where the rope touches it, as posed (tests).</summary>
            public Vector3 Contact;
        }

        RopeSheave[] _sheaves = Array.Empty<RopeSheave>();
        /// <summary>The box round <see cref="_sheaves"/>' contacts, world metres.</summary>
        Vector3 _sheaveLo, _sheaveHi;
        /// <summary>Sheaves turn only this close to the eye (metres): past it a turning wheel can't be told from a still one.</summary>
        const float SheaveSpinDistance = 300f;

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
            var l = rig != null ? rig.HeadSocket("rope_left_bw") : null;
            var r = rig != null ? rig.HeadSocket("rope_right_bw") : null;
            if (l == null || r == null) return 0;
            return -(l.localPosition.z + r.localPosition.z) / 2;
        }

        /// <summary>
        /// Where a terminal's rope leaves it toward the line (TW-16): how far along the line from the bullwheel, and how
        /// high above the terminal's origin (on the ground), from its rope_right_bw and rope_right_out sockets.
        /// </summary>
        public static bool RopeExit(LiftModelSet models, string assetId, out float along, out float rise) => RopeExit(models, assetId, null, out along, out rise);

        /// <summary>The same for the model's head <paramref name="head"/> (LD9; null: as built).</summary>
        public static bool RopeExit(LiftModelSet models, string assetId, string head, out float along, out float rise)
        {
            along = rise = 0;
            var prefab = models != null ? models.Find(assetId) : null;
            var rig = prefab != null ? prefab.GetComponent<LiftRig>() : null;
            int h = rig != null ? rig.HeadIndex(head) : -1;
            var bw = rig != null ? rig.HeadSocket("rope_right_bw") : null;
            var exit = rig != null ? rig.HeadSocket(h, "rope_right_out") : null;
            if (bw == null || exit == null) return false;
            along = exit.localPosition.z - bw.localPosition.z;
            rise = exit.localPosition.y;
            return true;
        }

        /// <summary>
        /// A terminal's footprint from its model (CL-09): how far it reaches behind its bullwheel (away from the line) and
        /// in front, and its half width, in metres, from its meshes' bounds in its own frame (+Z toward the other terminal).
        /// </summary>
        public static bool TerminalExtent(LiftModelSet models, string assetId, out float behind, out float ahead, out float halfWidth) =>
            TerminalExtent(models, assetId, null, out behind, out ahead, out halfWidth);

        /// <summary>The same with the model's head <paramref name="head"/> shown (LD9; null: as built).</summary>
        public static bool TerminalExtent(LiftModelSet models, string assetId, string head, out float behind, out float ahead, out float halfWidth)
        {
            behind = ahead = halfWidth = 0;
            var prefab = models != null ? models.Find(assetId) : null;
            var rig = prefab != null ? prefab.GetComponent<LiftRig>() : null;
            int shown = rig != null ? rig.HeadIndex(head) : -1;
            HashSet<Transform> showing = null, hiding = null;
            if (shown >= 0)
            {
                showing = new HashSet<Transform>();
                hiding = new HashSet<Transform>();
                for (int k = 0; k < rig.Heads.Length; k++)
                    foreach (int p in rig.Heads[k].Pivots) (k == shown ? showing : hiding).Add(rig.Pivots[p]);
            }
            if (rig == null || rig.HeadSocket("rope_left_bw") == null || rig.HeadSocket("rope_right_bw") == null) return false;
            float bw = (rig.HeadSocket("rope_left_bw").localPosition.z + rig.HeadSocket("rope_right_bw").localPosition.z) / 2;
            var root = prefab.transform.worldToLocalMatrix;
            float minZ = float.MaxValue, maxZ = float.MinValue, maxX = 0;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !Shown(mf.transform, prefab.transform, showing, hiding)) continue;   // a hidden head's steel
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

        /// <summary>Whether t and every parent up to root are active (a prefab asset isn't in a scene, so
        /// activeInHierarchy doesn't say): a terminal's hidden heads (drive at either end) are inactive.</summary>
        static bool Shown(Transform t, Transform root, HashSet<Transform> showing = null, HashSet<Transform> hiding = null)
        {
            for (; t != null && t != root; t = t.parent)
            {
                if (hiding != null && hiding.Contains(t)) return false;
                if (showing != null && showing.Contains(t)) continue;   // LD9: the head measured, shown or not in the prefab
                if (!t.gameObject.activeSelf) return false;
            }
            return true;
        }

        /// <summary>
        /// Builds the lift: terminals at its bullwheels, towers, the solved rope and the chairs. Heights are the edited
        /// ground (<paramref name="ground"/>, in the package's frame); <paramref name="frame"/> turns design points into
        /// local metres. The livery is the lift's two colours (LT41): the primary on the parts the models mark as livery, the accent on theirs.
        /// </summary>
        /// <remarks>Each terminal stands at the lift's stored level (SK14, save v2): a raised terminal stands on its pier
        /// above the ground (LF-12 v3, pads only cut). A lift without levels (from a v1 save) stands at its origins' ground.</remarks>
        readonly List<float> _asBuilt = new List<float>();
        // What Build stood the lift from, so Reheight can stand its towers again (BR21).
        Lift _builtFrom;
        LiftLivery _livery;

        /// <summary>TW-02 v4: each tower's steel as built over the ground it stands on now, metres (its stored height until hand
        /// grading moves the ground at its base).</summary>
        public IReadOnlyList<float> AsBuiltHeights => _asBuilt;

        /// <summary>
        /// Builds the lift over <paramref name="ground"/>. <paramref name="fixedBases"/> (TW-02 v4, task 15): each tower's
        /// base level as the lift's own earthwork left it (<see cref="LiftBuild.Works"/>), so the heads and rope stay where
        /// they were built while hand grading moves the ground; each tower then stands on <paramref name="ground"/> with the
        /// steel and foundation it needs (<see cref="AsBuiltHeights"/>). Null: the bases on <paramref name="ground"/>.
        /// </summary>
        public void Build(Lift lift, IHeightSource ground, LocalFrame frame, LiftLivery livery, IReadOnlyList<double> fixedBases = null)
        {
            Clear();
            _asBuilt.Clear();
            _builtFrom = lift;
            _livery = livery;
            Bed = null;
            (double Bottom, double Top)? levels = lift.Levels is LiftLevels v ? (v.BottomMetres, v.TopMetres) : ((double, double)?)null;
            var type = LiftTypes.Measured(LiftType.Of(lift), _models);
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
            _belt = false;
            _sheaves = Array.Empty<RopeSheave>();
            if (type.Belt)
            {
                Rope = null;
                BuildBelt(type, ground, frame, livery, bottom, top, dir, levels);
                return;
            }

            // Terminals: each end's model (TW-16 v3: the type says which stands where), facing along the line toward the
            // other end, with its bullwheel's centre on the design point.
            _spawning = PartBottom;
            var ret = PlaceTerminal(Model(type.Bottom.Model), bottom, Quaternion.LookRotation(dir), Height, livery, levels?.Bottom);
            _spawning = PartTop;
            var drive = PlaceTerminal(Model(type.Top.Model), top, Quaternion.LookRotation(-dir), Height, livery, levels?.Top);
            var retRig = ret != null ? ret.GetComponent<LiftRig>() : null;
            var driveRig = drive != null ? drive.GetComponent<LiftRig>() : null;
            // LD9: each terminal shows the head for its end (drive at either end); the top's first row until the rope picks.
            if (type.Bottom.Head != null) retRig?.ShowHead(type.Bottom.Head);
            if (type.Top.Head != null) driveRig?.ShowHead(type.Top.Head);
            // Each terminal's pier runs about 15 m below its 0.00 level, buried in the pad; its footing stands at grade under
            // the pier (the model contract, owner: piers simply tall, no stacking). Models without one skip it.
            _spawning = PartBottom;
            PlaceFooting(ret, Height, livery);
            _spawning = PartTop;
            PlaceFooting(drive, Height, livery);

            // Where the rope leaves each station, along the line and in elevation.
            float S(Vector3 p) => Vector3.Dot(p - bottom, dir);
            var bExit = retRig != null ? retRig.HeadSocket("rope_right_out").position : bottom + dir * 6 + Vector3.up * (float)(Height(bottom) + 3.5);
            var tExit = driveRig != null ? driveRig.HeadSocket("rope_left_out").position : top - dir * 4 + Vector3.up * (float)(Height(top) + 3.04);
            float length = Vector3.Distance(Flat(bottom), Flat(top));
            var b = frame.ToAlbers(bottom.x, bottom.z);
            var t = frame.ToAlbers(top.x, top.z);
            // Each terminal stands on its pad (LF-12 v2), raised by any last resort in its fill; the station towers' foundations
            // stand as high as their terminals need (TW-02 v3, derived as the build derived them).
            Rope = fixedBases != null && fixedBases.Count == lift.Towers.Count
                ? LiftRope.SolveWithBases(type, ground, b.X, b.Y, t.X, t.Y, lift.Towers, lift.Stance, S(bExit), bExit.y, length - S(tExit), tExit.y, fixedBases)
                : LiftRope.Solve(type, ground, b.X, b.Y, t.X, t.Y, lift.Towers, lift.Stance, S(bExit), bExit.y, length - S(tExit), tExit.y);
            // LD9: the top row the rope picked (its exit already the solve's), shown in place of the first.
            if (Rope.Type.Top.Head != null && Rope.Type.Top.Head != type.Top.Head) driveRig?.ShowHead(Rope.Type.Top.Head);
            type = Rope.Type;

            // Towers: the base level on its pier, the mast sections and head along the mast's axis (leaned down the line
            // by the rope's lean, TW-14), the rope at the head's sockets.
            var heads = new List<LiftRig>();
            for (int i = 0; i < Rope.Towers.Count; i++)
            {
                var sol = Rope.Towers[i];
                string head = HeadId(sol.Head);
                _heads.Add(head);
                // TW-02 v4: the tower stands on the ground as it is now, its steel and foundation reaching the fixed head; a
                // leaned mast's foot slides along the line with its length, so the head keeps its place and its lean.
                var at = bottom + dir * (float)sol.BaseS;
                double groundY = Height(at);
                double steel = lift.Towers[i].HeightMm / 1000.0, reveal = sol.Reveal;
                if (Math.Abs(groundY - sol.BaseY) > 1e-4) LiftRope.AsBuilt(sol, groundY, out steel, out reveal);
                if (Math.Abs(groundY - sol.BaseY) > 1e-4 && Math.Abs(sol.Lean) > 1e-9)
                    for (int pass = 0; pass < 2; pass++)
                    {
                        at = bottom + dir * (float)(sol.RopeS + steel * Math.Sin(sol.Lean));
                        groundY = Height(at);
                        LiftRope.AsBuilt(sol, groundY, out steel, out reveal);
                    }
                _asBuilt.Add((float)steel);
                at.y = (float)groundY;
                var facing = Quaternion.LookRotation(-dir);   // the platform side (+Z) faces the return
                var lean = Quaternion.AngleAxis((float)(sol.Lean * Mathf.Rad2Deg), Vector3.Cross(Vector3.up, -dir));
                _spawning = i;
                var h = SpawnTower(head, at, facing, lean, (float)steel, (float)reveal, livery);
                heads.Add(h != null ? h.GetComponent<LiftRig>() : null);
            }

            if (type.BullwheelVisible) KeepWheels(type, retRig, driveRig);

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
                AddWheel(loop, driveRig, driveRig.HeadSocket("rope_left_bw").position, driveRig.HeadSocket("rope_right_bw").position);
                loop.AddRange(StationRope(driveRig, "right"));
            }
            AddSpans(loop, Rope.Empty, bottom, dir, -right * half, S(bExit), S(tExit), false);
            if (retRig != null)
            {
                var back = StationRope(retRig, "left");
                back.Reverse();
                loop.AddRange(back);
                AddWheel(loop, retRig, retRig.HeadSocket("rope_left_bw").position, retRig.HeadSocket("rope_right_bw").position);
            }
            SetLoop(loop);
            BuildCable((float)type.RopeDiameterMetres / 2);
            var carrier = Carrier(type);
            PrepareChairs(carrier, livery, type, Track(type, retRig, driveRig, bottom, dir));
            if (type.Tow && carrier != null) Gone(carrier);   // put together for this lift only
        }

        /// <summary>
        /// The carrier the lift runs: its chair, or a tow's recoil box with its bar under the cord (the T-bar's or J-bar's;
        /// monta_tbar.json carrier.cordGap), put together once as one carrier with the bar's levels of detail in the box's
        /// (the instanced draw takes the parts from its LOD group).
        /// </summary>
        GameObject Carrier(LiftType type)
        {
            if (!type.Tow) return Prefab("chair");
            var box = Model(_kit + "springbox");
            var bar = Model(_kit + (type.CatalogId == LiftCatalogIds.MontaJbar ? "bar_j" : "bar_t"));
            if (box == null) return null;
            var carrier = Object.Instantiate(box);
            carrier.SetActive(false);
            var cord = carrier.GetComponent<LiftRig>()?.Socket("cord");
            if (bar != null && cord != null)
            {
                var b = Object.Instantiate(bar, carrier.transform);
                b.transform.SetPositionAndRotation(cord.position + Vector3.down * 0.05f, carrier.transform.rotation);
                var into = carrier.GetComponent<LODGroup>();
                var from = b.GetComponent<LODGroup>();
                if (into != null)
                {
                    var lods = into.GetLODs();
                    var extra = from != null ? from.GetLODs() : new[] { new LOD(0, b.GetComponentsInChildren<Renderer>()) };
                    for (int i = 0; i < lods.Length; i++)
                    {
                        var more = extra[Mathf.Min(i, extra.Length - 1)].renderers;
                        var all = new Renderer[lods[i].renderers.Length + more.Length];
                        lods[i].renderers.CopyTo(all, 0);
                        more.CopyTo(all, lods[i].renderers.Length);
                        lods[i].renderers = all;
                    }
                    into.SetLODs(lods);
                }
                if (from != null) Object.DestroyImmediate(from);
            }
            return carrier;
        }

        /// <summary>
        /// Findings #2 and #6 (task 07): with posable models, both rope sides are posed train by train, from the return's
        /// hold-down row over every tower's head to the drive's support row (<see cref="RopeOverSheaves.PoseLine"/>), the
        /// pivots turned to match, and the loop built through the sheaves' contact points and the spans between them.
        /// Returns false (and changes nothing) when any model isn't posable.
        /// </summary>
        bool TryPosedLoop(LiftType type, LiftRig retRig, LiftRig driveRig, List<LiftRig> heads, Vector3 bottom, Vector3 dir, Vector3 right, LiftLivery livery)
        {
            // Our right (the loaded, uphill rope) is the return's right, and the towers' and the drive's left: they face down the
            // line. A tow's towers face up it (SpawnTower), so its loaded rope is their right.
            string tower = Tow ? "r" : "l", back = Tow ? "l" : "r";
            string[] up = { "r", tower, "l" }, down = { "l", back, "r" };
            // Every tower's head must turn; a terminal row that can't (the Monta return's fixed row until its rebuild) is
            // posed round as a fixed sheave at its rope exit (LT33), and said so in the build log.
            RigidNote = "";
            RopeRadii = "";
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
            var spun = new List<RopeSheave>(256);
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
                // Each train's rope at its own sheaves' radius, as the model was built (owner finding 2026-11-06); the chairs'
                // where a model doesn't say.
                var rhos = trains.ConvertAll(t => double.IsNaN(t.Rho) ? rho : t.Rho);
                for (int i = 0; i < trains.Count; i++)
                {
                    var rig = i == 0 ? retRig : i == trains.Count - 1 ? driveRig : heads[i - 1];
                    string note = $"{(rig != null ? rig.AssetId : "?")} {rhos[i]:F3}{(double.IsNaN(trains[i].Rho) ? "*" : "")}";
                    if (RopeRadii.IndexOf(note, StringComparison.Ordinal) < 0) RopeRadii += (RopeRadii.Length > 0 ? ", " : "") + note;
                }
                double y0 = trees[0].Sheaves[0].AxleW;
                // RP-08 (owner, round 3): the rope hangs at the load it carries; the chairs are empty, so both sides take the
                // empty sag. The checks keep both load cases.
                double w = type.EmptyNewtonsPerMetre;
                var posed = RopeOverSheaves.PoseLine(trees, above, holds, rhos, type.CounterweightNewtons, w, y0, true, 3, turn);
                for (int i = 0; i < trains.Count; i++) TrainPosing.Apply(trains[i], posed.Poses[i], dir);
                PoseError = Math.Max(PoseError, posed.TangentError);
                // The rope runs up this side (the loaded one) and down the other, the loop's travel order: every sheave it
                // touches turns with it, its rim at rope speed; a sheave it leaves stands still, as a real one does.
                var travel = side == 0 ? dir : -dir;
                for (int i = 0; i < trains.Count; i++)
                {
                    var rig = i == 0 ? retRig : i == trains.Count - 1 ? driveRig : heads[i - 1];
                    double groove = rhos[i] - type.RopeDiameterMetres / 2;
                    if (rig == null || groove <= 0.01) continue;
                    var pose = posed.Poses[i];
                    var lateral = right * trains[i].Lateral;
                    for (int k = 0; k < trains[i].Sheaves.Length && k < pose.Touched.Length; k++)
                    {
                        var s = trains[i].Sheaves[k];
                        int at = Array.IndexOf(rig.Pivots, s);   // a fixed row's socket (LT33) isn't a wheel
                        if (!pose.Touched[k] || at < 0) continue;
                        var axis = rig.PivotAxes != null && at < rig.PivotAxes.Length && rig.PivotAxes[at].sqrMagnitude > 0
                            ? rig.PivotAxes[at].normalized : Vector3.right;
                        var contact = bottom + dir * (float)pose.ContactS[k] + Vector3.up * (float)pose.ContactW[k] + lateral;
                        // The rim at the contact moves with the rope: ω × r = v, so ω points along r × v.
                        float sense = Mathf.Sign(Vector3.Dot(s.rotation * axis, Vector3.Cross(contact - s.position, travel)));
                        spun.Add(new RopeSheave
                        {
                            Pivot = s, Rest = s.localRotation, Axis = axis,
                            Rate = sense * (float)(type.SpeedMetresPerSecond / groove), Contact = contact,
                        });
                    }
                }

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
                void Bend(LiftRig rig, string sideName) { var b = rig.HeadSocket($"rope_{sideName}_bend"); if (b != null) loop.Add(b.position); }
                if (side == 0)
                {
                    loop.Add(retRig.HeadSocket("rope_right_bw").position);
                    Bend(retRig, "right");
                    loop.AddRange(points);
                    Bend(driveRig, "left");
                    loop.Add(driveRig.HeadSocket("rope_left_bw").position);
                    AddWheel(loop, driveRig, driveRig.HeadSocket("rope_left_bw").position, driveRig.HeadSocket("rope_right_bw").position);
                }
                else
                {
                    loop.Add(driveRig.HeadSocket("rope_right_bw").position);
                    Bend(driveRig, "right");
                    points.Reverse();
                    loop.AddRange(points);
                    Bend(retRig, "left");
                    loop.Add(retRig.HeadSocket("rope_left_bw").position);
                    AddWheel(loop, retRig, retRig.HeadSocket("rope_left_bw").position, retRig.HeadSocket("rope_right_bw").position);
                }
            }
            // G8: a pivot marked to follow another (the D6's walkways, pinned to their crossarm's hanger) takes that pivot's
            // world turn, whatever its own parents do, so the walkway turns with its train.
            foreach (var rig in heads) Follow(rig);
            Posed = true;
            _sheaves = spun.ToArray();
            _sheaveLo = Vector3.positiveInfinity; _sheaveHi = Vector3.negativeInfinity;
            foreach (var s in _sheaves) { _sheaveLo = Vector3.Min(_sheaveLo, s.Contact); _sheaveHi = Vector3.Max(_sheaveHi, s.Contact); }
            SetLoop(loop);
            BuildCable((float)type.RopeDiameterMetres / 2);
            var carrier = Carrier(type);
            PrepareChairs(carrier, livery, type, Track(type, retRig, driveRig, bottom, dir));
            if (type.Tow && carrier != null) Gone(carrier);   // put together for this lift only
            return true;
        }

        /// <summary>Whether the trains were posed to the rope (posable models), and the largest tangent mismatch (radians).</summary>
        public bool Posed { get; private set; }
        /// <summary>The terminal rows posed as fixed (no pivots, LT33), for the build log; empty when every train turned.</summary>
        public string RigidNote { get; private set; } = "";
        /// <summary>The axle-to-rope distance each model's trains were posed at, metres (the build log; * where the model
        /// doesn't say and the chairs' is used).</summary>
        public string RopeRadii { get; private set; } = "";
        /// <summary>Heads whose type disagrees with the drawn rope's bend over them (or too sharp to carry it): reported.</summary>
        public int SideMismatches { get; private set; }
        public string MismatchNote { get; private set; } = "";
        public double PoseError { get; private set; }

        static Vector3 Flat(Vector3 p) => new Vector3(p.x, 0, p.z);

        string HeadId(LiftRope.Head head)
        {
            // TooSharp is drawn as the kit's biggest support head: the D6's b10, the quads' b8 (until TW-06 is resolved).
            if (head == LiftRope.Head.TooSharp && _kit != Quad && Model(_kit + "tower_b10") != null) return "tower_b10";
            // The tow kit's biggest support is its s6 (it has no b8: the chairs' head stood on a T-bar mast, 2026-11-06).
            if (head == LiftRope.Head.TooSharp && Tow) return "tower_s6";
            switch (head)
            {
                case LiftRope.Head.S4: return "tower_s4";
                case LiftRope.Head.S6: return "tower_s6";
                case LiftRope.Head.D8: return "tower_d8";
                case LiftRope.Head.C8: return "tower_c8";
                case LiftRope.Head.S10: return "tower_s10";   // the wide kit's (G6, LT41)
                case LiftRope.Head.B10: return "tower_b10";
                case LiftRope.Head.D10: return "tower_d10";
                case LiftRope.Head.D4: return "tower_d4";     // the tow kit's hold-down (the T-bar)
                default: return "tower_b8";   // B8, and TooSharp drawn as the biggest support until task 09 reports it
            }
        }

        /// <summary>A carpet's bed as built (null for a rope lift): the modules' joints the view stood them on.</summary>
        public CarpetBed.Result Bed { get; private set; }

        /// <summary>C3/C10 (glidepath_carpet.json gallery): a covered line's exit bay comes every this many metres at most.</summary>
        const float ExitBayEvery = 24f;

        /// <summary>
        /// A belt lift (the Glidepath carpets, surface lifts PR B): its bed laid from the stored levels (<see cref="CarpetBed.Lay"/>);
        /// the bottom (tension) station facing up the line at its level, each module hung by its back socket on the joint
        /// before it and pitched to the next (2 m, the 1 m filler last), its feet stretched to the ground under them; the top
        /// (drive) station turned round on the last joint by its belt_in. Covered: a gallery bay on every module, an exit bay
        /// at least every 24 m, a portal at each end; uncovered: no rails (C6's default). The control posts where the stations
        /// say, on the approaches. No rope, towers or carriers.
        /// </summary>
        void BuildBelt(LiftType type, IHeightSource ground, LocalFrame frame, LiftLivery livery, Vector3 bottom, Vector3 top, Vector3 dir,
                       (double Bottom, double Top)? levels)
        {
            double Height(Vector3 p)
            {
                var a = frame.ToAlbers(p.x, p.z);
                return ground.At(a.X, a.Y);
            }
            string kit = type.TowerKit;
            var b = frame.ToAlbers(bottom.x, bottom.z);
            var t = frame.ToAlbers(top.x, top.z);
            var bed = CarpetBed.Lay(type, ground, b.X, b.Y, t.X, t.Y, levels?.Bottom, levels?.Top);
            Bed = bed;
            var stationB = Model(kit + "bottom");
            var stationT = Model(kit + "top");
            var module = Model(kit + "segment");
            var filler = Model(kit + "segment_1m") ?? module;
            if (stationB == null || stationT == null || module == null) return;
            // Each station pitched up the line as the bed says (level when 0): the bottom faces up and the top, turned round,
            // down the line.
            float bottomPitch = Mathf.Atan((float)bed.BottomPitch), topPitch = Mathf.Atan((float)bed.TopPitch);
            var facing = Quaternion.LookRotation(dir * Mathf.Cos(bottomPitch) + Vector3.up * Mathf.Sin(bottomPitch), Vector3.up);
            var turned = Quaternion.LookRotation(-dir * Mathf.Cos(topPitch) - Vector3.up * Mathf.Sin(topPitch), Vector3.up);
            var apron = new Apron();
            _spawning = PartBottom;
            var origin = bottom;
            origin.y = (float)bed.BottomLevel;
            var bottomGo = Spawn(stationB, origin, facing, livery);
            apron.Station(bottomGo, Height);
            // The joints from the station's own exit: the bed's (s, y) offsets from its first joint, so the modules meet the
            // station's socket whatever its model measures.
            var first = origin + facing * SocketIn(stationB, "belt_out");
            var back = SocketIn(module, "back");
            Vector3 JointAt(int k) => first + dir * (float)(bed.Joints[k].s - bed.Joints[0].s) + Vector3.up * (float)(bed.Joints[k].y - bed.Joints[0].y);
            var gallery = type.Covered ? Model(kit + "gallery") : null;
            var exitBay = type.Covered ? Model(kit + "gallery_exit") : null;
            int modules = bed.Modules.Count;
            // Exit bays (C3): evenly along the gallery, at least one, none on the end modules.
            int exits = type.Covered && modules > 2 ? Mathf.Max(1, Mathf.CeilToInt(modules * 2f / ExitBayEvery) - 1) : 0;
            var exitAt = new HashSet<int>();
            for (int e = 1; e <= exits; e++) exitAt.Add(Mathf.Clamp(Mathf.RoundToInt(modules * e / (exits + 1f)), 1, modules - 2));
            _spawning = PartRope;
            for (int k = 0; k < modules; k++)
            {
                var a = JointAt(k);
                var c = JointAt(k + 1);
                var along = (c - a).normalized;
                var pitch = Quaternion.LookRotation(along, Vector3.up);
                var prefab = bed.Modules[k] < CarpetBed.ModuleMetres - 0.01 ? filler : module;
                var go = Spawn(prefab, a - pitch * back, pitch, livery);
                StretchFeet(go, Height);
                apron.Module(go.transform, (float)bed.Modules[k], (float)type.DeckHalfWidthMetres, Height);
                apron.Material(go);
                var bay = exitAt.Contains(k) ? exitBay ?? gallery : gallery;
                if (bay != null && prefab == module) Spawn(bay, a - pitch * back, pitch, livery);
            }
            var last = JointAt(modules);
            _spawning = PartTop;
            var topAt = last - turned * SocketIn(stationT, "belt_in");
            var topGo = Spawn(stationT, topAt, turned, livery);
            apron.Station(topGo, Height);
            _spawning = PartRope;
            _apron = apron.Build(Root.transform, out var apronGo);
            if (apronGo != null) _partOf[apronGo] = PartRope;
            var portal = type.Covered ? Model(kit + "portal") : null;
            if (portal != null && modules > 0)
            {
                var g = SocketIn(portal, "gallery");
                Spawn(portal, first - facing * g, facing, livery);
                Spawn(portal, last - turned * g, turned, livery);
            }
            var post = Model("glidepath_carpet_post");
            if (post != null)
            {
                var pb = origin + facing * SocketIn(stationB, "post");
                pb.y = (float)Height(pb);
                Spawn(post, pb, facing, livery);
                var pt = topAt + turned * SocketIn(stationT, "post");
                pt.y = (float)Height(pt);
                Spawn(post, pt, turned, livery);
            }
            // Owner finding 2026-11-06: a carpet picks (hover, Edit, Remove) by its pieces' boxes; it has no rope loop.
            _belt = true;
            _loop = Array.Empty<Vector3>();
            bool any = false;
            foreach (var r in Root.GetComponentsInChildren<Renderer>())
            {
                if (!any) { _bounds = r.bounds; any = true; }
                else _bounds.Encapsulate(r.bounds);
            }
            if (!any) _bounds = new Bounds(bottom, Vector3.zero);
        }

        /// <summary>
        /// Owner 2026-11-06: the carpet runs down to the ground, no legs. One mesh for a whole carpet: each module's skirt
        /// (glidepath_carpet.json section: 40 mm over the foot, at the deck's outer edge) carried down to the ground either
        /// side, and each station's footprint walled down from its foot, a metre a panel, both faces, in the skirt's grey.
        /// </summary>
        sealed class Apron
        {
            const float SkirtBottom = 0.04f, Below = 0.05f, Panel = 1f;
            readonly List<Vector3> _v = new List<Vector3>();
            readonly List<Vector3> _n = new List<Vector3>();
            readonly List<int> _t = new List<int>();
            Material _material;

            /// <summary>A wall from <paramref name="a"/> to <paramref name="b"/> (its top edge) down to the ground.</summary>
            void Wall(Vector3 a, Vector3 b, Func<Vector3, double> height)
            {
                int panels = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / Panel));
                var side = Vector3.Cross(Vector3.up, b - a).normalized;
                for (int i = 0; i < panels; i++)
                {
                    var p0 = Vector3.Lerp(a, b, (float)i / panels);
                    var p1 = Vector3.Lerp(a, b, (float)(i + 1) / panels);
                    var q0 = new Vector3(p0.x, Mathf.Min(p0.y, (float)height(p0)) - Below, p0.z);
                    var q1 = new Vector3(p1.x, Mathf.Min(p1.y, (float)height(p1)) - Below, p1.z);
                    foreach (float face in new[] { 1f, -1f })
                    {
                        int at = _v.Count;
                        _v.Add(p0); _v.Add(p1); _v.Add(q0); _v.Add(q1);
                        for (int k = 0; k < 4; k++) _n.Add(side * face);
                        if (face > 0) { _t.Add(at); _t.Add(at + 2); _t.Add(at + 1); _t.Add(at + 1); _t.Add(at + 2); _t.Add(at + 3); }
                        else { _t.Add(at); _t.Add(at + 1); _t.Add(at + 2); _t.Add(at + 1); _t.Add(at + 3); _t.Add(at + 2); }
                    }
                }
            }

            /// <summary>A module's skirts, from its back end (its origin, at its foot) <paramref name="length"/> along it.</summary>
            public void Module(Transform module, float length, float halfWidth, Func<Vector3, double> height)
            {
                foreach (float v in new[] { -halfWidth, halfWidth })
                    Wall(module.TransformPoint(new Vector3(v, SkirtBottom, 0)), module.TransformPoint(new Vector3(v, SkirtBottom, length)), height);
            }

            /// <summary>A station's footprint (its meshes' extent in its own frame) walled down from its foot.</summary>
            public void Station(GameObject station, Func<Vector3, double> height)
            {
                if (station == null) return;
                var root = station.transform;
                float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                foreach (var mf in station.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null) continue;
                    var bb = mf.sharedMesh.bounds;
                    for (int c = 0; c < 8; c++)
                    {
                        var corner = new Vector3((c & 1) == 0 ? bb.min.x : bb.max.x, (c & 2) == 0 ? bb.min.y : bb.max.y, (c & 4) == 0 ? bb.min.z : bb.max.z);
                        var local = root.InverseTransformPoint(mf.transform.TransformPoint(corner));
                        if (local.y > 0.5f) continue;   // its low parts: the plates and side panels, not a barrier's posts
                        x0 = Mathf.Min(x0, local.x); x1 = Mathf.Max(x1, local.x);
                        z0 = Mathf.Min(z0, local.z); z1 = Mathf.Max(z1, local.z);
                    }
                }
                if (x0 > x1) return;
                var p00 = root.TransformPoint(new Vector3(x0, 0, z0));
                var p10 = root.TransformPoint(new Vector3(x1, 0, z0));
                var p11 = root.TransformPoint(new Vector3(x1, 0, z1));
                var p01 = root.TransformPoint(new Vector3(x0, 0, z1));
                Wall(p00, p10, height);
                Wall(p10, p11, height);
                Wall(p11, p01, height);
                Wall(p01, p00, height);
                Material(station);
            }

            /// <summary>The skirt's grey from the first piece that has it.</summary>
            public void Material(GameObject piece)
            {
                if (_material != null || piece == null) return;
                Material any = null;
                foreach (var r in piece.GetComponentsInChildren<Renderer>())
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        any = any ?? m;
                        if (m.name.IndexOf("grey", StringComparison.OrdinalIgnoreCase) >= 0) { _material = m; return; }
                    }
                _material = any;
            }

            public Mesh Build(Transform parent, out GameObject go)
            {
                go = null;
                if (_v.Count == 0 || _material == null) return null;
                var mesh = new Mesh { name = "Carpet apron", indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(_v);
                mesh.SetNormals(_n);
                mesh.SetTriangles(_t, 0);
                mesh.RecalculateBounds();
                go = new GameObject("Carpet apron");
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = _material;
                return mesh;
            }
        }

        /// <summary>A prefab's socket in its own frame (sockets are under the root, which is at its origin).</summary>
        static Vector3 SocketIn(GameObject prefab, string name)
        {
            var rig = prefab != null ? prefab.GetComponent<LiftRig>() : null;
            var socket = rig != null ? rig.Socket(name) : null;
            return socket != null ? rig.transform.InverseTransformPoint(socket.position) : Vector3.zero;
        }

        /// <summary>
        /// glidepath_carpet.json feet: each pair (its pivot at the deck, its foot socket at the nominal 150 mm) stretched down
        /// to the ground under it (the bed rides the snow room over it, owner 2026-11-06), behind the apron.
        /// </summary>
        static void StretchFeet(GameObject module, Func<Vector3, double> height)
        {
            var rig = module != null ? module.GetComponent<LiftRig>() : null;
            if (rig == null || rig.Pivots == null) return;
            foreach (string end in new[] { "front", "back" })
            {
                Transform pivot = null;
                foreach (var pv in rig.Pivots)
                    if (pv != null && pv.name.EndsWith("pivot_feet_" + end, StringComparison.Ordinal)) { pivot = pv; break; }
                var foot = rig.Socket("foot_" + end);
                if (pivot == null || foot == null) continue;
                float nominal = pivot.position.y - foot.position.y;
                if (nominal < 0.02f) continue;
                float want = Mathf.Max(0.10f, pivot.position.y - (float)height(foot.position));
                var scale = pivot.localScale;
                scale.y *= want / nominal;
                pivot.localScale = scale;
            }
        }

        GameObject PlaceTerminal(GameObject prefab, Vector3 bullwheel, Quaternion rotation, Func<Vector3, double> height, LiftLivery livery,
                                 double? level = null)
        {
            if (prefab == null) return null;
            var rig = prefab.GetComponent<LiftRig>();
            var l = rig != null ? rig.HeadSocket("rope_left_bw") : null;
            var r = rig != null ? rig.HeadSocket("rope_right_bw") : null;
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
            var mast = (Tow ? Model(_kit + "mast") : null) ?? Prefab("tower_mast");
            // A leaned mast stands on the leaned base for its angle (TW-14: 2.5° steps); until those are imported, the plumb one.
            lean.ToAngleAxis(out float degrees, out _);
            int tenths = Mathf.RoundToInt(degrees / 2.5f) * 25;
            var foundation = Tow ? Model(_kit + "base") : (tenths > 0 ? Prefab($"tower_base_l{tenths:000}") : null) ?? Prefab("tower_base");
            if (head == null || mast == null || foundation == null) return null;
            // A tow's tower stands in the frame it was modelled in (owner finding 2026-11-06: turned to the chair towers'
            // down-the-line facing, its pre-leaned base stub and head sleeve leaned against the mast): +Z up the line, +X the
            // loaded side, the mast leaning 8° toward it (D11) as the Lift Lab stands it, the base off to the return side so
            // the level head stands over the line.
            if (Tow)
            {
                facing *= Quaternion.Euler(0, 180, 0);
                lean = Quaternion.AngleAxis(-TowLeanDegrees, facing * Vector3.forward);
            }
            var top = mast.GetComponent<LiftRig>().Socket("top");
            var parts = new TowerParts
            {
                Mast = mast,
                LeanedBase = !Tow && !foundation.name.EndsWith("tower_base", StringComparison.Ordinal),
                FootLocal = foundation.GetComponent<LiftRig>().Socket("mast_foot").localPosition,
                RopeOnHead = head.GetComponent<LiftRig>().Socket("rope_right").localPosition.y,
                Facing = facing,
                Lean = lean,
                Tow = Tow,
                Section = top != null && top.localPosition.y > 0.1f ? top.localPosition.y : 1f,
            };
            parts.Base = Spawn(foundation, position, facing, livery);
            parts.Head = Spawn(head, position, Tow ? facing : lean * facing, livery);
            Stand(parts, position, steel, reveal, livery);
            if (_spawning >= 0)
            {
                while (_towerParts.Count <= _spawning) _towerParts.Add(null);
                _towerParts[_spawning] = parts;
            }
            return parts.Head;
        }

        /// <summary>
        /// A tower's pieces as <see cref="SpawnTower"/> stood them (task 15 round 4): kept so a re-height moves them, showing
        /// or hiding mast sections, instead of building the tower again. The head never moves (TW-02 v4: the steel and the
        /// foundation take up the ground), so the trains' pose on it stays.
        /// </summary>
        sealed class TowerParts
        {
            public GameObject Base, Head, Mast;
            public readonly List<GameObject> Sections = new List<GameObject>();
            public bool LeanedBase;
            /// <summary>A tow's tower (the T-bar): leaned across the line, its head level and over the line.</summary>
            public bool Tow;
            public float Section = 1f;
            public Vector3 FootLocal;
            public float RopeOnHead;
            public Quaternion Facing, Lean;
        }

        readonly List<TowerParts> _towerParts = new List<TowerParts>();

        /// <summary>How many towers it stands (LD3's numbers).</summary>
        public int TowerCount => _towerParts.Count;

        /// <summary>Tower <paramref name="index"/>'s head, in world space (LD3: its number stands over it); false if it has none.</summary>
        public bool TowerTop(int index, out Vector3 at)
        {
            var parts = index >= 0 && index < _towerParts.Count ? _towerParts[index] : null;
            at = parts != null && parts.Head != null ? parts.Head.transform.position : default;
            return parts != null && parts.Head != null && parts.Head.activeInHierarchy;
        }

        // A tower's base on the ground at position, as many 1 m sections as fit under the head and the head on top (SpawnTower's
        // arithmetic). Sections beyond those it needs are hidden, and a taller tower gets new ones (the only Instantiate).
        void Stand(TowerParts parts, Vector3 position, float steel, float reveal, LiftLivery livery)
        {
            if (parts.Tow)
            {
                StandTow(parts, position, steel + reveal, livery);
                return;
            }
            float foot = parts.FootLocal.y;
            int sections = Mathf.Max(1, Mathf.RoundToInt(steel - foot - parts.RopeOnHead));
            float rise = steel - (foot + sections + parts.RopeOnHead) + reveal;
            var basePos = position + Vector3.up * rise;
            parts.Base.transform.SetPositionAndRotation(basePos, parts.Facing);
            var axis = parts.Lean * Vector3.up;
            var turned = parts.Lean * parts.Facing;
            var mastFoot = parts.LeanedBase ? basePos + parts.Facing * parts.FootLocal : basePos + Vector3.up * foot;
            while (parts.Sections.Count < sections) parts.Sections.Add(Spawn(parts.Mast, mastFoot, turned, livery));
            for (int i = 0; i < parts.Sections.Count; i++)
            {
                var s = parts.Sections[i];
                bool shown = i < sections;
                if (shown) s.transform.SetPositionAndRotation(mastFoot + axis * i, turned);
                if (s.activeSelf != shown) s.SetActive(shown);
            }
            parts.Head.transform.SetPositionAndRotation(mastFoot + axis * sections, turned);
        }

        /// <summary>
        /// A tow's tower (the T-bar; the Lift Lab's arithmetic): as many mast sections as fit up the leaned axis under the
        /// level head, the base off to the return side so the head lands on the line at <paramref name="position"/>, its rope
        /// <paramref name="rope"/> over the ground there, the base riding up or down by the remainder.
        /// </summary>
        void StandTow(TowerParts parts, Vector3 position, float rope, LiftLivery livery)
        {
            var axis = parts.Lean * Vector3.up;
            var foot = parts.Facing * parts.FootLocal;
            int sections = Mathf.Max(1, Mathf.RoundToInt((rope - foot.y - parts.RopeOnHead) / (axis.y * parts.Section)));
            var top = foot + axis * (parts.Section * sections);
            var at = new Vector3(position.x - top.x, position.y + rope - top.y - parts.RopeOnHead, position.z - top.z);
            parts.Base.transform.SetPositionAndRotation(at, parts.Facing);
            var turned = parts.Lean * parts.Facing;
            while (parts.Sections.Count < sections) parts.Sections.Add(Spawn(parts.Mast, at + foot, turned, livery));
            for (int i = 0; i < parts.Sections.Count; i++)
            {
                var s = parts.Sections[i];
                bool shown = i < sections;
                if (shown) s.transform.SetPositionAndRotation(at + foot + axis * (parts.Section * i), turned);
                if (s.activeSelf != shown) s.SetActive(shown);
            }
            parts.Head.transform.SetPositionAndRotation(at + top, parts.Facing);
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
            var exit = rig.HeadSocket($"rope_{side}_out").position;
            var hold = rig.HeadSocket($"rope_{side}_hold");
            var points = new List<Vector3> { rig.HeadSocket($"rope_{side}_bw").position };
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
            var bend = rig.HeadSocket($"rope_{side}_bend");
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
            float sign = TurnSign(rig, from, to);
            const int steps = 24;
            for (int k = 1; k < steps; k++)
                loop.Add(centre + Quaternion.AngleAxis(sign * 180f * k / steps, Vector3.up) * r);
        }

        /// <summary>Which way round world up the rope turns behind a bullwheel, going from <paramref name="from"/> to <paramref name="to"/>.</summary>
        static float TurnSign(LiftRig rig, Vector3 from, Vector3 to)
        {
            var r = from - (from + to) / 2;
            return Vector3.Dot(Vector3.Cross(r, -rig.transform.forward), Vector3.up) >= 0 ? 1 : -1;
        }

        /// <summary>
        /// BB2 (task P3-16): keeps each terminal's bullwheel pivot, to spin it with the rope. The rope runs onto every
        /// bullwheel at its left socket and off at its right (the loop's travel order), so the wheel turns the way the rope
        /// goes round it (<see cref="AddWheel"/>); the pivot's own axis may point either way, or lean (the model's).
        /// </summary>
        void KeepWheels(LiftType type, params LiftRig[] rigs)
        {
            var wheels = new List<Wheel>(2);
            foreach (var rig in rigs)
            {
                var pivot = rig != null ? rig.Pivot("bullwheel") : null;
                var l = rig != null ? rig.HeadSocket("rope_left_bw") : null;
                var r = rig != null ? rig.HeadSocket("rope_right_bw") : null;
                if (pivot == null || l == null || r == null) continue;
                int index = Array.IndexOf(rig.Pivots, pivot);
                var axis = rig.PivotAxes != null && index >= 0 && index < rig.PivotAxes.Length ? rig.PivotAxes[index].normalized : Vector3.up;
                var rest = pivot.localRotation;
                var worldAxis = (pivot.parent != null ? pivot.parent.rotation : Quaternion.identity) * rest * axis;
                // AddWheel's sign turns the rope with Quaternion.AngleAxis about world up; match it about the pivot's own axis.
                float sign = TurnSign(rig, l.position, r.position) * Mathf.Sign(Vector3.Dot(worldAxis, Vector3.up));
                wheels.Add(new Wheel { Pivot = pivot, Rest = rest, Axis = axis, Sign = sign });
            }
            _wheels = wheels.ToArray();
            _wheelRate = (float)(type.SpeedMetresPerSecond / (type.GaugeMetres / 2));
        }

        /// <summary>The bullwheels at <paramref name="seconds"/> on the chairs' clock (BB2). No allocation.</summary>
        void SpinWheels(double seconds)
        {
            if (_wheels.Length == 0) return;
            float degrees = (float)((seconds * _wheelRate) % (2 * Math.PI)) * Mathf.Rad2Deg;
            foreach (var w in _wheels)
                if (w.Pivot != null) w.Pivot.localRotation = w.Rest * Quaternion.AngleAxis(w.Sign * degrees, w.Axis);
        }

        /// <summary>The rope's sheaves near <paramref name="eye"/> at <paramref name="seconds"/> on the chairs' clock. No
        /// allocation; the far ones keep the turn they had.</summary>
        void SpinSheaves(Vector3 eye, double seconds)
        {
            // v0.3.2 cost 1.6 ms a frame at 40 lifts reading every pivot's world position (4.01). A built lift doesn't move,
            // so the test uses the contact cached at the pose, within half a metre of the pivot, and a whole lift past the
            // distance from its sheaves' box is skipped at once.
            const float near = SheaveSpinDistance * SheaveSpinDistance;
            if (_sheaves.Length == 0) return;
            Vector3 closest = Vector3.Max(_sheaveLo, Vector3.Min(eye, _sheaveHi));
            if ((closest - eye).sqrMagnitude > near) return;
            for (int i = 0; i < _sheaves.Length; i++)
            {
                ref readonly var s = ref _sheaves[i];
                if ((s.Contact - eye).sqrMagnitude > near || s.Pivot == null) continue;
                float degrees = (float)((seconds * s.Rate) % (2 * Math.PI)) * Mathf.Rad2Deg;
                s.Pivot.localRotation = s.Rest * Quaternion.AngleAxis(degrees, s.Axis);
            }
        }

        /// <summary>The rope sheaves this lift spins (tests).</summary>
        public int SheaveCount => _sheaves.Length;

        /// <summary>Spun sheave <paramref name="i"/>'s pivot and where the rope touches it (tests).</summary>
        public Transform SheavePivot(int i, out Vector3 contact)
        {
            contact = i < _sheaves.Length ? _sheaves[i].Contact : Vector3.zero;
            return i < _sheaves.Length ? _sheaves[i].Pivot : null;
        }

        /// <summary>The bullwheels this lift spins (tests).</summary>
        public int WheelCount => _wheels.Length;

        /// <summary>Bullwheel <paramref name="i"/>'s pivot (tests).</summary>
        public Transform WheelPivot(int i) => i < _wheels.Length ? _wheels[i].Pivot : null;

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
        /// from its grip and facing the way it travels, at the LOD its distance from the camera calls for. Spins the
        /// bullwheels (task P3-16, BB2) and the rope's sheaves near the eye too. No allocation.
        /// </summary>
        public void DrawChairs(Camera camera, double seconds)
        {
            SpinWheels(seconds);
            if (camera != null) SpinSheaves(camera.transform.position, seconds);
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

        /// <summary>
        /// The towers stood again on <paramref name="ground"/> under their fixed heads (TW-02 v4; owner BR21: a confirmed brush
        /// session; round 4: every frame of a brush near the lift): only the towers whose steel or footing changed move, their
        /// pieces moved and mast sections shown or hidden (<see cref="Stand"/>), and the terminals, the rope and the chairs
        /// stay as they are, since the heads don't move. <paramref name="near"/>: only towers whose foot is within this box
        /// (local x, z) are looked at; null for all. False when the lift isn't the one built (the caller builds it again).
        /// </summary>
        /// <summary>How many towers the last <see cref="Reheight"/> moved (the brush bench: none at Confirm, since the brush moved them live).</summary>
        public int LastReheightMoved { get; private set; }

        public bool Reheight(Lift lift, IHeightSource ground, LocalFrame frame, Rect? near = null)
        {
            if (Rope == null || !ReferenceEquals(lift, _builtFrom) || Rope.Towers.Count != _asBuilt.Count || _heads.Count != _asBuilt.Count
                || _towerParts.Count != _asBuilt.Count) return false;
            var bottom = new Vector3((float)lift.Bottom.XMetres, 0, (float)lift.Bottom.YMetres);
            var top = new Vector3((float)lift.Top.XMetres, 0, (float)lift.Top.YMetres);
            var dir = (top - bottom).normalized;
            double Height(Vector3 p)
            {
                var a = frame.ToAlbers(p.x, p.z);
                return ground.At(a.X, a.Y);
            }
            bool moved = false;
            LastReheightMoved = 0;
            for (int i = 0; i < Rope.Towers.Count; i++)
            {
                // As Build stands it (TW-02 v4, the leaned foot sliding along the line).
                var sol = Rope.Towers[i];
                var at = bottom + dir * (float)sol.BaseS;
                if (near is Rect box && !box.Contains(new Vector2(at.x, at.z)) && !box.Contains(new Vector2(bottom.x + dir.x * (float)sol.RopeS, bottom.z + dir.z * (float)sol.RopeS)))
                    continue;
                double groundY = Height(at);
                double steel = lift.Towers[i].HeightMm / 1000.0, reveal = sol.Reveal;
                if (Math.Abs(groundY - sol.BaseY) > 1e-4) LiftRope.AsBuilt(sol, groundY, out steel, out reveal);
                if (Math.Abs(groundY - sol.BaseY) > 1e-4 && Math.Abs(sol.Lean) > 1e-9)
                    for (int pass = 0; pass < 2; pass++)
                    {
                        at = bottom + dir * (float)(sol.RopeS + steel * Math.Sin(sol.Lean));
                        groundY = Height(at);
                        LiftRope.AsBuilt(sol, groundY, out steel, out reveal);
                    }
                if ((float)steel == _asBuilt[i]) continue;
                at.y = (float)groundY;
                _spawning = i;
                Stand(_towerParts[i], at, (float)steel, (float)reveal, _livery);
                _asBuilt[i] = (float)steel;
                moved = true;
                LastReheightMoved++;
            }
            _spawning = PartRope;
            // The pieces keep their parts (_partOf); the boxes the pointer hits are made again at the next pick.
            if (moved) ForgetPick();
            return true;
        }

        void Clear()
        {
            if (Root != null)   // at quit the scene may have destroyed it first
                for (int i = Root.transform.childCount - 1; i >= 0; i--) Gone(Root.transform.GetChild(i).gameObject);
            if (_cable != null) Gone(_cable);
            _cable = null;
            if (_apron != null) Gone(_apron);
            _apron = null;
            foreach (var m in _instanced) Gone(m);
            _instanced.Clear();
            _heads.Clear();
            Posed = false;
            PoseError = 0;
            _chairLods = Array.Empty<ChairLod>();
            _chairCount = 0;
            _wheels = Array.Empty<Wheel>();
            _towerParts.Clear();
            _highlighted = false;   // the pieces are going: nothing to relight
            ForgetPick();
            _partOf.Clear();
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
