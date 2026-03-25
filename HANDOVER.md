# SoDVR — Technical Handover

**Date**: 2026-03-25
**Phase**: 5 rewrite — switching from VDXR internal patching to standard OpenXR loader

---

## Why We're Rewriting

The previous approach (git `1be2b0e`) patched VDXR's internal session-type guards directly in machine code:
- 14 `PatchBytes` calls to NOP guard branches in xrCreateSession, xrCreateSwapchain, xrGetD3D11GraphicsRequirementsKHR, etc.
- Direct calls into VDXR's internal compositor setup function (CSF) and initD3D11
- Manual writes into the `OpenXrRuntime*` object (`robj`) to fake requirements

**Result**: Swapchains created (rc=0, 3 images), frame loop running, but swapchain image texture pointers were always null (0x0). The null path in SC-member traced back to `robj+0x990` (streaming pool) being unpopulated, which traced back to CSF's inner `ovr_Create()` wrapper being skipped when `robj+0x28 = 0x01`.

**Root insight**: LCVR, CWVR, and UnityVRMod all use standard `openxr_loader.dll` P/Invoke on VDXR — no guard patching — and produce real D3D11 swapchain textures. The guard bypasses were solving a problem that likely didn't exist for the standard API path.

---

## Current State of the Codebase

**OpenXRManager.cs** — still contains the old VDXR patching code. This file needs to be rewritten. Key code to **delete**:
- `TryInitializeProvider` / `NopFnACall` — UnityOpenXR.dll setup, no longer needed
- `TrySubsystemPath` — XRDisplaySubsystem path (always fails, IL2CPP stripped)
- `TryUnitySessionPath` — dead end, session_InitializeSession returns false
- `SetupDirect` — remove all `PatchBytes` and `ScanAndPatch*` calls; the VDXR guards don't fire on the standard loader path
- `GetVDXRRuntimeObject`, `SetRuntimeGraphicsRequirements`, `CallInitD3D11Direct`, `CallCompositorSetupDirect` — all VDXR internal manipulation, delete
- `PatchVtableDispatch`, `PatchInnerRequirementsCheck`, `PatchSecondCall`, `PatchCreateSessionContextBlock`, `ScanAndPatchMinus38`, `ScanAndPatchErrorCode`, `ScanAndPatchMinus38InDll` — patch infrastructure, delete
- `GetRuntimeCreateSessionViaGpa`, `PatchRuntimeCreateSession`, `FindPeExport` — VDXR-specific, delete
- `PatchSingleByte`, `PatchBytes`, `AllocNear`, `DumpFunctionBytes` — patch utilities, delete
- `CallD3D11GraphicsRequirements` — replace with simple direct call
- `EnumerateExtensions` — keep (useful diagnostic)

Key code to **keep** (already correct):
- `CreateInstance` — works, uses standard XR_KHR_D3D11_enable extension
- `XrGetSystem` — works
- `GetFn` — works
- `GetD3D11Device` — works (Texture2D.whiteTexture → GetNativeTexturePtr → vtable[3])
- `GetAdapterLuid` — works (IDXGIDevice → GetAdapter → GetDesc)
- `XrCreateSession` — fix only: change binding type from `0x3B9B3378` → `1000003000`
- `XrBeginSession`, `PollEvents`, `PollEventsPublic` — keep as-is
- `StartFrameThread`, `FrameThreadProc`, `FrameWait`, `FrameBegin`, `FrameEnd` — keep as-is
- `StopFrameThread`, `FrameWaitPublic`, `FrameBeginPublic`, `FrameEndEmpty` — keep as-is
- `SetupStereo`, `CreateSwapchain`, `PickSwapchainFormat`, `EnumSwapchainImages` — keep (remove ESI dump spam)
- `AcquireSwapchainImage`, `WaitSwapchainImage`, `ReleaseSwapchainImage` — keep as-is
- `LocateViews`, `ParseEyeView` — keep as-is
- `FrameEndStereo`, `WriteProjectionView` — keep as-is
- `D3D11CopyTexture` — keep as-is
- `EyePose` struct — keep as-is
- `ReadFloat`, `WriteFloat`, `LogActiveRuntime` — keep as-is

