using MountainPlanner.Domain.Measure;
using Unity.Profiling;
using UnityEngine;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// What the map shows (T17; tasks 12 and 12b, owner 2026-10-02), in two groups:
    /// - **Map layers** show or hide physical things: <see cref="Snow"/> and <see cref="Trees"/> (lifts and the rest
    ///   join as they're built). Any combination.
    /// - **Info layers** only display information: <see cref="SlopeAngle"/>, <see cref="Exposure"/> and
    ///   <see cref="SnowDepth"/> recolour the ground, one at a time (<see cref="Info"/>); <see cref="SnowConditions"/>
    ///   is reserved for the snow simulation; <see cref="Contours"/> draw over anything.
    /// The cover map (task 07) is a developer view in the F1 panel; it takes the same slot as the info layers.
    ///
    /// Keys, the HUD's rows and the F1 panel all switch through here. A switch only sets shader values or stops a
    /// draw, so it shows in the frame it's made: nothing re-reads the cache or re-uploads a splat. <see cref="Apply"/>
    /// runs inside the <see cref="ApplyMarker"/> profiler marker, which the acceptance tests record.
    ///
    /// Snow off is the "under the snow" view: the ground, cliff ledges, diorama walls and trees lose their snow and
    /// lakes show bare ice. It is a view, not weather: the snow-depth state (task 10) is left alone.
    /// </summary>
    public sealed class MapLayers
    {
        public const string Snow = "snow", Trees = "trees";
        public const string SlopeAngle = "slope", Exposure = "exposure", SnowDepth = "depth", SnowConditions = "conditions", Contours = "contours";
        public const string CoverMap = "cover";

        /// <summary>What recolours the ground: nothing, one info layer, or the developer's cover map. The shader reads the number.</summary>
        public enum InfoView { None = 0, CoverMap = 1, SlopeAngle = 2, Exposure = 3, SnowDepth = 4 }

        public const string ApplyMarkerName = "MountainPlanner.Layers.Apply";
        /// <summary>Wraps every layer change: one sample in the frame of a switch, none otherwise.</summary>
        public static readonly ProfilerMarker ApplyMarker = new ProfilerMarker(ApplyMarkerName);

        public bool SnowOn { get; private set; } = true;
        public bool TreesOn { get; private set; } = true;
        public bool ContoursOn { get; private set; }
        public InfoView Info { get; private set; }
        public bool CoverMapOn => Info == InfoView.CoverMap;
        /// <summary>The info layer that's on, as its id, or null (none, or the developer's cover map).</summary>
        public string InfoLayerId => Info switch
        {
            InfoView.SlopeAngle => SlopeAngle,
            InfoView.Exposure => Exposure,
            InfoView.SnowDepth => SnowDepth,
            _ => null,
        };
        /// <summary>The F1 panel's tree-snow switch (bare evergreens under a snowy ground); the Snow layer still wins.</summary>
        public bool TreeSnowOn { get; private set; } = true;

        GroundLayers _ground;
        Material _edge, _cliff;
        ForestView _forest;

        /// <summary>The map layers, in the HUD's order.</summary>
        public static readonly string[] MapIds = { Snow, Trees };
        /// <summary>The info layers, in the HUD's order.</summary>
        public static readonly string[] InfoIds = { SlopeAngle, Exposure, SnowDepth, SnowConditions, Contours };

        /// <summary>Whether a layer can be switched in this version (Snow conditions waits for the snow simulation).</summary>
        public static bool IsSwitchable(string id) => id == Snow || id == Trees || id == Contours || id == CoverMap || ViewOf(id) != InfoView.None;

        /// <summary>The info layers that take turns; Contours combine with any of them.</summary>
        public static bool IsExclusive(string id) => id == SnowConditions || ViewOf(id) != InfoView.None;

        static InfoView ViewOf(string id) => id switch
        {
            SlopeAngle => InfoView.SlopeAngle,
            Exposure => InfoView.Exposure,
            SnowDepth => InfoView.SnowDepth,
            CoverMap => InfoView.CoverMap,
            _ => InfoView.None,
        };

        public bool IsOn(string id) => id switch
        {
            Snow => SnowOn,
            Trees => TreesOn,
            Contours => ContoursOn,
            SnowConditions => false,
            _ => ViewOf(id) != InfoView.None && Info == ViewOf(id),
        };

        /// <summary>
        /// The resort's terrain layers and the materials whose snow follows the Snow layer (either may be null), and
        /// the grid convergence (degrees from grid north clockwise to true north) for the Exposure layer.
        /// </summary>
        public void Bind(GroundLayers ground, Material edge, Material cliff, float gridConvergenceDegrees = 0)
        {
            _ground = ground;
            _edge = edge;
            _cliff = cliff;
            Shader.SetGlobalFloat(ConvergenceId, gridConvergenceDegrees * Mathf.Deg2Rad);
            Apply();
        }

        /// <summary>The forest arrives after the terrain (ResortOpener); it takes the current layers on arrival.</summary>
        public void BindForest(ForestView forest)
        {
            _forest = forest;
            Apply();
        }

        /// <summary>
        /// Switches a layer; false (and no change) for one that can't be switched in this version. Turning on an
        /// info layer (or the cover map) turns off the one that was on.
        /// </summary>
        public bool Set(string id, bool on)
        {
            switch (id)
            {
                case Snow: SnowOn = on; break;
                case Trees: TreesOn = on; break;
                case Contours: ContoursOn = on; break;
                default:
                    var view = ViewOf(id);
                    if (view == InfoView.None) return false;
                    if (on) Info = view;
                    else if (Info == view) Info = InfoView.None;
                    break;
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

        /// <summary>Pushes the layers to the GPU-side state: a few shader values and a component switch.</summary>
        public void Apply()
        {
            using (ApplyMarker.Auto())
            {
                float snow = SnowOn ? 1 : 0;
                _ground?.SetSnow(SnowOn);
                _ground?.SetOverlay(Info == InfoView.CoverMap);
                Shader.SetGlobalFloat(InfoViewId, Info == InfoView.CoverMap ? 0 : (float)Info);
                Shader.SetGlobalFloat(ContoursId, ContoursOn ? 1 : 0);
                ApplyUnits(DisplayUnits.Current);
                if (_edge != null) _edge.SetFloat(SnowOnId, snow);
                if (_cliff != null) _cliff.SetFloat(SnowLoadId, snow);
                if (_forest != null)
                {
                    _forest.enabled = TreesOn;
                    var trees = _forest.Renderer;
                    float load = SnowOn && TreeSnowOn ? 1 : 0;
                    if (trees != null && trees.SnowLoad != load) trees.SetSnowLoad(load);
                }
            }
        }

        /// <summary>The contour interval and the snow-depth colour stops follow the player's units (task 12b.2).</summary>
        static void ApplyUnits(UnitSystem units)
        {
            Shader.SetGlobalFloat(ContourIntervalId, (float)UnitFormat.ContourIntervalMetres(units));
            double[] stops = units == UnitSystem.Imperial ? ImperialStops : MetricStops;
            for (int k = 0; k < Stops.Length; k++) Stops[k] = (float)stops[k];
            Shader.SetGlobalFloatArray(SnowDepthStopsId, Stops);
        }

        static readonly double[] ImperialStops = UnitFormat.SnowDepthStops(UnitSystem.Imperial), MetricStops = UnitFormat.SnowDepthStops(UnitSystem.Metric);
        static readonly float[] Stops = new float[6];
        static readonly int ContourIntervalId = Shader.PropertyToID("_MP_ContourInterval");
        static readonly int SnowDepthStopsId = Shader.PropertyToID("_MP_SnowDepthStops");
        static readonly int SnowOnId = Shader.PropertyToID("_SnowOn");
        static readonly int SnowLoadId = Shader.PropertyToID("_SnowLoad");
        /// <summary>InfoLayers.hlsl: 0 none, 2 slope angle, 3 exposure, 4 snow depth (the cover map has its own switch).</summary>
        static readonly int InfoViewId = Shader.PropertyToID("_MP_InfoView");
        static readonly int ContoursId = Shader.PropertyToID("_MP_Contours");
        static readonly int ConvergenceId = Shader.PropertyToID("_MP_GridConvergence");
    }
}
