# SoDVR — Handover Prompt for New Session

Paste this at the start of a new conversation.

---

## Context

I am building a BepInEx 6 IL2CPP VR mod for Shadows of Doubt (Unity 2021.3.45f2, HDRP).
The mod adds full 6DOF VR support targeting VDXR (Virtual Desktop) via Samsung Galaxy XR headset.
The stereo rendering, head tracking, controller input, and settings panel all work.

The current session is blocked on two root-cause bugs in the canvas management system.
Previous attempts to fix symptoms individually made things worse.
The canvas system needs a targeted redesign at `SoDVR/VR/VRCamera.cs`.

Full project context is in `CLAUDE.md`. Full bug history is in `FAILURE-CaseBoard.md`.
Technical handover detail is in `HANDOVER.md`.

---

## Two Root Causes to Fix (in order)

### Root Cause 1 — CanvasScaler inflates all canvas sizeDelta

**What happens**: Every game canvas has a `CanvasScaler` in "Scale With Screen Size" mode.
By the time our mod converts a canvas from ScreenSpace to WorldSpace, the CanvasScaler
has already inflated `sizeDelta` from the authored reference (e.g. 1280×720) to match the
display resolution (e.g. 2720×1680 — a 2.5× factor).

Our scale values (e.g. 0.0015 per pixel) were calibrated for ~1920px-wide canvases.
Applied to a 2720px canvas, the result is a 4m-wide wall instead of a 2m-wide panel.

**Confirmed actual sizes (from log 2026-03-29)**:
```
MenuCanvas, CaseCanvas, etc. → sizeDelta = (2720, 1680) @ scale 0.0015 = 4.08m × 2.52m
MinimapCanvas               → sizeDelta = (2720, 1680) @ scale 0.0013 = 3.54m × 2.18m
WindowCanvas (notepad)      → sizeDelta = (3200, 1800) @ scale 0.0006 = 1.92m × 1.08m
```

**Fix**: In `ConvertCanvasToWorldSpace`, disable the CanvasScaler BEFORE switching renderMode:

```csharp
var scaler = canvas.GetComponent<CanvasScaler>();
if (scaler != null) try { scaler.enabled = false; } catch { }
canvas.renderMode = RenderMode.WorldSpace;
// Now sizeDelta stays at the authored reference (e.g. 1280×720)
// and existing scale values will produce the intended world size.
```

After this fix the map, notepad, case board, and all menus should be reasonable sizes.

---

### Root Cause 2 — HDRP inherited auto-exposure darkens all WorldSpace UI

**What happens**: WorldSpace canvas graphics are rendered as 3D geometry by our VR eye cameras.
HDRP applies the scene camera's auto-exposure to all geometry, including our UI.
City interiors run at EV≈8–12 → multiplier 1/256 to 1/4096.
Any vertex colour ≤ (1,1,1) is reduced to near-black.

`PostprocessOff=True` and `TonemapOff=True` are confirmed on both VR cameras but do NOT
stop the inherited exposure — the exposure is applied before post-processing in HDRP 12.

**Does NOT work** (all confirmed):
- `FrameSettingsField.ExposureControl` per-camera: bit never persists
- HDRP Volume with EV=0 Fixed: not applied to WorldSpace canvas cameras
- Setting HDR material colour (e.g. `dotMat.color = new Color(4096,0,4096)`): untested

**Recommended fix approaches** (try in this order):

**Option A — CommandBuffer to overwrite HDRP's 1×1 exposure texture**
HDRP stores the current exposure as a 1×1 `RFloat` texture in `HDCamera` history buffers.
Overwriting it with `0.0` (log2 space = 1.0 linear = neutral) before the VR camera renders
should make the VR cameras see "no exposure correction needed":

```csharp
// Attach once to left camera's BeginCameraRendering event:
RenderPipelineManager.beginCameraRendering += OnBeginVRCameraRendering;

void OnBeginVRCameraRendering(ScriptableRenderContext ctx, Camera cam)
{
    if (cam != _leftCam && cam != _rightCam) return;
    var hdCam = HDCamera.GetOrCreate(cam);
    // Find the exposure texture via reflection and overwrite with neutral value
    // Texture name in HDRP 12: "_ExposureTexture" or via hdCam.GetPreviousFrameRT(...)
}
```

**Option B — Read live EV and apply compensating boost to VRMod-owned graphics**
```csharp
var hdCam = HDCamera.GetOrCreate(_leftCam);
float ev = /* read from hdCam */;
float boost = Mathf.Pow(2f, ev);  // e.g. 256 at EV8
// Apply as material._Color on cursor dot, laser, etc. each scan cycle
```

**Option C — Separate UI camera on its own layer (most robust)**
Add a third camera (clearFlags=Depth, cullingMask=UI layer only) with its own HDRP
FrameSettings that correctly disables exposure. All WorldSpace canvas elements moved to
a dedicated UI layer. This camera renders after the eye cameras but before the swapchain copy.

---

## What to do this session

1. Read `CLAUDE.md`, `HANDOVER.md`, and `FAILURE-CaseBoard.md` for full context.
2. Fix Root Cause 1 (CanvasScaler, 5-line change) first. Build, deploy, test. Confirm canvas sizes are correct.
3. Then tackle Root Cause 2 (HDRP exposure). Start with Option A (CommandBuffer).
4. Only after both root causes are resolved, address remaining symptoms:
   - Cursor dot visibility (dot is correctly tracked but invisible — exposure fix should solve this)
   - Laser pointer visibility (same)
   - Canvas recentering on reopen (likely working after size fix makes it perceptible)
   - Map empty (size fix should reveal the minimap)

## Do NOT do this session
- Do not tune scale values — they are already correct for the authored canvas sizes once CanvasScaler is disabled.
- Do not change the canvas category system — the categories and distances are fine.
- Do not attempt further individual symptom fixes before both root causes are addressed.
- Do not add more diagnostic logging — we have enough; the two root causes are confirmed.

## Build & deploy
```bash
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
Log: `E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`
User says **"done"** = game was run, log is ready. Always read the log immediately.
