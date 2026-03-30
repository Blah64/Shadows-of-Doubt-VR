# Plan: Fix Missing Content, Giant Canvases, and Crash Prevention

## Status Summary

**Brightness/text readability**: SOLVED — 32x boost is good. Stop adjusting brightness.

**Three remaining problems**:
1. Missing text/icons on ActionPanelCanvas and PopupMessage
2. Pin notes and notebook appearing at enormous scale
3. Game crashes (native D3D device loss)

---

## Problem 1: Missing Text/Icons (ActionPanelCanvas, PopupMessage)

### Root Cause (confirmed from DetailDiag logging)

**ActionPanelCanvas**: Half the graphics use `Mobile/Particles/Additive` shader with `mc=(0.0,0.0,0.0,0.00)`. Our code classifies them as `isAdditive=true` and **skips** all rendering fixes — no renderQueue set, no material color boost. Example:

```
[9]'Icon' sh='Mobile/Particles/Additive' mc=(0.0,0.0,0.0,0.00) vc=(1.00,0.64,0.72,1.00)
```

**Why these are visible without the VR mod but invisible with it:**
Without the VR mod, canvases use `ScreenSpace-Overlay` render mode. Unity's built-in UI rendering path handles legacy shaders (including `Mobile/Particles/Additive`) correctly — it bypasses HDRP entirely.

When we convert canvases to `WorldSpace` for VR, they become 3D geometry rendered through HDRP's pipeline. HDRP does not properly support legacy mobile shaders — `Mobile/Particles/Additive` produces no visible output when rendered as WorldSpace geometry through HDRP. (Note: `UI/Default` works in WorldSpace because Unity special-cases it across all render pipelines.)

**Two problems combine:**
1. The shader itself doesn't render through HDRP's WorldSpace path
2. Our `isAdditive` guard skips these items entirely — no renderQueue, no material fixes

**PopupMessage**: Same issue — decorative elements (InnerFrame, TopBar, LineBreak, borders) all use `Mobile/Particles/Additive`. The structural frame of the popup is invisible, making it look empty even though the text IS boosted (`mc=(32.0,32.0,32.0)`).

**The text items themselves** (e.g. `'Text (TMP)'` at q3009) DO have the 32x boost and SHOULD be visible. But:
- ActionPanelCanvas text may be empty strings (game hasn't populated content because VR click path doesn't trigger the same game logic as flatscreen mouse)
- PopupMessage text IS populated (PanelTitle, MessageText) — so the issue there is that the surrounding frame is invisible, making the popup hard to find/read

### Fix: Replace `Mobile/Particles/Additive` with `UI/Default`

In `ForceUIZTestAlways`, when creating the VRPatch material copy, **replace the shader** for additive items:

```csharp
// Current code skips additive items for renderQueue and color boost.
// NEW: Replace the Additive shader with UI/Default so HDRP actually renders them.
if (isAdditive)
{
    var uiShader = Shader.Find("UI/Default");
    if (uiShader != null) mat.shader = uiShader;
    mat.renderQueue = 3001;  // just above background
    // Additive items use vertex color for their visible content.
    // Set material color to pass-through (white) so vertex color drives appearance.
    mat.color = new Color(1f, 1f, 1f, 0.7f);  // slightly transparent for glow effect
    // Apply same ZTest settings
    mat.SetInt("unity_GUIZTestMode", 8);
}
```

The shader replacement makes the items renderable through HDRP. Setting `mat.color` to white lets the vertex color (which carries the actual icon/border colours) drive the visual appearance — same as how these items worked in ScreenSpace-Overlay.

### Fix: Ensure PopupMessage OptionButtons have raycastTarget=true

DetailDiag shows `[4]'LensFlare' rt=True` but many buttons might not have raycastTarget. The existing `forceRaycastTarget` logic should cover Panel/Menu categories. Verify PopupMessage (Menu category) gets this treatment.

