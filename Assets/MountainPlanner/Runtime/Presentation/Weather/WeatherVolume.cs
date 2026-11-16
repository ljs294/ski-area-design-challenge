using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation.Weather
{
    /// <summary>How the weather's air is drawn: by each surface's shader (the Low look), or by one screen pass that can
    /// shadow it (god rays), and with <see cref="Full"/> march the cloud deck as a volume.</summary>
    public enum WeatherVolumeMode { Off, Rays, Full }

    /// <summary>
    /// The weather's air as one screen pass (task 4.06): WeatherVolume.shader marches it at half resolution after the sky,
    /// smooths its fixed step pattern and blends it over the picture in place (no new colour target, so MSAA isn't resolved
    /// early). Queued for every game camera while <see cref="Active"/>, since the surfaces' own fog is off then; copies the
    /// camera's depth buffer first (the game camera makes no depth texture), so the blend can test the buffer's stencil and
    /// skip what fogs itself (<see cref="KeptStencil"/>). Nothing allocates per frame.
    /// </summary>
    public sealed class WeatherVolume : System.IDisposable
    {
        public const string ShaderResource = "MountainPlannerWeather/WeatherVolume";
        /// <summary>Steps through the haze for the sun's shadow, by <see cref="WeatherVolumeMode"/> tier.</summary>
        public const int StepsMedium = 8, StepsHigh = 12;
        /// <summary>Steps through the marched cloud deck.</summary>
        public const int DeckStepsHigh = 20, DeckStepsUltra = 28;
        /// <summary>The stencil bit lifts, cables, skier figures and the design's lines write: the blend leaves those samples
        /// alone, and their own shaders fog them with a share of their colour kept (WeatherFogKeep). They draw after every
        /// other solid thing (queue AlphaTest+40 and up), so nothing drawn over one keeps its mark.</summary>
        public const int KeptStencil = 8;

        /// <summary>How each quality tier draws the air: Low by its surfaces (no pass), Medium with god rays over the flat
        /// clouds, High and Ultra with the marched deck too.</summary>
        public static WeatherVolumeMode ModeFor(QualityPreset preset) =>
            preset >= QualityPreset.High ? WeatherVolumeMode.Full : preset == QualityPreset.Medium ? WeatherVolumeMode.Rays : WeatherVolumeMode.Off;

        /// <summary>Reads <c>-wxvolume off|rays|full</c> (any case); nothing else, not a number.</summary>
        public static bool TryParseMode(string text, out WeatherVolumeMode mode)
        {
            mode = WeatherVolumeMode.Off;
            if (string.IsNullOrEmpty(text) || char.IsDigit(text[0]) || text[0] == '-' || text[0] == '+') return false;
            return System.Enum.TryParse(text, true, out mode) && System.Enum.IsDefined(typeof(WeatherVolumeMode), mode);
        }

        /// <summary>Where along its first step a low-resolution texel's ray starts (0-1): a fixed 4x4 pattern, so each of 16
        /// neighbours starts at a different sixteenth and the smooth pass averages them back (the shader's Pattern).</summary>
        public static float StepStart(int x, int y) => (Bayer[(x & 3) + (y & 3) * 4] + 0.5f) / 16;
        static readonly int[] Bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        readonly Material _material;
        readonly VolumePass _pass;

        /// <summary>Set by <see cref="WeatherView"/> each frame: something's in the air and the pass draws it.</summary>
        public bool Active { get; set; }
        public int Steps { get; set; } = StepsHigh;
        /// <summary>The sun's reach through the haze is stepped (else the air is drawn unshadowed, in one draw).</summary>
        public bool Rays { get; set; }
        /// <summary>The cloud deck is marched as a volume (<see cref="WeatherVolumeMode.Full"/>).</summary>
        public bool Deck { get; set; }
        /// <summary>Shows the sun's reach as grey instead of the weather (<c>-wxdebug</c>).</summary>
        public bool ShowReach { get; set; }
        public bool Available => _material != null;
        /// <summary>Frames the pass was queued (tests and the developer card).</summary>
        public int Queued { get; private set; }

        public WeatherVolume()
        {
            var shader = Resources.Load<Shader>(ShaderResource);
            if (shader == null || !shader.isSupported) { Debug.LogWarning("[WeatherVolume] The weather's screen pass isn't available; the surfaces draw the air."); return; }
            _material = CoreUtils.CreateEngineMaterial(shader);
            _pass = new VolumePass(_material) { renderPassEvent = RenderPassEvent.AfterRenderingSkybox };
            RenderPipelineManager.beginCameraRendering += Enqueue;
        }

        void Enqueue(ScriptableRenderContext context, Camera camera)
        {
            if (!Active || _material == null || camera.cameraType != CameraType.Game) return;
            var data = camera.GetUniversalAdditionalCameraData();
            if (data == null) return;
            _pass.Steps = Steps;
            _pass.Rays = Rays;
            _pass.Deck = Deck;
            _pass.Debug = ShowReach;
            data.scriptableRenderer.EnqueuePass(_pass);
            Queued++;
        }

        public void Dispose()
        {
            RenderPipelineManager.beginCameraRendering -= Enqueue;
            CoreUtils.Destroy(_material);
        }

        sealed class VolumePass : ScriptableRenderPass
        {
            static readonly int DepthId = Shader.PropertyToID("_WxVolumeDepth"), DepthMsId = Shader.PropertyToID("_WxVolumeDepthMS"),
                                DepthSizeId = Shader.PropertyToID("_WxVolumeDepthSize"), SizeId = Shader.PropertyToID("_WxVolumeSize"),
                                StepsId = Shader.PropertyToID("_WxVolumeSteps"), TexId = Shader.PropertyToID("_WxVolumeTex"),
                                DeckTexId = Shader.PropertyToID("_WxVolumeDeckTex"),
                                ScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
            const string MsaaKeyword = "_WX_DEPTH_MSAA";
            static readonly BaseRenderFunc<PassData, RasterGraphContext> DrawFunc = Draw;
            readonly Material _material;
            const int DepthPass = 4;
            // One block per draw: rays march, rays smooth, blend, deck march, deck smooth, depth copy.
            readonly MaterialPropertyBlock[] _blocks = { new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock(),
                                                         new MaterialPropertyBlock(), new MaterialPropertyBlock(), new MaterialPropertyBlock() };
            public int Steps;
            public bool Rays, Deck, Debug;

            public VolumePass(Material material)
            {
                _material = material;
                profilingSampler = new ProfilingSampler("Weather volume");
            }

            sealed class PassData
            {
                public TextureHandle Depth, Source, DeckSource;
                public Material Material;
                public Vector4 DepthSize, Size;
                public int Pass, Steps;
                public bool Msaa, Debug;
                public MaterialPropertyBlock Block;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                var resources = frame.Get<UniversalResourceData>();
                var depth = resources.activeDepthTexture;
                var colour = resources.activeColorTexture;
                if (resources.isActiveTargetBackBuffer || !depth.IsValid() || !colour.IsValid()) return;
                var depthDesc = graph.GetTextureDesc(depth);
                bool msaa = depthDesc.msaaSamples != MSAASamples.None && depthDesc.bindTextureMS;
                var depthSize = new Vector4(depthDesc.width, depthDesc.height, 0, 0);
                var desc = graph.GetTextureDesc(colour);
                desc.clearBuffer = false;
                desc.msaaSamples = MSAASamples.None;
                desc.bindTextureMS = false;
                desc.depthBufferBits = DepthBits.None;
                // The depth as a plain texture: the blend binds the real buffer for its stencil test, so it can't sample it.
                desc.name = "_WxVolumeDepthCopy";
                desc.width = depthDesc.width;
                desc.height = depthDesc.height;
                desc.format = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat;
                desc.filterMode = FilterMode.Point;
                var real = depth;
                var copy = graph.CreateTexture(desc);
                using (var builder = graph.AddRasterRenderPass<PassData>("Weather volume depth", out var data, profilingSampler))
                {
                    Fill(data, real, TextureHandle.nullHandle, msaa, depthSize, depthSize, DepthPass, 5);
                    builder.UseTexture(real);
                    builder.SetRenderAttachment(copy, 0);
                    builder.SetRenderFunc(DrawFunc);
                }
                depth = copy;
                desc.name = "_WxVolumeMarch";
                desc.width = Mathf.Max(1, desc.width / 2);
                desc.height = Mathf.Max(1, desc.height / 2);
                desc.format = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16_SFloat;
                desc.filterMode = FilterMode.Bilinear;
                var size = new Vector4(desc.width, desc.height, 1f / desc.width, 1f / desc.height);
                var reach = TextureHandle.nullHandle;
                if (Rays)
                {
                    var march = graph.CreateTexture(desc);
                    desc.name = "_WxVolumeSmooth";
                    reach = graph.CreateTexture(desc);
                    using (var builder = graph.AddRasterRenderPass<PassData>("Weather volume march", out var data, profilingSampler))
                    {
                        Fill(data, depth, TextureHandle.nullHandle, msaa, depthSize, size, 0);
                        builder.UseTexture(depth);
                        builder.UseAllGlobalTextures(true);
                        builder.SetRenderAttachment(march, 0);
                        builder.SetRenderFunc(DrawFunc);
                    }
                    using (var builder = graph.AddRasterRenderPass<PassData>("Weather volume smooth", out var data, profilingSampler))
                    {
                        Fill(data, depth, march, msaa, depthSize, size, 1);
                        builder.UseTexture(depth);
                        builder.UseTexture(march);
                        builder.SetRenderAttachment(reach, 0);
                        builder.SetRenderFunc(DrawFunc);
                    }
                }
                var deck = TextureHandle.nullHandle;
                if (Deck)
                {
                    desc.format = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                    desc.name = "_WxVolumeDeckMarch";
                    var march = graph.CreateTexture(desc);
                    desc.name = "_WxVolumeDeck";
                    deck = graph.CreateTexture(desc);
                    using (var builder = graph.AddRasterRenderPass<PassData>("Weather volume deck", out var data, profilingSampler))
                    {
                        Fill(data, depth, TextureHandle.nullHandle, msaa, depthSize, size, 3, 3);
                        builder.UseTexture(depth);
                        builder.UseAllGlobalTextures(true);
                        builder.SetRenderAttachment(march, 0);
                        builder.SetRenderFunc(DrawFunc);
                    }
                    using (var builder = graph.AddRasterRenderPass<PassData>("Weather volume deck smooth", out var data, profilingSampler))
                    {
                        Fill(data, depth, march, msaa, depthSize, size, 1, 4);
                        builder.UseTexture(depth);
                        builder.UseTexture(march);
                        builder.SetRenderAttachment(deck, 0);
                        builder.SetRenderFunc(DrawFunc);
                    }
                }
                using (var builder = graph.AddRasterRenderPass<PassData>("Weather volume blend", out var data, profilingSampler))
                {
                    Fill(data, depth, reach, msaa, depthSize, size, 2);
                    data.DeckSource = deck;
                    builder.UseTexture(depth);
                    if (reach.IsValid()) builder.UseTexture(reach);
                    if (deck.IsValid()) builder.UseTexture(deck);
                    builder.UseAllGlobalTextures(true);
                    builder.SetRenderAttachment(colour, 0);
                    builder.SetRenderAttachmentDepth(real, AccessFlags.Read);   // for the stencil test only
                    builder.SetRenderFunc(DrawFunc);
                }
            }

            void Fill(PassData data, TextureHandle depth, TextureHandle source, bool msaa, Vector4 depthSize, Vector4 size, int pass, int block = -1)
            {
                data.Depth = depth; data.Source = source; data.DeckSource = TextureHandle.nullHandle; data.Msaa = msaa; data.DepthSize = depthSize; data.Size = size;
                data.Material = _material; data.Pass = pass; data.Steps = Steps; data.Debug = Debug; data.Block = _blocks[block < 0 ? pass : block];
            }

            // Each draw carries its own property block: the graph runs them later, from one material. Only the depth copy
            // reads the camera's (maybe multisampled) buffer; the rest read the copy.
            static void Draw(PassData d, RasterGraphContext ctx)
            {
                var block = d.Block;
                if (d.Msaa) d.Material.EnableKeyword(MsaaKeyword); else d.Material.DisableKeyword(MsaaKeyword);
                block.SetTexture(d.Msaa && d.Pass == DepthPass ? DepthMsId : DepthId, (RTHandle)d.Depth);
                block.SetVector(DepthSizeId, d.DepthSize);
                block.SetVector(SizeId, d.Size);
                block.SetVector(StepsId, new Vector4(d.Steps, d.Debug ? 1 : 0, 0, 0));
                block.SetVector(ScaleBiasId, new Vector4(1, 1, 0, 0));
                if (d.Pass == 1 || d.Pass == 2) block.SetTexture(TexId, d.Source.IsValid() ? (RTHandle)d.Source : (Texture)Texture2D.whiteTexture);
                if (d.Pass == 2) block.SetTexture(DeckTexId, d.DeckSource.IsValid() ? (RTHandle)d.DeckSource : (Texture)Texture2D.whiteTexture);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.Material, d.Pass, MeshTopology.Triangles, 3, 1, block);
            }
        }
    }
}
