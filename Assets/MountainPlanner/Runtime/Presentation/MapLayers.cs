using Unity.Profiling;
using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The map layers (T17, task 12): Snow, Ground cover, Forest, Cover map and Imagery, switched by Shift+1–5,
    /// the HUD's layer rows and the F1 panel. Every switch only sets shader values or stops a draw, so it shows
    /// in the frame it's made: nothing re-reads the cache or re-uploads a splat. <see cref="Apply"/> runs inside
    /// the <see cref="ApplyMarker"/> profiler marker, which the acceptance test records.
    ///
    /// Snow off is the "under the snow" view: the ground, cliff ledges, diorama walls and trees lose their snow
    /// and lakes show bare ice. It is a view, not weather: the snow-depth state (task 10) is left alone.
    /// Ground cover is always on in this version, and Imagery is reserved.
    /// </summary>
    public sealed class MapLayers
    {
        public const string Snow = "snow", Ground = "ground", Forest = "forest", Cover = "cover", Imagery = "imagery";

        /// <summary>Wraps every layer change: one sample in the frame of a switch, none otherwise.</summary>
        public const string ApplyMarkerName = "MountainPlanner.Layers.Apply";
        public static readonly ProfilerMarker ApplyMarker = new ProfilerMarker(ApplyMarkerName);

        public bool SnowOn { get; private set; } = true;
        public bool ForestOn { get; private set; } = true;
        public bool CoverMapOn { get; private set; }
        /// <summary>The F1 panel's tree-snow switch (bare evergreens under a snowy ground); the Snow layer still wins.</summary>
        public bool TreeSnowOn { get; private set; } = true;

        GroundLayers _ground;
        Material _edge, _cliff;
        ForestView _forest;

        /// <summary>Whether a layer can be switched in this version (Ground cover is always on; Imagery is reserved).</summary>
        public static bool IsSwitchable(string id) => id == Snow || id == Forest || id == Cover;

        public bool IsOn(string id) => id switch
        {
            Snow => SnowOn,
            Ground => true,
            Forest => ForestOn,
            Cover => CoverMapOn,
            _ => false,
        };

        /// <summary>The resort's terrain layers and the materials whose snow follows the Snow layer (either may be null).</summary>
        public void Bind(GroundLayers ground, Material edge, Material cliff)
        {
            _ground = ground;
            _edge = edge;
            _cliff = cliff;
            Apply();
        }

        /// <summary>The forest arrives after the terrain (ResortOpener); it takes the current layers on arrival.</summary>
        public void BindForest(ForestView forest)
        {
            _forest = forest;
            Apply();
        }

        /// <summary>Switches a layer; false (and no change) for a layer that can't be switched in this version.</summary>
        public bool Set(string id, bool on)
        {
            switch (id)
            {
                case Snow: SnowOn = on; break;
                case Forest: ForestOn = on; break;
                case Cover: CoverMapOn = on; break;
                default: return false;
            }
            Apply();
            return true;
        }

        public bool Toggle(string id) => Set(id, !IsOn(id));

        public void SetTreeSnow(bool on)
        {
            TreeSnowOn = on;
            Apply();
        }

        /// <summary>Pushes the layers to the GPU-side state: a few material floats and a component switch.</summary>
        public void Apply()
        {
            using (ApplyMarker.Auto())
            {
                float snow = SnowOn ? 1 : 0;
                _ground?.SetSnow(SnowOn);
                _ground?.SetOverlay(CoverMapOn);
                if (_edge != null) _edge.SetFloat(SnowOnId, snow);
                if (_cliff != null) _cliff.SetFloat(SnowLoadId, snow);
                if (_forest != null)
                {
                    _forest.enabled = ForestOn;
                    var trees = _forest.Renderer;
                    float load = SnowOn && TreeSnowOn ? 1 : 0;
                    if (trees != null && trees.SnowLoad != load) trees.SetSnowLoad(load);
                }
            }
        }

        static readonly int SnowOnId = Shader.PropertyToID("_SnowOn");
        static readonly int SnowLoadId = Shader.PropertyToID("_SnowLoad");
    }
}
