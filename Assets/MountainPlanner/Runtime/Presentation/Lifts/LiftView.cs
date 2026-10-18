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
    public sealed class LiftView : IDisposable
    {
        public LiftRope Rope { get; private set; }
        public GameObject Root { get; }
        /// <summary>The head chosen for each tower, in order (for captures, tests and the review).</summary>
        public IReadOnlyList<string> Heads => _heads;
        public int ChairCount => _chairCount;
        /// <summary>TW-16: how far the bottom terminal stands above the ground (metres).</summary>
        public float BottomRaise { get; private set; }

        readonly LiftModelSet _models;
        readonly LiftMaterials _materials;
        readonly Material _cableMaterial;
        readonly List<string> _heads = new List<string>();
        readonly string _prefix;
        Mesh _cable;

        // The rope loop in world space, in travel order, with the distance walked to each point.
        Vector3[] _loop = Array.Empty<Vector3>();
        float[] _walked = Array.Empty<float>();
        float _loopLength;
        Bounds _bounds;
        int _chairCount;
        float _chairSpacing;
        float _speed;
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
            _prefix = "sessellift_fgq4_";
            Root = new GameObject(name);
            Root.transform.SetParent(parent, false);
        }

        GameObject Prefab(string id) => _models != null ? _models.Find(_prefix + id) : null;

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
        /// local metres. The livery colours the drive's hood.
        /// </summary>
        public void Build(Lift lift, IHeightSource ground, LocalFrame frame, Color livery)
        {
            Clear();
            var type = LiftType.Of(lift.CatalogId);
            var bottom = new Vector3((float)lift.Bottom.XMetres, 0, (float)lift.Bottom.YMetres);
            var top = new Vector3((float)lift.Top.XMetres, 0, (float)lift.Top.YMetres);
            double Height(Vector3 p)
            {
                var a = frame.ToAlbers(p.x, p.z);
                return ground.At(a.X, a.Y);
            }
            var dir = (top - bottom).normalized;
            var right = Vector3.Cross(Vector3.up, dir);   // the loaded rope is on the right looking uphill (LC-02 models)

            // Terminals: the return at the bottom facing up the line, the drive at the top facing down it, each with its
            // bullwheel's centre on the design point.
            var ret = PlaceTerminal(Prefab("return"), bottom, Quaternion.LookRotation(dir), Height, livery);
            var drive = PlaceTerminal(Prefab("drive"), top, Quaternion.LookRotation(-dir), Height, livery);
            var retRig = ret != null ? ret.GetComponent<LiftRig>() : null;
            var driveRig = drive != null ? drive.GetComponent<LiftRig>() : null;

            // Where the rope leaves each station, along the line and in elevation.
            float S(Vector3 p) => Vector3.Dot(p - bottom, dir);
            var bExit = retRig != null ? retRig.Socket("rope_right_out").position : bottom + dir * 6 + Vector3.up * (float)(Height(bottom) + 3.5);
            var tExit = driveRig != null ? driveRig.Socket("rope_left_out").position : top - dir * 4 + Vector3.up * (float)(Height(top) + 3.04);
            float length = Vector3.Distance(Flat(bottom), Flat(top));
            var b = frame.ToAlbers(bottom.x, bottom.z);
            var t = frame.ToAlbers(top.x, top.z);
            Rope = LiftRope.SolveRaised(type, ground, b.X, b.Y, t.X, t.Y, lift.Towers, lift.Stance, S(bExit), bExit.y, length - S(tExit), tExit.y, out double raise);
            BottomRaise = (float)raise;
            if (ret != null && raise > 0)
            {
                // The bottom terminal stands higher (TW-16, for now): lift the model, and its sockets with it.
                ret.transform.position += Vector3.up * BottomRaise;
                bExit = retRig.Socket("rope_right_out").position;
            }

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
                var h = SpawnTower(head, at, facing, lean, lift.Towers[i].HeightMm / 1000f, livery);
                heads.Add(h != null ? h.GetComponent<LiftRig>() : null);
            }

            // Posable models (the model contract): each train rocks to the rope and the rope runs over its sheaves.
            if (retRig != null && driveRig != null && TryPosedLoop(type, retRig, driveRig, heads, bottom, dir, right, livery)) return;

            // The rope loop: up the right side (loaded), round the drive's bullwheel, down the left, round the return's.
            var loop = new List<Vector3>(4096);
            float half = (float)type.GaugeMetres / 2;
            if (retRig != null) loop.AddRange(StationRope(retRig, "right"));
            AddSpans(loop, Rope.Loaded, bottom, dir, right * half, S(bExit), S(tExit), true);
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
            PrepareChairs(Prefab("chair"), livery, (float)type.ChairSpacingMetres, (float)type.SpeedMetresPerSecond);
        }

        /// <summary>
        /// Findings #2 and #6 (task 07): with posable models, both rope sides are posed train by train, from the return's
        /// hold-down row over every tower's head to the drive's support row (<see cref="RopeOverSheaves.PoseLine"/>), the
        /// pivots turned to match, and the loop built through the sheaves' contact points and the spans between them.
        /// Returns false (and changes nothing) when any model isn't posable.
        /// </summary>
        bool TryPosedLoop(LiftType type, LiftRig retRig, LiftRig driveRig, List<LiftRig> heads, Vector3 bottom, Vector3 dir, Vector3 right, Color livery)
        {
            // Our right (the loaded, uphill rope) is the return's right, and the towers' and the drive's left: they face down the line.
            string[] up = { "r", "l", "l" }, down = { "l", "r", "r" };
            foreach (var sides in new[] { up, down })
            {
                if (!TrainPosing.Posable(retRig, sides[0]) || !TrainPosing.Posable(driveRig, sides[2])) return false;
                foreach (var h in heads) if (!TrainPosing.Posable(h, sides[1])) return false;
            }
            double rho = LiftType.SheaveRadiusMetres + type.RopeDiameterMetres / 2;
            var loop = new List<Vector3>(4096);
            for (int side = 0; side < 2; side++)
            {
                var sides = side == 0 ? up : down;
                var trains = new List<TrainPosing.Train> { TrainPosing.Read(retRig, sides[0], bottom, dir, right) };
                var above = new List<bool> { false };   // the return's row holds the rope down
                for (int i = 0; i < heads.Count; i++)
                {
                    trains.Add(TrainPosing.Read(heads[i], sides[1], bottom, dir, right));
                    above.Add(Rope.Towers[i].Head != LiftRope.Head.D8);
                }
                trains.Add(TrainPosing.Read(driveRig, sides[2], bottom, dir, right));
                above.Add(true);                          // the drive's row supports it
                if (trains.Exists(t => t == null)) return false;
                var trees = trains.ConvertAll(t => t.Tree);
                var holds = trains.ConvertAll(t => t.HoldRow);
                double y0 = trees[0].Sheaves[0].AxleW;
                double w = side == 0 ? type.LoadedNewtonsPerMetre : type.EmptyNewtonsPerMetre;
                var posed = RopeOverSheaves.PoseLine(trees, above, holds, rho, type.CounterweightNewtons, w, y0);
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
                if (side == 0)
                {
                    loop.Add(retRig.Socket("rope_right_bw").position);
                    loop.AddRange(points);
                    loop.Add(driveRig.Socket("rope_left_bw").position);
                    AddWheel(loop, driveRig, driveRig.Socket("rope_left_bw").position, driveRig.Socket("rope_right_bw").position);
                }
                else
                {
                    loop.Add(driveRig.Socket("rope_right_bw").position);
                    points.Reverse();
                    loop.AddRange(points);
                    loop.Add(retRig.Socket("rope_left_bw").position);
                    AddWheel(loop, retRig, retRig.Socket("rope_left_bw").position, retRig.Socket("rope_right_bw").position);
                }
            }
            Posed = true;
            SetLoop(loop);
            BuildCable((float)type.RopeDiameterMetres / 2);
            PrepareChairs(Prefab("chair"), livery, (float)type.ChairSpacingMetres, (float)type.SpeedMetresPerSecond);
            return true;
        }

        /// <summary>Whether the trains were posed to the rope (posable models), and the largest tangent mismatch (radians).</summary>
        public bool Posed { get; private set; }
        public double PoseError { get; private set; }

        static Vector3 Flat(Vector3 p) => new Vector3(p.x, 0, p.z);

        static string HeadId(LiftRope.Head head)
        {
            switch (head)
            {
                case LiftRope.Head.S4: return "tower_s4";
                case LiftRope.Head.S6: return "tower_s6";
                case LiftRope.Head.D8: return "tower_d8";
                case LiftRope.Head.C8: return "tower_c8";
                default: return "tower_b8";   // B8, and TooSharp drawn as the biggest support until task 09 reports it
            }
        }

        GameObject PlaceTerminal(GameObject prefab, Vector3 bullwheel, Quaternion rotation, Func<Vector3, double> height, Color livery)
        {
            if (prefab == null) return null;
            var rig = prefab.GetComponent<LiftRig>();
            var l = rig != null ? rig.Socket("rope_left_bw") : null;
            var r = rig != null ? rig.Socket("rope_right_bw") : null;
            var centre = l != null && r != null ? (l.localPosition + r.localPosition) / 2 : Vector3.zero;
            centre.y = 0;
            var origin = bullwheel - rotation * centre;
            origin.y = (float)height(origin);
            return Spawn(prefab, origin, rotation, livery);
        }

        GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Color livery)
        {
            var go = Object.Instantiate(prefab, position, rotation, Root.transform);
            _materials?.Apply(go, livery);
            return go;
        }

        /// <summary>
        /// A tower: the base on the ground (level; a leaned mast needs the tilted base plate, a model change in review), as
        /// many 1 m mast sections as fit and the head, so the rope passes the head <paramref name="ropeHeight"/> from the
        /// mast's foot along its axis; the base rides up or down by the remainder (under half a metre), as a footing would.
        /// </summary>
        GameObject SpawnTower(string headId, Vector3 position, Quaternion facing, Quaternion lean, float ropeHeight, Color livery)
        {
            var head = Prefab(headId);
            var mast = Prefab("tower_mast");
            // A leaned mast stands on the leaned base for its angle (TW-14: 2.5° steps); until those are imported, the plumb one.
            lean.ToAngleAxis(out float degrees, out _);
            int tenths = Mathf.RoundToInt(degrees / 2.5f) * 25;
            var foundation = (tenths > 0 ? Prefab($"tower_base_l{tenths:000}") : null) ?? Prefab("tower_base");
            if (head == null || mast == null || foundation == null) return null;
            bool leanedBase = foundation.name != _prefix + "tower_base";
            var footLocal = foundation.GetComponent<LiftRig>().Socket("mast_foot").localPosition;
            float foot = footLocal.y;
            float ropeOnHead = head.GetComponent<LiftRig>().Socket("rope_right").localPosition.y;
            int sections = Mathf.Max(1, Mathf.RoundToInt(ropeHeight - foot - ropeOnHead));
            float rise = ropeHeight - (foot + sections + ropeOnHead);
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
        void PrepareChairs(GameObject chair, Color livery, float spacing, float speed)
        {
            _chairCount = 0;
            if (chair == null || _loopLength <= 0) return;
            _chairCount = Mathf.Max(1, Mathf.FloorToInt(_loopLength / spacing));
            _chairSpacing = _loopLength / _chairCount;   // the same chairs evenly round the whole loop
            _speed = speed;
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
            Object.Destroy(instance);
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
            float shift = (float)((seconds * _speed) % _loopLength);
            int seg = 1;
            for (int k = 0; k < _chairCount; k++)
            {
                float d = (_chairSpacing * k + shift) % _loopLength;
                if (seg >= _walked.Length || _walked[seg - 1] > d) seg = 1;
                while (seg < _walked.Length - 1 && _walked[seg] < d) seg++;
                float span = _walked[seg] - _walked[seg - 1];
                var a = _loop[seg - 1];
                var b = _loop[seg];
                var p = span > 0 ? Vector3.Lerp(a, b, (d - _walked[seg - 1]) / span) : a;
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
                        Graphics.RenderMeshInstanced(rp, part.Mesh, sub, part.Matrices, part.Count);
                    }
                }
        }

        void Clear()
        {
            if (Root != null)   // at quit the scene may have destroyed it first
                for (int i = Root.transform.childCount - 1; i >= 0; i--) Object.Destroy(Root.transform.GetChild(i).gameObject);
            if (_cable != null) Object.Destroy(_cable);
            _cable = null;
            foreach (var m in _instanced) Object.Destroy(m);
            _instanced.Clear();
            _heads.Clear();
            Posed = false;
            PoseError = 0;
            _chairLods = Array.Empty<ChairLod>();
            _chairCount = 0;
        }

        public void Dispose()
        {
            Clear();
            if (Root != null) Object.Destroy(Root);
        }
    }
}
