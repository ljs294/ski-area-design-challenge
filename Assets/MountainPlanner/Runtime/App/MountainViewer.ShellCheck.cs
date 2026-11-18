using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MountainPlanner.Presentation;
using MountainPlanner.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.07 PR A3 round 3 repair (owner, 2026-11-17: "I see no difference in snow height from the terrain mesh"): the
    /// acceptance gate for the snow shell (the slices are <see cref="ShellProbe"/>'s, shared with SnowShellInUnityTests). <c>-shellcheck x,z[;x,z…]</c> measures, in the running game, how high the top
    /// DRAWN terrain surface stands above the earth at each spot and logs it beside what the game's own numbers say
    /// (<see cref="SnowCursor.LiftAt"/>): a shell that isn't drawn, or is drawn flat, reads 0 m whatever the CPU twin says.
    /// <para>How: a small camera looks straight down at the spot and draws thin height slices (near and far planes
    /// <see cref="SliceMetres"/> apart), terrain and snow shell only; the highest slice with anything in it is the top
    /// surface. No depth texture and no screenshot reading, so it measures the same thing in the editor and a built player.</para>
    /// <para><c>-shellsteps</c> adds the conditions that matter in play: the Snow layer off and on again, and a quality
    /// change and back, each measured. With <c>-screenshot</c> the check runs just before the picture; alone, it quits after.</para>
    /// </summary>
    public sealed partial class MountainViewer
    {
        const float SliceMetres = ShellProbe.SliceMetres, PlateMetres = 8f;

        static string ShellCheckSpots
        {
            get
            {
                var args = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(args, "-shellcheck");
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
        }

        void StartShellCheck(string[] args)
        {
            if (ShellCheckSpots == null || Array.IndexOf(args, "-screenshot") >= 0) return;   // with a picture: CaptureAndQuit runs it
            StartCoroutine(ShellCheckAndQuit());
        }

        IEnumerator ShellCheckAndQuit()
        {
            yield return WaitForCaptureReady();
            yield return ShellCheck("as opened");
            Application.Quit();
        }

        /// <summary>Every spot of <c>-shellcheck</c>, measured and logged; <c>-shellsteps</c> adds the layer and quality steps.</summary>
        IEnumerator ShellCheck(string when)
        {
            string spots = ShellCheckSpots;
            if (spots == null || _resort == null) yield break;
            var list = new List<Vector2>();
            if (spots == "whale")
            {
                // -shellcheck whale: a line of spots along and across the gun whale's pile, and one beside it (finding W6).
                if (WhaleCentre is Vector2 c)
                {
                    var along = WhaleAim;
                    var side = new Vector2(-along.y, along.x);
                    foreach (float a in new[] { -16f, -8f, 0f, 8f, 16f }) list.Add(c + along * a);
                    list.Add(c + side * 8);
                    list.Add(c - side * 8);
                    list.Add(c + side * 40);
                }
                else Debug.LogWarning("[ShellCheck] whale: no whale was built (-gunwhale)");
            }
            else
            foreach (string s in spots.Split(';'))
            {
                var v = s.Split(',');
                if (v.Length == 2 && float.TryParse(v[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(v[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) list.Add(new Vector2(x, z));
            }
            yield return MeasureShell(list, when);
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-shellsteps") < 0 || _layers == null) yield break;
            _layers.Set(MapLayers.Snow, false);
            for (int f = 0; f < 5; f++) yield return null;
            yield return MeasureShell(list, "Snow layer off");
            _layers.Set(MapLayers.Snow, true);
            for (int f = 0; f < 5; f++) yield return null;
            yield return MeasureShell(list, "Snow layer on again");
            foreach (var detail in new[] { TerrainDetail.Medium, TerrainDetail.High })
            {
                foreach (var terrain in Terrain.activeTerrains) TerrainDetailSettings.Apply(terrain, detail);
                for (int f = 0; f < 5; f++) yield return null;
                yield return MeasureShell(list, "terrain detail " + detail);
            }
        }

        IEnumerator MeasureShell(List<Vector2> spots, string when)
        {
            var states = _resort.States;
            var tiles = _resort.Ground?.ShellTiles?.ToList() ?? new List<Terrain>();
            int shells = 0, shown = 0, keyword = 0;
            foreach (var tile in tiles)
            {
                var shell = TerrainTiles.ShellOf(tile);
                if (shell == null) continue;
                shells++;
                if (shell.drawHeightmap && shell.enabled && shell.gameObject.activeInHierarchy) shown++;
                if (TerrainTiles.IsShell(shell)) keyword++;
            }
            Debug.Log($"[ShellCheck] {when}: {tiles.Count} tiles, {shells} shells ({shown} shown, {keyword} with the shell material), " +
                      $"lift scale {(states != null ? states.LiftScale : 0)}, global lift params {Shader.GetGlobalVector("_SnowLiftParams").x}, " +
                      $"shell material {(_resort.Ground?.ShellMaterial != null ? _resort.Ground.ShellMaterial.shader.name : "none")}");

            // The lift map as the graphics card holds it (the CPU twin reads its own array).
            float[] gpu = null;
            int gpuWidth = 0;
            if (Shader.GetGlobalTexture("_SnowLiftMap") is Texture2D liftMap)
            {
                var request = AsyncGPUReadback.Request(liftMap, 0);
                request.WaitForCompletion();
                if (!request.hasError) { gpu = request.GetData<float>().ToArray(); gpuWidth = liftMap.width; }
            }

            // Terrain and shells alone, on their own layer, measured by slices (ShellProbe).
            var probe = new ShellProbe(tiles);
            var found = new int[2];
            // The gate checks itself first: a plate a known height above the earth (above any snow) must read that height, or no reading below counts.
            if (spots.Count > 0 && HighlightMaterial != null)
            {
                float earth0 = _resort.Surface.HeightAt(spots[0].x, spots[0].y);
                var plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
                Destroy(plate.GetComponent<Collider>());
                plate.layer = ShellProbe.Layer;
                plate.GetComponent<Renderer>().sharedMaterial = HighlightMaterial;
                plate.transform.SetPositionAndRotation(new Vector3(spots[0].x, earth0 + PlateMetres, spots[0].y), Quaternion.Euler(90, 0, 0));
                plate.transform.localScale = new Vector3(4, 4, 1);
                yield return probe.Slices(spots[0], earth0, found);
                Destroy(plate);
                yield return null;
                Debug.Log($"[ShellCheck] {when} self-test: a plate {PlateMetres:F2} m above the earth reads " +
                          (found[0] == ShellProbe.Nothing ? "NOTHING: the gate is blind" : $"{found[0] * SliceMetres:F2} m: {(found[0] == Mathf.RoundToInt(PlateMetres / SliceMetres) ? "the gate sees" : "THE GATE IS OFF")}"));
            }
            foreach (var spot in spots)
            {
                float earth = _resort.Surface.HeightAt(spot.x, spot.y);
                if (float.IsNaN(earth)) { Debug.Log($"[ShellCheck] {when} at ({spot.x:F0}, {spot.y:F0}): off the terrain"); continue; }
                yield return probe.Slices(spot, earth, found);
                int top = found[0], lowest = found[1];
                float says = SnowCursor.LiftAt(states, _resort.Ring, spot.x, spot.y);
                string onCard = "-";
                if (gpu != null && states != null)
                {
                    int i = Mathf.FloorToInt((spot.x - _resort.Ring.xMin) / SurfaceStates.CellMetres), j = Mathf.FloorToInt((spot.y - _resort.Ring.yMin) / SurfaceStates.CellMetres);
                    if (i >= 0 && j >= 0 && i < gpuWidth && j * gpuWidth + i < gpu.Length) onCard = gpu[j * gpuWidth + i].ToString("F2", CultureInfo.InvariantCulture) + " m";
                }
                string drawn = top == ShellProbe.Nothing ? "NOTHING DRAWN in the slices"
                    : $"{(top - 0.5f) * SliceMetres:F2}–{(top + 0.5f) * SliceMetres:F2} m above the earth (lowest drawn slice {lowest * SliceMetres:F2} m)";
                bool ok = ShellProbe.Agrees(top, says);
                Debug.Log($"[ShellCheck] {when} at ({spot.x:F0}, {spot.y:F0}): game says {says:F2} m, lift map on the card {onCard}, " +
                          $"top drawn surface {drawn}: {(ok ? "OK" : "MISMATCH")}");
            }
            probe.Dispose();
        }
    }
}
