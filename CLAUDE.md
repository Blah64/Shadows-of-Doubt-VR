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
    VR/VRCamera.cs         — Drives stereo rendering: xrWaitFrame/BeginFrame/Render/EndFrame + UI canvas management
  SoDVR.Preload/           — Preload helper (leave as-is)
  RuntimeDeps/             — DLL references for build (not deployed)
  SoDVR.csproj
  SoDVR.sln
```

## Build & deploy
```bash
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
rm -rf "../BepInEx/plugins/SoDVR"
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
**IMPORTANT**: Always use the flat layout (`plugins/SoDVR.dll`), never the subdirectory layout (`plugins/SoDVR/SoDVR.dll`). If both exist, BepInEx may load the wrong one silently.
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

## CRITICAL: IL2CPP interop pitfalls
- `GetComponentInParent<Button>()` always returns null in IL2CPP context — do not use for Button detection
- `Graphic.color` vs `material.color`: setting `mat.color.a` alone does NOT make UI transparent — must also set `g.color.a`
- `g.color = ...` per-frame marks canvas dirty (`SetVertexDirty`) → continuous rebuild lag; only set once at scan time
- `renderQueue` in HDRP Transparent: HDRP ignores fine-grained queue ordering; always sorts by spherical distance
- `Resources.FindObjectsOfTypeAll<Canvas>()` more reliable than `FindObjectsOfType<Canvas>()` in IL2CPP
- TMP hides `Graphic.color` with `new color` — must use `g.TryCast<TMP_Text>().color` to set vertex color
- `AddComponent<RectTransform>()` on a freshly created `new GameObject()` **returns null** in IL2CPP (Transform already exists). Instead: `AddComponent<Image>()` first, then `GetComponent<RectTransform>()`.
- VRMod-owned GameObjects (cursor canvas, settings panel) **must** have `DontDestroyOnLoad()` — the loading→menu scene transition destroys all non-persistent objects, silently invalidating cached Unity object references.

## CRITICAL: HDRP exposure — confirmed broken for per-camera override (2026-03-27)
**This is the current blocking problem for UI brightness.**

What was tried and confirmed NOT working:
- `FrameSettingsField.ExposureControl` **never persists** when set per-camera — `ExposureOff` always reads back `False`. Not overridable in HDRP 12 IL2CPP.
- `FrameSettingsField.Tonemapping` **does** persist — `TonemapOff=True` confirmed on all cameras.
- `VRExposureOverride` Volume (global, layer 31, EV=0→Fixed, Tonemapping=None, priority=1000) on UI overlay cameras (`volumeLayerMask=1<<31`) is **NOT applied** — confirmed: `fixedExposure=-10` (1024× brightness) produced zero change. Volume path is dead.

**Root cause**: The UI overlay cameras do not run their own HDRP exposure computation. They share/inherit the scene camera's auto-exposure (typically EV≈8–12 for city interiors → exposure multiplier ≈ 1/256–1/4096 → white vertex colours appear near-black).

**What the next session must try** (see HANDOVER.md for full detail):
1. Try `FrameSettingsField.Postprocess` (master toggle for all post-effects) in `s_VrDisabledFields` — if it persists unlike ExposureControl, it disables exposure as a side-effect
2. CommandBuffer to overwrite HDRP's 1×1 exposure texture to neutral (log2=0) before the UI camera's post-process pass
3. Read HDRP's live exposure value via `HDCamera` internals and apply compensating vertex-colour boost

## Phase 6 UI canvas status (COMPLETE)
- All canvases converted to WorldSpace each 30-frame scan ✓
- Canvases placed in front of head on first valid pose; stay fixed ✓
- **Home key** re-centres all canvases ✓
- TMP text: vertex colour = white, shader = UI/Default, TSA=(1,1,1,0) ✓
- **Button/menu text still dark** — HDRP auto-exposure issue (see HANDOVER.md §UI Brightness; not yet solved)

## Phase 7 controller input status (COMPLETE)
- Right controller pose tracked via OpenXR action sets ✓
- Trigger fires `ExecuteEvents` pointer-click on closest canvas hit ✓
- **Cursor dot** (`VRCursorCanvasInternal`) visible on all screens including post-scene-load menu ✓
  - Canvas NOT in `_ownedCanvasIds` → `RescanCanvasAlpha` applies ZTest Always material patch (makes it visible despite exposure)
  - `DontDestroyOnLoad` on cursor GO — survives loading→menu scene transition
  - Dot moves via `anchoredPosition` (2D projection onto canvas plane at `UIDistance`)
  - `_cursorCanvas` cached directly at `BuildCameraRig` time — never rely on name-lookup in `PositionCanvases`

## Phase 8 (current): VR Settings Panel
- Phase 0 test canvas (`VRSettingsPanelInternal`, F10 to toggle) confirmed visible ✓
- **Next**: Phase 1 — extract into `VRSettingsPanel.cs` with two tabs, per PLAN-Claude.md

## Known canvas names (from LogOutput.log, 2026-03-26)
| Canvas | Elements | Notes |
|--------|----------|-------|
| `Canvas` | 4 | Startup loading splash (800×600) |
| `MenuCanvas` | 2231 | Main menu; `[0]'Background'`, `[2230]'FadeOverlay'` |
| `GameCanvas` | 327 | In-game HUD; `[1]'Fade'` suppressed |
| `OverlayCanvas` | 42 | sortOrder=4, 1920×1080 |
| `TooltipCanvas` | 82 | sortOrder=3, 1920×1080 |
| `PrototypeBuilderCanvas` | 144 | City builder, sortOrder=2 |
| `VirtualCursorCanvas & EventSystem` | 181 | Virtual keyboard + cursor, sortOrder=5 |
| `ActionPanelCanvas`, `DialogCanvas`, `BioDisplayCanvas`, `MinimapCanvas` | various | In-game panels |

## History
- git `1be2b0e` — full VDXR-internal patching approach (archived checkpoint, do not rebase)
- git `346a6df` — **Phase 5 complete**: standard loader working, stereo image in headset
- **Phase 6 complete**: camera positioning ✓, head tracking ✓, UI canvases visible in VR ✓
- **Phase 7 complete**: controller pose ✓, trigger click ✓, cursor dot visible on all screens ✓
- **Phase 8 (current)**: VR settings panel — Phase 0 test canvas confirmed ✓, Phase 1 next
