using System;

namespace MountainPlanner.Domain.Water
{
    /// <summary>What covers a lake or stream (0.3 §4.6, T8).</summary>
    public enum WaterSurfaceState
    {
        OpenWater = 0,
        Ice = 1,
        SnowCoveredIce = 2,
    }

    /// <summary>A water body's surface: its state, plus ice and snow thickness in metres.</summary>
    public readonly struct WaterSurface : IEquatable<WaterSurface>
    {
        public readonly WaterSurfaceState State;
        public readonly float IceMetres, SnowMetres;

        public WaterSurface(WaterSurfaceState state, float iceMetres, float snowMetres)
        {
            if (state < WaterSurfaceState.OpenWater || state > WaterSurfaceState.SnowCoveredIce) throw new ArgumentOutOfRangeException(nameof(state));
            if (!(iceMetres >= 0) || !(snowMetres >= 0) || float.IsInfinity(iceMetres) || float.IsInfinity(snowMetres))
                throw new ArgumentOutOfRangeException(nameof(iceMetres), "Thicknesses must be finite and not negative.");
            // Open water carries neither; ice carries no snow.
            State = state;
            IceMetres = state == WaterSurfaceState.OpenWater ? 0 : iceMetres;
            SnowMetres = state == WaterSurfaceState.SnowCoveredIce ? snowMetres : 0;
        }

        /// <summary>Iteration 1: frozen, under the same 12 in as the land.</summary>
        public static WaterSurface IterationOne => new WaterSurface(WaterSurfaceState.SnowCoveredIce, 0.4f, Snow.SnowDepthField.IterationOneMetres);

        public bool Equals(WaterSurface other) => State == other.State && IceMetres == other.IceMetres && SnowMetres == other.SnowMetres;
        public override bool Equals(object obj) => obj is WaterSurface other && Equals(other);
        public override int GetHashCode() => ((int)State * 397) ^ IceMetres.GetHashCode() * 31 ^ SnowMetres.GetHashCode();
        public override string ToString() => $"{State} (ice {IceMetres:0.##} m, snow {SnowMetres:0.##} m)";
    }

    /// <summary>
    /// Every water body's surface state: the seam a future weather engine freezes and thaws lakes through
    /// (0.3 §4.6). The lake itself never changes, and the renderer draws whatever the state says.
    /// Iteration 1 knows one body, <see cref="AllWater"/>, which stands for every lake and stream: telling
    /// lakes apart needs a body-ID map, which comes with the weather engine (owner, 2026-10-01). Changes bump
    /// <see cref="Version"/>, so a renderer polls it instead of subscribing.
    /// </summary>
    public sealed class WaterBodies
    {
        public const int AllWater = 0;
        readonly WaterSurface[] _surfaces;

        public WaterBodies(int count = 1)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            _surfaces = new WaterSurface[count];
            for (int i = 0; i < count; i++) _surfaces[i] = WaterSurface.IterationOne;
        }

        public int Count => _surfaces.Length;

        /// <summary>Goes up by one on every change.</summary>
        public int Version { get; private set; }

        public WaterSurface this[int body] => _surfaces[Check(body)];

        /// <summary>Sets one body's surface; returns false (and keeps the version) when it was already that.</summary>
        public bool Set(int body, WaterSurface surface)
        {
            if (_surfaces[Check(body)].Equals(surface)) return false;
            _surfaces[body] = surface;
            Version++;
            return true;
        }

        int Check(int body) => body >= 0 && body < _surfaces.Length ? body : throw new ArgumentOutOfRangeException(nameof(body));
    }
}
