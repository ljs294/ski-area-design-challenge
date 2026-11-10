#nullable enable
using System.Collections.Generic;
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition.Weather.Qc
{
    /// <summary>The site's QC verdict (QC plan): Clean, Limited (one line to the player), Check anchor (the owner's report only).</summary>
    public enum WeatherVerdict { Clean, Limited, CheckAnchor }

    /// <summary>
    /// What the screens did (QC plan, principle 4): every test's counts, in order, plus the verdict they moved. Written into the
    /// manifest's weather chunk (<see cref="WeatherInfo.Qc"/>) with the limits it ran with.
    /// </summary>
    public sealed class WeatherQcLog
    {
        public readonly List<WeatherQcEntry> Entries = new List<WeatherQcEntry>();
        public readonly List<string> LimitedReasons = new List<string>();
        public readonly List<string> AnchorReasons = new List<string>();

        public WeatherVerdict Verdict => AnchorReasons.Count > 0 ? WeatherVerdict.CheckAnchor
                                       : LimitedReasons.Count > 0 ? WeatherVerdict.Limited : WeatherVerdict.Clean;

        public static string Name(WeatherVerdict v) => v == WeatherVerdict.CheckAnchor ? "Check anchor" : v.ToString();

        public WeatherQcEntry Add(string source, string test, long checkedCount, long rejected, string note = "", string outcome = "ok")
        {
            var e = new WeatherQcEntry { Source = source, Test = test, Checked = checkedCount, Rejected = rejected, Note = note, Outcome = outcome };
            Entries.Add(e);
            return e;
        }

        public void Report(string source, string test, string note) => Add(source, test, 0, 0, note, "reported");

        public void Flag(string source, string test, string note)
        {
            Add(source, test, 0, 0, note, "flag");
            AnchorReasons.Add(note);
        }

        public void Limit(string reason) => LimitedReasons.Add(reason);

        public long Rejected(string source, string test)
        {
            long n = 0;
            foreach (var e in Entries) if (e.Source == source && e.Test == test) n += e.Rejected;
            return n;
        }

        /// <summary>The limits as recorded in the package.</summary>
        public static Dictionary<string, double> Limits() => new Dictionary<string, double>
        {
            ["Version"] = WeatherQcLimits.Version,
            ["GeoreferenceMetres"] = WeatherQcLimits.GeoreferenceMetres,
            ["LatticeResidualSigma"] = WeatherQcLimits.LatticeResidualSigma,
            ["OutlierWinterMads"] = WeatherQcLimits.OutlierWinterMads,
            ["PowerFlatlineHours"] = WeatherQcLimits.PowerFlatlineHours,
            ["PowerMinHoursForShape"] = WeatherQcLimits.PowerMinHoursForShape,
            ["PowerDaymetMinCorrelation"] = WeatherQcLimits.PowerDaymetMinCorrelation,
            ["SnotelResetIn"] = WeatherQcLimits.SnotelResetIn,
            ["SnotelSpikeIn"] = WeatherQcLimits.SnotelSpikeIn,
            ["SnotelSweJumpIn"] = WeatherQcLimits.SnotelSweJumpIn,
            ["SnotelSweDropIn"] = WeatherQcLimits.SnotelSweDropIn,
            ["SnotelStuckDays"] = WeatherQcLimits.SnotelStuckDays,
            ["SnotelUndercatchShare"] = WeatherQcLimits.SnotelUndercatchShare,
            ["SnotelUsableDayShare"] = WeatherQcLimits.SnotelUsableDayShare,
            ["SnotelMinWinters"] = WeatherQcLimits.SnotelMinWinters,
            ["SnotelRatioLow"] = WeatherQcLimits.SnotelRatioLow,
            ["SnotelRatioHigh"] = WeatherQcLimits.SnotelRatioHigh,
        };
    }
}