### Additional: Log actual TMP text content for ActionPanelCanvas

Add diagnostic: after `StrengthenMenuTextMaterial`, log the first 50 chars of each TMP_Text.text on ActionPanelCanvas. This will confirm whether the game populates the text or leaves it empty.

---

## Problem 2: Pin Notes and Notebook Appearing Gigantic

### Root Cause Analysis

The log shows correct scales:
```
WindowCanvas [Menu] -> WorldSpace (1920x1080px, scale=0.000625, world=1.20x0.68m)
Reparent 'WindowCanvas': detached, scale=0.000625 sizeDelta=(1920,1080) worldW=1.20m
```

1.20m wide at 1.80m distance is reasonable. But the user reports "1 km." Two possible causes:

**Cause A — `SetParent(null, true)` does NOT adjust children's localPosition/localScale.**

When WindowCanvas is a child of GameCanvas (scale=0.000781):
1. ConvertCanvasToWorldSpace sets WindowCanvas.localScale = 0.000625
2. WindowCanvas lossyScale = 0.000781 × 0.000625 = 0.000000488
3. `SetParent(null, true)` sets WindowCanvas.localScale = 0.000000488 (preserves world scale)
4. Children keep their localPosition/localScale unchanged
5. We override WindowCanvas.localScale = 0.000625 (1282× increase)
6. **Children's world transforms scale by 1282×**

If a child has localPosition (960, 540) and localScale (1, 1, 1):
- Before override: child is at (960×0.000000488, 540×0.000000488) = (0.47mm, 0.26mm) from canvas centre
- After override: child is at (960×0.000625, 540×0.000625) = (0.6m, 0.34m) from canvas centre

That math actually works out fine for the localPosition. BUT if any child has a non-identity localScale (like the "Note" nested canvas scaler inflating it), that child's world size would be 1282× too large.

**Cause B — CanvasScaler on nested canvases is NOT disabled.**

`ConvertCanvasToWorldSpace` disables CanvasScaler on the root canvas, but nested canvases (Note, Scroll View) are processed by `ForceUIZTestAlways` which does NOT disable CanvasScaler. If the nested "Note" canvas has a CanvasScaler that inflates its RectTransform, and then we reparent the parent canvas, the inflated child coordinates get scaled up.

**Cause C — "Pin notes" are NOT canvases at all.**

In Shadows of Doubt, pinning evidence to the case board creates 3D objects in the game world. These objects are positioned at the physical case board location in the player's apartment. Our VR mod repositions the CaseCanvas (a flat background) to be in front of the user, but the 3D pinned objects remain at the game's world position. If the player is far from their apartment, these objects render at huge apparent scale (close to the VR camera because VROrigin is at the game camera position, which is also far from the apartment).

### Fix Plan

**Step 1**: Disable CanvasScaler on ALL nested canvases, not just root canvases. In the nested canvas loop (line 1232), before `ForceUIZTestAlways`:

```csharp
// Disable CanvasScaler on nested canvases too — prevents sizeDelta inflation
// that compounds with the parent's WorldSpace scale.
try {
    var ncScaler = nc.GetComponent<CanvasScaler>();
    if (ncScaler != null) ncScaler.enabled = false;
} catch { }
```

**Step 2**: After the reparent pass overrides localScale, force-reset child canvas RectTransform sizeDelta to reference values. For each reparented canvas, iterate its child canvases and log their sizeDelta to verify.

**Step 3**: Add scale diagnostic logging — after reparent, log the actual lossyScale and the first few children's world positions/scales to catch unexpected scaling.

**Step 4**: For "pin notes" (3D case board objects) — investigate whether these are canvases or 3D GameObjects. If 3D objects, we'd need to hook into the game's case board creation system to reposition them relative to our CaseCanvas placement. This is a larger effort and may be deferred.

---

## Problem 3: Game Crashes (D3D Device Loss)

### Observed Pattern

