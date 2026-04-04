# SoDVR — Technical Handover

**Date**: 2026-04-04 (updated end of Phase 19)
**Phase**: 20 — polish and remaining issues

---

## Phase 16–19 (2026-04-03/04) — Grip-drag polish + B button COMPLETE ✓

### Phase 16 (commit `e39e82e`)
Case board context menu, string connect, aim dot fixes. See previous HANDOVER notes.

### Phase 17 (commit `9e52b49`)
Note warp fix, individual note 6DOF drag (nested canvas drag), aim dot on moved notes.

### Phase 18 (commits `8338fba`, `92caaec`)

**Grip-drag generalized to all nested canvases**

Previously only "Note" canvases were grip-draggable. Now ALL `_windowNestedList` nested canvases (evidence windows, detective notebook content, etc.) can be individually moved.

Key discoveries:
- `Scroll View` is a child of `Note` in Unity hierarchy → sub-child filter required: skip canvases whose `transform.IsChildOf(other.transform)` for any other nested canvas
- `Scroll View` uses stretch anchors → `sizeDelta` is negative → bounds check always fails if used directly
- Bounds margin 0.65× sizeDelta prevents near-edge misses that fell through to WindowCanvas
- `WindowCanvas` must be excluded from top-level grip-drag — it is a container; grabbing it moved all children at once

**_gripDragEnforce dict** — absolute world position enforcement:
```csharp
private readonly Dictionary<int, (Vector3 pos, Quaternion rot)> _gripDragEnforce = new();
```
- Populated on grip-drag release (alongside `_gripDragAnchorOffsets` relative-to-anchor entry)
- Also updated when `PositionCanvases` restores from anchor offset on case board reopen
- Enforced in BOTH `Update()` (before aim dot scan) AND `LateUpdate()` (after `PositionCanvases`)
- Cleared when canvas destroyed; specific entries cleared on B button release (MinimapCanvas, WindowCanvas)

**Case board reopen**: on ActionPanelCanvas activation, all canvases with `_gripDragAnchorOffsets` entries are removed from `_positionedCanvases` AND `_gripDragEnforce`, so `PositionCanvases` recomputes from anchor-relative offset relative to the new ActionPanelCanvas position.

**Diagnostic log cleanup**: 426 lines of one-time/verbose debug logs removed. Retained: compass one-time diag, pin/arm/carry periodic status, all operational logs.

### Phase 19 (commit `92f92f5`)

**B button: hold-to-show for map/notebook**

Game's Tab key is hold-to-show: map/notebook visible while Tab is held. Previous implementation sent a momentary press (Tab DOWN + deferred UP) → map opened and instantly closed.

Fix: `_tabHeldDown` bool tracks whether we are holding Tab.
```csharp
private bool _tabHeldDown;  // true while we are holding Tab key down
```
- B pressed → `keybd_event(VK_TAB, DOWN)`, `_tabHeldDown = true`
- B held → Tab stays held (no action)
- B released → `ReleaseTabKey()` → `keybd_event(VK_TAB, UP)`, `_tabHeldDown = false`
- `ReleaseTabKey()` also called when case board opens or VR settings open

**Flashing fix**: `_cursorHasTarget` suppression was triggering Tab release when map opened (map became cursor target → suppress → release → close → cursor gone → press again). Fix: skip `_cursorHasTarget` suppression while `_tabHeldDown` is true.

**Fresh placement**: on B release, MinimapCanvas and WindowCanvas removed from `_positionedCanvases` and `_gripDragEnforce` → next open gets fresh head-relative placement.

---

## Phase 15 (2026-04-03) — Minimap interaction COMPLETE ✓

### What was done

**B button pan** — direct `content.anchoredPosition` manipulation on the minimap ScrollRect.
`ExecuteEvents` drag chain (`initializePotentialDrag` → `OnBeginDrag` → `OnDrag`) is not exposed
in IL2CPP interop so ScrollRect.m_Dragging stays false and `OnDrag` returns early. Pan is implemented
by plane-raycasting to the minimap canvas, converting to viewport-local delta each frame.

**Trigger click — open evidence note** — `MapController.mapCursorNode` is driven by our own
`InverseTransformPoint + MapToNode + nodeMap.TryGetValue` each frame, bypassing MapController's broken
`ScreenPointToLocalPointInRectangle(camera=null)` path (always null for WorldSpace canvases).
`_minimapLastKnownNode` holds a fallback for the frame where MapController resets the node after our Update.

