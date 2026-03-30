# SoDVR — Technical Handover

**Date**: 2026-03-30 (updated end of Phase 11)
**Phase**: 11 complete (movement + controls + arm display) — next priority: Phase 12 comfort/polish

---

## Phase 11: Movement & Controls — COMPLETE ✓

### What was implemented

**Controller bindings** — full button map wired in OpenXRManager.cs + VRCamera.cs:

| Button | Action | Method |
|--------|--------|--------|
| Left stick | Locomotion (4 m/s) | `UpdateLocomotion()` — `CharacterController.Move()` |
| Left stick click | Sprint toggle | `UpdateSprint()` — auto-stops on stick release |
| Right stick X | Snap turn ±30° | `UpdateSnapTurn()` |
| Right stick click | Flashlight | `UpdateFlashlight()` — middle mouse button |
| Left Y / menu | ESC (pause menu) | `UpdateMenuButton()` — 1.5s real-time cooldown |
| Left X | Crouch (C key) | `UpdateCrouch()` |
| Left trigger | World interact | `UpdateInteract()` — LMB + camera redirected to left controller aim |
| Left grip | Inventory (X key) | `UpdateInventory()` |
| Right trigger | UI click | `TryClickCanvas()` — existing from Phase 7 |
| Right A | Jump | `UpdateJump()` — CharacterController vertical velocity + gravity |
| Right B | Notebook/map (Tab) | `UpdateNotebook()` |

**Held item tracking** — two systems:
1. Carried world objects (`InteractionController.carryingObject`) — transform overridden to VR controller position + 0.3m forward every frame
2. First-person arms (`LagPivot` → `FirstPersonModels` → `Arms`) — reparented to VROrigin, scaled, positioned at controllers

**VR arm display**:
- `LagPivot` reparented to VROrigin
- `FirstPersonModels` scaled by `ArmScale` (0.0002) for pixel→meter conversion
- `Arms` forced active, intermediate transforms zeroed every frame
- Per-hand rotation offsets: Right `Euler(90,90,0)`, Left `Euler(90,-90,0)`
- Fist-offset: arm positioned so fist child (not elbow) aligns with controller
- `ArmForwardOffset` (-0.25m) slides arm along controller forward for hand alignment
- Applied in both `UpdateHeldItemTracking` and `ForceItemPositionPreRender`

**VR Settings Panel additions** (VRSettingsPanel.cs):
- Left Laser beam toggle
- Item Hand selection (Left/Right)

---

## What Was Fixed This Session (Phase 11)

### Arm rotation alignment — FIXED ✓
Arms initially pointed wrong direction. Per-hand rotation offsets solved it:
- Right arm: `Quaternion.Euler(90, 90, 0)` — confirmed good
- Left arm: `Quaternion.Euler(90, -90, 0)` — mirrored yaw

### Arm size tuning — FIXED ✓
`ArmScale` reduced from 0.0004 → 0.0002 for correct hand proportions.

### Arm position alignment — FIXED ✓
- Fist-offset positioning ensures game hand (not elbow) aligns with controller
- `ArmForwardOffset = -0.25m` slides arm back along controller forward axis

### Floating during pause menu — FIXED ✓
Jump/gravity uses `Time.unscaledDeltaTime` so gravity works even when `timeScale=0`.
Player no longer floats in the air while ESC menu is open.

---

## Previous Phases Summary

### Phase 10: World Graphics — COMPLETE ✓
- `HDAdditionalCameraData.flipYMode = ForceFlipY` — HDRP handles Y-flip + culling atomically
- VR eye cameras copy game camera's HDRP settings (especially `volumeLayerMask`)
- Walls, floors, lighting all correct

### Phase 9: Canvas/UI — COMPLETE ✓
- All game canvases converted to WorldSpace, placed in front of head
- CanvasScaler disabled before WorldSpace conversion
- HDR material colour boost for text/icons visibility
- Additive shader replacement (Mobile/Particles → UI/Default)
- Canvas scale drift enforcement every scan cycle
- Material cache cap + rescan rate-limit for crash prevention

### Phase 8: VR Settings Panel — COMPLETE ✓
- 4-tab panel (Audio, Display, Controls, Movement)
- FMOD audio controls, all settings wired
- Settings button intercept from main menu

### Phase 7: Controller Input — COMPLETE ✓
- Controller pose tracking, trigger click, cursor dot

### Phase 6: Camera & UI — COMPLETE ✓
- Stereo rendering, head tracking, UI canvases visible

### Phase 5: OpenXR Init — COMPLETE ✓
- Standard OpenXR loader, stereo image in headset

---

## Current UI Canvas State

| Canvas | Size | Status |
|--------|------|--------|
| MenuCanvas | 1.20m | Working ✓ |
| WindowCanvas (notes/notebook) | 1.20m | Working ✓ |
| ActionPanelCanvas | 1.60m | Working ✓ |
| DialogCanvas | 1.20m | Working ✓ |
| BioDisplayCanvas | 1.80m | Working ✓ |
| GameCanvas/HUD | 1.50m | Working ✓ |
| TooltipCanvas | 0.80m | Working ✓ |
| PopupMessage | 1.20m | Working ✓ (scale enforced each cycle) |
| VRSettingsPanelInternal | 1.60m | Working ✓ |
| CaseCanvas | — | Disabled (bright white background) |
| MinimapCanvas | 1.50m | Partially working |

---

## Known Issues / Polish Opportunities (Phase 12+)

- **CaseCanvas disabled** — was bright white background; actual case-board content lives elsewhere
- **MinimapCanvas** — partially working, needs review
- **Some additive items** — show as semi-transparent white overlays, not original colours
- **PopupMessage** — game resets scale frequently (fixed each cycle, slight visual lag)
- **Comfort options** not implemented: vignette, configurable snap-turn degrees, IPD adjustment
- **VR arm rotation** may need per-item tuning (different held items may have different orientations)
- **Left controller full tracking** — left laser beam works, but dual-hand physics interactions not implemented
- **Trigger stopping issue** — user reported previously, not yet diagnosed

---

## Canvas System Architecture

```
Every 90 frames — ScanAndConvertCanvases():
  1. Prune dead entries from _managedCanvases
  2. Find all root Screen-Space canvases not yet managed
     → Skip: CaseCanvas, MapDuct*, MapButton*, Loading Icon*, Ignored category
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
  → HUD canvases: parented to HUDAnchor (fixed at dist/angle)
  → Menu/Panel/CaseBoard/Default: placed in front of head ONCE on first visibility
  → Tooltip: repositioned every frame
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
- [x] **Phase 11**: Movement — all controls bound, held items + arm display (`255dafc`)
- [ ] **Phase 12 (next)**: Comfort options (vignette, snap-turn config, IPD) + polish
