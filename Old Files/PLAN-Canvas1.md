# PLAN — Phase 9: GUI, HUD & Game UI in VR

**Date**: 2026-03-28
**Scope**: Fix all remaining UI issues so every game canvas is usable in the headset.
Phase 10 (movement) is blocked on this.

---

## Current state

The canvas pipeline is operational:
- Every 90 frames, `ScanAndConvertCanvases` discovers root ScreenSpace canvases via
  `Resources.FindObjectsOfTypeAll<Canvas>()` and converts them to WorldSpace.
- Each canvas is placed once at 2 m from the head on first activation.
- Material patches (ZTest Always, render-queue assignment, stencil neutralization)
  make UI visible through 3D geometry.
- Brightness boosts (text ×4 vertex + ×4 material, images ×16 material) partially
  compensate for HDRP auto-exposure.
- Controller ray-cast + trigger click + cursor dot all functional.

### What's broken or incomplete

| Issue | Severity | Status |
|-------|----------|--------|
| HDRP auto-exposure darkens UI text to near-black | **Blocking** | Workaround only (×4/×16 boost) |
| Right-eye temporal jitter | Medium | GL.InvalidateState deployed, unconfirmed |
| "Blue box" flash on ESC | Low | Canvas whitelist deployed, unconfirmed |
| No per-canvas positioning strategy | Medium | All canvases stacked at same distance |
| Trigger click fires every frame while held | Low | Rising-edge exists, deprioritised |
| Y-button (ESC) fires multiple times per press | Low | Cooldown guard in place, deprioritised |
| Some game canvases not yet verified in VR | Medium | Only MenuCanvas/GameCanvas well-tested |

---

## Game canvas inventory

From `Assembly-CSharp.dll` (`InterfaceController` class) — these are the canvases
the game creates at runtime:

| Field name | Purpose | VR treatment needed |
|------------|---------|---------------------|
| `hudCanvas` | Crosshair, awareness indicator, notifications | HUD — needs comfort positioning |
| `statusCanvas` | Health / energy / awareness bars | HUD — group with hudCanvas |
| `menuCanvas` | Main menu (2231 elements, incl. FadeOverlay) | Menu — recentre on activate ✓ |
| `dialogCanvas` | NPC conversation / interrogation | Menu — recentre on activate ✓ |
| `gameWorldCanvas` | World-anchored 3D labels (names, prices) | Leave as WorldSpace — no conversion |
| `caseCanvas` | Investigation board (pinned evidence) | Panel — large, may need scale adjustment |
| `contentCanvas` | Document / folder viewer | Panel — same treatment as caseCanvas |
| `windowCanvas` | In-game windows (phone, email, computer) | Panel — positioned at interaction point |
| `tooltipsCanvas` | Item/button tooltips (sortOrder=3) | Tooltip — attach near cursor or controller |
| `controlPanelCanvas` | Door/safe/intercom UI | Panel — world-anchored at interaction |
| `controlsCanvas` | Settings / key remapping | Menu — treat like menuCanvas |
| `minimapCanvas` | Minimap overlay | HUD — position at wrist or corner |
| `osCanvas` | In-game computer desktop | Panel — full-size, positioned at desk |
| `keyboardCanvas` | Virtual keyboard for computer | Panel — group with osCanvas |
| `upgradesCanvas` | Sync disk / augmentation screen | Menu — recentre like dialog |
| `interactionProgressCanvas` | Lockpick / hack progress bar | HUD — attach near interaction point |
| `fingerprintDisplayCanvas` | Fingerprint scanner results | Panel — positioned at scanner |
| `mapLayerCanvas` | Full map layers | Panel — large, needs custom scale |

### Canvas categories for VR

**Category A — HUD** (always visible, comfort-positioned):
`hudCanvas`, `statusCanvas`, `interactionProgressCanvas`

**Category B — Menu** (recentre in front of head on activate):
`menuCanvas`, `dialogCanvas`, `controlsCanvas`, `upgradesCanvas`

