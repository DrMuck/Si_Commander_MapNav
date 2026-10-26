# Si_Commander_MapNav: design

Client-only MelonLoader mod (Il2Cpp game client) for DrMuck.

**Goal:** while the commander has the large map (M) open, the RTS camera can still be moved. This worked in Silica builds from about a year ago and does not work now.

**Line references**
- `106:File.cs:N` means `C:\tmp\silica_decompile_106\File.cs` (Release 1.0.6, test branch).
- `0947:File.cs:N` means `C:\tmp\silica_decompile_0947\File.cs` (Beta 0.9.47, default branch).
- The local client runs Release **1.0.10** since 2026-10-26 16:31. Its interop was checked against 1.0.7 for every class used here; see "Branch differences".

## 1. Why the camera freezes (vanilla)

- `LargeMap` is a `Menu` (106:Silica.UI.Menus/LargeMap.cs:7).
- While any menu is open (closing animations included), `GameInput.GetPreferredInput` returns only `InputMap.UI` (106:Silica/GameInput.cs:303-306).
- `GameInput.UpdateInput` → `SwitchInput` then disables every map not in that set, including **Commander** (GameInput.cs:332-356, 616-631).
- `StrategyMode.Update` keeps running with the map open (106:StrategyMode.cs:2276-2380). It only needs `Game.GetCameraEnabled()` and `InStrategyMode`. `GetCameraEnabled` is `true` for every multiplayer mode; only MP_Prospector overrides it.
- So the camera's smoothing and clamping keep running; they just get no input:
  - `UpdateCameraPosition` reads `GameInput.ReadValue<Vector2>("Camera Movement", Commander)` (2080).
  - `UpdateCameraRotation` reads `ReadValue<float>("Rotate Camera", Commander)` (2019).
  - Zoom reads `GetAction(Commander, "Zoom Camera")?.ReadValue<float>()` (2342).
  - A disabled action reads 0 in all three cases.
- Screen-edge scrolling (`UpdateCameraInput`, 2106) also bails out while any menu is open. With the map covering the screen this does not matter, so it is ignored.

## 2. The lever: per-action `EnableOverride`

`GameInput.SetActionEnableOverride(InputMap, string, bool)` (GameInput.cs:633-651) sets `ActionMapContainer.ActionContainer.EnableOverride`.

How the override behaves (106:Silica/ActionMapContainer.cs):
- **The setter acts on the InputAction itself** (14-35). `_enableOverride = value; if (value != action.enabled) { value ? action.Enable() : action.Disable(); }`.
  - So no extra `Enable()` is needed when setting it. This contradicts the brief, which expected one.
- `SetEnabled(false)` disables the map, then re-enables every overridden action (121-139).
  - **Setting the override therefore works in both orders.** If the map is already UI-only, the setter enables the action. If the switch comes later, `SetEnabled(false)` re-enables it.
- `ProcessInput` → `ProcessActions(overrideOnly: true)` keeps processing overridden actions while their map is disabled (141-169).
- `GameInput.ReadValue` returns a value when `action.enabled || EnableOverride` (GameInput.cs:190-208).
  - `GetButton`, `GetButtonDown` and `GetButtonUp` check only `action.enabled` (211-255). All three camera actions go through `ReadValue`, so this is fine.
- Vanilla uses the same mechanism for "Direct Control Unit" (StrategyMode.cs:838/851).

**The trap is clearing the override.**
- `EnableOverride = false` calls `action.Disable()` whenever the action is enabled. That includes the case where the Commander map was already re-enabled (map closed) before our `OnUpdate` ran that frame.
- That would leave WASD dead in normal RTS play until the next input-map switch.
- **Decision:** after clearing, if `GameInput.CurrentInput` contains `Commander`, call `action.Enable()` again (`Nav.cs` `SetOverride`).
- Extra safeguard: while active, any overridden action found disabled is re-enabled (`HealOverrides`, logged once).
  - Vanilla `ForceDisableAll` is always followed by `EnableAllWithOverride` (GameInput.cs:410-411, 435-436), so this should never fire.

