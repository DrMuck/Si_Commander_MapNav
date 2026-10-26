using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSilica.UI;
using Il2CppSilica.UI.Menus;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SiCommanderMapNav;

// Click-to-jump on the large map.
//
// A left press + release within DragThreshold pixels inside the map viewport moves the RTS camera to look at that spot:
// StrategyMode.Instance.MoveCameraToPosition(world) (StrategyMode.cs:781-786, identical in 0947:611) - pure local transform
// math + ClampCamera, no network, the same call vanilla uses for the next-tech-tier indicator (RTSNextTechTierIndicator.cs:34).
// It sets the camera PIVOT so the camera looks at the point; the map's camera arrow (camera position, Map.cs:358) therefore
// sits a bit "behind" the clicked spot, pointing at it.
//
// Vanilla mouse handling on the map (verified in the decompile + the old UIManagerSol prefab):
//   - left DRAG pans the map: UIDrag on the map layer "Map Window/Map Mask/Map" (UIDrag.cs, IBeginDrag/IDrag/IEndDrag only).
//     EventSystem only starts a drag beyond its drag threshold, so a press+release under DragThreshold is never a pan.
//   - wheel zooms the map: UIScroll on the same object -> Map.Zoom (UIScroll.cs, prefab m_MethodName: Zoom).
//   - NO vanilla click handler on the map or its MapIcons (MapIcon/ContextIcon implement no pointer interface; the
//     IPointerClickHandler implementers are ClickHandler and RTS panels). Middle mouse = Global/Ping (untouched),
//     right click untouched. UIMovePanel is the GAMEPAD left-stick pan (UI/Move), not a mouse drag.
// So a short left click on the map has no vanilla meaning; we give it one. Clicks on anything selectable (buttons,
// toggles, the scoreboard, the respawn menu, legend) or on any UI drawn above the map are left alone: the top-most UI
// raycast hit must lie inside the Map subtree and carry no interactive control up to the Map root (IsMapPoint, 0.1.2).
//
// Screen -> world is the exact inverse of the PlayerIcon placement (Map.cs:358-362):
//   icon: local = (rect.width * (w.x - off.x) / bounds.x, rect.height * (w.z - off.z) / bounds.z); screen = MapLayer.TransformPoint(local)
//   ours: local = MapLayer.InverseTransformPoint(screen);  w.x = off.x + local.x / rect.width * bounds.x;  w.z = off.z + local.y / rect.height * bounds.z
// with bounds/off = Map.sceneBounds / sceneOffset (terrain size / terrain centre, Map.cs:174-178; protected fields, public in
// the interop). Overlay canvas => world space = pixels (checked once per scene through the corner z, logged).
public sealed partial class ClientMod
{
    private const float DragThreshold = 6f;   // px between press and release; above = a drag (pan), not a click
    private const double MaxClickSeconds = 1.0;

    private bool pressValid;
    private Vector2 pressPos;
    private double pressAt;
    private bool spaceLogged, spaceFlat = true;
    private string lastTopHit = "";

    private void ResetClick() { pressValid = false; }

    private void ClickUpdate(LargeMap lm)
    {
        var mouse = Mouse.current;
        if (mouse == null || !Application.isFocused) { pressValid = false; return; }
        if (mouse.leftButton.wasPressedThisFrame)
        {
            pressPos = mouse.position.ReadValue();
            pressAt = Now;
            pressValid = IsMapPoint(lm, pressPos, out string why);
            DiagPress(lm, pressPos, pressValid, why);
        }
        if (!mouse.leftButton.wasReleasedThisFrame || !pressValid) return;
        pressValid = false;
        Vector2 release = mouse.position.ReadValue();
        float moved = Vector2.Distance(release, pressPos);
        if (moved > DragThreshold) { Write("Click release: drag (" + moved.ToString("0") + " px), not a click."); return; }
        if (Now - pressAt > MaxClickSeconds) { Write("Click release: held " + (Now - pressAt).ToString("0.0") + " s, not a click."); return; }
        Jump(lm, pressPos);
    }

    // 0.1.1 diagnostics: one line per left press while NAV is on (pointer, viewport, verdict, world point).
    private void DiagPress(LargeMap lm, Vector2 p, bool valid, string why)
    {
        var map = MapOf(lm);
        string inside = "?", world = "?";
        try { inside = map != null && map.Viewport != null && ScreenRect(map.Viewport, out var vp) ? (vp.Contains(p) ? "inside" : "OUTSIDE") + " viewport " + vp : "no viewport"; } catch (Exception e) { inside = "ERR " + e.GetType().Name; }
        try
        {
            world = map == null ? "no map" : ScreenToWorld(map, p, out var w) ? "(" + w.x.ToString("0") + ", " + w.z.ToString("0") + ")" : "outside terrain";
            if (map != null) world += " [sceneBounds " + map.sceneBounds + ", sceneOffset " + map.sceneOffset + "]";
        }
        catch (Exception e) { world = "ERR " + e.GetType().Name; }
        Write("Click press at (" + p.x.ToString("0") + "," + p.y.ToString("0") + "), " + inside + ": " + (valid ? "map point" : "BLOCKED - " + why) + ", world " + world + ", top UI hit '" + lastTopHit + "'.");
    }

