using HarmonyLib;
using Il2Cpp;
using Il2CppDebugTools;
using Il2CppSilica;
using Il2CppSilica.UI;
using Il2CppSilica.UI.Menus;
using UnityEngine;
using UnityEngine.InputSystem;
using InputMap = Il2CppSilica.GameInput.InputMap;

namespace SiCommanderMapNav;

// Keyboard camera navigation while the large map is open.
//
// Why the camera freezes in vanilla (identical in 0.9.47 and 1.0.6/1.0.10):
//   LargeMap is a Menu, so GameInput.GetPreferredInput returns only InputMap.UI while it is open (GameInput.cs:303-306),
//   GameInput.UpdateInput -> SwitchInput disables the Commander action map (GameInput.cs:616-631), and StrategyMode.Update,
//   which keeps running (StrategyMode.cs:2276-2380, gated only on Game.GetCameraEnabled + InStrategyMode), reads zero from
//   GameInput.ReadValue("Camera Movement" / "Rotate Camera", Commander) (StrategyMode.cs:2080, 2019) and from
//   GetAction(Commander, "Zoom Camera").ReadValue (2342).
//
// The lever: GameInput.SetActionEnableOverride(Commander, action, true) (GameInput.cs:633-651) sets
// ActionContainer.EnableOverride, whose SETTER enables / disables the Unity InputAction itself when the state differs
// (ActionMapContainer.cs:14-35). ActionMapContainer.SetEnabled(false) re-enables overridden actions after disabling the map
// (ActionMapContainer.cs:121-139), and GameInput.ReadValue returns the value when `action.enabled || EnableOverride`
// (GameInput.cs:190-208). So setting the override works in both orders (before or after the switch to the UI map) without
// any extra Enable() from us. Vanilla uses the same mechanism for "Direct Control Unit" (StrategyMode.cs:838/851).
//
// The trap is CLEARING it: EnableOverride=false calls action.Disable() whenever the action is enabled - also when the
// Commander map is enabled again (map closed in the same frame before our Update ran). That would leave WASD dead in normal
// RTS play until the next map switch. So after clearing we re-enable the action whenever GameInput.CurrentInput contains
// Commander (SetOverride below).
//
// Only ReadValue paths honour the override; GetButton/GetButtonDown/Up check action.enabled only (GameInput.cs:211-255),
// but the three camera actions are all read through ReadValue, and only these three are overridden: selection, orders and
// every other commander hotkey stay dead behind the map, as in vanilla.
//
// Bindings (SilicaInput.asset, old AssetRipper export): Camera Movement = WASD + arrows + gamepad left stick; Rotate Camera
// = Q/E + comma/period + right stick x; Zoom Camera = mouse scroll y + right stick y. While the map is open the UI map is
// live too: UI/Navigate also has WASD/arrows (harmless unless a UI Selectable has focus), UI/ScrollWheel drives the map's
// own zoom (UIScroll -> Map.Zoom on the map layer). Hence zoom is opt-in (CameraZoomWhileMapOpen) and keyboard nav is off
// for the gamepad scheme, where the left stick already pans the map (UI/Move -> UIMovePanel, LargeMapInputStateHandler).
public sealed partial class ClientMod
{
    private const string MoveAction = "Camera Movement", RotateAction = "Rotate Camera", ZoomAction = "Zoom Camera";
    private static readonly string[] NavActions = { MoveAction, RotateAction, ZoomAction };

    private readonly HashSet<string> overridden = new();
    private bool navActive;         // gate open: commander, strategy mode, large map open, no text input ...
    private static bool fallbackWanted;   // read by the GetPreferredInput postfix
    private string lastBlock = "";
    private double followUntil;
    private bool healLogged;

