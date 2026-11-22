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
        /// <summary>The ground a trail tool paints, brush-style (TF-02); null in tests without a map.</summary>
        public ITrailPaint Paint { get; set; }

        /// <summary>
        /// The terrain edits the tool's current analysis would commit at Break ground (task 05, TE2): the complete edit
        /// set once applied (<see cref="DesignHistory.Terrain"/> plus this tool's grading and clearing), or null for none.
        /// The host submits it with the command and clears it on commit; each tool starts with none. Task 08 sets it.
        /// </summary>
        public Domain.Edits.TerrainEdits PendingTerrain { get; set; }

        /// <summary>The ground the tools shape (task 08): the terrain edit session and its earthwork; null in tests without one.</summary>
        public IEarthworkHost Earthwork { get; set; }

        /// <summary>The ground a terrain brush moves as it paints (task 15); null in tests without a mountain.</summary>
        public ILiveGround LiveGround { get; set; }

        /// <summary>
        /// The ground as edited now (task 05's heights in the package's frame), for builds that need the real terrain (task 09's
        /// lifts); null while the edit session is still starting.
        /// </summary>
        public Func<Domain.Edits.EditedHeights> EditedGround { get; set; }

        /// <summary>The terminals being placed, as their own models (task 09, LT19, LT22); null without the lift models.</summary>
        public Presentation.Lifts.TerminalGhost BottomGhost { get; set; }
        public Presentation.Lifts.TerminalGhost TopGhost { get; set; }

        /// <summary>What the terminal in hand would join (NW-14, NT16: a shared base or a transfer, and the trails it
        /// gathers), set by the game's catchment preview while a lift tool is armed; null when there's nothing to say.</summary>
        public Func<string> CatchmentNote { get; set; }

        /// <summary>The tower in hand as its own model (F4, LT38); null without the lift models.</summary>
        public Func<Presentation.Lifts.TowerGhost> TowerGhost { get; set; }

        /// <summary>Draws the lift being built or edited in 3D as it stands (F4: terminals, the towers so far, the rope, no
        /// chairs); null hides it. Called when the draft changes, never per frame.</summary>
        public Action<Lift> ShowDraftLift { get; set; }

        /// <summary>Hides a built lift while Edit lift shows its draft in its place (0 shows it again).</summary>
        public Action<ulong> HideBuiltLift { get; set; }

        /// <summary>The draft's part under the pointer (F1): a tower's index, or LiftView.PartBottom / PartTop / PartRope;
        /// int.MinValue for none.</summary>
        public Func<int> DraftPartUnderPointer { get; set; }

        /// <summary>Lights one part of the draft white (F1: the selected tower or terminal); int.MinValue for none.</summary>
        public Action<int> HighlightDraftPart { get; set; }

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

        /// <summary>Task 4.14: the stream or lake at a design point ("Twin Creek", "a stream", "a lake"), or null for dry ground;
        /// a pond's shoreline may not cross one. Null in tests without a map.</summary>
        public Func<PointMm, string> WaterUnder { get; set; }

        /// <summary>Task 4.14 (PD-05): the streams a dam line from a to b crosses, from the area's water.json (or the
        /// rasterised streams body); null in tests without a map.</summary>
        public Func<PointMm, PointMm, IReadOnlyList<StreamCrossing>> StreamsCrossing { get; set; }

        /// <summary>Task 4.14 (PD-12): metres from a design point to the nearest stream (a line from water.json, or the streams
        /// body), +∞ when none is within reach; null in tests without a map (any point may start a dam).</summary>
        public Func<PointMm, double> StreamDistance { get; set; }

        /// <summary>
        /// Task 4.14 (F3): the review's water standing at <c>level</c> metres over the mask's points, hiding the built pond or
        /// dam (by id, 0 for none) it would replace; a null mask takes it away. Unset in tests.
        /// </summary>
        public Action<WaterMask, double, ulong> PreviewWater { get; set; }

        /// <summary>Task 4.14 (F3): the area's stream lines, local metres (x east, z north), for the Dam tool to show.</summary>
        public Func<IReadOnlyList<Vector2[]>> StreamLines { get; set; }

        /// <summary>Task 4.14 (WS-01, LK-02): the lake under a design point (its WaterBodyTable id, name, estimated depth in
        /// tenths of a foot, area), or null; the Water source tool picks with it.</summary>
        public Func<PointMm, LakeUnder?> LakeAt { get; set; }

        /// <summary>
        /// Task 4.15: solves a draft snowmaking network as the game plays it (the edited ground, the sources' full pools, this
        /// hour's weather), on any thread; null until the ground is ready, and in tests without a mountain.
        /// </summary>
        public Func<SnowService> Snow { get; set; }

        /// <summary>Task 4.15 (WS-05, WS-11): the snowmaking source whose water is nearest a point within a reach (mm), or null;
        /// null in tests without a map.</summary>
        public Func<PointMm, int, SourceNear?> SourceNear { get; set; }

        /// <summary>Task 4.15: the game's last solve of the built network (the guns as set), or null.</summary>
        public Func<Domain.Snowmaking.SnowSolution> LiveSnow { get; set; }

        /// <summary>Task 4.15 (plan Q4): the game's "every gun running" solve while <see cref="SetSnowCheck"/> is on, or null.</summary>
        public Func<Domain.Snowmaking.SnowSolution> LiveSnowCheck { get; set; }

        /// <summary>Task 4.15: whether the game's solve is waiting on its latency after an edit (the result shows greyed).</summary>
        public Func<bool> SnowPending { get; set; }

        /// <summary>Task 4.15: turns the game's "every gun running" check on or off.</summary>
        public Action<bool> SetSnowCheck { get; set; }

        /// <summary>Task 4.15: lights network pieces on the map (pipes, nodes, houses, guns; empty for none) and the
        /// "Show me" flight to one point (null: stay).</summary>
        public Action<IReadOnlyList<SnowId>, PointMm?> ShowSnow { get; set; }

        /// <summary>Where confirmed earthwork is paid for (BR19): the Phase 4 economy; null until then, so nothing is charged.</summary>
        public IEarthworkCharges Charges { get; set; }

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

        /// <summary>BB19: Ctrl + wheel sizes this tool's brush (<see cref="ToolInput.BrushNotches"/>) instead of zooming.</summary>
        public void ClaimBrushWheel(bool on) => KeyBindings.BrushWheel = on;

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

        /// <summary>
        /// As <see cref="Analyse{T}"/>, for work already running (a terrain brush's fold, started when a press ended): its
        /// result reaches <paramref name="done"/> only if nothing moved on since this call.
        /// </summary>
        public void Await<T>(Task<T> work, Action<T> done)
        {
            if (_disarmed) return;
            _pending.Add(new Pending<T>(work, NextStamp(), done, this));
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
