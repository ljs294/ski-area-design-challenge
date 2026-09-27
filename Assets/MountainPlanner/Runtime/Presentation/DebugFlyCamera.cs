using MountainPlanner.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// A temporary camera for looking at the terrain until task 11 builds the real one (T9, U2), with
    /// Cities: Skylines-style controls. It orbits a target on the ground and never goes below it.
    ///   W A S D / arrows: move · Q / E: rotate · R / F: tilt · Page Up / Page Down or wheel: zoom
    ///   Middle drag: rotate and tilt · Right drag: move · Shift: faster
    /// Zoom and rotation ease toward their targets so wheel steps feel smooth.
    /// </summary>
    public sealed class DebugFlyCamera : MonoBehaviour
    {
        public ITerrainSurface Surface;
        public Vector3 Target;
        public float Distance = 3500f;
        public float Yaw = 200f;
        public float Pitch = 24f;
        public float MinClearance = 3f;

        [Tooltip("Fraction of the distance each wheel notch zooms (0.25 = 25%).")]
        public float ZoomStep = 0.25f;
        public float MinDistance = 20f;
        public float MaxDistance = 20000f;
        public float RotateSpeed = 90f;  // degrees per second, Q / E
        public float TiltSpeed = 45f;    // degrees per second, R / F
        public float Smoothing = 12f;    // higher is snappier

        float _distanceGoal;
        float _yawGoal;
        float _pitchGoal;
        bool _initialised;

        public void Frame(Vector3 target, float distance)
        {
            Target = target;
            Distance = _distanceGoal = distance;
        }

        public void SetAngles(float yaw, float pitch)
        {
            Yaw = _yawGoal = yaw;
            Pitch = _pitchGoal = pitch;
        }

        void LateUpdate()
        {
            if (!_initialised)
            {
                _distanceGoal = Distance;
                _yawGoal = Yaw;
                _pitchGoal = Pitch;
                _initialised = true;
            }

            var mouse = Mouse.current;
            var keys = Keyboard.current;
            float dt = Time.unscaledDeltaTime;
            bool fast = keys != null && keys.shiftKey.isPressed;
            float boost = fast ? 3f : 1f;
            var yawOnly = Quaternion.Euler(0, _yawGoal, 0);
            var forward = yawOnly * Vector3.forward;
            var right = yawOnly * Vector3.right;

            if (mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                if (mouse.middleButton.isPressed)
                {
                    _yawGoal += delta.x * 0.25f;
                    _pitchGoal -= delta.y * 0.25f;
                }
                else if (mouse.rightButton.isPressed)
                {
                    // Drag the ground: the terrain follows the pointer.
                    float scale = Distance * 0.0012f;
                    Target -= (right * delta.x + forward * delta.y) * scale;
                }

                // Windows reports 120 per wheel notch; some devices report 1.
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    float notches = Mathf.Abs(scroll) >= 20f ? scroll / 120f : scroll;
                    _distanceGoal *= Mathf.Pow(1f - ZoomStep * (fast ? 2f : 1f), notches);
                }
            }

            if (keys != null)
            {
                var move = Vector3.zero;
                if (keys.wKey.isPressed || keys.upArrowKey.isPressed) move += forward;
                if (keys.sKey.isPressed || keys.downArrowKey.isPressed) move -= forward;
                if (keys.dKey.isPressed || keys.rightArrowKey.isPressed) move += right;
                if (keys.aKey.isPressed || keys.leftArrowKey.isPressed) move -= right;
                Target += move.normalized * Mathf.Max(30f, Distance * 0.8f) * boost * dt;

                if (keys.qKey.isPressed) _yawGoal += RotateSpeed * boost * dt;
                if (keys.eKey.isPressed) _yawGoal -= RotateSpeed * boost * dt;
                if (keys.rKey.isPressed) _pitchGoal += TiltSpeed * boost * dt;
                if (keys.fKey.isPressed) _pitchGoal -= TiltSpeed * boost * dt;
                if (keys.pageUpKey.isPressed) _distanceGoal *= Mathf.Pow(0.2f * (fast ? 0.3f : 1f), dt);
                if (keys.pageDownKey.isPressed) _distanceGoal /= Mathf.Pow(0.2f * (fast ? 0.3f : 1f), dt);
            }

            _pitchGoal = Mathf.Clamp(_pitchGoal, 2f, 89f);
            _distanceGoal = Mathf.Clamp(_distanceGoal, MinDistance, MaxDistance);
            float t = 1f - Mathf.Exp(-Smoothing * dt);
            Distance = Mathf.Exp(Mathf.Lerp(Mathf.Log(Distance), Mathf.Log(_distanceGoal), t));
            Yaw = Mathf.LerpAngle(Yaw, _yawGoal, t);
            Pitch = Mathf.Lerp(Pitch, _pitchGoal, t);

            var rotation = Quaternion.Euler(Pitch, Yaw, 0);
            if (Surface != null)
            {
                // The target rides on the ground, so moving across a ridge keeps the view level with it.
                float targetGround = Surface.HeightAt(Target.x, Target.z);
                if (!float.IsNaN(targetGround)) Target.y = targetGround;
            }
            var position = Target - rotation * Vector3.forward * Distance;
            if (Surface != null)
            {
                float ground = Surface.HeightAt(position.x, position.z);
                if (!float.IsNaN(ground) && position.y < ground + MinClearance) position.y = ground + MinClearance;
            }
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(Target - position, Vector3.up));
        }
    }
}
