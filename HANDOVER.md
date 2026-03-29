# SoDVR — Technical Handover

**Date**: 2026-03-29 (updated end of session)
**Phase**: 9 complete (UI canvas polish) — next priority: world graphics

---

## What Was Fixed This Session

### CanvasScaler inflation — FIXED ✓
`ConvertCanvasToWorldSpace` now disables CanvasScaler before switching to WorldSpace.
`sizeDelta` stays at reference resolution (e.g. 1920×1080) instead of ballooning to 2720×1680.
Canvases are correctly sized at their TargetWorldWidth values.

### HDRP exposure workaround — WORKING ✓
No exposure isolation possible in IL2CPP HDRP 12. Workaround: HDR material colour boost.
- **Text** (`isText=true`): `StrengthenMenuTextMaterial` sets `_FaceColor`/`_Color` to `(32,32,32,1)`. Now applied to ALL canvases (was previously only Menu/Panel/CaseBoard).
- **Non-text non-bg graphics**: material color × 4 (`Mathf.Max(c.r, 0.01f) * 4f`). Covers panels, borders, images.
- **Additive items** (`Mobile/Particles/Additive` shader): shader replaced with `UI/Default`, `mat.color=(1,1,1,0.85)`. Vertex color drives appearance.

### Mobile/Particles/Additive shader — FIXED ✓
Legacy mobile shader does not render in HDRP WorldSpace pipeline. All items classified
as `isAdditive` (name contains "Additive", "Particle", or "Add") now get shader swapped
to `UI/Default` at material creation time. Icons, borders, glows now visible.

### Canvas scale drift — FIXED ✓
The game resets canvas localScale when opening UI panels (WindowCanvas → scale 1.0 when
opening notebook). The reparent/scale-enforce pass now re-applies correct scale every scan
cycle for ALL root managed canvases. Log line: `ScaleFix 'WindowCanvas': 1.0 → 0.000625`.

### Notebook/notes size — FIXED ✓
Root cause was scale drift (above). After fix: Note worldSize=(0.42,0.54)m, Notebook=(0.76,0.66)m.

### CaseCanvas — DISABLED ✓
CaseCanvas had only 4 items (Viewport, ContentContainer, BG, ControllerSelection) and was
rendering as a bright white background washing out windows in front of it. `canvas.enabled=false`
applied at scan time. Actual case-board content lives elsewhere.

### Crash prevention — IMPLEMENTED ✓
- **RescanCanvasAlpha rate limit**: 600-frame cooldown per canvas (preventing MinimapCanvas
  from creating 1367 new materials every 90-frame scan cycle)
- **Material cache cap**: `s_uiZTestMats` capped at 2000 entries. Warning logged when hit.
  Both caches clear on `BuildCameraRig` (scene reload).

---

## Current UI Canvas State (2026-03-29)

| Canvas | Size | Status |
|--------|------|--------|
| MenuCanvas | 1.20m | Working — text visible ✓ |
| WindowCanvas (notes/notebook) | 1.20m | Working — correct size after scale fix ✓ |
| ActionPanelCanvas | 1.60m | Working — icons/text visible ✓ |
| DialogCanvas | 1.20m | Working ✓ |
| BioDisplayCanvas | 1.80m | Working ✓ |
| GameCanvas/HUD | 1.50m | Working ✓ |
| TooltipCanvas | 0.80m | Working ✓ |
| PopupMessage | 1.20m | Working ✓ (scale fixed every cycle — game resets it) |
| VRSettingsPanelInternal | 1.60m | Working — text visible ✓ |
| CaseCanvas | — | **Disabled** — was bright white background |
| MinimapCanvas | 1.50m | Partially — content present but needs review |

### Known remaining UI issues (deferred)
- Some panels may still have minor visibility issues (brightness tuning)
- Additive items now show as semi-transparent white overlays — not full original colours
- PopupMessage gets scale-reset by game frequently (fixed each cycle, slight lag)

---

## Next Priority: World Graphics

The user has flagged world graphics as the next issue to fix. Details to be determined
at the start of the next session. Likely candidates:
- In-world 3D objects affected by VR rendering (exposure, culling, layering)
- Floating world-space UI (interaction prompts, overhead labels) broken or invisible
- Main camera suppression (`cullingMask=0`) side-effects on game world rendering

---

## Canvas System Architecture (current)

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
     + scale enforcement: re-apply correct scale if drifted (catches game resets)
     + skips canvases with sizeDelta.x < 1 (zero-size canvases)
  4. RescanCanvasAlpha (with 600-frame cooldown per canvas):
     → RelaxMenuCanvasClipping (Menu/Panel/CaseBoard only)
     → RelaxMenuTextMaterials (Menu/Panel/CaseBoard only — StrengthenMenuTextMaterial)
     → ForceUIZTestAlways on new/changed graphics
  5. Nested canvas discovery:
     → GetComponentsInChildren<Canvas> on each root
     → Skip (Clone) canvases, Loading Icon
     → Disable nested CanvasScaler
     → ForceUIZTestAlways on nested canvas
     → Add to _managedCanvases + _nestedCanvasIds

Every frame — PositionCanvases():
  → HUD canvases: parented to HUDAnchor (fixed at dist/angle)
  → Menu/Panel/CaseBoard/Default: placed in front of head ONCE on first visibility
  → Tooltip: repositioned every frame
  → Nested canvases: skipped (follow parent)
  → Scale enforcement applies each scan, not each frame
