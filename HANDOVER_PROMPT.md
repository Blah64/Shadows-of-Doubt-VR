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

Also read the parked case board investigation notes:
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\NotesWork.md`

## What is working (Phase 19 complete — last commit 92f92f5)

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
| Right B | Map/notebook — **hold to show** (Tab held while B held); middle-click drag when aiming at a canvas |

- **Held item tracking** — carried world objects follow VR controller ✓
- **VR arm display** — both arms track their respective controllers ✓
- **Left laser beam** — LineRenderer on left controller, toggle in VR Settings ✓
- **Case board** — pins/notes/evidence interactive, pin drag working ✓
- **Save/load** — no warp after loading a save game ✓
- **Grip-drag** — individual note/evidence windows, map, location details, bio display all grip-draggable ✓
  - Positions saved relative to ActionPanelCanvas; restored on case board reopen ✓
  - Map position enforced every frame (game can't reset it) ✓
- **Action text** — left controller aim direction used for interact raycasts ✓
- **Minimap** — hold-B to show, grip-drag to reposition, trigger click opens evidence note, A right-click opens context menu ✓
- **Floor navigation on map** — floor +/- buttons clickable, room nodes at load=1+ ✓
- **Awareness compass** — VR fix implemented (UpdateCompass() in LateUpdate) ✓

## Active work: Polish (Phase 20)

The following issues are known and ready to work on:

### Other polish items
- **Comfort options** — vignette on snap-turn, configurable snap-turn degrees, IPD adjustment
- Some additive items show as semi-transparent white (not original colours)
- VR arm rotation may need per-item tuning for specific items

## Build & deploy
```bash
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
Log: `E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`

**Mono branch** for decompiling game assembly:
`E:\SteamLibrary\steamapps\common\Shadows of Doubt\Shadows of Doubt_Data\Managed\Assembly-CSharp.dll`
Use `ilspycmd` to decompile — e.g. `ilspycmd "...Assembly-CSharp.dll" -t MapContextMenu`

Ask me to switch to the mono branch when you need to read game source.

When I say **"done"** it means I've run the game and the log is ready. Always read it immediately.

Start by reading the files above, then ask what I'd like to work on next.
