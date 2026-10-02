using MountainPlanner.Domain.Cover;
using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The terrain's six layers (0.5 §2): snow, then the five ground layers under it. Snow is terrain
    /// layer 0 so a tile reads as snow the moment it appears (Unity's default splat is all layer 0),
    /// before its ground cover streams in (task 07). The textures are
    /// procedural placeholders in the art-direction palette until the style tile (task 08) paints them.
    ///
    /// The cover overlay (task 07) and the snow (map layers, task 12) are switches on the resort's terrain
    /// material: the splat holds the snow and the ground under it, and the shader composes them, so a switch
    /// shows in the frame it's made with nothing re-uploaded.
    /// </summary>
    public sealed class GroundLayers
    {
        public const int Count = GroundCover.Layers + 1;
        public const int Snow = GroundCover.Layers;

        /// <summary>Base colours (0.5 §2 and §5): forest floor, grass, rock, developed, water, snow.</summary>
        static readonly Color[] Base =
        {
            new Color(0.23f, 0.18f, 0.12f),   // forest floor: dark umber needles and soil
            new Color(0.55f, 0.53f, 0.36f),   // grass / meadow: muted, winter-dormant straw green
            new Color(0.47f, 0.49f, 0.52f),   // rock / alpine: cool grey granite
            new Color(0.45f, 0.42f, 0.38f),   // developed: neutral grey-brown
            new Color(0.62f, 0.72f, 0.76f),   // water: frozen, blue-grey ice (iteration 1)
            new Color(0.957f, 0.969f, 0.980f),// snow lit, #F4F7FA
        };

        /// <summary>Flat overlay colours; the same as `acquire cover-map` draws.</summary>
        public static readonly Color[] Overlay =
        {
            new Color32(40, 74, 52, 255), new Color32(178, 170, 98, 255), new Color32(132, 134, 140, 255),
            new Color32(196, 88, 64, 255), new Color32(48, 110, 196, 255), new Color32(245, 248, 252, 255),
        };

        static readonly float[] TileSizes = { 4, 5, 8, 4, 10, 6 };
        static readonly float[] Smoothness = { 0.05f, 0.08f, 0.2f, 0.15f, 0.5f, 0.35f };
        static readonly string[] Names = { "Forest floor", "Grass", "Rock", "Developed", "Water", "Snow" };

        public readonly TerrainLayer[] Layers = new TerrainLayer[Count];
        readonly Texture2D[] _textures = new Texture2D[Count];
        readonly Texture2D[] _flat = new Texture2D[Count];
        public bool OverlayOn { get; private set; }
        public bool SnowOn { get; private set; } = true;

        public GroundLayers()
        {
            for (int k = 0; k < Count; k++)
            {
                _textures[k] = Noisy(Names[k], Base[k], k == Snow ? 0.06f : 0.16f, (uint)(k * 7919 + 17), Smoothness[k]);
                _flat[k] = Flat(Names[k] + " (overlay)", Overlay[k]);
                Layers[Slot(k)] = new TerrainLayer
                {
                    name = Names[k], diffuseTexture = _textures[k], tileSize = new Vector2(TileSizes[k], TileSizes[k]), smoothness = Smoothness[k],
                };
            }
        }

        /// <summary>
        /// Metres per texture repeat and smoothness per terrain slot (snow, forest floor, grass, rock, developed, ice). The
        /// photo layers (task 12c) repeat at about twice their real size; developed is an aerial texture of 30 m.
        /// </summary>
        static readonly float[] SlotTile = { 12, 3, 4, 6, 30, 20 };
        static readonly float[] SlotSmooth = { 0.35f, 0.05f, 0.08f, 0.2f, 0.15f, 0.6f };

        /// <summary>The terrain material this resort draws with (the mountain terrain shader), if any.</summary>
        public Material Material { get; private set; }

        /// <summary>
        /// Sets up a resort's copy of the mountain terrain shader: per-layer tiling and smoothness, the
        /// overlay colours, and the edge of the downloaded data (local frame) where the terrain is cut.
        /// </summary>
        public void Configure(Material material, Rect ring)
        {
            Material = material;
            var tile = new float[Count];
            var smooth = new float[Count];
            var overlay = new Vector4[Count];
            for (int k = 0; k < Count; k++)
            {
                int slot = Slot(k);
                tile[slot] = SlotTile[slot];
                smooth[slot] = SlotSmooth[slot];
                overlay[slot] = Overlay[k];
            }
            material.SetFloatArray("_Tile", tile);
            material.SetFloatArray("_Smooth", smooth);
            material.SetVectorArray("_OverlayColor", overlay);
            material.SetVector("_RingBounds", new Vector4(ring.xMin, ring.yMin, ring.xMax, ring.yMax));
            material.SetFloat(OverlayId, OverlayOn ? 1 : 0);
            material.SetFloat(SnowOnId, SnowOn ? 1 : 0);
        }

        /// <summary>The terrain layer slot of cover layer k (0-4 ground in GroundLayer order, 5 = snow).</summary>
        public static int Slot(int k) => k == Snow ? 0 : k + 1;

        /// <summary>
        /// Where valley grass gives way to alpine meadow (task 12c): from 45% to 70% of the way up the site's elevation
        /// range, a stand-in until the forest's treeline is shared with the renderer.
        /// </summary>
        public void SetElevationRange(float lowest, float highest)
        {
            if (Material == null) return;
            float span = highest - lowest;
            Material.SetVector(AlpineBandId, new Vector4(lowest + 0.45f * span, lowest + 0.7f * span, 0, 0));
        }

        static readonly int AlpineBandId = Shader.PropertyToID("_AlpineBand");

        /// <summary>
        /// The season's colour on grass and meadow (task 12c's hook for the seasons task): an rgb multiplier on the
        /// summer-olive grass, white for none (e.g. gold in autumn, straw in winter). A shader global, so one call
        /// covers every resort; instant, no texture change.
        /// </summary>
        public static void SetGrassTint(Color tint) => Shader.SetGlobalVector(GrassTintId, new Vector4(tint.r, tint.g, tint.b, 1));

        static readonly int GrassTintId = Shader.PropertyToID("_GrassTint");

        static readonly int OverlayId = Shader.PropertyToID("_Overlay");
        static readonly int SnowOnId = Shader.PropertyToID("_SnowOn");

        /// <summary>The cover-map overlay: flat class colours, with the snow left out whatever the Snow layer says.</summary>
        public void SetOverlay(bool on)
        {
            if (on == OverlayOn) return;
            OverlayOn = on;
            if (Material != null)
            {
                Material.SetFloat(OverlayId, on ? 1 : 0);
                return;
            }
            // Without the mountain shader (tests, fallback) Unity's terrain shader draws the layers' own textures.
            for (int k = 0; k < Count; k++)
            {
                Layers[Slot(k)].diffuseTexture = on ? _flat[k] : _textures[k];
                Layers[Slot(k)].smoothness = on ? 0 : Smoothness[k];
            }
        }

        /// <summary>The snow on the ground (lakes: snow on ice, or bare ice); the ground cover under it shows when it's off.</summary>
        public void SetSnow(bool on)
        {
            SnowOn = on;
            if (Material != null) Material.SetFloat(SnowOnId, on ? 1 : 0);
        }

        /// <summary>URP terrain reads a layer texture's alpha as smoothness, so alpha carries the layer's smoothness.</summary>
        static Texture2D Noisy(string name, Color colour, float amount, uint seed, float smoothness)
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Two octaves of tileable value noise from a keyed hash (no unkeyed randomness).
                    float n = 0.65f * Value(x, y, 32, seed) + 0.35f * Value(x, y, 8, seed + 1);
                    float f = 1 + amount * (2 * n - 1);
                    pixels[y * size + x] = (Color32)new Color(colour.r * f, colour.g * f, colour.b * f, smoothness);
                }
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        static float Value(int x, int y, int cell, uint seed)
        {
            int cells = 256 / cell;
            float fx = (float)x / cell, fy = (float)y / cell;
            int x0 = (int)fx, y0 = (int)fy;
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3 - 2 * tx);
            ty = ty * ty * (3 - 2 * ty);
            float H(int i, int j) => (float)((CoverNoise.Lattice(seed, i % cells, j % cells) + 1) * 0.5);
            float top = Mathf.Lerp(H(x0, y0), H(x0 + 1, y0), tx), bottom = Mathf.Lerp(H(x0, y0 + 1), H(x0 + 1, y0 + 1), tx);
            return Mathf.Lerp(top, bottom, ty);
        }

        static Texture2D Flat(string name, Color colour)
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Repeat };
            var pixels = new Color32[16];
            var matte = (Color32)new Color(colour.r, colour.g, colour.b, 0);
            for (int i = 0; i < 16; i++) pixels[i] = matte;
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
