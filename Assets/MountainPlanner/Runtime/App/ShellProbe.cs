using System;
using System.Collections;
using System.Collections.Generic;
using MountainPlanner.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.07 PR A3 (round 3 repair): how high the top DRAWN terrain surface stands above the earth at a spot, measured
    /// on the GPU. A small camera looks straight down and draws thin height slices (near and far planes
    /// <see cref="SliceMetres"/> apart) of the terrain tiles and their snow shells only; the highest slice with anything in
    /// it is the top surface. No depth texture and no screenshot reading, so it measures the same thing in the editor, a
    /// PlayMode test and a built player. Used by <c>-shellcheck</c> (<see cref="MountainViewer"/>) and
    /// <c>SnowShellInUnityTests</c>.
    /// </summary>
    public sealed class ShellProbe : IDisposable
    {
        public const float SliceMetres = 0.25f, TopMetres = 6f, EyeMetres = 20f;
        public const int Layer = 31, Pixels = 16;
        /// <summary>No slice had anything drawn in it.</summary>
        public const int Nothing = int.MinValue;

        readonly GameObject _go;
        readonly Camera _cam;
        readonly RenderTexture _rt;
        readonly Texture2D _read;
        readonly List<(GameObject Go, int Layer)> _moved = new List<(GameObject, int)>();

        /// <summary>Puts <paramref name="tiles"/> and their shells on <see cref="Layer"/> until disposed and makes the camera.</summary>
        public ShellProbe(IEnumerable<Terrain> tiles)
        {
            foreach (var tile in tiles)
            {
                if (tile == null) continue;
                Move(tile.gameObject);
                var shell = TerrainTiles.ShellOf(tile);
                if (shell != null) Move(shell.gameObject);
            }
            _rt = new RenderTexture(Pixels, Pixels, 24, RenderTextureFormat.ARGB32) { name = "Shell probe" };
            _read = new Texture2D(Pixels, Pixels, TextureFormat.RGBA32, false, true);
            _go = new GameObject("Shell probe camera");
            _cam = _go.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = 0.25f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.magenta;
            _cam.cullingMask = 1 << Layer;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.targetTexture = _rt;
            _cam.depth = -50;
            // Drawn on demand (Render), never by the frame: a batch-mode editor never reaches the end of a frame.
            _cam.enabled = false;
            _go.transform.rotation = Quaternion.Euler(90, 0, 0);
        }

        void Move(GameObject g)
        {
            _moved.Add((g, g.layer));
            g.layer = Layer;
        }

        Color32[] Read()
        {
            _cam.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = _rt;
            _read.ReadPixels(new Rect(0, 0, Pixels, Pixels), 0, 0);
            RenderTexture.active = previous;
            return _read.GetPixels32();
        }

        static bool Differs(Color32[] a, Color32[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++)
                if (Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b) > 24) n++;
            return n > a.Length / 8;
        }

        /// <summary>
        /// The highest (<c>result[0]</c>) and lowest (<c>result[1]</c>) slices, in <see cref="SliceMetres"/> above
        /// <paramref name="earth"/>, with anything drawn at <paramref name="spot"/> (local x/z); <see cref="Nothing"/>: none.
        /// Draws each slice itself (<see cref="Camera.Render"/>) and waits a frame between slices.
        /// </summary>
        public IEnumerator Slices(Vector2 spot, float earth, int[] result)
        {
            int slices = Mathf.RoundToInt(TopMetres / SliceMetres);
            _go.transform.position = new Vector3(spot.x, earth + EyeMetres, spot.y);
            // Unity culls a terrain's patches by their bounds on the earth, which a thin slice above flat ground misses
            // (the shell's lift is in the shader): the measurement culls with a tall box that always holds the earth.
            _cam.cullingMatrix = Matrix4x4.Ortho(-4, 4, -4, 4, 0.1f, EyeMetres + 10f) * _cam.worldToCameraMatrix;
            // An empty slice well above anything (what the sky, fog or weather alone leave in the picture).
            _cam.nearClipPlane = EyeMetres - 12f;
            _cam.farClipPlane = EyeMetres - 12f + SliceMetres;
            yield return null;
            var empty = Read();
            result[0] = Nothing;
            result[1] = int.MaxValue;
            for (int k = slices + 12; k >= -8; k--)
            {
                float a = (k - 0.5f) * SliceMetres, b = (k + 0.5f) * SliceMetres;   // the slice's heights above the earth
                _cam.nearClipPlane = EyeMetres - b;
                _cam.farClipPlane = EyeMetres - a;
                yield return null;
                if (!Differs(Read(), empty)) continue;
                if (result[0] == Nothing) result[0] = k;
                result[1] = k;
            }
        }

        /// <summary>The measured top (a slice index) agrees with <paramref name="saysMetres"/> within a slice and the curve's rounding.</summary>
        public static bool Agrees(int top, float saysMetres) => top != Nothing && Mathf.Abs(top * SliceMetres - saysMetres) <= SliceMetres + 0.13f;

        public void Dispose()
        {
            if (_cam != null) _cam.targetTexture = null;
            if (_go != null) Object.Destroy(_go);
            if (_rt != null) Object.Destroy(_rt);
            if (_read != null) Object.Destroy(_read);
            foreach (var (g, layer) in _moved) if (g != null) g.layer = layer;
            _moved.Clear();
        }
    }
}
