using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SI-05 v2 and SI-19: the columns and fields a system reads and writes. A system declares them; a test checks that
    /// every column a row reads is written by an earlier row or is an input (<see cref="SystemsTable.Inputs"/>).
    /// </summary>
    [Flags]
    public enum SimColumns
    {
        None = 0,
        // Inputs: from commands and the clock
        Clock = 1 << 0,
        Commands = 1 << 1,
        Design = 1 << 2,
        // The season and the day
        Season = 1 << 3,
        Hours = 1 << 4,
        // The face-hour step's fields (WX-06, WX-08: weather first)
        Weather = 1 << 5,
        Snow = 1 << 6,
        Lakes = 1 << 7,
        TrailReady = 1 << 8,
        // The board (SI-19)
        LiftOpen = 1 << 9,
        LiftHold = 1 << 10,
        // Per tick
        TickInterval = 1 << 11,
        LiftRun = 1 << 12,
        GunSchedule = 1 << 13,
        GunOutput = 1 << 14,
        Skiers = 1 << 15,
        Wear = 1 << 16,
        Events = 1 << 17,
        Ledgers = 1 << 18,
        Snapshot = 1 << 19,
    }

    /// <summary>One row of the systems table: a name, what it reads and writes, and its work for one step.</summary>
    public abstract class SimSystem
    {
        public abstract string Name { get; }
        public abstract SimColumns Reads { get; }
        public abstract SimColumns Writes { get; }
        internal abstract void Run(SimWorld w);
        public override string ToString() => Name;
    }

    /// <summary>
    /// SI-05 v2: the systems table in p4-02's order. The tick runs the table; a new system is a new row (and a test that its
    /// reads come after their writes), never an edit to the loop. Rows for later tasks are here and empty.
    /// </summary>
    public static class SystemsTable
    {
        /// <summary>What no row writes: the clock (the host advances it), commands and the design they bring.</summary>
        public const SimColumns Inputs = SimColumns.Clock | SimColumns.Commands;

        public static readonly SimSystem[] Rows =
        {
            new CommandsSystem(),
            new HourStepSystem(),
            new TickIntervalSystem(),
            new LiftsSystem(),
            new Empty("Gun schedules (4.16)", SimColumns.Weather | SimColumns.Hours, SimColumns.GunSchedule),
            new Empty("Gun output (4.15)", SimColumns.GunSchedule | SimColumns.Weather | SimColumns.TickInterval, SimColumns.GunOutput),
            new Empty("Skiers (4.12)", SimColumns.LiftOpen | SimColumns.LiftRun | SimColumns.TrailReady | SimColumns.TickInterval, SimColumns.Skiers),
            new Empty("Wear (4.07)", SimColumns.Skiers | SimColumns.TickInterval, SimColumns.Wear),
            new Empty("Events", SimColumns.Clock, SimColumns.Events),
            new LedgersSystem(),
            new PublishSystem(),
        };

        sealed class Empty : SimSystem
        {
            readonly string _name;
            readonly SimColumns _reads, _writes;
            public Empty(string name, SimColumns reads, SimColumns writes) { _name = name; _reads = reads; _writes = writes; }
            public override string Name => _name;
            public override SimColumns Reads => _reads;
            public override SimColumns Writes => _writes;
            internal override void Run(SimWorld w) { }
        }

        sealed class CommandsSystem : SimSystem
        {
            public override string Name => "Commands";
            public override SimColumns Reads => SimColumns.Commands | SimColumns.Clock;
            public override SimColumns Writes => SimColumns.Design | SimColumns.Season | SimColumns.Events;
            internal override void Run(SimWorld w) => w.RunCommands();
        }

        /// <summary>The face-hour step and the day's boundaries: midnight, the opening, each hour (WX-08: weather first).</summary>
        sealed class HourStepSystem : SimSystem
        {
            public override string Name => "Face-hour step";
            public override SimColumns Reads => SimColumns.Clock | SimColumns.Design | SimColumns.Season;
            public override SimColumns Writes => SimColumns.Season | SimColumns.Hours | SimColumns.Weather | SimColumns.Snow | SimColumns.Lakes
                | SimColumns.TrailReady | SimColumns.LiftOpen | SimColumns.LiftHold | SimColumns.Events;
            internal override void Run(SimWorld w) => w.RunHourStep();
        }

        /// <summary>The step's face interval: the day or night rate, cut at the next boundary.</summary>
        sealed class TickIntervalSystem : SimSystem
        {
            public override string Name => "Tick interval";
            public override SimColumns Reads => SimColumns.Clock | SimColumns.Season | SimColumns.Hours | SimColumns.LiftOpen;
            public override SimColumns Writes => SimColumns.TickInterval;
            internal override void Run(SimWorld w) => w.RunTickInterval();
        }

        /// <summary>Lift holds and schedules (4.11 adds holds, lines and rides): which ropes run, and their travel (RP-07 v2).</summary>
        sealed class LiftsSystem : SimSystem
        {
            public override string Name => "Lift holds and schedules";
            public override SimColumns Reads => SimColumns.LiftOpen | SimColumns.LiftHold | SimColumns.Hours | SimColumns.Season | SimColumns.TickInterval;
            public override SimColumns Writes => SimColumns.LiftRun | SimColumns.Events;
            internal override void Run(SimWorld w) => w.RunLifts();
        }

        sealed class LedgersSystem : SimSystem
        {
            public override string Name => "Ledgers";
            public override SimColumns Reads => SimColumns.LiftRun | SimColumns.Season | SimColumns.Hours | SimColumns.TickInterval | SimColumns.Events;
            public override SimColumns Writes => SimColumns.Ledgers | SimColumns.Events;
            internal override void Run(SimWorld w) => w.RunLedgers();
        }

        sealed class PublishSystem : SimSystem
        {
            public override string Name => "Snapshot publish";
            public override SimColumns Reads => SimColumns.Ledgers | SimColumns.LiftRun | SimColumns.LiftOpen | SimColumns.Season | SimColumns.Events;
            public override SimColumns Writes => SimColumns.Snapshot;
            internal override void Run(SimWorld w) => w.RunPublish();
        }
    }
}
