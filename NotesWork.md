# VR Case Board Interaction Issues — Work Notes

## Status: UNRESOLVED (parked 2026-04-01)

Three interrelated problems with case board context menus, opened pinned notes, and pin proximity detection in VR. Multiple fix attempts have failed or only partially helped.

---

## Problem 1: Context Menu Aim Dot / Visual Misalignment

### What happens
When you right-click a pin on the case board, a context menu (`ContextMenu(Clone)`) appears. The aim dot (VR cursor) does not align with the visual location of the context menu — clicking menu items misses.

### Root cause analysis
The context menu lives inside `TooltipCanvas → ContextMenus → ContextMenu(Clone)`. The game sets `ContextMenu(Clone).localPosition` to **screen coordinates** every frame (e.g. `(-960, 540, 0)` for a 1920x1080 display). In WorldSpace mode, this offsets the visual content ~0.5m from the canvas center.

Our code zeros `localPosition` of both `ContextMenus` and its children every frame (3 locations: PositionCanvases in LateUpdate, Update enforce block, ForceItemPositionPreRender). However, the game ALSO resets these every frame in its own Update.

**Critical evidence from log line 1047:**
```
CM diag: child 'ContextMenu(Clone)' localPos=(-960.00, 540.00, 0.00)
         localRot=(0.00, 284.11, 0.00) localScale=(1.00, 1.00, 0.00)
```
Note: **localScale.z = 0** — the game sets Z scale to zero! And `localRot.y = 284.11` — game sets a non-zero Y rotation. Our zeroing resets position/rotation/scale to identity, but the game may override before the next aim dot scan.

### What we tried
1. **Original approach**: Post-loop code tried to raycast against `ContextMenus` Canvas component directly. **Failed**: ContextMenus likely has no Canvas component (`GetComponent<Canvas>()` returns null) — the post-loop code never produced a hit.

2. **Current approach (session 2026-04-01)**: Let TooltipCanvas through the main aim dot loop when `_prevContextMenuActive` is true (removed the `continue` that was skipping it). Removed the broken post-loop code. **Result**: AimDot now reports `primary → 'TooltipCanvas'` (log lines 1057-1097), but visual is still misaligned — user reported "no problems fixed."

### Theories for why it's still broken
- **Race condition**: The game's Update sets ContextMenu(Clone) to screen coords; our Update zeroing runs but the game may override again in the same frame (Unity component execution order is not guaranteed between components).
- **The game's localScale.z = 0**: Even if position is zeroed, a Z-scale of 0 might collapse the visual into a plane that doesn't render correctly or doesn't match the aim dot intersection.
- **The game's localRotation**: Game sets Y rotation to ~284 degrees. Our zeroing resets to identity but game may override before render.
- **Possible approach**: Instead of fighting the game's per-frame updates, intercept at a LOWER level — e.g. use `Harmony` postfix on whatever method sets the position, or reparent the ContextMenu(Clone) out of the game's control entirely (similar to how HUD canvases are reparented to `_hudAnchor`).

### Code locations (line numbers as of 2026-04-01)
- **Context menu detection (PositionCanvases)**: Lines 3262-3274 — local `contextMenuFrozen` variable, filters by `childName.StartsWith("ContextMenu")`
- **Context menu detection (UpdateGripDrag)**: Lines 4566-4580 — `contextMenuNowActive`, same filter
- **Freeze position computation**: Lines 3279-3286 — `_contextMenuFreezePos = headPos + forward * dist + up * vertOffset`
- **Zeroing (PositionCanvases)**: Lines 3320-3340 — zeros ContextMenus and first active child
- **Zeroing (Update enforce)**: Lines 3720-3744 — same pattern, runs before aim dot scan
- **Zeroing (pre-render)**: Line ~5484-5509 — same pattern
- **Aim dot scan skip logic**: Lines 3777-3788 — RepositionEveryFrame canvases: skip unless `_prevContextMenuActive` or dialog active
- **TryClickCanvas ContextMenus addition**: Lines 6207-6230 — adds ContextMenus canvas to click hits (uses `cmTr.GetComponent<Canvas>()` which may be null)
- **Diagnostic logging**: Lines 3288-3312 — fires on first freeze frame, shows transforms before zeroing

---

## Problem 2: Opened Pinned Notes (WindowCanvas) Misalignment