**A button right-click — context menu** — `TryRightClickCanvas` calls `mapCtrl.mapContextMenu.OpenMenu()`
directly. Fresh node computed from ray at click time (not stale lastKnown) to avoid wrong-location menus.

**`_minimapCanvasRef` override** — `_cursorTargetCanvas` was always WindowCanvas (evidence note sitting
in front of the minimap). Both A/B button target selection and the per-frame MapCursor update now
plane-raycast + bounds-check against `_minimapCanvasRef` (cached at scan time) independently.

**Pinned note visibility restored** — `isScrollViewport` skip in the RectMask2D disable loop was
leaving RectMask2D enabled on ALL ScrollRect viewports, clipping pinned notes in WorldSpace.

**Floor navigation** — floor +/- buttons on MinimapCanvas are standard UI Buttons, clickable via trigger.
Rooms at load=1+. `mapCtrl.load` is the floor currently displayed. `_minimapLastKnownNode` is cleared
when `mapCtrl.load` changes.

### Key fields added
```csharp
private Canvas?  _minimapCanvasRef;      // cached at scan time
private NewNode? _minimapLastKnownNode;  // fallback when MapController resets mapCursorNode
private float    _minimapLastLoad;       // floor level at last node cache — cleared on floor change
```

### Commit
`4648a66` — "Add minimap interaction: pan, location click, context menu"

---

## Phase 14 Session 2 (2026-04-02) — Compass arrow + TDR fixes

### GPU TDR fix — DONE ✓

`UpdateInventory()` was setting `_gameCamRef.transform.rotation = _leftControllerGO.transform.rotation`
every frame while grip was held.  This drove expensive HDRP shadow/volumetric recalculations → nvlddmkm.sys TDR (crash) on Blackwell GPU.

Fix: removed the rotation override from `UpdateInventory()`.  Camera.main is already redirected to controller direction safely in two places:
1. **End of `Update()`** — game's `InteractionRaycastCheck` runs in a later Update and reads this for action text / interact raycasts
2. **Post-`FrameEndStereo`** — used for held item tracking

### Action text fix — DONE ✓

After removing the grip redirect, `UpdatePose()` (line ~884) was the last writer of Camera.main rotation in Update — it wrote VR head rotation.  The game's interaction system then aimed at the head, not the controller.

Fix: added one line at the very **end** of `Update()` that overwrites Camera.main rotation with the left controller direction.  The game's `InteractionRaycastCheck` runs in a later Update (script ordering) and reads the controller direction → action text labels now point from the controller correctly.

### UIPointerController overlays — CONFIRMED WORKING ✓

A force-visibility test (red squares) proved UIPointerController overlays were already rendering.  The "missing arrow" complaint was about a different element entirely.  The force test was reverted and the overlays remain functional (commit `f2652ae`).

### Awareness compass — INITIAL FIX IMPLEMENTED, AWAITING TEST (commit `f040235`)

**What it is**: NOT a canvas.  The awareness compass is a **3D MeshRenderer system** on `InterfaceController`.

```
compassContainer  (public serialized GO)
  └── backgroundTransform  (ring MeshRenderer)
        └── spawned icons  (Instantiate(awarenessIndicator, backgroundTransform))
              ├── imageTransform  (billboard, faces camera)
              └── arrowTransform  (points at threat; localZ ≈ -5 at rest)
```

**Why it's invisible in VR**:
1. `compassContainer` is parented to game camera hierarchy in scene — tracks suppressed game cam
2. `backgroundTransform.rotation = LookRotation(Vector3.forward, Vector3.up)` → faces world-forward, not VR cam
3. `imageTransform.rotation = game cam rotation` = controller direction, not VR head

**Fix added** — `UpdateCompass()` called from `LateUpdate()` after `InterfaceController.Update()`, before render:
```csharp
_compassContainer.position = headPos + headFwd * 1.2f + headUp * (-0.55f);
backgroundTransform.rotation = LookRotation(headFwd, headUp);
foreach icon: imageTransform.rotation = _leftCam.transform.rotation;
```

**New fields**:
```csharp
private Transform? _compassContainer;   // cached step 5h in DiscoverMovementSystem
private bool       _compassDiagDone;
private const float CompassDist    = 1.2f;
private const float CompassYOffset = -0.55f;
```

