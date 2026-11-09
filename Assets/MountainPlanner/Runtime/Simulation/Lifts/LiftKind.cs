namespace MountainPlanner.Simulation
{
    /// <summary>
    /// How a lift runs (LO-01, LO-06, LO-10, LO-11): which hold gust applies, whether its riders are hidden (compressed) or
    /// on the snow, and whether it can download. Derived from the catalog by the submitter (SI-21); never saved.
    /// </summary>
    public enum LiftKind : byte
    {
        FixedGripChair = 1,
        DetachableChair = 2,
        Gondola = 3,
        /// <summary>LO-08: a jig-back; until its models exist it runs on its catalog capacity like any rope (no dead code).</summary>
        Tram = 4,
        /// <summary>T-bars and J-bars: the rider is on the snow for the whole tow (LO-06).</summary>
        Tow = 5,
        Carpet = 6,
    }

    /// <summary>A lift's operating status as the snapshot says it (the HUD's Status word, <c>UI.OperatingStatus</c>).</summary>
    public enum LiftOpStatus : byte
    {
        /// <summary>Not built: a plan.</summary>
        Planned = 0,
        /// <summary>Built and not running today, or past its hours: out of season, Peak days only on a weekday, no way down.</summary>
        Closed = 1,
        /// <summary>Running today, before its opening.</summary>
        Scheduled = 2,
        /// <summary>Loading skiers.</summary>
        Open = 3,
        /// <summary>LO-11: stopped for wind, expected back the same day.</summary>
        OnHold = 4,
        /// <summary>LO-10: lost its way down mid-day; closed to boarding, the chairs still carry riders down.</summary>
        Downloading = 5,
    }

    public static class LiftKinds
    {
        /// <summary>LO-06: a chair, gondola or tram rider is hidden for the compressed ride; a tow or carpet rider is on the snow.</summary>
        public static bool Compressed(LiftKind k) => k != LiftKind.Tow && k != LiftKind.Carpet;

        /// <summary>LO-10: chairs and gondolas download; tows and carpets just close.</summary>
        public static bool CanDownload(LiftKind k) => k == LiftKind.FixedGripChair || k == LiftKind.DetachableChair || k == LiftKind.Gondola || k == LiftKind.Tram;

        /// <summary>LO-11: the index into <see cref="Pacing.HoldGustMph"/> and <see cref="Pacing.HoldGustPerSeatTenths"/>, or −1 for a kind that never holds.</summary>
        public static int HoldIndex(LiftKind k) => k switch
        {
            LiftKind.FixedGripChair => 0,
            LiftKind.DetachableChair => 1,
            LiftKind.Gondola => 2,
            LiftKind.Tram => 3,
            _ => -1,
        };
    }
}
