# Si_Commander_MapNav

A client-only MelonLoader mod for Silica (Il2Cpp game client). While you are commander and the large map (**M**) is open, the RTS camera still moves:

- **WASD / arrow keys** move the camera, and **Q / E** (or `,` / `.`) rotate it. You see the camera arrow move on the map.
- **Shift** speeds the camera up (x5, like vanilla).
- **Left click** on the map moves the camera to look at that spot. Dragging still pans the map.
- **Double left click** on one of your own units on the map takes direct control of it, as if you had selected it and pressed **F**. The map closes and you are in the unit. **F** takes you back to commander view as usual.
  - This calls the same vanilla method as the F key, so vanilla decides which units are allowed: your team only, not driven by another player, alive, no structures.
  - With `ControlUnitClick = single`, one click on a unit is enough (clicks elsewhere still jump).
- **Optional:**
  - the mouse wheel can zoom the camera as well (off by default, because the wheel already zooms the map);
  - the map can close after a click (off by default).
- When the map is zoomed in, it scrolls along while the camera moves with the keyboard.

Camera movement and click-to-jump send nothing to the server. Taking control of a unit sends exactly what the vanilla F key sends (the game's own "select unit" message). So the mod works on vanilla and modded servers alike. Version 0.2.0, author DrMuck.

**Status:**
- 0.1.3 is confirmed working in game on the Release 1.0.10 client: WASD/QE, the Shift boost and click-to-jump.
- 0.1.4 only moves the diagnostic lines behind the `Debug` pref.
- 0.2.0 adds taking control of a unit from the map. It compiles against the installed Release 1.0.15 client but has **not been run in game yet** (`docs/TEST_CHECKLIST.md` section G).
- The `override` drive mode (0.1.0) still does not move the camera; use `direct`, the default.
- `docs/DEV_NOTE.md` is a proposal to the developers to add this to vanilla.

## Install
Copy the DLL that matches the client's Steam branch into `<Silica>\Mods\`:
- `Client\bin\Release\default\Si_Commander_MapNav.dll` for the **default** branch (Beta 0.9.47);
- `Client\bin\Release\net6.0\Si_Commander_MapNav.dll` for the **test** branch (Release 1.0.x).

Packaged copies are in `Projects\_dist\Si_Commander_MapNav_<version>_<date>\`. Copy with the game closed, because the running game locks the DLL.

## Settings
`UserData\MelonPreferences.cfg`, category `[Si_Commander_MapNav]`:

| Key | Default | Meaning |
|---|---|---|
| Enabled | true | Master switch |
| KeyboardNav | true | WASD/arrows/QE move and rotate the camera while the map is open (keyboard and mouse only) |
| DriveMode | direct | `direct`: the mod moves the camera rig itself from the default keys (rebinding is not honoured). `override`: the 0.1.0 route, which re-enables the vanilla Commander camera actions. |
| SpeedBoostFactor | 5 | Camera speed factor while Shift is held with the map open (vanilla Camera Speed Boost = 5). 1 = off. |
| CameraZoomWhileMapOpen | false | The mouse wheel zooms the camera too (it zooms the map at the same time) |
| ClickToJump | true | A left click on the map jumps the camera there |
| CloseMapOnClick | false | Close the map after a jump. With `ControlUnitClick = double` the map closes 0.35 s after the click, so a double click still works. |
| ControlUnitClick | double | Take direct control of an own unit from the map. `double`: double left click on or near its icon (the first click still jumps). `single`: one click on or near an own unit takes control instead of jumping. `off`: never. |
| ControlPickRadiusPx | 14 | How close (screen pixels from the icon centre) a click must be to an own unit. Icons are 20-25 px. A click right on an icon always counts. |
| MapFollowsCamera | true | Scroll the map along while the keyboard moves the camera |
| PreferredInputFallback | false | Fallback: keep the whole Commander input map live behind the map. Use only if keyboard nav fails. Read at game start, so restart after changing it. |
| Debug | false | Verbose log: DIAG lines while the map is open and a camera key is held, one line per click on the map, overrides, map pushes, icon sizes at a control click |

Every control attempt logs one `Control: ...` line (unit, world position, pixel distance, result), also without Debug.

Logs go to the MelonLoader log and `UserData\Si_Commander_MapNav\session-*.log`.

## Build
```
cd Client
dotnet build -c Release                         # default branch -> bin\Release\default\
dotnet build -c Release -p:SilicaBranch=test    # test branch    -> bin\Release\net6.0\
```

The reference DLLs go in `include\client\<branch>\{net6,Il2CppAssemblies}`. That folder is git-ignored. Copy them from `Projects\_silica_refs\client_<branch>\`; you need MelonLoader, 0Harmony, Il2CppInterop.Runtime/Common, Il2CppSilicaCore, Il2Cppmscorlib, Il2Cppcom.rlabrecque.steamworks.net, UnityEngine.CoreModule, UnityEngine.UI and Unity.InputSystem.

**Verify against the installed client** (it has the same folder layout as include):
```
dotnet build -c Release -p:SilicaBranch=test "-p:ClientRefs=C:/SteamLibrary/steamapps/common/Silica/MelonLoader/" -p:OutputPath=bin/verify_installed/ -p:IntermediateOutputPath=obj/verify_installed/
```

## Docs
- `docs/DESIGN.md`: why vanilla freezes the camera, the EnableOverride mechanism and its pitfall, the decisions, the Tab-minimap findings and the branch diffs.
- `docs/TEST_CHECKLIST.md`: the in-game test steps.
- `tools/prefab_fields.py`: a YAML grep for AssetRipper prefabs, which finds components by their serialized field names.
