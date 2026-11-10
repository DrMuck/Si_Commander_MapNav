using Il2Cpp;
using Il2CppSilica;
using Il2CppSilica.UI;
using Il2CppSilica.UI.Menus;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SiCommanderMapNav;

// Click on an own unit on the large map = take direct control of it (0.2.0).
//
// The vanilla path ("Direct Control Unit", F by default; 1015 = C:\tmp\silica_decompile_1015, Release 1.0.15 = the
// installed client; 0947 = Beta 0.9.47, same logic):
//   StrategyMode.UpdateHotkeys -> UpdateDirectControlHotkey (1015:StrategyMode.cs:3627-3651, 0947 same):
//     on GetButtonDown("Direct Control Unit", Commander):
//       DirectControlUnit set           -> RequestDirectControl(null)   (back to commander)
//       else InStrategyMode && exactly one selected object, not destroyed, is a Unit && unit.GetAllowDirectControl()
//                                       -> RequestDirectControl(unit)
//   RequestDirectControl(unit) is PUBLIC and just calls DirectControl(unit) (StrategyMode.cs:828-900). DirectControl
//   checks GetAllowDirectControl itself (839), sets DirectControlUnit, ToggleStrategyMode(false) (849), sends
//   NetworkLayer.SendPlayerSelectUnit(CurrentPlayer, unit) (865 = packet PlayerSelectUnit, NetworkLayer.cs:1609-1640),
//   and sets the Commander "Direct Control Unit" EnableOverride (891) so F keeps working from the unit's input map.
//   Return (F again, or unit destroyed/unselected): DirectControl(null) clears the override, ToggleStrategyMode(true)
//   (camera re-centred on the current camera position, 773) and re-selects the unit it left (876-887).
//   Nothing in it depends on HOW control was entered: the selection is only the hotkey's way of naming the unit; the
//   return path uses DirectControlUnit, not the selection.
//   Unit.GetAllowDirectControl (Unit.cs:1557-1568): !IsDestroyed && !ControlledBy && not in a driver compartment &&
//   Team == Team.CurrentTeam && !ObjectInfo.DisableDirectControl && GameMode.GetAllowDirectControl (GameMode.cs:238-253:
//   !ControlledBy && GameBegun && !GameOver && the local player is commander). Structures are not Units, so never.
//   The SERVER only checks Player.GetCanSwitchToUnit (NetworkLayer.cs:554-560, Player.cs:655-666: not already its unit,
//   not controlled by anyone) - no team check. So the client-side GetAllowDirectControl is the real gate, and we never
//   go around it: we pre-filter with it for a good pick, and RequestDirectControl checks it again.
//
// So we call exactly what the F key calls, with the unit the click names instead of the selected one. No selection change
// (DirectControl -> ToggleStrategyMode -> ClearInputActions clears the selection anyway, StrategyMode.cs:781, 1005-1027),
// no packet of our own: the only packet is the vanilla PlayerSelectUnit that DirectControl sends.
//
// Order: RequestDirectControl FIRST, then close the map, both in this OnUpdate. GameInput.UpdateInput is polled from
// GameManager.Update (GameManager.cs:359), so no input-map switch can happen between the two calls; the order is only
// about failure: if vanilla refuses (returns false), the map stays open and nothing changed. DirectControl touches no menu
// synchronously (the UI-mode switch is a request, UIManager.cs:312-354). Closing through MenuManager.CloseMenu runs
// LargeMap.Close (respawn panel, blur reset, LargeMap.cs:92-101) and keeps GetPreferredInput at UI-only for the 0.15 s
// close delay (GameInput.cs:303-306, UIOpenCloseHandler.cs:23/45-53), so the click's release cannot reach the unit's fire
// input. Next frame our gate sees the map closed and turns nav off exactly as on a normal close (NAV OFF, overrides
// cleared, click state reset).
//
// Pick: (1) the UI raycast under the pointer - the first MapIcon hit whose RelatedObject (= the Target's Owner,
// TargetMapIconDisplay.cs:53) is an eligible Unit, i.e. what the player sees under the cursor; (2) otherwise the nearest
// eligible unit of Team.CurrentTeam whose projected map position (WorldToMapScreen = the icon placement,
// MapIconDisplay.cs:104-112) lies within ControlPickRadiusPx. (2) needs no generic Il2Cpp field access and also catches
// clicks a few px beside a small, moving icon; (1) settles overlaps the way the player sees them.
public sealed partial class ClientMod
{
    private const double DoubleClickSeconds = 0.35;   // first click's release -> second click's press
    private const float DoubleClickPx = 8f;

    private enum ControlClick { Off, Single, Double }

    private bool firstClickValid;
    private Vector2 firstClickPos;
    private double firstClickAt;
    private double pendingCloseAt;   // CloseMapOnClick in double mode: closing waits for a possible second click
    private bool controlModeWarned, hintFailedLogged;