Every session ends with the log cutting off mid-line without any exception. This is a native D3D11 device loss or GPU timeout. Contributing factors from the logs:

1. **Material instance explosion**: `RescanAlpha 'MinimapCanvas': 1370 new graphic(s) patched` — creating 1370 new Material instances in one frame. Each is a `new Material(orig)` allocated on the GPU.

2. **Repeated rescan storms**: MinimapCanvas gets rescanned every 90 frames with hundreds of NEW graphics each time (map markers spawning). Pattern: 1370 → 212 → 370 → 308 → 272 in successive scans.

3. **MenuStencilRelax spam**: `ControlsDisplayCanvas` triggers `MenuStencilRelax` nearly every frame — dozens of times in the logs between user actions. This suggests the canvas state keeps changing, forcing repeated material reprocessing.

4. **Two Camera.Render() calls per frame**: Each stereo frame renders the scene twice (left + right eye). Combined with material explosion, this doubles GPU load.

### Fix Plan

**Fix A — Rate-limit material creation per frame**

Add a material creation budget per frame. If more than N materials are created in a single ForceUIZTestAlways/RescanCanvasAlpha call, defer the rest to the next frame.

```csharp
private const int MaxMaterialsPerFrame = 200;
private int _materialsCreatedThisFrame = 0;
// Reset at start of LateUpdate
// In material creation block: if (++_materialsCreatedThisFrame > MaxMaterialsPerFrame) break;
```

**Fix B — Skip patching MinimapCanvas child graphics that are map markers**

Map markers (elements inside "Content" and "Lines" nested canvases in MinimapCanvas) are dynamically spawned. They don't need individual material patches — they're small dots/icons on the map. Skip patching graphics inside nested canvases named "Content" or "Lines" under MinimapCanvas.

Or simpler: move MinimapCanvas from HUD category to Ignored, and let it render with default materials. The minimap is tiny on the HUD and doesn't need ZTest/stencil fixes.

**Fix C — Cache `ControlsDisplayCanvas` stencil relaxation**

`MenuStencilRelax` is being called every time RescanCanvasAlpha runs for ControlsDisplayCanvas. Add a Set tracking which canvases have been stencil-relaxed to avoid reprocessing.

**Fix D — Reduce canvas scan frequency**

Current: every 90 frames. For a 90fps headset, that's once per second. This is fine for initial setup, but the repeated rescans with hundreds of new graphics are the problem. Change to: scan every 90 frames for the first 10 scans, then every 300 frames (3.3 seconds) thereafter. Or better: only rescan canvases that have had their active state change (tracked via _canvasWasActive).

**Fix E — Pool/reuse materials instead of creating new ones**

The `s_uiZTestMats` cache keyed by `(origId << 2) | boostType` already helps, but when a graphic's material changes (game reassigns materials), the origId changes and a new Material is created. Consider caching by shader+boostType instead of origId+boostType to reuse materials more aggressively.

**Fix F — Disable Camera.Render() during heavy canvas processing**

When ForceUIZTestAlways processes more than 500 graphics in a single frame, skip the stereo render for that frame (return early from LateUpdate after canvas processing). This prevents the GPU from trying to render while thousands of new materials are being uploaded.

### Priority Order for Crash Fixes

1. **Fix D** (reduce scan frequency) — highest impact, easiest to implement
2. **Fix B** (skip MinimapCanvas internals) — eliminates the biggest material creation spike
3. **Fix A** (rate-limit materials) — safety net for any remaining spikes
4. **Fix C** (cache stencil relax) — reduces per-frame overhead
5. **Fix F** (skip render during heavy processing) — prevents GPU timeout

---

## Execution Order

1. **Problem 1** first — replace Additive shaders with UI/Default (highest user impact)
2. **Problem 3 fixes D + B** — reduce crash frequency
3. **Problem 2** — disable nested CanvasScaler + add diagnostics
4. **Problem 3 remaining fixes** — rate limiting, caching
5. Test cycle
