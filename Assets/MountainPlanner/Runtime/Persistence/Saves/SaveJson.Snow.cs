using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Persistence.Saves
{
    public static partial class SaveJson
    {
        // v15 (task 4.15, HY-01, SM-02): "Snowmaking" holds "Nodes", "Pipes", "PumpHouses" and "Guns", each in ID order. Kinds,
        // models, overrides and foundations are words; IDs and sources are hex like the rest of the design.
        static void WriteSnow(JsonWriter w, SnowNetwork net)
        {
            w.WritePropertyName("Snowmaking");
            w.WriteStartObject();
            w.WritePropertyName("Nodes");
            w.WriteStartArray();
            foreach (var n in net.Nodes)
            {
                w.WriteStartObject();
                w.WritePropertyName("Id"); w.WriteValue(Hex(n.Id.Value));
                w.WritePropertyName("Kind"); w.WriteValue(NodeWord(n.Kind));
                w.WritePropertyName("At"); WritePoint(w, n.At);
                w.WritePropertyName("Label"); w.WriteValue(n.Label);
                w.WritePropertyName("Source"); w.WriteValue(Hex(n.Source));
                w.WritePropertyName("Host"); w.WriteValue(Hex(n.Host.Value));
                w.WritePropertyName("Forward"); w.WriteValue(n.Forward);
                w.WritePropertyName("House"); w.WriteValue(Hex(n.House.Value));
                w.WritePropertyName("Hp"); w.WriteValue(n.Pump.Hp);
                w.WritePropertyName("EfficiencyPermille"); w.WriteValue(n.Pump.EfficiencyPermille);
                w.WritePropertyName("BoostPsi"); w.WriteValue(n.Pump.BoostPsi);
                w.WritePropertyName("PumpOn"); w.WriteValue(n.Pump.On);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("Pipes");
            w.WriteStartArray();
            foreach (var p in net.Pipes)
            {
                w.WriteStartObject();
                w.WritePropertyName("Id"); w.WriteValue(Hex(p.Id.Value));
                w.WritePropertyName("Name"); w.WriteValue(p.Name);
                w.WritePropertyName("DiameterIn"); w.WriteValue(p.DiameterIn);
                w.WritePropertyName("Route");
                w.WriteStartArray();
                foreach (var q in p.Route) WritePoint(w, q);
                w.WriteEndArray();
                w.WritePropertyName("From"); w.WriteValue(Hex(p.From.Value));
                w.WritePropertyName("To"); w.WriteValue(Hex(p.To.Value));
                w.WritePropertyName("FromClosed"); w.WriteValue(p.FromClosed);
                w.WritePropertyName("ToClosed"); w.WriteValue(p.ToClosed);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("PumpHouses");
            w.WriteStartArray();
            foreach (var h in net.Houses)
            {
                w.WriteStartObject();
                w.WritePropertyName("Id"); w.WriteValue(Hex(h.Id.Value));
                w.WritePropertyName("Name"); w.WriteValue(h.Name);
                w.WritePropertyName("Centre"); WritePoint(w, h.Centre);
                w.WritePropertyName("BearingCentideg"); w.WriteValue(h.BearingCentideg);
                w.WritePropertyName("LengthMm"); w.WriteValue(h.LengthMm);
                w.WritePropertyName("WidthMm"); w.WriteValue(h.WidthMm);
                w.WritePropertyName("Source"); w.WriteValue(Hex(h.Source));
                w.WritePropertyName("CompressorCfm"); w.WriteValue(h.CompressorCfm);
                w.WritePropertyName("Foundation"); w.WriteValue(h.Foundation == HouseFoundation.Slab ? "slab" : "flatten");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WritePropertyName("Guns");
            w.WriteStartArray();
            foreach (var g in net.Guns)
            {
                w.WriteStartObject();
                w.WritePropertyName("Id"); w.WriteValue(Hex(g.Id.Value));
                w.WritePropertyName("Model"); w.WriteValue(GunWord(g.Model));
                w.WritePropertyName("At"); WritePoint(w, g.At);
                w.WritePropertyName("Hydrant"); w.WriteValue(Hex(g.Hydrant.Value));
                w.WritePropertyName("AimCentideg"); w.WriteValue(g.AimCentideg);
                w.WritePropertyName("Override"); w.WriteValue(g.Override == GunOverride.On ? "on" : g.Override == GunOverride.Off ? "off" : "auto");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }

        static SnowNetwork ReadSnow(JObject d)
        {
            var s = Obj(d, "Snowmaking");
            var nodes = new List<SnowNode>();
            foreach (var t in Arr(s, "Nodes"))
            {
                var o = AsObj(t);
                var pump = new PumpSpec(Int(o, "Hp"), Int(o, "EfficiencyPermille"), Int(o, "BoostPsi"), Bool(o, "PumpOn"));
                nodes.Add(new SnowNode(new SnowId(HexValue(Str(o, "Id"))), NodeKind(Str(o, "Kind")), Point(o, "At"), Int(o, "Label"),
                                       HexValue(Str(o, "Source")), pump, new SnowId(HexValue(Str(o, "Host"))), Bool(o, "Forward"),
                                       new SnowId(HexValue(Str(o, "House")))));
            }
            var pipes = new List<SnowPipe>();
            foreach (var t in Arr(s, "Pipes"))
            {
                var o = AsObj(t);
                var route = new List<PointMm>();
                foreach (var q in Arr(o, "Route"))
                {
                    if (!(q is JArray xy) || xy.Count != 2) throw new FormatException("A pipe point isn't a point");
                    route.Add(new PointMm(IntOf(xy[0]), IntOf(xy[1])));
                }
                pipes.Add(new SnowPipe(new SnowId(HexValue(Str(o, "Id"))), Str(o, "Name"), Int(o, "DiameterIn"), route,
                                       new SnowId(HexValue(Str(o, "From"))), new SnowId(HexValue(Str(o, "To"))), Bool(o, "FromClosed"), Bool(o, "ToClosed")));
            }
            var houses = new List<PumpHouse>();
            foreach (var t in Arr(s, "PumpHouses"))
            {
                var o = AsObj(t);
                string f = Str(o, "Foundation");
                if (f != "flatten" && f != "slab") throw new FormatException($"'{f}' isn't a pump house's foundation");
                houses.Add(new PumpHouse(new SnowId(HexValue(Str(o, "Id"))), Str(o, "Name"), Point(o, "Centre"), Int(o, "BearingCentideg"),
                                         Int(o, "LengthMm"), Int(o, "WidthMm"), HexValue(Str(o, "Source")), Int(o, "CompressorCfm"),
                                         f == "slab" ? HouseFoundation.Slab : HouseFoundation.Flatten));
            }
            var guns = new List<SnowGun>();
            foreach (var t in Arr(s, "Guns"))
            {
                var o = AsObj(t);
                string ov = Str(o, "Override");
                var over = ov == "auto" ? GunOverride.Auto : ov == "on" ? GunOverride.On : ov == "off" ? GunOverride.Off
                         : throw new FormatException($"'{ov}' isn't a gun's override");
                guns.Add(new SnowGun(new SnowId(HexValue(Str(o, "Id"))), GunKind(Str(o, "Model")), Point(o, "At"),
                                     new SnowId(HexValue(Str(o, "Hydrant"))), Int(o, "AimCentideg"), over));
            }
            return SnowNetwork.From(nodes, pipes, houses, guns);
        }

        static string NodeWord(SnowNodeKind k)
        {
            switch (k)
            {
                case SnowNodeKind.Intake: return "intake";
                case SnowNodeKind.Junction: return "junction";
                case SnowNodeKind.Hydrant: return "hydrant";
                case SnowNodeKind.Pump: return "pump";
                case SnowNodeKind.Outlet: return "outlet";
                case SnowNodeKind.House: return "house";
                default: throw new ArgumentOutOfRangeException(nameof(k));
            }
        }

        static SnowNodeKind NodeKind(string w)
        {
            switch (w)
            {
                case "intake": return SnowNodeKind.Intake;
                case "junction": return SnowNodeKind.Junction;
                case "hydrant": return SnowNodeKind.Hydrant;
                case "pump": return SnowNodeKind.Pump;
                case "outlet": return SnowNodeKind.Outlet;
                case "house": return SnowNodeKind.House;
                default: throw new FormatException($"'{w}' isn't a snowmaking node");
            }
        }

        static string GunWord(GunModel m)
        {
            switch (m)
            {
                case GunModel.Stick10: return "stick10";
                case GunModel.Stick20: return "stick20";
                case GunModel.Stick30: return "stick30";
                case GunModel.Ground: return "ground";
                default: throw new ArgumentOutOfRangeException(nameof(m));
            }
        }

        static GunModel GunKind(string w)
        {
            switch (w)
            {
                case "stick10": return GunModel.Stick10;
                case "stick20": return GunModel.Stick20;
                case "stick30": return GunModel.Stick30;
                case "ground": return GunModel.Ground;
                default: throw new FormatException($"'{w}' isn't a gun model");
            }
        }
    }
}