    private void ResetControlClick() { firstClickValid = false; pendingCloseAt = 0; }

    private ControlClick ControlClickMode
    {
        get
        {
            string v = (prefControlClick.Value ?? "").Trim().ToLowerInvariant();
            if (v == "off") return ControlClick.Off;
            if (v == "single") return ControlClick.Single;
            if (v != "double" && v != "" && !controlModeWarned) { controlModeWarned = true; Write("ControlUnitClick = '" + prefControlClick.Value + "' is unknown, using \"double\"."); }
            return ControlClick.Double;
        }
    }

    // A valid short click on the map (press + release passed IsMapPoint and the drag / time checks in ClickUpdate).
    // pressTime = when this click's button went down.
    private void OnMapClick(LargeMap lm, Vector2 screen, double pressTime)
    {
        var mode = ControlClickMode;
        if (mode == ControlClick.Single && TryControlAt(lm, screen, "single click", false)) return;
        if (mode == ControlClick.Double)
        {
            bool second = firstClickValid && pressTime - firstClickAt <= DoubleClickSeconds && Vector2.Distance(screen, firstClickPos) <= DoubleClickPx;
            if (second)
            {
                firstClickValid = false;   // a third click starts a new pair
                if (TryControlAt(lm, screen, "double click", true)) return;
            }
            else { firstClickValid = true; firstClickPos = screen; firstClickAt = Now; }
        }
        if (prefClickToJump.Value) Jump(lm, screen);
    }

    // Jump() asks this before closing the map (CloseMapOnClick): in double mode the close waits DoubleClickSeconds so a
    // second click can still land on the open map.
    private bool DeferCloseForDoubleClick()
    {
        if (ControlClickMode != ControlClick.Double) return false;
        pendingCloseAt = Now + DoubleClickSeconds;
        Dbg("CloseMapOnClick deferred " + DoubleClickSeconds + " s (double-click window).");
        return true;
    }

    // Called every frame from ClickUpdate. Never closes while a press is in progress (a second click being made).
    private void PendingCloseUpdate(LargeMap lm)
    {
        if (pendingCloseAt <= 0 || Now < pendingCloseAt || pressValid) return;
        pendingCloseAt = 0;
        CloseLargeMap(lm, "click-to-jump");
    }

    // Picks the unit for a click and hands it to vanilla. True = control taken (map closed).
    // logAlways: one Write line even when nothing is in reach (deliberate double click); a single click on bare terrain
    // in single mode logs only with Debug.
    private bool TryControlAt(LargeMap lm, Vector2 screen, string how, bool logAlways)
    {
        var map = MapOf(lm);
        var sm = StrategyMode.Instance;
        if (map == null || map.MapLayer == null || sm == null) return false;
        string at = how + " at " + screen.x.ToString("0") + "," + screen.y.ToString("0");
        float radius = Math.Max(1f, prefPickRadius.Value);

        Unit hinted = HintUnitUnderPointer(map, screen, out string hintInfo);
        Unit best = hinted, refused = null;
        float bestPx = float.MaxValue, refusedPx = float.MaxValue;
        if (best != null && WorldToMapScreen(map, best.transform.position, out var hs)) bestPx = Vector2.Distance(hs, screen);

        var team = Team.CurrentTeam;
        var units = team == null ? null : team.Units;
        int inReach = 0;
        for (int i = 0; units != null && i < units.Count; i++)
        {
            var u = units[i];
            if (u == null || !WorldToMapScreen(map, u.transform.position, out var s)) continue;
            float px = Vector2.Distance(s, screen);
            if (px > radius) continue;
            inReach++;
            if (hinted != null) continue;   // only counting; the icon under the cursor wins
            if (Eligible(u)) { if (px < bestPx) { best = u; bestPx = px; } }
            else if (px < refusedPx) { refused = u; refusedPx = px; }
        }

        if (best == null)
        {
            string line = "Control: " + at + ": no controllable own unit within " + radius.ToString("0") + " px"
                + (refused != null ? "; nearest own unit " + Describe(refused) + " at " + refusedPx.ToString("0") + " px is not controllable (" + RefusalReason(refused) + ")" : "")
                + (hintInfo != null ? "; icon under the cursor: " + hintInfo : "") + ".";
            if (logAlways || refused != null || hintInfo != null) Write(line); else Dbg(line);
            return false;
        }

        string what = Describe(best) + " at world (" + best.transform.position.x.ToString("0") + ", " + best.transform.position.z.ToString("0") + "), "
            + (bestPx < float.MaxValue ? bestPx.ToString("0") + " px" : "? px") + (hinted != null ? ", icon under the cursor" : ", nearest of " + inReach + " in reach");
        bool ok;
        try { ok = sm.RequestDirectControl(best); }
        catch (Exception error) { Write("Control: " + at + " -> " + what + ": RequestDirectControl threw " + error.GetType().Name + "."); Error(error); return false; }
        if (!ok)
        {
            Write("Control: " + at + " -> " + what + ": REFUSED by vanilla RequestDirectControl (" + RefusalReason(best) + "); map stays open.");
            return false;
        }
        Write("Control: " + at + " -> " + what + ": taken (vanilla RequestDirectControl; F returns to commander view).");
        Dbg("after RequestDirectControl: InStrategyMode " + StrategyMode.InStrategyMode + ", DirectControlUnit " + (sm.DirectControlUnit != null) + ", CurrentInput " + GameInput.CurrentInput + (hintInfo != null ? ", hint " + hintInfo : ""));
        ResetControlClick();
        CloseLargeMap(lm, "taking control");
        return true;
    }

