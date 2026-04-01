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

## What is working (Phase 13 complete, Phase 14 partial)

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
| Right A | Jump (or right-click when aiming at a canvas) |
| Right B | Notebook/map (Tab) (or middle-click drag when aiming at a canvas) |

- **Held item tracking** — carried world objects follow VR controller ✓
- **VR arm display** — both arms track their respective controllers ✓
- **Left laser beam** — LineRenderer on left controller, toggle in VR Settings ✓
- **Case board** — pins/notes/evidence interactive, pin drag working ✓
- **Save/load** — no warp after loading a save game ✓
- **Case board grip-drag** — panels (map, notes, bio, etc.) relocatable; ActionPanelCanvas fixed in place ✓

## Known issues / parked work

**Parked (see NotesWork.md for full analysis + suggested fixes):**
- Context menu aim dot / visual misalignment (game writes screen coords + Z-scale=0 every frame to ContextMenu(Clone))
- Opened pinned notes (WindowCanvas) aim dot misalignment — not yet diagnosed
- Pin proximity "stealing" with 2+ pins on case board — coordinate space drift after context menu

**Other:**
- MinimapCanvas partially working
- Some additive items show as semi-transparent white, not original colours
- PopupMessage gets scale-reset by game (fixed each scan cycle, slight lag)
- No comfort options yet (vignette, configurable snap-turn degrees, IPD)
- Jump while stationary — may not work in some states (not diagnosed)
- Notebook B-press — reportedly opens and instantly closes (not diagnosed)

## Ready to implement (plan written, not started)

**HUD settings** — 5 adjustable settings in VR Settings General tab + auto-hide:
- Plan file: `C:\Users\blah6\.claude\plans\tender-wibbling-sunbeam.md`
- Files to modify: `SoDVR/VR/VRSettingsPanel.cs` + `SoDVR/VR/VRCamera.cs`

## Uncommitted changes

`SoDVR/VR/VRCamera.cs` has uncommitted changes from the last session (case board interaction attempts — parked). Commit or review before starting new work.

## Build & deploy
```bash
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
Log: `E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`

When I say **"done"** it means I've run the game and the log is ready. Always read it immediately.

Start by reading the files above, then ask what I'd like to work on next.
