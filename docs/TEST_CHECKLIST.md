# Si_Commander_MapNav 0.2.0: in-game test checklist

**Setup:** the client is on the Steam test branch. `Mods\Si_Commander_MapNav.dll` is the test build. Any server works.

> **0.2.0 is new and not yet run in game: start with section G** (take control from the map), then re-run B9, C12 and C14 to make sure nothing regressed. The load line now reads `READY 0.2.0 ... ControlUnitClick=double ControlPickRadiusPx=14 ...`.

**Logs:** MelonLoader console / `MelonLoader\Latest.log`, and `UserData\Si_Commander_MapNav\session-*.log`. Set `Debug = true` in `UserData\MelonPreferences.cfg` `[Si_Commander_MapNav]` for override, click and push detail.

> **Since 0.1.4, the `DIAG ...` and `Click press ...` lines appear only with `Debug = true`.** Set Debug first for the diagnostic runs in sections 0 and 0b.
> 0.1.3 passed steps 2-5 in game on 2026-10-26: WASD/QE, Shift boost and click-to-jump.

## 0. The diagnostic run (0.1.1 – 0.1.3, done)
- Install: with the game **closed**, copy `Mods\~pending\Si_Commander_MapNav.dll` over `Mods\Si_Commander_MapNav.dll`, then delete the `~pending` folder. The log must say `READY 0.1.4 ... DriveMode=direct`.
1. Join as commander and open the map (M). The log shows `NAV ON` and two `DIAG nav-on` lines.
2. **Hold W for 2 s with the map open**, then release.
3. Left-click once on an empty map spot (do not drag). The log shows `Click press at ...` and either `Jump: ...` or the reason.
4. Close the map, quit the game, and **send the session log** (`UserData\Si_Commander_MapNav\session-*.log`, the newest one).
4b. 0.1.3: with the map open, hold **Shift + W**. The camera moves about 5x faster; the DIAG line shows `Shift1 boost x5`.
5. 0.1.3: click on 3 empty map spots and on 1 unit icon. Each press logs `Click press at ...: map point ... top UI hit '<object> < <parents> ...'`, followed by `Jump: camera to ...`. If one is still BLOCKED, send the log: the top-hit chain shows which UI element swallowed it.

## 0b. Override diagnostics run (answers why 0.1.0 read nothing; for a note to the developer)
6. With the game **closed**, set `DriveMode = "override"` under `[Si_Commander_MapNav]` in `UserData\MelonPreferences.cfg`.
7. Join as commander, open the map (M), **hold W for 2 s**, release, close the map, and quit.
8. Send the newest `UserData\Si_Commander_MapNav\session-*.log`. The `DIAG held` lines show, per action:
   - `enabled` / `mapEnabled` / `value` / `controls`;
   - the `EnableOverride` flag;
   - raw W, and whether the rig moved.
9. Set `DriveMode = "direct"` again afterwards.

## A. Load
1. Start the game. The log shows `READY 0.2.0 - Commander MapNav. Enabled=True KeyboardNav=True DriveMode=direct ... ClickToJump=True CloseMapOnClick=False ControlUnitClick=double ControlPickRadiusPx=14 MapFollowsCamera=True ...`.
   - The prefs category `[Si_Commander_MapNav]` appears in MelonPreferences.cfg after the first quit.
2. There are no `ERROR` lines in the session log during load and while joining a server.

## B. Keyboard navigation (core)
3. Join a server and take the commander seat (RTS view). Press **M**.
   - Log: `LargeMap found: ...` (once per scene), `Map UI space: screen-space overlay ...`, `NAV ON: large map open as commander (KeyboardMouse)`.
4. With the map open, hold **W / A / S / D**. The camera arrow on the map moves; the world behind the map moves too (visible at the map edges if not fully covered).
5. **Arrow keys** do the same.
6. **Q / E** rotate the camera. The arrow on the map turns, in 90° steps unless smooth movement is on.
7. The **mouse wheel** over the map zooms only the map, not the camera (`CameraZoomWhileMapOpen = false`).
8. Press **M** again to close. Log: `NAV OFF: large map closed.`
9. **Critical:** right after closing, WASD / Q / E / wheel still work in normal RTS play.
   - Repeat 5-10 times with fast M toggling while holding W.
   - If WASD is ever dead after closing: note it, check the log for `override OFF ... (Commander map live, action re-enabled)` (with Debug on), and send the log.
10. Close the map with **Escape** instead of M. Same as 9.
11. Box-select some units, open the map, then left-click and right-click on the map. No units are deselected and no move orders are issued behind the map (vanilla behaviour kept).

## C. Click-to-jump
12. Open the map and left-click a spot far from the camera.
    - The camera moves to look at that spot; the arrow ends up just "behind" the spot, pointing at it.
    - Log: `Jump: camera to (x, z) ...`.
    - The map stays open (`CloseMapOnClick = false`).
13. Click on a unit or structure icon: the camera jumps there too. No other vanilla action fires.
14. **Drag** the map with the left button: the map pans and the camera does **not** jump.
    - Debug log: `drag (N px), not a click`.
15. Click on the scoreboard, legend and any button in the map window. Nothing jumps; vanilla button behaviour is unchanged.
16. Click outside the terrain area (beside the map texture, still inside the viewport). Nothing happens.
17. Middle mouse on the map still pings as in vanilla. Right click does nothing new.
18. Set `CloseMapOnClick = true` (edit the cfg with the game closed, or use a prefs editor). A click jumps and closes the map. Log: `Large map closed after click-to-jump.`
    - WASD works right afterwards (as in step 9).

