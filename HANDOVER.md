# SoDVR — Technical Handover

**Date**: 2026-03-27
**Phase**: 8 — VR Settings Panel (Phase 0 confirmed, Phase 1 next)

---

## What Works (Fully Operational)

### Phase 5 — Stereo rendering (git `346a6df`)
- Full OpenXR pipeline: xrCreateInstance → xrGetSystem → gfxReqs → xrCreateSession → xrBeginSession → xrCreateSwapchain ×2 → xrEnumerateSwapchainImages — all rc=0
- Swapchain: 2554×2756, DXGI_FORMAT_R8G8B8A8_UNORM (format 28), 3 images per eye
- `D3D11CopyResource` delivers frames to headset each Unity frame
- Head tracking (`xrLocateViews`) drives left/right eye poses ✓
- VROrigin follows game camera world position ✓
- HDRP eye cameras: AA=None, custom FrameSettings (SSAO/SSR/Volumetrics/MotionBlur/Tonemapping off) ✓

### Phase 6 — UI canvases in VR (COMPLETE)
- All screen-space canvases auto-converted to WorldSpace every 30 frames ✓
- Each canvas placed once in front of head on first valid tracked pose ✓
- **Home key** re-centres all canvases ✓
- FadeOverlay/Fade suppressed ✓
- TMP font shader swapped from Distance Field → UI/Default with correct TSA ✓
- Canvases visible in headset (text too dark — see §UI Brightness; workaround: ZTest Always)

### Phase 7 — Controller input (COMPLETE)
- Right controller pose tracked via OpenXR action sets (aim pose, trigger) ✓
- Controller visible as `RightController` GameObject under VROrigin ✓
- Trigger fires `ExecuteEvents` pointer-click on closest WorldSpace canvas plane hit ✓
- **Cursor dot** (`VRCursorCanvasInternal`) visible on all screens ✓
  - ScreenSpaceOverlay canvas → converted via normal pipeline → HDRP-registered + ZTest Always
  - `DontDestroyOnLoad` — survives loading→menu scene transition
  - NOT in `_ownedCanvasIds` → `RescanCanvasAlpha` applies ZTest Always material patch (bypasses exposure darkening)
  - Dot moves via `anchoredPosition` (2D projection onto canvas plane at `UIDistance`)
  - `_cursorCanvas` cached in `BuildCameraRig`; `_cursorRect` via `AddComponent<Image>()` + `GetComponent<RectTransform>()`

### Phase 8 — VR Settings Panel (IN PROGRESS)
- Phase 0 test canvas (`VRSettingsPanelInternal`, F10 to toggle) confirmed visible in headset ✓
- Panel created as ScreenSpaceOverlay → converted by scan pipeline, registered in `_ownedCanvasIds`
- `DontDestroyOnLoad` on panel GO ✓
- **Next**: Phase 1 — extract panel into `SoDVR/VR/VRSettingsPanel.cs` per PLAN-Claude.md

---

## Critical VDXR Discoveries (do not change these)

### Type constant offset (+0x5DC0)
| Struct | Spec | **VDXR value** |
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

## BLOCKING ISSUE: UI Brightness

### Confirmed root cause (2026-03-27)
WorldSpace UI rendered by the HDRP overlay cameras is darkened by HDRP's scene auto-exposure. The scene cameras compute EV≈8–12 for the city environment (multiplier ≈ 1/256 to 1/4096). The UI overlay cameras inherit this exposure rather than computing their own, so unlit white vertex colours appear near-black in the headset.

### What was tried and CONFIRMED NOT WORKING
| Approach | Result |
|----------|--------|
| `FrameSettingsField.ExposureControl = false` per camera | Does not persist — `ExposureOff` always reads back `False`. Not overridable in HDRP 12 per-camera FS. |
| `FrameSettingsField.Tonemapping = false` | **Works** — `TonemapOff=True` confirmed on all cameras. |
| VRExposureOverride Volume (EV=0 Fixed, Tonemapping=None, layer 31, priority 1000, `volumeLayerMask=1<<31` on UI cameras) | Volume is **not applied** — confirmed: changing `fixedExposure` to `-10` (1024× boost) produced zero visible change. |
| `HDAdditionalCameraData.clearColorMode = None` | Added; uncertain if effective for compositing. |

### State of current code (VRCamera.cs)
- Scene cameras: `cullingMask = ~(1<<UILayer)`, `ExposureOff=False` (unavoidable), `TonemapOff=True`
- UI overlay cameras: `cullingMask = 1<<UILayer`, `clearColorMode=None`, `volumeLayerMask=1<<31`, `TonemapOff=True`, `ExposureOff=False` (unavoidable)
- VRExposureOverride volume: still present but ineffective (currently has `fixedExposure=-10` from last diagnostic test — should be reverted to `0` if the approach is abandoned)
- TMP text: vertex colour = white, shader = UI/Default, TSA=(1,1,1,0) — all data confirmed correct