    // Why navigation is off right now, or null when it is on. Checked every frame (no Menu virtuals patched).
    private string BlockReason(out LargeMap lm)
    {
        lm = null;
        if (!prefEnabled.Value) return "disabled in prefs";
        if (Game.InMainMenu) return "main menu";
        var player = Player.CurrentPlayer;
        if (player == null) return "no local player";
        if (MenuManager.Instance == null) return "no MenuManager";
        lm = FindLargeMap();
        if (lm == null) return "no large map registered";
        if (!IsOpen(lm)) return "large map closed";
        if (!StrategyMode.InStrategyMode || StrategyMode.Instance == null) return "not in strategy mode";
        if (!player.IsCommander) return "not commander";
        if (UIManager.TextInputActive) return "text input active";
        if (DebugConsole.IsOpen) return "debug console open";
        var mode = GameMode.CurrentGameMode;
        if (mode != null && mode.GetPickSpawn()) return "respawn selection";
        return null;
    }

    private void NavUpdate()
    {
        string block = BlockReason(out var lm);
        bool active = block == null;
        if (active != navActive)
        {
            navActive = active;
            if (active) { Write("NAV ON: large map open as commander (" + GameInput.CurrentControlScheme + "). " + PrefSummary()); DiagOnNavOn(); }
            else Write("NAV OFF: " + block + ".");
            if (!active) { ResetClick(); ResetDrive(); followUntil = 0; }
        }
        else if (!active && block != lastBlock) Dbg("blocked: " + block);
        lastBlock = block ?? "";

        bool keyboard = active && prefKeyboardNav.Value && GameInput.CurrentControlScheme != EControlScheme.Gamepad;
        bool direct = IsDirectMode;
        // DriveMode=override (0.1.0): EnableOverride on the Commander actions, vanilla StrategyMode.Update reads them.
        // DriveMode=direct (0.1.1 default): Move/Rotate are NOT overridden; Drive.cs moves the rig itself (DirectDrive).
        // Zoom stays on the override route in both modes (opt-in, default off).
        SetOverride(MoveAction, keyboard && !direct);
        SetOverride(RotateAction, keyboard && !direct);
        SetOverride(ZoomAction, keyboard && prefZoom.Value);
        fallbackWanted = keyboard && prefFallback.Value;
        if (keyboard) HealOverrides();
        if (keyboard && direct) DirectDrive();

        if (active && prefClickToJump.Value) ClickUpdate(lm);
        else ResetClick();

        if (keyboard)
        {
            bool held = NavKeysHeld();
            if (prefFollow.Value && held) followUntil = Now + 0.75;   // camera smoothing keeps moving a bit
            DiagUpdate(held);
        }
    }

    // Sets / clears our override on one Commander action. Only touches actions we set ourselves.
    private void SetOverride(string action, bool on)
    {
        if (on == overridden.Contains(action)) return;
        GameInput.SetActionEnableOverride(InputMap.Commander, action, on);
        if (on) { overridden.Add(action); Dbg("override ON  " + action + " (CurrentInput " + GameInput.CurrentInput + ")"); return; }
        overridden.Remove(action);
        // The setter just disabled the InputAction. If the Commander map is live again (map closed), that would kill the
        // action for normal RTS play: re-enable it (see the class comment).
        bool commanderLive = (GameInput.CurrentInput & InputMap.Commander) != 0;
        if (commanderLive)
        {
            var a = GameInput.GetAction(InputMap.Commander, action);
            if (a != null && !a.enabled) a.Enable();
        }
        Dbg("override OFF " + action + (commanderLive ? " (Commander map live, action re-enabled)" : ""));
    }

    // Belt and braces: vanilla ForceDisableAll (GameInput.cs:695-706, on SetPlayerInput / SetInputModule) disables every
    // action; it is always followed by EnableAllWithOverride, which re-enables ours. If anything else ever disables one of
    // our overridden actions while the map is open, enable it again (logged once per session).
    private void HealOverrides()
    {
        foreach (var name in overridden)
        {
            var a = GameInput.GetAction(InputMap.Commander, name);
            if (a == null || a.enabled) continue;
            a.Enable();
            if (!healLogged) { healLogged = true; Write("Overridden action '" + name + "' was disabled by something else; re-enabled (logged once)."); }
        }
    }