## D. Map follows camera
19. Zoom the map in strongly (wheel) and hold W until the arrow reaches the viewport edge. The map scrolls along, so the arrow stays about 12 % inside the edge.
20. Drag the map so the arrow is off-screen, without pressing keys. The map does **not** jump back (follow only runs right after keyboard movement).
21. If it jitters: set `MapFollowsCamera = false` and note it.

## E. Gate / edge cases
22. As an **FPS player** (not commander), open M: no `NAV ON`; WASD behaves as vanilla.
23. As commander with the map open, open chat (Enter) and type text containing w/a/s/d. The camera does not move. Log: `NAV OFF: text input active`, then `NAV ON` again after closing chat.
24. Open the debug console with the map open. Nav turns off.
25. Lose or leave the commander role while the map is open. The map closes (vanilla) and the log shows `NAV OFF`. In FPS, movement is normal.
26. Map change / round end with the map open. Log: `Overrides cleared (scene ...)` if it was active. Nothing is stuck in the next round.
27. Disconnect while the map is open, then rejoin as commander. Everything works.
28. Gamepad (if available): with the gamepad as active scheme, the map keeps the vanilla stick pan; keyboard nav stays off.
29. `Enabled = false`: pure vanilla behaviour (frozen camera with the map open).

## F. Fallback (only if B fails)
30. If WASD does nothing with the map open (step 4) although `NAV ON` is logged:
    - set `PreferredInputFallback = true` and **restart the game** (log: `Fallback patch installed`), then retest step 4;
    - then retest step 11. With the fallback, clicks behind the map may select or order units: report what happens.

## G. Take control of a unit from the map (0.2.0)
Commander in RTS view, map open (M). Ideally a server where a second human is on your team (for step 36). Every attempt logs one `Control: ...` line in the session log.

31. **Double-click an own unit icon** (an AI soldier or vehicle, not a structure).
    - The first click jumps the camera there (`Jump: ...`).
    - The second click takes control. The map closes and the camera lerps into the unit; you are in first/third person as after F.
    - Log:
      - `Control: double click at x,y -> '<name>' (<type>) at world (x, z), N px, icon under the cursor: taken (vanilla RequestDirectControl; ...)`;
      - then `Large map closed after taking control.` and `NAV OFF: ...`.
    - Drive, shoot and move the unit: everything behaves like vanilla direct control.
32. **Press F.** You are back in commander view (vanilla). The unit you left is selected and the camera looks at it.
33. **Right after that, press M again.** NAV ON is logged. WASD / Q / E move the camera, Shift boosts, a single click jumps. Close the map with M: WASD works in normal RTS play (as in B9).
34. **Re-enter a second unit.** Open the map, double-click a different own unit: control is taken. Press F, then repeat once more with a third unit. Each time F returns you to commander view.
35. **Double-click an enemy icon** (visible enemy unit):
    - nothing is taken and the map stays open;
    - the log says `no controllable own unit within 14 px` and `icon under the cursor: '<name>' (...) (not controllable: other team)`, or names your nearest own unit if one is in reach.
36. **Double-click a unit driven by a human teammate** (the icon shows the player's name):
    - nothing is taken;
    - the log says `not controllable (controlled by player '<name>')`.
37. **Double-click a structure icon** (HQ, refinery):
    - nothing is taken (unless an own unit icon is within 14 px, then that unit is taken; the log line says which one);
    - the log says `icon under the cursor: '<structure>' (not a unit)`.
38. **Double-click empty terrain.** Both clicks jump the camera; nothing else happens. Log: `Control: double click at ...: no controllable own unit within 14 px.`
39. **Dead unit:** double-click the spot where an own unit just died (icon gone). Nothing is taken. A destroyed unit's icon disappears (vanilla `IsValid`), so this mainly checks that nothing odd happens.
40. **Slow double click** (about 0.5 s between the clicks): two jumps, no control. The window is 0.35 s from the first release to the second press.
41. **Drag then click:** drag the map, then double-click a unit. The drag never counts as a click (Debug: `drag (N px), not a click`).
42. **Two units on top of each other:** double-click the overlapping icons. One of them is taken. The log says `icon under the cursor` (the one drawn on top) or `nearest of K in reach`.
43. **`ControlUnitClick = single`** (edit the cfg with the game closed):
    - one click on an own unit takes control right away (no jump);
    - one click on terrain, an enemy or a structure jumps as before.
44. **`ControlUnitClick = off`:** double-clicks only jump, as in 0.1.4. No `Control:` lines are logged.
45. **`CloseMapOnClick = true` with `double`:**
    - a single click jumps, and the map closes about 0.35 s later;
    - a double click on a unit takes control (the map does not close between the two clicks).
46. **Pick radius:** if units are hard to hit, set `Debug = true`. The `Control:` line then carries `[icon WxH px]`. Set `ControlPickRadiusPx` to about half that width or a little more.
47. **Round end while controlling** (or the unit dies): vanilla puts you back to commander view. Opening M works as before.

**If it does not work, send the newest session log and look for:**
- no `Control:` line at all after a double click: the clicks did not count as a double click. With Debug, check the two `Click press` lines (BLOCKED?) and their times.
- `REFUSED by vanilla RequestDirectControl (<reason>)`: vanilla said no. The reason names the vanilla check.
- `RequestDirectControl threw ...` or `ERROR`: an interop problem; send the log.
- `Map icon lookup failed ... (logged once)`: the icon-under-cursor route is broken; the distance pick still works.
- `taken` but you are not in the unit (stuck in a free camera): the server did not confirm the switch. Press F to return, and note the server and mods.
- `Large map NOT closed (CloseMenu returned false) after taking control`: close the map with M or Escape; note it.