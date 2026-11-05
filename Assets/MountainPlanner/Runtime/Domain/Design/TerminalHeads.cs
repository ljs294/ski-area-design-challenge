using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// LD9 (owner, 2026-11-02, drive at either end): the ends a fixed-grip terminal family can stand at, one per head its
    /// models carry. A family is a model prefix (<c>sessellift_fgq4_</c>, <c>monta_fg3_</c>, <c>sessellift_stern2_</c>):
    /// - its return's 8-sheave hold-down head at the bottom (a Monta's <c>return_bottom</c> station);
    /// - its drive's hold-down head at the bottom (the Sessellift's and the Stern's);
    /// - its drive's or its return's top heads, 4, 6 and 8 sheaves a side (the Stern: its drive at the bottom, its return's top
    ///   heads, no other ends).
    /// The footprints, exits and origins are the models' as the view measures them (<c>LiftTypes.Measured</c>), copied here so
    /// the engine-free build stands each end where the view draws it.
    /// </summary>
    public static class TerminalHeads
    {
        public sealed class Set
        {
            /// <summary>The return's hold-down at the bottom (drive at the top).</summary>
            public TerminalEnd ReturnBottom;
            /// <summary>The drive's hold-down at the bottom (drive at the bottom); null for a Monta, whose drive stands there as
            /// its own station.</summary>
            public TerminalEnd DriveBottom;
            /// <summary>The drive's top rows (drive at the top), 4, 6 and 8 sheaves.</summary>
            public TerminalEnd[] DriveTop;
            /// <summary>The return's top rows (drive at the bottom); null for a Monta, whose return has one sheave.</summary>
            public TerminalEnd[] ReturnTop;
        }

        /// <summary>One end as measured on its model and head: the footprint behind and ahead of the bullwheel and either side,
        /// the rope's exit along and above the origin, and the origin's distance from the bullwheel (metres).</summary>
        readonly struct Measure
        {
            public readonly double Behind, Ahead, HalfWidth, ExitAlong, ExitRise, OriginAlong;
            public Measure(double behind, double ahead, double halfWidth, double exitAlong, double exitRise, double originAlong)
            {
                Behind = behind; Ahead = ahead; HalfWidth = halfWidth; ExitAlong = exitAlong; ExitRise = exitRise; OriginAlong = originAlong;
            }
        }

        static readonly (string Key, int Sheaves)[] TopKeys = { ("top", 4), ("top6", 6), ("top8", 8) };

        // The Sessellift FGQ-4's ends as LiftType carried them before LD9 (task 07's measurements), for a model not in the table.
        static readonly Measure HoldDownQuad = new Measure(2.332, 8.059, 3.022, 7.880, 3.543, 1.990);
        static readonly Measure SupportQuad = new Measure(2.530, 7.218, 3.328, 7.021, 3.039, 3.230);

        /// <summary>The models' measurements by "model|head" (a Monta's <c>return_bottom</c> has no heads: "model|"), as
        /// <c>LiftView.RopeExit</c>, <c>TerminalExtent</c> and <c>OriginAlong</c> measure them on that head; the EditMode test
        /// <c>TerminalHeadsTests</c> holds them to the models within 2 cm and prints the table again when a model changes.</summary>
        static readonly Dictionary<string, Measure> Measured = new Dictionary<string, Measure>(StringComparer.Ordinal)
        {
            ["monta_fg2_drive|top"] = new Measure(5.045, 8.390, 4.295, 8.179, 3.011, 0.000),
            ["monta_fg2_drive|top6"] = new Measure(5.045, 8.978, 4.295, 8.770, 2.954, 0.000),
            ["monta_fg2_drive|top8"] = new Measure(5.045, 9.450, 4.295, 9.280, 2.407, 0.000),
            ["monta_fg2_return_bottom|"] = new Measure(1.762, 8.267, 1.828, 8.089, 3.543, 0.000),
            ["monta_fg3_drive|top"] = new Measure(5.045, 8.390, 4.885, 8.179, 3.011, 0.000),
            ["monta_fg3_drive|top6"] = new Measure(5.045, 8.978, 4.885, 8.770, 2.954, 0.000),
            ["monta_fg3_drive|top8"] = new Measure(5.045, 9.450, 4.885, 9.280, 2.407, 0.000),
            ["monta_fg3_return_bottom|"] = new Measure(1.922, 8.267, 1.981, 8.089, 3.543, 0.000),
            ["monta_fg4_drive|top"] = new Measure(5.045, 8.390, 5.475, 8.179, 3.011, 0.000),
            ["monta_fg4_drive|top6"] = new Measure(5.045, 8.978, 5.475, 8.770, 2.954, 0.000),
            ["monta_fg4_drive|top8"] = new Measure(5.045, 9.450, 5.475, 9.280, 2.407, 0.000),
            ["monta_fg4_return_bottom|"] = new Measure(2.165, 8.267, 2.212, 8.089, 3.543, 0.000),
            ["sessellift_fgd2_drive|bottom"] = new Measure(2.147, 8.472, 3.328, 8.294, 3.543, 3.230),
            ["sessellift_fgd2_drive|top"] = new Measure(2.147, 7.218, 3.328, 7.021, 3.039, 3.230),
            ["sessellift_fgd2_drive|top6"] = new Measure(2.147, 7.895, 3.328, 7.688, 2.954, 3.230),
            ["sessellift_fgd2_drive|top8"] = new Measure(2.147, 8.367, 3.328, 8.198, 2.407, 3.230),
            ["sessellift_fgd2_return|bottom"] = new Measure(1.949, 8.059, 2.639, 7.880, 3.543, 1.990),
            ["sessellift_fgd2_return|top"] = new Measure(1.949, 6.927, 2.639, 6.730, 3.039, 1.990),
            ["sessellift_fgd2_return|top6"] = new Measure(1.949, 7.604, 2.639, 7.397, 2.954, 1.990),
            ["sessellift_fgd2_return|top8"] = new Measure(1.949, 8.076, 2.639, 7.907, 2.407, 1.990),
            ["sessellift_fgq4_drive|bottom"] = new Measure(2.530, 8.472, 3.328, 8.294, 3.543, 3.230),
            ["sessellift_fgq4_drive|top"] = new Measure(2.530, 7.218, 3.328, 7.021, 3.039, 3.230),
            ["sessellift_fgq4_drive|top6"] = new Measure(2.530, 7.895, 3.328, 7.688, 2.954, 3.230),
            ["sessellift_fgq4_drive|top8"] = new Measure(2.530, 8.367, 3.328, 8.198, 2.407, 3.230),
            ["sessellift_fgq4_return|bottom"] = new Measure(2.332, 8.059, 3.022, 7.880, 3.543, 1.990),
            ["sessellift_fgq4_return|top"] = new Measure(2.332, 6.927, 3.022, 6.730, 3.039, 1.990),
            ["sessellift_fgq4_return|top6"] = new Measure(2.332, 7.604, 3.022, 7.397, 2.954, 1.990),
            ["sessellift_fgq4_return|top8"] = new Measure(2.332, 8.076, 3.022, 7.907, 2.407, 1.990),
            ["sessellift_fgt3_drive|bottom"] = new Measure(2.299, 8.472, 3.328, 8.294, 3.543, 3.230),
            ["sessellift_fgt3_drive|top"] = new Measure(2.299, 7.218, 3.328, 7.021, 3.039, 3.230),
            ["sessellift_fgt3_drive|top6"] = new Measure(2.299, 7.895, 3.328, 7.688, 2.954, 3.230),
            ["sessellift_fgt3_drive|top8"] = new Measure(2.299, 8.367, 3.328, 8.198, 2.407, 3.230),
            ["sessellift_fgt3_return|bottom"] = new Measure(2.101, 8.059, 2.791, 7.880, 3.543, 1.990),
            ["sessellift_fgt3_return|top"] = new Measure(2.101, 6.927, 2.791, 6.730, 3.039, 1.990),
            ["sessellift_fgt3_return|top6"] = new Measure(2.101, 7.604, 2.791, 7.397, 2.954, 1.990),
            ["sessellift_fgt3_return|top8"] = new Measure(2.101, 8.076, 2.791, 7.907, 2.407, 1.990),
            ["sessellift_stern2_drive|bottom"] = new Measure(9.269, 8.455, 5.344, 8.276, 3.543, 0.000),
            ["sessellift_stern2_return|top"] = new Measure(1.910, 6.561, 1.976, 6.364, 3.039, 0.000),
            ["sessellift_stern2_return|top6"] = new Measure(1.910, 7.239, 1.976, 7.031, 2.954, 0.000),
            ["sessellift_stern2_return|top8"] = new Measure(1.910, 7.711, 1.976, 7.541, 2.407, 0.000),
            ["sessellift_stern3_drive|bottom"] = new Measure(9.269, 8.455, 5.344, 8.276, 3.543, 0.000),
            ["sessellift_stern3_return|top"] = new Measure(2.062, 6.561, 2.128, 6.364, 3.039, 0.000),
            ["sessellift_stern3_return|top6"] = new Measure(2.062, 7.239, 2.128, 7.031, 2.954, 0.000),
            ["sessellift_stern3_return|top8"] = new Measure(2.062, 7.711, 2.128, 7.541, 2.407, 0.000),
            ["sessellift_stern4_drive|bottom"] = new Measure(9.269, 8.455, 5.560, 8.276, 3.543, 0.000),
            ["sessellift_stern4_return|top"] = new Measure(2.293, 6.561, 2.359, 6.364, 3.039, 0.000),
            ["sessellift_stern4_return|top6"] = new Measure(2.293, 7.239, 2.359, 7.031, 2.954, 0.000),
            ["sessellift_stern4_return|top8"] = new Measure(2.293, 7.711, 2.359, 7.541, 2.407, 0.000),
        };

        static readonly Dictionary<string, Set> Cache = new Dictionary<string, Set>(StringComparer.Ordinal);

        public static Set Of(string family)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(family, out var set)) return set;
                bool monta = family.StartsWith("monta_", StringComparison.Ordinal);
                // The Stern drives at the bottom only: its drive's hold-down there and its return's top heads.
                bool stern = family.StartsWith("sessellift_stern", StringComparison.Ordinal);
                set = new Set
                {
                    ReturnBottom = stern ? null : HoldDown(monta ? family + "return_bottom" : family + "return", monta ? null : "bottom", HoldDownQuad),
                    DriveBottom = monta ? null : HoldDown(family + "drive", "bottom", HoldDownQuad),
                    DriveTop = stern ? null : Tops(family + "drive"),
                    ReturnTop = monta ? null : Tops(family + "return"),
                };
                Cache[family] = set;
                return set;
            }
        }

        static TerminalEnd HoldDown(string model, string head, Measure fallback)
        {
            var m = Find(model, head, fallback);
            return new TerminalEnd(model, TerminalRow.HoldDown, LiftType.ReturnExitMin, LiftType.ReturnExitMax, 0.9 * LiftType.ReturnExitMax,
                                   m.Behind, m.Ahead, m.HalfWidth, m.ExitAlong, m.ExitRise, m.OriginAlong, 8, head: head);
        }

        static TerminalEnd[] Tops(string model)
        {
            var ends = new TerminalEnd[TopKeys.Length];
            for (int i = 0; i < ends.Length; i++)
            {
                var (key, sheaves) = TopKeys[i];
                var m = Find(model, key, SupportQuad);
                ends[i] = new TerminalEnd(model, TerminalRow.Support, -sheaves * LiftRope.BendPerSheave, 0, 0, m.Behind, m.Ahead, m.HalfWidth,
                                          m.ExitAlong, m.ExitRise, m.OriginAlong, sheaves, head: key);
            }
            return ends;
        }

        static Measure Find(string model, string head, Measure fallback) =>
            Measured.TryGetValue(model + "|" + head, out var m) ? m : fallback;
    }
}
