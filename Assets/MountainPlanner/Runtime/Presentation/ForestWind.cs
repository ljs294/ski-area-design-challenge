using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The forest's wind (TR4): three levels the player cycles (the viewer's B key), eased so trees never
    /// snap, and a wind clock that runs faster in stronger wind, so trees sway faster without jumping. The
    /// tree shader (WindOffset in TreeInstanced.shader) reads it as <c>_Wind</c>. Task 4.06: the weather drives it
    /// (<see cref="SetWeather"/>) until the player picks a level again; the lean's direction then follows the hour's wind,
    /// turning at most <see cref="MostTurnPerSecond"/>, and the strength never falls to zero, so <c>_WIND</c> never flips.
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

        /// <summary>Task 4.06: the weather's wind: the strength at 10 mph is the breeze's, a gale is full.</summary>
        public const float WeatherFloor = 0.02f, WeatherMost = 1.15f, MostTurnPerSecond = 10f;

        public Level Target { get; private set; }
        /// <summary>True while the weather sets the wind (task 4.06); a chosen level takes it back.</summary>
        public bool Weathered { get; private set; }
        float _goal, _towardDegrees = 67.5f, _weatherToward = 67.5f;
        public float Strength { get; private set; }
        public float Clock { get; private set; }

        public ForestWind(Level level = Level.Breeze)
        {
            Target = level;
            Strength = _goal = StrengthOf(level);
        }

        /// <summary>The weather's strength for a wind (mph).</summary>
        public static float StrengthOfWind(float mph) => Mathf.Clamp(WeatherFloor + mph * 0.0333f, WeatherFloor, WeatherMost);

        /// <summary>Task 4.06: the weather's wind: its speed (mph) and where it blows toward (grid degrees, clockwise from +z).</summary>
        public void SetWeather(float mph, float towardGridDegrees)
        {
            if (!Weathered) _towardDegrees = BaseDegrees;
            Weathered = true;
            _goal = StrengthOfWind(mph);
            _weatherToward = towardGridDegrees;
        }

        const float BaseDegrees = 67.5f;

        public static float StrengthOf(Level level) => level switch
        {
            Level.Calm => 0f,
            Level.Breeze => 0.35f,
            _ => 1f,
        };

        /// <summary>Calm, breeze, strong, calm…; the strength eases to the new level.</summary>
        public Level Cycle()
        {
            Weathered = false;
            Target = (Level)(((int)Target + 1) % 3);
            _goal = StrengthOf(Target);
            return Target;
        }

        /// <summary>Jumps straight to a level (captures and benchmarks, which shouldn't wait for the ease).</summary>
        public void Set(Level level)
        {
            Weathered = false;
            Target = level;
            Strength = _goal = StrengthOf(level);
            _towardDegrees = BaseDegrees;
        }

        public void Advance(float deltaSeconds)
        {
            if (deltaSeconds <= 0) return;
            float goal = _goal;
            if (Weathered)
            {
                float turn = Mathf.DeltaAngle(_towardDegrees, _weatherToward);
                float most = MostTurnPerSecond * deltaSeconds;
                _towardDegrees = (_towardDegrees + Mathf.Clamp(turn, -most, most) + 360f) % 360f;
            }
            Strength = Mathf.Abs(goal - Strength) < 1e-3f ? goal : Mathf.Lerp(Strength, goal, 1f - Mathf.Exp(-3f * deltaSeconds / EaseSeconds));
            Clock = (Clock + deltaSeconds * (0.7f + 0.6f * Strength)) % ClockPeriod;
        }

        /// <summary>Where it blows toward now, in world xz (unit length).</summary>
        public Vector2 Direction
        {
            get
            {
                float veer = VeerDegrees * Mathf.Deg2Rad * Mathf.Sin(Clock * (2f * Mathf.PI / 200f));
                float a = _towardDegrees * Mathf.Deg2Rad - veer;
                return new Vector2(Mathf.Sin(a), Mathf.Cos(a));
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
