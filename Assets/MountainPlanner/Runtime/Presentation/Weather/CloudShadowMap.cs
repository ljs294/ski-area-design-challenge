using UnityEngine;

namespace MountainPlanner.Presentation.Weather
{
    /// <summary>
    /// Task 4.06: the clouds' shadows, worked out once a frame into a map of the deck's plane around the view, so a surface reads
    /// them with one tap (Weather.hlsl's <c>CloudShadow</c>) instead of the field's taps and the sun's sums per pixel; terrain,
    /// cliffs and trees then share one shadow exactly. <see cref="Extent"/> across, 1024² at High (about 20 m texels), 512² below;
    /// beyond it the ground takes the region's cover on average (it's deep in the haze by then). Its origin snaps to whole texels,
    /// so panning never makes the shadows crawl. Redrawn every <see cref="Every"/> frames (the clouds drift under a metre a frame
    /// over 20 m texels), and at once when the view moves a tenth of the way across.
    /// </summary>
    public sealed class CloudShadowMap
    {
        public const string ShaderResource = "MountainPlannerWeather/CloudShadowMap";
        /// <summary>How far the map reaches across (m), and its sizes (texels).</summary>
        public const float Extent = 20000;
        public const int SizeHigh = 1024, SizeLow = 512;

        static readonly int MapId = Shader.PropertyToID("_WxShadowMap"), AtId = Shader.PropertyToID("_WxShadowAt"),
                            EyeId = Shader.PropertyToID("_WxShadowEye");

        /// <summary>Redraw every this many frames.</summary>
        public int Every { get; set; } = 3;

        Material _material;
        RenderTexture _map;
        bool _missing;
        int _since;
        Vector2 _drawnAt;

        /// <summary>
        /// Draws the map centred on <paramref name="centre"/> (the deck's plane above the view's target, grid xz), at
        /// <paramref name="size"/> texels across. <paramref name="eye"/>: the camera moved onto the deck's plane as the target is
        /// (xy) and its height over the target (z), so the shadows' fine detail fades with distance. False when it can't (no
        /// shader): the shadows are then off.
        /// </summary>
        public bool Draw(Vector2 centre, Vector3 eye, int size)
        {
            if (_material == null)
            {
                if (_missing) return false;
                var shader = Resources.Load<Shader>(ShaderResource);
                if (shader == null || !shader.isSupported)
                {
                    _missing = true;
                    Debug.LogWarning("[Weather] the cloud shadow map's shader is missing: " + ShaderResource);
                    return false;
                }
                _material = new Material(shader) { name = "CloudShadowMap", hideFlags = HideFlags.DontSave };
            }
            if (_map == null || _map.width != size)
            {
                Release();
                var format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8) ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
                _map = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
                {
                    name = "CloudShadowMap", useMipMap = false, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.DontSave,
                };
                _map.Create();
                _since = Every;   // a new map draws at once
            }
            if (++_since < Every && _map.IsCreated() && (centre - _drawnAt).sqrMagnitude < Extent * Extent * 0.01f) return true;
            _since = 0;
            _drawnAt = centre;
            float texel = Extent / size;
            float x = Mathf.Floor(centre.x / texel) * texel - Extent * 0.5f, z = Mathf.Floor(centre.y / texel) * texel - Extent * 0.5f;
            Shader.SetGlobalVector(AtId, new Vector4(x, z, 1f / Extent, size));
            _material.SetVector(EyeId, eye);
            var active = RenderTexture.active;
            Graphics.Blit(null, _map, _material);
            RenderTexture.active = active;
            Shader.SetGlobalTexture(MapId, _map);
            return true;
        }

        void Release()
        {
            if (_map == null) return;
            _map.Release();
            Object.Destroy(_map);
            _map = null;
        }

        public void Dispose()
        {
            Release();
            if (_material != null) Object.Destroy(_material);
            _material = null;
        }
    }
}
