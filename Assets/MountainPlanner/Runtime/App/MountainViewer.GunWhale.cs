using System;
using System.Globalization;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.07 PR A3, developer flags for seeing the snow lift the ground (both imply <c>-snowseed</c>):
    /// <list type="bullet">
    /// <item><c>-snowseedm &lt;metres&gt;</c>: the seed is an even depth everywhere (the 0, 1 and 3 m captures).</item>
    /// <item><c>-gunwhale &lt;nights&gt; [x z]</c>: a 20 ft stick gun's whale (<see cref="GunWhale"/>, SG-03/SG-05/SN-27) added
    /// to the seed, on open ground near the base aimed down the fall line, or with its gun at local x, z.</item>
    /// </list>
    /// </summary>
    public sealed partial class MountainViewer
    {
        static float? _seedMetres;
        static bool _seedMetresRead;

        /// <summary><c>-snowseedm</c>'s depth (m), or null.</summary>
        internal static float? SeedMetres
        {
            get
            {
                if (_seedMetresRead) return _seedMetres;
                _seedMetresRead = true;
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-snowseedm");
                if (i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float m))
                    _seedMetres = Mathf.Clamp(m, 0, 60);
                return _seedMetres;
            }
        }

        /// <summary><c>-gunwhale</c>'s nights (0: none) and the gun's spot, if given.</summary>
        internal static (int Nights, Vector2? At) GunWhaleWanted
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-gunwhale");
                if (i < 0) return (0, null);
                int nights = i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Mathf.Clamp(n, 1, 30) : 3;
                Vector2? at = null;
                if (i + 3 < args.Length && float.TryParse(args[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(args[i + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) at = new Vector2(x, z);
                return (nights, at);
            }
        }

        /// <summary>The whale's own snow per cell of the field (m, added to what is there), once built; null without the switch.</summary>
        float[] _whaleAdd;
        bool _whaleLaid;
        float _whaleModelledMetres, _whaleSquareMetres;

        /// <summary>The middle of the whale's pile and the gun's aim (local x/z), once built: the gate's "whale" spots.</summary>
        internal Vector2? WhaleCentre { get; private set; }
        internal Vector2 WhaleAim { get; private set; }

        /// <summary>The seed the flags ask for, from the opening's snowpack (<paramref name="cells"/>, metres); the same array
        /// when no flag is set.</summary>
        float[] DeveloperSeed(float[] cells, int width, int height, float cellMetres)
        {
            float? even = SeedMetres;
            var (nights, at) = GunWhaleWanted;
            if (even == null && nights == 0) return cells;
            var seed = (float[])cells.Clone();
            if (even != null) for (int c = 0; c < seed.Length; c++) seed[c] = even.Value;
            if (nights > 0)
            {
                if (_whaleAdd == null) BuildWhale(nights, at, width, height, cellMetres);
                for (int c = 0; c < seed.Length; c++) seed[c] += _whaleAdd[c];
            }
            return seed;
        }

        void BuildWhale(int nights, Vector2? at, int width, int height, float cellMetres)
        {
            var (gun, aim) = at.HasValue ? (at.Value, Downhill(at.Value)) : WhaleSpot();
            var settings = GunWhale.Settings.Stick20;
            var whale = GunWhale.Build(gun.x, gun.y, aim.x, aim.y, nights, Pacing.Default.SeedDensity, settings);
            var ring = _resort.Ring;
            _whaleAdd = whale.AddedMetres(ring.xMin, ring.yMin, cellMetres, width, height, out long outside);
            float tallest = 0;
            foreach (float add in _whaleAdd) tallest = Mathf.Max(tallest, add);
            _whaleModelledMetres = (float)whale.PeakMetres;
            _whaleSquareMetres = tallest;
            WhaleAim = aim;
            WhaleCentre = gun + aim * (float)(settings.ThrowMetres / 2);
            // What is true here, in the order it loses height: the gun's own model, its mean over the field's squares, and (once
            // the snow is in: WhaleReport) what the game draws. An earlier line called the square's mean "drawn" (finding W2).
            Debug.Log($"[GunWhale] {nights} night(s), gun at ({gun.x:F0}, {gun.y:F0}) aimed ({aim.x:F2}, {aim.y:F2}), pile centred " +
                      $"({WhaleCentre.Value.x:F0}, {WhaleCentre.Value.y:F0}): {whale.MadeCm3 / 1e6:F0} m³ of water" +
                      (outside > 0 ? $" ({outside / 1e6:F0} m³ fell off the field)" : "") +
                      $"; modelled peak {_whaleModelledMetres / 0.3048f:F1} ft; fullest {cellMetres:F0} m square {tallest / 0.3048f:F1} ft on top of the snow there; " +
                      "the drawn peak follows once the snow is in");
        }

        void StartWhaleReport(string[] args)
        {
            if (GunWhaleWanted.Nights > 0) StartCoroutine(WhaleReport());
        }

        /// <summary>
        /// Says what became of <c>-gunwhale</c> once the live snow is in (findings W2 and W5): the peak the game draws
        /// (<see cref="Presentation.SnowCursor.LiftAt"/>, the curve the snow shell stands on) and where, or that no whale was
        /// laid and why.
        /// </summary>
        System.Collections.IEnumerator WhaleReport()
        {
            float waited = 0;
            while (_resort == null || _resort.States == null) yield return null;
            var states = _resort.States;
            // Until the snow is live and the shell stands on it (the lift switches on a frame or two after the first pass;
            // with the Snow layer off it never does, and the report says so).
            for (; !(_simSnowTerrainSent && states.FirstPassDone && (states.LiftOn || !states.SnowShown)) && waited < 180; waited += Time.unscaledDeltaTime) yield return null;
            for (int f = 0; f < 10; f++) yield return null;
            if (_whaleAdd == null)
            {
                Debug.LogWarning("[GunWhale] NOT laid: " + (_simSnowSent
                    ? "the game opened with snow of its own (a save, or the snow went live before the seed), so no seed was sent"
                    : "no seed was sent (the snow's inputs or the opening's snowpack never arrived)"));
                yield break;
            }
            if (!_whaleLaid)
            {
                Debug.LogWarning("[GunWhale] NOT laid: the simulation didn't take the seed");
                yield break;
            }
            var centre = WhaleCentre.Value;
            float peak = 0;
            var peakAt = centre;
            for (float dz = -40; dz <= 40; dz += 2)
                for (float dx = -40; dx <= 40; dx += 2)
                {
                    float h = Presentation.SnowCursor.LiftAt(states, _resort.Ring, centre.x + dx, centre.y + dz);
                    if (h > peak) { peak = h; peakAt = new Vector2(centre.x + dx, centre.y + dz); }
                }
            var side = new Vector2(-WhaleAim.y, WhaleAim.x);
            float around = (Presentation.SnowCursor.LiftAt(states, _resort.Ring, centre.x + side.x * 40, centre.y + side.y * 40)
                            + Presentation.SnowCursor.LiftAt(states, _resort.Ring, centre.x - side.x * 40, centre.y - side.y * 40)) / 2;
            Debug.Log($"[GunWhale] drawn peak {peak / 0.3048f:F1} ft ({peak:F2} m) at ({peakAt.x:F0}, {peakAt.y:F0}), {(peak - around) / 0.3048f:F1} ft above the snow " +
                      $"round it ({around:F2} m); the gun's model peaks at {_whaleModelledMetres / 0.3048f:F1} ft and the fullest square holds {_whaleSquareMetres / 0.3048f:F1} ft " +
                      "(the snow lives on 8 m squares and is smoothed, so a narrow ridge draws as a lower, broader mound)" +
                      (states.LiftOn ? "" : "; the Snow layer is off, so nothing of it is drawn now"));
            // -whaleview: the view opens on the pile from the side, 80 m off and low, so a demo starts looking at it.
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-whaleview") >= 0 && Camera != null)
            {
                Camera.Frame(new Vector3(centre.x, _resort.Surface.HeightAt(centre.x, centre.y), centre.y), 80);
                Camera.SetAngles(Mathf.Atan2(WhaleAim.y, WhaleAim.x) * Mathf.Rad2Deg, 12);
            }
        }

        /// <summary>The fall line at a spot (unit, local x/z): down the earth's slope over 20 m; east where it's flat.</summary>
        Vector2 Downhill(Vector2 at)
        {
            var s = _resort.Surface;
            const float d = 10;
            float gx = s.HeightAt(at.x + d, at.y) - s.HeightAt(at.x - d, at.y), gz = s.HeightAt(at.x, at.y + d) - s.HeightAt(at.x, at.y - d);
            var g = new Vector2(-gx, -gz);
            return float.IsNaN(g.x) || float.IsNaN(g.y) || g.sqrMagnitude < 1e-4f ? Vector2.right : g.normalized;
        }

        /// <summary>
        /// Where the automatic whale goes: the lowest spot in the core whose whole pile lies on gentle, even, open ground (finding
        /// W4). The gun's square alone used to decide, so the pile could land on a ridge or beside trees and be mistaken for the
        /// ground. Over the pile's footprint (the gun's aim down the fall line, ±<see cref="SpotAlong"/> m along and
        /// ±<see cref="SpotAcross"/> m across its middle): a slope under 15 %, no point more than <see cref="SpotBump"/> m off
        /// the plane through its middle, nothing kept clear of snow (lakes, belts, the ring's edge) and no tree cover. If no
        /// spot passes, the lowest gentle one with a clear gun square (the old rule), and the log says so.
        /// </summary>
        const float SpotAlong = 24, SpotAcross = 16, SpotBump = 0.6f, SpotCanopy = 0.05f;

        (Vector2 Gun, Vector2 Aim) WhaleSpot()
        {
            var core = _resort.Core;
            var s = _resort.Surface;
            var states = _resort.States;
            var canopy = _resort.SnowCanopy;
            float throwHalf = (float)(GunWhale.Settings.Stick20.ThrowMetres / 2);
            Vector2 best = core.center, fallback = core.center;
            float lowest = float.PositiveInfinity, fallbackLowest = float.PositiveInfinity;
            const float step = 16, d = 10;

            bool Clear(float x, float z)
            {
                if (states == null) return true;
                int i = Mathf.FloorToInt((x - _resort.Ring.xMin) / Presentation.SurfaceStates.CellMetres);
                int j = Mathf.FloorToInt((z - _resort.Ring.yMin) / Presentation.SurfaceStates.CellMetres);
                int w = states.Snow.Width;
                if (i < 0 || j < 0 || i >= w || j >= states.Snow.Height || states.Keep[j * w + i] < 1) return false;
                return canopy == null || canopy.Length != w * states.Snow.Height || canopy[j * w + i] <= SpotCanopy;
            }

            for (float z = core.yMin + 64; z <= core.yMax - 64; z += step)
                for (float x = core.xMin + 64; x <= core.xMax - 64; x += step)
                {
                    float h = s.HeightAt(x, z);
                    if (float.IsNaN(h) || (h >= lowest && h >= fallbackLowest)) continue;
                    float gx = (s.HeightAt(x + d, z) - s.HeightAt(x - d, z)) / (2 * d), gz = (s.HeightAt(x, z + d) - s.HeightAt(x, z - d)) / (2 * d);
                    if (float.IsNaN(gx) || float.IsNaN(gz) || gx * gx + gz * gz > 0.15f * 0.15f) continue;
                    if (!Clear(x, z)) continue;
                    if (h < fallbackLowest) { fallbackLowest = h; fallback = new Vector2(x, z); }
                    if (h >= lowest) continue;
                    // The whole pile: its middle half a throw down the aim.
                    var aim = Downhill(new Vector2(x, z));
                    var mid = new Vector2(x, z) + aim * throwHalf;
                    var side = new Vector2(-aim.y, aim.x);
                    float h0 = s.HeightAt(mid.x, mid.y);
                    float mx = (s.HeightAt(mid.x + d, mid.y) - s.HeightAt(mid.x - d, mid.y)) / (2 * d), mz = (s.HeightAt(mid.x, mid.y + d) - s.HeightAt(mid.x, mid.y - d)) / (2 * d);
                    if (float.IsNaN(h0) || float.IsNaN(mx) || float.IsNaN(mz) || mx * mx + mz * mz > 0.15f * 0.15f) continue;
                    bool even = true;
                    for (float a = -SpotAlong; a <= SpotAlong && even; a += 8)
                        for (float c = -SpotAcross; c <= SpotAcross && even; c += 8)
                        {
                            var q = mid + aim * a + side * c;
                            float hq = s.HeightAt(q.x, q.y);
                            even = !float.IsNaN(hq) && Mathf.Abs(hq - (h0 + mx * (q.x - mid.x) + mz * (q.y - mid.y))) <= SpotBump && Clear(q.x, q.y);
                        }
                    if (!even) continue;
                    lowest = h;
                    best = new Vector2(x, z);
                }
            if (float.IsPositiveInfinity(lowest))
            {
                Debug.LogWarning("[GunWhale] no even, open, treeless spot in the core for the whole pile: using the lowest gentle spot (the mound may sit on uneven ground)");
                best = fallback;
            }
            return (best, Downhill(best));
        }
    }
}
