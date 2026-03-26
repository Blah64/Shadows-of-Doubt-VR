# SoDVR — Technical Handover

**Date**: 2026-03-26
**Phase**: 7 — Controller input (next work item)

---

## What Works (Fully Operational)

### Phase 5 — Stereo rendering (git `346a6df`)
- `xrCreateInstance` → `xrGetSystem` → `xrGetD3D11GraphicsRequirementsKHR` → `xrCreateSession` → `xrBeginSession` → `xrCreateSwapchain ×2` → `xrEnumerateSwapchainImages` — all rc=0
- Swapchain: 3648×3936, DXGI_FORMAT_R8G8B8A8_UNORM (format 28), 3 images per eye
- `D3D11CopyResource` delivers frames to headset each Unity frame
- Head tracking (`xrLocateViews`) drives left/right eye poses ✓
- VROrigin follows game camera world position ✓
- HDRP eye cameras: AA=None, custom FrameSettings (SSAO/SSR/Volumetrics/MotionBlur etc. off) ✓

### Phase 6 — UI canvases visible in VR (current code)
- All screen-space (`ScreenSpaceOverlay` / `ScreenSpaceCamera`) root canvases automatically converted to `WorldSpace` every 30 frames
- Each canvas placed once in front of head on first valid tracked pose; stays fixed so player can look around it
- **Home key** clears placement set — all canvases re-centre on next LateUpdate
- FadeOverlay / Fade graphics suppressed (rate-limited every 4 frames)
- Canvases visible in headset: main menu, HUD, tooltips, dialogs, minimap etc.

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

## Known VDXR-Supported Extensions (from log, 2026-03-26)
Full list confirmed at runtime:
```
XR_KHR_D3D11_enable          (in use)
XR_EXT_hand_tracking          ← hand tracking available
XR_EXT_palm_pose
XR_EXT_active_action_set_priority
XR_FB_display_refresh_rate
XR_KHR_composition_layer_depth
XR_KHR_composition_layer_cylinder
XR_KHR_visibility_mask
XR_OCULUS_audio_device_guid
```
To use `XR_EXT_hand_tracking`, add it to the extensions array in `xrCreateInstance` (in `OpenXRManager.cs`).

---

## IL2CPP Interop Pitfalls (learned the hard way)

| Pitfall | Detail |
|---------|--------|
| `GetComponentInParent<Button>()` | Always returns null in IL2CPP — never use for Button detection |
| `Graphic.color` vs `mat.color` | Separate in HDRP UI. `mat.color.a` alone does not make UI transparent. Must set `g.color.a`. |
| `g.color = ...` per-frame | Calls `SetVertexDirty()` → canvas mesh rebuild → serious lag at 60 Hz. Rate-limit or avoid. |
| `renderQueue` in HDRP Transparent | HDRP ignores fine-grained queue values within Transparent range; always sorts by spherical distance. |
| `FindObjectsOfType<Canvas>()` | Less reliable in IL2CPP. Use `Resources.FindObjectsOfTypeAll<Canvas>()`. |

---

## Unresolved UI Issues (left for future work)

### Button visibility / brightness
- **Root cause**: HDRP composites UI colour as `vertex-color (g.color) × material._Color`. Our `mat.color` boost (`×3`) is ineffective unless `g.color` is also boosted. But setting `g.color` every frame causes canvas rebuild lag; setting it once at conversion time works but must avoid the game overriding it.
- **Correct fix**: At `ForceUIZTestAlways` time, for non-background Graphic elements, also set `g.color = new Color(g.color.r * UIColorBoost, g.color.g * UIColorBoost, g.color.b * UIColorBoost, g.color.a)` — a one-time vertex colour boost at scan time only.

### Background transparency
- **Root cause**: Same issue — `mat.color.a = 0.25f` does nothing without `g.color.a = 0.25f`.
- **Correct fix**: At scan time, for elements named "background*", also set `g.color = new Color(g.color.r, g.color.g, g.color.b, UIBackgroundAlpha)`.

Both fixes are one-shot (called once per canvas at `ConvertCanvasToWorldSpace` time), so they avoid per-frame dirty marks.

---

## Phase 7 — Controller Input (next)

### Goal
Laser-pointer style interaction: controller pose tracked via OpenXR, ray projected into the scene, intersection with WorldSpace canvases detected by Unity's `GraphicRaycaster`, trigger = click.

### Step 1 — Add controller extension to xrCreateInstance
In `OpenXRManager.cs`, add to the extensions array:
```csharp
// Already have: "XR_KHR_D3D11_enable"
// No extra extension needed for standard controller input — action sets are core OpenXR 1.0
```