### Recommended fix approaches (next session)

**Option A — `FrameSettingsField.Postprocess` master toggle (try first, 5 min)**
Add `FrameSettingsField.Postprocess` to `s_VrDisabledFields` alongside Tonemapping, ExposureControl, etc.
If this field persists (unlike ExposureControl), it disables ALL post-processing including exposure as a side-effect.
Check log for `ExposureOff=True` after adding it.

```csharp
// In s_VrDisabledFields array, add:
FrameSettingsField.Postprocess,   // may disable entire post-process stack incl. exposure
```
Also add to the UI camera disabled-fields array inside `CreateUIOverlayCam`.

**Option B — Overwrite HDRP's exposure texture via CommandBuffer (medium complexity)**
HDRP stores the current frame's exposure as a 1×1 RFloat texture in the `HDCamera` history.
A CommandBuffer can overwrite this to neutral (1.0 linear = log2 value 0) before the post-process pass.

```csharp
// In CreateUIOverlayCam, after creating HDAdditionalCameraData:
var cb = new CommandBuffer { name = "ForceNeutralExposure" };
// Access HDRP's exposure texture:
//   var hdCam = HDCamera.GetOrCreate(cam);
//   var expRT = hdCam.GetCurrentFrameRT((int)HDCameraFrameHistoryType.Exposure);
// Blit a 1×1 white texture (= linear 1.0 = neutral exposure) into expRT:
//   cb.Blit(Texture2D.whiteTexture, expRT);
cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, cb);
```
The exact HDCamera API path needs investigation in the HDRP 12 IL2CPP interop DLLs.

**Option C — Remove UI overlay cameras; add layer 5 to scene cameras; rely on TonemapOff**
Remove `_leftUICam`/`_rightUICam`. Add `(1 << UILayer)` back into scene camera `cullingMask`.
Since `TonemapOff=True` already works on scene cameras, test whether removing tonemapping alone
is sufficient to get readable UI (tonemapping was compressing bright values; exposure was also
darkening, but maybe the combined effect was tolerable or exposure is not as extreme as estimated).
This simplifies the architecture and may work if scene EV is moderate.

**Option D — Read live exposure and boost vertex colours to compensate**
In `Update()`, use `HDCamera.GetOrCreate(_leftCam)` to get the current exposure value.
Compute `boost = 1 / currentExposure`. Each scan cycle apply `tmp.color = Color.white * boost`
for all text elements (clamped, but if boost > 1 the LDR clamp gives white).
Rate-limit to once per scan (every 30 frames) to avoid rebuild lag.

---

## Options Panel Text Invisible (secondary issue)

### Root cause
The options/settings panel is a child hierarchy within MenuCanvas that is initially inactive.
When `ForceUIZTestAlways` runs at canvas scan time, inactive TMP elements get `g.material = VRPatch_clone`.
When the panel opens (activates), TMP rebuilds the text and may call `CanvasRenderer.SetMaterial`
with a font-asset material whose shader has NOT been swapped to UI/Default yet (different font from
the main menu buttons).

`RescanCanvasAlpha` (runs every 30 frames) will catch this on the next cycle and swap the shader.
The text should appear within ~0.5 s of the panel opening. If it never appears, the issue is
that TMP is overriding with a material variation instance that is not being tracked.

### Diagnostic
On End key press with options panel open, check the dump for options text elements:
- `crShader=null` → TMP hasn't rendered yet; wait one more rescan cycle
- `crShader=TextMeshPro/Distance Field` → swap hasn't fired yet for this instance
- `crShader=UI/Default` → shader is correct; problem is purely exposure

---

## IL2CPP Interop Pitfalls