**Only three actions are overridden:** Camera Movement, Rotate Camera and (opt-in) Zoom Camera.
- Selection, orders and every other commander hotkey stay disabled behind the map, exactly as in vanilla.
- Nothing is sent over the network: the RTS camera is purely local.

### Fallback (pref `PreferredInputFallback`, default off)

- A Harmony postfix on `GameInput.GetPreferredInput` ORs in `InputMap.Commander` while the gate is open. The method has no ref/out parameters, so a postfix is safe on Il2Cpp.
- It is broader than the override route: the whole Commander map, including world selection and orders, would be live behind the map.
- Use it only if the override route turns out not to work in game.
- It is installed by hand (`[assembly: HarmonyDontPatchAll]`) and only when the pref is on at game start, so the default path never carries a patch. Changing the pref needs a restart.

## 3. Activation gate (polled every frame, `Nav.cs` `BlockReason`)

All of these must hold:
- the pref `Enabled` is on, and the game is not in the main menu;
- `Player.CurrentPlayer` and `MenuManager.Instance` exist;
- a registered `LargeMap` exists and `IsOpen`;
- `StrategyMode.InStrategyMode` and `StrategyMode.Instance` exist;
- `Player.CurrentPlayer.IsCommander`;
- `!UIManager.TextInputActive` and `!DebugConsole.IsOpen`;
- `!GameMode.CurrentGameMode.GetPickSpawn()` (respawn selection).

Keyboard navigation additionally needs:
- the pref `KeyboardNav`;
- a control scheme other than Gamepad.

**Why gamepad is excluded:** the gamepad left stick already pans the map (UI/Move → `UIMovePanel`, wired by `LargeMapInputStateHandler`). It is also bound to Camera Movement, so overriding would move both the camera and the map.

**How the LargeMap is found:** the non-generic route through `MenuManager._menus` (Dictionary<Type, Menu>, MenuManager.cs:10 and 67-75), the same as Si_Spectator.
- The generic `IsMenuOpened<LargeMap>()` call is avoided because Il2Cpp generic calls are the fragile path.
- `Menu.IsOpen` is false while the menu is closing. The overrides are therefore cleared during the close animation, while the Commander map is still disabled.

