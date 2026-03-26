# SoDVR — Claude Project Instructions

## What this is
A BepInEx 6 IL2CPP plugin adding full 6DOF VR support to **Shadows of Doubt** (Unity 2021.3.45f2, HDRP).
Target runtime: **VDXR** (`virtualdesktop-openxr.dll`) via Samsung Galaxy XR headset.

## Key constraints
- **IL2CPP** — no managed game assembly, all game interaction via Il2CppInterop
- **BepInEx 6** — plugin entry point is `Plugin.cs`, all patching in `Awake()`
- **Standard OpenXR** — P/Invoke to `openxr_loader.dll` via `xrGetInstanceProcAddr`; no VDXR vtable hacks or guard patches
- **D3D11 binding** — `XrGraphicsBindingD3D11KHR` uses Unity's existing D3D11 device (obtained via `GetNativeTexturePtr()` → vtable slot 3 `GetDevice`)
- **No coroutines** — IL2CPP coroutine interop is avoided; camera drives via `Update()`/`LateUpdate()`

## Repo layout
```
VRMod/
  SoDVR/
    Plugin.cs              — BepInEx entry; creates VROrigin GameObject on scene load
    OpenXRManager.cs       — Standard OpenXR init (xrCreateInstance → session → swapchain), stereo frame loop
    VR/VRCamera.cs         — Drives stereo rendering: xrWaitFrame/BeginFrame/Render/EndFrame
  SoDVR.Preload/           — Preload helper (leave as-is)
  RuntimeDeps/             — DLL references for build (not deployed)
  SoDVR.csproj
  SoDVR.sln
```

## Build & deploy
```bash
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
Log output: `E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`

## Working conventions
- User says **"done"** = game has been run, log is ready to read
- Always read `LogOutput.log` immediately when user says "done"
- Delegate all builds to assistant (user doesn't run builds manually)
- Prefer focused log dumps over wide ones
- Step-by-step, one change at a time

## Standard OpenXR init sequence (WORKING)
```
xrGetInstanceProcAddr (from openxr_loader.dll, loaded directly)
  → disable all implicit API layers (openxr-oculus-compatibility via env var)
  → xrCreateInstance          (extensions: XR_KHR_D3D11_enable)
  → xrGetSystem               (HEAD_MOUNTED_DISPLAY)
  → xrGetD3D11GraphicsRequirementsKHR  (instance, systemId, &reqs)
  → [get Unity D3D11 device via Texture2D.whiteTexture.GetNativeTexturePtr() + vtable[3]]
  → xrCreateSession           (XrGraphicsBindingD3D11KHR{type=0x3B9B3378, device=unityDev})
  → xrBeginSession
  → xrCreateSwapchain × 2    (per-eye, format 28 = DXGI_FORMAT_R8G8B8A8_UNORM)
  → xrEnumerateSwapchainImages → ID3D11Texture2D* array (non-null)
```

## D3D11 texture copy (each stereo frame)
```
Unity renders eye → RenderTexture(ARGB32) → GetNativeTexturePtr() = ID3D11Texture2D* (src)
AcquireSwapchainImage → index → LeftSwapchainImages[index] = ID3D11Texture2D* (dst)
ID3D11DeviceContext::CopyResource(ctx, dst, src)   [vtable slot 47]
ReleaseSwapchainImage
xrEndFrame with XrCompositionLayerProjection
```

## CRITICAL: VDXR uses non-spec OpenXR type constants
VDXR's type constants are offset by **+0x5DC0** from the OpenXR spec values.
Always use these VDXR values, not the spec values:

| Struct | Spec value | **VDXR value (use this)** |
|---|---|---|
| XrGraphicsBindingD3D11KHR | 1000003000 | `unchecked((int)0x3B9B3378)` |
| XrSwapchainImageD3D11KHR | 1000003001 | `unchecked((int)0x3B9B3379)` |
| XrGraphicsRequirementsD3D11KHR | 1000003002 | `unchecked((int)0x3B9B337A)` |

Base OpenXR types (XR_TYPE_SESSION_CREATE_INFO=8, etc.) use spec values unchanged.

## CRITICAL: Swapchain format must match Unity RenderTexture format
- Unity `RenderTextureFormat.ARGB32` → `DXGI_FORMAT_R8G8B8A8_UNORM` = **format 28**
- Swapchain must be created with format 28 so `CopyResource` succeeds
- `_preferredFormats = { 28, 29, 87, 91 }` — format 28 is first

## CRITICAL: openxr-oculus-compatibility API layer
Virtual Desktop installs an Oculus compatibility layer that intercepts OpenXR calls.
It must be disabled before `xrCreateInstance` by setting its `disable_environment` env var.
See `DisableUnityOpenXRLayer()` in OpenXRManager.cs — reads all layer JSONs from registry
and sets their `disable_environment` variables.

## History
- git `1be2b0e` — full VDXR-internal patching approach (archived checkpoint, do not rebase)
- git `346a6df` — **Phase 5 complete**: standard loader working, stereo image in headset
- Current work — Phase 6: camera positioning, head tracking → game world
