# Handover prompt — paste this to start a new session

---

I'm working on a BepInEx 6 IL2CPP VR mod for Shadows of Doubt (Unity 2021.3.45f2, HDRP).
The mod is called SoDVR. Read the project instructions and handover doc first:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\CLAUDE.md`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\HANDOVER.md`

Then read the main source files:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\VR\VRCamera.cs`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\OpenXRManager.cs`

## What is working (Phase 10 complete)
- Stereo rendering in headset ✓
- Head tracking ✓
- World graphics — walls, floors, lighting all correct ✓ (fixed Phase 10)
- All game UI canvases visible in VR (WorldSpace conversion pipeline) ✓
- Right controller pose tracked, trigger fires click events, cursor dot visible ✓
- VR Settings panel (F10 or main menu Settings button): 4 tabs, all settings wired ✓
- Snap turn (right stick X ±30°) ✓
- ESC/menu button (left Y/menu button) ✓

## Phase 11 goal: movement

The movement infrastructure is already coded in VRCamera.cs but **not yet fully tested** since Phase 10. Specific tasks in priority order:

**Priority 1 — Test locomotion and snap turn**
Load into the game world. Try the left thumbstick (should move the player) and right thumbstick X (should snap-turn). Check `LogOutput.log` for `[Movement] Cached playerCC on '...'` to confirm the CharacterController was found. If movement doesn't work, diagnose from the log.

**Priority 2 — Jump**
No jump binding exists yet. Need to:
1. Add a new OpenXR boolean action `_jumpAction` in `OpenXRManager.cs`, bound to `/user/hand/right/input/a/click`
2. Add `GetJumpButtonState(out bool pressed)` public API
3. Add `UpdateJump()` in `VRCamera.cs` — on press edge, simulate Space key (`keybd_event(0x20, ...)`) OR inject into CharacterController directly

**Priority 3 — World interact**
No interact binding exists. 'E' key triggers world interaction (doors, objects, NPCs). Need to:
1. Decide binding (suggestion: right grip or left trigger — check what feels natural)
2. Add OpenXR action + binding for chosen button
3. `UpdateInteract()` in VRCamera: detect press edge, simulate 'E' key (`keybd_event(0x45, ...)`)

**Lower priority / deferred**
- Sprint/run (hold left stick or right grip?)
- Crouch (TBD)
- Left controller full tracking (Phase 13)

## Key facts about movement system (from decompiled mono assemblies)
- Game uses **Rewired** for input, **CharacterController** for movement — NOT Rigidbody
- `CharacterController.Move()` is the correct approach (Rigidbody is kinematic, driven by CC)
- Walls/floors/room geometry is on **layer 29**, culled by `GeometryCullingController` based on **player position**, not camera — so locomotion works correctly as long as we move the player character
- `VROrigin.transform.position` follows the game camera transform every frame, so VROrigin tracks the player's physical position automatically

## Key constraints
- No coroutines — everything drives via `Update()`/`LateUpdate()`
- IL2CPP pitfalls — see CLAUDE.md for full list
- `keybd_event` P/Invoke already in VRCamera.cs (used for ESC) — reuse the same pattern for Space/E
- OpenXR actions must be created at instance level (before xrCreateSession) in `SetupActionSetsInstance()` and bound in the same function — follow existing pattern for `_menuButtonAction`

## Build & deploy
```bash
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
rm -rf "../BepInEx/plugins/SoDVR"
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
Log: `E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`

When I say **"done"** it means I've run the game and the log is ready. Always read it immediately.

Start by reading the four files above, then confirm what's already in place for movement and what needs to be added.
