using System.Collections.Generic;
using MountainPlanner.Domain.Geo;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Edits
{
    /// <summary>
    /// Draws a pending edit's changed contours in yellow (CF-08): one line mesh, rebuilt when the preview changes and
    /// cleared on cancel and on commit. Nothing per frame.
    /// </summary>
    public sealed class PendingContourView : System.IDisposable
    {
        public const string ShaderResource = "MountainPlannerEdits/PendingContours";
        /// <summary>Lines float this far above their level, so the terrain never hides them.</summary>
        public const float Lift = 0.15f;

        readonly GameObject _go;
        readonly Mesh _mesh;
        readonly Material _material;
        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<int> _indices = new List<int>();

        public PendingContourView(Transform parent)
        {
            _go = new GameObject("Pending contours");
            _go.transform.SetParent(parent, false);
            _mesh = new Mesh { name = "Pending contours", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var shader = Resources.Load<Shader>(ShaderResource);
            _material = new Material(shader != null ? shader : Shader.Find("Hidden/InternalErrorShader")) { name = "Pending contours" };
            var renderer = _go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _go.SetActive(false);
        }

        public int Segments { get; private set; }

        /// <summary>Shows the segments (<see cref="Domain.Edits.PendingContours.Build"/>'s quintuples, package metres) in the local frame.</summary>
        public void Show(List<double> segments, LocalFrame frame)
        {
            _vertices.Clear();
            _indices.Clear();
            for (int s = 0; s + 4 < segments.Count; s += 5)
            {
                var (x0, z0) = frame.ToLocal(new AlbersPoint(segments[s], segments[s + 1]));
                var (x1, z1) = frame.ToLocal(new AlbersPoint(segments[s + 2], segments[s + 3]));
                float y = (float)segments[s + 4] + Lift;
                _indices.Add(_vertices.Count);
                _vertices.Add(new Vector3((float)x0, y, (float)z0));
                _indices.Add(_vertices.Count);
                _vertices.Add(new Vector3((float)x1, y, (float)z1));
            }
            Segments = _indices.Count / 2;
            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetIndices(_indices, MeshTopology.Lines, 0, true);
            _go.SetActive(Segments > 0);
        }

        public void Clear()
        {
            Segments = 0;
            _mesh.Clear();
            _go.SetActive(false);
        }

        public void Dispose()
        {
            if (_go != null) Object.Destroy(_go);
            Object.Destroy(_mesh);
            Object.Destroy(_material);
        }
    }
}
