using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// Task 4.25 (GO-13, owner 2026-11-20 "we should do a dots thing for groomers"): the groomers on the map as amber dots, a
    /// little bigger than a skier's, with a warm glow round a working one at night (its headlights). The caller places
    /// each dot (where it stands, whether it is hovered, how much glow) every frame; one <see cref="Graphics.RenderPrimitives"/>
    /// call draws them all. At most <see cref="Cap"/> (a fleet's most), so picking is a plain pass over the screen points.
    /// Nothing is allocated per frame.
    /// </summary>
    public sealed class GroomerLayer : IDisposable
    {
        public const string ShaderResource = "MountainPlannerGuests/GroomerDot";
        public const int Cap = 32;
        /// <summary>The dot's size at 100% UI scale (a skier's is 9 px), its floor, and the most metres it covers.</summary>
        public const float DotPixels = 14f, MinDotPixels = 8f, DotMetres = 4f, HitPixels = 10f;
        /// <summary>The dot is drawn at a groomer's middle, this far over the snow, pulled towards the camera as a skier's is.</summary>
        public const float DotLiftMetres = 1.5f, PullMaxMetres = 2.5f, PullShare = 0.01f;
        /// <summary>The headlights' halo reaches this many times the dot's radius.</summary>
        public const float GlowScale = 3.2f;

        readonly Material _material;
        readonly Vector4[] _dots = new Vector4[Cap];
        readonly Vector3[] _screen = new Vector3[Cap];
        GraphicsBuffer _buffer;
        int _count;
        static readonly int DotsId = Shader.PropertyToID("_Dots"), PixelsId = Shader.PropertyToID("_DotPixels"),
                            MetresId = Shader.PropertyToID("_DotMetres"), MinPixelsId = Shader.PropertyToID("_MinDotPixels"),
                            ScreenPixelsId = Shader.PropertyToID("_ScreenPixels"), LiftId = Shader.PropertyToID("_DotLift"),
                            PullMaxId = Shader.PropertyToID("_PullMax"), PullShareId = Shader.PropertyToID("_PullShare"),
                            GlowId = Shader.PropertyToID("_GlowScale"), ExposureId = Shader.PropertyToID("_ExposureEv");

        public float UiScale = 1f;
        public bool Visible = true;
        /// <summary>The scene's post exposure (EV) this frame, taken back out of the dot's colour.</summary>
        public float ExposureEv;
        public int Count => _count;

        public GroomerLayer(Shader shader)
        {
            _material = shader != null ? new Material(shader) { hideFlags = HideFlags.HideAndDontSave } : null;
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Cap, 16);
        }

        /// <summary>Starts a frame's dots.</summary>
        public void Begin() => _count = 0;

        /// <summary>Adds a dot standing at <paramref name="at"/>; <paramref name="glow"/> 0–1 (headlights).</summary>
        public void Add(Vector3 at, bool hovered, float glow)
        {
            if (_count >= Cap) return;
            int g = Mathf.Clamp(Mathf.RoundToInt(glow * 255), 0, 255);
            _dots[_count++] = new Vector4(at.x, at.y, at.z, (hovered ? 1 : 0) | (g > 0 ? 2 : 0) | (g << 2));
        }

        /// <summary>Uploads the frame's dots and draws them (after <see cref="Add"/>), and keeps their screen points for picking.</summary>
        public void Draw(Camera camera)
        {
            if (camera == null) return;
            for (int i = 0; i < _count; i++)
            {
                var d = _dots[i];
                var s = camera.WorldToScreenPoint(new Vector3(d.x, d.y + DotLiftMetres, d.z));
                _screen[i] = s;
            }
            if (!Visible || _material == null || _count == 0) return;
            _buffer.SetData(_dots, 0, 0, _count);
            _material.SetBuffer(DotsId, _buffer);
            _material.SetFloat(PixelsId, DotPixels * UiScale);
            _material.SetFloat(MinPixelsId, MinDotPixels);
            _material.SetFloat(MetresId, DotMetres);
            _material.SetFloat(LiftId, DotLiftMetres);
            _material.SetFloat(PullMaxId, PullMaxMetres);
            _material.SetFloat(PullShareId, PullShare);
            _material.SetFloat(GlowId, GlowScale);
            _material.SetFloat(ExposureId, ExposureEv);
            _material.SetVector(ScreenPixelsId, new Vector4(camera.pixelWidth, camera.pixelHeight, 0, 0));
            var rp = new RenderParams(_material)
            {
                camera = camera,
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 100_000f),
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderPrimitives(rp, MeshTopology.Triangles, 6, _count);
        }

        /// <summary>The dot nearest <paramref name="pointer"/> within the hit radius (last frame's points; bottom-left pixels), or −1.</summary>
        public int Pick(Vector2 pointer)
        {
            if (!Visible) return -1;
            float r = Math.Max(HitPixels, DotPixels * 0.75f + 2f) * UiScale, best = r * r;
            int found = -1;
            for (int i = 0; i < _count; i++)
            {
                var s = _screen[i];
                if (s.z <= 0) continue;
                float dx = s.x - pointer.x, dy = s.y - pointer.y, d = dx * dx + dy * dy;
                if (d <= best) { best = d; found = i; }
            }
            return found;
        }

        /// <summary>Dot <paramref name="i"/>'s screen point last frame (bottom-left pixels, z the depth; z ≤ 0 behind the eye).</summary>
        public Vector3 ScreenOf(int i) => i >= 0 && i < _count ? _screen[i] : new Vector3(0, 0, -1);

        public void Dispose()
        {
            _buffer?.Release(); _buffer = null;
            if (_material != null) UnityEngine.Object.Destroy(_material);
        }
    }
}