### What happens
When you click a pinned note on the case board to open it, `WindowCanvas` appears showing the note content. The aim dot does not line up with the visual — clicking buttons/text in the note misses.

### Root cause analysis
Not fully diagnosed. WindowCanvas is a separate managed canvas (not nested under TooltipCanvas). It should go through the normal aim dot scan. The misalignment suggests either:
1. WindowCanvas's world position/rotation doesn't match where it visually renders
2. The aim dot plane calculation is wrong for WindowCanvas
3. The game is repositioning WindowCanvas children to screen coordinates (same pattern as ContextMenus)

### What we tried
Nothing specific for WindowCanvas — all efforts were on ContextMenus.

### Investigation needed
- Add diagnostic logging when WindowCanvas is the aim dot primary — log its transform, lossyScale, sizeDelta, and the hit point in local coords
- Check if WindowCanvas has children being repositioned to screen coordinates
- Check if WindowCanvas uses RepositionEveryFrame category (it shouldn't — it's a panel, not a tooltip)
- Verify the bounds check passes correctly for WindowCanvas

---

## Problem 3: Pin Proximity "Stealing" — Wrong Pin Gets Clicks

### What happens
When two or more pinned notes are on the case board, clicking/dragging one pin often targets the wrong pin. One pin "steals" all interactions even when aiming directly at the other.

### Root cause analysis — coordinate space mismatch
The pin proximity scan (lines 4284-4358) does:
1. `hitLocal = pinnedRT.InverseTransformPoint(worldHitPoint)` — returns coords in Pinned container's **local space** (pivot-relative)
2. Compares against pin positions

**Originally** used `pinRT.anchoredPosition` for pins. `anchoredPosition` is relative to the pin's **anchors**, NOT the parent's pivot. If anchors aren't at (0.5, 0.5), these are in different coordinate spaces → distance comparison is wrong → wrong pin wins.

**Fixed (2026-04-01)** to use `pinRT.localPosition` instead, which IS in the same space as `InverseTransformPoint`. Also updated drag offset, start position, and drag update to use `localPosition` consistently.

### Evidence from log
**Before fix — pins found successfully with 1 pin:**
```
CB pin found via Pinned scan: 'PlayerStickyNote' canvasDist=313 localPos=(2000,1500) hitLocal=(1691,1548)
CB pin found via Pinned scan: 'PlayerStickyNote' canvasDist=108 localPos=(-428,765) hitLocal=(-348,837)
```

**After context menu episode — 2 pins, ALL clicks fail:**
```
CB Pinned scan: 2 children, no pin close enough
```
(Repeated ~30 times with no successful finds)

### Why the fix didn't help
The localPosition vs anchoredPosition fix is theoretically correct, BUT:
1. The "2 children, no pin close enough" messages show bestDist > 400 for BOTH pins — this means `hitLocal` is far from both pins' `localPosition`. Since the same InverseTransformPoint logic worked fine BEFORE the context menu was opened (lines 798-1037), something about the case board state changes after a context menu is shown.
2. Possible cause: the context menu freeze/unfreeze cycle changes the Pinned container's transform, or the case board is repositioned (grip-dragged), making the old InverseTransformPoint values drift.
3. Alternative: the Pinned container RectTransform's anchors/pivot change when pins are added, making InverseTransformPoint return different values.

### Investigation needed
- Add diagnostic logging to the "no pin close enough" path: log ALL pin localPositions and distances, plus the hitLocal value, to see where the mismatch is
- Check if `pinnedRT` (Pinned container RectTransform) changes its anchors/pivot/size over time
- Check if `_cbContentContainerRT` fallback is being used instead of `pinnedRT` — that could change the coordinate space
- Test whether the issue reproduces WITHOUT opening a context menu first (is it a state corruption from freeze/unfreeze?)
- Consider logging `pinnedRT.anchorMin`, `pinnedRT.anchorMax`, `pinnedRT.pivot`, `pinnedRT.sizeDelta` to understand the coordinate system

---

## Problem 4: PinnedQuickMenu Hover Tooltip Triggering Context Menu Freeze

### What happens
When hovering over a pin, the game shows `PinnedQuickMenu(Clone)` (a hover tooltip) inside ContextMenus. This was being detected as a context menu, triggering the freeze logic. PinnedQuickMenu flickers on/off rapidly, causing rapid freeze/unfreeze oscillation.

