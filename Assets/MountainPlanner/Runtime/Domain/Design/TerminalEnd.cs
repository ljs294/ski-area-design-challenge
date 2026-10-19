namespace MountainPlanner.Domain.Design
{
    /// <summary>TW-16 v3: what a terminal's own sheave row does to the rope where it leaves toward the line.</summary>
    public enum TerminalRow : byte
    {
        /// <summary>The rope runs under the row, which turns it up from level as it leaves (the Sessellift return's arc).</summary>
        HoldDown,
        /// <summary>The rope runs over the row, which only carries it: it may never pull up (the Sessellift drive's four sheaves).</summary>
        Support,
    }

    /// <summary>
    /// One end of a lift (TW-16 v3; owner's ruling at task 07: a lift's terminals face where its bullwheels are, and no rule
    /// assumes which end holds the drive): which terminal model stands there and what it needs of the rope, from the
    /// catalog (LC-01) and the model's sockets.
    /// - Angles are radians in the end's own frame: the rope leaving the station toward the line, positive climbing away
    ///   from the station.
    /// - Lengths are metres from the end's bullwheel, positive toward the line; heights are above the terminal's origin
    ///   (its load or unload level, where it stands on its pad).
    /// </summary>
    public sealed class TerminalEnd
    {
        /// <summary>The terminal's model (asset id).</summary>
        public string Model { get; }
        public TerminalRow Row { get; }
        /// <summary>The steepest and gentlest rope the row can take, in the end's frame.</summary>
        public double AngleMin { get; }
        public double AngleMax { get; }
        /// <summary>What placement aims for: 90% of a hold-down row's limit; level for a support row.</summary>
        public double Aim { get; }
        /// <summary>The footprint (CL-09, LF-12): behind the bullwheel (away from the line), in front, and either side.</summary>
        public double Behind { get; }
        public double Ahead { get; }
        public double HalfWidth { get; }
        /// <summary>Where the rope leaves the row (the model's rope_*_out socket).</summary>
        public double ExitAlong { get; }
        public double ExitRise { get; }
        /// <summary>Where the terminal's origin lies from its bullwheel (its rope_*_bw sockets).</summary>
        public double OriginAlong { get; }
        /// <summary>How many sheaves the row turns the rope over, per rope side (TW-05 at the row; task 09): the Sessellift
        /// return's 8, its drive's 4, the Monta drive's 12, the Monta return's single deflection sheave.</summary>
        public int Sheaves { get; }

        public TerminalEnd(string model, TerminalRow row, double angleMin, double angleMax, double aim,
                           double behind, double ahead, double halfWidth, double exitAlong, double exitRise, double originAlong, int sheaves)
        {
            Sheaves = sheaves;
            Model = model;
            Row = row;
            AngleMin = angleMin;
            AngleMax = angleMax;
            Aim = aim;
            Behind = behind;
            Ahead = ahead;
            HalfWidth = halfWidth;
            ExitAlong = exitAlong;
            ExitRise = exitRise;
            OriginAlong = originAlong;
        }

        /// <summary>The same end with the rope exit measured on its model.</summary>
        public TerminalEnd WithExit(double along, double rise) =>
            new TerminalEnd(Model, Row, AngleMin, AngleMax, Aim, Behind, Ahead, HalfWidth, along, rise, OriginAlong, Sheaves);

        /// <summary>The same end with its footprint measured on its model.</summary>
        public TerminalEnd WithFootprint(double behind, double ahead, double halfWidth) =>
            new TerminalEnd(Model, Row, AngleMin, AngleMax, Aim, behind, ahead, halfWidth, ExitAlong, ExitRise, OriginAlong, Sheaves);

        /// <summary>The same end with its origin measured on its model.</summary>
        public TerminalEnd WithOrigin(double along) =>
            new TerminalEnd(Model, Row, AngleMin, AngleMax, Aim, Behind, Ahead, HalfWidth, ExitAlong, ExitRise, along, Sheaves);

        /// <summary>Whether a rope at <paramref name="angle"/> (the end's frame) suits the row.</summary>
        public bool Takes(double angle) => angle >= AngleMin - 1e-9 && angle <= AngleMax + 1e-9;
    }
}
