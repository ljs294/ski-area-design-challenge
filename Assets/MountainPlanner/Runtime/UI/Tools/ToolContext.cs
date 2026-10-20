using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// What an armed tool may use (P3-06): the design (read it, never change it: commands go through the host), the
    /// pending preview, its window, the ground, the area's core box (SH-06), snapping (TR-05) and its keys (LF-09).
    /// </summary>
    public sealed class ToolContext
    {
        public DesignHistory History { get; }
        public IToolPreview Preview { get; }
        public ITerrainSurface Surface { get; }
        /// <summary>The area's core box, local x/z metres: trails, lifts and paths stay inside it (SH-06).</summary>
        public Rect Core { get; }
        public ToolWindow Window { get; internal set; }

        /// <summary>Task 08's centreline snapping (NW-07); null until trails have a centreline.</summary>
        public ICentrelineSnap Lines { get; set; }

        /// <summary>
        /// The terrain edits the tool's current analysis would commit at Break ground (task 05, TE2): the complete edit
        /// set once applied (<see cref="DesignHistory.Terrain"/> plus this tool's grading and clearing), or null for none.
        /// The host submits it with the command and clears it on commit; each tool starts with none. Task 08 sets it.
        /// </summary>
        public Domain.Edits.TerrainEdits PendingTerrain { get; set; }

        /// <summary>
        /// The ground as edited now (task 05's heights in the package's frame), for builds that need the real terrain (task 09's
        /// lifts); null while the edit session is still starting.
        /// </summary>
        public Func<Domain.Edits.EditedHeights> EditedGround { get; set; }

        /// <summary>The terminals being placed, as their own models (task 09, LT19, LT22); null without the lift models.</summary>
        public Presentation.Lifts.TerminalGhost BottomGhost { get; set; }
        public Presentation.Lifts.TerminalGhost TopGhost { get; set; }

        /// <summary>A design point (site-centred mm, TE5) in <see cref="EditedGround"/>'s frame.</summary>
        public Func<PointMm, (double X, double Y)> ToGround { get; set; }

        /// <summary>The built lift under the pointer, lit by the viewer's hover (task 09, LT23), or 0; set while a tool that
        /// picks lifts (<see cref="IPicksLifts"/>) is armed.</summary>
        public Func<ulong> LiftUnderPointer { get; set; }

        /// <summary>Keeps a lift lit while the tool has it selected (0 for none): Remove lift's first click, Edit lift's lift.</summary>
        public Action<ulong> HoldLift { get; set; }

        /// <summary>
        /// Submits a command the tool makes itself (Remove lift, Edit lift's Apply), with the complete edit set once applied
        /// (null keeps the ground), through the host, so it's reported like Break ground. Set by the host.
        /// </summary>
        public Func<DesignCommand, Domain.Edits.TerrainEdits, CommandResult> Submit { get; internal set; }

        /// <summary>A one-line notice in the HUD's toasts ("New trail discarded").</summary>
        public Action<string> Notice { get; set; }

        long _input;

        public ToolContext(DesignHistory history, IToolPreview preview, ITerrainSurface surface, Rect core)
        {
            History = history ?? throw new ArgumentNullException(nameof(history));
            Preview = preview ?? throw new ArgumentNullException(nameof(preview));
            Surface = surface;
            Core = core;
        }

        /// <summary>
        /// How far snapping reaches now, in mm: 30 ft (TR-05), or 6 pixels on the screen when that's more, so ends still
        /// snap zoomed far out (owner, P3-06 round 2). The host sets it each tick from the pointer's distance.
        /// </summary>
        public int SnapReachMm { get; internal set; } = DesignUnits.SnapMm;

        /// <summary>Snapping's on-screen reach (owner, P3-06 round 2).</summary>
        public const float SnapPixels = 6;

        /// <summary>The nearest snap target within <see cref="SnapReachMm"/>, or the point itself.</summary>
        public SnapHit Snap(PointMm at, SnapTargets targets, ulong skip = 0) => DesignSnap.Nearest(History.Document, at, targets, Lines, skip, SnapReachMm);

        /// <summary>The snap reach for a pointer where one pixel covers <paramref name="metresPerPixel"/> of ground.</summary>
        public static int ReachFor(float metresPerPixel) =>
            Math.Max(DesignUnits.SnapMm, (int)Math.Min(int.MaxValue / 2, Math.Round(metresPerPixel * SnapPixels * 1000)));

        /// <summary>Ground height at a local x/z (NaN off the terrain).</summary>
        public float HeightAt(Vector2 xz) => Surface != null ? Surface.HeightAt(xz.x, xz.y) : float.NaN;

        public bool InCore(Vector2 xz) => Core.Contains(xz);

        /// <summary>Claims a tool key, so the camera or HUD action on the same key waits (LF-09: Page Up and Page Down).</summary>
        public void Claim(GameAction action, bool on) => KeyBindings.Claim(action, on);

        /// <summary>The stamp an analysis started now would carry (CF-03); each call is a new input.</summary>
        public AnalysisStamp NextStamp() => History.Stamp(++_input);

        /// <summary>The stamp a result must carry to still be current.</summary>
        public AnalysisStamp CurrentStamp => History.Stamp(_input);

        /// <summary>
        /// Runs <paramref name="work"/> off the main thread (CF-02 step 3). The host hands the result to
        /// <paramref name="done"/> on the main thread at its next tick, only if nothing moved on meanwhile: the design,
        /// the terrain, or a newer input (CF-03). A result that's stale is dropped, never merged.
        /// </summary>
        public void Analyse<T>(Func<T> work, Action<T> done)
        {
            if (_disarmed) return;
            _pending.Add(new Pending<T>(Task.Run(work), NextStamp(), done, this));
        }

        interface IPending { bool Finish(); }

        sealed class Pending<T> : IPending
        {
            readonly Task<T> _task;
            readonly AnalysisStamp _stamp;
            readonly Action<T> _done;
            readonly ToolContext _ctx;
            public Pending(Task<T> task, AnalysisStamp stamp, Action<T> done, ToolContext ctx) { _task = task; _stamp = stamp; _done = done; _ctx = ctx; }

            public bool Finish()
            {
                if (!_task.IsCompleted) return false;
                if (_task.IsFaulted) Debug.LogException(_task.Exception);
                else if (!_task.IsCanceled && new Stamped<T>(_stamp, _task.Result).TryTake(_ctx.CurrentStamp, out var value)) _done(value);
                return true;
            }
        }

        readonly List<IPending> _pending = new List<IPending>();

        /// <summary>How many analyses are still running (tests).</summary>
        public int Running => _pending.Count;

        /// <summary>The host's tick: finished analyses hand over their results. Allocates nothing.</summary>
        internal void Pump()
        {
            for (int i = 0; i < _pending.Count;)
            {
                if (_pending[i].Finish()) _pending.RemoveAt(i);
                else i++;
            }
        }

        bool _disarmed;
        internal void Disarmed()
        {
            _disarmed = true;
            ++_input;
            _pending.Clear();
        }
    }
}
