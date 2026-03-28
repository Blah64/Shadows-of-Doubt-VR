# Handover prompt — paste this to start a new session

---

I'm working on a BepInEx 6 IL2CPP VR mod for Shadows of Doubt (Unity 2021.3.45f2, HDRP).
The mod is called SoDVR. Read the project instructions and handover doc first:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\CLAUDE.md`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\HANDOVER.md`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\PLAN-Claude.md`

Then read the main source file:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\VR\VRCamera.cs`

We are starting **Phase 8: VR Settings Panel — Phase 1 (skeleton panel)**.

What is working:
- Stereo rendering in headset ✓
- Head tracking ✓
- All game UI canvases visible in VR (WorldSpace conversion pipeline) ✓
- Right controller pose tracked, trigger fires click events ✓
- Cursor dot visible on all screens (survives scene transitions) ✓
- Phase 0 test canvas (`VRSettingsPanelInternal`, F10 to toggle) confirmed visible ✓

What to build next (Phase 1 per PLAN-Claude.md):
- New file `SoDVR/VR/VRSettingsPanel.cs` that owns the canvas, layout, and all settings logic
- Dark semi-transparent background panel (900×700 reference resolution)
- Title "VR Settings", Close button
- Two tab buttons: **Graphics** | **General**
- 3–4 hardcoded placeholder rows per tab (no scroll view)
- F10 still toggles visibility
- VRCamera additions ≤30 lines: call `VRSettingsPanel.Init()`, add canvas ID to `_ownedCanvasIds`, wire F10

Key constraints from HANDOVER.md:
- Canvas must be created as `ScreenSpaceOverlay`, then `ScanAndConvertCanvases` converts it (gives HDRP registration)
- `DontDestroyOnLoad` on the root GO — mandatory, scene transition destroys non-persistent objects
- Register canvas ID in `_ownedCanvasIds` immediately after `AddComponent<Canvas>()` — before scan fires
- `_cursorRect`: always `AddComponent<Image>()` first, then `GetComponent<RectTransform>()` — `AddComponent<RectTransform>()` returns null in IL2CPP
- `CanvasScaler` with `referenceResolution=(900,700)` so conversion uses the right size
- `_ownedCanvasIds` gates `RescanCanvasAlpha` only; `PositionCanvases` still places owned canvases once

Build and deploy with:
```
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
When I say "done" it means I've run the game and the log is ready at:
`E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`
Always read it immediately.

Start by reading the four files above, then propose the implementation plan for Phase 1.
