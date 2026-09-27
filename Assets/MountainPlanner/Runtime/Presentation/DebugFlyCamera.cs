using MountainPlanner.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// A temporary camera for looking at the terrain until task 11 builds the real one (T9, U2):
    /// orbit around a target, pan, zoom, and fly the target with the keyboard. Never below the ground.
    ///   Right mouse drag: orbit · Middle drag or Shift + right drag: pan · Wheel: zoom
    ///   W A S D: move · Q / E: down / up · Shift: faster
    /// </summary>
    public sealed class DebugFlyCamera : MonoBehaviour
    {
        public ITerrainSurface Surface;
        public Vector3 Target;
        public float Distance = 3500f;
        public float Yaw = 200f;
        public float Pitch = 24f;
        public float MinClearance = 3f;

        public void Frame(Vector3 target, float distance)
        {
            Target = target;
            Distance = distance;
        }

        void LateUpdate()
        {
            var mouse = Mouse.current;
            var keys = Keyboard.current;
            float dt = Time.unscaledDeltaTime;
            bool fast = keys != null && keys.leftShiftKey.isPressed;

            if (mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();
                bool pan = mouse.middleButton.isPressed || (mouse.rightButton.isPressed && fast);
                if (pan)
                {
                    float scale = Distance * 0.0015f;
                    Target -= transform.right * delta.x * scale + Vector3.ProjectOnPlane(transform.up, Vector3.up).normalized * delta.y * scale;
                }
                else if (mouse.rightButton.isPressed)
                {
                    Yaw += delta.x * 0.2f;
                    Pitch = Mathf.Clamp(Pitch - delta.y * 0.2f, 2f, 89f);
                }
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f) Distance = Mathf.Clamp(Distance * Mathf.Pow(0.999f, scroll), 5f, 20000f);
            }

            if (keys != null)
            {
                var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                var move = Vector3.zero;
                if (keys.wKey.isPressed) move += forward;
                if (keys.sKey.isPressed) move -= forward;
                if (keys.dKey.isPressed) move += transform.right;
                if (keys.aKey.isPressed) move -= transform.right;
                if (keys.eKey.isPressed) move += Vector3.up;
                if (keys.qKey.isPressed) move -= Vector3.up;
                Target += move * Mathf.Max(20f, Distance * 0.6f) * (fast ? 4f : 1f) * dt;
            }

            var rotation = Quaternion.Euler(Pitch, Yaw, 0);
            var position = Target - rotation * Vector3.forward * Distance;
            if (Surface != null)
            {
                float ground = Surface.HeightAt(position.x, position.z);
                if (!float.IsNaN(ground) && position.y < ground + MinClearance) position.y = ground + MinClearance;
                float targetGround = Surface.HeightAt(Target.x, Target.z);
                if (!float.IsNaN(targetGround) && Target.y < targetGround) Target.y = targetGround;
            }
            transform.SetPositionAndRotation(position, Quaternion.LookRotation(Target - position, Vector3.up));
        }
    }
}
