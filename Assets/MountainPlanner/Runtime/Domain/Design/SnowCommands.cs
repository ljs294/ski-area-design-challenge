using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>How a new pipe's end meets the network (HY-02: connections are explicit, crossings never connect).</summary>
    public enum SnowEndKind : byte
    {
        /// <summary>A free end: a new junction (a dead end until something meets it).</summary>
        Free = 0,
        /// <summary>On an existing node: a pipe end, a tap or a pump house.</summary>
        Node = 1,
        /// <summary>On an existing pipe's route: a new junction tapped onto it.</summary>
        Pipe = 2,
        /// <summary>In a source's water: a new gravity intake drawing from it.</summary>
        Intake = 3,
        /// <summary>In a pond's or dam's water: a new outlet filling it (WS-11).</summary>
        Outlet = 4,
    }

    /// <summary>One end of a pipe being drawn: what the tool snapped it to.</summary>
    public readonly struct SnowEnd
    {
        public readonly SnowEndKind Kind;
        public readonly PointMm At;
        /// <summary>The node (<see cref="SnowEndKind.Node"/>) or the pipe (<see cref="SnowEndKind.Pipe"/>) it snapped to.</summary>
        public readonly SnowId Target;
        /// <summary>The source an intake draws from or an outlet fills.</summary>
        public readonly ulong Source;

        public SnowEnd(SnowEndKind kind, PointMm at, SnowId target = default, ulong source = 0)
        {
            Kind = kind;
            At = at;
            Target = target;
            Source = source;
        }

        public static SnowEnd Free(PointMm at) => new SnowEnd(SnowEndKind.Free, at);
        public static SnowEnd OnNode(SnowNode n) => new SnowEnd(SnowEndKind.Node, n.At, n.Id);
        public static SnowEnd OnPipe(SnowId pipe, PointMm at) => new SnowEnd(SnowEndKind.Pipe, at, pipe);
        public static SnowEnd Intake(PointMm at, ulong source) => new SnowEnd(SnowEndKind.Intake, at, default, source);
        public static SnowEnd Outlet(PointMm at, ulong source) => new SnowEnd(SnowEndKind.Outlet, at, default, source);
    }

    /// <summary>Shared steps of the snowmaking commands.</summary>
    static class SnowEdit
    {
        /// <summary>Resolves a pipe end to a node, adding it (or a tap) to the network when it is new.</summary>
        public static string End(ref SnowNetwork net, ref IdMint mint, SnowEnd end, out SnowNode node, List<ulong> created)
        {
            node = null;
            switch (end.Kind)
            {
                case SnowEndKind.Node:
                    node = net.FindNode(end.Target);
                    if (node == null) return "That connection is no longer there.";
                    if (node.Kind == SnowNodeKind.Pump) return "Connect pipes at a junction, not to a pump.";
                    return null;
                case SnowEndKind.Pipe:
                {
                    var host = net.FindPipe(end.Target);
                    if (host == null) return "That pipe is no longer there.";
                    var on = SnowGeometry.Project(host.Route, end.At);
                    if (on.DistanceMm > SnowRules.HoseMm) return "Snap the end onto the pipe.";
                    if (TapProblem(net, host, on.ChainageMm, null) is string t) return t;
                    node = new SnowNode(mint.SnowNode(), SnowNodeKind.Junction, on.Point, net.NextLabel(SnowNodeKind.Junction), host: host.Id);
                    break;
                }
                case SnowEndKind.Intake:
                    if (end.Source == 0) return "An intake needs a water source.";
                    node = new SnowNode(mint.SnowNode(), SnowNodeKind.Intake, end.At, net.NextLabel(SnowNodeKind.Intake), end.Source);
                    break;
                case SnowEndKind.Outlet:
                    if (end.Source == 0) return "An outlet needs a pond or dam to fill.";
                    node = new SnowNode(mint.SnowNode(), SnowNodeKind.Outlet, end.At, net.NextLabel(SnowNodeKind.Outlet), end.Source);
                    break;
                default:
                    node = new SnowNode(mint.SnowNode(), SnowNodeKind.Junction, end.At, net.NextLabel(SnowNodeKind.Junction));
                    break;
            }
            net = net.Put(node);
            created.Add(node.Id.Value);
            return null;
        }

        /// <summary>Whether a new tap at a chainage keeps clear of the pipe's ends and its other taps.</summary>
        public static string TapProblem(SnowNetwork net, SnowPipe host, double chainageMm, SnowNode except)
        {
            double len = SnowGeometry.Length(host.Route);
            if (chainageMm < SnowRules.TapGapMm || chainageMm > len - SnowRules.TapGapMm) return "Too close to the pipe's end: connect to the end instead.";
            foreach (var t in net.TapsOn(host.Id))
            {
                if (except != null && t.Id == except.Id) continue;
                if (Math.Abs(SnowGeometry.Project(host.Route, t.At).ChainageMm - chainageMm) < SnowRules.TapGapMm)
                    return "Another hydrant, pump or junction is within 10 ft.";
            }
            return null;
        }

        /// <summary>
        /// Places a hydrant run on a pipe: positions already within 10 ft of a tap or the pipe's ends are skipped (MapLibre's
        /// "skipped occupied", kept). Returns how many were placed.
        /// </summary>
        public static int Run(ref SnowNetwork net, ref IdMint mint, SnowPipe pipe, List<double> chainages, List<ulong> created)
        {
            int placed = 0;
            foreach (var c in chainages)
            {
                if (TapProblem(net, pipe, c, null) != null) continue;
                var h = new SnowNode(mint.SnowNode(), SnowNodeKind.Hydrant, SnowGeometry.At(pipe.Route, c), net.NextLabel(SnowNodeKind.Hydrant), host: pipe.Id);
                net = net.Put(h);
                created.Add(h.Id.Value);
                placed++;
            }
            return placed;
        }

        /// <summary>The free hydrant nearest a point within the hose, ties by ID; none if there is none.</summary>
        public static SnowId NearestFreeHydrant(SnowNetwork net, PointMm at, HashSet<SnowId> taken)
        {
            SnowId best = SnowId.None;
            long bestD = long.MaxValue;
            foreach (var n in net.Nodes)
            {
                if (n.Kind != SnowNodeKind.Hydrant || taken.Contains(n.Id) || net.GunOn(n.Id) != null) continue;
                long d = n.At.DistanceSquared(at);
                if (d > (long)SnowRules.HoseMm * SnowRules.HoseMm || d >= bestD) continue;
                bestD = d;
                best = n.Id;
            }
            return best;
        }

        public static CommandResult Finish(DesignDocument doc, SnowNetwork net, IdMint mint, List<ulong> created, Func<DesignDocument, string, CommandResult> invalid)
        {
            if (SnowRules.NetworkProblem(net) is string p) return invalid(doc, p);
            return CommandResult.Accept(doc.WithSnow(net).WithOrdinal(mint.NextOrdinal), created);
        }
    }

    /// <summary>
    /// Lay a pipe (HY-01, HY-02): a route between two ends, each a node, a tap onto another pipe, a free junction, an intake
    /// or an outlet; optionally a hydrant run along it (every <see cref="SpacingMm"/>, both ends of the run included). One
    /// command, one undo step.
    /// </summary>
    public sealed class AddSnowPipe : DesignCommand
    {
        public string Name { get; }
        public int DiameterIn { get; }
        public IReadOnlyList<PointMm> Route { get; }
        public SnowEnd From { get; }
        public SnowEnd To { get; }
        /// <summary>The hydrant spacing, or 0 for none.</summary>
        public int SpacingMm { get; }

        public AddSnowPipe(long baseRevision, string name, int diameterIn, IReadOnlyList<PointMm> route, SnowEnd from, SnowEnd to, int spacingMm = 0)
            : base(baseRevision)
        {
            Name = name;
            DiameterIn = diameterIn;
            Route = route ?? Array.Empty<PointMm>();
            From = from;
            To = to;
            SpacingMm = spacingMm;
        }

        public override string Label => "Lay a water line";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (Route.Count < 2) return Invalid(doc, "Add at least two pipe points.");
            if (!SnowRules.ValidDiameter(DiameterIn)) return Invalid(doc, "Choose a pipe size.");
            var net = doc.Snow;
            var mint = new IdMint(doc);
            var created = new List<ulong>();
            if (SnowEdit.End(ref net, ref mint, From, out var a, created) is string ea) return Invalid(doc, ea);
            if (SnowEdit.End(ref net, ref mint, To, out var b, created) is string eb) return Invalid(doc, eb);
            if (a.Id == b.Id) return Invalid(doc, "A pipe can't end where it starts.");
            var route = new List<PointMm>(Route);
            route[0] = a.At;
            route[route.Count - 1] = b.At;
            string name = string.IsNullOrWhiteSpace(Name) ? $"Line {doc.Snow.Pipes.Count + 1}" : Name.Trim();
            var pipe = new SnowPipe(mint.SnowPipe(), name, DiameterIn, route, a.Id, b.Id);
            net = net.Put(pipe);
            created.Add(pipe.Id.Value);
            if (SpacingMm > 0)
            {
                double len = SnowGeometry.Length(route);
                var run = SnowGeometry.RunBySpacing(0, len, SpacingMm);
                if (run.Count == 0) return Invalid(doc, $"That spacing makes more than {SnowRules.MaxRun} hydrants.");
                SnowEdit.Run(ref net, ref mint, pipe, run, created);
            }
            return SnowEdit.Finish(doc, net, mint, created, Invalid);
        }
    }

    /// <summary>
    /// A hydrant run on a laid pipe (MapLibre's, re-specified): between two points of its route, by spacing or by count,
    /// both ends included; positions already taken are skipped.
    /// </summary>
    public sealed class AddHydrantRun : DesignCommand
    {
        public SnowId Pipe { get; }
        public PointMm Start { get; }
        public PointMm End { get; }
        public int SpacingMm { get; }
        public int Count { get; }

        public AddHydrantRun(long baseRevision, SnowId pipe, PointMm start, PointMm end, int spacingMm, int count) : base(baseRevision)
        {
            Pipe = pipe;
            Start = start;
            End = end;
            SpacingMm = spacingMm;
            Count = count;
        }

        public override string Label => "Place hydrants";

        protected override CommandResult Run(DesignDocument doc)
        {
            var pipe = doc.Snow.FindPipe(Pipe);
            if (pipe == null) return Missing(doc, "That pipe");
            double a = SnowGeometry.Project(pipe.Route, Start).ChainageMm, b = SnowGeometry.Project(pipe.Route, End).ChainageMm;
            if (Math.Abs(b - a) < 1) return Invalid(doc, "Choose two distinct points on the pipe.");
            var run = Count > 0 ? SnowGeometry.RunByCount(a, b, Count) : SnowGeometry.RunBySpacing(a, b, SpacingMm);
            if (run.Count == 0) return Invalid(doc, Count > 0 ? $"Enter between 2 and {SnowRules.MaxRun} hydrants." : $"That spacing makes more than {SnowRules.MaxRun} hydrants.");
            var net = doc.Snow;
            var mint = new IdMint(doc);
            var created = new List<ulong>();
            if (SnowEdit.Run(ref net, ref mint, pipe, run, created) == 0) return Invalid(doc, "Every position is already taken.");
            return SnowEdit.Finish(doc, net, mint, created, Invalid);
        }
    }

    /// <summary>One hydrant or inline pump on a pipe (HY-01, HY-05). A pump pushes along its host's route when <see cref="Forward"/>.</summary>
    public sealed class AddSnowTap : DesignCommand
    {
        public SnowNodeKind Kind { get; }
        public SnowId Pipe { get; }
        public PointMm At { get; }
        public bool Forward { get; }
        public PumpSpec Pump { get; }

        public AddSnowTap(long baseRevision, SnowNodeKind kind, SnowId pipe, PointMm at, bool forward = true, PumpSpec pump = default) : base(baseRevision)
        {
            Kind = kind;
            Pipe = pipe;
            At = at;
            Forward = forward;
            Pump = kind == SnowNodeKind.Pump && pump.Hp == 0 ? PumpSpec.Booster : pump;
        }

        public override string Label => Kind == SnowNodeKind.Pump ? "Place a pump" : "Place a hydrant";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (Kind != SnowNodeKind.Hydrant && Kind != SnowNodeKind.Pump) return Invalid(doc, "Only hydrants and pumps sit on a pipe.");
            var pipe = doc.Snow.FindPipe(Pipe);
            if (pipe == null) return Missing(doc, "That pipe");
            var on = SnowGeometry.Project(pipe.Route, At);
            if (on.DistanceMm > SnowRules.HoseMm) return Invalid(doc, "Place it on the pipe.");
            if (SnowEdit.TapProblem(doc.Snow, pipe, on.ChainageMm, null) is string t) return Invalid(doc, t);
            var mint = new IdMint(doc);
            var node = new SnowNode(mint.SnowNode(), Kind, on.Point, doc.Snow.NextLabel(Kind), 0,
                                    Kind == SnowNodeKind.Pump ? Pump : default, pipe.Id, Forward);
            var created = new List<ulong> { node.Id.Value };
            return SnowEdit.Finish(doc, doc.Snow.Put(node), mint, created, Invalid);
        }
    }

    /// <summary>
    /// Build a pump house (HY-01, SG-06): a 60 × 40 ft building at a source, its discharge node at its centre where pipes
    /// connect. The source check (within reach of its water) is the tool's, which knows the shores.
    /// </summary>
    public sealed class AddPumpHouse : DesignCommand
    {
        public string Name { get; }
        public PointMm Centre { get; }
        public int BearingCentideg { get; }
        public ulong Source { get; }
        public HouseFoundation Foundation { get; }
        public int CompressorCfm { get; }

        public AddPumpHouse(long baseRevision, string name, PointMm centre, int bearingCentideg, ulong source,
                            HouseFoundation foundation = HouseFoundation.Flatten, int compressorCfm = SnowRules.DefaultCompressorCfm) : base(baseRevision)
        {
            Name = name;
            Centre = centre;
            BearingCentideg = ((bearingCentideg % 36000) + 36000) % 36000;
            Source = source;
            Foundation = foundation;
            CompressorCfm = compressorCfm;
        }

        public override string Label => "Build a pump house";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (Source == 0) return Invalid(doc, "A pump house stands at a water source.");
            var net = doc.Snow;
            var mint = new IdMint(doc);
            string name = string.IsNullOrWhiteSpace(Name) ? $"Pump house {net.Houses.Count + 1}" : Name.Trim();
            var house = new PumpHouse(mint.PumpHouse(), name, Centre, BearingCentideg, SnowRules.HouseLengthMm, SnowRules.HouseWidthMm,
                                      Source, CompressorCfm, Foundation);
            if (HouseOverlap(net, house) is string o) return Invalid(doc, o);
            var node = new SnowNode(mint.SnowNode(), SnowNodeKind.House, Centre, net.Houses.Count + 1, Source, PumpSpec.House, house: house.Id);
            net = net.Put(house).Put(node);
            return SnowEdit.Finish(doc, net, mint, new List<ulong> { house.Id.Value, node.Id.Value }, Invalid);
        }

        /// <summary>Pump houses may touch but not overlap (MapLibre's rule, kept), checked on their footprints' corners and edges.</summary>
        internal static string HouseOverlap(SnowNetwork net, PumpHouse h)
        {
            var a = h.Corners();
            foreach (var o in net.Houses)
            {
                if (o.Id == h.Id) continue;
                var b = o.Corners();
                if (Overlaps(a, b)) return $"It overlaps {o.Name}.";
            }
            return null;
        }

        // Separating-axis test on two convex quads; touching edges are not an overlap.
        static bool Overlaps(PointMm[] a, PointMm[] b)
        {
            return !Separated(a, b) && !Separated(b, a);
            static bool Separated(PointMm[] p, PointMm[] q)
            {
                for (int i = 0; i < 4; i++)
                {
                    var e0 = p[i];
                    var e1 = p[(i + 1) % 4];
                    double nx = e1.Y - e0.Y, ny = -(e1.X - e0.X);
                    double minP = double.MaxValue, maxP = double.MinValue, minQ = double.MaxValue, maxQ = double.MinValue;
                    foreach (var v in p) { double d = nx * v.X + ny * v.Y; minP = Math.Min(minP, d); maxP = Math.Max(maxP, d); }
                    foreach (var v in q) { double d = nx * v.X + ny * v.Y; minQ = Math.Min(minQ, d); maxQ = Math.Max(maxQ, d); }
                    if (maxP <= minQ || maxQ <= minP) return true;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// Install guns (SG-04, MapLibre's flow re-specified): each point hooks to the nearest free hydrant within the 50 ft hose,
    /// in the order given; a point with none in reach is built disconnected.
    /// </summary>
    public sealed class AddSnowGuns : DesignCommand
    {
        public GunModel Model { get; }
        public IReadOnlyList<PointMm> Points { get; }
        /// <summary>The yaw of each new gun, hundredths of a degree (the tool aims it across its trail).</summary>
        public IReadOnlyList<int> Aims { get; }

        public AddSnowGuns(long baseRevision, GunModel model, IReadOnlyList<PointMm> points, IReadOnlyList<int> aims = null) : base(baseRevision)
        {
            Model = model;
            Points = points ?? Array.Empty<PointMm>();
            Aims = aims;
        }

        public override string Label => Points.Count == 1 ? "Install a gun" : "Install guns";

        protected override CommandResult Run(DesignDocument doc)
        {
            if (Points.Count == 0) return Invalid(doc, "Place at least one gun.");
            var net = doc.Snow;
            var mint = new IdMint(doc);
            var created = new List<ulong>();
            var taken = new HashSet<SnowId>();
            for (int i = 0; i < Points.Count; i++)
            {
                var h = SnowEdit.NearestFreeHydrant(net, Points[i], taken);
                if (!h.IsNone) taken.Add(h);
                int aim = Aims != null && i < Aims.Count ? ((Aims[i] % 36000) + 36000) % 36000 : 0;
                var g = new SnowGun(mint.SnowGun(), Model, Points[i], h, aim, GunOverride.Auto);
                net = net.Put(g);
                created.Add(g.Id.Value);
            }
            return SnowEdit.Finish(doc, net, mint, created, Invalid);
        }

        /// <summary>
        /// "Fill hydrants": a gun beside every free hydrant on a pipe, set off the pipe by <paramref name="offsetMm"/> on its
        /// left (looking along the route), aimed across it. The points for <see cref="AddSnowGuns"/>.
        /// </summary>
        public static void FillPoints(SnowNetwork net, SnowId pipe, int offsetMm, List<PointMm> points, List<int> aims)
        {
            var p = net.FindPipe(pipe);
            if (p == null) return;
            foreach (var t in net.TapsOn(pipe))
            {
                if (t.Kind != SnowNodeKind.Hydrant || net.GunOn(t.Id) != null) continue;
                var on = SnowGeometry.Project(p.Route, t.At);
                var a = p.Route[on.Segment];
                var b = p.Route[on.Segment + 1];
                double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= 0) continue;
                double lx = -dy / len, ly = dx / len;
                points.Add(new PointMm((int)Math.Round(t.At.X + lx * offsetMm), (int)Math.Round(t.At.Y + ly * offsetMm)));
                double yaw = Math.Atan2(-lx, -ly) * 180.0 / Math.PI;   // facing back across the pipe
                aims.Add((int)Math.Round(((yaw % 360) + 360) % 360 * 100) % 36000);
            }
        }
    }

    /// <summary>SM-02's per-gun override, on one gun or many (the lasso). One undo step.</summary>
    public sealed class SetGunOverride : DesignCommand
    {
        public IReadOnlyList<SnowId> Guns { get; }
        public GunOverride Override { get; }

        public SetGunOverride(long baseRevision, IReadOnlyList<SnowId> guns, GunOverride over) : base(baseRevision)
        {
            Guns = guns ?? Array.Empty<SnowId>();
            Override = over;
        }

        public override string Label => Override == GunOverride.On ? "Turn guns on" : Override == GunOverride.Off ? "Turn guns off" : "Set guns to Auto";

        protected override CommandResult Run(DesignDocument doc)
        {
            var net = doc.Snow;
            foreach (var id in Guns)
            {
                var g = net.FindGun(id);
                if (g == null) return Missing(doc, "That gun");
                if (g.Override != Override) net = net.Put(g.WithOverride(Override));
            }
            return ReferenceEquals(net, doc.Snow) ? CommandResult.Accept(doc) : CommandResult.Accept(doc.WithSnow(net));
        }
    }

    /// <summary>A pipe's name, size and valves (HY-01, the pipe card).</summary>
    public sealed class EditSnowPipe : DesignCommand
    {
        public SnowId Id { get; }
        public string Name { get; }
        public int DiameterIn { get; }
        public bool FromClosed { get; }
        public bool ToClosed { get; }

        public EditSnowPipe(long baseRevision, SnowId id, string name, int diameterIn, bool fromClosed, bool toClosed) : base(baseRevision)
        {
            Id = id;
            Name = name;
            DiameterIn = diameterIn;
            FromClosed = fromClosed;
            ToClosed = toClosed;
        }

        public override string Label => "Edit a water line";

        protected override CommandResult Run(DesignDocument doc)
        {
            var p = doc.Snow.FindPipe(Id);
            if (p == null) return Missing(doc, "That pipe");
            if (!SnowRules.ValidDiameter(DiameterIn)) return Invalid(doc, "Choose a pipe size.");
            var q = new SnowPipe(p.Id, string.IsNullOrWhiteSpace(Name) ? p.Name : Name.Trim(), DiameterIn, p.Route, p.From, p.To, FromClosed, ToClosed);
            var net = doc.Snow.Put(q);
            if (SnowRules.NetworkProblem(net) is string e) return Invalid(doc, e);
            return CommandResult.Accept(doc.WithSnow(net));
        }
    }

    /// <summary>A pump's settings and direction (HY-05 v2; the pump and pump-house cards), and a house's compressor.</summary>
    public sealed class EditSnowPump : DesignCommand
    {
        public SnowId Node { get; }
        public PumpSpec Pump { get; }
        public bool Forward { get; }
        /// <summary>The pump house's compressor, or 0 to leave it.</summary>
        public int CompressorCfm { get; }

        public EditSnowPump(long baseRevision, SnowId node, PumpSpec pump, bool forward, int compressorCfm = 0) : base(baseRevision)
        {
            Node = node;
            Pump = pump;
            Forward = forward;
            CompressorCfm = compressorCfm;
        }

        public override string Label => "Edit a pump";

        protected override CommandResult Run(DesignDocument doc)
        {
            var n = doc.Snow.FindNode(Node);
            if (n == null || !n.IsPump) return Missing(doc, "That pump");
            if (SnowRules.PumpProblem(Pump) is string pp) return Invalid(doc, pp);
            var net = doc.Snow.Put(n.WithPump(Pump).WithForward(Forward));
            if (CompressorCfm > 0 && n.Kind == SnowNodeKind.House)
            {
                var h = net.FindHouse(n.House);
                if (h != null) net = net.Put(h.WithCompressor(CompressorCfm));
            }
            if (SnowRules.NetworkProblem(net) is string e) return Invalid(doc, e);
            return CommandResult.Accept(doc.WithSnow(net));
        }
    }

    /// <summary>
    /// Remove pipes, nodes, pump houses or guns (one undo step). A pipe takes its taps with it, and its end nodes when
    /// nothing else meets them; a gun on a removed hydrant stays, disconnected (MapLibre's guard, re-specified: the tool
    /// asks first). A pump house leaves a junction where its pipes met it. A node at a pipe's end can't go alone.
    /// </summary>
    public sealed class RemoveSnow : DesignCommand
    {
        public IReadOnlyList<SnowId> Ids { get; }

        public RemoveSnow(long baseRevision, IReadOnlyList<SnowId> ids) : base(baseRevision) { Ids = ids ?? Array.Empty<SnowId>(); }

        public override string Label => "Remove snowmaking";

        protected override CommandResult Run(DesignDocument doc)
        {
            var net = doc.Snow;
            foreach (var id in Ids)
            {
                if (net.FindGun(id) is SnowGun) { net = net.Without(id); continue; }
                if (net.FindPipe(id) is SnowPipe pipe) { net = RemovePipe(net, pipe); continue; }
                if (net.FindHouse(id) is PumpHouse house) { net = RemoveHouse(net, house); continue; }
                if (net.FindNode(id) is SnowNode node)
                {
                    if (node.Kind == SnowNodeKind.House) { var h = net.FindHouse(node.House); if (h != null) net = RemoveHouse(net, h); continue; }
                    if (!node.IsTap) return Invalid(doc, "That node is a pipe's end: remove the pipe instead.");
                    if (net.PipesAt(id).Count > 0) return Invalid(doc, "Pipes end on that junction: remove them first.");
                    net = Disconnect(net, id).Without(id);
                    continue;
                }
                if (doc.Snow.Contains(id.Value)) continue;   // already gone with an earlier one
                return Missing(doc, "That snowmaking part");
            }
            if (SnowRules.NetworkProblem(net) is string e) return Invalid(doc, e);
            return CommandResult.Accept(doc.WithSnow(net));
        }

        static SnowNetwork RemovePipe(SnowNetwork net, SnowPipe pipe)
        {
            foreach (var t in net.TapsOn(pipe.Id))
            {
                if (net.PipesAt(t.Id).Count > 0)
                {
                    // A junction other pipes end on stays, as a free node where it stood.
                    net = net.Put(new SnowNode(t.Id, SnowNodeKind.Junction, t.At, t.Label));
                    continue;
                }
                net = Disconnect(net, t.Id).Without(t.Id);
            }
            net = net.Without(pipe.Id);
            foreach (var end in new[] { pipe.From, pipe.To })
            {
                var n = net.FindNode(end);
                if (n == null || n.IsTap || n.Kind == SnowNodeKind.House || net.PipesAt(end).Count > 0) continue;
                net = net.Without(end);
            }
            return net;
        }

        static SnowNetwork RemoveHouse(SnowNetwork net, PumpHouse house)
        {
            var node = SnowRules.NodeOfHouse(net, house.Id);
            net = net.Without(house.Id);
            if (node == null) return net;
            return net.PipesAt(node.Id).Count > 0
                ? net.Put(new SnowNode(node.Id, SnowNodeKind.Junction, node.At, net.NextLabel(SnowNodeKind.Junction)))
                : net.Without(node.Id);
        }

        static SnowNetwork Disconnect(SnowNetwork net, SnowId hydrant)
        {
            var g = net.GunOn(hydrant);
            return g == null ? net : net.Put(g.WithHydrant(SnowId.None));
        }
    }
}