**Diagnostic logging** fires on first LateUpdate call after discovery — logs world pos, local pos, parent/grandparent names, lossyScale, and icon count. Look for `[Compass]` lines in the log.

**What needs to happen next**:
1. Run game, get into a session where an NPC spots you (spawns awareness icons)
2. Read `[Compass]` log lines — verify parent chain, check world/local positions and scale
3. Tune `CompassDist` / `CompassYOffset` so compass appears at screen-bottom
4. If still invisible: check layer, check material shader compatibility with HDRP
5. Full technical notes: `VRMod/ArrowWork.md`

---

## Phase 14 (Partial — 2026-04-01)

### Done in Phase 14 so far

**A button → right-click on any aimed canvas**
- Previously: A button only right-clicked on the case board
- Now: if `_cursorHasTarget` is true (aim dot on any canvas), right-click targets `_cursorTargetCanvas`; falls back to case board if case board open and no specific aim target
- Field: `private Canvas? _cursorTargetCanvas` — set in the aim dot scan to the nearest hit canvas

**B button → middle-click drag on any aimed canvas**
- Same pattern as A button fix — uses `_cursorTargetCanvas` when aiming at a canvas

**Jump/notebook suppressed when aiming at canvas**
- When `_cursorHasTarget` is true, right-A no longer jumps (it right-clicks instead)
- When `_cursorHasTarget` is true, right-B no longer opens notebook (it middle-clicks instead)

### Case board interaction issues — PARKED

Three problems investigated but NOT fixed. See `NotesWork.md` for full analysis, theories, and suggested next approaches.

**Problem 1: Context menu aim dot / visual misalignment**
Root cause identified: the game writes `ContextMenu(Clone).localPosition` to screen coords, `localRotation.y ≈ 284°`, and `localScale.z = 0` every frame. Our zeroing competes with the game's updates. Z-scale=0 may also prevent correct bounds testing. Aim dot now correctly targets TooltipCanvas (which is frozen in place when context menu is active), but the VISUAL content (ContextMenu(Clone)) may still be offset if our zeroing loses the race.

**Problem 2: Opened pinned notes (WindowCanvas) misalignment**
Not yet diagnosed. WindowCanvas is a managed canvas — it should go through normal aim dot scan. Something makes the aim dot not match the visual location. Needs diagnostic logging.

**Problem 3: Pin proximity "stealing"**
Fixed coordinate space (anchoredPosition → localPosition), but "2 children, no pin close enough" errors appeared consistently after context menu was used. InverseTransformPoint results may drift when the case board state changes. Needs per-pin distance logging to diagnose.

### HUD settings — NOT YET IMPLEMENTED

Plan file: `C:\Users\blah6\.claude\plans\tender-wibbling-sunbeam.md`

The plan adds 5 HUD settings to VR Settings General tab (distance, size, height, H.offset, laggy-follow toggle) plus auto-hide when ESC/case board is open. NOT IMPLEMENTED — ready to execute when returning to this work.

---

## Phase 13: Save-Load Warp Fix & Case Board Polish — COMPLETE ✓

### Save/load warp — root cause chain

1. **Button detection miss**: `TryClickCanvas` hit `'Border'` (child element), not `'Continue'` (the button). Fixed: walk up 6 hierarchy levels to find the button name.

2. **Premature movement rediscovery**: Grace period (180 frames ≈ 3s) expired long before load (~24s). Fixed: defer rediscovery until menu is fully hidden.

3. **Gravity during load**: `_hasBeenGrounded` still true from before load. Fixed: reset on save/load trigger.

4. **Same-scene cullingMask gate**: After same-scene reload, game camera cullingMask stays `0`. Fixed: bypass cullingMask check when `_playerCC == null` AND menu is gone.

### ActionPanelCanvas grip-drag deadlock

When ActionPanelCanvas was grip-dragged, it stored a self-referential zero offset. On reopen, restore code waited for `_positionedCanvases.Contains(_actionPanelId)` but ActionPanelCanvas was removed — infinite deferral. Fixed: excluded from grip-drag candidates; skip anchor-offset restore when `id == _actionPanelId`.

---

## Phase 12: Case Board Polish — COMPLETE ✓

