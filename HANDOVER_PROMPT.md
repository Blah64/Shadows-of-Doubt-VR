# Handover prompt — paste this to start a new session

---

I'm working on a BepInEx 6 IL2CPP VR mod for Shadows of Doubt (Unity 2021.3.45f2, HDRP).
The mod is called SoDVR. Read the project instructions and handover doc first:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\CLAUDE.md`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\HANDOVER.md`

Then read the main source files:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\VR\VRCamera.cs`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\OpenXRManager.cs`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\VR\VRSettingsPanel.cs`

## What is working (Phase 11 complete — everything through movement)

- Stereo rendering in headset ✓
- Head tracking ✓
- World graphics — walls, floors, lighting all correct ✓
- All game UI canvases visible in VR (WorldSpace conversion pipeline) ✓
- VR Settings panel (F10 or main menu Settings button): 4 tabs, all settings wired ✓
- **Full controller bindings** — all buttons mapped:

| Button | Action |
|--------|--------|
| Left stick | Locomotion (4 m/s walk) |
| Left stick click | Sprint toggle |
| Right stick X | Snap turn ±30° |
| Right stick click | Flashlight (middle mouse) |
| Left Y / menu | ESC (pause menu) |
| Left X | Crouch (C key) |
| Left trigger | World interact (LMB + left-hand aim) |
| Left grip | Inventory (X key) |
| Right trigger | UI click |
| Right A | Jump |
| Right B | Notebook/map (Tab key) |

- **Held item tracking** — carried world objects follow VR controller ✓
- **VR arm display** — both arms track their respective controllers with per-hand rotation offsets ✓
- **Left laser beam** — LineRenderer on left controller, toggle in VR Settings ✓

## Known issues / polish opportunities

- CaseCanvas disabled (was bright white background; case-board content is elsewhere)
- MinimapCanvas partially working
- Some additive items show as semi-transparent white, not original colours
- PopupMessage gets scale-reset by game (fixed each scan cycle, slight lag)
- Trigger stopping issue — user reported, not diagnosed
- No comfort options yet (vignette, configurable snap-turn degrees, IPD)
- VR arm rotation may need per-item tuning for different held items

## Build & deploy
```bash
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
rm -rf "../BepInEx/plugins/SoDVR"
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
Log: `E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`

When I say **"done"** it means I've run the game and the log is ready. Always read it immediately.

Start by reading the files above, then ask what I'd like to work on next.
