using System;
using System.Collections.Generic;
using System.Globalization;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.15's cards (the approved mockup's snowpipecard and snowgunshort): a click on a snowmaking part with no tool armed
    /// opens its card in the water card's panel. A water line shows its size, length and climb, then the stretch under the
    /// click (flow and its way, pressure at either end, friction, velocity), the whole line's friction and its pressure class
    /// margin; a hydrant its pressure and gun; a gun its mode, state, stage and, when short, what it lacks and a fix; an inline
    /// pump its state, flow and head; a pump house its pump, air and what it pumps out of and into its source. Built from
    /// the design and the last solve only, so the same answer gives the same card; every button is one undoable command.
    /// </summary>
    public static class SnowCards
    {
        public const string KeyPrefix = "snow:";
        const double PipeClassPsi = 1000, ClassWarnPsi = 800, FastFtS = 15;

        public static string Key(SnowId id) => KeyPrefix + id.Value.ToString("x16", CultureInfo.InvariantCulture);

        public static bool TryParse(string key, out SnowId id)
        {
            id = SnowId.None;
            if (key == null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
            if (!ulong.TryParse(key.Substring(KeyPrefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong v)) return false;
            id = new SnowId(v);
            return true;
        }

        /// <summary>
        /// The card for <paramref name="id"/>, or null when the design has no such part. <paramref name="atMm"/> is where along
        /// a water line the click landed (its route's chainage), for the "Here" rows; <paramref name="sourceName"/> names a
        /// source by its id.
        /// </summary>
        public static WaterCardModel? For(SnowNetwork net, SnowSolution sol, SnowId id, double atMm, Func<ulong, string> sourceName, bool pending)
        {
            if (net == null) return null;
            // The solve must be of this network: one of another (an edit on its way) shows no figures.
            if (sol != null && !sol.Solved) sol = null;
            string key = Key(id);
            var rows = new List<(string, string)>();
            var acts = new List<(string, string, bool)>();
            string hint = pending ? "Updating: these figures are the last answer." : null;
            if (net.FindPipe(id) is SnowPipe pipe) return Pipe(net, sol, pipe, atMm, key, rows, acts, hint);
            if (net.FindGun(id) is SnowGun gun) return Gun(net, sol, gun, key, rows, acts, hint);
            if (net.FindHouse(id) is PumpHouse house) return House(net, sol, house, key, rows, acts, hint, sourceName);
            if (net.FindNode(id) is SnowNode node)
            {
                if (node.Kind == SnowNodeKind.House && net.FindHouse(node.House) is PumpHouse h) return House(net, sol, h, Key(h.Id), rows, acts, hint, sourceName);
                if (node.Kind == SnowNodeKind.Pump) return Pump(net, sol, node, key, rows, acts, hint);
                return Node(net, sol, node, key, rows, acts, hint, sourceName);
            }
            return null;
        }

        // ---------------- a water line ----------------

        static WaterCardModel Pipe(SnowNetwork net, SnowSolution sol, SnowPipe p, double atMm, string key, List<(string, string)> rows, List<(string, string, bool)> acts, string hint)
        {
            double lengthM = SnowGeometry.Length(p.Route) / 1000.0;
            var g = sol?.Graph;
            double climbFt = double.NaN;
            if (g != null)
            {
                int a = g.NodeOf(p.From), b = g.NodeOf(p.To);
                if (a >= 0 && b >= 0) climbFt = g.GradeFt[b] - g.GradeFt[a];
            }
            int hydrants = 0, guns = 0;
            var taps = net.TapsOn(p.Id);
            foreach (var t in taps)
            {
                if (t.Kind != SnowNodeKind.Hydrant) continue;
                hydrants++;
                foreach (var gun in net.Guns) if (gun.Hydrant == t.Id) { guns++; break; }
            }
            rows.Add(("Diameter", SnowSnap.Inches(p.DiameterIn)));
            rows.Add(("Length", ToolFormat.Length(lengthM) + (double.IsNaN(climbFt) ? "" : $" · {(climbFt >= 0 ? "climbs" : "drops")} {ToolFormat.Length(Math.Abs(climbFt) * 0.3048)}")));
            rows.Add(("Hydrants", $"{hydrants} · {SnowSnap.Count(guns, "gun")}"));
            var stretches = SnowProfile.Stretches(sol, p);
            if (stretches.Count == 0)
                rows.Add(("Water", sol == null ? "solving…" : "none reaches this line"));
            else
            {
                var here = stretches[0];
                foreach (var s in stretches) if (atMm >= s.FromMm && atMm <= s.ToMm) { here = s; break; }
                int la = g.A[here.Link], lb = g.B[here.Link];
                string ends = NodeName(net, g, la) + "–" + NodeName(net, g, lb);
                if (!double.IsNaN(here.FromPsi) || !double.IsNaN(here.ToPsi))
                {
                    double q = Math.Abs(here.Gpm);
                    // Downstream is where the flow goes: the stretch's far end when it runs with the route.
                    bool withRoute = here.Gpm >= 0;
                    double upPsi = withRoute ? here.FromPsi : here.ToPsi, downPsi = withRoute ? here.ToPsi : here.FromPsi;
                    rows.Add(("Here", ends));
                    if (q < 0.5) rows.Add(("Flow", "none: nothing past here is running"));
                    else
                    {
                        string way = "";
                        int to = withRoute ? (here.ToMm >= here.FromMm ? lb : la) : la;
                        int from = to == lb ? la : lb;
                        if (g.GradeFt[to] > g.GradeFt[from] + 1) way = " · uphill";
                        else if (g.GradeFt[to] < g.GradeFt[from] - 1) way = " · downhill";
                        rows.Add(("Flow", ToolFormat.Gpm(q) + way));
                    }
                    rows.Add(("Pressure", $"{SnowSnap.Psi(upPsi)} → {SnowSnap.Psi(downPsi)}"));
                    rows.Add(("Friction", $"{SnowSnap.Psi(here.FrictionPsi)} in {ToolFormat.Length(here.LengthFt * 0.3048)}"));
                    rows.Add(("Velocity", SnowSnap.Speed(here.VelocityFtS) + (here.VelocityFtS > FastFtS ? " · over 15 ft/s" : "")));
                }
                else rows.Add(("Water", "none reaches here"));
                double friction = 0, hi = double.NaN;
                foreach (var s in stretches)
                {
                    friction += s.FrictionPsi;
                    foreach (int n in new[] { g.A[s.Link], g.B[s.Link] })
                        if (sol.Known[n] && (double.IsNaN(hi) || sol.PipePsi[n] > hi)) hi = sol.PipePsi[n];
                }
                rows.Add(("Friction, whole line", SnowSnap.Psi(friction)));
                if (!double.IsNaN(hi))
                    rows.Add(("Class", $"{SnowSnap.Psi(hi)} of {SnowSnap.Psi(PipeClassPsi)} · " + (hi > PipeClassPsi ? "over the class" : $"margin {SnowSnap.Psi(PipeClassPsi - hi)}")));
            }
            rows.Add(("Valves", Valves(p)));
            // SG-06 (owner 2026-11-19: one pipe): the air line runs in the same trench; its compressors are the houses this
            // line's water connects to.
            var houses = new List<string>();
            int comp = g != null && g.NodeOf(p.From) is int f && f >= 0 ? g.Component[f] : -1;
            if (comp >= 0)
                foreach (var h in net.Houses)
                {
                    var hn = SnowRules.NodeOfHouse(net, h.Id);
                    int hi2 = hn != null ? g.NodeOf(hn.Id) : -1;
                    if (hi2 >= 0 && g.Component[hi2] == comp) houses.Add(h.Name);
                }
            rows.Add(("Air", "laid alongside" + (houses.Count > 0 ? " · from " + string.Join(", ", houses) : "")));
            int i = Array.IndexOf(SnowRules.Diameters, p.DiameterIn);
            if (i >= 0 && i + 1 < SnowRules.Diameters.Length) acts.Add(("upsize", $"Upsize to {SnowRules.Diameters[i + 1]} in", false));
            if (i > 0) acts.Add(("downsize", $"Down to {SnowRules.Diameters[i - 1]} in", false));
            acts.Add(("valvefrom", p.FromClosed ? "Open start valve" : "Close start valve", p.FromClosed));
            acts.Add(("valveto", p.ToClosed ? "Open end valve" : "Close end valve", p.ToClosed));
            return new WaterCardModel
            {
                Key = key, Kind = "Water line", Name = SnowSnap.Name(p), Renamable = true, Rows = rows.ToArray(), Actions = acts.ToArray(),
                Hint = Join(hint, "Click the name to rename it. Pressure at grade; the class is read at the buried depth."),
            };
        }

        static string Valves(SnowPipe p)
        {
            if (p.FromClosed && p.ToClosed) return "both ends closed";
            if (p.FromClosed) return "start closed · end open";
            if (p.ToClosed) return "start open · end closed";
            return "open";
        }

        // ---------------- a gun ----------------

        static WaterCardModel Gun(SnowNetwork net, SnowSolution sol, SnowGun gun, string key, List<(string, string)> rows, List<(string, string, bool)> acts, string hint)
        {
            var spec = GunCatalog.Spec(gun.Model);
            var hydrant = gun.Hydrant.IsNone ? null : net.FindNode(gun.Hydrant);
            int gi = sol != null ? sol.GunIndex(gun.Id) : -1;
            var state = gi >= 0 ? sol.GunStates[gi] : GunState.Idle;
            float psi = gi >= 0 ? sol.GunPsi[gi] : float.NaN;
            int stage = gi >= 0 ? sol.GunStage[gi] : 0;
            rows.Add(("Mode", gun.Override == GunOverride.On ? "On" : gun.Override == GunOverride.Off ? "Off" : "Auto · waits for the schedule"));
            rows.Add(("Now", sol == null ? "solving…" : hydrant == null ? "not on a hydrant" : Now(state, gun.Override)));
            if (stage > 0)
            {
                var st = GunCatalog.Stages[stage - 1];
                bool runs = state == GunState.Running || state == GunState.RunningWarm;
                rows.Add((runs ? "Stage" : "Would run at", $"stage {stage} of 5 · {st.Conditions} · {st.WaterGpm} gpm · {st.AirCfm} cfm"));
            }
            if (hydrant != null)
            {
                double hose = Math.Sqrt(gun.At.DistanceSquared(hydrant.At)) / 1000.0;
                rows.Add(("Hydrant", $"{SnowSnap.Label(net, hydrant)} · {ToolFormat.Length(hose)} hose"));
            }
            if (!float.IsNaN(psi)) rows.Add(("Pressure", $"{SnowSnap.Psi(psi)} · needs {SnowSnap.Psi(GunCatalog.MinPsi)}"));
            rows.Add(("Model", $"{spec.Name} · throws {spec.ThrowFt} ft"));
            string advice = null;
            if (state == GunState.ShortOfPressure && hydrant != null && !float.IsNaN(psi))
            {
                var line = net.FindPipe(hydrant.Host);
                int i = line != null ? Array.IndexOf(SnowRules.Diameters, line.DiameterIn) : -1;
                bool canUp = i >= 0 && i + 1 < SnowRules.Diameters.Length;
                advice = $"Needs {GunCatalog.MinPsi} psi, has {Math.Max(0, Math.Round(psi)):0}: "
                         + (canUp ? $"upsize {SnowSnap.Name(line)} to {SnowRules.Diameters[i + 1]} in, or add an inline pump below it." : "add an inline pump below it.");
                if (canUp) acts.Add(("gunupsize", $"Upsize {SnowSnap.Name(line)}", false));
            }
            else if (state == GunState.ShortOfAir) advice = "Its pump house's compressor is full: add air on the pump house's card, or turn other guns off.";
            else if (state == GunState.NoWater) advice = "No water reaches its hydrant: check for a closed valve, a pump facing the wrong way, or a missing pump house.";
            else if (state == GunState.RunningWarm) advice = "It's forced on above 28°F wet bulb: it draws water and makes no snow.";
            acts.Insert(0, ("auto", "Auto", gun.Override == GunOverride.Auto));
            acts.Insert(1, ("on", "On", gun.Override == GunOverride.On));
            acts.Insert(2, ("off", "Off", gun.Override == GunOverride.Off));
            return new WaterCardModel
            {
                Key = key, Kind = "Snow gun", Name = "Gun" + (hydrant != null ? " at " + SnowSnap.Label(net, hydrant) : ""), Renamable = false,
                Rows = rows.ToArray(), Actions = acts.ToArray(), Hint = Join(advice, hint),
            };
        }

        static string Now(GunState s, GunOverride o)
        {
            switch (s)
            {
                case GunState.Running: return "making snow";
                case GunState.RunningWarm: return "running, too warm for snow";
                case GunState.ShortOfPressure: return "short of pressure";
                case GunState.ShortOfAir: return "short of air";
                case GunState.NoWater: return "no water reaches its hydrant";
                case GunState.TooWarm: return "too warm to start";
                default: return o == GunOverride.Off ? "off" : "waiting";
            }
        }

        // ---------------- an inline pump ----------------

        static WaterCardModel Pump(SnowNetwork net, SnowSolution sol, SnowNode n, string key, List<(string, string)> rows, List<(string, string, bool)> acts, string hint)
        {
            var line = net.FindPipe(n.Host);
            rows.Add(("On", line != null ? SnowSnap.Name(line) : "—"));
            int k = PumpLink(sol, n.Id);
            if (sol == null) rows.Add(("Status", "solving…"));
            else if (k < 0) rows.Add(("Status", "not in the solve"));
            else
            {
                var g = sol.Graph;
                rows.Add(("Status", PumpStatus(sol.PumpStates[k], n.Pump.On, sol, g, k)));
                double q = sol.LinkGpm[k];
                rows.Add(("Flow", ToolFormat.Gpm(Math.Max(0, q))));
                bool known = sol.Known[g.A[k]] && sol.Known[g.B[k]];
                if (known)
                {
                    double pin = sol.GradePsi[g.A[k]], pout = sol.GradePsi[g.B[k]];
                    rows.Add(("In · out", $"{SnowSnap.Psi(pin)} → {SnowSnap.Psi(pout)}"));
                    rows.Add(("Adds", "+" + SnowSnap.Psi(Math.Max(0, pout - pin))));
                }
            }
            rows.Add(("Setpoint", $"+{SnowSnap.Psi(n.Pump.BoostPsi)} · {n.Pump.Hp:N0} hp at {n.Pump.EfficiencyPermille / 10}%"));
            acts.Add(("flip", "Flip", false));
            acts.Add(("pumpon", n.Pump.On ? "Turn off" : "Turn on", n.Pump.On));
            acts.Add(("boostdown", "−25 psi", false));
            acts.Add(("boostup", "+25 psi", false));
            return new WaterCardModel
            {
                Key = key, Kind = "Inline pump", Name = SnowSnap.Label(net, n), Renamable = false, Rows = rows.ToArray(), Actions = acts.ToArray(),
                Hint = Join(hint, "It holds its setpoint while its power lasts; off, it passes water forward only."),
            };
        }

        static string PumpStatus(PumpState s, bool on, SnowSolution sol, HydraulicGraph g, int k)
        {
            if (!on) return "off: passes water forward only";
            bool sucWet = sol.Known[g.A[k]], disWet = sol.Known[g.B[k]];
            if (!sucWet && disWet) return "faces away from the water: flip it";
            if (!sucWet) return "no water reaches it";
            switch (s)
            {
                case PumpState.PowerLimited: return "at its power limit";
                case PumpState.Throttled: return "throttled to the pipe class";
                case PumpState.NoForwardFlow: return "can't push: the water past it stands higher";
                default: return sol.LinkGpm[k] < 0.5 ? "nothing past it is running" : "holding its setpoint";
            }
        }

        static int PumpLink(SnowSolution sol, SnowId node)
        {
            if (sol == null) return -1;
            var g = sol.Graph;
            for (int k = 0; k < g.LinkCount; k++) if (g.LinkKind[k] == HLinkKind.Pump && g.LinkDesign[k] == node) return k;
            return -1;
        }

        // ---------------- a pump house ----------------

        static WaterCardModel House(SnowNetwork net, SnowSolution sol, PumpHouse h, string key, List<(string, string)> rows, List<(string, string, bool)> acts, string hint, Func<ulong, string> sourceName)
        {
            var node = SnowRules.NodeOfHouse(net, h.Id);
            var spec = node?.Pump ?? PumpSpec.House;
            rows.Add(("Pump", $"{spec.Hp:N0} hp at {spec.EfficiencyPermille / 10}% · holds {SnowSnap.Psi(spec.BoostPsi)}"));
            double q = 0;
            int k = node != null ? PumpLink(sol, node.Id) : -1;
            if (sol == null) rows.Add(("Now", "solving…"));
            else if (k >= 0)
            {
                var g = sol.Graph;
                q = Math.Max(0, sol.LinkGpm[k]);
                rows.Add(("Now", (sol.Known[g.B[k]] ? SnowSnap.Psi(sol.GradePsi[g.B[k]]) + " · " : "") + ToolFormat.Gpm(q)));
                rows.Add(("Status", PumpStatus(sol.PumpStates[k], spec.On, sol, g, k)));
                int c = g.Component[g.B[k]];
                if (c >= 0 && c < sol.AirDrawn.Length) rows.Add(("Air", $"{sol.AirDrawn[c]:N0} of {h.CompressorCfm:N0} cfm"));
            }
            if (k < 0 || sol == null) rows.Add(("Air", $"{h.CompressorCfm:N0} cfm"));
            string src = h.Source != 0 ? sourceName?.Invoke(h.Source) : null;
            rows.Add(("Source", string.IsNullOrEmpty(src) ? "none" : src));
            // SI-10: what it draws from its source, and how much of that fills a pond through an outlet.
            double fill = 0, into = 0;
            var intoFrom = new List<string>();
            if (sol != null)
            {
                var g = sol.Graph;
                for (int j = 0; j < g.LinkCount; j++)
                {
                    if (g.LinkKind[j] != HLinkKind.Outlet) continue;
                    double f = Math.Max(0, sol.LinkGpm[j]);
                    if (f < 0.5) continue;
                    var outlet = net.FindNode(g.LinkDesign[j]);
                    if (outlet == null) continue;
                    if (k >= 0 && g.Component[g.A[j]] == g.Component[g.B[k]]) fill += f;
                    if (outlet.Source == h.Source && h.Source != 0) into += f;
                }
                foreach (var other in net.Houses)
                {
                    if (other.Id == h.Id) continue;
                    // A house feeding an outlet in this house's source names itself in Pumped in.
                    var on = SnowRules.NodeOfHouse(net, other.Id);
                    int ok = on != null ? PumpLink(sol, on.Id) : -1;
                    if (ok < 0) continue;
                    for (int j = 0; j < g.LinkCount; j++)
                        if (g.LinkKind[j] == HLinkKind.Outlet && sol.LinkGpm[j] > 0.5 && g.Component[g.A[j]] == g.Component[g.B[ok]]
                            && net.FindNode(g.LinkDesign[j])?.Source == h.Source) { intoFrom.Add(other.Name); break; }
                }
            }
            rows.Add(("Pumped out", ToolFormat.Gpm(q) + (fill > 0.5 ? $" · {ToolFormat.Gpm(fill)} of it to fill a pond" : "")));
            rows.Add(("Pumped in", into > 0.5 ? ToolFormat.Gpm(into) + (intoFrom.Count > 0 ? " from " + string.Join(", ", intoFrom) : "") : "none"));
            acts.Add(("setdown", "−25 psi", false));
            acts.Add(("setup", "+25 psi", false));
            acts.Add(("airdown", "−500 cfm", false));
            acts.Add(("airup", "+500 cfm", false));
            return new WaterCardModel
            {
                Key = key, Kind = "Pump house", Name = h.Name, Renamable = true, Rows = rows.ToArray(), Actions = acts.ToArray(),
                Hint = Join(hint, "Click the name to rename it. The pump holds its setpoint while its power lasts."),
            };
        }

        // ---------------- a hydrant, junction, intake or outlet ----------------

        static WaterCardModel Node(SnowNetwork net, SnowSolution sol, SnowNode n, string key, List<(string, string)> rows, List<(string, string, bool)> acts, string hint, Func<ulong, string> sourceName)
        {
            var g = sol?.Graph;
            int i = g != null ? g.NodeOf(n.Id) : -1;
            var line = n.IsTap ? net.FindPipe(n.Host) : null;
            if (line != null) rows.Add(("Line", SnowSnap.Name(line)));
            if (sol == null) rows.Add(("Pressure", "solving…"));
            else if (i >= 0 && sol.Known[i]) rows.Add(("Pressure", SnowSnap.Psi(sol.GradePsi[i]) + (n.Kind == SnowNodeKind.Hydrant && sol.GradePsi[i] < GunCatalog.MinPsi ? " · under a gun's 200" : "")));
            else rows.Add(("Pressure", "no water reaches it"));
            if (i >= 0) rows.Add(("Elevation", ToolFormat.Elevation(g.GradeFt[i] * 0.3048)));
            string kind = "Junction";
            switch (n.Kind)
            {
                case SnowNodeKind.Hydrant:
                {
                    kind = "Hydrant";
                    SnowGun on = null;
                    foreach (var gun in net.Guns) if (gun.Hydrant == n.Id) { on = gun; break; }
                    rows.Add(("Gun", on == null ? "free" : (on.Override == GunOverride.On ? "On" : on.Override == GunOverride.Off ? "Off" : "Auto") + " · " + GunCatalog.Spec(on.Model).Name));
                    break;
                }
                case SnowNodeKind.Outlet:
                {
                    kind = "Outlet";
                    string s = n.Source != 0 ? sourceName?.Invoke(n.Source) : null;
                    rows.Add(("Fills", string.IsNullOrEmpty(s) ? "—" : s));
                    double f = 0;
                    if (g != null)
                        for (int k = 0; k < g.LinkCount; k++)
                            if (g.LinkKind[k] == HLinkKind.Outlet && g.LinkDesign[k] == n.Id) f = Math.Max(0, sol.LinkGpm[k]);
                    rows.Add(("Rate", ToolFormat.Gpm(f)));
                    break;
                }
                case SnowNodeKind.Intake:
                {
                    kind = "Intake";
                    string s = n.Source != 0 ? sourceName?.Invoke(n.Source) : null;
                    rows.Add(("Draws from", string.IsNullOrEmpty(s) ? "—" : s));
                    break;
                }
            }
            return new WaterCardModel
            {
                Key = key, Kind = kind, Name = SnowSnap.Label(net, n), Renamable = false, Rows = rows.ToArray(), Actions = acts.ToArray(), Hint = hint,
            };
        }

        static string NodeName(SnowNetwork net, HydraulicGraph g, int node)
        {
            var d = g.Design[node];
            var n = d.IsNone ? null : net.FindNode(d);
            if (n != null) return SnowSnap.Label(net, n);
            return g.Kind[node] == HNodeKind.HighPoint ? "high point" : "the line";
        }

        static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + " " + b;

        // ---------------- the buttons ----------------

        /// <summary>The command a card button makes, or null (nothing to do); <paramref name="done"/> is its toast.</summary>
        public static DesignCommand Action(SnowNetwork net, long revision, SnowId id, string action, out string done)
        {
            done = null;
            if (net == null) return null;
            if (net.FindPipe(id) is SnowPipe p)
            {
                int i = Array.IndexOf(SnowRules.Diameters, p.DiameterIn);
                switch (action)
                {
                    case "upsize" when i >= 0 && i + 1 < SnowRules.Diameters.Length:
                        done = $"{SnowSnap.Name(p)} upsized to {SnowRules.Diameters[i + 1]} in";
                        return new EditSnowPipe(revision, p.Id, p.Name, SnowRules.Diameters[i + 1], p.FromClosed, p.ToClosed);
                    case "downsize" when i > 0:
                        done = $"{SnowSnap.Name(p)} down to {SnowRules.Diameters[i - 1]} in";
                        return new EditSnowPipe(revision, p.Id, p.Name, SnowRules.Diameters[i - 1], p.FromClosed, p.ToClosed);
                    case "valvefrom": return new EditSnowPipe(revision, p.Id, p.Name, p.DiameterIn, !p.FromClosed, p.ToClosed);
                    case "valveto": return new EditSnowPipe(revision, p.Id, p.Name, p.DiameterIn, p.FromClosed, !p.ToClosed);
                }
                return null;
            }
            if (net.FindGun(id) is SnowGun gun)
            {
                switch (action)
                {
                    case "auto": return new SetGunOverride(revision, new[] { gun.Id }, GunOverride.Auto);
                    case "on": return new SetGunOverride(revision, new[] { gun.Id }, GunOverride.On);
                    case "off": return new SetGunOverride(revision, new[] { gun.Id }, GunOverride.Off);
                    case "gunupsize":
                        var h = gun.Hydrant.IsNone ? null : net.FindNode(gun.Hydrant);
                        var line = h != null ? net.FindPipe(h.Host) : null;
                        return line != null ? Action(net, revision, line.Id, "upsize", out done) : null;
                }
                return null;
            }
            var house = net.FindHouse(id);
            var node = house != null ? SnowRules.NodeOfHouse(net, house.Id) : net.FindNode(id);
            if (node == null || !node.IsPump) return null;
            var s = node.Pump;
            switch (action)
            {
                case "flip": done = SnowSnap.Label(net, node) + " flipped"; return new EditSnowPump(revision, node.Id, s, !node.Forward);
                case "pumpon": return new EditSnowPump(revision, node.Id, s.WithOn(!s.On), node.Forward);
                case "boostdown":
                case "setdown": return new EditSnowPump(revision, node.Id, new PumpSpec(s.Hp, s.EfficiencyPermille, Math.Max(25, s.BoostPsi - 25), s.On), node.Forward);
                case "boostup":
                case "setup": return new EditSnowPump(revision, node.Id, new PumpSpec(s.Hp, s.EfficiencyPermille, s.BoostPsi + 25, s.On), node.Forward);
                case "airdown" when house != null: return new EditSnowPump(revision, node.Id, s, node.Forward, Math.Max(500, house.CompressorCfm - 500));
                case "airup" when house != null: return new EditSnowPump(revision, node.Id, s, node.Forward, house.CompressorCfm + 500);
            }
            return null;
        }

        /// <summary>The rename a card's name makes: a water line's or a pump house's.</summary>
        public static DesignCommand Rename(SnowNetwork net, long revision, SnowId id, string name)
        {
            if (net?.FindPipe(id) is SnowPipe p) return new EditSnowPipe(revision, p.Id, name, p.DiameterIn, p.FromClosed, p.ToClosed);
            if (net?.FindHouse(id) is PumpHouse h) return new RenamePumpHouse(revision, h.Id, name);
            return null;
        }
    }
}
