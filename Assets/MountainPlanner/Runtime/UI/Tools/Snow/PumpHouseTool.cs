using System;
using System.Globalization;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.15's Pump house tool (P; HY-01, HY-05 v2, SG-06; the approved mockup's snowhouse): click its centre beside a
    /// source's water (a pond, a dam or a lake used for snowmaking, within 300 ft of its shore), then its bearing (the long
    /// side, at least 3 ft away), then review: a 60 × 40 ft house with a 1,000 hp pump holding 600 psi (to about 2,430 gpm
    /// before its power runs out), the compressor (5,000 cfm by default) and its foundation. Water lines connect anywhere on
    /// its footprint.
    /// </summary>
    public sealed class PumpHouseTool : IDesignTool, IKeepsWork
    {
        public const string ToolId = "pump";
        const int FootprintSlot = 0, CentreSlot = 1, BearingSlot = 2, WaterSlot = 3;
        const int MinBearingMm = 914;   // 3 ft
        static readonly string[] Foundations = { "Flatten pad", "Slab" };

        enum Step { Centre, Bearing, Review }

        ToolContext _ctx;
        Step _step;
        PointMm _centre;
        int _bearing;
        SourceNear? _source;
        HouseFoundation _foundation = HouseFoundation.Flatten;
        int _cfm = SnowRules.DefaultCompressorCfm;
        string _name;
        Action<int> _setFoundation;
        Action<int, string> _setCfm;
        readonly ReviewModel _review = new ReviewModel();
        readonly Vector2[] _box = new Vector2[4];
        readonly Vector2[] _two = new Vector2[2];

        public string Id => ToolId;
        public string Name => "Pump house";
        public bool HasWork => _step != Step.Centre;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            var w = context.Window;
            w.SetSteps(new[] { "Centre", "Bearing", "Review" });
            w.SetPlanShown(false);
            w.SetGoLabel("Break ground");
            w.RemoveOption("found");
            w.RemoveOption("cfm");
            _setFoundation = w.AddSwitch("found", "Foundation", Foundations, 0, i => { _foundation = i == 0 ? HouseFoundation.Flatten : HouseFoundation.Slab; Review(); },
                "A flattened pad cut and filled level, or a slab that follows the ground");
            _setCfm = w.AddSlider("cfm", "Compressor", SnowRules.MinCompressorCfm, SnowRules.MaxCompressorCfm, 500, _cfm,
                v => { _cfm = v; _setCfm(v, Cfm(v)); Review(); }, "SG-06: the air its guns share. A warm-stage gun takes 56 cfm, a cold one 16");
            _setCfm(_cfm, Cfm(_cfm));
            Restart();
        }

        static string Cfm(int cfm) => cfm.ToString("N0", CultureInfo.InvariantCulture) + " cfm";

        void Restart()
        {
            _step = Step.Centre;
            _source = null;
            _name = NextName();
            var w = _ctx.Window;
            w.SetName(_name);
            w.SetRenamable(Rename);
            w.SetStep(0);
            w.SetHint("Click the pump house's centre beside a pond, a dam or a lake you use for snowmaking water (within 300 ft of its shore).");
            w.SetKeys();
            w.SetMessage(null);
            w.ShowOption("found", false);
            w.ShowOption("cfm", false);
            w.SetNext(false, false);
            w.SetCommit(false, false);
            w.ShowReview(null);
            _ctx.Preview.Clear();
        }

        string NextName()
        {
            var net = _ctx.History.Document.Snow;
            for (int n = net.Houses.Count + 1; ; n++)
            {
                string name = "Pump house " + n;
                bool taken = false;
                foreach (var h in net.Houses) taken |= h.Name == name;
                if (!taken) return name;
            }
        }

        string Rename(string name)
        {
            if (DesignRules.NameProblem(name) is string p) return p;
            _name = name;
            _ctx.Window.SetName(name);
            return null;
        }

        public void Tick(in ToolInput input)
        {
            bool here = input.HasGround && !input.OverPanel;
            switch (_step)
            {
                case Step.Centre:
                {
                    if (!here) { _ctx.Preview.Hide(FootprintSlot); _ctx.Preview.Hide(WaterSlot); return; }
                    var near = _ctx.SourceNear?.Invoke(input.Point, SnowRules.HouseReachMm);
                    DrawHouse(input.Point, 0, near.HasValue ? PreviewInk.Ok : PreviewInk.Bad);
                    if (near.HasValue) { _two[0] = SnowSnap.Xz(input.Point); _two[1] = SnowSnap.Xz(near.Value.Water); _ctx.Preview.Line(WaterSlot, _two, 2, PreviewInk.Snap); }
                    else _ctx.Preview.Hide(WaterSlot);
                    if (!input.Click) return;
                    if (!input.InCore) { _ctx.Window.SetMessage("Keep the pump house inside the core.", true); return; }
                    if (!near.HasValue) { _ctx.Window.SetMessage("Stand it beside a pond, a dam or a lake you use for snowmaking (Lake, L): within 300 ft of the water.", true); return; }
                    if (near.Value.DistanceMetres <= 0.01) { _ctx.Window.SetMessage("Stand it on the shore, not in the water.", true); return; }
                    _centre = input.Point;
                    _source = near;
                    _step = Step.Bearing;
                    var w = _ctx.Window;
                    w.SetStep(1);
                    w.SetHint("Draws from " + near.Value.Name + ", " + ToolFormat.Length(near.Value.DistanceMetres) + " from the water. Click again to turn it: its long side points at the pointer.");
                    w.SetMessage(null);
                    _ctx.Preview.Dot(CentreSlot, SnowSnap.Xz(_centre), PreviewInk.Snap);
                    return;
                }
                case Step.Bearing:
                {
                    if (!here) return;
                    long d2 = _centre.DistanceSquared(input.Point);
                    if (d2 >= (long)MinBearingMm * MinBearingMm)
                        _bearing = BearingTo(_centre, input.Point);
                    DrawHouse(_centre, _bearing, PreviewInk.Ok);
                    _two[0] = SnowSnap.Xz(_centre); _two[1] = SnowSnap.Xz(input.Point);
                    _ctx.Preview.Line(BearingSlot, _two, 2, PreviewInk.Line);
                    if (!input.Click) return;
                    if (d2 < (long)MinBearingMm * MinBearingMm) { _ctx.Window.SetMessage("Click at least 3 ft from the centre to set the bearing.", true); return; }
                    _step = Step.Review;
                    _ctx.Preview.Hide(BearingSlot);
                    var w = _ctx.Window;
                    w.SetStep(2);
                    w.SetHint("Check it and Break ground. Then draw a water line from it (N).");
                    w.SetMessage(null);
                    w.ShowOption("found", true);
                    w.ShowOption("cfm", true);
                    _setFoundation((int)(_foundation == HouseFoundation.Flatten ? 0 : 1));
                    Review();
                    return;
                }
            }
        }

        static int BearingTo(PointMm a, PointMm b)
        {
            double deg = Math.Atan2(b.X - a.X, b.Y - a.Y) * 180 / Math.PI;
            return ((int)Math.Round(deg * 100) % 36000 + 36000) % 36000;
        }

        void DrawHouse(PointMm centre, int bearing, PreviewInk ink)
        {
            int n = SnowSnap.Footprint(centre, bearing, _box);
            _ctx.Preview.Line(FootprintSlot, _box, n, ink, true);
        }

        AddPumpHouse Command(long revision) => new AddPumpHouse(revision, _name, _centre, _bearing, _source?.Id ?? 0, _foundation, _cfm);

        void Review()
        {
            if (_ctx == null || _step != Step.Review) return;
            var spec = PumpSpec.House;
            double holds = 3960.0 * spec.Hp * spec.EfficiencyPermille / 1000.0 / (spec.BoostPsi * 2.31);
            var check = Command(_ctx.History.Revision).Apply(_ctx.History.Document);
            _review.Clear();
            _review.Add("Source", $"{_source?.Name} · {ToolFormat.Length(_source?.DistanceMetres ?? 0)} to the water", "WS-05: its intake draws from it");
            _review.Add("Building", (DisplayUnits.Current == Domain.Measure.UnitSystem.Imperial ? "60 × 40 ft" : "18 × 12 m") + " · " + (_foundation == HouseFoundation.Flatten ? "on a flattened pad" : "on a slab"));
            _review.Add("Pump", $"{spec.Hp:N0} hp at {spec.EfficiencyPermille / 10}% · holds {spec.BoostPsi} psi", "HY-05 v2: a pressure setpoint up to its power; set it on its card");
            _review.Add("Holds 600 psi to", ToolFormat.Gpm(holds), "Past that its 1,000 hp runs out and the pressure falls (3,960 · hp · η ÷ Q ft)");
            _review.Add("Compressor", Cfm(_cfm) + $" · about {_cfm / 56} warm or {_cfm / 16} cold guns", "SG-06: 56 cfm a gun at stages 1–3, 16 at 4–5");
            if (_foundation == HouseFoundation.Flatten) _review.Warn("The pad's cut and fill comes with the pump house's model; until then the ground stays as it is.", null);
            if (!check.Accepted) _review.Warn(check.Reason, null, true);
            _review.Ready(check.Accepted, false);
            _ctx.Window.ShowReview(_review);
            _ctx.Window.SetCommit(check.Accepted, false);
        }

        public ToolBackOut Escape()
        {
            if (_step == Step.Review) { _step = Step.Bearing; _ctx.Window.SetStep(1); _ctx.Window.ShowReview(null); _ctx.Window.ShowOption("found", false); _ctx.Window.ShowOption("cfm", false); _ctx.Window.SetCommit(false, false); return ToolBackOut.Stepped; }
            if (_step == Step.Bearing) { Restart(); return ToolBackOut.Stepped; }
            return ToolBackOut.Close;
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision) =>
            stage == WorkStage.Built && _step == Step.Review ? Command(baseRevision) : null;

        public void Committed(CommandResult result)
        {
            _ctx.Notice?.Invoke(_name + " built");
            Restart();
        }

        public void AskBeforeLeaving(Action then) => _ctx.Window.SetAskRow("Leave without building this pump house?", GameAction.ToolNext,
            ("leave", "Leave", () => { _step = Step.Centre; then(); }), ("stay", "Stay", () => _ctx.Window.SetAskRow(null, GameAction.ToolNext)));

        public void Disarm()
        {
            if (_ctx == null) return;
            _ctx.Preview.Clear();
            _ctx = null;
        }

        // For tests and scripted captures.
        public bool Reviewing => _step == Step.Review;
        public ulong Source => _source?.Id ?? 0;
        public int Bearing => _bearing;
    }
}
