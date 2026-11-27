using System;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Task 4.50 phase 6, slice 3 (owner 2026-11-25): when a building's lights and windows glow. Every light carries a
    /// schedule tag from the Blender kit (tools/assets/buildings/README.md): <see cref="Dusk"/> lights burn from dusk to
    /// dawn (a photocell); the others (<see cref="LiftOpen"/>, <see cref="Running"/>, <see cref="NightShift"/>) only while
    /// their building is in use: an operator hut while its lift runs, so after closing a hut keeps only its door sconce.
    /// A building in use shows its lit interior as the light fails (<see cref="Dim"/>), so a hut glows at closing even
    /// in winter, when the lifts stop before dark; the photocell lamps wait for night (<see cref="Night"/>).
    /// Nothing here lights the ground.
    /// </summary>
    public static class BuildingGlow
    {
        public const string Dusk = "dusk", LiftOpen = "lift_open", Running = "running", NightShift = "night_shift";

        /// <summary>The interior shows from the sun this high (degrees), fully from <see cref="DimFullDegrees"/>.</summary>
        public const double DimFromDegrees = 10, DimFullDegrees = -2;

        /// <summary>Whether a light on <paramref name="schedule"/> is switched on (a dusk light always: its photocell decides).</summary>
        public static bool On(string schedule, bool inUse) => schedule == Dusk || (inUse && !string.IsNullOrEmpty(schedule));

        /// <summary>
        /// How strongly a switched-on light shows, 0–1: a dusk light by the night's weight (<paramref name="night"/>, the
        /// scene's own: 0 by day, 1 once the sun is 7° down); any other by the failing light (<see cref="Dim"/>).
        /// </summary>
        public static double Level(string schedule, bool inUse, double night, double sunElevationDegrees)
        {
            if (!On(schedule, inUse)) return 0;
            return schedule == Dusk ? Clamp01(night) : Dim(sunElevationDegrees);
        }

        /// <summary>The failing light, 0–1: 0 with the sun <see cref="DimFromDegrees"/> up or higher, 1 at
        /// <see cref="DimFullDegrees"/> and below, eased between.</summary>
        public static double Dim(double sunElevationDegrees)
        {
            double t = Clamp01((DimFromDegrees - sunElevationDegrees) / (DimFromDegrees - DimFullDegrees));
            return t * t * (3 - 2 * t);
        }

        static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));
    }
}
