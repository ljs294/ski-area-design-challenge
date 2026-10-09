using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation
{
    /// <summary>Photo mode's depth of field (task P2-07; owner: Off, Natural, Miniature).</summary>
    public enum PhotoFocusMode { Off, Natural, Miniature }

    /// <summary>
    /// Photo mode's depth of field (S10, task P2-07): a depth-aware disc blur (PhotoFocus.shader) injected into the
    /// game camera's URP renderer before post processing, so the grade applies after it. URP's own Bokeh is a physical
    /// lens and can't blur at mountain distances (under a pixel at 3 km, even at 300 mm f/1), so the presets set a look:
    /// <list type="bullet">
    /// <item><b>Natural</b>, a long lens: the far slopes soften a little, and the foreground;</item>
    /// <item><b>Miniature</b>, tilt-shift: a narrow sharp band at the focus, strong blur in front and behind.</item>
    /// </list>
    /// The blur is a fraction of the picture's height, so a 2× photo looks like the screen. Off costs nothing: the pass
    /// isn't queued. Focus sits on the ground at the middle of the picture (the viewer sets <see cref="FocusDistance"/>).
    /// </summary>
    public sealed class PhotoFocus : System.IDisposable
    {
        /// <summary>How each preset blurs: strength per unit of (1 − focus/depth), and the largest radius as a fraction of the height.</summary>
        public static Vector2 Preset(PhotoFocusMode mode) =>
            mode == PhotoFocusMode.Natural ? new Vector2(0.9f, 0.005f)
            : mode == PhotoFocusMode.Miniature ? new Vector2(3.0f, 0.012f)
            : Vector2.zero;

        public const string ShaderResource = "MountainPlannerPhoto/PhotoFocus";

        readonly Camera _camera;
        readonly Material _material;
        readonly FocusPass _pass;

        public PhotoFocusMode Mode { get; private set; }
        /// <summary>Metres from the camera to the sharp plane.</summary>
        public float FocusDistance { get; set; } = 1500;

        public PhotoFocus(Camera camera)
        {
            _camera = camera;
            var shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null || !shader.isSupported) { Debug.LogWarning("[PhotoFocus] The focus shader isn't available; depth of field is off."); return; }
            _material = CoreUtils.CreateEngineMaterial(shader);
            _pass = new FocusPass(_material) { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
            _pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            RenderPipelineManager.beginCameraRendering += Enqueue;
        }

        public bool Available => _material != null;

        public void SetMode(PhotoFocusMode mode) => Mode = Available ? mode : PhotoFocusMode.Off;

        void Enqueue(ScriptableRenderContext context, Camera camera)
        {
            if (Mode == PhotoFocusMode.Off || camera != _camera || _material == null) return;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data == null || !data.renderPostProcessing) return;
            var p = Preset(Mode);
            _pass.Params = new Vector4(Mathf.Max(1f, FocusDistance), p.x, p.y, camera.pixelHeight / (float)Mathf.Max(1, camera.pixelWidth));
            data.scriptableRenderer.EnqueuePass(_pass);
        }

        public void Dispose()
        {
            RenderPipelineManager.beginCameraRendering -= Enqueue;
            CoreUtils.Destroy(_material);
        }

        sealed class FocusPass : ScriptableRenderPass
        {
            static readonly int ParamsId = Shader.PropertyToID("_PhotoFocus"), BlurId = Shader.PropertyToID("_PhotoFocusBlur");
            readonly Material _material;
            public Vector4 Params;

            public FocusPass(Material material)
            {
                _material = material;
                profilingSampler = new ProfilingSampler("Photo focus");
            }

            sealed class PassData
            {
                public TextureHandle Source, Blur;
                public Material Material;
                public Vector4 Params;
                public int Pass;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                var resources = frame.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid()) return;
                var source = resources.activeColorTexture;
                var desc = graph.GetTextureDesc(source);
                desc.name = "_PhotoFocusHalf";
                desc.width = Mathf.Max(1, desc.width / 2);
                desc.height = Mathf.Max(1, desc.height / 2);
                desc.clearBuffer = false;
                desc.filterMode = FilterMode.Bilinear;
                desc.msaaSamples = MSAASamples.None;
                desc.depthBufferBits = DepthBits.None;
                var half = graph.CreateTexture(desc);
                var full = graph.GetTextureDesc(source);
                full.name = "_PhotoFocusOut";
                full.clearBuffer = false;
                var output = graph.CreateTexture(full);

                using (var builder = graph.AddRasterRenderPass<PassData>("Photo focus blur", out var data, profilingSampler))
                {
                    data.Source = source; data.Material = _material; data.Params = Params; data.Pass = 0;
                    builder.UseTexture(source);
                    builder.UseTexture(resources.cameraDepthTexture);
                    builder.SetRenderAttachment(half, 0);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) => Draw(d, ctx));
                }
                using (var builder = graph.AddRasterRenderPass<PassData>("Photo focus mix", out var data, profilingSampler))
                {
                    data.Source = source; data.Blur = half; data.Material = _material; data.Params = Params; data.Pass = 1;
                    builder.UseTexture(source);
                    builder.UseTexture(half);
                    builder.UseTexture(resources.cameraDepthTexture);
                    builder.SetRenderAttachment(output, 0);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) => Draw(d, ctx));
                }
                resources.cameraColor = output;
            }

            static void Draw(PassData d, RasterGraphContext ctx)
            {
                d.Material.SetVector(ParamsId, d.Params);
                if (d.Pass == 1) d.Material.SetTexture(BlurId, (RTHandle)d.Blur);
                Blitter.BlitTexture(ctx.cmd, d.Source, new Vector4(1, 1, 0, 0), d.Material, d.Pass);
            }
        }
    }
}
