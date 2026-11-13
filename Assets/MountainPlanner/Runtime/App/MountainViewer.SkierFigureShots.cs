using System;
using System.Collections;
using System.IO;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.22: <c>-skiershot &lt;folder&gt;</c>, a look at the guests as figures for a review. The mountain opens with a
    /// crowd (<c>-guests N</c>, default 1,500), then: the busiest lift line close and low; a skier and a snowboarder
    /// followed close (stills, and a short movie of each as <c>&lt;folder&gt;/&lt;name&gt;/f_&lt;ms&gt;.jpg</c>, which
    /// Editor/PickerLabSetup.EncodeMovies turns into MP4s); a run from 80 m; and from 400 m, where the figures hand over to
    /// dots. Then quits. Not a measurement.
    /// </summary>
    public sealed partial class MountainViewer
    {
        void StartSkierFigureShots(string[] args)
        {
            int i = Array.IndexOf(args, "-skiershot");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(SkierFigureShots(Path.GetFullPath(args[i + 1]), GuestsArgument(args, 1500)));
        }

        IEnumerator SkierFigureShots(string dir, int crowd)
        {
            BeginScriptedRun();
            Directory.CreateDirectory(dir);
            yield return BenchCrowd(crowd);
            SendSim(SimCommandKind.SetSpeed, 2);   // 2x: the figures move at a pace a still can catch
            for (int k = 0; k < 120; k++) yield return null;
            Debug.Log($"[SkierShots] figures {(_figures != null ? "on" : "OFF (dots only)")}, {_simNow?.Guests?.SkierCount ?? 0} on the hill");

            // The busiest line.
            var s = _simNow;
            int best = -1, most = 0;
            for (int i = 0; s != null && i < s.LiftCount; i++) if (s.LineCount[i] > most) { most = s.LineCount[i]; best = i; }
            if (best >= 0 && _skierDoc != null && Camera != null)
            {
                var lift = _skierDoc.FindLift(new MountainPlanner.Domain.Design.LiftId(s.LiftIds[best]));
                if (lift != null)
                {
                    var b = MountainPlanner.Presentation.Design.DesignFrame.ToLocal(lift.Bottom);
                    float y = ReadoutHeight(b.x, b.y);
                    var at = new Vector3(b.x, float.IsNaN(y) ? 0 : y, b.y);
                    Camera.Frame(at, 40f);
                    Camera.SetAngles(Camera.Yaw, 30f);
                    for (int k = 0; k < 90; k++) yield return null;
                    yield return FigureShot(dir, "1-line");
                    Camera.Frame(at, 16f);
                    Camera.SetAngles(Camera.Yaw + 60f, 12f);
                    for (int k = 0; k < 60; k++) yield return null;
                    yield return FigureShot(dir, "2-line-low");
                    Debug.Log($"[SkierShots] line: {lift.Name}, {most} in line");
                }
            }

            yield return ChairShots(dir, best);
            yield return FollowShots(dir, "3-skier", boarder: false);
            yield return FollowShots(dir, "4-boarder", boarder: true);
            yield return TowShot(dir);

            // A run from 80 m and from 400 m, around whoever was followed last.
            if (_lastFollowed != Vector3.zero && Camera != null)
            {
                Camera.Frame(_lastFollowed, 80f);
                Camera.SetAngles(Camera.Yaw, 28f);
                for (int k = 0; k < 90; k++) yield return null;
                yield return FigureShot(dir, "5-run-80m");
                Camera.Frame(_lastFollowed, 400f);
                Camera.SetAngles(Camera.Yaw, 32f);
                for (int k = 0; k < 90; k++) yield return null;
                yield return FigureShot(dir, "6-run-400m");
            }
            Debug.Log("[SkierShots] done");
            Application.Quit();
        }

        Vector3 _lastFollowed;

        /// <summary>A skiing guest of the kind asked for, followed from 10 m: three stills a second apart, then 8 s of movie.</summary>
        IEnumerator FollowShots(string dir, string name, bool boarder)
        {
            if (_skiers == null || Camera == null) yield break;
            // Someone who has just got off at a lift's top, so the movie has a whole run of turns in it.
            var tops = new System.Collections.Generic.List<Vector2>();
            var sim = _simNow;
            for (int i = 0; sim != null && _skierDoc != null && i < sim.LiftCount; i++)
            {
                var l = _skierDoc.FindLift(new MountainPlanner.Domain.Design.LiftId(sim.LiftIds[i]));
                if (l != null) tops.Add(MountainPlanner.Presentation.Design.DesignFrame.ToLocal(l.Top));
            }
            uint serial = 0;
            for (int tries = 0; tries < 3600 && serial == 0; tries++)
            {
                var records = _skiers.Records;
                for (int i = 0; i < _skiers.Count && serial == 0; i++)
                {
                    var r = records[i];
                    _skiers.Describe(i, out var dot, out _);
                    if (r.State != (byte)GuestState.Skiing || dot.w < 0 || SkierPlaceJob.IsBoarder(r.Serial) != boarder) continue;
                    if (r.Band < 1 && !boarder) continue;   // a turning skier, not a wedge, for the first look
                    bool nearTop = false;
                    foreach (var t in tops) if (Vector2.Distance(t, new Vector2(dot.x, dot.z)) < 60f) nearTop = true;
                    if (!nearTop && tries < 3000) continue;
                    serial = r.Serial;
                }
                if (serial == 0) yield return null;
            }
            if (serial == 0) { Debug.Log($"[SkierShots] {name}: nobody skiing"); yield break; }
            float yaw = Camera.Yaw;
            IEnumerator Track(int frames)
            {
                for (int k = 0; k < frames; k++)
                {
                    if (_skiers.TryFind(serial, out var at, out _))
                    {
                        _lastFollowed = at;
                        Camera.Frame(at + new Vector3(0, 1f, 0), 10f);
                        Camera.SetAngles(yaw, 14f);
                    }
                    yield return null;
                }
            }
            yield return Track(30);
            for (int n = 0; n < 3; n++)
            {
                yield return Track(30);
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{name}-{(char)('a' + n)}.png"));
                yield return null;
            }
            string frames = Path.Combine(dir, name);
            Directory.CreateDirectory(frames);
            float start = Time.unscaledTime;
            float next = 0;
            while (Time.unscaledTime - start < 8f)
            {
                yield return Track(1);
                float t = Time.unscaledTime - start;
                if (t < next) continue;
                next = t + 1f / 15f;
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(frames, $"f_{(int)(t * 1000):D6}.jpg"), shot.EncodeToJPG(85));
                Destroy(shot);
            }
            _skiers.TryFind(serial, out _, out var rec);
            Debug.Log($"[SkierShots] {name}: serial {serial}, band {rec.Band}, state {(GuestState)rec.State}");
        }

        /// <summary>The busiest lift's chairs from beside the line, a third of the way up: a still and 6 s of movie.</summary>
        IEnumerator ChairShots(string dir, int simLift)
        {
            var s = _simNow;
            if (s == null || simLift < 0 || _skierDoc == null || Camera == null) yield break;
            var lift = _skierDoc.FindLift(new MountainPlanner.Domain.Design.LiftId(s.LiftIds[simLift]));
            if (lift == null) yield break;
            var b = MountainPlanner.Presentation.Design.DesignFrame.ToLocal(lift.Bottom);
            var t = MountainPlanner.Presentation.Design.DesignFrame.ToLocal(lift.Top);
            var m = Vector2.Lerp(b, t, 0.3f);
            float y = ReadoutHeight(m.x, m.y);
            var along = (t - b).normalized;
            var at = new Vector3(m.x, (float.IsNaN(y) ? 0 : y) + 8f, m.y);
            Camera.Frame(at, 28f);
            // From the side, looking across the line.
            float yaw = Mathf.Atan2(along.x, along.y) * Mathf.Rad2Deg + 90f;
            Camera.SetAngles(yaw, 6f);
            for (int k = 0; k < 120; k++) yield return null;
            yield return FigureShot(dir, "7-chairs");
            // Then a loaded chair, close, followed up the line from beside and a little above it.
            var near = at;
            bool Ride()
            {
                if (_riders == null || !_riders.NearestSeat(near, out var seat)) return false;
                near = seat;
                float look = yaw - 120f;   // up the line and across it
                var dir = new Vector3(Mathf.Sin(look * Mathf.Deg2Rad), 0, Mathf.Cos(look * Mathf.Deg2Rad));
                Camera.FlyFrom(seat - dir * 7f + Vector3.up * 2.2f, look, 16f);
                return true;
            }
            for (int k = 0; k < 600 && !Ride(); k++) yield return null;
            for (int k = 0; k < 60; k++) { Ride(); yield return null; }
            Ride();
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "8-chair-close.png"));
            yield return null;
            Debug.Log($"[SkierShots] chairs: {lift.Name}, {_riders?.Count ?? 0} seated riders; {_riders?.Describe()}");
            int riding = 0;
            for (int i = 0; _skiers != null && i < _skiers.Count; i++) if (_skiers.Records[i].State == (byte)GuestState.Riding) riding++;
            Debug.Log($"[SkierShots] {riding} guests riding");
            string frames = Path.Combine(dir, "7-chairs");
            Directory.CreateDirectory(frames);
            float start = Time.unscaledTime, next = 0;
            while (Time.unscaledTime - start < 6f)
            {
                Ride();
                yield return null;
                float e = Time.unscaledTime - start;
                if (e < next) continue;
                next = e + 1f / 15f;
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(frames, $"f_{(int)(e * 1000):D6}.jpg"), shot.EncodeToJPG(85));
                Destroy(shot);
            }
        }

        /// <summary>A guest riding a tow or a carpet (they stand on the snow and are drawn), if the mountain has one.</summary>
        IEnumerator TowShot(string dir)
        {
            if (_skiers == null || Camera == null) yield break;
            uint serial = 0;
            for (int tries = 0; tries < 1800 && serial == 0; tries++)
            {
                var records = _skiers.Records;
                for (int i = 0; i < _skiers.Count && serial == 0; i++)
                {
                    _skiers.Describe(i, out var dot, out _);
                    if (records[i].State == (byte)GuestState.Riding && dot.w >= 0) serial = records[i].Serial;
                }
                if (serial == 0) yield return null;
            }
            if (serial == 0) { Debug.Log("[SkierShots] 9-tow: nobody on a tow or a carpet"); yield break; }
            float yaw = Camera.Yaw;
            for (int k = 0; k < 90; k++)
            {
                if (_skiers.TryFind(serial, out var at, out _)) { Camera.Frame(at + Vector3.up, 9f); Camera.SetAngles(yaw, 16f); }
                yield return null;
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "9-tow.png"));
            yield return null;
            Debug.Log($"[SkierShots] 9-tow: serial {serial}");
        }

        IEnumerator FigureShot(string dir, string name)
        {
            float until = Time.unscaledTime + 1f;
            float worst = 0, sum = 0, gpu = 0, skier = 0;
            int frames = 0, gpuFrames = 0;
            var timings = new FrameTiming[1];
            while (until > Time.unscaledTime)
            {
                yield return null;
                float ms = Time.unscaledDeltaTime * 1000f;
                sum += ms; frames++;
                worst = Mathf.Max(worst, ms);
                if (_skierMs != null && _skierFrames > 0) skier += _skierMs[Math.Min(_skierFrames, _skierMs.Length) - 1];   // the last skier tick (-skiertiming)
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0) { gpu += (float)timings[0].gpuFrameTime; gpuFrames++; }
            }
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, name + ".png"));
            yield return null;
            yield return null;
            Debug.Log($"[SkierShots] {name}: {_figures?.Drawn ?? 0} figures drawn ({_riders?.Count ?? 0} on chairs), frame {sum / Mathf.Max(1, frames):F1} ms mean, {worst:F1} ms worst over {frames} frames, GPU {gpu / Mathf.Max(1, gpuFrames):F1} ms, skier CPU {skier / Mathf.Max(1, frames):F2} ms");
        }
    }
}