```

### ForceUIZTestAlways material classification:
```
isAdditive = shader name contains "Additive", "Particle", or "Add"
isBg       = GO name contains "background" or equals "BG"
isText     = IsTextGraphic(g) — TMP_Text or Text component

Material created once per (origMaterialID, boostType) pair:
  isAdditive → shader = UI/Default, renderQueue=3001, color=(1,1,1,0.85)
  isBg       → renderQueue=3000, color.a = UIBackgroundAlpha (0.07)
  isText     → renderQueue=3009, StrengthenMenuTextMaterial(_FaceColor/_Color=32)
  other      → renderQueue=3008, color *= 4 (min 0.01 per channel)
```

---

## Critical VDXR Discoveries (do not change)

### Type constant offset (+0x5DC0)
| Struct | Spec | VDXR value |
|---|---|---|
| XrGraphicsBindingD3D11KHR | 1000003000 | `unchecked((int)0x3B9B3378)` |
| XrSwapchainImageD3D11KHR | 1000003001 | `unchecked((int)0x3B9B3379)` |
| XrGraphicsRequirementsD3D11KHR | 1000003002 | `unchecked((int)0x3B9B337A)` |

Base types (SESSION_CREATE_INFO=8 etc.) use spec values unchanged.

### openxr-oculus-compatibility layer
Must be disabled before `xrCreateInstance` — see `DisableUnityOpenXRLayer()` in `OpenXRManager.cs`.

### Swapchain format
`RenderTextureFormat.ARGB32` → format 28 (`DXGI_FORMAT_R8G8B8A8_UNORM`). `_preferredFormats = {28,29,87,91}`.

---

## IL2CPP Interop Pitfalls

| Pitfall | Detail |
|---------|--------|
| `GetComponentInParent<Button>()` | Always returns null in IL2CPP — walk transform hierarchy manually |
| `AddListener` on `new Button.ButtonClickedEvent()` | Silently fails — use `TryClickCanvas` GO instance ID intercept instead |
| `btn.GetInstanceID()` | Returns COMPONENT id — use `btn.gameObject.GetInstanceID()` for GO comparisons |
| `btn.onClick = new Button.ButtonClickedEvent()` | Kills persistent prefab-baked listeners — use to suppress game button default behaviour |
| `Graphic.color` vs `mat.color` | Must set both for transparency in HDRP UI |
| `g.color = ...` per-frame | Calls `SetVertexDirty()` → canvas mesh rebuild lag. Rate-limit. |
| `Resources.FindObjectsOfTypeAll<Canvas>()` | More reliable than `FindObjectsOfType` in IL2CPP |
| `TMP_Text.color` vs `Graphic.color` | TMP overrides base color — must cast to TMP_Text and set `.color` |
| `FrameSettingsField.ExposureControl` | Cannot be overridden per-camera in HDRP 12 IL2CPP |
| `AddComponent<RectTransform>()` on new GO | Returns null — use `AddComponent<Image>()` first, then `GetComponent<RectTransform>()` |
| `DontDestroyOnLoad` on VRMod GOs | Required — scene transition destroys all non-persistent objects |
| `canvas.enabled` per-frame toggle | Causes material instance flood → crash. Always state-track changes. |
| `AudioListener.volume` | Zero effect on FMOD — use `FMODUnity.RuntimeManager.GetBus("bus:/").setVolume()` |
| `CanvasScaler` + WorldSpace | Disable CanvasScaler BEFORE switching to WorldSpace — already implemented ✓ |
| `Mobile/Particles/Additive` in HDRP WorldSpace | Does not render — replace shader with UI/Default ✓ |
| Game resets canvas localScale | Reparented canvases have scale re-enforced every scan cycle ✓ |

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
| `VRMod/SoDVR/VR/VRCamera.cs` | Stereo render loop, UI canvas management, controller click |
| `VRMod/SoDVR/VR/VRSettingsPanel.cs` | VR Settings panel — 4 tabs, all settings wired |
| `VRMod/SoDVR/Plugin.cs` | BepInEx entry point — do not change |
| `VRMod/FAILURE-CaseBoard.md` | Full log of failed approaches for case board / UI polish |
| `BepInEx/plugins/SoDVR.dll` | Deployed plugin (flat layout — never subdirectory) |
| `BepInEx/LogOutput.log` | Runtime log |

---

## Phase Roadmap

- [x] Phase 5: Standard loader → stereo image in headset (`346a6df`)
- [x] Phase 6: Camera positioning, head tracking, UI canvases visible in VR
- [x] Phase 7: Controller pose + trigger click + cursor dot tracking
- [x] Phase 8: VR Settings Panel — 4 tabs, FMOD audio, Settings button intercept (`12172ad`)
- [x] **Phase 9**: Canvas sizes fixed, UI text/icons visible, crash prevention, notebook sizing
- [ ] **Phase 10 (current)**: World graphics — TBD
- [ ] Phase 11: Movement — thumbstick locomotion, snap turn, jump, interact bindings
- [ ] Phase 12: Comfort options (vignette, snap-turn degrees, IPD)
- [ ] Phase 13: Left controller full tracking + dual-hand interactions