    private void Jump(LargeMap lm, Vector2 screen)
    {
        var map = MapOf(lm);
        var strategy = StrategyMode.Instance;
        if (map == null || strategy == null) return;
        if (!ScreenToWorld(map, screen, out var world)) { Write("Click: outside the terrain area, no jump."); return; }
        var cam = Game.CurrentCamera;
        world.y = cam != null ? cam.transform.position.y : 0f;   // MoveCameraToPosition keeps the pivot height itself (StrategyMode.cs:784)
        strategy.MoveCameraToPosition(world);
        Write("Jump: camera to (" + world.x.ToString("0") + ", " + world.z.ToString("0") + ") from a map click at " + screen.x.ToString("0") + "," + screen.y.ToString("0") + ".");
        if (prefCloseOnClick.Value) CloseLargeMap(lm);
    }

    // True when a press at `p` (pixels, origin bottom-left) is a click on the bare map.
    //
    // Since 0.1.2: decided ONLY by what the click would hit in the UI, relative to the Map ("Map Window") subtree.
    // 0.1.1 also refused UIManager.Instance.MouseOverButton, which is true over the bare map image in game (all three
    // clicks on empty terrain were "BLOCKED - over a button"): UIManager sets it when any raycast hit has a Button
    // component (UIManager.cs:475-482), and the map layer itself evidently counts. That check is gone.
    //   1. pointer inside the Viewport ("Map Mask") screen rect, canvas is screen-space overlay;
    //   2. EventSystem.RaycastAll, top (first) result:
    //        BLOCK  top hit outside the Map subtree (map.transform = "Map Window"): scoreboard, legend, toolbar, respawn
    //               menu, chat, ...
    //        walk from the top hit upward (0.1.3):
    //          object is the MapLayer, an ancestor of it (Viewport "Map Mask", Map root), the IconParent or carries a
    //          MapIcon -> ALLOW (map surface; controls on these objects do not count: the MapLayer "Map" carries a Button,
    //          RespawnMenu.MapButton, which made 0.1.2 block every click);
    //          object carries an interactive control (Button, Toggle, Scrollbar, Slider, Dropdown, InputField,
    //          TMP_InputField, TMP_Dropdown - matched by Il2Cpp type name) -> BLOCK;
    //          otherwise continue with the parent;
    //        no hit at all -> ALLOW (the geometry check already passed).
    //   3. If the raycast is unavailable or throws: ALLOW unless the pointer is inside the screen rect of an active
    //      Selectable of the LargeMap that is NOT under the Map transform.
    private bool IsMapPoint(LargeMap lm, Vector2 p, out string why)
    {
        why = null;
        lastTopHit = "not checked";
        var map = MapOf(lm);
        if (map == null || map.MapLayer == null) { why = "no map"; return false; }
        var viewport = map.Viewport;
        if (viewport == null || !ScreenRect(viewport, out var vp)) { why = "no viewport"; return false; }
        if (!spaceFlat) { why = "map canvas is not screen-space overlay (unsupported)"; return false; }
        if (!vp.Contains(p)) { why = "outside the map viewport"; return false; }
        try { return TopHitAllows(map, p, ref why); }
        catch (Exception error)
        {
            lastTopHit = "raycast failed (" + error.GetType().Name + "), geometric fallback";
            return FallbackAllows(lm, map, p, ref why);
        }
    }

    private static readonly HashSet<string> InteractiveTypes = new()
    { "Button", "Toggle", "Scrollbar", "Slider", "Dropdown", "InputField", "TMP_InputField", "TMP_Dropdown" };

    private bool TopHitAllows(Map map, Vector2 p, ref string why)
    {
        var es = EventSystem.current;
        if (es == null) throw new InvalidOperationException("no EventSystem");
        var data = new PointerEventData(es) { position = p };
        var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        es.RaycastAll(data, hits);
        if (hits.Count == 0) { lastTopHit = "no hit"; return true; }
        var top = hits[0].gameObject;
        if (top == null) { lastTopHit = "null object"; return true; }
        var root = map.transform;
        lastTopHit = Chain(top.transform) + " (" + hits.Count + " hits)";
        if (!top.transform.IsChildOf(root)) { why = "UI outside the map ('" + top.name + "')"; return false; }
        var layer = map.MapLayer.transform;
        var icons = map.IconParent;
        for (var t = top.transform; t != null; t = t.parent)
        {
            // Map surface reached before any control on another object: allow. The surface is the MapLayer ("Map", which
            // carries RespawnMenu.MapButton - a Button whose only listener clears the respawn spawn-point selection), every
            // ancestor of it (Viewport "Map Mask", the Map root "Map Window"), the IconParent and any MapIcon object.
            if (t.Pointer == layer.Pointer || layer.IsChildOf(t) || (icons != null && t.Pointer == icons.Pointer)) return true;
            if (t.GetComponent<MapIcon>() != null) return true;
            string control = InteractiveOn(t);
            if (control != null) { why = control + " '" + t.gameObject.name + "' on the map"; return false; }
            if (t.Pointer == root.Pointer) return true;
        }
        return true;
    }