**VRCamera.cs** — do not change.
**Plugin.cs** — do not change.

---

## Rewrite Plan for OpenXRManager.cs

### New `TryInitializeProvider`
Only needs to:
1. Load `openxr_loader.dll` → get `xrGetInstanceProcAddr`
2. Store `_gpaAddr`, create `_gpa` delegate
3. Return true

Remove all UnityOpenXR.dll setup, XRSDKPreInit, NopFnACall.

### New `CheckAndStart`
Remove Path A (TrySubsystemPath) entirely. Remove the 60-frame retry gate. Just:
```csharp
if (!_directReady) SetupDirect();
if (!_directReady) return false;
return TryDirectPath();
```

### New `SetupDirect`
```
_gpa = delegate for _gpaAddr
EnumerateExtensions()               // diagnostic
CreateInstance()                    // xrCreateInstance
GetFn(all needed functions)
XrGetSystem()
_directReady = true
```
No patches. No robj. No vtable inspection.

### New `TryDirectPath`
```
GetD3D11Device() — wait until Unity has a D3D11 device
CallD3D11GraphicsRequirements()     // xrGetD3D11GraphicsRequirementsKHR(_instance, _systemId, &reqs)
XrCreateSession(device)             // XrGraphicsBindingD3D11KHR{type=1000003000, device}
PollEvents()
XrBeginSession()
StartFrameThread()
IsRunning = true
```

### New `CallD3D11GraphicsRequirements`
Simple single call:
```csharp
Marshal.WriteInt32(p, 0, 1000003002); // XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR
int rc = gfxReqs(_instance, _systemId, p);
```
No candidate loop, no counter logging.

---

## Delegates to Remove
- `XrNegotiateLoaderRuntimeDelegate`
- `VDXRSingletonDelegate`
- `InitD3D11Delegate`
- `CompositorSetupDelegate`
- `XRSDKPreInitDelegate`, `SetStage1Delegate`, `GetProcAddrPtrDelegate`, `UnitySessionBoolDelegate`

## Fields to Remove
- `_hUnityOpenXR`, `_pfnCreateSessionDirect`, `_directReady` (keep), `_gfxReqsDone` (keep)
- `_robjWriteDone`, `_robjDumped`, `_subsystemRetries`, `_unitySessTried`
- `_postSessionFrames` (was used for retry gate, no longer needed)

---

## Expected Outcomes After Rewrite

With standard loader and proper `XrGraphicsBindingD3D11KHR`:
- `xrGetD3D11GraphicsRequirementsKHR` → rc=0 (no guard bypass needed)
- `xrCreateSession` → rc=0, `_session` non-zero
- `xrCreateSwapchain` → rc=0, swapchain images have real `ID3D11Texture2D*` pointers (non-null)
- D3D11CopyTexture → renders Unity eye view into headset
- Headset should show game image

If xrGetD3D11GraphicsRequirementsKHR still fails (rc≠0), call xrCreateSession anyway — some runtimes enforce the call as merely advisory.

---

## File Locations

| File | Purpose |
|------|---------|
| `VRMod/SoDVR/OpenXRManager.cs` | **Needs rewrite** (see above) |
| `VRMod/SoDVR/VR/VRCamera.cs` | Stereo render loop — do not change |
| `VRMod/SoDVR/Plugin.cs` | BepInEx entry point — do not change |
| `BepInEx/plugins/SoDVR.dll` | Deployed plugin |
| `BepInEx/LogOutput.log` | Runtime log |

---

## Phase Roadmap

- [x] Phase 1–4: OpenXR init, session, swapchain — all rc=0 (VDXR patch approach)
- [ ] **Phase 5 (current)**: Rewrite OpenXRManager.cs → standard loader → real swapchain textures → image in headset
- [ ] Phase 6: Input — head tracking → game camera, controller → movement/interaction
- [ ] Phase 7: UI world-space conversion + laser pointer
- [ ] Phase 8–11: Full input mapping, game-specific interactions, comfort, polish
