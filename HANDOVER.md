# SoDVR — Technical Handover

**Date**: 2026-03-30 (updated end of Phase 13)
**Phase**: 13 complete — next priority: Phase 14 comfort/polish

---

## Phase 13: Save-Load Warp Fix & Case Board Polish — COMPLETE ✓

### Save/load warp — root cause chain

The warp-on-load took multiple attempts to diagnose. Full root cause:

1. **Button detection miss**: `TryClickCanvas` hit `'Border'` (child element), not `'Continue'` (the button). Save/load trigger check was comparing `go.name == "continue"` against `"Border"` — always false. Fixed: walk up 6 hierarchy levels to find the button name.

2. **Premature movement rediscovery**: Grace period (180 frames ≈ 3s) expired long before the game finished loading (~24s). `DiscoverMovementSystem()` ran while player CC was still at origin `(0,1,0)`, setting `_pauseOriginPos = (0,1,0)`. Fixed: defer rediscovery until menu is fully hidden (= load complete, player at save position).

3. **Gravity during load**: `_hasBeenGrounded` was still `true` from before the load. After grace expired and CC was at origin, gravity pulled the CC down. Fixed: reset `_hasBeenGrounded = false` and `_jumpVerticalVelocity = 0` at the save/load trigger.

4. **Same-scene cullingMask gate**: After same-scene reload, game camera cullingMask stays `0` (we suppressed it). Normal per-frame discovery gate (`cullingMask != 0`) would block rediscovery forever. Fixed: bypass cullingMask check when `_playerCC == null` AND menu is gone.

### ActionPanelCanvas grip-drag deadlock

When the user grip-dragged `ActionPanelCanvas` (the case board anchor), it stored a self-referential zero offset in `_gripDragAnchorOffsets`. On reopen:
- Restore code waited for `_positionedCanvases.Contains(_actionPanelId)`
- ActionPanelCanvas was just removed from `_positionedCanvases` for recentre
- Infinite deferral — ActionPanelCanvas never moved from its old world position

Fixed:
1. Excluded `ActionPanelCanvas` from grip-draggable canvas candidates
2. Skip anchor-offset restore when `id == _actionPanelId` (let it fall through to normal head+forward placement)

---

## Phase 12: Case Board Polish — COMPLETE ✓

- **Pin drag**: case board pins/notes/evidence now draggable via direct `RectTransform` manipulation (EventSystem couldn't route native drag events to CaseCanvas)
- **Context menu world-lock**: skip `RepositionEveryFrame` when `ContextMenus` has active children — prevents context menus from chasing the player mid-interaction

---

## Phase 11: Movement & Controls — COMPLETE ✓

**Full controller button map:**

| Button | Action | Method |
|--------|--------|--------|
| Left stick | Locomotion (4 m/s) | `UpdateLocomotion()` — `CharacterController.Move()` |
| Left stick click | Sprint toggle | `UpdateSprint()` |
| Right stick X | Snap turn ±30° | `UpdateSnapTurn()` |
| Right stick click | Flashlight | middle mouse button |
| Left Y / menu | ESC | `UpdateMenuButton()` — 1.5s realtime cooldown |
| Left X | Crouch (C key) | `UpdateCrouch()` |
| Left trigger | World interact | LMB + camera redirected to left controller aim |
| Left grip | Inventory (X key) | `UpdateInventory()` |
| Right trigger | UI click | `TryClickCanvas()` |
| Right A | Jump | `UpdateJump()` — CharacterController vertical + gravity |
| Right B | Notebook/map (Tab) | `UpdateNotebook()` |

**Held item tracking** — two systems:
1. Carried world objects (`InteractionController.carryingObject`) — transform overridden to VR controller + 0.3m forward every frame
2. First-person arms (`LagPivot` → `FirstPersonModels` → `Arms`) — reparented to VROrigin, scaled by 0.0002, fist-offset aligned to controllers

---

## Current UI Canvas State

| Canvas | Size | Status |
|--------|------|--------|
| `MenuCanvas` | 1.20m | Working ✓ |
| `WindowCanvas` (notes/notebook) | 1.20m | Working ✓ |
| `ActionPanelCanvas` | 1.60m | Working ✓ (case board anchor, not grip-draggable) |
| `DialogCanvas` | 1.20m | Working ✓ |
| `BioDisplayCanvas` | 1.80m | Working ✓ |
| `GameCanvas`/HUD | 1.50m | Working ✓ |
| `TooltipCanvas` | 0.80m | Working ✓ |
| `PopupMessage` | 1.20m | Working ✓ (scale enforced each cycle) |
| `VRSettingsPanelInternal` | 1.60m | Working ✓ |
| `CaseCanvas` | 1.80m | Working ✓ (BG suppressed, pin board interactive) |
| `LocationDetailsCanvas` | 1.80m | Working ✓ (grip-draggable) |
| `MinimapCanvas` | 1.50m | Partially working |

---

## Known Issues / Polish Opportunities (Phase 14+)

- **MinimapCanvas** — partially working, needs review
- **Some additive items** — show as semi-transparent white overlays, not original colours
- **PopupMessage** — game resets scale frequently (fixed each cycle, slight visual lag)
- **Comfort options** not yet: vignette, configurable snap-turn degrees, IPD adjustment
- **VR arm rotation** may need per-item tuning (different held items may have different orientations)
- **Jump while stationary** — reported not working in some states (not yet diagnosed)
- **Notebook B-press** — reportedly opens and instantly closes (not yet diagnosed)

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
  → Grip-dragged canvases: restore ActionPanel-relative offset (NOT ActionPanelCanvas itself)
  → Menu/Panel/CaseBoard/Default: placed at headPos + forward ONCE on first visibility
  → Tooltip: repositioned every frame
```

---

## Save/Load Rediscovery Flow (same-scene reload)

```
User clicks "Continue" (or any name containing "continue"/"new game"/"new city")
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
| **Same-scene reload: cullingMask=0** | After same-scene save load, game camera cullingMask stays 0 (we suppressed it) — per-frame discovery gate must bypass cullingMask check when menu is gone |
| **ActionPanelCanvas is grip anchor** | Do not add to grip-drag candidates — self-referential offset causes infinite deferral deadlock on reopen |

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
- [x] **Phase 13**: Save-load warp eliminated, ActionPanelCanvas grip-drag deadlock fixed (`d0bd328`)
- [ ] **Phase 14 (next)**: Comfort options (vignette, snap-turn config, IPD) + remaining polish
