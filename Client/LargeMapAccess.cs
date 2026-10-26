using Il2CppSilica.UI;
using Il2CppSilica.UI.Menus;

namespace SiCommanderMapNav;

// Finding the large map (M).
//
// LargeMap is a Menu inside the faction UI prefab (UIManagerSol/.../Canvas, BIG map/Sol_LargeMap) that TeamUI instantiates
// for the local player's team; Menu.Awake registers it in MenuManager._menus (Dictionary<Type, Menu>, MenuManager.cs:10,
// 67-75). We look it up there (the non-generic route, same as Si_Spectator's LargeMapAccess.cs) instead of calling the
// generic IsMenuOpened<LargeMap>() every frame: Il2Cpp generic method calls go through MethodInfoStoreGeneric and are the
// fragile path. The cached reference is re-validated every frame (Unity null = destroyed, e.g. team switch / scene load)
// and looked up again at most once per second while missing.
// Its Map (LargeMap._map, LargeMap.cs:16) is "Map Window" with MapLayer = the map RawImage and Viewport = "Map Mask".
public sealed partial class ClientMod
{
    private LargeMap largeMap;
    private double largeMapSearchAt;
    private bool largeMapLogged;

    private void ResetMapRefs() { largeMap = null; largeMapSearchAt = 0; largeMapLogged = false; spaceLogged = false; spaceFlat = true; }

    private LargeMap FindLargeMap()
    {
        if (largeMap != null && largeMap.WasCollected) largeMap = null;
        if (largeMap != null) { try { if (largeMap.gameObject == null) largeMap = null; } catch { largeMap = null; } }   // Unity-destroyed
        if (largeMap != null) return largeMap;
        if (Now < largeMapSearchAt) return null;
        largeMapSearchAt = Now + 1;
        var manager = MenuManager.Instance;
        var menus = manager == null ? null : manager._menus;
        if (menus == null) return null;
        foreach (var menu in menus.Values)
        {
            var found = menu == null ? null : menu.TryCast<LargeMap>();
            if (found != null) { largeMap = found; break; }
        }
        if (largeMap != null && !largeMapLogged)
        {
            largeMapLogged = true;
            var map = MapOf(largeMap);
            Write("LargeMap found: '" + largeMap.gameObject.name + "', Map " + (map == null ? "MISSING" : "'" + map.gameObject.name + "'")
                + (map == null ? "" : ", MapLayer " + (map.MapLayer != null) + ", Viewport " + (map.Viewport != null) + ", sceneBounds " + map.sceneBounds + ", sceneOffset " + map.sceneOffset) + ".");
        }
        return largeMap;
    }

    private static bool IsOpen(LargeMap map) { try { return map != null && map.IsOpen; } catch { return false; } }
    private static Map MapOf(LargeMap lm) { try { return lm == null ? null : lm._map; } catch { return null; } }

    // Closing through the registered instance (MenuManager.CloseMenu(Menu), MenuManager.cs non-generic overload).
    private void CloseLargeMap(LargeMap lm)
    {
        var manager = MenuManager.Instance;
        if (manager == null || lm == null) return;
        bool closed = false;
        try { closed = manager.CloseMenu(lm); } catch (Exception error) { Error(error); }
        Write("Large map " + (closed ? "closed" : "NOT closed (CloseMenu returned false)") + " after click-to-jump.");
    }
}
