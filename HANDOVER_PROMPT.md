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

## What is working (Phase 15 complete — last commit 4648a66)

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
| Right B | Notebook/map (Tab) (or middle-click drag / minimap pan when aiming at a canvas) |

- **Held item tracking** — carried world objects follow VR controller ✓
- **VR arm display** — both arms track their respective controllers ✓
- **Left laser beam** — LineRenderer on left controller, toggle in VR Settings ✓
- **Case board** — pins/notes/evidence interactive, pin drag working ✓
- **Save/load** — no warp after loading a save game ✓
- **Case board grip-drag** — panels relocatable; ActionPanelCanvas fixed in place ✓
- **Action text** — left controller aim direction used for interact raycasts ✓
- **Minimap** — B button pan, trigger click opens evidence note, A right-click opens context menu ✓
- **Floor navigation on map** — floor +/- buttons clickable, room nodes at load=1+ ✓

## Active work: Case board interaction fixes (Phase 16)

Three issues are parked in `NotesWork.md` with full analysis. These are the target for this session:

### Problem 1: Context menu aim dot / visual misalignment
The game overwrites `ContextMenu(Clone).localPosition` to screen coords, `localRot.y ≈ 284°`,
and `localScale.z = 0` every frame. Our zeroing (in 3 locations) may lose the race.
Z-scale=0 may also prevent correct bounds testing.

**Current state**: TooltipCanvas is frozen at the correct world position when context menu active.
But the ContextMenu(Clone) content may still be visually offset because the game resets it.

**Suggested approach**: Reparent `ContextMenu(Clone)` away from game control at first detection,
then the game's positioning MonoBehaviour loses its handle and stops writing to it.
Alternatively intercept the MonoBehaviour that drives the position each frame.

### Problem 2: Opened pinned notes (WindowCanvas) aim dot misalignment
When a note is opened from a pin on the case board, the WindowCanvas appears but the aim dot
(and therefore trigger-click) doesn't align with the visible window.

**Not yet diagnosed** — needs diagnostic logging to compare aim dot position vs canvas transform.

### Problem 3: Pin proximity "stealing" with 2+ pins
When 2 or more pins are present, the wrong pin gets targeted. Coordinate space was fixed
(anchoredPosition → localPosition), but "2 children, no pin close enough" errors still appear
after context menu is used. `hitLocal` from `InverseTransformPoint` may drift after the
context menu freeze/unfreeze cycle.

**Current state**: likely needs per-pin distance logging immediately after context menu closes
to see what coordinates are being compared.

## Other known issues

- Context menu from map (A right-click): menu items may not respond if `mapCursorNode` gets
  reset before user clicks a menu item — may need investigation
- Awareness compass (3D MeshRenderer system): VR fix implemented but not confirmed working
  (needs NPC spotting the player to spawn awareness icons)
- HUD settings plan written but not implemented (plan: `C:\Users\blah6\.claude\plans\tender-wibbling-sunbeam.md`)
- Some additive items show as semi-transparent white, not original colours
- No comfort options yet (vignette, configurable snap-turn degrees, IPD)

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
