# SoDVR — Technical Handover

**Date**: 2026-03-27
**Phase**: 6 complete (UI visible), brightness fix in progress

---

## What Works (Fully Operational)

### Phase 5 — Stereo rendering (git `346a6df`)
- Full OpenXR pipeline: xrCreateInstance → xrGetSystem → gfxReqs → xrCreateSession → xrBeginSession → xrCreateSwapchain ×2 → xrEnumerateSwapchainImages — all rc=0
- Swapchain: 2554×2756, DXGI_FORMAT_R8G8B8A8_UNORM (format 28), 3 images per eye
- `D3D11CopyResource` delivers frames to headset each Unity frame
- Head tracking (`xrLocateViews`) drives left/right eye poses ✓
- VROrigin follows game camera world position ✓
- HDRP eye cameras: AA=None, custom FrameSettings (SSAO/SSR/Volumetrics/MotionBlur/Tonemapping off) ✓

### Phase 6 — UI canvases in VR (current code, PARTIAL)
- All screen-space canvases auto-converted to WorldSpace every 30 frames ✓
- Each canvas placed once in front of head on first valid tracked pose ✓
- **Home key** re-centres all canvases ✓
- FadeOverlay/Fade suppressed ✓
- TMP font shader swapped from Distance Field → UI/Default with correct TSA ✓
- Canvases visible in headset, but **text too dark to read** (see §UI Brightness below)

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

---

## Phase 7 — Controller Input (after brightness fix)

### Goal
Laser-pointer style interaction: controller pose tracked via OpenXR, ray projected into the scene,
intersection with WorldSpace canvases detected by Unity's `GraphicRaycaster`, trigger = click.

### Step 1 — OpenXR action set setup (after xrCreateSession, before xrBeginSession)
```
xrCreateActionSet(instance, {name="gameplay", localizedName="Gameplay", priority=0}) → actionSet
xrCreateAction(actionSet, {name="hand_pose",  type=POSE_INPUT,    subactionPaths=["/user/hand/left","/user/hand/right"]}) → poseAction
xrCreateAction(actionSet, {name="trigger",    type=BOOLEAN_INPUT, subactionPaths=[...]}) → triggerAction
xrCreateAction(actionSet, {name="thumbstick", type=VECTOR2F_INPUT, subactionPaths=[...]}) → thumbstickAction

xrSuggestInteractionProfileBindings(instance, {
  profile: "/interaction_profiles/oculus/touch_controller",   ← Samsung Galaxy XR via VDXR
  bindings: [
    { poseAction,       "/user/hand/right/input/aim/pose" },
    { poseAction,       "/user/hand/left/input/aim/pose"  },
    { triggerAction,    "/user/hand/right/input/trigger/value" },
    { thumbstickAction, "/user/hand/right/input/thumbstick"   },
  ]
})

xrCreateActionSpace(session, {action=poseAction, subactionPath="/user/hand/right"}) → rightAimSpace
xrAttachSessionActionSets(session, {actionSets=[actionSet]})
```

### Step 2 — Per frame
```
xrSyncActions(session, {activeSets=[{actionSet, XR_NULL_PATH}]})
xrLocateSpace(rightAimSpace, referenceSpace, displayTime) → rightPose
xrGetActionStateBoolean(session, {action=triggerAction, subactionPath="/user/hand/right"}) → triggerState
```

### Step 3 — Laser pointer
- `RightController` GameObject under VROrigin; add `LineRenderer` for the beam
- Apply rightPose each frame (same coord flip as `ApplyCameraPose`)
- Raycast against WorldSpace canvases via `GraphicRaycaster` (already added to each canvas at scan time)
- On trigger: `ExecuteEvents.Execute(hitGO, ped, ExecuteEvents.pointerClickHandler)`

### Interaction profile note
Samsung Galaxy XR via Virtual Desktop presents as Oculus Touch. Profile: `/interaction_profiles/oculus/touch_controller`.

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
- [ ] **Phase 7 (current)**: Fix UI brightness (see §UI Brightness above), then controller input
- [ ] Phase 8: Movement (thumbstick → character move/rotate), jump, interact bindings
- [ ] Phase 9: Comfort options (vignette, snap-turn, IPD)
- [ ] Phase 10: Polish, performance tuning