| Pitfall | Detail |
|---------|--------|
| `GetComponentInParent<Button>()` | Always returns null in IL2CPP — never use for Button detection |
| `Graphic.color` vs `mat.color` | Separate in HDRP UI. `mat.color.a` alone does not make UI transparent. Must set `g.color.a`. |
| `g.color = ...` per-frame | Calls `SetVertexDirty()` → canvas mesh rebuild → serious lag at 60 Hz. Rate-limit or avoid. |
| `renderQueue` in HDRP Transparent | HDRP ignores fine-grained queue values within Transparent range; always sorts by spherical distance. |
| `FindObjectsOfType<Canvas>()` | Less reliable in IL2CPP. Use `Resources.FindObjectsOfTypeAll<Canvas>()`. |
| `TMP_Text.color` vs `Graphic.color` | TMP overrides `Graphic.color` with `new color` — the base setter is ignored. Must cast to `TMP_Text` and set `.color` directly. |
| `FrameSettingsField.ExposureControl` | Cannot be overridden per-camera in HDRP 12 IL2CPP. Bit never persists. |
| `AddComponent<RectTransform>()` on new GO | Returns **null** in IL2CPP — the GO already has a Transform. Instead: `AddComponent<Image>()` first, then `GetComponent<RectTransform>()`. |
| `DontDestroyOnLoad` on VRMod GOs | **Required** for any VRMod-created canvas or panel. The loading→menu scene transition destroys all non-persistent objects, invalidating cached Canvas/RectTransform references silently. |
| Duplicate `plugins/SoDVR.dll` | BepInEx loads two copies if both `plugins/SoDVR.dll` and `plugins/SoDVR/SoDVR.dll` exist. Use `powershell.exe -Command "Remove-Item -Recurse -Force 'path\SoDVR'"` to clean up; `rm -rf` in bash may silently fail on Windows. |

---

## Phase 8 — VR Settings Panel (CURRENT WORK)

See `PLAN-Claude.md` for the complete phase plan (Phases 0–4).

### Status
- **Phase 0** (visibility proof): `VRSettingsPanelInternal` test canvas confirmed visible ✓
- **Phase 1** (skeleton panel): **TODO** — extract into `SoDVR/VR/VRSettingsPanel.cs`

### Phase 1 deliverables (per PLAN-Claude.md)
- New file `SoDVR/VR/VRSettingsPanel.cs` — owns canvas, layout, settings logic
- Dark semi-transparent background, title "VR Settings", Close button
- Two tab buttons: **Graphics** | **General**
- 3–4 hardcoded rows per tab as layout proof (no scroll view yet)
- F10 toggles visibility
- `VRCamera` additions: ≤30 lines — call `VRSettingsPanel.Init()`, add canvas ID to `_ownedCanvasIds`, wire F10

### Canvas creation rules (learned from Phase 0 + cursor dot work)
- Create as `ScreenSpaceOverlay` → let `ScanAndConvertCanvases` convert → HDRP-registered automatically
- `DontDestroyOnLoad` on the root GO — **mandatory**
- Register canvas instance ID in `_ownedCanvasIds` immediately after `AddComponent<Canvas>()` — before scan runs
- `_ownedCanvasIds` gates `RescanCanvasAlpha` only; `PositionCanvases` still places owned canvases normally (placed once via `_positionedCanvases`, re-placed on F10 by removing from `_positionedCanvases`)
- Add `CanvasScaler` with `referenceResolution = (900, 700)` so `ConvertCanvasToWorldSpace` uses the right size
- Assert `GraphicRaycaster` exists after conversion (added by `ConvertCanvasToWorldSpace`, but confirm)

### Setting families (for Phase 2 wiring)
See `PLAN-Claude.md §Setting families and write paths` for complete API.
Key families: A (`OnToggleChanged` + `SessionData` guard), B (`Game.Instance.SetXxx` + `PlayerPrefs.Save`), C (enum setter), D (float setter), E (frame cap compound), F (DLSS special), G (resolution, deferred).

---

## File Locations

| File | Purpose |
|------|---------|
| `VRMod/SoDVR/OpenXRManager.cs` | OpenXR init, session, swapchain, frame loop — add action sets here |
| `VRMod/SoDVR/VR/VRCamera.cs` | Stereo render loop, UI canvas management — add controller pose + laser here |
| `VRMod/SoDVR/Plugin.cs` | BepInEx entry point — do not change |
| `BepInEx/plugins/SoDVR.dll` | Deployed plugin (flat layout only) |
| `BepInEx/LogOutput.log` | Runtime log |
| `VRMod/CLAUDE.md` | Claude project instructions (always loaded) |
| `VRMod/HANDOVER.md` | This file |

---

## Phase Roadmap

- [x] Phase 1–4: OpenXR init, session, swapchain (VDXR patch approach, archived `1be2b0e`)
- [x] **Phase 5**: Standard loader → real swapchain textures → stereo image in headset (`346a6df`)
- [x] **Phase 6**: Camera positioning, head tracking, UI canvases visible in VR
- [x] **Phase 7**: Controller pose + trigger click + cursor dot visible on all screens
- [ ] **Phase 8 (current)**: VR Settings Panel (Phase 0 done, Phase 1–4 per PLAN-Claude.md)
- [ ] Phase 9: Movement (thumbstick → character move/rotate), jump, interact bindings
- [ ] Phase 10: Comfort options (vignette, snap-turn, IPD)
- [ ] Phase 11: Polish, performance tuning
