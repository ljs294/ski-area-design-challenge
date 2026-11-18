using System;
using System.Globalization;
using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Finding A3-6: the snow shell's culling boxes follow the snow. Every patch box of every tile is raised by the deepest
    /// lift the snow has held plus <see cref="ShellPadSlackMetres"/>, in whole metres, so rising snow re-pads a handful of
    /// times a season (<see cref="TerrainTiles.PadShellBounds"/>; height edits re-pad by themselves).
    /// <para>Developer switches: <c>-noshellpad</c> leaves the boxes on the earth (the check's before);
    /// <c>-flatpad x,z,r</c> flattens the earth's heights in a square of half-side r metres round (x, z) to the height at its
    /// centre, straight into the tiles (no design edit, nothing saved), so the check can stand over perfectly flat ground
    /// like a graded pad's.</para>
    /// </summary>
    public sealed partial class MountainViewer
    {
        const float ShellPadSlackMetres = 1;

        float _shellPad, _shellPadExtra;
        bool _noShellPad;
        object _shellPadFor;

        void ReadShellPadArguments(string[] args)
        {
            _noShellPad = Array.IndexOf(args, "-noshellpad") >= 0;
            _shellPadExtra = Array.IndexOf(args, "-shelldebug") >= 0 ? 2 : 0;   // -shelldebug raises the shell 2 m more (_ShellForce)
            int f = Array.IndexOf(args, "-flatpad");
            if (f >= 0 && f + 1 < args.Length)
            {
                var p = args[f + 1].Split(',');
                if (p.Length == 3 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                    && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
                    && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float r))
                    FlattenForCheck(x, z, r);
            }
        }

        void UpdateShellPad()
        {
            var ground = _resort?.Ground;
            var states = _resort?.States;
            if (ground?.ShellTiles == null || states == null) return;
            if (!ReferenceEquals(_shellPadFor, ground)) { _shellPadFor = ground; _shellPad = 0; }
            if (_noShellPad) return;
            float need = states.LiftCeiling + _shellPadExtra;
            if (_shellPad > 0 && need <= _shellPad - ShellPadSlackMetres) return;
            _shellPad = Mathf.Ceil(need) + ShellPadSlackMetres;
            foreach (var tile in ground.ShellTiles)
                if (tile != null) TerrainTiles.PadShellBounds(tile.terrainData, _shellPad);
            Debug.Log($"[ShellPad] the snow shell's culling boxes stand {_shellPad:F0} m over the earth (deepest lift {states.LiftCeiling:F2} m)");
        }

        void FlattenForCheck(float x, float z, float half)
        {
            if (_resort?.Ground?.ShellTiles == null) return;
            foreach (var tile in _resort.Ground.ShellTiles)
            {
                if (tile == null) continue;
                var data = tile.terrainData;
                Vector3 o = tile.GetPosition();
                int res = data.heightmapResolution;
                float step = data.size.x / (res - 1);
                int ci = Mathf.RoundToInt((x - o.x) / step), cj = Mathf.RoundToInt((z - o.z) / step);
                if (ci < 0 || cj < 0 || ci >= res || cj >= res) continue;
                int r = Mathf.CeilToInt(half / step);
                int i0 = Mathf.Max(0, ci - r), j0 = Mathf.Max(0, cj - r), i1 = Mathf.Min(res - 1, ci + r), j1 = Mathf.Min(res - 1, cj + r);
                float level = data.GetHeights(ci, cj, 1, 1)[0, 0];
                var block = new float[j1 - j0 + 1, i1 - i0 + 1];
                for (int j = 0; j <= j1 - j0; j++)
                    for (int i = 0; i <= i1 - i0; i++) block[j, i] = level;
                TerrainTiles.SetHeightsRegion(data, i0, j0, block);
                Debug.Log($"[ShellPad] -flatpad: {block.GetLength(1)}x{block.GetLength(0)} samples of {tile.name} flattened to {o.y + level * data.size.y:F2} m round ({x:F0}, {z:F0})");
                return;
            }
            Debug.LogWarning($"[ShellPad] -flatpad: no tile under ({x:F0}, {z:F0})");
        }
    }
}
