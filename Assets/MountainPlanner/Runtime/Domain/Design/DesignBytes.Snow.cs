namespace MountainPlanner.Domain.Design
{
    public static partial class DesignBytes
    {
        // Shape 15 (task 4.15, HY-01): the snowmaking network. Nodes (kind, place, label, source, pump, host pipe and direction, house),
        // pipes (name, diameter, route, ends, valves), pump houses (footprint, source, compressor, foundation) and guns
        // (model, place, hydrant, aim, override), each list in ID order.
        static void WriteSnow(Writer w, SnowNetwork net)
        {
            w.I32(net.Nodes.Count);
            foreach (var n in net.Nodes)
            {
                w.U64(n.Id.Value);
                w.U8((byte)n.Kind);
                w.Point(n.At);
                w.I32(n.Label);
                w.U64(n.Source);
                w.I32(n.Pump.Hp);
                w.I32(n.Pump.EfficiencyPermille);
                w.I32(n.Pump.BoostPsi);
                w.U8(n.Pump.On ? (byte)1 : (byte)0);
                w.U64(n.Host.Value);
                w.U8(n.Forward ? (byte)1 : (byte)0);
                w.U64(n.House.Value);
            }
            w.I32(net.Pipes.Count);
            foreach (var p in net.Pipes)
            {
                w.U64(p.Id.Value);
                w.Str(p.Name);
                w.I32(p.DiameterIn);
                w.I32(p.Route.Count);
                foreach (var q in p.Route) w.Point(q);
                w.U64(p.From.Value);
                w.U64(p.To.Value);
                w.U8((byte)((p.FromClosed ? 1 : 0) | (p.ToClosed ? 2 : 0)));
            }
            w.I32(net.Houses.Count);
            foreach (var h in net.Houses)
            {
                w.U64(h.Id.Value);
                w.Str(h.Name);
                w.Point(h.Centre);
                w.I32(h.BearingCentideg);
                w.I32(h.LengthMm);
                w.I32(h.WidthMm);
                w.U64(h.Source);
                w.I32(h.CompressorCfm);
                w.U8((byte)h.Foundation);
            }
            w.I32(net.Guns.Count);
            foreach (var g in net.Guns)
            {
                w.U64(g.Id.Value);
                w.U8((byte)g.Model);
                w.Point(g.At);
                w.U64(g.Hydrant.Value);
                w.I32(g.AimCentideg);
                w.U8((byte)g.Override);
            }
        }

        static SnowNetwork ReadSnow(Reader r)
        {
            var nodes = new SnowNode[r.Count(SnowRules.MaxNodes)];
            for (int i = 0; i < nodes.Length; i++)
            {
                var id = new SnowId(r.U64());
                var kind = (SnowNodeKind)r.U8();
                var at = r.Point();
                int label = r.I32();
                ulong source = r.U64();
                int hp = r.I32(), eff = r.I32(), boost = r.I32();
                bool on = r.Bool();
                var host = new SnowId(r.U64());
                bool forward = r.Bool();
                var house = new SnowId(r.U64());
                nodes[i] = new SnowNode(id, kind, at, label, source, new PumpSpec(hp, eff, boost, on), host, forward, house);
            }
            var pipes = new SnowPipe[r.Count(SnowRules.MaxPipes)];
            for (int i = 0; i < pipes.Length; i++)
            {
                var id = new SnowId(r.U64());
                string name = r.Str();
                int d = r.I32();
                var route = new PointMm[r.Count(SnowRules.MaxRoutePoints)];
                for (int k = 0; k < route.Length; k++) route[k] = r.Point();
                var from = new SnowId(r.U64());
                var to = new SnowId(r.U64());
                byte valves = r.U8();
                pipes[i] = new SnowPipe(id, name, d, route, from, to, (valves & 1) != 0, (valves & 2) != 0);
            }
            var houses = new PumpHouse[r.Count(SnowRules.MaxHouses)];
            for (int i = 0; i < houses.Length; i++)
            {
                var id = new SnowId(r.U64());
                string name = r.Str();
                var c = r.Point();
                int bearing = r.I32(), length = r.I32(), width = r.I32();
                ulong source = r.U64();
                int cfm = r.I32();
                houses[i] = new PumpHouse(id, name, c, bearing, length, width, source, cfm, (HouseFoundation)r.U8());
            }
            var guns = new SnowGun[r.Count(SnowRules.MaxGuns)];
            for (int i = 0; i < guns.Length; i++)
            {
                var id = new SnowId(r.U64());
                var model = (GunModel)r.U8();
                var at = r.Point();
                var hydrant = new SnowId(r.U64());
                int aim = r.I32();
                guns[i] = new SnowGun(id, model, at, hydrant, aim, (GunOverride)r.U8());
            }
            return SnowNetwork.From(nodes, pipes, houses, guns);
        }
    }
}
