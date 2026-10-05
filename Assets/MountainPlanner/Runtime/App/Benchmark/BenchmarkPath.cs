using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// The benchmark's fixed camera path (0.3 §8; task 15): a loop through named legs. Each leg flies from the
    /// previous leg's last pose to its own (<see cref="TransitionSeconds"/>), then holds there while the camera
    /// turns slowly (<see cref="HoldSeconds"/>, <see cref="DriftDegrees"/>), so the camera is always moving. The pose
    /// is a function of time alone, so every run flies the same path, whatever the frame rate.
    /// </summary>
    public sealed class BenchmarkPath
    {
        public const float TransitionSeconds = 3f, HoldSeconds = 6f, DriftDegrees = 20f;

        public readonly struct Leg
        {
            public readonly string Name;
            public readonly Vector3 Target;
            public readonly float Distance, Yaw, Pitch;
            public Leg(string name, Vector3 target, float distance, float yaw, float pitch)
            {
                Name = name; Target = target; Distance = distance; Yaw = yaw; Pitch = pitch;
            }
        }

        public struct Pose
        {
            public Vector3 Target;
            public float Distance, Yaw, Pitch;
        }

        readonly Leg[] _legs;

        public BenchmarkPath(Leg[] legs) => _legs = legs;

        public int LegCount => _legs.Length;
        public string LegName(int leg) => _legs[leg].Name;
        public static float LegSeconds => TransitionSeconds + HoldSeconds;
        public float LapSeconds => _legs.Length * LegSeconds;

        /// <summary>The pose at <paramref name="seconds"/> into a lap (wraps around), and the leg it belongs to.</summary>
        public int Evaluate(float seconds, out Pose pose)
        {
            float t = Mathf.Repeat(seconds, LapSeconds);
            int leg = Mathf.Min(_legs.Length - 1, (int)(t / LegSeconds));
            float local = t - leg * LegSeconds;
            var to = _legs[leg];
            if (local < TransitionSeconds)
            {
                var from = _legs[(leg + _legs.Length - 1) % _legs.Length];
                float s = Mathf.SmoothStep(0f, 1f, local / TransitionSeconds);
                pose.Target = Vector3.Lerp(from.Target, to.Target, s);
                pose.Distance = Mathf.Exp(Mathf.Lerp(Mathf.Log(from.Distance), Mathf.Log(to.Distance), s));
                pose.Yaw = Mathf.LerpAngle(from.Yaw + DriftDegrees, to.Yaw, s);
                pose.Pitch = Mathf.Lerp(from.Pitch, to.Pitch, s);
            }
            else
            {
                float s = (local - TransitionSeconds) / HoldSeconds;
                pose.Target = to.Target;
                pose.Distance = to.Distance;
                pose.Yaw = to.Yaw + DriftDegrees * s;
                pose.Pitch = to.Pitch;
            }
            return leg;
        }

        /// <summary>True when <paramref name="seconds"/> is in the last half-second of a leg's hold: where a run takes the leg's screenshot.</summary>
        public static bool NearHoldEnd(float seconds) => Mathf.Repeat(seconds, LegSeconds) > LegSeconds - 0.5f;
    }
}
