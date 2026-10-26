using Il2Cpp;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SiCommanderMapNav;

// DriveMode = "direct" (default since 0.1.1): move the RTS camera rig ourselves instead of re-enabling vanilla input.
//
// Why: in 0.1.0 (override route) the gate opened in game but the camera did not move, and it is not yet known which link
// fails (see Diag.cs). Direct mode needs none of GameInput: it reads Keyboard.current and writes
// StrategyMode.Instance.transform (the camera PIVOT / rig), which vanilla StrategyMode.Update keeps processing while the
// map is open (StrategyMode.cs 106:2276-2380 = 109:2375-2470):
//   currentPosition = base.transform.position                        (109:2440)
//   ClampPositionToCameraBounds(ref currentPosition, translation, CameraSpeed)   (109:2060-2079, x/z to CameraBounds)
//   ClampCameraHeight(...) -> base.transform.position = currentPosition (109:2244-2263, terrain-follow height)
//   Game.CurrentCamera smooth-damps toward base.transform.position   (109:2464-2469)
// So whatever we add to transform.position this frame (or the previous one, depending on script order) is clamped,
// height-corrected and smoothed by vanilla exactly like keyboard movement. We clamp x/z to CameraBounds ourselves too, in
// case vanilla Update is the part that does not run (Diag.cs then logs "RIG MOVED, CAMERA DID NOT").
//
// Movement = StrategyMode.UpdateCameraPosition (106:2074-2085, 109:2176-2186):
//   speed = (CameraHeightPct * 0.9 + 0.1) * 600 * dt * (m_SmoothMovement ? 0.5 : 1), dt = clamp(unscaledDeltaTime, 0, 0.033)
//   right = transform.right, forward = Cross(right, up).normalized; input clamped per axis, times CameraSpeed (turbo).
// The vanilla 2DVector composite (Commander/Camera Movement: WASD + arrows) normalizes diagonals; we do the same.
// Rotation = UpdateCameraRotation (106:2012-2047): smooth -> yaw += 120 deg/s * dir * dt; stepped -> SnapYaw (90 deg steps)
// once per key press; around the pivot with SetYawAroundPivot's math (106:2049-2064, 109:2151-2166), copied below because
// the 0.9.47 interop has neither SetYawAroundPivot nor SnapYaw (0.9.47 inlines the same math, 0947:1601-1630).
//
// Keys are HARDCODED to the vanilla default bindings (SilicaInput.asset): Camera Movement = W/A/S/D + arrows,
// Rotate Camera = Q/E + comma/period. Player rebinding of those actions is NOT honoured in direct mode.
public sealed partial class ClientMod
{
    private bool yawStepped;

    private bool IsDirectMode => !string.Equals((prefDriveMode.Value ?? "").Trim(), "override", StringComparison.OrdinalIgnoreCase);

    private void ResetDrive() { yawStepped = false; }

    private static float Axis(bool positive, bool negative) => (positive ? 1f : 0f) - (negative ? 1f : 0f);

    // Any camera key physically held (both drive modes; used for MapFollowsCamera and the diagnostics).
    private static bool NavKeysHeld()
    {
        var kb = Keyboard.current;
        if (kb == null) return false;
        return kb.wKey.isPressed || kb.aKey.isPressed || kb.sKey.isPressed || kb.dKey.isPressed
            || kb.upArrowKey.isPressed || kb.downArrowKey.isPressed || kb.leftArrowKey.isPressed || kb.rightArrowKey.isPressed
            || kb.qKey.isPressed || kb.eKey.isPressed || kb.commaKey.isPressed || kb.periodKey.isPressed;
    }

    private void DirectDrive()
    {
        var kb = Keyboard.current;
        var sm = StrategyMode.Instance;
        if (kb == null || sm == null || !Application.isFocused) return;
        float y = Axis(kb.wKey.isPressed || kb.upArrowKey.isPressed, kb.sKey.isPressed || kb.downArrowKey.isPressed);
        float x = Axis(kb.dKey.isPressed || kb.rightArrowKey.isPressed, kb.aKey.isPressed || kb.leftArrowKey.isPressed);
        float yawDir = Axis(kb.eKey.isPressed || kb.periodKey.isPressed, kb.qKey.isPressed || kb.commaKey.isPressed);
        if (x == 0f && y == 0f && yawDir == 0f) { yawStepped = false; return; }

        float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.033f);
        bool smooth = sm.m_SmoothMovement;
        var t = sm.transform;
        Vector3 pos = t.position;

        if (yawDir != 0f && (smooth || !yawStepped))
        {
            float current = t.rotation.eulerAngles.y;
            float yaw = smooth ? current + 120f * yawDir * dt : SnapYaw(current, yawDir);
            YawAroundPivot(sm, ref pos, yaw);
            yawStepped = true;
        }
        else if (yawDir == 0f) yawStepped = false;

        if (x != 0f || y != 0f)
        {
            var input = new Vector2(x, y);
            if (input.sqrMagnitude > 1f) input.Normalize();
            float speed = (sm.CameraHeightPct * 0.9f + 0.1f) * 600f * dt * (smooth ? 0.5f : 1f);
            Vector3 right = t.right;
            Vector3 forward = Vector3.Cross(right, Vector3.up).normalized;
            float turbo = sm.CameraSpeed;
            pos += (input.y * speed * forward + input.x * speed * right) * (turbo > 0f ? turbo : 1f);
            Bounds b = sm.CameraBounds;
            if (b.size.x > 0f && b.size.z > 0f)
            {
                pos.x = Mathf.Clamp(pos.x, b.min.x, b.max.x);
                pos.z = Mathf.Clamp(pos.z, b.min.z, b.max.z);
            }
        }
        t.position = pos;
    }

    // = StrategyMode.SnapYaw (106:2066-2069)
    private static float SnapYaw(float yaw, float direction) =>
        Mathf.Repeat(direction > 0f ? (Mathf.Floor((yaw + 1f) / 90f) + 1f) * 90f : (Mathf.Ceil((yaw - 1f) / 90f) - 1f) * 90f, 360f);

    // = StrategyMode.SetYawAroundPivot (106:2049-2064): rotate the rig to `yaw` keeping the ground point it looks at fixed.
    private static void YawAroundPivot(StrategyMode sm, ref Vector3 currentPosition, float yaw)
    {
        var t = sm.transform;
        float cameraPitch = sm.CameraPitch;
        float pitch = Mathf.Clamp(cameraPitch, 30f, 85f);
        float height = Mathf.Lerp(sm.CameraMaxTerrainHeight, Mathf.Lerp(sm.CameraMinTerrainHeight, sm.CameraMaxTerrainHeight, sm.CameraHeightPct), sm.TerrainHeightPct);
        float slant = height / Mathf.Sin(pitch * (MathF.PI / 180f));
        float ground = Mathf.Sqrt(Math.Max(0f, slant * slant - height * height));
        Vector3 forward = t.forward; forward.y = 0f; forward.Normalize();
        Vector3 pivot = forward * ground + currentPosition;
        t.rotation = Quaternion.Euler(cameraPitch, yaw, 0f);
        forward = t.forward; forward.y = 0f; forward.Normalize();
        currentPosition = forward * -ground + pivot;
    }
}
