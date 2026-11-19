using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>One SG-01 stage: water and air a gun draws at it, and the coldest wet bulb it starts at.</summary>
    public readonly struct GunStage
    {
        /// <summary>1–5 (0: too warm, or not running).</summary>
        public readonly int Number;
        /// <summary>The stage applies at or below this wet bulb, tenths of a degree Fahrenheit.</summary>
        public readonly int WetBulbTenthsF;
        public readonly int WaterGpm;
        public readonly int AirCfm;
        public readonly string Conditions;

        public GunStage(int number, int wetBulbTenthsF, int waterGpm, int airCfm, string conditions)
        {
            Number = number;
            WetBulbTenthsF = wetBulbTenthsF;
            WaterGpm = waterGpm;
            AirCfm = airCfm;
            Conditions = conditions;
        }

        public bool IsNone => Number == 0;
    }

    /// <summary>What a gun model is (SG-03, SG-04): its throw and mount. Data beside the lift catalog, not a switch.</summary>
    public readonly struct GunSpec
    {
        public readonly GunModel Model;
        public readonly string Name;
        public readonly string AssetId;
        public readonly int ThrowFt;
        public readonly bool Tower;

        public GunSpec(GunModel model, string name, string assetId, int throwFt, bool tower)
        {
            Model = model;
            Name = name;
            AssetId = assetId;
            ThrowFt = throwFt;
            Tower = tower;
        }
    }

    /// <summary>
    /// The SLE guns (SG-01…04; the ideas doc's table, re-specified, a fictional maker by code name only). Both mounts share one
    /// head, so the stages are the same for every model.
    /// </summary>
    public static class GunCatalog
    {
        /// <summary>SG-02: the least water pressure a gun runs on at its hydrant.</summary>
        public const int MinPsi = 200;
        /// <summary>SG-01: above this wet bulb (tenths °F) a gun is too warm.</summary>
        public const int TooWarmTenthsF = 280;

        /// <summary>SG-01, warmest first.</summary>
        public static readonly GunStage[] Stages =
        {
            new GunStage(1, 280, 18, 56, "Marginal"),
            new GunStage(2, 240, 28, 56, "Fair"),
            new GunStage(3, 190, 38, 56, "Good"),
            new GunStage(4, 140, 48, 16, "Excellent"),
            new GunStage(5, 90, 58, 16, "Optimal"),
        };

        public static readonly GunSpec[] Models =
        {
            new GunSpec(GunModel.Stick10, "SLE stick 10 ft", "sle_stick_gun_10", 30, true),
            new GunSpec(GunModel.Stick20, "SLE stick 20 ft", "sle_stick_gun_20", 80, true),
            new GunSpec(GunModel.Stick30, "SLE stick 30 ft", "sle_stick_gun_30", 125, true),
            new GunSpec(GunModel.Ground, "SLE ground gun", "sle_ground_gun", 30, false),
        };

        public static GunSpec Spec(GunModel m) => Models[(int)m];

        /// <summary>The coldest stage a wet bulb allows (SG-01), or none above <see cref="TooWarmTenthsF"/>.</summary>
        public static GunStage StageAt(int wetBulbTenthsF)
        {
            GunStage best = default;
            foreach (var s in Stages) if (wetBulbTenthsF <= s.WetBulbTenthsF) best = s;
            return best;
        }

        /// <summary>SM-02: a gun forced on while too warm still draws stage 1's water and air.</summary>
        public static GunStage Forced => Stages[0];
    }
}
