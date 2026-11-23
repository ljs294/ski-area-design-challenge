using System.Threading.Tasks;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.07 B1 part 2a: the trails' fine snow layout goes to the worker (<see cref="SimCommandKind.SetTrailSnow"/>) once
    /// its snow is live, and again whenever the design it skis on changes the layout. Built on a background task from the
    /// built trails, the network sent with the design and the weather's grid; sent when it differs from the last one sent.
    /// </summary>
    public sealed partial class MountainViewer
    {
        SimNetwork _trailSnowNet;
        DesignDocument _trailSnowDoc;
        bool _trailSnowDirty;
        Task<TrailSnowLayout> _trailSnowJob;
        TrailSnowLayout _trailSnowReady;
        /// <summary>Task 4.08 PR A: the layout last sent, for the fine atlas (bound when its tiles come in).</summary>
        TrailSnowLayout _trailSnowSent;
        ulong _trailSnowSentHash;

        /// <summary>The design just sent to the worker (with its network): the layout follows it.</summary>
        void NoteTrailSnow(SimDesign design, DesignDocument doc)
        {
            _trailSnowNet = design?.Network;
            _trailSnowDoc = doc;
            _trailSnowDirty = true;
        }

        // Task 4.07 B1 part 2c: a reopened save's trail snow waits for the layout, so the clock does too (the layout goes in the
        // same queue ahead of the release, so the first tick already has it). A failed layout or a minute releases it.
        const float TrailSnowWaitSeconds = 60;
        bool _trailSnowWait, _trailSnowBackSaid;
        float _trailSnowWaitUntil, _trailSnowWaitFrom;

        void HoldForTrailSnow(bool waiting)
        {
            _trailSnowWait = waiting;
            _trailSnowBackSaid = !waiting;
            _trailSnowWaitFrom = Time.unscaledTime;
            _trailSnowWaitUntil = Time.unscaledTime + TrailSnowWaitSeconds;
        }

        /// <summary>The clock still waits for the save's trail layout.</summary>
        bool TrailSnowMissing
        {
            get
            {
                if (!_trailSnowWait || _trailSnowSentHash != 0) return false;
                if (Time.unscaledTime < _trailSnowWaitUntil) return true;
                Debug.LogWarning($"[TrailSnow] the trail layout didn't come in {TrailSnowWaitSeconds:F0} s; the clock starts without it (the save's trail snow waits)");
                _trailSnowWait = false;
                return false;
            }
        }

        /// <summary>Once, when the worker has taken a reopened save's trail snow back.</summary>
        void SayTrailSnowBack(SimSnapshot s)
        {
            if (_trailSnowBackSaid || _trailSnowSentHash == 0 || s.TrailSnowWaiting || s.TrailFineCells == 0) return;
            _trailSnowBackSaid = true;
            Debug.Log($"[TrailSnow] trail snow came back: {s.TrailBlocksRestored} squares as saved, {s.TrailBlocksReseeded} seeded again " +
                      $"(they didn't add up), {s.TrailBlocks - s.TrailBlocksRestored - s.TrailBlocksReseeded} new; the layout came after " +
                      $"{Time.unscaledTime - _trailSnowWaitFrom:F1} s");
        }

        /// <summary>A new world: nothing has been sent to it.</summary>
        void ResetTrailSnow()
        {
            _trailSnowSentHash = 0;
            _trailSnowReady = null;
            _trailSnowDirty = true;
        }

        /// <summary>Per frame, after the snow's inputs.</summary>
        void SendTrailSnow()
        {
            if (_sim == null || !_simSnowTerrainSent) return;
            if (_trailSnowJob != null)
            {
                if (!_trailSnowJob.IsCompleted) return;
                if (_trailSnowJob.Status == TaskStatus.RanToCompletion) _trailSnowReady = _trailSnowJob.Result;
                else
                {
                    Debug.LogWarning($"[TrailSnow] the layout failed: {_trailSnowJob.Exception?.GetBaseException().Message}");
                    _trailSnowWait = false;   // the clock isn't left waiting for it
                }
                _trailSnowJob = null;
            }
            if (_trailSnowReady != null)
            {
                ulong hash = _trailSnowReady.Hash();
                if (hash == _trailSnowSentHash) _trailSnowReady = null;
                else if (_sim.Send(SimCommandKind.SetTrailSnow, 0, _trailSnowReady))
                {
                    Debug.Log($"[TrailSnow] {_trailSnowReady.Trails} trails, {_trailSnowReady.Blocks} snow squares at {_trailSnowReady.FineMetres} m " +
                              $"({_trailSnowReady.FineCells} fine cells) to the worker (hash {hash:X16})");
                    _trailSnowSentHash = hash;
                    _trailSnowSent = _trailSnowReady;
                    _trailSnowReady = null;
                }
                return;
            }
            var wx = _resort?.WeatherTerrain;
            var geo = _netGeometry;
            if (!_trailSnowDirty || _trailSnowNet == null || _trailSnowDoc == null || wx == null || geo == null) return;
            _trailSnowDirty = false;
            _trailSnowJob = BuildTrailSnow(_trailSnowDoc, _trailSnowNet, geo, wx, Pacing.Default.FineCellCap);
        }

        /// <summary>
        /// Task 4.26 BB22: the layout's job, started in its own method. A lambda's captured locals live in one object the
        /// compiler makes where they're declared, so with the lambda in <see cref="SendTrailSnow"/> that object was made at
        /// the top of every call: 56 bytes a frame, though a job starts only when the trails change.
        /// </summary>
        static Task<TrailSnowLayout> BuildTrailSnow(DesignDocument doc, SimNetwork net, TrailNetworkGeometry geo,
                                                    Domain.Weather.WeatherTerrain wx, int cap) =>
            Task.Run(() => TrailSnowInputs.Build(doc, net, geo.CentrelineLengthMm, geo.PointAlong,
                                                 wx.Width, wx.Depth, wx.OriginXMm, wx.OriginYMm, cap));
    }
}
