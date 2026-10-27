using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>The tool keys pressed this frame (P3-06). The host reads <c>KeyBindings</c>; tools never touch the keyboard.</summary>
    [Flags]
    public enum ToolKeys
    {
        None = 0,
        Next = 1,
        BreakGround = 2,
        BrushNarrower = 4,
        BrushWider = 8,
        UndoStroke = 16,
        TowerUp = 32,
        TowerDown = 64,
        Delete = 128,
        // P3-09 (LT19, LT21): the terminal in hand turns and changes level.
        TurnLeft = 256,
        TurnRight = 512,
        LevelUp = 1024,
        LevelDown = 2048,
        // P3-09 (LF-04): the tower in hand slides down or up the line.
        SlideBack = 4096,
        SlideOn = 8192,
        AddTower = 16384,
        // P3-09 (LT44): the selected tower's kind turns round.
        TowerKind = 32768,
        // P3-15 (TB-02, TB-07): a brush's strength (with Shift, a Slope's grade) and its target.
        BrushWeaker = 65536,
        BrushStronger = 131072,
        TargetUp = 262144,
        TargetDown = 524288,
    }

    /// <summary>
    /// One frame of input for the armed tool, filled by the host (P3-06): the pointer's ground point (already snapped
    /// when the tool asked for snapping), the left button, and the tool keys. A struct, so ticking allocates nothing.
    /// </summary>
    public struct ToolInput
    {
        /// <summary>The pointer hits the terrain (not over a panel).</summary>
        public bool HasGround;
        /// <summary>Where, in the local frame (metres: x east, y up, z north).</summary>
        public Vector3 Ground;
        /// <summary>The same point in design millimetres (the local frame, SH-03; see <see cref="DesignFrame"/>).</summary>
        public PointMm Point;
        /// <summary>Inside the area's core box (SH-06): the only place a tool may put anything.</summary>
        public bool InCore;
        /// <summary>How many metres of ground one screen pixel covers at the pointer (0: unknown); sets the snap reach.</summary>
        public float MetresPerPixel;
        /// <summary>The pointer in screen pixels (origin bottom-left), and whether it's over a HUD panel.</summary>
        public Vector2 Screen;
        public bool OverPanel;
        /// <summary>The left button went down on the mountain this frame; is held since a press on the mountain; came up.</summary>
        public bool Click, Drag, Release;
        /// <summary>The same for the right button, for a tool that owns it (<see cref="IOwnsRightButton"/>, task 15's brushes).</summary>
        public bool RightClick, RightDrag, RightRelease;
        /// <summary>Seconds since the last tick (unscaled): a brush lays its dabs by time (TB-03).</summary>
        public float DeltaSeconds;
        /// <summary>The Erase key is held (Shift by default: Shift-drag erases, TF-02).</summary>
        public bool Erase;
        public ToolKeys Keys;
        /// <summary>Shift is held: a turn key's bigger step (P3-09).</summary>
        public bool Shift;
        /// <summary>Shift + wheel notches this frame while a terminal turn is claimed (+ turns left), else 0 (LT21).</summary>
        public float TurnNotches;

        public bool Pressed(ToolKeys key) => (Keys & key) != 0;
    }

    /// <summary>A tool that picks built lifts with the pointer (Edit and Remove lift, task 09): the viewer lights the lift under
    /// the pointer while it's armed, as it does with no tool.</summary>
    public interface IPicksLifts { }

    /// <summary>A tool that owns the right mouse button while it's armed (task 15's Terrain brushes, owner BR4): the camera
    /// stops panning on right-drag until it closes.</summary>
    public interface IOwnsRightButton { }

    /// <summary>
    /// A tool whose unconfirmed work can carry on in the next tool chosen (task 15's Terrain brushes share one session,
    /// owner BR15). The host asks before disarming; a tool that hands over is disarmed without putting its work back.
    /// </summary>
    public interface ICarriesWork
    {
        /// <summary>Hands this tool's pending work to <paramref name="next"/> (not yet armed); false when it can't take it.</summary>
        bool CarryTo(IDesignTool next);
    }

    /// <summary>What Esc did (CF-07): backed out a step, or the tool is at its first step and would close.</summary>
    public enum ToolBackOut { Stepped, Close }

    /// <summary>
    /// A construction tool (P3-06): New lift, New trail, and from tasks 08–10 every other Toolbox tool. The host arms one
    /// at a time (CF-01), ticks it each frame with <see cref="ToolInput"/>, routes Esc (CF-07), and on Break ground or
    /// Save plan asks it for one command, which it submits to <see cref="DesignHistory"/> (CF-02, CF-05, CF-06).
    /// A tool draws through <see cref="ToolContext.Preview"/> and its window through <see cref="ToolContext.Window"/>;
    /// it never changes the design itself ("snapshots out, commands in").
    /// </summary>
    public interface IDesignTool
    {
        /// <summary>The tray tile's id ("new-lift"), the mockup's data-ui name.</summary>
        string Id { get; }

        /// <summary>The window's title ("New lift").</summary>
        string Name { get; }

        /// <summary>Anything drawn that Esc or ✕ would throw away: they ask first (CF-06, CF-07).</summary>
        bool HasWork { get; }

        /// <summary>Starts the tool: builds its window, claims its keys. Called once per arming.</summary>
        void Arm(ToolContext context);

        /// <summary>Once a frame while armed. Must allocate nothing (CF-09).</summary>
        void Tick(in ToolInput input);

        /// <summary>Esc: back one step, or <see cref="ToolBackOut.Close"/> at the first step.</summary>
        ToolBackOut Escape();

        /// <summary>The command for Break ground (<see cref="WorkStage.Built"/>) or Save plan (<see cref="WorkStage.Plan"/>), or null if not ready.</summary>
        DesignCommand Commit(WorkStage stage, long baseRevision);

        /// <summary>The design accepted the command: start over at step 1 for the next one (P3-06 Q4).</summary>
        void Committed(CommandResult result);

        /// <summary>Ends the tool. The host calls it exactly once per arming and then clears the preview, window and key claims.</summary>
        void Disarm();
    }
}
