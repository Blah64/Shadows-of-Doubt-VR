# Handover prompt — paste this to start a new session

---

I'm working on a BepInEx 6 IL2CPP VR mod for Shadows of Doubt (Unity 2021.3.45f2, HDRP).
The mod is called SoDVR. Read the project instructions and handover doc first:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\CLAUDE.md`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\HANDOVER.md`

Then read the main source files:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\VR\VRCamera.cs`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\OpenXRManager.cs`

What is working:
- Stereo rendering in headset ✓
- Head tracking ✓
- All game UI canvases visible in VR (WorldSpace conversion pipeline) ✓
- Right controller pose tracked, trigger fires click events, cursor dot visible ✓
- VR Settings panel (F10 or main menu Settings button): 4 tabs, all settings wired ✓
  - Audio volumes work via FMOD APIs (GetBus/GetVCA on RuntimeManager)
  - MenuCanvas hides while VR panel is open

What to fix next (Phase 9 — GUI & graphics polish):

**Priority 1 — Right-eye jitter (unconfirmed fix, test first)**
A `GL.InvalidateState()` fix was deployed at the end of the previous session. The render loop
is now: `GL.invertCulling=true → GL.InvalidateState() → _leftCam.Render() → GL.Flush() →
GL.InvalidateState() → _rightCam.Render() → GL.invertCulling=false`. Need to confirm whether
this eliminated the jitter. If not, try swapping render order (right first, then left) to
determine whether jitter is in the second-rendered camera or always in the right camera.

**Priority 2 — Trigger click fires every frame while held**
`TryClickCanvas` has no debounce — needs `_triggerNeedsRelease` latch (same pattern as
`UpdateMenuButton`). See HANDOVER.md §Bug 3 for the exact code pattern.

**Priority 3 — Y/menu button still fires multiple times per press**
`UpdateMenuButton` has a 1.5 s cooldown + `_menuBtnNeedsRelease` latch but multi-fires still
appear in logs. See HANDOVER.md §Bug 2. Start by logging `realtimeSinceStartup` at each call
to diagnose whether the cooldown is actually being respected.

**Priority 4 — UI brightness (HDRP auto-exposure)**
Game UI text/buttons appear near-black in the headset. Root cause: HDRP's scene auto-exposure
(EV≈8–12 for city interiors) is inherited by the UI overlay cameras. See HANDOVER.md
§BLOCKING ISSUE for the three recommended fix approaches.

**Priority 5 — Confirm blue-box fix**
Canvas reposition whitelist deployed but not yet confirmed. Ask me to run the game and check
whether a blue box still appears when pressing Y to toggle the pause menu.

Key constraints:
- No coroutines — everything drives via `Update()`/`LateUpdate()`
- IL2CPP: `GetComponentInParent<Button>()` always null; `AddListener` on new `ButtonClickedEvent` unreliable
- `btn.GetInstanceID()` = component id; always use `btn.gameObject.GetInstanceID()` for GO comparisons
- `DontDestroyOnLoad` required on all VRMod-created GameObjects
- `canvas.enabled` must be state-tracked — toggling per-frame floods material instances and crashes

Build and deploy with:
```
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
```
(Post-build step in csproj auto-copies to BepInEx/plugins/SoDVR.dll)

When I say "done" it means I've run the game and the log is ready at:
`E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`
Always read it immediately.

Start by reading the four files above, then tell me what you want to tackle first in priority order.
