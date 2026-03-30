# FAILURE-CaseBoard.md
# Everything tried for case board / UI polish — Phase 9

## Root cause (discovered 2026-03-29)

**All canvas size/scale problems share one root cause:**
`SetParent(null, true)` in the reparent pass preserves the world rect.
At reparent time the CanvasScaler has already inflated every canvas's `sizeDelta`
from the designed reference (e.g. 1280×720) to match the display resolution:

| Canvas | Designed | Actual sizeDelta | Actual worldSize at our scale |
|---|---|---|---|
| WindowCanvas | 1280×720 | **3200×1800** | 1.92m×1.08m (at 0.0006) |
| MenuCanvas, CaseCanvas, LocationDetailsCanvas, etc. | ~1920×1080 | **2720×1680** | 4.08m×2.52m (at 0.0015) |
| MinimapCanvas | ~1920×1080 | **2720×1680** | 3.54m×2.18m (at 0.0013) — so large the map appears "empty" |

Fixed scale values (e.g. 0.0015 per pixel) were tuned for 1920-pixel-wide canvases.
When the actual canvas is 2720 pixels wide, 0.0015 gives a 4m-wide wall.

**Fix needed**: dynamic scale = (targetWorldWidth_m) / sizeDelta.x,
computed per-canvas from actual sizeDelta, applied in a post-reparent correction pass.

---

## Cursor dot — "no dot visible"

**What we know (from log):**
- Dot IS active and in-bounds (log: `Cursor: dist=1.45 px=(294,-262)`)
- `PostprocessOff=True`, `TonemapOff=True` on VR cameras — VR camera's own post-processing is off
- `img.color = Color.red` (or Color.white), `dotMat.renderQueue = 5000`
- `unity_GUIZTestMode = 8` (ZTest=Always) applied

**Things tried:**
1. `dotMat.renderQueue = 5000`, `SetInt("unity_GUIZTestMode", 8)` — no effect; dot invisible
2. `img.color = Color.red` — invisible; HDRP inherited auto-exposure from scene camera
   (EV≈8–12 → multiplier 1/256–1/4096) darkens (1,0,0) to (0.004,0,0) ≈ black
3. Changed to `img.color = Color.white`, `dotMat.color = new Color(4096,0,4096)` (HDR boost) — result unknown; exposed to inherited exposure issue
4. Increased sizeDelta from 20×20 to 60×60 canvas units
5. Added cursor canvas to `_ownedCanvasIds` to protect material from RescanCanvasAlpha overwrite —
   previously RescanCanvasAlpha was overwriting q=5000 → q=3008, but q=3008 also has ZTest=Always
   so render queue wasn't the blocker

**Root cause of invisibility:**
HDRP auto-exposure is INHERITED from the scene camera even when `PostprocessOff=True` on VR cameras.
City interior EV≈8–12 → multiplier ≈ 1/256 to 1/4096.
Any color ≤ (1,1,1) vertex colour → rendered near-black.

**Status:** HDR dot color (×4096 boost via material._Color) deployed but untested yet.

---

## Laser pointer — "no laser visible"

**What we know:**
- `VRLaserBeam created` ✓ (log confirmed)
- `Laser shader: HDRP/Unlit` ✓ (found and applied)
- Positions are set every frame in UpdateControllerPose
- LineRenderer is enabled when controller pose is valid

**Things tried:**
1. `Shader.Find("Sprites/Default")` — not rendered by HDRP for 3D LineRenderer (HDRP ignores non-HDRP shaders)
2. `Shader.Find("HDRP/Unlit")` with `mat.color = Color.cyan` — shader found, not visible
   (same HDRP inherited exposure issue as cursor dot — cyan (0,1,1) → (0,0.004,0.004) = black)
3. `HDRP/Unlit` with HDR cyan `(0,4,4)` — better but still likely below visible threshold at EV12
4. `HDRP/Unlit` with `(0,4096,4096)` — deployed but untested

**Root cause of invisibility:** same as cursor dot — HDRP inherited auto-exposure.

---

## Canvas size ("giant wall") — notepad, notes, case board elements

**Things tried:**
1. `WindowCanvas` set to Menu category (scale=0.0015) → worldSize 2.88m × 1.62m (too big)
2. New `SubPanel` category (scale=0.0008) → worldSize 1.92m × 1.08m (still "giant wall" per user)
3. SubPanel scale reduced to 0.0006 → worldSize 1.92m × 1.08m ← because sizeDelta is 3200, not 1280!
4. **Root cause found**: all canvases have inflated sizeDelta (2720–3200) due to CanvasScaler
   → ALL fixed scale values are wrong; need dynamic scale per actual sizeDelta

**Status:** Not fixed. Needs dynamic scale correction pass.

---

## Canvas persists after close ("giant notepad remains in world")

