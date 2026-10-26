# Feature request: commander camera navigation while the large map (M) is open

## Request
- **What:** while the commander has the large map (M) open, let the RTS camera keep moving: WASD/arrows to move, Q/E to rotate, Shift to boost, and a left click on the map to move the camera there.
- **History:** this worked in an older Silica build. Today the camera freezes as soon as the map opens.
- **Proof it works:** a client mod (Si_Commander_MapNav, by DrMuck) reproduces it on Release 1.0.10. Commanders who tested it confirm it works well.

Line numbers below come from an ILSpy decompile of `SilicaCore.dll`, Release 1.0.9 dedicated server (the same update as client 1.0.10), one file per type. Treat the method names as authoritative.

## Why it is gone
1. `LargeMap` is a `Menu` (LargeMap.cs:7).
2. While any menu is open, `GameInput.GetPreferredInput` returns only `InputMap.UI` (GameInput.cs:296-306). `SwitchInput` (GameInput.cs:616) therefore disables the **Commander** action map.
3. `StrategyMode.Update` (StrategyMode.cs:2375) keeps running, but every camera input reads 0:
   - `UpdateCameraPosition` reads `ReadValue("Camera Movement", Commander)` (2180);
   - `UpdateCameraRotation` reads `"Rotate Camera"` (2118);
   - zoom reads `GetAction(Commander, "Zoom Camera")` (2443);
   - `UpdateSpeedBoostHotkey` (3545-3549) uses `GetButton("Camera Speed Boost")`, so `CameraSpeed` stays 1.
4. **The camera pipeline itself is fine with the map open.** The mod writes `StrategyMode.Instance.transform.position` directly and the camera moves. Vanilla `ClampPositionToCameraBounds` (2461), `ClampCameraHeight` (2262) and the `SmoothDamp` toward the pivot (2464) apply unchanged. Only the input is missing.

## Proposed change A: input
**A1 (recommended, one condition).**
In `GameInput.GetPreferredInput`, inside the menu branch (line 303), keep the Commander map on when:
- the LargeMap is the only open menu,
- the player is in strategy mode,
- and no text input is active.

```csharp
if (Game.InMainMenu || ((bool)MenuManager.Instance && MenuManager.Instance.IsAnyMenuOpened(includeClosingMenus: true)))
{
    if (StrategyMode.InStrategyMode && !UIManager.TextInputActive && MenuManager.Instance.IsOnlyOpenMenu<LargeMap>())  // new helper over _openMenus
        return inputMap | InputMap.UI | InputMap.Commander;
    return inputMap | InputMap.UI;
}
```

This also makes the Shift boost work, because `GetButton` keeps working.

**Side effects to check:** every Commander action becomes live behind the map.
- The right-click move/attack order is guarded by `!UIManager.MouseOverAnyElement` (StrategyMode.cs:2481); the full-screen map should satisfy that guard.
- Drag-select, placement hotkeys and control groups ("Assign/Select Group N" via `performed` callbacks, 3330-3345) would fire.
- If that is unwanted, gate those on `!MenuManager.Instance.IsAnyMenuOpened()`, or use A2 for the camera actions only.

**A2 (narrower).** In `LargeMap.Open` / `Close` (LargeMap.cs:76/92), call `GameInput.SetActionEnableOverride(InputMap.Commander, "Camera Movement" / "Rotate Camera", true / false)`, the same way `StrategyMode` handles "Direct Control Unit" (StrategyMode.cs:852/865).

Caveats:
- **Shift boost does not come through A2.** `GetButton` checks only `action.enabled` and ignores `EnableOverride` (GameInput.cs:247-255). The boost would need a `ReadValue`-based read.
- **Clearing the override can disable a live action.** The `EnableOverride` setter disables the InputAction (ActionMapContainer.cs:14-35). If `Close` clears it after the Commander map is already back on, the action must be re-enabled.
- **This route failed when tried from the mod.** The mod set these overrides from outside and the camera did not move. There were no warnings in Game.log, and the action names and map were valid. We could not find out why without the project; we never ran the diagnostic pass with the override active. Please verify A2 in the editor before choosing it.

## Proposed change B: click on the map to move the camera
Add a small component to the large map's MapLayer object (`Map Window/Map Mask/Map`, which already has `UIDrag` and `UIScroll`).
- `IPointerClickHandler` does not fire once a drag has started, so the `UIDrag` pan is unaffected.
- The `Button` already on that object is `RespawnMenu.MapButton`. Its only listener is `OnMapClick` → `SelectSpawnPointGroup(null)` (RespawnMenu.cs:147, 540), so both can coexist.
- The conversion is the inverse of the PlayerIcon placement in `Map.UpdateMap` (Map.cs:431-435):
  - forward: `local = (rect.w * (w.x - off.x) / bounds.x, rect.h * (w.z - off.z) / bounds.z)`, with `bounds = sceneBounds` and `off = sceneOffset` (Map.cs:199-200);
  - inverse: `w.x = off.x + local.x / rect.w * bounds.x`, `w.z = off.z + local.y / rect.h * bounds.z`.

```csharp
// Map.cs: new helper, the inverse of the PlayerIcon placement in UpdateMap.
public bool ScreenToWorld(Vector2 screenPos, Camera eventCamera, out Vector3 world)
{
    world = default;
    if (!MapLayer || sceneBounds.x <= 0f || sceneBounds.z <= 0f) return false;
    RectTransform rt = MapLayer.rectTransform;
    if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screenPos, eventCamera, out Vector2 local)) return false;
    Rect r = rt.rect;
    world = new Vector3(sceneOffset.x + local.x / r.width * sceneBounds.x, sceneOffset.y, sceneOffset.z + local.y / r.height * sceneBounds.z);
    return Mathf.Abs(world.x - sceneOffset.x) <= sceneBounds.x * 0.5f && Mathf.Abs(world.z - sceneOffset.z) <= sceneBounds.z * 0.5f;
}

// New component on the MapLayer object, with _map = the large map's Map ("Map Window").
public class MapClickMoveCamera : MonoBehaviour, IPointerClickHandler
{
    [SerializeField] private Map _map;

    public void OnPointerClick(PointerEventData e)
    {
        if (e.button != PointerEventData.InputButton.Left || e.dragging) return;     // drag = UIDrag pan
        if (!StrategyMode.InStrategyMode || !StrategyMode.Instance) return;
        if (!Player.CurrentPlayer || !Player.CurrentPlayer.IsCommander) return;
        if ((bool)GameMode.CurrentGameMode && GameMode.CurrentGameMode.GetPickSpawn()) return;
        if (_map && _map.ScreenToWorld(e.position, e.pressEventCamera, out Vector3 world))
            StrategyMode.Instance.MoveCameraToPosition(world);                        // StrategyMode.cs:795
    }
}
```

Clicks on map icons bubble up to the MapLayer, so clicking a unit icon also moves the camera there. The mod does the same, and testers liked it. `MoveCameraToPosition` sets the pivot so the camera looks at the point, and it recomputes the height itself.

## Nice to have
When the map is zoomed in, scroll the map along with the camera icon: if the icon leaves an inner margin of `Viewport`, shift the MapLayer position by the overshoot.

## Effort estimate
- A1: about 10 lines plus a pass over the newly live Commander hotkeys.
- B: about 40 lines plus adding the component to the four faction UI prefabs.
- Total: roughly 1-2 hours including testing.
