using System;
using System.Collections;
using System.Globalization;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.08 PR A, a developer flag for the fine trail snow: <c>-paintwhale &lt;feet&gt; [x z]</c> paints a 20 ft stick
    /// gun's whale (<see cref="GunWhale"/>, capped at the feet asked, SN-27 v2's 26 ft at most) onto a trail by
    /// <see cref="SimCommandKind.PaintSnow"/>, as machine made snow, once the trails' fine snow is live. Without a spot it goes
    /// on the gentlest trail square whose neighbours are all trail (a run's middle, not its edge), aimed down the fall line.
    /// The log says what was painted and, once it is drawn, how high the drawn pile stands; <c>-shellcheck whale</c> measures
    /// it and <c>-whaleview</c> opens the view on it, as for <c>-gunwhale</c>. With <c>-simto</c> the paint waits for its date;
    /// <c>-nofine</c> draws it through the 8 m snow alone, as before this task (the review's before picture).
    /// </summary>
    public sealed partial class MountainViewer
    {
        static (float Feet, Vector2? At)? _paintWhaleWanted;

        /// <summary><c>-paintwhale</c>'s height (feet; 0: none) and the pile's middle, if given (local x, z). Read once: it is
        /// asked every frame, and the command line comes back as a new array each time (garbage, BB22).</summary>
        internal static (float Feet, Vector2? At) PaintWhaleWanted => _paintWhaleWanted ??= ReadPaintWhale();

        static (float Feet, Vector2? At) ReadPaintWhale()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-paintwhale");
            if (i < 0) return (0, null);
            float feet = i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? Mathf.Clamp(f, 1, 26) : 26;
            Vector2? at = null;
            if (i + 3 < args.Length && float.TryParse(args[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && float.TryParse(args[i + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) at = new Vector2(x, z);
            return (feet, at);
        }

        bool _paintSent, _paintReported;
        float _paintModelledMetres;

        /// <summary>The paint went to the worker and its tiles are drawn (or it never will be: no flag, or no trail snow).</summary>
        bool PaintWhaleSettled => PaintWhaleWanted.Feet <= 0 || _paintReported;

        /// <summary>Per frame, after the trail snow is sent: the paint, once, when the worker holds the trails' fine snow.</summary>
        void SendPaintWhale(SimSnapshot s)
        {
            if (_paintSent || _sim == null || _resort?.Surface == null || _simToRunning) return;   // after a -simto run's date
            var (feet, at) = PaintWhaleWanted;
            if (feet <= 0) return;
            var layout = _trailSnowSent;
            if (layout == null || s.TrailFineCells == 0 || s.TrailSnowWaiting) return;
            Vector2 centre;
            if (at.HasValue) centre = at.Value;
            else if (!PaintSpot(layout, out centre))
            {
                _paintSent = _paintReported = true;
                Debug.LogWarning("[PaintWhale] NOT painted: no trail square has trail all round it");
                return;
            }
            var aim = Downhill(centre);
            var settings = GunWhale.Settings.Stick20;
            settings.CapMetres = feet * 0.3048;
            var gun = centre - aim * (float)(settings.ThrowMetres / 2);
            int density = Pacing.Default.SeedDensity;
            // The fewest nights that bring the middle to the cap (the rest spills down the pile, SN-27).
            GunWhale whale = null;
            for (int nights = 1; nights <= 30; nights++)
            {
                whale = GunWhale.Build(gun.x, gun.y, aim.x, aim.y, nights, density, settings);
                if (whale.PeakMetres >= settings.CapMetres * 0.99) break;
            }
            var paint = ToPaint(whale);
            _paintSent = _sim.Send(SimCommand.Of(SimCommandKind.PaintSnow, 0, paint));
            if (!_paintSent) return;
            _paintModelledMetres = (float)whale.PeakMetres;
            WhaleCentre = centre;
            WhaleAim = aim;
            Debug.Log($"[PaintWhale] painted on the trails at ({centre.x:F0}, {centre.y:F0}) aimed ({aim.x:F2}, {aim.y:F2}): " +
                      $"{whale.MadeCm3 / 1e6:F0} m³ of water, modelled peak {whale.PeakMetres / 0.3048:F1} ft (cap {feet:F0} ft); " +
                      "the drawn peak follows once its tiles are up");
            StartCoroutine(PaintReport());
        }

        /// <summary>The whale's 3 ft squares as a paint: water depth (µm) a square, its corner in design mm.</summary>
        static SimSnowPaint ToPaint(GunWhale whale)
        {
            double area = GunWhale.FineMetres * GunWhale.FineMetres;
            var water = new int[whale.Size * whale.Size];
            for (int k = 0; k < water.Length; k++) water[k] = (int)Math.Min(int.MaxValue, Math.Round(whale.WaterCm3[k] / area));   // cm³ over m² = µm
            return new SimSnowPaint((long)Math.Round(whale.WestMetres * 1000), (long)Math.Round(whale.SouthMetres * 1000),
                                    (int)Math.Round(GunWhale.FineMetres * 1000), whale.Size, whale.Size, water, whale.Density);
        }

        /// <summary>The gentlest trail square (8 m) whose eight neighbours are trail too; ties go to the lower one.</summary>
        bool PaintSpot(TrailSnowLayout layout, out Vector2 centre)
        {
            centre = default;
            const float cell = SurfaceGrid.CellMetres;
            int w = layout.GridWidth, h = layout.GridHeight;
            float best = float.PositiveInfinity, bestHeight = float.PositiveInfinity;
            bool found = false;
            var s = _resort.Surface;
            foreach (int c in layout.BlockCell)
            {
                int i = c % w, j = c / w;
                if (i < 1 || j < 1 || i >= w - 1 || j >= h - 1) continue;
                bool inside = true;
                for (int dj = -1; dj <= 1 && inside; dj++)
                    for (int di = -1; di <= 1 && inside; di++)
                        inside = layout.BlockOf((j + dj) * w + i + di) >= 0;
                if (!inside) continue;
                float x = layout.OriginXMm / 1000f + (i + 0.5f) * cell, z = layout.OriginYMm / 1000f + (j + 0.5f) * cell;
                float h0 = s.HeightAt(x, z);
                float gx = (s.HeightAt(x + cell, z) - s.HeightAt(x - cell, z)) / (2 * cell), gz = (s.HeightAt(x, z + cell) - s.HeightAt(x, z - cell)) / (2 * cell);
                float slope = gx * gx + gz * gz;
                if (float.IsNaN(h0) || float.IsNaN(slope)) continue;
                // Within a percent of grade the lower one wins: the pile reads best near the base, as the gun's own.
                if (slope < best - 1e-4f || (Mathf.Abs(slope - best) <= 1e-4f && h0 < bestHeight))
                {
                    best = slope; bestHeight = h0; centre = new Vector2(x, z); found = true;
                }
            }
            return found;
        }

        /// <summary>Once the painted tiles are drawn: the drawn peak (the CPU twin of the snow shell) beside the model's.</summary>
        IEnumerator PaintReport()
        {
            var states = _resort.States;
            float waited = 0;
            // The worker stages the painted tiles a tick later; the atlas takes and uploads them over a few frames.
            for (int f = 0; f < 30; f++) yield return null;
            while (states?.Fine != null && (states.Fine.TilesTaken == 0 || states.Fine.SlotsWaiting > 0) && waited < 60)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            for (int f = 0; f < 10; f++) yield return null;
            _paintReported = true;
            if (states == null || WhaleCentre is not Vector2 centre) yield break;
            float peak = 0;
            var peakAt = centre;
            for (float dz = -40; dz <= 40; dz += 1)
                for (float dx = -40; dx <= 40; dx += 1)
                {
                    float hgt = states.LiftAt(centre.x + dx, centre.y + dz);
                    if (hgt > peak) { peak = hgt; peakAt = new Vector2(centre.x + dx, centre.y + dz); }
                }
            Debug.Log($"[PaintWhale] drawn peak {peak / 0.3048f:F1} ft ({peak:F2} m) at ({peakAt.x:F0}, {peakAt.y:F0}); the model peaks at " +
                      $"{_paintModelledMetres / 0.3048f:F1} ft; atlas {states.Fine.TilesTaken} tiles taken, {states.Fine.SlotsWaiting} waiting" +
                      (states.LiftOn ? "" : "; the Snow layer is off, so nothing of it is drawn now"));
            // -nofine: the same snow drawn as before this task, the 8 m lift alone (the review's before picture; the CPU twin
            // still stands on the fine snow).
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-nofine") >= 0)
            {
                states.Fine.SetDrawn(false);
                float before = Presentation.SnowCursor.LiftAt8(states, _resort.Ring, centre.x, centre.y);
                Debug.Log($"[PaintWhale] -nofine: the fine snow is not drawn; the 8 m snow alone stands {before / 0.3048f:F1} ft at the pile's middle");
            }
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-whaleview") >= 0 && Camera != null)
            {
                Camera.Frame(new Vector3(centre.x, _resort.Surface.HeightAt(centre.x, centre.y), centre.y), 80);
                Camera.SetAngles(Mathf.Atan2(WhaleAim.y, WhaleAim.x) * Mathf.Rad2Deg, 12);
            }
        }
    }
}
