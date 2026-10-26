using System.Diagnostics;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(SiCommanderMapNav.ClientMod), "Commander MapNav", SiCommanderMapNav.ClientMod.Version, "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonPlatform(MelonPlatformAttribute.CompatiblePlatforms.WINDOWS_X64)]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]
[assembly: HarmonyDontPatchAll]   // the only patch (fallback) is installed by hand, see Nav.cs InstallFallbackPatch

namespace SiCommanderMapNav;

// Client-only mod: while the commander has the large map (M) open, the RTS camera stays navigable.
//   Nav.cs            keyboard camera movement / rotation (/ zoom) through per-action EnableOverride on the Commander map
//   MapClick.cs       left click on the map = jump the RTS camera there (StrategyMode.MoveCameraToPosition, local only)
//   LargeMapAccess.cs finding the registered LargeMap and its Map
// Nothing here sends a packet or touches gameplay state: the RTS camera is local (StrategyMode.cs:781-786), and the
// commander's map already shows the camera icon (Map.OrderedLateUpdate, Map.cs:294/358, when there is no Unit.CurrentUnit).
// Lifecycle: every game call is wrapped; Error() is throttled; after too many errors the mod switches itself off for the
// session and restores vanilla input (ClearOverrides).
public sealed partial class ClientMod : MelonMod
{
    public const string Version = "0.1.1";

    private static ClientMod instance;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private StreamWriter log;
    private double nextError;
    private int errors;
    private bool faulted;

    private MelonPreferences_Entry<string> prefDriveMode;
    private MelonPreferences_Entry<bool> prefEnabled, prefKeyboardNav, prefZoom, prefClickToJump, prefCloseOnClick, prefFollow, prefFallback, prefDebug;

    internal double Now => clock.Elapsed.TotalSeconds;

    public override void OnInitializeMelon()
    {
        instance = this;
        OpenLog();
        var c = MelonPreferences.CreateCategory("Si_Commander_MapNav");
        prefEnabled = c.CreateEntry("Enabled", true, description: "Master switch. Off = the mod does nothing (vanilla input).");
        prefKeyboardNav = c.CreateEntry("KeyboardNav", true, description: "While the large map (M) is open as commander: WASD / arrows move and Q / E (, / .) rotate the RTS camera (keyboard + mouse only).");
        prefDriveMode = c.CreateEntry("DriveMode", "direct", description: "How keyboard nav moves the camera: \"direct\" = the mod moves the RTS camera rig itself from raw W/A/S/D, arrows, Q/E, comma/period (default keys only, rebinding not honoured); \"override\" = 0.1.0 behaviour, re-enables the vanilla Commander camera actions (honours rebinding; did not move the camera in the first in-game test).");
        prefZoom = c.CreateEntry("CameraZoomWhileMapOpen", false, description: "Also let the mouse wheel zoom the RTS camera while the map is open. Off by default: the same wheel already zooms the map itself, so both would zoom at once.");
        prefClickToJump = c.CreateEntry("ClickToJump", true, description: "Left click (press + release without dragging) on the large map moves the RTS camera to look at that spot. Dragging still pans the map.");
        prefCloseOnClick = c.CreateEntry("CloseMapOnClick", false, description: "Close the large map after a click-to-jump.");
        prefFollow = c.CreateEntry("MapFollowsCamera", true, description: "While you move the camera with the keyboard, the map scrolls along when the camera icon would leave the visible map area (only matters when the map is zoomed in).");
        prefFallback = c.CreateEntry("PreferredInputFallback", false, description: "Fallback only if keyboard nav does not work: keep the WHOLE Commander input map enabled while the map is open (all commander hotkeys and world clicks live behind the map). Read at game start (restart after changing). Leave off.");
        prefDebug = c.CreateEntry("Debug", false, description: "Verbose log lines (overrides, clicks, map pushes).");
        Write("READY " + Version + " - Commander MapNav. " + PrefSummary());
        InstallFallbackPatch();
    }
    public override void OnDeinitializeMelon()
    {
        ClearOverrides("mod unloaded");
        try { log?.Dispose(); } catch { }
        log = null; instance = null;
    }
    public override void OnApplicationQuit() => ClearOverrides("application quit");
    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        // GameInput's action maps are static and survive scene loads (GameInput.cs:53), so an override left on would leak
        // into the next level: drop ours, and every reference into the old scene.
        ClearOverrides("scene " + sceneName);
        ResetMapRefs();
        ResetClick();
        Dbg("Scene loaded: " + sceneName);
    }

    public override void OnUpdate()
    {
        if (faulted) return;
        try { NavUpdate(); }
        catch (Exception error) { Error(error); }
    }
    public override void OnLateUpdate()
    {
        if (faulted || !navActive) return;
        try { FollowLateUpdate(); }
        catch (Exception error) { Error(error); }
    }

    private string PrefSummary() =>
        "Enabled=" + prefEnabled.Value + " KeyboardNav=" + prefKeyboardNav.Value + " DriveMode=" + prefDriveMode.Value + " CameraZoomWhileMapOpen=" + prefZoom.Value
        + " ClickToJump=" + prefClickToJump.Value + " CloseMapOnClick=" + prefCloseOnClick.Value + " MapFollowsCamera=" + prefFollow.Value
        + " PreferredInputFallback=" + prefFallback.Value + " Debug=" + prefDebug.Value;

    // ---- logging: MelonLoader log + UserData\Si_Commander_MapNav\session-<utc>.log (newest 20 kept) ----
    private void OpenLog()
    {
        try
        {
            var folder = Path.Combine(MelonEnvironment.UserDataDirectory, "Si_Commander_MapNav");
            Directory.CreateDirectory(folder);
            foreach (var old in Directory.GetFiles(folder, "session-*.log").OrderByDescending(f => f).Skip(19))
                try { File.Delete(old); } catch { }
            log = new StreamWriter(new FileStream(Path.Combine(folder, $"session-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception error) { LoggerInstance.Warning("Session log unavailable: " + error.Message); }
    }
    internal void Write(string text)
    {
        var line = DateTime.UtcNow.ToString("o") + " " + text.Replace('\r', ' ').Replace('\n', ' ');
        LoggerInstance.Msg(line);
        if (log == null) return;
        try { log.WriteLine(line); } catch { try { log.Dispose(); } catch { } log = null; }
    }
    internal void Dbg(string text) { if (prefDebug != null && prefDebug.Value) Write("[dbg] " + text); }
    // At most one error line per 10 s; after 50 errors the mod gives up for this session and restores vanilla input.
    internal void Error(Exception error)
    {
        errors++;
        if (errors >= 50 && !faulted)
        {
            faulted = true;
            Write("ERROR limit reached (" + errors + "), Commander MapNav disabled for this session. Last: " + error);
            try { ClearOverrides("fault"); } catch { }
            return;
        }
        if (Now < nextError) return;
        nextError = Now + 10;
        Write("ERROR (" + errors + ") " + error);
    }
}
