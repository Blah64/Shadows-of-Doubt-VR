# SoDVR — Technical Handover

**Date**: 2026-03-25
**Phase**: 6 — Camera positioning, head tracking → game world

---

## What Works (Phase 5 Complete — git `346a6df`)

The full OpenXR pipeline is operational:

- `xrCreateInstance` → rc=0
- `xrGetSystem` → rc=0, systemId=0x1
- `xrGetD3D11GraphicsRequirementsKHR` → rc=0, LUID populated
- `xrCreateSession` → rc=0, session=0x1
- `xrBeginSession` → rc=0
- `xrCreateSwapchain` × 2 → rc=0, 3648×3936, 3 images each
- `xrEnumerateSwapchainImages` → rc=0, real `ID3D11Texture2D*` pointers (non-null)
- `D3D11CopyResource` → working, frames delivered to headset
- Head tracking → stereo image tracks with head movement ✓
- VROrigin follows game camera world position ✓

---

## Critical VDXR Discoveries

### 1. Type constant offset (+0x5DC0)
VDXR uses non-spec type constants for all D3D11 extension structs.
The OpenXR loader passes through what the app writes, so we must write VDXR's values:

| Struct | Spec | VDXR |
|---|---|---|
| XrGraphicsBindingD3D11KHR | 1000003000 = 0x3B9AD5B8 | 0x3B9B3378 |
| XrSwapchainImageD3D11KHR | 1000003001 = 0x3B9AD5B9 | 0x3B9B3379 |
| XrGraphicsRequirementsD3D11KHR | 1000003002 = 0x3B9AD5BA | 0x3B9B337A |

Discovered via Python + capstone disassembly of VDXR's vtable implementations:
- `gfxReqs_impl` at +0x014: `cmp dword ptr [r9], 0x3b9b337a` — type check, returns -1 if mismatch
- `createSession_impl` loop: iterates `next` chain checking for `0x3B9B3378`; if not found and gfxReqs flag unset → returns -38

### 2. openxr-oculus-compatibility API layer
Virtual Desktop registers `openxr-oculus-compatibility.dll` as an implicit OpenXR API layer.
It intercepts `xrCreateSession` and enforces its own graphics requirements check → -38.
Fix: before `xrCreateInstance`, read the layer JSON and set `DISABLE_XR_APILAYER_VIRTUALDESKTOP_OCULUS_COMPATIBILITY=1`.
See `DisableUnityOpenXRLayer()` in OpenXRManager.cs.

### 3. Swapchain format must match Unity RenderTexture
- Unity `RenderTextureFormat.ARGB32` → D3D11 `DXGI_FORMAT_R8G8B8A8_UNORM` = format **28**
- `CopyResource` silently fails if src/dst formats differ
- VDXR supports format 28; use `_preferredFormats = { 28, 29, 87, 91 }`

---

## Current State (Phase 6 start)

### VRCamera.cs
- `_gameCam` field stores the original game camera's transform
- `BuildCameraRig`: disables mc, saves `_gameCam = mc.transform`, teleports VROrigin to mc's world position
- `Update()`: `if (_gameCam != null) transform.position = _gameCam.position;` — follows character each frame
- Eye cameras positioned at VROrigin + HMD pose offset (from `xrLocateViews`)
- Head tracking works: rotation/position applied via `ApplyCameraPose`

### What the headset shows
- Stereo image with correct head tracking ✓
- Positioned at the game character's camera world position ✓
- Game scene renders from that position ✓

---

## Phase 6 Work Remaining

### Camera / rendering
- [ ] The game uses HDRP — verify eye cameras render game geometry correctly (may need `HDAdditionalCameraData`)
- [ ] Verify colors/gamma look correct (HDRP tonemapping, linear vs gamma)
- [ ] The game camera's **yaw** is not transferred to VROrigin — the player always faces "world north" regardless of game character orientation. May need to sync VROrigin.rotation.y from the game camera.

### Input — Phase 6
- [ ] Head rotation → game character look direction (send mouse/look input to match HMD yaw)
- [ ] Controller bindings — movement (walk/run), interact, jump, menu
- [ ] OpenXR action sets: `xrCreateActionSet`, `xrCreateAction`, `xrSuggestInteractionProfileBindings`, `xrAttachSessionActionSets`
- [ ] `xrLocateSpace` for hand/controller pose
- [ ] Map controller input to game's IL2CPP input system

---

## File Locations

| File | Purpose |
|------|---------|
| `VRMod/SoDVR/OpenXRManager.cs` | OpenXR init, session, swapchain, frame loop |
| `VRMod/SoDVR/VR/VRCamera.cs` | Stereo render loop, camera rig, position tracking |
| `VRMod/SoDVR/Plugin.cs` | BepInEx entry point — do not change |
| `BepInEx/plugins/SoDVR.dll` | Deployed plugin |
| `BepInEx/LogOutput.log` | Runtime log |

---

## Phase Roadmap

- [x] Phase 1–4: OpenXR init, session, swapchain — all rc=0 (VDXR patch approach, archived)
- [x] **Phase 5**: Rewrite OpenXRManager.cs → standard loader → real swapchain textures → stereo image in headset
- [ ] **Phase 6 (current)**: Camera positioning ✓ (partial), head tracking → game yaw, controller input
- [ ] Phase 7: UI world-space conversion + laser pointer
- [ ] Phase 8–11: Full input mapping, game-specific interactions, comfort, polish
