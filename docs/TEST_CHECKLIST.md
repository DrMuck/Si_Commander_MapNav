# Si_Commander_MapNav 0.1.1: in-game test checklist

**Setup:** the client is on the Steam test branch. `Mods\Si_Commander_MapNav.dll` is the test build. Any server works.

**Logs:** MelonLoader console / `MelonLoader\Latest.log`, and `UserData\Si_Commander_MapNav\session-*.log`. Set `Debug = true` in `UserData\MelonPreferences.cfg` `[Si_Commander_MapNav]` for override, click and push detail.

## 0. 0.1.1 first: the diagnostic run (do this before anything else)
- Install: with the game **closed**, copy `Mods\~pending\Si_Commander_MapNav.dll` over `Mods\Si_Commander_MapNav.dll`, then delete the `~pending` folder. The log must say `READY 0.1.1 ... DriveMode=direct`.
1. Join as commander and open the map (M). The log shows `NAV ON` and two `DIAG nav-on` lines.
2. **Hold W for 2 s with the map open**, then release.
3. Left-click once on an empty map spot (do not drag). The log shows `Click press at ...` and either `Jump: ...` or the reason.
4. Close the map, quit the game, and **send the session log** (`UserData\Si_Commander_MapNav\session-*.log`, the newest one).
5. Optional second run: set `DriveMode = override` and repeat steps 1-4. Same log request; it shows why the 0.1.0 route fails.

## A. Load
1. Start the game. The log shows `READY 0.1.1 - Commander MapNav. Enabled=True KeyboardNav=True DriveMode=direct CameraZoomWhileMapOpen=False ClickToJump=True CloseMapOnClick=False MapFollowsCamera=True ...`.
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
