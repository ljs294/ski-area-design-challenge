using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// An ID in the snowmaking network (task 4.15, HY-01): pipes, nodes, pump houses and guns share this type and are kept
    /// apart by their DesignKey tags (18–21), so no two ever collide.
    /// </summary>
    public readonly struct SnowId : IEquatable<SnowId>, IComparable<SnowId>
    {
        public readonly ulong Value;
        public SnowId(ulong value) { Value = value; }
        public static readonly SnowId None = default;
        public bool IsNone => Value == 0;
        public bool Equals(SnowId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is SnowId o && Equals(o);
        public override int GetHashCode() => Value.GetHashCode();
        public int CompareTo(SnowId other) => Value.CompareTo(other.Value);
        public static bool operator ==(SnowId a, SnowId b) => a.Value == b.Value;
        public static bool operator !=(SnowId a, SnowId b) => a.Value != b.Value;
        public override string ToString() => "snow:" + Value.ToString("x16");
    }

    /// <summary>HY-01's node kinds. Saved as bytes: never renumber.</summary>
    public enum SnowNodeKind : byte
    {
        /// <summary>A gravity intake in a source: a fixed head at the source's level (WS-05).</summary>
        Intake = 0,
        /// <summary>Where pipes meet (HY-02), made when a pipe snaps onto another, or a pipe's free end.</summary>
        Junction = 1,
        /// <summary>A hydrant on a pipe: one gun hangs on it by its hose (HY-01).</summary>
        Hydrant = 2,
        /// <summary>An inline booster pump between exactly two pipes (HY-05).</summary>
        Pump = 3,
        /// <summary>A pipe's end inside a pond or dam: it discharges into that source (WS-11, the pump-house fill).</summary>
        Outlet = 4,
        /// <summary>A pump house's discharge: its intake, pump and compressor in one (HY-01, SG-06).</summary>
        House = 5,
    }

    /// <summary>SM-02's per-gun override. Saved as bytes: never renumber.</summary>
    public enum GunOverride : byte
    {
        Auto = 0,
        On = 1,
        Off = 2,
    }

    /// <summary>The SLE guns (SG-01, SG-03, `GunCatalog`). Saved as bytes: never renumber.</summary>
    public enum GunModel : byte
    {
        Stick10 = 0,
        Stick20 = 1,
        Stick30 = 2,
        Ground = 3,
    }

    /// <summary>
    /// A pump's settings (HY-05 v2, owner-approved plan 2026-11-18, Q1): it adds up to <see cref="BoostPsi"/> while its power
    /// lasts, H = min(boost, 3,960·hp·η ÷ Q), with a small droop so the solve stays smooth. Off, its check valve passes water
    /// forward only (Q2).
    /// </summary>
    public readonly struct PumpSpec : IEquatable<PumpSpec>
    {
        public readonly int Hp;
        /// <summary>Efficiency in thousandths, (0, 1000].</summary>
        public readonly int EfficiencyPermille;
        public readonly int BoostPsi;
        public readonly bool On;

        public PumpSpec(int hp, int efficiencyPermille, int boostPsi, bool on)
        {
            Hp = hp;
            EfficiencyPermille = efficiencyPermille;
            BoostPsi = boostPsi;
            On = on;
        }

        /// <summary>The pump house's pump: 1,000 hp, 85 %, 600 psi (about 2,430 gpm before its power runs out).</summary>
        public static PumpSpec House => new PumpSpec(1000, 850, 600, true);
        /// <summary>A mid-mountain booster: 250 hp, 85 %, 300 psi.</summary>
        public static PumpSpec Booster => new PumpSpec(250, 850, 300, true);

        public PumpSpec WithOn(bool on) => new PumpSpec(Hp, EfficiencyPermille, BoostPsi, on);
        public bool Equals(PumpSpec o) => Hp == o.Hp && EfficiencyPermille == o.EfficiencyPermille && BoostPsi == o.BoostPsi && On == o.On;
        public override bool Equals(object obj) => obj is PumpSpec o && Equals(o);
        public override int GetHashCode() => Hp * 31 + BoostPsi;
    }

    /// <summary>
    /// A node of the snowmaking network (HY-01). A node is either a pipe's end (<see cref="SnowPipe.From"/>,
    /// <see cref="SnowPipe.To"/>) or a tap on a pipe's route (<see cref="Host"/>): hydrants, inline pumps and the junctions
    /// made when a pipe snaps onto another sit on their host's route, so a drawn pipe stays one pipe and the hydraulic graph
    /// splits it at its taps. <see cref="Source"/> is the pond, dam or lake ID an intake draws from, an outlet fills, or a
    /// house stands at; <see cref="Label"/> is the H#/J#/P# number the player reads. Immutable.
    /// </summary>
    public sealed class SnowNode
    {
        public SnowId Id { get; }
        public SnowNodeKind Kind { get; }
        public PointMm At { get; }
        public int Label { get; }
        public ulong Source { get; }
        /// <summary>A pump's or house's settings (default for other kinds).</summary>
        public PumpSpec Pump { get; }
        /// <summary>The pipe this node taps, or none for a pipe's end.</summary>
        public SnowId Host { get; }
        /// <summary>An inline pump pushes water along its host's route (from its first point to its last); else against it.</summary>
        public bool Forward { get; }
        /// <summary>The pump house a House node belongs to.</summary>
        public SnowId House { get; }

        public SnowNode(SnowId id, SnowNodeKind kind, PointMm at, int label, ulong source = 0, PumpSpec pump = default,
                        SnowId host = default, bool forward = true, SnowId house = default)
        {
            Id = id;
            Kind = kind;
            At = at;
            Label = label;
            Source = source;
            Pump = pump;
            Host = host;
            Forward = forward;
            House = house;
        }

        public bool IsPump => Kind == SnowNodeKind.Pump || Kind == SnowNodeKind.House;
        public bool IsTap => !Host.IsNone;
        public SnowNode WithPump(PumpSpec pump) => new SnowNode(Id, Kind, At, Label, Source, pump, Host, Forward, House);
        public SnowNode WithForward(bool forward) => new SnowNode(Id, Kind, At, Label, Source, Pump, Host, forward, House);
        public SnowNode WithHost(SnowId host, PointMm at) => new SnowNode(Id, Kind, at, Label, Source, Pump, host, Forward, House);
        public SnowNode WithKind(SnowNodeKind kind, int label) => new SnowNode(Id, kind, At, label, Source, Pump, Host, Forward, House);
    }

    /// <summary>
    /// A buried pipe between two nodes (HY-01, HY-03): its route, ends included, at least two points; a diameter from
    /// <see cref="SnowRules.Diameters"/>; a valve at each end (HY-01, new), closed when set. Immutable.
    /// </summary>
    public sealed class SnowPipe
    {
        public SnowId Id { get; }
        public string Name { get; }
        public int DiameterIn { get; }
        public IReadOnlyList<PointMm> Route => _route;
        public SnowId From { get; }
        public SnowId To { get; }
        public bool FromClosed { get; }
        public bool ToClosed { get; }

        readonly PointMm[] _route;

        public SnowPipe(SnowId id, string name, int diameterIn, IReadOnlyList<PointMm> route, SnowId from, SnowId to,
                        bool fromClosed = false, bool toClosed = false)
        {
            Id = id;
            Name = name ?? "";
            DiameterIn = diameterIn;
            _route = new PointMm[route?.Count ?? 0];
            for (int i = 0; i < _route.Length; i++) _route[i] = route[i];
            From = from;
            To = to;
            FromClosed = fromClosed;
            ToClosed = toClosed;
        }

        public bool Closed => FromClosed || ToClosed;
        public SnowId Other(SnowId node) => node == From ? To : From;
        public SnowPipe WithName(string name) => new SnowPipe(Id, name, DiameterIn, _route, From, To, FromClosed, ToClosed);
        public SnowPipe WithDiameter(int d) => new SnowPipe(Id, Name, d, _route, From, To, FromClosed, ToClosed);
        public SnowPipe WithValves(bool fromClosed, bool toClosed) => new SnowPipe(Id, Name, DiameterIn, _route, From, To, fromClosed, toClosed);
        public SnowPipe WithRoute(IReadOnlyList<PointMm> route, SnowId from, SnowId to) => new SnowPipe(Id, Name, DiameterIn, route, from, to, FromClosed, ToClosed);

        /// <summary>The plan length in millimetres (the 3D length comes from the profile, HY-03).</summary>
        public long PlanLengthMm()
        {
            double s = 0;
            for (int i = 1; i < _route.Length; i++) s += Math.Sqrt(_route[i].DistanceSquared(_route[i - 1]));
            return (long)Math.Round(s);
        }
    }

    /// <summary>How a pump house stands on the ground (MapLibre's two choices, re-specified).</summary>
    public enum HouseFoundation : byte
    {
        /// <summary>A level pad graded under the footprint and a 6 ft apron (the default).</summary>
        Flatten = 0,
        /// <summary>A level slab on the slope: the ground is left as it is.</summary>
        Slab = 1,
    }

    /// <summary>
    /// A pump house (HY-01, SG-06, WS-11): the game's first building. 60 × 40 ft by default with a 16 ft eave and a 4:12 gable
    /// along its long axis; it stands at a source, owns one <see cref="SnowNodeKind.House"/> node (its intake, pump and
    /// discharge) and a compressor of <see cref="CompressorCfm"/>. Immutable.
    /// </summary>
    public sealed class PumpHouse
    {
        public SnowId Id { get; }
        public string Name { get; }
        public PointMm Centre { get; }
        /// <summary>The long axis, hundredths of a degree clockwise from grid north, [0, 36000).</summary>
        public int BearingCentideg { get; }
        public int LengthMm { get; }
        public int WidthMm { get; }
        public ulong Source { get; }
        public int CompressorCfm { get; }
        public HouseFoundation Foundation { get; }

        public PumpHouse(SnowId id, string name, PointMm centre, int bearingCentideg, int lengthMm, int widthMm, ulong source,
                         int compressorCfm, HouseFoundation foundation)
        {
            Id = id;
            Name = name ?? "";
            Centre = centre;
            BearingCentideg = bearingCentideg;
            LengthMm = lengthMm;
            WidthMm = widthMm;
            Source = source;
            CompressorCfm = compressorCfm;
            Foundation = foundation;
        }

        public PumpHouse WithName(string name) => new PumpHouse(Id, name, Centre, BearingCentideg, LengthMm, WidthMm, Source, CompressorCfm, Foundation);
        public PumpHouse WithCompressor(int cfm) => new PumpHouse(Id, Name, Centre, BearingCentideg, LengthMm, WidthMm, Source, cfm, Foundation);

        /// <summary>The footprint's four corners, clockwise from the front-left, in millimetres.</summary>
        public PointMm[] Corners()
        {
            double a = BearingCentideg / 100.0 * Math.PI / 180.0;
            double fx = Math.Sin(a), fy = Math.Cos(a);   // the long axis (bearing clockwise from +Y)
            double rx = fy, ry = -fx;                    // its right-hand side
            double hl = LengthMm / 2.0, hw = WidthMm / 2.0;
            var c = new PointMm[4];
            c[0] = P(-hl, -hw); c[1] = P(hl, -hw); c[2] = P(hl, hw); c[3] = P(-hl, hw);
            return c;
            PointMm P(double l, double w) => new PointMm((int)Math.Round(Centre.X + fx * l + rx * w), (int)Math.Round(Centre.Y + fy * l + ry * w));
        }
    }

    /// <summary>
    /// An SLE gun (SG-01…04): it hangs on one hydrant by a hose of at most 50 ft; one gun per hydrant. <see cref="Hydrant"/>
    /// none means "disconnected" (built, no water until a free hydrant is in reach). <see cref="AimCentideg"/> is the yaw.
    /// Immutable.
    /// </summary>
    public sealed class SnowGun
    {
        public SnowId Id { get; }
        public GunModel Model { get; }
        public PointMm At { get; }
        public SnowId Hydrant { get; }
        public int AimCentideg { get; }
        public GunOverride Override { get; }

        public SnowGun(SnowId id, GunModel model, PointMm at, SnowId hydrant, int aimCentideg, GunOverride over)
        {
            Id = id;
            Model = model;
            At = at;
            Hydrant = hydrant;
            AimCentideg = aimCentideg;
            Override = over;
        }

        public SnowGun WithOverride(GunOverride o) => new SnowGun(Id, Model, At, Hydrant, AimCentideg, o);
        public SnowGun WithHydrant(SnowId h) => new SnowGun(Id, Model, At, h, AimCentideg, Override);
        public SnowGun WithAim(int centideg) => new SnowGun(Id, Model, At, Hydrant, centideg, Override);
    }

    /// <summary>
    /// The whole snowmaking network as the design holds it (task 4.15, shape 16): nodes, pipes, pump houses and guns, each
    /// kept in ID order (SH-04) so the same commands give the same bytes. Immutable and copy-on-write, one field of the
    /// <see cref="DesignDocument"/>.
    /// </summary>
    public sealed class SnowNetwork
    {
        public static readonly SnowNetwork Empty = new SnowNetwork(Array.Empty<SnowNode>(), Array.Empty<SnowPipe>(), Array.Empty<PumpHouse>(), Array.Empty<SnowGun>());

        public IReadOnlyList<SnowNode> Nodes => _nodes;
        public IReadOnlyList<SnowPipe> Pipes => _pipes;
        public IReadOnlyList<PumpHouse> Houses => _houses;
        public IReadOnlyList<SnowGun> Guns => _guns;
        public bool IsEmpty => _nodes.Length == 0 && _pipes.Length == 0 && _houses.Length == 0 && _guns.Length == 0;

        readonly SnowNode[] _nodes;
        readonly SnowPipe[] _pipes;
        readonly PumpHouse[] _houses;
        readonly SnowGun[] _guns;

        SnowNetwork(SnowNode[] nodes, SnowPipe[] pipes, PumpHouse[] houses, SnowGun[] guns)
        {
            _nodes = nodes;
            _pipes = pipes;
            _houses = houses;
            _guns = guns;
        }

        /// <summary>A network from stored parts, sorted by ID (the save reader and tests).</summary>
        public static SnowNetwork From(IReadOnlyList<SnowNode> nodes, IReadOnlyList<SnowPipe> pipes, IReadOnlyList<PumpHouse> houses, IReadOnlyList<SnowGun> guns)
        {
            var n = Copy(nodes); Array.Sort(n, (a, b) => a.Id.CompareTo(b.Id));
            var p = Copy(pipes); Array.Sort(p, (a, b) => a.Id.CompareTo(b.Id));
            var h = Copy(houses); Array.Sort(h, (a, b) => a.Id.CompareTo(b.Id));
            var g = Copy(guns); Array.Sort(g, (a, b) => a.Id.CompareTo(b.Id));
            return n.Length + p.Length + h.Length + g.Length == 0 ? Empty : new SnowNetwork(n, p, h, g);
        }

        static T[] Copy<T>(IReadOnlyList<T> list)
        {
            var a = new T[list?.Count ?? 0];
            for (int i = 0; i < a.Length; i++) a[i] = list[i];
            return a;
        }

        public SnowNode FindNode(SnowId id) => Find(_nodes, id, x => x.Id);
        public SnowPipe FindPipe(SnowId id) => Find(_pipes, id, x => x.Id);
        public PumpHouse FindHouse(SnowId id) => Find(_houses, id, x => x.Id);
        public SnowGun FindGun(SnowId id) => Find(_guns, id, x => x.Id);

        static T Find<T>(T[] a, SnowId id, Func<T, SnowId> key) where T : class
        {
            int lo = 0, hi = a.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int c = key(a[mid]).CompareTo(id);
                if (c == 0) return a[mid];
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return null;
        }

        public bool Contains(ulong value)
        {
            var id = new SnowId(value);
            return FindNode(id) != null || FindPipe(id) != null || FindHouse(id) != null || FindGun(id) != null;
        }

        /// <summary>The taps on a pipe, in ID order (the graph orders them along the route).</summary>
        public List<SnowNode> TapsOn(SnowId pipe)
        {
            var list = new List<SnowNode>();
            foreach (var n in _nodes) if (n.Host == pipe) list.Add(n);
            return list;
        }

        /// <summary>The pipes that end on a node, in ID order.</summary>
        public List<SnowPipe> PipesAt(SnowId node)
        {
            var list = new List<SnowPipe>();
            foreach (var p in _pipes) if (p.From == node || p.To == node) list.Add(p);
            return list;
        }

        /// <summary>The gun on a hydrant, or null.</summary>
        public SnowGun GunOn(SnowId hydrant)
        {
            if (hydrant.IsNone) return null;
            foreach (var g in _guns) if (g.Hydrant == hydrant) return g;
            return null;
        }

        /// <summary>The next free label number for a node kind (H1, H2, …; numbers are never reused while the node stands).</summary>
        public int NextLabel(SnowNodeKind kind)
        {
            int max = 0;
            foreach (var n in _nodes) if (n.Kind == kind && n.Label > max) max = n.Label;
            return max + 1;
        }

        // Copy-on-write edits, each keeping ID order.
        public SnowNetwork Put(SnowNode node) => new SnowNetwork(Put(_nodes, node, x => x.Id), _pipes, _houses, _guns);
        public SnowNetwork Put(SnowPipe pipe) => new SnowNetwork(_nodes, Put(_pipes, pipe, x => x.Id), _houses, _guns);
        public SnowNetwork Put(PumpHouse house) => new SnowNetwork(_nodes, _pipes, Put(_houses, house, x => x.Id), _guns);
        public SnowNetwork Put(SnowGun gun) => new SnowNetwork(_nodes, _pipes, _houses, Put(_guns, gun, x => x.Id));

        public SnowNetwork Without(SnowId id) => new SnowNetwork(Drop(_nodes, id, x => x.Id), Drop(_pipes, id, x => x.Id),
                                                                  Drop(_houses, id, x => x.Id), Drop(_guns, id, x => x.Id));

        static T[] Put<T>(T[] a, T item, Func<T, SnowId> key)
        {
            var id = key(item);
            var list = new List<T>(a.Length + 1);
            bool done = false;
            foreach (var x in a)
            {
                if (!done && key(x).CompareTo(id) > 0) { list.Add(item); done = true; }
                if (key(x) == id) { if (!done) { list.Add(item); done = true; } continue; }
                list.Add(x);
            }
            if (!done) list.Add(item);
            return list.ToArray();
        }

        static T[] Drop<T>(T[] a, SnowId id, Func<T, SnowId> key)
        {
            int i = Array.FindIndex(a, x => key(x) == id);
            if (i < 0) return a;
            var b = new T[a.Length - 1];
            Array.Copy(a, 0, b, 0, i);
            Array.Copy(a, i + 1, b, i, a.Length - i - 1);
            return b;
        }
    }
}
