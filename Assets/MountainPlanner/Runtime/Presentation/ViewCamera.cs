using MountainPlanner.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// The game camera (0.3 §4.7, 0.4 §6, task 11; keys in docs/plans/controls-key-map.md). Two modes, with
    /// the same keys meaning the same thing in both:
    ///   Orbit (default): RTS-style around a focus point on the ground. W A S D / arrows or right-drag pan,
    ///     Q / E or middle-drag rotate, R / F tilt, wheel, + / − or Page Up / Page Down zoom.
    ///   Free-fly: the camera turns about itself. W / S fly along the view, A / D strafe, Q / E turn,
    ///     R / F pitch, Page Up / Page Down rise and sink, wheel and + / − fly forward and back, right- or
    ///     middle-drag look.
    ///   Shift: faster.
    /// Bounds (owner, 2026-10-01): the focus point never leaves the ring, and free-fly stays over it; the orbit
    /// eye may swing out up to <see cref="OutsideRing"/> past the edge to see the diorama walls, never below
    /// the plinth top. Over the terrain the camera stays <see cref="Clearance"/> above the snow
    /// (<see cref="SnowDepth"/> over the ground), and the near plane closes in as it gets low.
    /// Moves start on the frame the key goes down; zoom and turns ease toward their targets.
    /// </summary>
    public sealed class ViewCamera : MonoBehaviour
    {
        public enum Mode { Orbit, FreeFly }

        public ITerrainSurface Surface;
        /// <summary>The ring (local x/z) and the plinth top: the bounds. Without a ring (the Lift Lab) the camera is free.</summary>
        public Rect Ring;
        public bool HasRing;
        public float PlinthTop = float.NegativeInfinity;
        /// <summary>The snow on the ground, metres (iteration 1: a flat 12 in).</summary>
        public float SnowDepth = 0.3048f;
        /// <summary>The least gap between the near plane and the snow, metres.</summary>
        public float Clearance = 0.5f;
        /// <summary>How far beyond the ring's edge the orbit eye may go, metres.</summary>
        public float OutsideRing = 2000f;

        public Vector3 Target;
        public float Distance = 3500f;
        public float Yaw = 200f;
        public float Pitch = 24f;

        [Tooltip("Fraction of the distance each wheel notch zooms (0.25 = 25%).")]
        public float ZoomStep = 0.25f;
        public float MinDistance = 2f;
        public float MaxDistance = 20000f;
        public float RotateSpeed = 90f;  // degrees per second, Q / E
        public float TiltSpeed = 45f;    // degrees per second, R / F
        public float Smoothing = 12f;    // higher is snappier

        /// <summary>
        /// Set by the viewer: true while the pointer is over a HUD panel. Over it, the wheel scrolls the panel, not
        /// the camera, and a drag that starts there doesn't move the camera (one that starts on the map keeps going).
        /// </summary>
        public static System.Func<Vector2, bool> PointerBlocked;
        /// <summary>
        /// The same test for the app flow's screens (download card and pill, quality card, dialogs, the site picker),
        /// which live across scene reloads, so the flow sets it once and the viewer's <see cref="PointerBlocked"/> stays its own.
        /// </summary>
        public static System.Func<Vector2, bool> OverlayBlocked;
        /// <summary>
        /// Set by the HUD while the Toolbox tray is open: letter keys belong to the tools then, and the camera
        /// moves with the arrows, Page Up / Page Down, + / − and the mouse only.
        /// </summary>
        public bool LettersToTools;
        /// <summary>False ignores the keyboard and mouse entirely (benchmarks, scripted reviews).</summary>
        public bool InputEnabled = true;

        /// <summary>The right or middle drag in progress started over a panel, so it belongs to the UI until both are up.</summary>
        bool _dragOnPanel;

        public Mode Current { get; private set; } = Mode.Orbit;
        /// <summary>The camera's near plane, as last set (it closes in near the ground).</summary>
        public float Near => _camera != null ? _camera.nearClipPlane : 1f;

        float _distanceGoal, _yawGoal, _pitchGoal;
        Vector3 _eye;          // free-fly position
        bool _initialised;
        Camera _camera;
        float _farNear;        // the camera's own near plane, used when high

        const float MinPitch = 2f, MaxPitch = 89f, FlyPitchLimit = 85f;

        void Awake()
        {
            _camera = GetComponent<Camera>();
            _farNear = _camera != null ? _camera.nearClipPlane : 1f;
        }

        /// <summary>Orbit around <paramref name="target"/> from <paramref name="distance"/> metres (switches to orbit).</summary>
        public void Frame(Vector3 target, float distance)
        {
            Current = Mode.Orbit;
            Target = target;
            Distance = _distanceGoal = distance;
        }

        public void SetAngles(float yaw, float pitch)
        {
            Yaw = _yawGoal = yaw;
            Pitch = _pitchGoal = pitch;
        }

        /// <summary>Free-fly from <paramref name="eye"/> looking along yaw and pitch (degrees; positive pitch looks down). Bounds still apply.</summary>
        public void FlyFrom(Vector3 eye, float yaw, float pitch)
        {
            EnsureInitialised();
            Current = Mode.FreeFly;
            _eye = eye;
            SetAngles(yaw, Mathf.Clamp(pitch, -FlyPitchLimit, FlyPitchLimit));
        }

        /// <summary>C: orbit to free-fly at the current eye, or free-fly to orbit around the ground point in view.</summary>
        public void ToggleMode()
        {
            EnsureInitialised();
            if (Current == Mode.Orbit)
            {
                _eye = transform.position;
                var forward = transform.forward;
                Yaw = _yawGoal = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                Pitch = _pitchGoal = Mathf.Asin(Mathf.Clamp(-forward.y, -1, 1)) * Mathf.Rad2Deg;
                Current = Mode.FreeFly;
                return;
            }
            float hit = GroundAlong(new Ray(transform.position, transform.forward), 30000f);
            float distance = float.IsNaN(hit) ? Mathf.Max(200f, HeightAboveGround(transform.position) * 2f) : hit;
            Target = transform.position + transform.forward * distance;
            Distance = _distanceGoal = Mathf.Clamp(distance, MinDistance, MaxDistance);
            Pitch = _pitchGoal = Mathf.Clamp(Pitch, MinPitch, MaxPitch);
            Current = Mode.Orbit;
        }

        void EnsureInitialised()
        {
            if (_initialised) return;
            _distanceGoal = Distance;
            _yawGoal = Yaw;
            _pitchGoal = Pitch;
            _initialised = true;
        }

        void LateUpdate()
        {
            EnsureInitialised();
            float dt = Time.unscaledDeltaTime;
            if (InputEnabled) ReadInput(dt);
            if (Current == Mode.Orbit) UpdateOrbit(dt);
            else UpdateFly(dt);
        }

        void ReadInput(float dt)
        {
            var mouse = Mouse.current;
            var keys = Keyboard.current;
            bool fast = keys != null && keys.shiftKey.isPressed;
            float boost = fast ? 3f : 1f;
            bool letters = !LettersToTools && keys != null && !keys.ctrlKey.isPressed && !keys.altKey.isPressed;
            bool fly = Current == Mode.FreeFly;
            var yawOnly = Quaternion.Euler(0, _yawGoal, 0);
            var forward = fly ? Quaternion.Euler(_pitchGoal, _yawGoal, 0) * Vector3.forward : yawOnly * Vector3.forward;
            var right = yawOnly * Vector3.right;
            float speed = fly ? Mathf.Max(20f, HeightAboveGround(_eye) * 0.8f) : Mathf.Max(30f, Distance * 0.8f);

            if (mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                Vector2 pointer = mouse.position.ReadValue();
                bool middle = mouse.middleButton.isPressed, rightDown = mouse.rightButton.isPressed;
                if (!middle && !rightDown) _dragOnPanel = false;
                else if (mouse.middleButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)
                    _dragOnPanel |= IsPointerBlocked(pointer);
                if (_dragOnPanel) delta = Vector2.zero;   // the drag belongs to the panel it started on
                if (middle || (fly && rightDown))
                {
                    _yawGoal += delta.x * 0.25f;
                    _pitchGoal -= delta.y * 0.25f;   // drag up: orbit lowers the view, free-fly looks up
                }
                else if (rightDown)
                {
                    // Drag the ground: the terrain follows the pointer.
                    float scale = Distance * 0.0012f;
                    Target -= (right * delta.x + yawOnly * Vector3.forward * delta.y) * scale;
                }

                // Windows reports 120 per wheel notch; some devices report 1.
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f && !IsPointerBlocked(pointer))
                {
                    float notches = Mathf.Abs(scroll) >= 20f ? scroll / 120f : scroll;
                    if (fly) _eye += forward * notches * speed * 0.15f * boost;
                    else _distanceGoal *= Mathf.Pow(1f - ZoomStep * (fast ? 2f : 1f), notches);
                }
            }

            if (keys == null) return;
            var move = Vector3.zero;
            if ((letters && keys.wKey.isPressed) || keys.upArrowKey.isPressed) move += forward;
            if ((letters && keys.sKey.isPressed) || keys.downArrowKey.isPressed) move -= forward;
            if ((letters && keys.dKey.isPressed) || keys.rightArrowKey.isPressed) move += right;
            if ((letters && keys.aKey.isPressed) || keys.leftArrowKey.isPressed) move -= right;
            if (fly)
            {
                if (keys.pageUpKey.isPressed) move += Vector3.up;
                if (keys.pageDownKey.isPressed) move -= Vector3.up;
                if (keys.equalsKey.isPressed || keys.numpadPlusKey.isPressed) move += forward;
                if (keys.minusKey.isPressed || keys.numpadMinusKey.isPressed) move -= forward;
                _eye += move.normalized * speed * boost * dt;
            }
            else
            {
                Target += move.normalized * speed * boost * dt;
                bool zoomIn = keys.pageUpKey.isPressed || keys.equalsKey.isPressed || keys.numpadPlusKey.isPressed;
                bool zoomOut = keys.pageDownKey.isPressed || keys.minusKey.isPressed || keys.numpadMinusKey.isPressed;
                if (zoomIn) _distanceGoal *= Mathf.Pow(0.2f * (fast ? 0.3f : 1f), dt);
                if (zoomOut) _distanceGoal /= Mathf.Pow(0.2f * (fast ? 0.3f : 1f), dt);
            }
            if (letters && keys.qKey.isPressed) _yawGoal += RotateSpeed * boost * dt;
            if (letters && keys.eKey.isPressed) _yawGoal -= RotateSpeed * boost * dt;
            if (letters && keys.rKey.isPressed) _pitchGoal += TiltSpeed * boost * dt * (fly ? -1f : 1f);   // R tilts the view up
            if (letters && keys.fKey.isPressed) _pitchGoal -= TiltSpeed * boost * dt * (fly ? -1f : 1f);
        }

        /// <summary>True when the pointer (screen pixels) is over the HUD or any of the flow's panels.</summary>
        public static bool IsPointerBlocked(Vector2 screen) =>
            (PointerBlocked?.Invoke(screen) ?? false) || (OverlayBlocked?.Invoke(screen) ?? false);

        void UpdateOrbit(float dt)
        {
            _pitchGoal = Mathf.Clamp(_pitchGoal, MinPitch, MaxPitch);
            _distanceGoal = Mathf.Clamp(_distanceGoal, MinDistance, MaxDistance);
            float t = 1f - Mathf.Exp(-Smoothing * dt);
            Distance = Mathf.Exp(Mathf.Lerp(Mathf.Log(Distance), Mathf.Log(_distanceGoal), t));
            Yaw = Mathf.LerpAngle(Yaw, _yawGoal, t);
            Pitch = Mathf.Lerp(Pitch, _pitchGoal, t);

            if (HasRing)
            {
                Target.x = Mathf.Clamp(Target.x, Ring.xMin, Ring.xMax);
                Target.z = Mathf.Clamp(Target.z, Ring.yMin, Ring.yMax);
            }
            if (Surface != null)
            {
                // The target rides on the ground, so moving across a ridge keeps the view level with it.
                float targetGround = Surface.HeightAt(Target.x, Target.z);
                if (!float.IsNaN(targetGround)) Target.y = targetGround + SnowDepth;
            }
            var rotation = Quaternion.Euler(Pitch, Yaw, 0);
            var back = rotation * Vector3.back;
            float distance = Distance;
            if (HasRing)
            {
                // The eye stays inside the ring grown by OutsideRing: shorten the boom where it would leave.
                var box = new Rect(Ring.xMin - OutsideRing, Ring.yMin - OutsideRing, Ring.width + 2 * OutsideRing, Ring.height + 2 * OutsideRing);
                distance = Mathf.Min(distance, BoxExit(Target, back, box));
            }
            var position = Target + back * distance;
            position.y = Mathf.Max(position.y, Floor(position));
            SetNear(position);
            var look = Target - position;
            transform.SetPositionAndRotation(position, look.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(look, Vector3.up) : rotation);
        }

        void UpdateFly(float dt)
        {
            _pitchGoal = Mathf.Clamp(_pitchGoal, -FlyPitchLimit, FlyPitchLimit);
            float t = 1f - Mathf.Exp(-Smoothing * dt);
            Yaw = Mathf.LerpAngle(Yaw, _yawGoal, t);
            Pitch = Mathf.Lerp(Pitch, _pitchGoal, t);
            if (HasRing)
            {
                _eye.x = Mathf.Clamp(_eye.x, Ring.xMin, Ring.xMax);
                _eye.z = Mathf.Clamp(_eye.z, Ring.yMin, Ring.yMax);
            }
            _eye.y = Mathf.Clamp(_eye.y, Floor(_eye), Floor(_eye) + MaxDistance);
            SetNear(_eye);
            transform.SetPositionAndRotation(_eye, Quaternion.Euler(Pitch, Yaw, 0));
        }

        /// <summary>The lowest the eye may be at a place: above the snow by the clearance and the near plane, or the plinth top off the terrain.</summary>
        float Floor(Vector3 p)
        {
            float ground = Surface != null ? Surface.HeightAt(p.x, p.z) : float.NaN;
            bool overRing = !HasRing || Ring.Contains(new Vector2(p.x, p.z));
            if (!float.IsNaN(ground) && overRing) return ground + SnowDepth + Clearance + NearFor(Clearance);
            return HasRing ? PlinthTop + Clearance : float.NegativeInfinity;
        }

        /// <summary>
        /// Near the ground the near plane closes in so close-ups never clip; high up it goes back to the
        /// camera's own, for depth precision. A near plane of n reaches out at most about 1.5 n from the eye at
        /// this field of view, so a gap of 3 n keeps it clear of the snow.
        /// </summary>
        void SetNear(Vector3 eye)
        {
            if (_camera == null) return;
            float above = HeightAboveGround(eye);
            _camera.nearClipPlane = float.IsNaN(above) ? _farNear : Mathf.Clamp(above / 3f, 0.05f, _farNear);
        }

        float NearFor(float gap) => Mathf.Min(_farNear, 0.05f + gap / 3f);

        float HeightAboveGround(Vector3 p)
        {
            float ground = Surface != null ? Surface.HeightAt(p.x, p.z) : float.NaN;
            return float.IsNaN(ground) ? (Surface == null ? 1000f : float.NaN) : p.y - ground - SnowDepth;
        }

        /// <summary>Distance along a ray from a point inside a box (x/z only) to where it leaves.</summary>
        static float BoxExit(Vector3 from, Vector3 dir, Rect box)
        {
            float t = float.PositiveInfinity;
            if (dir.x > 1e-6f) t = Mathf.Min(t, (box.xMax - from.x) / dir.x);
            else if (dir.x < -1e-6f) t = Mathf.Min(t, (box.xMin - from.x) / dir.x);
            if (dir.z > 1e-6f) t = Mathf.Min(t, (box.yMax - from.z) / dir.z);
            else if (dir.z < -1e-6f) t = Mathf.Min(t, (box.yMin - from.z) / dir.z);
            return Mathf.Max(0, t);
        }

        /// <summary>Distance along a ray to the ground (NaN if it doesn't meet it within <paramref name="max"/>): march, then bisect.</summary>
        public float GroundAlong(Ray ray, float max)
        {
            if (Surface == null) return float.NaN;
            float t = 0, previous = 0;
            while (t < max)
            {
                var p = ray.GetPoint(t);
                float h = Surface.HeightAt(p.x, p.z);
                if (!float.IsNaN(h) && p.y <= h)
                {
                    float lo = previous, hi = t;
                    for (int i = 0; i < 12; i++)
                    {
                        float mid = (lo + hi) * 0.5f;
                        var m = ray.GetPoint(mid);
                        float hm = Surface.HeightAt(m.x, m.z);
                        if (!float.IsNaN(hm) && m.y <= hm) hi = mid; else lo = mid;
                    }
                    return hi;
                }
                previous = t;
                t += Mathf.Max(10, t * 0.01f);
            }
            return float.NaN;
        }
    }
}