    // Raycast unavailable: inside the viewport (checked by the caller) and not over a LargeMap control outside the Map.
    private bool FallbackAllows(LargeMap lm, Map map, Vector2 p, ref string why)
    {
        var selectables = lm.GetComponentsInChildren<Selectable>(false);
        var root = map.transform;
        for (int i = 0; selectables != null && i < selectables.Length; i++)
        {
            var sel = selectables[i];
            if (sel == null || sel.transform.IsChildOf(root)) continue;
            if (ScreenRect(sel.transform.TryCast<RectTransform>(), out var r) && r.Contains(p)) { why = "over control '" + sel.gameObject.name + "' (fallback)"; return false; }
        }
        return true;
    }

    // Name of the first interactive UI control on this object, or null.
    private static string InteractiveOn(Transform t)
    {
        var components = t.GetComponents<Component>();
        for (int i = 0; components != null && i < components.Length; i++)
        {
            var c = components[i];
            if (c == null) continue;
            string name = c.GetIl2CppType().Name;
            if (InteractiveTypes.Contains(name)) return name;
        }
        return null;
    }

    // "Map < Map Mask < Map Window < ..." (object first, then up to 6 parents)
    private static string Chain(Transform t)
    {
        var parts = new List<string>();
        for (int i = 0; t != null && i < 7; i++, t = t.parent) parts.Add(t.gameObject.name);
        return string.Join(" < ", parts);
    }

    private bool ScreenToWorld(Map map, Vector2 screen, out Vector3 world)
    {
        world = default;
        var rt = map.MapLayer.rectTransform;
        Rect r = rt.rect;
        Vector3 bounds = map.sceneBounds, off = map.sceneOffset;
        if (r.width <= 0f || r.height <= 0f || bounds.x <= 0f || bounds.z <= 0f) return false;
        Vector3 local = rt.InverseTransformPoint(new Vector3(screen.x, screen.y, rt.position.z));
        world = new Vector3(off.x + local.x / r.width * bounds.x, 0f, off.z + local.y / r.height * bounds.z);
        // outside the terrain square = clicked beside the map texture
        return Math.Abs(world.x - off.x) <= bounds.x * 0.5f + 1f && Math.Abs(world.z - off.z) <= bounds.z * 0.5f + 1f;
    }

    // World -> screen pixel on the map, same formula as the vanilla PlayerIcon (Map.cs:358-362).
    private static bool WorldToMapScreen(Map map, Vector3 w, out Vector2 screen)
    {
        screen = default;
        var rt = map.MapLayer.rectTransform;
        Rect r = rt.rect;
        Vector3 bounds = map.sceneBounds, off = map.sceneOffset;
        if (bounds.x <= 0f || bounds.z <= 0f) return false;
        Vector3 s = map.MapLayer.transform.TransformPoint(new Vector3(r.width * ((w.x - off.x) / bounds.x), r.height * ((w.z - off.z) / bounds.z), 0f));
        screen = new Vector2(s.x, s.y);
        return true;
    }

    // Screen rectangle (pixels, origin bottom-left like Mouse.position) of a UI element on a screen-space overlay canvas.
    // Also records once per scene whether the canvas really is overlay (corners with z ~ 0); clicks are refused otherwise.
    private bool ScreenRect(RectTransform rt, out Rect rect)
    {
        rect = default;
        if (rt == null) return false;
        var corners = new Il2CppStructArray<Vector3>(4);
        rt.GetWorldCorners(corners);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue; bool flat = true;
        for (int i = 0; i < 4; i++)
        {
            Vector3 c = corners[i];
            if (Math.Abs(c.z) > 1f) flat = false;
            minX = Math.Min(minX, c.x); minY = Math.Min(minY, c.y); maxX = Math.Max(maxX, c.x); maxY = Math.Max(maxY, c.y);
        }
        spaceFlat = flat;
        if (!spaceLogged) { spaceLogged = true; Write("Map UI space: " + (flat ? "screen-space overlay (corners = pixels)" : "NOT overlay - click-to-jump disabled") + ", viewport " + Rect.MinMaxRect(minX, minY, maxX, maxY) + "."); }
        rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }
}
