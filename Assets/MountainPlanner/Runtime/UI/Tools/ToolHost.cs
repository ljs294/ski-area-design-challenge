using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Hud;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Runs the Toolbox's tools (P3-06): one armed at a time (CF-01), ticked each frame, Esc backing out one step
    /// (CF-07), and Break ground or Save plan turned into one command submitted to the design (CF-02, CF-05, CF-06),
    /// at most once a tick (CF-04). Disarming happens exactly once per arming and puts everything back: the preview,
    /// the window, and the keys the tool claimed (CF-01).
    /// </summary>
    public sealed class ToolHost
    {
        readonly Func<IDesignTool, ToolContext> _context;
        readonly Action<HudWindow> _open;
        ToolContext _ctx;
        bool _asking;
        long _tick, _confirmedTick = -1;
        ToolKeys _clicked;

        /// <param name="context">Makes a fresh context for each arming (the design, the preview, the ground).</param>
        /// <param name="open">Opens the tool's window in the HUD.</param>
        public ToolHost(Func<IDesignTool, ToolContext> context, Action<HudWindow> open)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _open = open ?? throw new ArgumentNullException(nameof(open));
        }

        public IDesignTool Active { get; private set; }
        public string ActiveId => Active?.Id;
        public bool Armed => Active != null;
        public ToolContext Context => _ctx;

        /// <summary>For tests (CF-01): how many times a tool was disarmed.</summary>
        public int Disarms { get; private set; }

        /// <summary>The armed tool changed (its id, or null).</summary>
        public event Action<string> ActiveChanged;

        /// <summary>A command went to the design (accepted or not).</summary>
        public event Action<CommandResult> Submitted;

        /// <summary>A one-line notice for the HUD's toasts.</summary>
        public event Action<string> Notice;

        /// <summary>
        /// Arms the tool with this id, or closes it when it's already armed (a second click on its tile or key). Any
        /// other armed tool is cancelled first, at once (CF-01), with a notice if it had work (P3-06 Q8).
        /// </summary>
        public bool Choose(string id)
        {
            if (Active != null && Active.Id == id) { Close(); return false; }
            if (!ToolCatalog.Find(id, out var entry)) return false;
            if (Active != null)
            {
                if (Active.HasWork) Notice?.Invoke(Active.Name + " discarded");
                Disarm();
            }
            var tool = entry.Make();
            _ctx = _context(tool);
            _ctx.Notice = s => Notice?.Invoke(s);
            var window = new ToolWindow(tool.Id, tool.Name, entry.Glyph);
            _ctx.Window = window;
            window.NextClicked += () => _clicked |= ToolKeys.Next;
            window.BreakGroundClicked += () => Confirm(WorkStage.Built);
            window.SavePlanClicked += () => Confirm(WorkStage.Plan);
            window.Window.CloseRequested = AllowClose;
            Active = tool;
            _asking = false;
            // Tab hands the tool window the keyboard while a tool is armed, instead of opening Analysis (P3-06 Q7).
            KeyBindings.Claim(GameAction.ToolWindowKeys, true);
            tool.Arm(_ctx);
            _open(window.Window);
            window.Window.Closed += () => { if (Active == tool) Disarm(); };
            ActiveChanged?.Invoke(tool.Id);
            return true;
        }

        /// <summary>Closes the armed tool without asking (the tray closing, Analysis, photo mode, leaving the area).</summary>
        public void Close()
        {
            if (Active != null) Disarm();
        }

        void Disarm()
        {
            var tool = Active;
            var ctx = _ctx;
            Active = null;
            _ctx = null;
            _asking = false;
            _clicked = ToolKeys.None;
            Disarms++;
            try { tool.Disarm(); }
            finally
            {
                ctx.Disarmed();
                KeyBindings.ClearClaims();
                ctx.Preview.Clear();
                ctx.Window?.Window.Close();
                ActiveChanged?.Invoke(null);
            }
        }

        // ✕ and Esc at the first step: drawn work asks once ("Discard it? ✕ again"), then goes (CF-06, CF-07).
        bool AllowClose()
        {
            if (Active == null) return true;
            if (Active.HasWork && !_asking)
            {
                _asking = true;
                _ctx.Window?.SetAsking("Discard it? Esc or ✕ again");
                return false;
            }
            return true;
        }

        /// <summary>Esc (CF-07): the tool backs out a step, or at its first step closes, asking first if anything is drawn. False with no tool.</summary>
        public bool Escape()
        {
            if (Active == null) return false;
            if (_asking) { Disarm(); return true; }
            if (Active.Escape() == ToolBackOut.Stepped) return true;
            if (AllowClose()) Disarm();
            return true;
        }

        /// <summary>Once a frame while a tool is armed.</summary>
        public void Tick(ToolInput input)
        {
            _tick++;
            if (Active == null) return;
            input.Keys |= _clicked;
            _clicked = ToolKeys.None;
            if (_asking && (input.Click || input.Keys != ToolKeys.None))
            {
                _asking = false;
                _ctx.Window?.SetAsking(null);
            }
            _ctx.Pump();   // finished analyses hand over (CF-03)
            _ctx.SnapReachMm = ToolContext.ReachFor(input.MetresPerPixel);
            if (Active == null) return;
            if (input.Pressed(ToolKeys.BreakGround)) { Confirm(WorkStage.Built); input.Keys &= ~ToolKeys.BreakGround; }
            Active.Tick(input);
        }

        /// <summary>
        /// Break ground (Built) or Save plan (Plan): the tool's command, submitted against the current revision. One
        /// confirmation a tick (CF-04); a rejected one leaves the tool in review with the reason shown.
        /// </summary>
        public CommandResult Confirm(WorkStage stage)
        {
            if (Active == null || _confirmedTick == _tick) return null;
            var window = _ctx.Window;
            if (window != null && !(stage == WorkStage.Built ? window.CanBreakGround : window.CanSavePlan)) return null;
            var command = Active.Commit(stage, _ctx.History.Revision);
            if (command == null) return null;
            _confirmedTick = _tick;
            // Break ground brings the analysis's terrain edits with it, so undo takes both back (TE2); a plan changes no ground (CF-06).
            var terrain = stage == WorkStage.Built ? _ctx.PendingTerrain : null;
            var result = _ctx.History.Submit(command, terrain);
            Submitted?.Invoke(result);
            if (!result.Accepted)
            {
                window?.SetMessage(result.Reason);
                return result;
            }
            _ctx.Preview.Clear();
            _ctx.PendingTerrain = null;
            Active.Committed(result);   // the tool says what it built, and starts over at step 1 (P3-06 Q4)
            return result;
        }
    }
}