- **Pin drag**: direct `RectTransform` manipulation (EventSystem can't route native drag events to CaseCanvas)
- **Context menu world-lock**: skip `RepositionEveryFrame` when `ContextMenus` has active `ContextMenu*` children

---

## Phase 11: Movement & Controls — COMPLETE ✓

| Button | Action | Method |
|--------|--------|--------|
| Left stick | Locomotion (4 m/s) | `UpdateLocomotion()` |
| Left stick click | Sprint toggle | `UpdateSprint()` |
| Right stick X | Snap turn ±30° | `UpdateSnapTurn()` |
| Right stick click | Flashlight | middle mouse button |
| Left Y / menu | ESC | `UpdateMenuButton()` — 1.5s realtime cooldown |
| Left X | Crouch (C key) | `UpdateCrouch()` |
| Left trigger | World interact | LMB + camera redirected to left controller aim |
| Left grip | Inventory (X key) | `UpdateInventory()` |
| Right trigger | UI click | `TryClickCanvas()` |
| Right A | Jump / right-click on canvas | `UpdateJump()` / `UpdateUIInput()` |
| Right B | Notebook/Tab / middle-click drag on canvas | `UpdateNotebook()` / `UpdateUIInput()` |

**Held item tracking** — two systems:
1. Carried world objects (`InteractionController.carryingObject`) — transform overridden to VR controller + 0.3m forward every frame
2. First-person arms (`LagPivot` → `FirstPersonModels` → `Arms`) — reparented to VROrigin, scaled by 0.0002, fist-offset aligned to controllers

---

## Current UI Canvas State

| Canvas | Size | Status |
|--------|------|--------|
| `MenuCanvas` | 1.20m | Working ✓ |
| `WindowCanvas` (notes/notebook) | 1.20m | Working ✓ (aim dot misalignment when opened from pin board — parked; individual nested canvases grip-draggable) |
| `ActionPanelCanvas` | 1.60m | Working ✓ (case board anchor, not grip-draggable) |
| `DialogCanvas` | 1.20m | Working ✓ |
| `BioDisplayCanvas` | 1.80m | Working ✓ (grip-draggable, position saved) |
| `GameCanvas`/HUD | 1.50m | Working ✓ |
| `TooltipCanvas` | 0.80m | Working ✓ (context menu aim alignment partial — parked) |
| `PopupMessage` | 1.20m | Working ✓ (scale enforced each cycle) |
| `VRSettingsPanelInternal` | 1.60m | Working ✓ |
| `CaseCanvas` | 1.80m | Working ✓ (BG suppressed, pin board interactive; pin steal issue parked) |
| `LocationDetailsCanvas` | 1.80m | Working ✓ (grip-draggable, position saved) |
| `MinimapCanvas` | 1.50m | Working ✓ (grip-draggable, hold-B to show, pan, location click, context menu) |

---

## Known Issues / Polish Opportunities (Phase 20+)

**Parked (detailed in NotesWork.md):**
- Context menu aim dot / visual misalignment (game writes screen coords + Z-scale=0 every frame)
- Opened pinned notes (WindowCanvas) aim dot misalignment
- Pin proximity stealing with 2+ pins on board

**Other:**
- Some additive items — semi-transparent white overlays, not original colours
- PopupMessage — game resets scale frequently (fixed each cycle, slight visual lag)
- Comfort options not yet: vignette, configurable snap-turn degrees, IPD adjustment
- VR arm rotation may need per-item tuning
- **HUD settings plan** — 5 settings (distance, size, height, H.offset, laggy-follow) + auto-hide — plan written (`C:\Users\blah6\.claude\plans\tender-wibbling-sunbeam.md`), not implemented
- Map grip-drag interaction: after moving map, some interactions may still use old position (needs validation)
- Minimap B-button: while held, map does not smoothly follow player rotation (placed once on press; OK since it's hold-to-show)

---

## Canvas System Architecture

```
Every 90 frames — ScanAndConvertCanvases():
  1. Prune dead entries from _managedCanvases
  2. Find all root Screen-Space canvases not yet managed
     → Skip: MapDuct*, MapButton*, Loading Icon*, Ignored category
     → ConvertCanvasToWorldSpace(canvas):
          - disable CanvasScaler
          - read sizeDelta (reference size)
          - renderMode = WorldSpace
          - scale = TargetWorldWidth / sizeDelta.x
          - ForceUIZTestAlways() — patch all graphics materials
          - Add GraphicRaycaster
  3. Reparent pass: for each root canvas with scaled parent → SetParent(null, true)
     + scale enforcement: re-apply correct scale if drifted
  4. RescanCanvasAlpha (with 600-frame cooldown per canvas)
  5. Nested canvas discovery + ForceUIZTestAlways

Every frame — PositionCanvases():
  → Active-state tracking: false→true transition → remove from _positionedCanvases (recentre)
  → ActionPanelCanvas activate: resets _caseBoardPrimaryId, removes ALL CaseBoard canvases
  → HUD canvases: parented to HUDAnchor (fixed at dist/angle)
  → CaseCanvas: follows ActionPanelCanvas via EnforceCaseCanvasPosition() (0.15m behind)
  → Tooltip w/ ContextMenu(Clone) active: freeze position (_contextMenuFreezePos), zero ContextMenus/child localPosition each frame
  → Grip-dragged canvases: restore ActionPanel-relative offset (NOT ActionPanelCanvas itself)
  → Menu/Panel/CaseBoard/Default: placed at headPos + forward ONCE on first visibility
  → Tooltip (no CM): repositioned every frame
```

---

## ContextMenus Canvas Hierarchy

```
TooltipCanvas (CanvasCategory.Tooltip, RepositionEveryFrame=true)
  └── ContextMenus (nested, CanvasCategory.Ignored, no Canvas component of its own)
      ├── ContextMenu(Clone)     ← actual right-click menu; game sets pos to screen coords,
      │   └── Border → items…     localRot Y≈284°, localScale.z=0 EVERY frame
      └── PinnedQuickMenu(Clone) ← hover tooltip; do NOT treat as context menu
```

Key points:
- `ContextMenus` has NO Canvas component — `GetComponent<Canvas>()` returns null
- When `ContextMenu(Clone)` is active, we freeze TooltipCanvas world position
- We zero `ContextMenus` and first active child `localPosition/Rotation/Scale` in 3 places per frame to fight game's screen-coord reset
- `_prevContextMenuActive` filters only children whose name starts with `"ContextMenu"` (not `"PinnedQuickMenu"`)
- TooltipCanvas is NOT skipped in aim dot scan when `_prevContextMenuActive` is true (it's frozen, plane is valid)

---

## Save/Load Rediscovery Flow (same-scene reload)

```
User clicks "Continue" (or name containing "continue"/"new game"/"new city")
  → TryClickCanvas walks hierarchy to find button name (raycaster hits child GOs)
  → Sets: _sceneLoadGrace=180, _playerCC=null, _playerRb=null,
          _pauseMovementActive=false, _hasBeenGrounded=false, _jumpVerticalVelocity=0
  → _movementDiscoveryDone stays true (prevents immediate re-discovery this frame)

During grace (180 frames ≈ 3s):
  → UpdateLocomotion, UpdateJump return early
  → Canvas scan paused

Grace expires:
  → If _playerCC == null: set _movementDiscoveryDone = false (schedule rediscovery)
  → Do NOT call DiscoverMovementSystem() here — game still loading, CC at origin

Per-frame gate (each Update):
  → if (!_movementDiscoveryDone && _gameCam != null):
      → Normal path: cullingMask != 0 → DiscoverMovementSystem()
      → Save/load path: cullingMask == 0 AND _playerCC == null AND menu gone
          → DiscoverMovementSystem()  ← player now at save position
```

---

## Critical VDXR Discoveries (do not change)

### Type constant offset (+0x5DC0)
| Struct | Spec | VDXR value |
|---|---|---|
| XrGraphicsBindingD3D11KHR | 1000003000 | `unchecked((int)0x3B9B3378)` |
| XrSwapchainImageD3D11KHR | 1000003001 | `unchecked((int)0x3B9B3379)` |
| XrGraphicsRequirementsD3D11KHR | 1000003002 | `unchecked((int)0x3B9B337A)` |

### openxr-oculus-compatibility layer
Must be disabled before `xrCreateInstance` — see `DisableUnityOpenXRLayer()`.

### Swapchain format
`RenderTextureFormat.ARGB32` → format 28 (`DXGI_FORMAT_R8G8B8A8_UNORM`).

### HDRP flipYMode
Use `HDAdditionalCameraData.flipYMode = ForceFlipY` — never combine `GL.invertCulling` with projection Y-negation.

---

## IL2CPP Interop Pitfalls

| Pitfall | Detail |
|---------|--------|
| `GetComponentInParent<Button>()` | Always returns null — walk hierarchy manually |
| `AddListener` on `new ButtonClickedEvent()` | Silently fails — use GO instance ID intercept |
| `btn.GetInstanceID()` | Returns component id — use `btn.gameObject.GetInstanceID()` |
| `Graphic.color` vs `mat.color` | Must set both for transparency in HDRP UI |
| `g.color = ...` per-frame | Calls `SetVertexDirty()` → rebuild lag. Rate-limit. |
| `Resources.FindObjectsOfTypeAll<Canvas>()` | More reliable than `FindObjectsOfType` in IL2CPP |
| `AddComponent<RectTransform>()` on new GO | Returns null — add Image first, then GetComponent |
| `DontDestroyOnLoad` on VRMod GOs | Required — scene transition destroys non-persistent objects |
| `CanvasScaler` + WorldSpace | Disable CanvasScaler BEFORE switching to WorldSpace |
| `Mobile/Particles/Additive` in HDRP WorldSpace | Does not render — replace shader with UI/Default |
| **GraphicRaycaster hits child GOs** | Click detection: raycaster returns innermost child hit — always walk hierarchy to find button/feature name |
| **Same-scene reload: cullingMask=0** | After same-scene save load, game camera cullingMask stays 0 — per-frame discovery gate must bypass cullingMask check when menu is gone |
| **ActionPanelCanvas is grip anchor** | Do not add to grip-drag candidates — self-referential offset causes infinite deferral deadlock on reopen |
| **anchoredPosition vs localPosition** | `InverseTransformPoint` returns pivot-relative = **localPosition** space. Do NOT compare against `anchoredPosition` (anchor-relative, different coordinate system) |
| **ContextMenus has no Canvas component** | `ContextMenus` child of TooltipCanvas does NOT have its own Canvas component — `GetComponent<Canvas>()` returns null. Can only interact via TooltipCanvas. |

---

## FMOD Audio API (confirmed working)

```csharp
FMODUnity.RuntimeManager.GetBus("bus:/").setVolume(vol);             // master, float 0..1
FMODUnity.RuntimeManager.GetVCA("vca:/Soundtrack").setVolume(vol);   // per-channel
// VCA paths: vca:/Soundtrack, vca:/Ambience, vca:/Weather, vca:/Footsteps,
//            vca:/Notifications, vca:/PA System, vca:/Other SFX
```

---

## File Locations

| File | Purpose |
|------|---------|
| `VRMod/SoDVR/OpenXRManager.cs` | OpenXR init, session, swapchain, frame loop, action sets |
| `VRMod/SoDVR/VR/VRCamera.cs` | Stereo render loop, UI canvas management, movement, arm display |
| `VRMod/SoDVR/VR/VRSettingsPanel.cs` | VR Settings panel — 4 tabs, all settings wired |
| `VRMod/SoDVR/Plugin.cs` | BepInEx entry point — do not change |
| `BepInEx/plugins/SoDVR.dll` | Deployed plugin (flat layout — never subdirectory) |
| `BepInEx/LogOutput.log` | Runtime log |
| `VRMod/NotesWork.md` | Parked investigation notes — context menu, pin, WindowCanvas issues |
| `C:\Users\blah6\.claude\plans\tender-wibbling-sunbeam.md` | HUD settings plan (ready to implement) |

---

## Phase Roadmap

- [x] Phase 5: Standard loader → stereo image in headset (`346a6df`)
- [x] Phase 6: Camera positioning, head tracking, UI canvases visible in VR
- [x] Phase 7: Controller pose + trigger click + cursor dot tracking
- [x] Phase 8: VR Settings Panel — 4 tabs, FMOD audio, Settings button intercept (`12172ad`)
- [x] Phase 9: Canvas sizes fixed, UI text/icons visible, crash prevention, notebook sizing
- [x] Phase 10: World graphics — walls/floors visible, lighting correct (`546a4b5`)
- [x] Phase 11: Movement — all controls bound, held items + arm display (`255dafc`)
- [x] Phase 12: Case board pin drag, context menu world-lock (`1ee8e9d`)
- [x] Phase 13: Save-load warp eliminated, ActionPanelCanvas grip-drag deadlock fixed (`d0bd328`)
- [ ] **Phase 14 (in progress)**: A/B button canvas mapping done; HUD settings + auto-hide plan ready; case board interaction issues parked
- [ ] **Phase 15 (next)**: Comfort options (vignette, snap-turn config, IPD) + remaining polish
