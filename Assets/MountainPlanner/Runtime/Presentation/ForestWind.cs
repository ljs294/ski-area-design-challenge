using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The forest's wind (TR4): three levels the player cycles (the viewer's B key), eased so trees never
    /// snap, and a wind clock that runs faster in stronger wind, so trees sway faster without jumping. The
    /// tree shader (WindOffset in TreeInstanced.shader) reads it as <c>_Wind</c>. Later, weather drives it.
    /// </summary>
    public sealed class ForestWind
    {
        public enum Level { Calm, Breeze, Strong }

        /// <summary>The clock wraps here; every wind frequency in the shader is whole cycles per this period.</summary>
        public const float ClockPeriod = 600f;
        /// <summary>Seconds for the strength to cover about 95% of a change.</summary>
        public const float EaseSeconds = 2f;
        /// <summary>The direction it blows toward: from the west-southwest (the Rockies' prevailing wind), in world xz.</summary>
        public static readonly Vector2 BaseDirection = new Vector2(Mathf.Sin(67.5f * Mathf.Deg2Rad), Mathf.Cos(67.5f * Mathf.Deg2Rad));
        /// <summary>How far the direction veers either side of <see cref="BaseDirection"/>, in degrees, over 200 s of clock.</summary>
        public const float VeerDegrees = 8f;

        public static readonly int WindId = Shader.PropertyToID("_Wind");

        public Level Target { get; private set; }
        public float Strength { get; private set; }
        public float Clock { get; private set; }

        public ForestWind(Level level = Level.Breeze)
        {
            Target = level;
            Strength = StrengthOf(level);
        }

        public static float StrengthOf(Level level) => level switch
        {
            Level.Calm => 0f,
            Level.Breeze => 0.35f,
            _ => 1f,
        };

        /// <summary>Calm, breeze, strong, calm…; the strength eases to the new level.</summary>
        public Level Cycle() => Target = (Level)(((int)Target + 1) % 3);

        /// <summary>Jumps straight to a level (captures and benchmarks, which shouldn't wait for the ease).</summary>
        public void Set(Level level)
        {
            Target = level;
            Strength = StrengthOf(level);
        }

        public void Advance(float deltaSeconds)
        {
            if (deltaSeconds <= 0) return;
            float goal = StrengthOf(Target);
            Strength = Mathf.Abs(goal - Strength) < 1e-3f ? goal : Mathf.Lerp(Strength, goal, 1f - Mathf.Exp(-3f * deltaSeconds / EaseSeconds));
            Clock = (Clock + deltaSeconds * (0.7f + 0.6f * Strength)) % ClockPeriod;
        }

        /// <summary>Where it blows toward now, in world xz (unit length).</summary>
        public Vector2 Direction
        {
            get
            {
                float veer = VeerDegrees * Mathf.Deg2Rad * Mathf.Sin(Clock * (2f * Mathf.PI / 200f));
                float s = Mathf.Sin(veer), c = Mathf.Cos(veer);
                return new Vector2(c * BaseDirection.x - s * BaseDirection.y, s * BaseDirection.x + c * BaseDirection.y);
            }
        }

        /// <summary>The shader's <c>_Wind</c>: direction (xy), strength (z), clock (w).</summary>
        public Vector4 ShaderValue
        {
            get
            {
                var d = Direction;
                return new Vector4(d.x, d.y, Strength, Clock);
            }
        }
    }
}