### Fix applied (2026-04-01)
Both detection sites now filter by child name:
- **PositionCanvases** (line 3272): `if (childName.StartsWith("ContextMenu")) { contextMenuFrozen = true; break; }`
- **UpdateGripDrag** (line 4589): same filter for `contextMenuNowActive`

**Status**: Fix applied but user reported "no problems fixed." The PinnedQuickMenu filtering may be working but masked by other issues. Need to verify with diagnostic logging.

---

## Architecture Overview — How These Systems Interact

### Frame execution order
```
Update:
  1. Enforce _canvasVRPose (lines ~3670-3750) — includes ContextMenus zeroing
  2. Aim dot scan (lines ~3757-3838) — raycast against managed canvases
  3. Trigger/click handling — TryClickCanvas, pin drag, etc.
  4. UpdateGripDrag (lines ~4560-4700) — context menu detection, _prevContextMenuActive set

LateUpdate:
  1. PositionCanvases (lines ~2900-3500) — canvas placement, context menu freeze, ContextMenus zeroing
  2. Snapshot _canvasVRPose
  3. Render (stereo frame)
```

### Key insight: _prevContextMenuActive is one frame delayed
`_prevContextMenuActive` is set at the END of UpdateGripDrag (step 4 in Update). The aim dot scan (step 2) reads the PREVIOUS frame's value. This means the aim dot scan uses stale state for 1 frame after a context menu opens/closes.

### Key insight: ContextMenus zeroing runs 3 times per frame
1. Update enforce (line ~3720) — before aim dot scan ✓
2. PositionCanvases LateUpdate (line ~3320) — after aim dot scan, before render
3. Pre-render (line ~5484) — right before render

But the game ALSO writes to these transforms. The race depends on Unity's component update order.

### Canvas hierarchy for context menus
```
TooltipCanvas (managed, CanvasCategory.Tooltip, RepositionEveryFrame=true)
  └── ContextMenus (nested, CanvasCategory.Ignored)
      ├── ContextMenu(Clone)     ← actual right-click menu
      │   └── Border → items...
      └── PinnedQuickMenu(Clone) ← hover tooltip (flickers)
```

### Canvas hierarchy for case board pins
```
ActionPanelCanvas (anchor, not draggable)
  └── CaseCanvas
      └── Content (with _cbContentContainerRT)
          └── Pinned
              ├── PlayerStickyNote (pin 1)
              ├── PlayerStickyNote (pin 2)
              └── ...
```

---

## Potential New Approaches to Try

### For context menu alignment (Problem 1)
1. **Reparent ContextMenu(Clone)** out of ContextMenus entirely — make it a direct child of a VR anchor, similar to how HUD canvases are reparented. This takes it out of the game's per-frame position updates.
2. **Disable the game's positioning component** — find what MonoBehaviour sets ContextMenu(Clone) position and disable it (via `GetComponent<>().enabled = false`).
3. **Use Harmony postfix** to intercept the position-setting method and suppress it.
4. **Accept screen coordinates** — instead of zeroing, read the screen coordinates the game sets and convert them to the correct WorldSpace local position relative to the case board.

### For pin proximity (Problem 3)
1. **Log ALL pin distances** on every failed scan to see what hitLocal vs localPosition values actually are.
2. **Use world-space distance** with a generous threshold — even though lossyScale ≈ 0.0003 clusters positions, there may be enough difference for a relative comparison.
3. **Screen-space raycast** — convert VR aim ray to screen coordinates and use the game's existing ScreenPointToLocalPointInRectangle.
4. **Check if Pinned container changes** after context menu open/close.

### For WindowCanvas alignment (Problem 2)
1. **Add diagnostic logging** — need to understand what's happening before fixing.
2. **Check if WindowCanvas has CanvasScaler** re-enabled (our fix disables it, but something could re-enable).

---

## Files Modified
- `SoDVR/VR/VRCamera.cs` — all changes in this file

## Relevant Log
- `BepInEx/LogOutput.log` from 2026-04-01 04:52 session
- Search for: `CM diag`, `AimDot primary`, `ContextMenus added`, `context menu freeze`, `pin found`, `Pinned scan`
