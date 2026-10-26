using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// The hovered lift's glow (task 09, LT23): a white casing round the whole lift's silhouette with a soft halo. Every
    /// renderer of the lit lift carries <see cref="Layer"/> in its rendering-layer mask (its chairs through their
    /// RenderParams); this pass draws them again with LiftOutline.shader's halo and casing shells after the transparents,
    /// depth-tested against the scene. Queued only while something is lit (<see cref="Lit"/>), so it costs nothing otherwise.
    /// </summary>
    public sealed class LiftOutline : System.IDisposable
    {
        /// <summary>The rendering-layer bit the lit lift's renderers carry (high, clear of the light layers).</summary>
        public const uint Layer = 1u << 25;
        public const string ShaderResource = "MountainPlannerLifts/LiftOutline";

        readonly Camera _camera;
        readonly Material _material;
        readonly OutlinePass _pass;

        /// <summary>Set while a lift is lit.</summary>
        public bool Lit { get; set; }

        public LiftOutline(Camera camera)
        {
            _camera = camera;
            var shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null || !shader.isSupported) { Debug.LogWarning("[LiftOutline] The outline shader isn't available; lifts won't glow."); return; }
            _material = CoreUtils.CreateEngineMaterial(shader);
            _material.enableInstancing = true;
            _pass = new OutlinePass(_material) { renderPassEvent = RenderPassEvent.BeforeRenderingTransparents };   // under the map lines and the ghost
            RenderPipelineManager.beginCameraRendering += Enqueue;
        }

        void Enqueue(ScriptableRenderContext context, Camera camera)
        {
            if (!Lit || camera != _camera || _material == null) return;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data == null) return;
            data.scriptableRenderer.EnqueuePass(_pass);
        }

        public void Dispose()
        {
            RenderPipelineManager.beginCameraRendering -= Enqueue;
            CoreUtils.Destroy(_material);
        }

        sealed class OutlinePass : ScriptableRenderPass
        {
            static readonly List<ShaderTagId> Tags = new List<ShaderTagId>
            {
                new ShaderTagId("UniversalForward"), new ShaderTagId("SRPDefaultUnlit"), new ShaderTagId("UniversalForwardOnly"),
            };
            static readonly BaseRenderFunc<PassData, RasterGraphContext> Draw = (d, ctx) =>
            {
                ctx.cmd.DrawRendererList(d.Halo);
                ctx.cmd.DrawRendererList(d.Rim);
                ctx.cmd.DrawRendererList(d.Casing);
            };

            readonly Material _material;

            public OutlinePass(Material material)
            {
                _material = material;
                profilingSampler = new ProfilingSampler("Lift outline");
            }

            sealed class PassData
            {
                public RendererListHandle Halo, Rim, Casing;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                var resources = frame.Get<UniversalResourceData>();
                var rendering = frame.Get<UniversalRenderingData>();
                var cameraData = frame.Get<UniversalCameraData>();
                var lights = frame.Get<UniversalLightData>();
                if (!resources.activeColorTexture.IsValid() || !resources.activeDepthTexture.IsValid()) return;
                var filter = new FilteringSettings(RenderQueueRange.all, -1, Layer);
                RendererListHandle List(int pass)
                {
                    var drawing = RenderingUtils.CreateDrawingSettings(Tags, rendering, cameraData, lights, SortingCriteria.CommonOpaque);
                    drawing.overrideMaterial = _material;
                    drawing.overrideMaterialPassIndex = pass;
                    return graph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing, filter));
                }
                using (var builder = graph.AddRasterRenderPass<PassData>("Lift outline", out var data, profilingSampler))
                {
                    data.Halo = List(0);
                    data.Rim = List(1);
                    data.Casing = List(2);
                    builder.UseRendererList(data.Halo);
                    builder.UseRendererList(data.Rim);
                    builder.UseRendererList(data.Casing);
                    builder.SetRenderAttachment(resources.activeColorTexture, 0);
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                    builder.SetRenderFunc(Draw);
                }
            }
        }
    }
}
