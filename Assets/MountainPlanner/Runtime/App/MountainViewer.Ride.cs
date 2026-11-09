using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.Simulation;
using MountainPlanner.UI.Photo;
using MountainPlanner.World.Lifts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.App
{
    /// <summary>
    /// Ride a lift (task 4.11 PR B; owner 2026-11-08, the approved mockup's #demo=ride): from a lift's card, the camera
    /// takes a seat on the next carrier to leave the bottom and rides it up in first person, every lift type. A chair or a
    /// gondola carries the eye in its hanger's frame, the same one the chair is drawn with, so the seat never drifts; a
    /// tow's rider stands on the snow under the bar; a carpet's rider stands on the belt. The mouse looks about the seat,
    /// Esc leaves to the camera as it was, and at the top the rider gets off by themself, to the orbit camera framed on the
    /// top station with the lift's card open. No people are drawn: every chair rides empty. While riding, the ridden
    /// carrier draws at LOD0 and is never culled, the near plane is 0.1 m, and the HUD is away behind a thin ride bar.
    /// </summary>
    public sealed partial class MountainViewer
    {
        /// <summary>The near plane in the seat: close enough for the restraint bar at arm's length.</summary>
        const float RideNear = 0.1f;
        /// <summary>Mouse look about the seat: yaw ±110°, pitch 70° down to 40° up, at the camera's drag rate.</summary>
        const float RideYawLimit = 110f, RidePitchDown = 70f, RidePitchUp = 40f, RideLookRate = 0.15f;
        /// <summary>A seated eye above the carrier's lowest point (a chair's footrest) and off its hanger, in the hanger's frame.</summary>
        static readonly Vector3 SeatEye = new Vector3(-0.35f, 1.2f, 0.05f);
        /// <summary>A standing eye above the snow (a tow's or a carpet's rider).</summary>
        const float StandingEye = 1.65f;
        /// <summary>The rider gets off this many seconds of rope run before the carrier reaches the top station's wheel.</summary>
        const float AlightEarly = 1.5f;

        RideBar _rideBar;
        bool _riding;
        LiftId _rideLift;
        int _rideCarrier;
        float _rideBottomT, _rideUpT;   // the track's time at the bottom wheel, and the run up to the top's
        double _rideStartSeconds, _rideLastSeconds;
        float _rideRate = 1f;           // rope seconds a real second, smoothed (the clock's speed, 0 when stopped)
        float _lookYaw, _lookPitch;
        Vector3 _beltFrom, _beltTo;
        float _beltSpeed;
        // The camera as it was, to put back on Esc.
        ViewCamera.Mode _backMode;
        Vector3 _backTarget, _backEye;
        float _backDistance, _backYaw, _backPitch, _backNear;
        string _rideWord;
        LiftOpStatus _rideShownStatus = (LiftOpStatus)255;
        bool _rideShownParked;
        int _rideLookSettle;

        internal bool Riding => _riding;

        /// <summary>The card's Ride: a seat on the lift's next carrier from the bottom.</summary>
        void StartRide(ulong id)
        {
            if (_riding || _resort == null || Camera == null || TitleMode) return;
            var liftId = new LiftId(id);
            if (!_liftViews.TryGetValue(liftId, out var shown) || shown.View == null || shown.Lift.Stage != WorkStage.Built) return;
            var view = shown.View;
            var lift = shown.Lift;
            if (_photo) SetPhoto(false);
            if (_tools != null && _tools.Armed) _tools.Close();
            double seconds = ChairSeconds(liftId);
            Vector2 bottom2 = DesignFrame.ToLocal(lift.Bottom), top2 = DesignFrame.ToLocal(lift.Top);
            if (view.IsBelt)
            {
                var type = LiftType.Of(lift);
                _beltSpeed = (float)type.SpeedMetresPerSecond;
                _beltFrom = OnGround(bottom2);
                _beltTo = OnGround(top2);
                if (lift.Levels is LiftLevels lv) { _beltFrom.y = (float)lv.BottomMetres; _beltTo.y = (float)lv.TopMetres; }
                if (_beltSpeed <= 0) return;
            }
            else
            {
                if (view.CarrierCount == 0 || view.Carriers == null) return;
                // The track's times at the two wheels: its points nearest each terminal's origin, the top's on the way up.
                var track = view.Carriers;
                float loop = track.LoopTime;
                var b3 = new Vector3(bottom2.x, 0, bottom2.y);
                var t3 = new Vector3(top2.x, 0, top2.y);
                _rideBottomT = track.Times[Nearest(track.Points, b3)];
                float topT = track.Times[Nearest(track.Points, t3)];
                _rideUpT = Mathf.Repeat(topT - _rideBottomT, loop);
                // The carrier that left the bottom wheel last: the least way up.
                _rideCarrier = 0;
                float best = float.MaxValue;
                for (int k = 0; k < view.CarrierCount; k++)
                {
                    float up = Mathf.Repeat(view.CarrierTime(k, seconds) - _rideBottomT, loop);
                    if (up < best) { best = up; _rideCarrier = k; }
                }
                view.RiddenCarrier = _rideCarrier;
            }
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            _backMode = Camera.Current;
            _backTarget = Camera.Target;
            _backDistance = Camera.Distance;
            _backYaw = Camera.Yaw;
            _backPitch = Camera.Pitch;
            _backEye = Camera.transform.position;
            _backNear = cam.nearClipPlane;
            Camera.Riding = true;
            cam.nearClipPlane = RideNear;
            _riding = true;
            _rideLift = liftId;
            _rideStartSeconds = _rideLastSeconds = seconds;
            _rideRate = 1f;
            _lookYaw = _lookPitch = 0;
            // First person (owner 2026-11-08): the mouse is the rider's head. The pointer locks and hides, so looking never
            // stops at the screen's edge; Esc leaves, H hides the bar. The first frames' movement (the click) is dropped.
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _rideLookSettle = 2;
            _rideShownStatus = (LiftOpStatus)255;
            if (_rideBar == null && Hud != null) _rideBar = CreateRideBar();
            _rideBar?.SetLift(lift.Plate, lift.Name);
            _rideBar?.SetBarHidden(false);
            _rideBar?.SetVisible(true);
            Debug.Log($"[Ride] {lift.Name}: carrier {_rideCarrier} of {view.CarrierCount}, {(view.IsBelt ? "belt" : view.IsTow ? "tow" : "seat")}");
        }

        RideBar CreateRideBar()
        {
            var bar = RideBar.Create(transform, Hud.Document.panelSettings);
            bar.HideChosen += () => bar.SetBarHidden(!bar.BarHidden);
            bar.ExitChosen += () => EndRide(false);
            return bar;
        }

        static int Nearest(Vector3[] points, Vector3 at)
        {
            int best = 0;
            float d = float.MaxValue;
            for (int i = 0; i < points.Length; i++)
            {
                float dx = points[i].x - at.x, dz = points[i].z - at.z, e = dx * dx + dz * dz;
                if (e < d) { d = e; best = i; }
            }
            return best;
        }

        /// <summary>Leaves the lift: at the top to the station's orbit view and card, else (Esc) to the camera as it was.</summary>
        void EndRide(bool atTop)
        {
            if (!_riding) return;
            _riding = false;
            if (_liftViews.TryGetValue(_rideLift, out var shown) && shown.View != null) shown.View.RiddenCarrier = -1;
            _rideBar?.SetVisible(false);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (Camera == null) return;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            cam.nearClipPlane = _backNear;
            Camera.Riding = false;
            if (atTop && shown.Lift != null)
            {
                Vector2 b = DesignFrame.ToLocal(shown.Lift.Bottom), t = DesignFrame.ToLocal(shown.Lift.Top);
                var heading = Mathf.Atan2(t.x - b.x, t.y - b.y) * Mathf.Rad2Deg;
                // Over the rider's shoulder: the top station, from down the line they just came up.
                Camera.Frame(OnGround(t), 140f);
                Camera.SetAngles(heading, 20f);
                Hud?.OpenLiftCardFor(_rideLift.Value);
                Hud?.Toast($"Off {shown.Lift.Name} at the top");
            }
            else if (_backMode == ViewCamera.Mode.FreeFly) Camera.FlyFrom(_backEye, _backYaw, _backPitch);
            else
            {
                Camera.Frame(_backTarget, _backDistance);
                Camera.SetAngles(_backYaw, _backPitch);
            }
        }

        /// <summary>The ride's keys: Esc leaves, the HUD key hides the bar, the clock's keys still work. True while riding.</summary>
        bool HandleRideKeys(Keyboard keys, bool letters)
        {
            if (!_riding) return false;
            if (KeyBindings.Pressed(keys, GameAction.HideUi, letters) && _rideBar != null) _rideBar.SetBarHidden(!_rideBar.BarHidden);
            if (_sim != null)
            {
                if (KeyBindings.Pressed(keys, GameAction.Pause, letters)) SendSim(SimCommandKind.TogglePause);
                if (KeyBindings.Pressed(keys, GameAction.Speed1, letters)) SendSim(SimCommandKind.SetSpeed, 1);
                if (KeyBindings.Pressed(keys, GameAction.Speed2, letters)) SendSim(SimCommandKind.SetSpeed, 2);
                if (KeyBindings.Pressed(keys, GameAction.Speed3, letters)) SendSim(SimCommandKind.SetSpeed, 3);
                if (KeyBindings.Pressed(keys, GameAction.Speed4, letters)) SendSim(SimCommandKind.SetSpeed, 4);
            }
            return true;
        }

        /// <summary>
        /// Each frame while riding, before the chairs are drawn (so the seat and its chair share this frame's rope time):
        /// the eye on the carrier, the mouse's look, the bar, and the automatic exit at the top. Allocates nothing.
        /// </summary>
        void TickRide()
        {
            if (!_riding) return;
            if (Camera == null || _resort == null || !_liftViews.TryGetValue(_rideLift, out var shown) || shown.View == null || shown.Lift.Stage != WorkStage.Built)
            {
                EndRide(false);
                return;
            }
            var view = shown.View;
            double seconds = ChairSeconds(_rideLift);
            float dt = Mathf.Max(1e-4f, Time.unscaledDeltaTime);
            float rate = (float)((seconds - _rideLastSeconds) / dt);
            _rideRate = Mathf.Lerp(_rideRate, Mathf.Max(0, rate), 1f - Mathf.Exp(-4f * dt));
            _rideLastSeconds = seconds;

            // The mouse looks about the seat, the pointer locked and hidden.
            var mouse = Mouse.current;
            if (_rideLookSettle > 0) _rideLookSettle--;
            else if (mouse != null)
            {
                var d = mouse.delta.ReadValue();
                _lookYaw = Mathf.Clamp(_lookYaw + d.x * RideLookRate, -RideYawLimit, RideYawLimit);
                _lookPitch = Mathf.Clamp(_lookPitch - d.y * RideLookRate, -RidePitchUp, RidePitchDown);
            }

            Vector3 eye;
            Quaternion facing;
            float progress, remaining;
            if (view.IsBelt)
            {
                float length = Vector3.Distance(_beltFrom, _beltTo);
                float along = Mathf.Max(0, (float)(seconds - _rideStartSeconds) * _beltSpeed);
                progress = length > 0 ? along / length : 1;
                remaining = Mathf.Max(0, length - along) / _beltSpeed;
                eye = Vector3.Lerp(_beltFrom, _beltTo, Mathf.Clamp01(progress)) + Vector3.up * (StandingEye + Camera.SnowDepth);
                var flat = new Vector3(_beltTo.x - _beltFrom.x, 0, _beltTo.z - _beltFrom.z);
                facing = flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(flat) : Quaternion.identity;
            }
            else
            {
                view.RiddenCarrier = _rideCarrier;
                view.CarrierPose(_rideCarrier, seconds, out _, out var hang);
                float up = Mathf.Repeat(view.CarrierTime(_rideCarrier, seconds) - _rideBottomT, view.Carriers.LoopTime);
                progress = _rideUpT > 0 ? up / _rideUpT : 1;
                remaining = Mathf.Max(0, _rideUpT - AlightEarly - up);
                facing = hang.rotation;
                if (view.IsTow)
                {
                    // A tow's rider stands on the snow under the bar, towed along its track.
                    var under = hang.MultiplyPoint3x4(new Vector3(0, -view.CarrierDrop, 0));
                    float ground = _resort.Surface.HeightAt(under.x, under.z);
                    eye = new Vector3(under.x, (float.IsNaN(ground) ? under.y : ground) + Camera.SnowDepth + StandingEye, under.z);
                }
                else eye = hang.MultiplyPoint3x4(new Vector3(SeatEye.x, SeatEye.y - view.CarrierDrop, SeatEye.z));
                if (up >= _rideUpT - AlightEarly && up <= _rideUpT + view.CarrierInterval) progress = 1;
            }
            Camera.transform.SetPositionAndRotation(eye, facing * Quaternion.Euler(_lookPitch, _lookYaw, 0));
            Camera.GetComponent<UnityEngine.Camera>().nearClipPlane = RideNear;

            // The bar: the Status word made once per change, the time to the top at the clock's current speed.
            var s = _simNow;
            int i = -1;
            if (s != null) for (int j = 0; j < s.LiftCount; j++) if (s.LiftIds[j] == _rideLift.Value) { i = j; break; }
            var status = i >= 0 ? s.LiftStatus[i] : LiftOpStatus.Open;
            bool moving = _rideRate > 0.05f;
            bool parked = status == LiftOpStatus.Closed && !moving && progress < 0.02f;
            if (status != _rideShownStatus || parked != _rideShownParked)
            {
                _rideShownStatus = status;
                _rideShownParked = parked;
                int hold = _holdMph.TryGetValue(_rideLift.Value, out int h) ? h : -1;
                _rideWord = status switch
                {
                    LiftOpStatus.Open => "Open",
                    LiftOpStatus.OnHold => hold > 0 ? $"On hold · wind over {hold} mph" : "On hold",
                    LiftOpStatus.Scheduled => $"Scheduled · opens {MountainPlanner.UI.Hud.HudText.ClockOfDay(s.LiftOpenSecond[i])}",
                    LiftOpStatus.Downloading => "Closed to boarding",
                    _ => parked ? "Closed · parked at the bottom" : "Closed",
                };
            }
            int lamp = status == LiftOpStatus.Open || status == LiftOpStatus.Downloading ? 0 : status == LiftOpStatus.OnHold ? 1 : 2;
            _rideBar?.SetState(_rideWord, lamp, moving ? Mathf.CeilToInt(remaining / _rideRate) : -1, progress);

            _rideProgress = progress;
            if (progress >= 1f) EndRide(true);
        }

        float _rideProgress;

        // ---------- -ride <lift> [-ridespeed N] [-rideshots <folder> | -ridebench <out.json>]: scripted rides ----------

        /// <summary>
        /// A ride by script, for review pictures and the frame budget: the first built lift whose name or catalog id holds
        /// <c>-ride</c>'s word rides with its chairs on a clock of their own at <c>-ridespeed</c> × real time (8 by default),
        /// so it runs whether or not the resort is open. <c>-rideshots</c> saves boarding, mid-ride looking down at the
        /// chair, near the top looking back, and the view off at the top; <c>-ridebench</c> records every frame of the ride.
        /// Both quit when done.
        /// </summary>
        System.Collections.IEnumerator RunRideArguments(string[] args)
        {
            int at = System.Array.IndexOf(args, "-ride");
            if (at < 0 || at + 1 >= args.Length) yield break;
            string want = args[at + 1];
            int shotsAt = System.Array.IndexOf(args, "-rideshots"), benchAt = System.Array.IndexOf(args, "-ridebench"), speedAt = System.Array.IndexOf(args, "-ridespeed");
            string shots = shotsAt >= 0 && shotsAt + 1 < args.Length ? args[shotsAt + 1] : null;
            string bench = benchAt >= 0 && benchAt + 1 < args.Length ? args[benchAt + 1] : null;
            float speed = speedAt >= 0 && speedAt + 1 < args.Length && float.TryParse(args[speedAt + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 8f;
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy) yield return null;
            for (int i = 0; i < 90; i++) yield return null;
            ulong id = 0;
            foreach (var kv in _liftViews)
                if (kv.Value.Lift.Stage == WorkStage.Built && kv.Value.View != null
                    && (kv.Value.Lift.Name.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) >= 0 || kv.Value.Lift.CatalogId.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) >= 0))
                { id = kv.Key.Value; break; }
            if (id == 0) { Debug.LogError($"[Ride] No built lift matches '{want}'"); Application.Quit(1); yield break; }
            _chairsOverride = true;
            _chairOverrideSeconds = 0;
            StartRide(id);
            if (!_riding) { Debug.LogError("[Ride] The ride didn't start"); Application.Quit(1); yield break; }
            if (shots != null) System.IO.Directory.CreateDirectory(shots);
            var stats = bench != null ? new FrameStats() : null;
            float[] marks = { 0.02f, 0.5f, 0.9f };
            string[] names = { "1-boarding", "2-mid-looking-down", "3-near-top-looking-back" };
            int next = 0;
            float started = Time.unscaledTime;
            while (_riding && Time.unscaledTime - started < 900f)
            {
                _chairOverrideSeconds += Time.unscaledDeltaTime * speed;
                stats?.Record(Time.unscaledDeltaTime, FrameStats.SampleGpuMs());
                if (shots != null && next < marks.Length && _rideProgress >= marks[next])
                {
                    _lookPitch = next == 1 ? 55f : 5f;
                    _lookYaw = next == 2 ? 100f : 0f;
                    yield return null;
                    yield return null;
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shots, names[next] + ".png"));
                    Debug.Log($"[Ride] {names[next]} at {_rideProgress:P0}, {_fps:F0} fps");
                    next++;
                }
                yield return null;
            }
            _chairsOverride = false;
            if (stats != null)
            {
                stats.Stop();
                var sum = stats.Summarise();
                System.IO.File.WriteAllText(bench, JsonUtility.ToJson(sum, true));
                Debug.Log($"[Ride] bench {sum.frames} frames, p50 {sum.p50Ms:F2} ms, p95 {sum.p95Ms:F2} ms, p99 {sum.p99Ms:F2} ms, max {sum.maxMs:F1} ms -> {bench}");
                stats.Dispose();
            }
            if (shots != null)
            {
                for (int i = 0; i < 60; i++) yield return null;
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shots, "4-off-at-the-top.png"));
                var f = Camera.transform.forward;
                Debug.Log($"[Ride] 4-off-at-the-top: pitch {Camera.Pitch:F1}, looking {Mathf.Asin(-f.y) * Mathf.Rad2Deg:F1} deg down, {Camera.Distance:F0} m out");
                yield return null;
                yield return null;
            }
            Debug.Log("[Ride] scripted ride done");
            Application.Quit();
        }
    }
}