    // Mirrors the vanilla hotkey's own test (StrategyMode.cs:3646: !IsDestroyed && GetAllowDirectControl). 0.9.47's
    // GetAllowDirectControl lacks the IsDestroyed term (0947:Unit.cs:1418-1429), the hotkey checks it separately.
    private static bool Eligible(Unit u)
    {
        try { return u != null && !u.WasCollected && !u.IsDestroyed && u.GetAllowDirectControl(); }
        catch { return false; }
    }

    // First MapIcon among the UI raycast hits under the pointer whose RelatedObject is an eligible Unit (null if none).
    // hintInfo describes the top-most MapIcon hit (any kind) for the log, or null when the pointer is on no icon.
    private Unit HintUnitUnderPointer(Map map, Vector2 p, out string hintInfo)
    {
        hintInfo = null;
        try
        {
            var es = EventSystem.current;
            if (es == null) return null;
            var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(new PointerEventData(es) { position = p }, hits);
            var root = map.transform;
            for (int i = 0; i < hits.Count; i++)
            {
                var go = hits[i].gameObject;
                if (go == null || !go.transform.IsChildOf(root)) continue;
                MapIcon icon = null;
                for (var t = go.transform; t != null && t.Pointer != root.Pointer && icon == null; t = t.parent) icon = t.GetComponent<MapIcon>();
                if (icon == null) continue;
                var obj = icon.RelatedObject;
                var unit = obj == null ? null : obj.TryCast<Unit>();
                if (hintInfo == null)
                {
                    hintInfo = unit != null ? Describe(unit) + (Eligible(unit) ? "" : " (not controllable: " + RefusalReason(unit) + ")")
                        : obj != null ? "'" + NameOf(obj) + "' (not a unit)" : "an icon without object";
                    if (prefDebug.Value && ScreenRect(icon.RectTransform, out var r)) hintInfo += " [icon " + r.width.ToString("0") + "x" + r.height.ToString("0") + " px]";
                }
                if (unit != null && Eligible(unit)) return unit;
            }
        }
        catch (Exception error)
        {
            // Hint only: the projection pick still works without it.
            if (!hintFailedLogged) { hintFailedLogged = true; Write("Map icon lookup failed (" + error.GetType().Name + ": " + error.Message + "); picking by distance only (logged once)."); }
        }
        return null;
    }

    // Why vanilla would refuse this unit, mirroring Unit.GetAllowDirectControl (Unit.cs:1557-1568) and
    // GameMode.GetAllowDirectControl (GameMode.cs:238-253). For the log only; the decision is always vanilla's.
    private static string RefusalReason(Unit u)
    {
        try
        {
            if (u == null || u.WasCollected) return "gone";
            if (u.IsDestroyed) return "destroyed";
            var by = u.ControlledBy;
            if (by != null) return by.Pointer == (Player.CurrentPlayer == null ? IntPtr.Zero : Player.CurrentPlayer.Pointer) ? "already yours" : "controlled by player '" + by.PlayerName + "'";
            var seat = u.InCompartment;
            if (seat != null && seat.IsDriver) return "sits in a driver seat";
            var team = Team.CurrentTeam;
            if (u.Team == null || team == null || u.Team.Pointer != team.Pointer) return "other team";
            if (u.ObjectInfo != null && u.ObjectInfo.DisableDirectControl) return "unit type has DisableDirectControl";
            var mode = GameMode.CurrentGameMode;
            if (mode != null && (!mode.GameBegun || mode.GameOver)) return "round not running";
            return u.GetAllowDirectControl() ? "allowed now" : "refused by the game mode";
        }
        catch (Exception e) { return "? (" + e.GetType().Name + ")"; }
    }

    private static string Describe(Unit u)
    {
        try
        {
            var info = u.ObjectInfo;
            return "'" + NameOf(u) + "'" + (info != null ? " (" + info.UnitType + ")" : "");
        }
        catch { return "'?'"; }
    }

    private static string NameOf(BaseGameObject o)
    {
        try
        {
            var info = o.ObjectInfo;
            string n = info != null ? info.DisplayName : null;
            return string.IsNullOrEmpty(n) ? o.gameObject.name : n;
        }
        catch { return "?"; }
    }
}