### Step 2 — OpenXR action set setup (after xrCreateSession, before xrBeginSession)
```
xrCreateActionSet(instance, {name="gameplay", localizedName="Gameplay", priority=0}) → actionSet
xrCreateAction(actionSet, {name="hand_pose",  type=XR_ACTION_TYPE_POSE_INPUT,    subactionPaths=["/user/hand/left","/user/hand/right"]}) → poseAction
xrCreateAction(actionSet, {name="trigger",    type=XR_ACTION_TYPE_BOOLEAN_INPUT,  subactionPaths=["/user/hand/left","/user/hand/right"]}) → triggerAction
xrCreateAction(actionSet, {name="thumbstick", type=XR_ACTION_TYPE_VECTOR2F_INPUT, subactionPaths=["/user/hand/left","/user/hand/right"]}) → thumbstickAction

xrSuggestInteractionProfileBindings(instance, {
  profile: "/interaction_profiles/oculus/touch_controller",   ← Samsung Galaxy XR uses this via VDXR
  bindings: [
    { poseAction,      "/user/hand/right/input/aim/pose" },
    { poseAction,      "/user/hand/left/input/aim/pose"  },
    { triggerAction,   "/user/hand/right/input/trigger/value" },
    { triggerAction,   "/user/hand/left/input/trigger/value"  },
    { thumbstickAction,"/user/hand/right/input/thumbstick"    },
    { thumbstickAction,"/user/hand/left/input/thumbstick"     },
  ]
})

xrCreateActionSpace(session, {action=poseAction, subactionPath="/user/hand/right"}) → rightAimSpace
xrCreateActionSpace(session, {action=poseAction, subactionPath="/user/hand/left"})  → leftAimSpace

xrAttachSessionActionSets(session, {actionSets=[actionSet]})
```

### Step 3 — Per frame (in Update / LateUpdate)
```
xrSyncActions(session, {activeSets=[{actionSet, XR_NULL_PATH}]})
xrLocateSpace(rightAimSpace, referenceSpace, displayTime) → rightPose   // controller world pose
xrGetActionStateBoolean(session, {action=triggerAction, subactionPath="/user/hand/right"}) → triggerState
xrGetActionStateVector2f(session, {action=thumbstickAction, ...}) → stickState
```

### Step 4 — Laser pointer in VRCamera.cs
- Create a `GameObject("RightController")` under VROrigin; add `LineRenderer` for the laser beam
- Each frame: apply `rightPose` to controller transform (same coord flip as ApplyCameraPose)
- Cast a `Physics.Raycast` from controller position along controller forward
- Also cast against WorldSpace canvas: use `GraphicRaycaster` on each managed canvas

### Step 5 — Canvas click via Unity event system
```csharp
// For each managed canvas, ensure it has a GraphicRaycaster with worldCamera set to _leftCam
var gr = canvas.GetComponent<GraphicRaycaster>() ?? canvas.AddComponent<GraphicRaycaster>();
gr.blockingMask = 0;

// On trigger press, send pointer event:
var ped = new PointerEventData(EventSystem.current);
ped.position = _leftCam.WorldToScreenPoint(hitPoint);
var results = new List<RaycastResult>();
gr.Raycast(ped, results);
if (results.Count > 0)
    ExecuteEvents.Execute(results[0].gameObject, ped, ExecuteEvents.pointerClickHandler);
```

### Interaction profile note
Samsung Galaxy XR paired through Virtual Desktop presents as an Oculus Touch controller to VDXR.
Profile path: `/interaction_profiles/oculus/touch_controller`
Aim pose gives pointing direction suitable for laser pointer (vs grip pose for held objects).

---

## File Locations

| File | Purpose |
|------|---------|
| `VRMod/SoDVR/OpenXRManager.cs` | OpenXR init, session, swapchain, frame loop — add action sets here |
| `VRMod/SoDVR/VR/VRCamera.cs` | Stereo render loop, UI canvas management — add controller pose + laser here |
| `VRMod/SoDVR/Plugin.cs` | BepInEx entry point — do not change |
| `BepInEx/plugins/SoDVR.dll` | Deployed plugin |
| `BepInEx/LogOutput.log` | Runtime log |
| `VRMod/CLAUDE.md` | Claude project instructions (always loaded) |
| `VRMod/HANDOVER.md` | This file |

---

## Phase Roadmap

- [x] Phase 1–4: OpenXR init, session, swapchain (VDXR patch approach, archived `1be2b0e`)
- [x] **Phase 5**: Standard loader → real swapchain textures → stereo image in headset (`346a6df`)
- [x] **Phase 6**: Camera positioning, head tracking, UI canvases visible in VR
- [ ] **Phase 7 (current)**: Controller input — laser pointer, trigger = click on canvas buttons
- [ ] Phase 8: Movement (thumbstick → character move/rotate), jump, interact bindings
- [ ] Phase 9: UI brightness fix (one-shot `g.color` boost at scan time), comfort options
- [ ] Phase 10: Polish, performance tuning, comfort (vignette, snap-turn)
