using System;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>The operator hut a terminal ships with (task 4.50 slice 2, owner 2026-11-25: always part of the terminal).</summary>
    public enum HutKind : byte { None, Large, Small, Mini, Tow }

    /// <summary>The unload ramp's three models, by chair width (owner 2026-11-24: one ramp for every lift, three widths).</summary>
    public enum RampWidth : byte { Quad, Triple, Double }

    /// <summary>What one end of a lift ships with: its hut and, at a fixed grip's top (drive or return), the stop gate, the operator's
    /// pedestal and the unload ramp's model (shown only when the top stands <see cref="TerminalExtras.RampRaiseMetres"/>
    /// or more over the ground).</summary>
    public readonly struct EndExtras
    {
        public readonly HutKind Hut;
        /// <summary>The hut's model id; null for none.</summary>
        public readonly string HutModel;
        /// <summary>A fixed grip's top: the stop gate, the pedestal and (raised 5 ft or more) the ramp.</summary>
        public readonly bool UnloadEnd;
        public readonly RampWidth Width;
        /// <summary>The maker style of the top terminal ("sessellift", "stern", "monta").</summary>
        public readonly string Maker;
        /// <summary>Whether the top terminal is the lift's drive (owner 2026-11-25: a top drive gets the ramp too).</summary>
        public readonly bool Drive;

        public EndExtras(HutKind hut, bool unloadEnd, RampWidth width, string maker, bool drive = false)
        {
            Hut = hut;
            HutModel = TerminalExtras.HutModelOf(hut);
            UnloadEnd = unloadEnd;
            Width = width;
            Maker = maker;
            Drive = drive;
        }

        /// <summary>The terminals table's row (unloadramp.json): the maker, "_drive" for a top drive ("sessellift_drive").</summary>
        public string Style => Maker == null ? null : Drive ? Maker + "_drive" : Maker;
        /// <summary>The Monta drive's own wider ramp (its A-frame's feet stand through the deck; the hut outboard of them).</summary>
        public bool Wide => UnloadEnd && Style == "monta_drive";
        public string RampModel => UnloadEnd ? TerminalExtras.RampModelOf(Width) + (Wide ? "_wide" : "") : null;
        public string GateModel => UnloadEnd ? "bldg_stop_gate_" + Style : null;
        public string PedestalModel => UnloadEnd ? TerminalExtras.Pedestal : null;
    }

    /// <summary>
    /// Task 4.50 phase 6, slice 2 (owner 2026-11-25): every lift terminal ships with its operator hut, derived from the
    /// lift's type and drive end alone (nothing saved). Hut size follows the drive (owner 2026-11-23): a detachable's or
    /// gondola's drive the large hut and its return the small; a fixed grip's drive the small and its return the mini; a
    /// surface tow the T-bar's own hut at its bottom (drive) station; a carpet keeps only its control post. A fixed grip's
    /// top, drive or return (owner 2026-11-25), gets the mini hut, the stop gate past its bullwheel and the operator's
    /// pedestal, and, standing 5 ft (1.524 m) or
    /// more over the mean ground under the level deck, the timber unload ramp for its chair's width (owner 2026-11-24;
    /// under 5 ft the ground stays the terminal's pad: the earth mound is a later piece). On the ramp the top gets no pad
    /// (LF-12 v6, <see cref="LiftBuild"/>'s TopPad).
    /// </summary>
    public static class TerminalExtras
    {
        /// <summary>Owner 2026-11-24: at 5 ft and up the timber ramp; below, the earth (today's pad).</summary>
        public const double RampRaiseMetres = 1.524;
        public const string Pedestal = "bldg_op_pedestal";

        /// <summary>unloadramp.json: the level deck from 3.0 m before the unload point to 1.5 m past it (the breakover).</summary>
        public const double DeckBefore = 3.0, DeckAfter = 1.5;
        /// <summary>unloadramp.json: the ski-off at 30 %, modelled 20 m long, 1 m walkway, each side flaring 1 in 10.</summary>
        public const double Slope = 0.3, RampLength = 20, Walkway = 1, Flare = 0.1;

        /// <summary>The ramp's chair-width numbers (unloadramp.json variants, built buildings.json): gauge, chair width, the
        /// whole deck's width, and the level deck's area, metres and m².</summary>
        public readonly struct RampSpec
        {
            public readonly double Gauge, ChairWidth, DeckWidth, LevelSquareMetres;
            public RampSpec(double gauge, double chairWidth, double deckWidth, double levelSquareMetres)
            {
                Gauge = gauge; ChairWidth = chairWidth; DeckWidth = deckWidth; LevelSquareMetres = levelSquareMetres;
            }
        }

        public static RampSpec Spec(RampWidth w, bool wide = false) => wide
            ? (w == RampWidth.Quad ? new RampSpec(4.120, 2.310, 11.6, 59.7)
               : w == RampWidth.Triple ? new RampSpec(3.658, 1.770, 10.4, 54.3) : new RampSpec(3.353, 1.240, 9.2, 48.9))
            : w == RampWidth.Quad ? new RampSpec(4.120, 2.310, 8.8, 47.1)
            : w == RampWidth.Triple ? new RampSpec(3.658, 1.770, 7.6, 41.7) : new RampSpec(3.353, 1.240, 6.4, 36.3);

        /// <summary>An end's ramp numbers (the Monta drive's wide ramps).</summary>
        public static RampSpec Spec(in EndExtras e) => Spec(e.Width, e.Wide);

        /// <summary>
        /// unloadramp.json terminals (measured on the lift models): the top terminal's bullwheel axle past its unload point,
        /// metres, for each style (<see cref="EndExtras.Style"/>); the same for its quad, triple and double. The stop gate
        /// stands from it.
        /// </summary>
        public static double WheelU(string style) => style switch
        {
            "stern" => 3.413,
            "monta" => 1.000,
            "sessellift_drive" => 3.230,
            "monta_drive" => 3.200,
            _ => 1.990,
        };

        /// <summary>unloadramp.json gate: the post stands this far inside the chair's inner end where it crosses the line.</summary>
        public const double GatePostClear = 0.450;

        public static string HutModelOf(HutKind k) => k switch
        {
            HutKind.Large => "bldg_hut_large",
            HutKind.Small => "bldg_hut_small",
            HutKind.Mini => "bldg_hut_mini",
            HutKind.Tow => "monta_tbar_hut",
            _ => null,
        };

        public static string RampModelOf(RampWidth w) => w == RampWidth.Quad ? "bldg_ramp_quad" : w == RampWidth.Triple ? "bldg_ramp_triple" : "bldg_ramp_double";

        /// <summary>Whether the lift's top terminal drives it (from its model's name, else the type's drive end).</summary>
        public static bool TopDrives(LiftType type)
        {
            string top = type?.Top?.Model ?? "", bottom = type?.Bottom?.Model ?? "";
            if (top.Contains("_drive")) return true;
            if (bottom.Contains("_drive") || top.Contains("_return")) return false;
            return type == null || type.Drive == LiftDrive.Top;
        }

        static bool FixedGrip(LiftType type) => !type.Detachable && !type.Tow && !type.Belt;

        /// <summary>The top return's maker style, from its model ("sessellift_stern…", "monta_…", else the Sessellift's).</summary>
        public static string MakerOf(LiftType type)
        {
            string m = type?.Top?.Model ?? "";
            return m.StartsWith("sessellift_stern", StringComparison.Ordinal) ? "stern" : m.StartsWith("monta_", StringComparison.Ordinal) ? "monta" : "sessellift";
        }

        public static RampWidth WidthOf(LiftType type) => type.Seats <= 2 ? RampWidth.Double : type.Seats == 3 ? RampWidth.Triple : RampWidth.Quad;

        /// <summary>What the <paramref name="top"/> (or bottom) end of a lift of <paramref name="type"/> ships with.</summary>
        public static EndExtras For(LiftType type, bool top)
        {
            if (type == null || type.Belt) return default;
            if (type.Tow) return new EndExtras(top ? HutKind.None : HutKind.Tow, false, RampWidth.Quad, null);
            bool drive = TopDrives(type) == top;
            if (type.Detachable) return new EndExtras(drive ? HutKind.Large : HutKind.Small, false, RampWidth.Quad, null);
            if (!FixedGrip(type)) return default;
            // Owner 2026-11-25: every fixed grip's top, drive or return, is an unload end (its gate, pedestal and ramp), its
            // hut the mini in the ramp's bay; a bottom drive keeps the small hut, a bottom return the mini.
            if (top) return new EndExtras(HutKind.Mini, true, WidthOf(type), MakerOf(type), drive);
            return new EndExtras(drive ? HutKind.Small : HutKind.Mini, false, WidthOf(type), MakerOf(type), drive);
        }

        /// <summary>
        /// How far a fixed grip's top stands over the mean ground under its level deck, metres: the pad's level less
        /// the ground before the pad (<see cref="TerminalPad.Result.Before"/>), sampled on a 5 × 5 grid over the deck (from
        /// <see cref="DeckBefore"/> before the unload point to <see cref="DeckAfter"/> past it, the deck's width centred on
        /// the line). 0 for any other end or without a pad.
        /// </summary>
        public static double UnloadRaise(LiftType type, TerminalPad.Result topPad) =>
            topPad?.Before == null ? 0 : UnloadRaise(type, topPad.Before, topPad.X, topPad.Y, topPad.Ux, topPad.Uy, topPad.Level);

        /// <summary>The same over <paramref name="ground"/> for a top bullwheel at (<paramref name="x"/>, <paramref name="y"/>),
        /// (<paramref name="ux"/>, <paramref name="uy"/>) toward the bottom, standing at <paramref name="level"/> (the view:
        /// the ground as the package has it).</summary>
        public static double UnloadRaise(LiftType type, IHeightSource ground, double x, double y, double ux, double uy, double level)
        {
            var e = For(type, true);
            if (!e.UnloadEnd || ground == null) return 0;
            var spec = Spec(e);
            double n = Math.Sqrt(ux * ux + uy * uy);
            if (n < 1e-9) return 0;
            ux /= n; uy /= n;
            // The ramp's +u runs away from the line (past the bullwheel); the unload point is WheelU in front of the wheel.
            double ax = -ux, ay = -uy;
            double px = x - ax * WheelU(e.Style), py = y - ay * WheelU(e.Style);
            // Across the line: the left of the ramp's +u (any side: the deck is centred on the line).
            double cx = -ay, cy = ax;
            double sum = 0;
            int count = 0;
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 4; j++)
                {
                    double u = -DeckBefore + (DeckBefore + DeckAfter) * i / 4.0;
                    double v = -spec.DeckWidth / 2 + spec.DeckWidth * j / 4.0;
                    double g = ground.At(px + ax * u + cx * v, py + ay * u + cy * v);
                    if (double.IsNaN(g)) continue;
                    sum += g;
                    count++;
                }
            return count == 0 ? 0 : Math.Max(0, level - sum / count);
        }

        /// <summary>Whether an end with these extras, standing <paramref name="raise"/> over its ground, shows the ramp.</summary>
        public static bool RampShown(in EndExtras e, double raise) => e.UnloadEnd && raise >= RampRaiseMetres - 1e-6;

        /// <summary>
        /// The ramp's deck above the ground, m² (unloadramp.json's site report on level ground): the level deck and, down the
        /// ski-off at 30 % till it meets the ground (at most its modelled 20 m), the ramp and walkway, flaring 1 in 10 a side.
        /// </summary>
        public static double RampDeckSquareMetres(RampWidth w, double raise, bool wide = false)
        {
            if (raise <= 0) return 0;
            var spec = Spec(w, wide);
            double meet = Math.Min(raise / Slope, RampLength);
            return spec.LevelSquareMetres + (spec.DeckWidth + Walkway) * meet + Flare * meet * meet;
        }
    }
}
