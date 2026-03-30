# Phase 10: World Graphics — Detailed Plan

## Problem Statement

User reports:
1. **Most walls and floors are invisible**
2. **Many props are invisible**
3. **Lighting does not work** — dark areas appear fully lit, light sources have no visible effect

## Root Cause Analysis

After reviewing VRCamera.cs, there are **three primary causes**, all in the camera/render setup:

---

### Cause 1: `GL.invertCulling = true` applied to BOTH eyes (HIGH — invisible geometry)

**Location:** VRCamera.cs lines 1059–1067

```csharp
GL.invertCulling = true;      // line 1059
_rightCam.Render();           // right eye — culling inverted ✓ (correct for RT Y-flip)
_leftCam.Render();            // left eye  — culling ALSO inverted ✗ (WRONG)
GL.invertCulling = false;     // line 1067
```

When rendering to a RenderTexture, Unity flips the image vertically (D3D11 convention). This reverses triangle winding, so `GL.invertCulling = true` is needed to compensate. **However, each `Camera.Render()` call is independent** — the flag must be set/cleared per eye, not wrapped around both.

Currently: both eyes render with inverted culling. For one eye this is correct; for the other it means **front faces are culled instead of back faces**, making walls, floors, and props invisible from many viewing angles. The reason "most" walls are invisible (not all) is because some geometry is double-sided or viewed at angles where the winding happens to work.

**Fix:** Toggle `GL.invertCulling` independently per eye render call.

---

### Cause 2: `allowHDR = false` (HIGH — breaks HDRP material pipeline)

**Location:** VRCamera.cs line 958

```csharp
cam.allowHDR = false;
```

HDRP is a **fundamentally HDR pipeline**. All lit materials, shaders, and the lighting system output HDR values (values > 1.0). When `allowHDR = false`:
- The RenderTexture is LDR (ARGB32), clamping all values to [0, 1]
- HDRP lit shaders still output HDR values, but they get clamped
- Materials with PBR metallic/roughness workflows produce washed-out or black output
- **Dark surfaces with subtle lighting become invisible** (HDR value 0.001 rounds to 0)
- **Bright surfaces clip to white** (everything looks uniformly bright — "no lighting")

This explains both symptoms: invisible geometry AND broken lighting (everything appears evenly lit because HDR dynamic range is destroyed).

**Fix:** Enable `allowHDR = true` and use HDR-capable RenderTextures (format `ARGBHalf` or `DefaultHDR`).

---

### Cause 3: `FrameSettingsField.Postprocess` disabled — kills tonemapping (MEDIUM — contributes to lighting issues)

**Location:** VRCamera.cs line 918

Disabling Postprocess is a "master kill" that removes:
- **Tonemapping** — maps HDR values to displayable range. Without it, even with HDR RTs, output won't look right.
- **Exposure** — auto-adjusts brightness for scene lighting. Without it, dark rooms and bright exteriors look the same.
- **Bloom** — light sources don't glow, reducing perception of "working lighting"

The original rationale was to prevent VR sickness from post-processing effects (motion blur, DoF, chromatic aberration). But tonemapping and exposure are **not comfort-affecting** — they're essential for HDRP to produce a visible image.

**Fix:** Selectively re-enable tonemapping, exposure, and color grading while keeping comfort-affecting effects disabled.

---

## Implementation Plan

### Step 1: Fix GL.invertCulling per-eye (quick win, may fix most invisible geometry)

Change the render loop so each eye gets its own culling state:

```csharp
// Right eye
GL.invertCulling = true;
GL.InvalidateState();
_rightCam.Render();
GL.Flush();
GL.invertCulling = false;
GL.InvalidateState();

// Left eye
GL.invertCulling = true;
GL.InvalidateState();
_leftCam.Render();
GL.Flush();
GL.invertCulling = false;
GL.InvalidateState();
```

**Why per-eye reset matters:** `Camera.Render()` may leave GPU state dirty. Resetting `invertCulling` between calls ensures each eye starts with a clean culling state. If both eyes need inversion (because both render to RTs), the reset+set pattern ensures HDRP's internal state is consistent.

**Test:** Build & run. If walls/floors reappear, this was the primary cause.

---

### Step 2: Enable HDR rendering

Change RenderTexture format and camera HDR flag:

```csharp
// In BuildCameraRig — RenderTexture creation:
_leftRT  = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR) { name = "SoDVR_Left" };
_rightRT = new RenderTexture(w, h, 24, RenderTextureFormat.DefaultHDR) { name = "SoDVR_Right" };

// In SetupEyeCam:
cam.allowHDR = true;   // was false
```

**Swapchain concern:** The OpenXR swapchain is format 28 (R8G8B8A8_UNORM, LDR). The `CopyResource` D3D11 call copies the RT to the swapchain. If the RT is now HDR (R16G16B16A16_FLOAT, format 10), `CopyResource` requires matching formats.

