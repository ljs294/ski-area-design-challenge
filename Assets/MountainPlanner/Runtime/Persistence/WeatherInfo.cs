using System.Collections.Generic;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// The package manifest's weather chunk (manifest v2, task 4.05; p4-04 §14). The calibration rows themselves are the binary
    /// <see cref="RowsFile"/> beside the manifest (the bytes are the rows' truth, WX-31); this block says where they came
    /// from, how they were screened and checked, and how far to trust them. Frozen with the manifest (0.3 §5): report items
    /// are generic named records, so a new check is data, not a format change.
    /// </summary>
    public sealed class WeatherInfo
    {
        public const string DefaultRowsFile = "weather.rows";

        public string RowsFile { get; set; } = DefaultRowsFile;
        public string RowsSha256 { get; set; } = "";
        public int RowsVersion { get; set; }

        /// <summary>WX-11 v1: the record window, label years named by October's year (both inclusive).</summary>
        public int FirstLabelYear { get; set; }
        public int LastLabelYear { get; set; }

        /// <summary>"Clean", "Limited" (WX-13, WX-17 v2: the player sees one line) or "Check anchor" (report only).</summary>
        public string Verdict { get; set; } = "";
        /// <summary>WX-13 and WX-17 v2: the limited flag and why (outside Daymet, a winter field pooled past the month).</summary>
        public bool Limited { get; set; }
        public List<string> Reasons { get; set; } = new List<string>();

        /// <summary>The reference pixel (WX-14): its centre and Daymet's elevation for it, the rows' zref.</summary>
        public double ReferenceLatitude { get; set; }
        public double ReferenceLongitude { get; set; }
        public double ReferenceElevationMetres { get; set; }
        /// <summary>WX-15 v2: the hours POWER's day boundary was shifted to match Daymet's observation day.</summary>
        public int ObservationLagHours { get; set; }

        /// <summary>WX-12: each source as the service described itself at fetch (never a typed copy).</summary>
        public List<WeatherSourceInfo> Sources { get; set; } = new List<WeatherSourceInfo>();
        /// <summary>The QC log: every screen's counts, and the limits it ran with.</summary>
        public List<WeatherQcEntry> Qc { get; set; } = new List<WeatherQcEntry>();
        public Dictionary<string, double> QcLimits { get; set; } = new Dictionary<string, double>();
        /// <summary>WX-07 v3, WX-10: the SNOTEL stations compared, report only.</summary>
        public List<WeatherStationInfo> Stations { get; set; } = new List<WeatherStationInfo>();
        /// <summary>Calibration checks and fit parameters (WX-31 v2's KS distances, WX-18's detection ratios, the pooling ladder).</summary>
        public List<WeatherCheckInfo> Checks { get; set; } = new List<WeatherCheckInfo>();

        /// <summary>The tool and the limits' version that built it.</summary>
        public string BuiltBy { get; set; } = "";
    }

    public sealed class WeatherSourceInfo
    {
        /// <summary>"Daymet", "NASA POWER", "SNOTEL".</summary>
        public string Name { get; set; } = "";
        public string Service { get; set; } = "";
        public string Version { get; set; } = "";
        /// <summary>The citation or data reference the service gave, filled with the access date where it asks for one.</summary>
        public string Citation { get; set; } = "";
        public string AccessedUtc { get; set; } = "";
    }

    /// <summary>One screen's result: of <see cref="Checked"/> values, <see cref="Rejected"/> were made missing.</summary>
    public sealed class WeatherQcEntry
    {
        public string Source { get; set; } = "";
        public string Test { get; set; } = "";
        public long Checked { get; set; }
        public long Rejected { get; set; }
        /// <summary>"ok", "reported" (kept, but worth a look) or "flag" (moved the verdict).</summary>
        public string Outcome { get; set; } = "ok";
        public string Note { get; set; } = "";
    }

    public sealed class WeatherStationInfo
    {
        public string Triplet { get; set; } = "";
        public string Name { get; set; } = "";
        public double DistanceMetres { get; set; }
        public double ElevationMetres { get; set; }
        /// <summary>The GHCN-Daily id when NOAA lists the station: Daymet may already have used it, so agreement is weak evidence.</summary>
        public string GhcnId { get; set; } = "";
        public int UsableWinters { get; set; }
        /// <summary>The median over usable winters of Daymet ÷ SNOTEL winter precipitation (0 when too few winters).</summary>
        public double MedianRatio { get; set; }
        public int UndercatchWinters { get; set; }
        public string Note { get; set; } = "";
    }

    public sealed class WeatherCheckInfo
    {
        public string Name { get; set; } = "";
        public double Value { get; set; }
        public double Low { get; set; }
        public double High { get; set; }
        /// <summary>"pass", "fail", "report" (no gate).</summary>
        public string Result { get; set; } = "report";
        public string Note { get; set; } = "";
    }
}