    // Restore vanilla input: clear every override we set. Safe to call any time (scene load, unload, fault, quit).
    private void ClearOverrides(string why)
    {
        fallbackWanted = false;
        if (navActive) { navActive = false; Write("NAV OFF: " + why + "."); }
        if (overridden.Count == 0) return;
        foreach (var name in overridden.ToList())
        {
            try { SetOverride(name, false); }
            catch (Exception error) { overridden.Remove(name); Error(error); }
        }
        Write("Overrides cleared (" + why + ").");
    }

    // ---- MapFollowsCamera: keep the camera icon inside the visible map while the keyboard moves the camera ----
    // The map scrolls by exactly the overshoot ("edge push"), so the icon stays at the edge of an inner margin instead of
    // leaving the viewport. Only right after keyboard movement (followUntil) and never while the left button is held, so a
    // mouse drag of the map (UIDrag on the map layer) is never fought. Map.UpdateZoom / ClampPosition (Map.cs:246-262,
    // 214-244) start from the current localPosition every LateUpdate, so the push persists and stays clamped by vanilla.
    // Screen space: the BIG map canvas is screen-space overlay (Si_Spectator logged "corners = pixels" in game), so
    // RectTransform world positions are pixels.
    private const float FollowMargin = 0.12f;
    private void FollowLateUpdate()
    {
        if (!prefFollow.Value || Now >= followUntil) return;
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.isPressed) return;
        var map = MapOf(largeMap);
        if (map == null || map.MapLayer == null || map.Viewport == null) return;
        var cam = Game.CurrentCamera;
        if (cam == null) return;
        if (!WorldToMapScreen(map, cam.transform.position, out var icon) || !ScreenRect(map.Viewport, out var vp) || !spaceFlat) return;
        float mx = vp.width * FollowMargin, my = vp.height * FollowMargin;
        var inner = Rect.MinMaxRect(vp.xMin + mx, vp.yMin + my, vp.xMax - mx, vp.yMax - my);
        Vector2 push = Vector2.zero;
        if (icon.x < inner.xMin) push.x = inner.xMin - icon.x; else if (icon.x > inner.xMax) push.x = inner.xMax - icon.x;
        if (icon.y < inner.yMin) push.y = inner.yMin - icon.y; else if (icon.y > inner.yMax) push.y = inner.yMax - icon.y;
        if (push == Vector2.zero) return;
        var rt = map.MapLayer.rectTransform;
        rt.position = rt.position + new Vector3(push.x, push.y, 0f);
        Dbg("map push " + push);
    }

    // ---- fallback (pref PreferredInputFallback, default off): keep the whole Commander map enabled ----
    // GetPreferredInput has no ref/out parameters, so a postfix is safe on Il2Cpp. Broader than the override route: every
    // commander hotkey and world click (selection, orders) would be live behind the map. Installed only when the pref is on
    // at game start (assembly-level HarmonyDontPatchAll in ClientMod.cs), so a patch problem can never touch the default path.
    private void InstallFallbackPatch()
    {
        if (!prefFallback.Value) return;
        try
        {
            var original = AccessTools.Method(typeof(GameInput), nameof(GameInput.GetPreferredInput));
            HarmonyInstance.Patch(original, postfix: new HarmonyMethod(typeof(ClientMod), nameof(PreferredInputPostfix)));
            Write("Fallback patch installed: GameInput.GetPreferredInput postfix (PreferredInputFallback = true).");
        }
        catch (Exception error) { Write("Fallback patch FAILED: " + error.Message); }
    }
    private static void PreferredInputPostfix(ref InputMap __result)
    {
        if (fallbackWanted && (__result & InputMap.UI) != 0) __result |= InputMap.Commander;
    }
}