**Category C — Panel** (large interactive surfaces):
`caseCanvas`, `contentCanvas`, `windowCanvas`, `osCanvas`, `keyboardCanvas`,
`fingerprintDisplayCanvas`, `mapLayerCanvas`

**Category D — World-anchored** (stay at interaction point):
`gameWorldCanvas`, `controlPanelCanvas`

**Category E — Tooltip** (follow cursor/controller):
`tooltipsCanvas`

---

## Task 1 — HDRP UI brightness (primary blocker)

### Root cause

HDRP's auto-exposure computes EV ≈ 8–12 for city interiors (exposure multiplier
≈ 1/256 to 1/4096). The eye cameras inherit this exposure. Unlit UI with white vertex
colours × 1/256 → near-black.

Current workaround (×4 text, ×16 image boost) is 1–2 orders of magnitude too weak
for dark environments.

### Approach 1A — `FrameSettingsField.Postprocess` master toggle

**Rationale**: If this field persists per-camera (unlike `ExposureControl` which
doesn't), it disables the entire post-processing stack including exposure. Tonemapping
is already off, so the main thing we lose is bloom — acceptable trade-off.

**Change**: Add to `s_VrDisabledFields` array in `VRCamera.cs` ~line 730:

```csharp
private static readonly FrameSettingsField[] s_VrDisabledFields =
{
    FrameSettingsField.Postprocess,         // ← NEW: master post-process kill
    FrameSettingsField.SSAO,
    FrameSettingsField.SSR,
    FrameSettingsField.Volumetrics,
    FrameSettingsField.MotionVectors,
    FrameSettingsField.MotionBlur,
    FrameSettingsField.DepthOfField,
    FrameSettingsField.ChromaticAberration,
    FrameSettingsField.ContactShadows,
    FrameSettingsField.Tonemapping,
};
```

**Verify**: Add readback log after camera setup:

```csharp
bool ppOff = !fsRb.IsEnabled(FrameSettingsField.Postprocess);
Log.LogInfo($"[VRCamera] HDRP FS readback: PostprocessOff={ppOff}");
```

If `PostprocessOff=True` → test in headset. If text is legible → done.
If `PostprocessOff=False` (same failure as ExposureControl) → try 1B.

### Approach 1B — `SetExposureTextureToEmpty()` on HDCamera

Discovered in `Unity.RenderPipelines.HighDefinition.Runtime.dll`:

```
HDCamera.GetOrCreate(Camera)
HDCamera.GetExposureTexture()       → current 1×1 RFloat exposure RT
HDCamera.SetExposureTextureToEmpty() → reset to neutral (m_EmptyExposureTexture)
```

This is a direct API on `HDCamera` that resets the exposure texture to neutral.
Call it each frame before `Camera.Render()`:

```csharp
// In LateUpdate, before each eye render:
try
{
    var hdLeft = HDCamera.GetOrCreate(_leftCam);
    hdLeft.SetExposureTextureToEmpty();  // exposure = 1.0 (log₂ = 0)
}
catch (Exception ex) { Log.LogWarning($"[VRCamera] Exposure reset failed: {ex.Message}"); }

GL.invertCulling = true;
GL.InvalidateState();
_leftCam.Render();
// ... same for right eye
```

If `SetExposureTextureToEmpty` is not public in IL2CPP, fall back to reflection or
direct field write:

```csharp
// Alternative: write m_EmptyExposureTexture to m_ExposureTextures via reflection
var hdType = Il2CppType.Of<HDCamera>();
// ... field access pattern
```

### Approach 1C — Increase boost multipliers (stop-gap)

If both 3A and 3B fail, increase the brightness constants to survive EV 10:

```csharp
private const float UITextBrightnessBoost  = 32.0f;   // was 4.0
private const float UIImageBrightnessBoost = 256.0f;   // was 16.0 (32²)
```

Bump `UIMaterialVersion` to 4 to force re-patching of all cached materials.

This will over-expose in lighter environments (loading screens) but at least makes
text readable in-game. Can be refined later with dynamic compensation (3D below).

### Approach 1D — Dynamic exposure compensation (future refinement)

Read the scene camera's live exposure value and scale boosts to compensate:

```csharp
// In ScanAndConvertCanvases (every 90 frames):
try
{
    var hdScene = HDCamera.GetOrCreate(_gameCamComponent);
    var expTex = hdScene.GetExposureTexture();
    // Read the 1×1 RFloat value via AsyncGPUReadback or RenderTexture.ReadPixels
    float ev = /* log₂ value from texture */;
    float compensationFactor = Mathf.Pow(2f, ev);  // boost = 2^EV to cancel exposure
    // Apply to UI materials...
}
```

This is the most correct approach but requires async GPU readback which adds
complexity. Defer to a future pass after 1A/1B/1C establishes baseline readability.

---

## Task 2 — Confirm deployed fixes (no code change)

Run game after Task 1 is applied. Verify in headset:

- **Right-eye jitter**: GL.InvalidateState fix — look for shimmer in right eye
  - If still jittering: swap render order (right first → left second) to determine
    if jitter follows second-rendered eye vs. always right eye
- **Blue-box on ESC**: press Y to open/close pause menu; confirm no blue flash from
  ActionPanelCanvas being repositioned

---

## Task 3 — Per-canvas positioning strategy

**File**: `VRCamera.cs`, `PositionCanvases()` ~line 2189
**Time**: 30 min

Currently all canvases are placed at `UIDistance = 2.0 m` with the same scale. This
means HUD elements, menus, and panels all overlap at the same spot.

### 5a — Canvas name → category mapping

Add a lookup table that maps canvas names to categories and per-category defaults:

```csharp
private enum CanvasCategory { HUD, Menu, Panel, WorldAnchored, Tooltip }

private static readonly Dictionary<string, CanvasCategory> s_canvasCategories = new(
    StringComparer.OrdinalIgnoreCase)
{
    // Category A — HUD
    ["hudCanvas"]                   = CanvasCategory.HUD,
    ["GameCanvas"]                  = CanvasCategory.HUD,
    ["statusCanvas"]                = CanvasCategory.HUD,
    ["interactionProgressCanvas"]   = CanvasCategory.HUD,

    // Category B — Menu
    ["MenuCanvas"]                  = CanvasCategory.Menu,
    ["DialogCanvas"]                = CanvasCategory.Menu,
    ["controlsCanvas"]              = CanvasCategory.Menu,
    ["upgradesCanvas"]              = CanvasCategory.Menu,

    // Category C — Panel
    ["caseCanvas"]                  = CanvasCategory.Panel,
    ["contentCanvas"]               = CanvasCategory.Panel,
    ["windowCanvas"]                = CanvasCategory.Panel,
    ["osCanvas"]                    = CanvasCategory.Panel,
    ["keyboardCanvas"]              = CanvasCategory.Panel,
    ["fingerprintDisplayCanvas"]    = CanvasCategory.Panel,
    ["mapLayerCanvas"]              = CanvasCategory.Panel,
    ["PrototypeBuilderCanvas"]      = CanvasCategory.Panel,

    // Category D — World-anchored
    ["gameWorldCanvas"]             = CanvasCategory.WorldAnchored,
    ["controlPanelCanvas"]          = CanvasCategory.WorldAnchored,

    // Category E — Tooltip
    ["TooltipCanvas"]               = CanvasCategory.Tooltip,
    ["tooltipsCanvas"]              = CanvasCategory.Tooltip,
};

private struct CanvasCategoryDefaults
{
    public float Distance;      // metres from head
    public float VerticalOffset;// metres above/below eye level
    public float Scale;         // world-units per canvas pixel
    public bool  RecentreOnActivate; // reposition when canvas re-appears
}

private static readonly Dictionary<CanvasCategory, CanvasCategoryDefaults> s_categoryDefaults = new()
{
    [CanvasCategory.HUD]           = new() { Distance = 1.8f, VerticalOffset = -0.15f,
                                             Scale = 0.0012f, RecentreOnActivate = false },
    [CanvasCategory.Menu]          = new() { Distance = 2.0f, VerticalOffset = 0.0f,
                                             Scale = 0.0015f, RecentreOnActivate = true },
    [CanvasCategory.Panel]         = new() { Distance = 1.5f, VerticalOffset = 0.0f,
                                             Scale = 0.0018f, RecentreOnActivate = true },
    [CanvasCategory.WorldAnchored] = new() { Distance = 1.0f, VerticalOffset = 0.0f,
                                             Scale = 0.001f,  RecentreOnActivate = false },
    [CanvasCategory.Tooltip]       = new() { Distance = 1.2f, VerticalOffset = -0.1f,
                                             Scale = 0.001f,  RecentreOnActivate = false },
};
```

### 5b — Apply category defaults in PositionCanvases

When placing a canvas for the first time, look up its category and apply the
category-specific distance, vertical offset, and scale instead of the global
`UIDistance` / `UICanvasScale` constants.

### 5c — Update `s_recentreOnActivate` to use categories

Replace the string-based `s_recentreOnActivate` HashSet with a category check:

```csharp
// In PositionCanvases, replace:
//   if (s_recentreOnActivate.Contains(name)) ...
// with:
if (GetCategory(name).RecentreOnActivate) ...
```

### 5d — Tooltip follows cursor

For `CanvasCategory.Tooltip`, position the canvas at `_cursorAimDepth - 0.02 m` so
it appears just in front of the aimed-at canvas, near the cursor dot. Update its
position every frame (never add to `_positionedCanvases`).

---

## Task 4 — Canvas-specific fixes

### 6a — `gameWorldCanvas` (world-space labels)

The game already creates this canvas as WorldSpace with 3D labels for NPCs, shops,
and items. Currently the scan converts it a second time, resetting its transform.

**Fix**: Skip canvases that are already `RenderMode.WorldSpace` at scan time AND
whose names match known world-anchored canvases.

### 6b — Large panel canvases (case board, map)

`caseCanvas` and `mapLayerCanvas` can be very large (the case board has dozens of
pinned items). These need:
- Larger scale (0.0018 → 0.002) so content is legible at arm's length
- Optional: slight tilt (5° back) so the top is farther away, reducing neck strain

### 6c — Computer / OS interface

`osCanvas` + `keyboardCanvas` should be grouped: keyboard below, screen above.
Position both at the same world point (the in-game computer/terminal) with
keyboard offset by -0.3 m vertically.

---

## Task 5 — Missing canvas handling audit

After Tasks 1–4, do a canvas discovery pass:

1. Add a one-time log dump in `ScanAndConvertCanvases` that prints **every** canvas
   found by `Resources.FindObjectsOfTypeAll<Canvas>()` with name, renderMode,
   and element count.
2. Run through a full gameplay loop: main menu → new game → walk around → interact
   with computer → open case board → talk to NPC → open map → open inventory.
3. Read log and compare against the inventory table above. Any undiscovered canvases
   get added to the category table.

---

## Execution order

```
1. Task 1A — FrameSettingsField.Postprocess  (5 min)
   → Build + test run #1 (user says "done")
   → Read log: check PostprocessOff readback + visual brightness
2. If 1A failed → Task 1B (SetExposureTextureToEmpty)  (30 min)
   → Build + test run #2
3. If 1B failed → Task 1C (boost multiplier increase)  (10 min)
4. Task 2 — Confirm jitter + blue-box  (during any test run above)
5. Task 3 — Per-canvas positioning  (30 min)
   → Build + test run #3
6. Task 4 — Canvas-specific fixes  (20 min)
7. Task 5 — Canvas audit  (15 min, during test run #4)

Deferred (low priority):
- Trigger click debounce
- Y-button frame-counter cooldown
```

Total: ~1.5 hours across 2–3 build-test cycles.

---

## Verification checklist

- [ ] Main menu: all text clearly legible (not dark)
- [ ] In-game HUD: crosshair, status bars, notifications visible
- [ ] Right eye: no temporal jitter (smooth as left eye)
- [ ] ESC press: no blue rectangle flash
- [ ] Dialog canvas: recentres in front of head when NPC conversation starts
- [ ] Case board: readable at arm's length, correctly scaled
- [ ] Tooltips: appear near cursor/controller, not at fixed point
- [ ] Computer screen: OS canvas + keyboard canvas properly grouped
- [ ] Map: full map canvas scaled appropriately, navigable
- [ ] No canvases stacked on top of each other at same distance
- [ ] Pause menu → resume: HUD still in correct position
- [ ] Scene transition (load game): canvases survive, no crash

---

## Files modified

| File | Changes |
|------|---------|
| `SoDVR/VR/VRCamera.cs` | Tasks 1–5: s_VrDisabledFields, exposure reset, canvas categories, positioning overhaul |

---

## Key APIs discovered from Mono assemblies

### HDRP exposure (from `Unity.RenderPipelines.HighDefinition.Runtime.dll`)

```
HDCamera.GetOrCreate(Camera)          → HDCamera wrapper
HDCamera.GetExposureTexture()         → current 1×1 RFloat exposure RT
HDCamera.GetExposureTextureHandle()   → RTHandle version
HDCamera.GetPreviousExposureTexture() → previous frame
HDCamera.SetExposureTextureToEmpty()  → reset to m_EmptyExposureTexture (neutral)
HDCamera.m_ExposureTextures           → exposure texture array (private)
HDCamera.m_EmptyExposureTexture       → pre-allocated neutral texture (private)
HDCamera.m_ExposureControlFS          → exposure control frame setting (private)
```

### FrameSettingsField post-processing fields

```
FrameSettingsField.Postprocess           — master toggle (disables entire PP stack)
FrameSettingsField.ExposureControl       — exposure only (KNOWN: does not persist per-camera)
FrameSettingsField.Tonemapping           — tonemapping only (CONFIRMED: persists)
FrameSettingsField.Bloom
FrameSettingsField.DepthOfField
FrameSettingsField.MotionBlur
FrameSettingsField.ChromaticAberration
FrameSettingsField.ColorGrading
FrameSettingsField.FilmGrain
FrameSettingsField.Dithering
FrameSettingsField.LensDistortion
FrameSettingsField.Vignette
FrameSettingsField.LensFlareDataDriven
```

### Game UI architecture (from `Assembly-CSharp.dll`)

```
InterfaceController           — master UI manager, owns all canvas references
  .hudCanvas                  — HUD crosshair/awareness
  .statusCanvas               — health/energy bars
  .menuCanvas                 — main menu
  .dialogCanvas               — NPC conversations
  .caseCanvas                 — investigation board
  .contentCanvas              — document viewer
  .windowCanvas               — in-game windows
  .tooltipsCanvas             — tooltips
  .minimapCanvas              — minimap
  .osCanvas                   — computer desktop
  .keyboardCanvas             — virtual keyboard
  .controlPanelCanvas         — door/safe/intercom
  .controlsCanvas             — settings/keybinds
  .upgradesCanvas             — sync disks
  .gameWorldCanvas            — world-space 3D labels
  .interactionProgressCanvas  — lockpick/hack progress
  .fingerprintDisplayCanvas   — fingerprint scanner
  .mapLayerCanvas             — full map layers

StatusController              — HUD bars
NotificationController        — alerts/notifications
MapController                 — map open/close
CasePanelController           — case board logic
DialogController              — conversation display
TooltipController             — tooltip positioning
VirtualCursorController       — virtual cursor
```
