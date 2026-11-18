using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.14's Water source tool (WS-01, LK-02; the Water group, L): click a lake to open its source card, then use it
    /// for snowmaking water or stop. Its depth is LK-02 v1's estimate (always clamped, the card says "estimate"), or yours:
    /// [ and ] a foot. Every pond and dam is a source already (WS-01: no checkbox, a MapLibre step dropped); a click on one
    /// says so. The intake (WS-05) is placed by task 4.15's pipes.
    /// </summary>
    public sealed class WaterSourceTool : IDesignTool
    {
        public const string ToolId = "water-source";
        const int FootTenths = 10;
        static readonly string[] Uses = { "Off", "Snowmaking source" };

        ToolContext _ctx;
        LakeUnder? _lake;
        int _depthTenths;   // 0: the estimate
        Action<int> _setUse;
        readonly ReviewModel _review = new ReviewModel();

        public string Id => ToolId;
        public string Name => "Water source";
        public bool HasWork => false;   // each change is its own command

        public void Arm(ToolContext context)
        {
            _ctx = context;
            var w = context.Window;
            w.SetName(null);
            w.SetSteps(new[] { "Pick a lake" });
            w.SetStep(0);
            w.SetPlanShown(false);
            w.SetCommit(false, false);
            w.SetNext(false, false);
            w.RemoveOption("use");
            _setUse = w.AddSwitch("use", "Use", Uses, 0, Use, "WS-01: a lake you use for snowmaking water draws down (LK-02's drawdown limit, task 4.14 PR B)");
            w.ShowOption("use", false);
            Idle();
        }

        void Idle()
        {
            _lake = null;
            _depthTenths = 0;
            var w = _ctx.Window;
            w.SetName(null);
            w.SetHint("Click a lake to use it for snowmaking water. Ponds and dams are sources already.");
            w.SetMessage(null);
            w.SetKeys();
            w.ShowOption("use", false);
            w.ShowReview(null);
        }

        public void Tick(in ToolInput input)
        {
            if (_lake.HasValue)
            {
                if (input.Pressed(ToolKeys.BrushWider)) Depth(+FootTenths);
                if (input.Pressed(ToolKeys.BrushNarrower)) Depth(-FootTenths);
            }
            if (!input.Click || input.OverPanel || !input.HasGround) return;
            var doc = _ctx.History.Document;
            ulong water = WaterPick.Under(doc, _ctx.Earthwork, input.Point);
            if (water != 0) { Idle(); _ctx.Window.SetMessage($"{WaterPick.NameOf(doc, water)} is a source already: every pond and dam is."); return; }
            var lake = _ctx.LakeAt?.Invoke(input.Point);
            if (!lake.HasValue) { if (_lake.HasValue) Idle(); return; }
            Choose(lake.Value);
        }

        /// <summary>Opens a lake's source card (a click on it, or its info card).</summary>
        public void Choose(LakeUnder lake)
        {
            _lake = lake;
            _depthTenths = _ctx.History.Document.FindLake(lake.Id).DepthTenthsFt;
            var w = _ctx.Window;
            w.SetName(lake.Label);
            w.SetHint("Use it for snowmaking water, or stop. [ and ] set its depth a foot when you know better than the estimate.");
            w.SetKeys((GameAction.BrushWider, "deeper"), (GameAction.BrushNarrower, "shallower"));
            w.ShowOption("use", true);
            Card();
        }

        /// <summary>For scripted captures: the Use switch.</summary>
        public void SetUse(bool on) => Use(on ? 1 : 0);

        bool On => _lake.HasValue && _ctx.History.Document.IsLakeSource(_lake.Value.Id);

        void Use(int i)
        {
            if (!_lake.HasValue) return;
            bool on = i == 1;
            if (on == On) { Card(); return; }
            var r = _ctx.Submit?.Invoke(new SetLakeSource(_ctx.History.Revision, _lake.Value.Id, on, _depthTenths), null);
            if (r != null && !r.Accepted) _ctx.Window.SetMessage(r.Reason, true);
            else _ctx.Notice?.Invoke(on ? _lake.Value.Label + " is a water source" : _lake.Value.Label + " is no longer a source");
            Card();
        }

        void Depth(int byTenths)
        {
            int now = _depthTenths > 0 ? _depthTenths : _lake.Value.EstimatedDepthTenthsFt;
            int next = Math.Max(1, Math.Min(PondRules.LakeDepthMaxTenthsFt, now + byTenths));
            _depthTenths = next == _lake.Value.EstimatedDepthTenthsFt ? 0 : next;
            // LK-10: the depth is the lake's own record, kept whether or not it is a source.
            var doc = _ctx.History.Document;
            var r = _ctx.Submit?.Invoke(new SetLake(_ctx.History.Revision, _lake.Value.Id, doc.FindLake(_lake.Value.Id).Name, _depthTenths), null);
            if (r != null && !r.Accepted && r.Reason != "Nothing to change.") _ctx.Window.SetMessage(r.Reason, true);
            Card();
        }

        // The source card (the mockup's lakesource): area, depth (estimate or yours), the water it holds.
        void Card()
        {
            var lake = _lake.Value;
            bool on = On;
            _setUse?.Invoke(on ? 1 : 0);
            int tenths = _depthTenths > 0 ? _depthTenths : lake.EstimatedDepthTenthsFt;
            double depthM = tenths / 10.0 * 0.3048;
            double volume = lake.AreaSquareMetres * depthM;
            _review.Clear();
            _review.Add("Area", ToolFormat.Area(lake.AreaSquareMetres))
                .Add("Mean depth", ToolFormat.Depth(depthM) + (_depthTenths > 0 ? " · yours" : " · estimate"), "LK-02 v1")
                .Add("Holds", $"{ToolFormat.Water(volume)} · {ToolFormat.Gallons(volume)}")
                .Add("Draw down", "up to 3 ft", "WS-05: a lake keeps its shore")
                .Add("Use", on ? "snowmaking source" : "off", "WS-01");
            _review.Ready(false, false);
            _ctx.Window.ShowReview(_review);
        }

        public ToolBackOut Escape()
        {
            if (!_lake.HasValue) return ToolBackOut.Close;
            Idle();
            return ToolBackOut.Stepped;
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision) => null;   // the switch submits
        public void Committed(CommandResult result) { }
        public void Disarm() { _ctx = null; }

        // For tests.
        public ulong Lake => _lake?.Id ?? 0;
        public int DepthTenths => _depthTenths;
    }
}
