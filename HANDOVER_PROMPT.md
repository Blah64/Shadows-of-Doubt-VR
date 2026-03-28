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

What to build next (Phase 9):
- Left controller pose tracking (same action set pattern as right controller)
- Thumbstick locomotion: left stick → character movement (forward/back/strafe)
- Snap turn: right stick X → rotate VROrigin by ±N degrees with dead-zone and cooldown
- Optional: grip/button bindings for jump and interact

Key constraints from HANDOVER.md:
- OpenXR action sets are in `OpenXRManager.cs` — add left controller bindings there
- No coroutines — everything drives via `Update()`/`LateUpdate()`
- IL2CPP: `GetComponentInParent<Button>()` always null; `AddListener` on new `ButtonClickedEvent` unreliable (use TryClickCanvas intercept pattern)
- `btn.GetInstanceID()` = component id; always use `btn.gameObject.GetInstanceID()` for GO comparisons
- `DontDestroyOnLoad` required on all VRMod-created GameObjects

Build and deploy with:
```
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
```
(Post-build step in csproj auto-copies to BepInEx/plugins/SoDVR.dll)

When I say "done" it means I've run the game and the log is ready at:
`E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`
Always read it immediately.

Start by reading the four files above, then propose the implementation plan for Phase 9.
