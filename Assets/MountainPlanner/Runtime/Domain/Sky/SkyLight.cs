using System;

namespace MountainPlanner.Domain.Sky
{
    /// <summary>
    /// The one directional light the scene has (0.3 §4.7, A4): the sun by day, the moon by night. The moon is
    /// artistic, not astronomical (owner, 2026-10-01): always <see cref="MoonElevation"/> up, opposite the
    /// sun's azimuth (where a full moon stands), at full strength, so the snow is readable every night
    /// (0.5 §4). The light fades out as the sun reaches the horizon, and the moon fades in through
    /// twilight; the switch happens at <see cref="SwitchElevation"/>, where both are dark.
    /// </summary>
    public readonly struct SkyLight
    {
        public const double MoonElevation = 45;
        /// <summary>Sun elevations (degrees): the sun fades from full to nothing, then the moon from nothing to full.</summary>
        public const double SunFull = 2, SunGone = -1, SwitchElevation = -2, MoonStart = -3, MoonFull = -8;

        /// <summary>Unit vector toward the light in the resort's frame: x east, y up, z grid north.</summary>
        public readonly double X, Y, Z;
        public readonly bool IsMoon;
        /// <summary>0–1: how much of the light's preset intensity shines (the twilight fades).</summary>
        public readonly double Strength;
        /// <summary>The sun's apparent elevation, degrees: the lighting look follows it.</summary>
        public readonly double SunElevation;

        SkyLight(double x, double y, double z, bool moon, double strength, double sunElevation)
        {
            X = x; Y = y; Z = z;
            IsMoon = moon;
            Strength = strength;
            SunElevation = sunElevation;
        }

        /// <summary>The light for a sun position; <paramref name="gridConvergence"/> as <see cref="SolarPosition.GridAzimuth"/> takes it.</summary>
        public static SkyLight From(in SolarPosition sun, double gridConvergence)
        {
            double e = sun.Elevation;
            if (e >= SwitchElevation)
            {
                var (x, y, z) = sun.DirectionInFrame(gridConvergence);
                return new SkyLight(x, y, z, false, SmoothStep(SunGone, SunFull, e), e);
            }
            double az = (sun.GridAzimuth(gridConvergence) + 180) * Math.PI / 180, el = MoonElevation * Math.PI / 180;
            return new SkyLight(Math.Sin(az) * Math.Cos(el), Math.Sin(el), Math.Cos(az) * Math.Cos(el), true,
                                SmoothStep(MoonStart, MoonFull, e), e);
        }

        static double SmoothStep(double from, double to, double v)
        {
            double t = Math.Max(0, Math.Min(1, (v - from) / (to - from)));
            return t * t * (3 - 2 * t);
        }
    }
}