**Things tried:**
1. `_canvasWasActive` tracks `activeSelf && canvas.enabled` → does not catch CanvasGroup.alpha=0 close
2. Added `CanvasGroup.alpha < 0.1` check to `_canvasWasActive` tracking loop
3. Added `CanvasGroup.alpha < 0.1` skip to placement loop
4. Added `CanvasGroup.alpha < 0.1` skip to depth scan

**Status:** Deployed but untested. May work if the game closes WindowCanvas via CanvasGroup fade.
Unknown if the game actually uses CanvasGroup.alpha to close this canvas.

---

## Selection UI not recentering on reopen

**What we know:**
- CaseCanvas in Panel category (recentre=true) ✓
- `_canvasWasActive` detects false→true transitions and calls `_positionedCanvases.Remove`
- `ActionPanelCanvas` (the selection buttons) appears to be always active (never goes inactive)

**Things tried:**
1. Changed `_canvasWasActive` to track `activeSelf && canvas.enabled` — works for SetActive/enabled changes
2. Added CanvasGroup.alpha tracking to `_canvasWasActive` — should catch alpha-fade closes

**Hypothesis:** ActionPanelCanvas may never go inactive between selections; game just swaps content
in-place. If so, no false→true transition → no recentre trigger.

**Status:** Not confirmed fixed.

---

## Cursor dot not appearing on case board elements

**What we know:**
- `CaseDiag 'CaseCanvas': totalGraphics=4` always — case board selection/action graphics NOT on CaseCanvas
- Depth scan DOES find CaseCanvas (`CaseDepth: oob=False depth=2.10`)
- BUT no cursor hit is registered on case board elements because raycastTarget is not set on relevant graphics

**Unknown:** which canvas actually holds the case board action button graphics (the selection UI).
`ActionPanelCanvas` is a separate canvas — it contains the action buttons that appear when you
click on a board element. It is not visually part of CaseCanvas.

**Things tried:**
1. `forceRaycastTarget = true` on Panel/Menu category canvases — applied to CaseCanvas and ActionPanelCanvas
2. Reparent pass gives CaseCanvas independence from GameCanvas — scale/drift fixed ✓

**Status:** Unknown if the actual clickable elements (pins, notes, strings on the board) have raycastTarget.
These are likely in `GameCanvas` or as world-space GameObjects, not on any canvas.

---

## Map empty

**Root cause (confirmed):**
`MinimapCanvas` sizeDelta = 2720×1680 at scale 0.0013 → worldSize = 3.54m × 2.18m.
The map is placed in front of the user but is 3.5m wide — far larger than the field of view.
Individual map markers are spread across 3.5m so the map appears empty.

**Fix:** dynamic scale correction (same fix as all other canvas size issues).

---

## Ghost canvas / invisible wall after closing case board

**Root cause:** canvas hidden by `CanvasGroup.alpha=0` (not `SetActive`) stays in depth scan.
The ray hits the invisible canvas plane and pins cursor depth to the ghost canvas.

**Fix deployed:** `CanvasGroup.alpha < 0.1` check added to depth scan.
**Status:** Should work. Untested.

---

## "Clone canvas" hypothesis — DISPROVED

**User hypothesis:** "missing clone canvases might be causing problems."

**What we found:** There are NO `(Clone)` root canvases in the scene.
All important canvases (`WindowCanvas`, `LocationDetailsCanvas`, `ActionPanelCanvas`, `CaseCanvas`)
are regular named root canvases already tracked. The `(Clone)` suffix only appears on small
graphic children inside canvases (e.g. `NewCustomFactButton(Clone)`).

**Changes made and reverted:**
1. Added blanket `(Clone)` filter to root canvas loop — WRONG DIRECTION, reverted
2. Removed blanket `(Clone)` filter from nested canvas loop (replaced with name-specific filter)
   → caused map regression (map element clones got patched and broke rendering)
   → reverted to original blanket `(Clone)` filter + added `Loading Icon` to filter

---

## What needs doing (prioritised)

1. **Dynamic scale correction pass** (fixes ALL size issues including map empty):
   - After reparent pass, iterate all non-nested managed canvases
   - Read actual `sizeDelta.x` from RectTransform
   - Set `localScale = targetWorldWidth_m / sizeDelta.x`
   - Target widths: Menu=1.8m, Panel=2.0m, HUD=2.0m, SubPanel=1.0m, Default=2.0m

2. **HDRP inherited exposure** (fixes cursor dot + laser + all UI text visibility):
   - Option A: CommandBuffer to overwrite HDRP 1×1 exposure texture to neutral (log2=0)
   - Option B: Read live EV from HDCamera internals, apply compensating vertex-colour boost per frame
   - Option C: Find a way to set `FrameSettingsField.ExposureControl` correctly per-camera

3. **Ghost canvas depth scan** — CanvasGroup.alpha check deployed, likely working

4. **Selection UI recentering** — need to confirm which canvas and how it hides/shows