**Two options:**
- **Option A:** Keep swapchain format 28, add an intermediate blit that tonemaps HDR→LDR before copy. Unity's `Graphics.Blit` with a tonemapping shader, or just let HDRP's tonemapper handle it (if re-enabled in Step 3).
- **Option B:** If HDRP tonemapping is re-enabled (Step 3), the camera's output RT will already contain tonemapped LDR values even though the RT format is HDR. `CopyResource` from R16G16B16A16 to R8G8B8A8 will truncate but the values are already in [0,1] range after tonemapping. **This may just work.**

**Preferred approach:** Do Step 2 + Step 3 together. HDRP tonemaps into the HDR RT, values are [0,1], CopyResource truncates to 8-bit. If visual artifacts appear, add explicit blit.

**Fallback if CopyResource fails:** Use `Graphics.Blit(_leftRT, ldrStagingRT)` with a simple shader or built-in blit to convert HDR→LDR before the D3D11 copy.

---

### Step 3: Re-enable essential HDRP post-processing

Selectively re-enable tonemapping, exposure, and color grading:

```csharp
// Change s_VrDisabledFields to KEEP these enabled:
private static readonly FrameSettingsField[] s_VrDisabledFields = {
    // REMOVED: FrameSettingsField.Postprocess  (was master kill — now enabled)
    // REMOVED: FrameSettingsField.Tonemapping  (needed for HDR→visible)
    FrameSettingsField.SSAO,                // keep disabled for perf
    FrameSettingsField.SSR,                 // keep disabled for perf
    FrameSettingsField.Volumetrics,         // keep disabled for perf + VR comfort
    FrameSettingsField.MotionVectors,       // not needed in VR
    FrameSettingsField.MotionBlur,          // VR sickness
    FrameSettingsField.DepthOfField,        // VR sickness
    FrameSettingsField.ChromaticAberration, // VR sickness
    FrameSettingsField.ContactShadows,      // keep disabled for perf
};
```

This re-enables:
- **Postprocess pipeline** (master switch)
- **Tonemapping** (HDR → visible range)
- **Exposure** (auto-adapts to scene brightness)
- **Color grading** (game's artistic intent preserved)
- **Bloom** (light sources visible — optional, can disable if distracting)

Keeps disabled: motion blur, DoF, chromatic aberration (VR comfort), SSAO/SSR/volumetrics/contact shadows (performance).

---

### Step 4: Adjust UI material compensation

With exposure and tonemapping now working, the UI HDR material boosts (32x text, 4x panels) will be **way too bright**. The boost was a workaround for broken exposure — with exposure working, we need to reduce or remove it.

- **Text:** Reduce `_FaceColor`/`_Color` from `(32,32,32,1)` to something closer to `(2,2,2,1)` or `(1,1,1,1)`
- **Panel colors:** Reduce 4x multiplier to 1x or 2x
- **Background alpha:** May need adjustment from 0.07

This will be iterative — build, test, adjust values. May need several rounds.

---

### Step 5: Test and iterate

After Steps 1–3:
1. Build and deploy
2. User tests: do walls/floors appear? Does lighting look correct?
3. If geometry still missing → investigate per-material shader compatibility
4. If lighting too bright/dark → tune exposure compensation or add manual exposure override
5. If UI now invisible (too dim) → adjust Step 4 boost values
6. If CopyResource fails (format mismatch) → add intermediate LDR blit

---

## Execution Order

| Step | Change | Risk | Rollback |
|------|--------|------|----------|
| 1 | GL.invertCulling per-eye | Low | Revert 6 lines |
| 2 | HDR RenderTextures + allowHDR | Medium (format mismatch possible) | Revert to ARGB32 |
| 3 | Re-enable Postprocess/Tonemapping | Medium (UI brightness changes) | Re-add to disabled list |
| 4 | Reduce UI HDR boost | Low | Adjust multiplier values |
| 5 | Test & iterate | — | — |

**Recommendation:** Do Steps 1–3 together in a single build since they're interdependent (HDR without tonemapping = white screen; tonemapping without HDR = no improvement). Step 4 adjustments after first test.

---

## Files to modify

- `SoDVR/VR/VRCamera.cs` — all changes are in this file:
  - `LateUpdate()` render loop (~line 1059) — GL.invertCulling fix
  - `BuildCameraRig()` (~line 747) — RenderTexture format
  - `SetupEyeCam()` (~line 958) — allowHDR flag
  - `s_VrDisabledFields` array (~line 916) — FrameSettings
  - `StrengthenMenuTextMaterial()` — reduce HDR boost (later)
  - `ForceUIZTestAlways()` — reduce color multipliers (later)
