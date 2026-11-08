namespace MountainPlanner.UI
{
    /// <summary>
    /// Whether a trail, lift or (later) any structure is running (4.21 demo findings F1, F4; owner 2026-11-08): the one word
    /// the lists and cards show in their Status column. A plan reads Planned; a built object reads Closed until the
    /// simulation opens it. 4.11 (lift operations, LO-01…13) and 4.07/4.17 (snow and trail openings, SN-20 v2) feed the
    /// real value; until then the app fills <see cref="Placeholder"/>.
    /// </summary>
    public enum OperatingStatus
    {
        Planned,
        Closed,
        Open,
        /// <summary>Stopped for now (wind, a fault) and expected back the same day.</summary>
        OnHold,
        /// <summary>Closed, but opening soon.</summary>
        Scheduled,
    }

    public static class OperatingStatusWords
    {
        /// <summary>Every status, in the order a Status filter lists them.</summary>
        public static readonly OperatingStatus[] All =
            { OperatingStatus.Open, OperatingStatus.Scheduled, OperatingStatus.OnHold, OperatingStatus.Closed, OperatingStatus.Planned };

        public static string Word(OperatingStatus s) => s switch
        {
            OperatingStatus.Open => "Open",
            OperatingStatus.Closed => "Closed",
            OperatingStatus.OnHold => "On hold",
            OperatingStatus.Scheduled => "Scheduled",
            _ => "Planned",
        };

        /// <summary>The value until the simulation runs things: a plan is Planned, a built object Closed.</summary>
        public static OperatingStatus Placeholder(bool built) => built ? OperatingStatus.Closed : OperatingStatus.Planned;

        /// <summary>A list's Status column filter: every word, keyed by its enum name.</summary>
        public static (string, string)[] Bins()
        {
            var bins = new (string, string)[All.Length];
            for (int i = 0; i < All.Length; i++) bins[i] = (All[i].ToString(), Word(All[i]));
            return bins;
        }
    }
}
