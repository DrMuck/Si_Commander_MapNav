using System.Text;
using Il2Cpp;
using Il2CppSilica;
using Il2CppSilica.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using InputMap = Il2CppSilica.GameInput.InputMap;

namespace SiCommanderMapNav;

// Always-on diagnostics while NAV is on (no Debug pref needed), added in 0.1.1 because 0.1.0 opened the gate in game but
// the camera did not move. One of these must be false, and the lines below show which:
//   (a) the Commander InputActions read non-zero after the override   -> per action: enabled, map enabled, value, controls
//   (b) StrategyMode.Update runs and writes the rig                     -> rig position moves while keys are held
//   (c) Game.CurrentCamera follows the rig while the menu is open      -> camera position / name; "RIG MOVED, CAMERA DID NOT"
// Output: one line on NAV ON, then one line per 0.5 s only while a camera key (WASD / arrows / Q / E / , / .) is physically
// held. Every value is read in its own try so one failing getter does not hide the rest.
public sealed partial class ClientMod
{
    private double diagNextAt;
    private bool diagHave;
    private Vector3 diagRig, diagCam;

    private void DiagOnNavOn()
    {
        diagHave = false; diagNextAt = 0;
        var sb = new StringBuilder("DIAG nav-on: DriveMode=" + (IsDirectMode ? "direct" : "override") + ";");
        // A wrong action name would only produce a DebugConsole warning in Documents\Silica\Game.log (GameInput.cs:642-645).
        foreach (var name in NavActions)
        {
            sb.Append(" '").Append(name).Append("': GetAction ");
            sb.Append(Try(() => GameInput.GetAction(InputMap.Commander, name) != null ? "found" : "NULL"));
            sb.Append(", container ").Append(Try(() => ContainerOverride(name)));
        }
        Write(sb.ToString());
        DiagLine("nav-on", false);
    }

    private void DiagUpdate(bool held)
    {
        if (!held) { diagHave = false; return; }
        if (Now < diagNextAt) return;
        diagNextAt = Now + 0.5;
        DiagLine("held", true);
    }

    private void DiagLine(string tag, bool held)
    {
        var sb = new StringBuilder("DIAG " + tag + ":");
        sb.Append(" CurrentInput=").Append(Try(() => GameInput.CurrentInput.ToString()));
        sb.Append(" scheme=").Append(Try(() => GameInput.CurrentControlScheme.ToString()));
        foreach (var name in NavActions) sb.Append(" | ").Append(ActionState(name));
        var kb = Keyboard.current;
        sb.Append(" | keys ").Append(kb == null ? "no keyboard" : Try(() =>
            "W" + B(kb.wKey.isPressed) + " A" + B(kb.aKey.isPressed) + " S" + B(kb.sKey.isPressed) + " D" + B(kb.dKey.isPressed)
            + " Up" + B(kb.upArrowKey.isPressed) + " Q" + B(kb.qKey.isPressed) + " E" + B(kb.eKey.isPressed)
            + " Shift" + B(kb.shiftKey.isPressed) + (kb.shiftKey.isPressed ? " boost x" + BoostFactor.ToString("0.#") : "")));
        Vector3 rig = default, cam = default; bool haveRig = false, haveCam = false;
        var sm = StrategyMode.Instance;
        sb.Append(" | rig ").Append(sm == null ? "NO StrategyMode.Instance" : Try(() =>
        {
            rig = sm.transform.position; haveRig = true;
            return V(rig) + " yaw " + sm.transform.rotation.eulerAngles.y.ToString("0") + " enabled " + B(sm.enabled) + " activeInHierarchy " + B(sm.gameObject.activeInHierarchy)
                + " heightPct " + sm.CameraHeightPct.ToString("0.00") + " CameraSpeed " + sm.CameraSpeed.ToString("0.##") + " smooth " + B(sm.m_SmoothMovement) + " InStrategyMode " + B(StrategyMode.InStrategyMode);
        }));
        var camera = Game.CurrentCamera;
        sb.Append(" | camera ").Append(camera == null ? "NULL" : Try(() =>
        {
            cam = camera.transform.position; haveCam = true;
            return "'" + camera.name + "' " + V(cam) + " enabled " + B(camera.enabled) + " active " + B(camera.gameObject.activeInHierarchy);
        }));
        sb.Append(" | TextInputActive ").Append(Try(() => B(UIManager.TextInputActive)));
        sb.Append(" IsPaused ").Append(Try(() => B(Game.IsPaused)));
        sb.Append(" timeScale ").Append(Try(() => Time.timeScale.ToString("0.##")));
        sb.Append(" GetCameraEnabled ").Append(Try(() => B(Game.GetCameraEnabled())));
        if (held && diagHave && haveRig && haveCam)
        {
            float rigMoved = Vector3.Distance(rig, diagRig), camMoved = Vector3.Distance(cam, diagCam);
            sb.Append(" | since last: rig ").Append(rigMoved.ToString("0.0")).Append(" m, camera ").Append(camMoved.ToString("0.0")).Append(" m");
            if (rigMoved > 1f && camMoved < 0.1f) sb.Append(" => RIG MOVED, CAMERA DID NOT (vanilla StrategyMode.Update not following / different camera)");
            else if (rigMoved < 0.1f) sb.Append(" => RIG DID NOT MOVE while a key was held");
        }
        if (haveRig && haveCam) { diagRig = rig; diagCam = cam; diagHave = held; }
        Write(sb.ToString());
    }

    private string ActionState(string name)
    {
        return name + ": " + Try(() =>
        {
            var a = GameInput.GetAction(InputMap.Commander, name);
            if (a == null) return "GetAction NULL";
            string value = Try(() => { var o = a.ReadValueAsObject(); return o == null ? "null" : o.ToString(); });
            return "enabled " + B(a.enabled) + " mapEnabled " + Try(() => B(a.actionMap.enabled)) + " value " + value
                + " controls " + Try(() => a.controls.Count.ToString()) + " override " + Try(() => ContainerOverride(name));
        });
    }

    // EnableOverride as stored in GameInput._actionMaps[Commander].actionContainers[name] (GameInput.cs:53, ActionMapContainer.cs:99).
    private static string ContainerOverride(string name)
    {
        var maps = GameInput._actionMaps;
        if (maps == null) return "no _actionMaps";
        if (!maps.ContainsKey(InputMap.Commander)) return "Commander map NOT registered";
        var containers = maps[InputMap.Commander].actionContainers;
        if (containers == null || !containers.ContainsKey(name)) return "action NOT in Commander map";
        return "EnableOverride=" + B(containers[name].EnableOverride);
    }

    private static string B(bool b) => b ? "1" : "0";
    private static string V(Vector3 v) => "(" + v.x.ToString("0.0") + "," + v.y.ToString("0.0") + "," + v.z.ToString("0.0") + ")";
    private static string Try(Func<string> f) { try { return f(); } catch (Exception e) { return "ERR " + e.GetType().Name; } }
}