**Transitions**
- Every on/off change is logged once ("NAV ON", "NAV OFF: <reason>").
- Overrides are cleared:
  - on every gate close;
  - on scene load (GameInput's maps are static, GameInput.cs:53);
  - on mod unload and application quit;
  - after the error limit (50 errors, then the mod switches itself off for the session).

## 4. Decisions

| Question | Decision | Why |
|---|---|---|
| Zoom while the map is open | **Off by default** (pref `CameraZoomWhileMapOpen`) | The mouse wheel is both `Commander/Zoom Camera` (`<Mouse>/scroll/y`) and the map's own zoom: `UIScroll` on the map layer calls `Map.Zoom` (prefab `m_MethodName: Zoom`). With the override on, one wheel turn zooms both. If enabled, StrategyMode applies it only when `!UIManager.MouseOverScrollView && !WindowManager.IsMouseOverAnyWindow()` (StrategyMode.cs:2341). |
| Map follows the camera | **On by default** (pref `MapFollowsCamera`), implemented as an "edge push", not `FocusOnWorldPositionSmooth` | After a click-to-jump the target is already under the cursor, so recentring would only move the map content away from the cursor. Following is useful only for keyboard movement on a zoomed-in map. The push moves the map layer by exactly the overshoot, keeping the camera icon inside an inner 12 % margin of the viewport. It runs for 0.75 s after the last keyboard input and never while the left button is held, so it never fights a mouse drag. `Map.FocusOnWorldPosition*` (Map.cs:373-399) scales by `mapZoom` only, not `totalZoom = baseZoom * mapZoom` (Map.cs:256-260). A screen-space push is exact at any zoom, and vanilla `ClampPosition` still clamps it (Map.cs:214-244). |
| Override order | Set: no special order needed (setter + `SetEnabled(false)` handle both orders). Clear: re-enable the action when Commander is live again | See §2 |
| Click semantics | `StrategyMode.MoveCameraToPosition(world)` = **look at** the clicked spot | Same call as `RTSNextTechTierIndicator.cs:34` and the sandbox browsers. It sets the camera pivot (StrategyMode.cs:781-786) and recomputes height itself. The camera arrow on the map shows the camera *position*, so it sits a little behind the clicked spot and points at it. |
| Close map on click | Pref `CloseMapOnClick`, **default off** | User choice. Closing uses `MenuManager.CloseMenu(Menu)` (MenuManager.cs:198, same in 0947). |
| Click vs drag | Press and release within **6 px** and **1 s** | A left drag pans the map (`UIDrag` on the map layer). EventSystem starts a drag only beyond its drag threshold, so a sub-6-px click never pans. |
| Mouse buttons | Left only | Middle mouse = Global/Ping (untouched). Right click is untouched. |
| Toggle key | None | Not needed (brief). The `Enabled` pref is the master switch. |

## 5. Click-to-jump details (`MapClick.cs`)

**Hit test.** Each condition below rejects the click:
- the press lies outside the screen rect of `Map.Viewport` ("Map Mask");
- `UIManager.Instance.MouseOverButton` is true;
- the top-most `EventSystem.RaycastAll` hit is not under the viewport (scoreboard, legend, respawn menu or tooltips drawn above the map);
- the hit, or a parent below the viewport, has a `Selectable` (buttons, toggles, sliders).

If there is no EventSystem or no raycast hit, the geometric checks decide.

**Vanilla click behaviour on the map: none.**
- `MapIcon` / `ContextIcon` implement no pointer interface.
- The `IPointerClickHandler` implementers are `ClickHandler` and the RTS panels.
- `LargeMap` and `Map` have no click code.

**This contradicts the brief:** `UIMovePanel` is not a mouse drag. It pans on `UI/Move` = `<Gamepad>/leftStick` (UIMovePanel.cs). The mouse-drag pan is `UIDrag` on the map layer.

**Screen → world** is the exact inverse of the PlayerIcon placement (Map.cs:358-362):
```
icon:  local = (rect.w * (w.x - off.x) / bounds.x, rect.h * (w.z - off.z) / bounds.z);  screen = MapLayer.TransformPoint(local)
ours:  local = MapLayer.InverseTransformPoint(screen);  w.x = off.x + local.x / rect.w * bounds.x;  w.z = off.z + local.y / rect.h * bounds.z
```
- `bounds` / `off` are `Map.sceneBounds` / `Map.sceneOffset`: terrain size and terrain centre (Map.cs:174-178). They are protected fields but public properties in the interop (checked with ilspycmd on both branches).
- Clicks that land outside the terrain square are ignored.
- The large-map canvas is screen-space overlay. Si_Spectator logged "corners = pixels" in game three times on 2026-10-25, and the prefab's `Canvas, BIG map` has render mode 0. This mod checks the corner z once per scene and refuses clicks if the canvas is not overlay.

## 6. Stretch goal: the Tab "Large Minimap" for the commander. **Skipped (not cheap).**

- `MinimapToggle` (106:Silica.UI/MinimapToggle.cs) opens the large minimap on Tab (Global/Large Minimap) only when all of these hold:
  - `!StrategyMode.InStrategyMode`;
  - `UIManager.CurrentMode == UIMode.FPS`;
  - the LargeMap is not open.
- In the old AssetRipper export `UIManagerSol.prefab`, the toggle sits on `UIManagerSol/UIs/FPS/Canvas, Minimap`. That canvas holds `Sol_Minimap` (`PositionAndScaleToView: 1`) and `Sol_LargeMinimap` (inactive, `DynamicPlayerIcon: 1`).
- `UIManager.MinimapCanvas` is that same `Canvas, Minimap` GameObject. `UIManager.SwitchUIMode` **deactivates it in every RTS mode** (RTSHuman / RTSAlien / RTS / RTSSandbox, 106:Silica.UI/UIManager.cs:412-440). It is also deactivated for Alien and None.
  - So in strategy mode the toggle component is disabled (`OnDisable` unsubscribes Tab), and its whole canvas is off.
- Making it work would need all of the following:
  - re-activate the MinimapCanvas in RTS mode against UIManager, which resets it on every mode switch;
  - or re-parent `Sol_LargeMinimap` into an RTS canvas;
  - plus our own Tab handling;
  - plus checks that its `TargetMapIconDisplay` works for a commander, and that the FPS minimap's other children stay hidden.
- Its only advantage over the M map is that it is not a Menu, so it never blocks input. With this mod the M map does not block camera input either, so the gain is small.
- Tool used: `tools\prefab_fields.py <prefab> <field> [field...]`, a YAML grep by serialized field names. The export has no `.cs.meta` files, so script GUIDs cannot be resolved.

## 7. Bindings (old export `C:\SilicaAssets\ExportedProject\Assets\MonoBehaviour\SilicaInput.asset`, Dec 2025)

| Map / action | Bindings |
|---|---|
| Commander / Camera Movement | WASD, arrows, `<Gamepad>/leftStick` |
| Commander / Rotate Camera | Q/E, comma/period, right stick x |
| Commander / Zoom Camera | `<Mouse>/scroll/y`, right stick y |
| UI / Navigate | WASD, arrows, sticks, dpad. Only matters if a UI Selectable has focus. |
| UI / Move | gamepad left stick (`UIMovePanel` map pan) |
| UI / ScrollWheel | mouse scroll (`UIScroll` → `Map.Zoom`) |
| Global / Map | M, gamepad select |
| Global / Large Minimap | Tab, gamepad select |
| Global / Ping | middle mouse |

User rebinds of the Commander actions apply automatically: we override by action name, not by key.

## 8. Branch differences (every method we depend on)

Diffed 0947 vs 106:

| Code | Result |
|---|---|
| `ActionMapContainer.SetEnabled`, `EnableOverride` | identical (106 only adds gamepad tap/hold interaction state) |
| `GameInput.ReadValue`, `GetPreferredInput`, `SetActionEnableOverride`, `SwitchInput` | identical (106 adds key-share filter and PlayStation members) |
| `StrategyMode.MoveCameraToPosition` (0947:611 / 106:781), `UpdateCameraPosition` (1641 / 2074) | identical |
| `StrategyMode.UpdateCameraRotation` | 0947 rotates 90° inline; 106 uses `SnapYaw` / `SetYawAroundPivot`. Same input read. |
| `StrategyMode.Update` | same zoom / rotation / position reads; differences only in free look, line orders and click-order targeting |
| `Map`, `LargeMap`, `LargeMapOpener`, `LargeMapInputStateHandler`, `UIMovePanel`, `UIScroll`, `MenuManager.CloseMenu(Menu)` | identical |
| `MinimapToggle` | 106 adds a tap-to-close branch |
| `UIDrag` | 106 adds `ForwardOtherButtons` |

Interop public surface, 1.0.7 (`_silica_refs\client_test`) vs the installed 1.0.10 (ilspycmd):
- `GameInput`, `ActionMapContainer`, `LargeMap`, `MinimapToggle`: identical.
- `Map`: + `GetIconSize`, `IconSizeOfMissionArea`, `IsOnWorldSpaceCanvas`.
- `StrategyMode`: + sell-structures members, − `primaryPressPosition`. None of these is used.

## 9. Open risks (only an in-game test settles them)

1. **IL2CPP inlining.** If IL2CPP inlined the `ReadValue` checks, StrategyMode might read the action differently. The override is a field on the container and the InputAction really is enabled, so even a direct `InputAction.ReadValue` works. Low risk.
2. **Map follow push.** `Map.UpdateZoom` rescales `localPosition` every LateUpdate (Map.cs:257-261). The push should persist, but it could jitter by a frame. Turn off `MapFollowsCamera` if it looks bad.
3. **UI/Navigate on WASD.** If a map Selectable has keyboard focus (e.g. after clicking a scoreboard button), WASD also moves the UI selection. This is cosmetic.
4. **IMGUI text fields of other mods** (Si_ModUI panel) are not covered by `UIManager.TextInputActive`. Typing in such a field while the M map is open as commander moves the camera.
5. **`IsPressed()` on the 2D composite** is used only to start the follow window. If it never fires, the map simply does not follow.
6. **Commander role change while the map is open.** `LargeMap.OnCommanderChanged` closes the map (LargeMap.cs:155-161) and the gate closes the same frame. This should be fine.

## 10. 0.1.1: in-game result of 0.1.0, direct drive mode, diagnostics

**In-game result of 0.1.0.** Release 1.0.10 client, Centauri commander vs AI, session log `session-20261026T155950658Z.log`:
- The registered LargeMap is on `Centauri_ScoreBoard`; its Map is `Map Window`.
- The gate worked: one NAV ON / NAV OFF pair per M press.
- The map viewport is screen-space overlay and fills the screen (2560x1369).
- There were no errors and no heal line.
- **The camera did not move.** No `Jump:` line was logged, so no click-to-jump happened. With Debug off, the one `Map UI space` line cannot tell whether it came from a click or from a map-follow push.

**Logs checked for vanilla warnings:**
- There is no Unity `Player.log`; Silica writes its DebugConsole log to `Documents\Silica\Game.log`. Its 16:59-17:11 session has no `GameInput.SetActionEnableOverride` warning (neither "not registered" nor "not present") and no warning, error or exception at all.
- `MelonLoader\Latest.log` has no forwarded Unity warnings.
- So the action names and the Commander map were valid.

**Current code (1.0.9 managed decompile of today's dedicated-server SilicaCore.dll, 16:30, scratch `C:	mp\silica_decompile_109`):**
- `ActionMapContainer`, `GameInput.GetPreferredInput`, `ReadValue` and `SetActionEnableOverride` are unchanged.
- In `StrategyMode`, the camera path is unchanged: `Update` 2375, `UpdateCameraPosition` 2176-2186, `ClampPositionToCameraBounds` 2060-2079, and `ClampCameraHeight` 2244-2263, which writes `base.transform.position` back.
- So the failure is not visible in the managed code. It has to be one of:
  - (a) the overridden InputAction does not read the keys;
  - (b) StrategyMode.Update does not run or write the rig while the menu is open;
  - (c) `Game.CurrentCamera` does not follow.

**Diagnostics (`Diag.cs`, always on while NAV is on):**
- On NAV ON: whether each nav action exists (`GetAction`, plus `_actionMaps[Commander].actionContainers[name].EnableOverride`), followed by one state line.
- While a camera key is physically held: one line per 0.5 s with:
  - `CurrentInput` and the control scheme;
  - per action: enabled, `actionMap.enabled`, `ReadValueAsObject()` (non-generic), `controls.Count` and the override flag;
  - raw key states;
  - rig position, yaw, enabled / activeInHierarchy, heightPct and smooth;
  - camera name, position, enabled and active;
  - `TextInputActive`, `IsPaused`, `timeScale` and `GetCameraEnabled`;
  - the rig and camera deltas since the previous line, with explicit verdicts "RIG MOVED, CAMERA DID NOT" and "RIG DID NOT MOVE".
- On every left press while NAV is on: pointer, viewport rect (inside or outside), verdict and block reason, the world point, the top UI raycast hit, and sceneBounds / sceneOffset. Release outcomes (drag, held too long, outside terrain) are logged without Debug.

**DriveMode (pref, default `direct`).**
- `override` is the 0.1.0 route, unchanged.
- `direct` never touches EnableOverride for Move or Rotate (Zoom stays on the override route, opt-in). Each frame it reads `Keyboard.current` and writes `StrategyMode.Instance.transform` (`Drive.cs`).
- Vanilla `Update` starts from `transform.position`, clamps to `CameraBounds`, recomputes the terrain-follow height, writes the position back and smooth-damps the camera. Our movement is therefore processed exactly like vanilla keyboard input, whatever the script order (at most one frame late).
- We clamp x/z to `CameraBounds` ourselves as well.

| | Formula (copied from vanilla) |
|---|---|
| Move | `speed = (CameraHeightPct*0.9+0.1)*600*dt*(m_SmoothMovement?0.5:1)`, `dt = clamp(unscaledDeltaTime,0,0.033)`; `right = transform.right`, `forward = Cross(right, up).normalized`; diagonals normalized (2DVector composite); times `CameraSpeed`. |
| Rotate | smooth: `yaw += 120*dir*dt`; stepped: `SnapYaw` once per press. Uses a copy of `SetYawAroundPivot`'s math, because the 0.9.47 interop has neither `SetYawAroundPivot` nor `SnapYaw` (0.9.47 inlines them). |
| Keys | Hardcoded vanilla defaults: W/A/S/D + arrows, Q/E + comma/period. **Rebinding is not honoured.** |

If direct mode also shows no movement, the diagnostics show whether the rig moved and the camera did not (b/c), or whether the rig never moved.

## 11. 0.1.2: in-game result of 0.1.1, click guard rewrite

**In-game result of 0.1.1** (Release 1.0.10 client, `session-20261026T162137877Z.log`):
- **Direct drive works.** The user confirmed it. With W/D held, the rig went from (-2085.9,548.2,-2482.6) to (-1613.4,548.2,-1730.8) and the camera followed.
- So hypotheses (b) and (c) from §10 are false: vanilla `StrategyMode.Update` runs, clamps and smooth-damps the camera while the map is open. The 0.1.0 failure lies in the override input path (a).
  - The cause is still unknown. The 0.1.1 diagnostics only ran in direct mode, where no override is set.
  - A run with `DriveMode=override` is pending (TEST_CHECKLIST step 6), for a note to the game developer.
- **Click-to-jump was rejected every time.** All three clicks on bare terrain logged `BLOCKED - over a button`.
  - The world points were plausible: sceneBounds (6000, 125, 6000), sceneOffset (0, 186, 0).
  - The cause was `UIManager.Instance.MouseOverButton`. UIManager sets it when any GraphicRaycaster hit under the pointer has a `Button` component (UIManager.cs:475-482), and it is true over the map image.

**New guard (`MapClick.cs` `IsMapPoint`, 0.1.2).** `MouseOverButton` is no longer used.
1. The pointer must be inside the `Map.Viewport` screen rect (overlay canvas). Otherwise: BLOCK "outside the map viewport".
2. `EventSystem.current.RaycastAll(PointerEventData{position}, Il2CppSystem List<RaycastResult>)` gives the top (first) result:

| Top hit | Result |
|---|---|
| No hit | ALLOW (the geometry check already passed) |
| Not a descendant of `map.transform` ("Map Window"): scoreboard, legend, toolbar, respawn menu, chat | BLOCK "UI outside the map" |
| Inside the Map subtree, and the object or a parent up to and including the Map root has an interactive control | BLOCK |
| Inside the Map subtree (MapLayer, FogLayer, Viewport, IconParent, MapIcons, ...) with no such control | ALLOW |

   - The interactive controls are `Button`, `Toggle`, `Scrollbar`, `Slider`, `Dropdown`, `InputField`, `TMP_InputField` and `TMP_Dropdown`. They are matched by Il2Cpp type name, so no TextMeshPro reference is needed.
3. **If the raycast throws or there is no EventSystem (geometric fallback):** ALLOW unless the pointer lies inside the screen rect of an active `Selectable` under the LargeMap that is not under `map.transform`.

**Logging:** every press logs the top hit with its parent chain (`object < parent < ...`, up to 7 levels) and the hit count.

**Unchanged:** drag vs click (6 px / 1 s), left button only, middle mouse (ping) untouched.

## 12. 0.1.3: in-game result of 0.1.2, map surface in the click walk

**In-game result of 0.1.2.** Every click was still blocked, every time with `BLOCKED - Button 'Map' on the map`:
- on terrain: top hit `Tiling2 < Map < Map Mask < Map Window < Panel < Wrapper < Centauri_ScoreBoard` (4 hits);
- on an icon: `Graphic < LargeMapIcon(Clone) < Icons < Map < Map Mask < Map Window < Panel` (5 hits).

The MapLayer object "Map" itself carries a Unity `Button`.

**What that Button is.** It is `RespawnMenu.MapButton`:
- In the old export, `Centauri_RespawnMenu.MapButton` points to the Button on `.../Map Window/Map Mask/Map`, and so does the Sol counterpart.
- Its persistent `m_OnClick` is empty in both prefabs.
- `RespawnMenu.Awake` adds the only listener, `OnMapClick` → `SelectSpawnPointGroup(null)` (RespawnMenu.cs:143-148, 540-543; the same in 0947). That clears the respawn panel's selected spawn-point group, a purely local UI state change.
- The commander gate excludes `GetPickSpawn()`, and `LargeMap.Open` activates the RespawnMenu only in pick-spawn (LargeMap.cs:80-89). Even if the listener fires on our click, it only clears an already empty selection in an inactive panel.
- **No conflict, not suppressed.** The `LargeMapIcon` / `MapIcon` prefabs have no Button.

**Walk (0.1.3).**
1. A top hit outside `map.transform` is blocked, as before.
2. Otherwise, walk from the top hit upward. For each object `t`:
   1. If `t` is the MapLayer, or an ancestor of the MapLayer (`layer.IsChildOf(t)`: the Viewport "Map Mask" and the Map root "Map Window"), or the IconParent: **ALLOW**. This is the map surface; controls on these objects are ignored.
   2. If `t` has a `MapIcon` component: **ALLOW**. The click jumps to that spot.
   3. If `t` carries Button, Toggle, Scrollbar, Slider, Dropdown, InputField, TMP_InputField or TMP_Dropdown: **BLOCK**.
   4. Otherwise go on to the parent. Reaching the Map root means ALLOW.

The two logged cases now give ALLOW at `Map` and at `LargeMapIcon(Clone)` respectively.

**Shift speed boost (also in 0.1.3).**
- Binding in `SilicaInput.asset`: Commander / **Camera Speed Boost** = `<Keyboard>/shift` (the either-shift control, so left or right) plus `<Gamepad>/leftStickPress`. Commander / Fast Modifier = `<Keyboard>/leftShift`, but `MoveFastModifierHeld` is hard-coded `false` and it is unused for the camera.
- Vanilla `UpdateSpeedBoostHotkey` (106:3442-3446, 109:3545-3549) sets `CameraSpeed = 5` while the button is held, else 1.
  - It runs from `UpdateHotkeys` at the end of `Update` (109:2507), after the `ClampPositionToCameraBounds` turbo multiply (109:2060-2062).
  - `GetButton` ignores EnableOverride, so behind the map it resets `CameraSpeed` to 1 every frame.

| Mode | Boost (pref `SpeedBoostFactor`, default 5; Shift read as `Keyboard.shiftKey`) |
|---|---|
| direct | Our translation is multiplied by the factor while Shift is held. If `StrategyMode.CameraSpeed` is ever != 1, that value is used **instead** (never both). |
| override | We write `StrategyMode.CameraSpeed = factor` while Shift is held. In either script order, the next clamp uses it before `UpdateHotkeys` resets it. |

The DIAG held line shows a `Shift` column, `boost xN` and `CameraSpeed`.
