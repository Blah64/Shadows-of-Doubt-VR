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
- **CanvasScaler inflates sizeDelta** (discovered 2026-03-29): every game canvas has a CanvasScaler in "Scale With Screen Size" mode. Before our code runs, CanvasScaler has already set sizeDelta to the scaled display size (e.g. 2720×1680 instead of 1280×720). When we then switch renderMode to WorldSpace and set a fixed scale, the canvas is 2–4 metres wide. **Fix: disable the CanvasScaler component before calling `canvas.renderMode = RenderMode.WorldSpace`.**


## CRITICAL: IL2CPP Button / event additional pitfalls
- `AddListener` on a **freshly-created** `new Button.ButtonClickedEvent()` is unreliable in IL2CPP — the listener silently fails to fire. Instead, intercept clicks in `TryClickCanvas` by comparing `tr.gameObject.GetInstanceID()` against a stored button GO id.
- `btn.GetInstanceID()` returns the **component** instance ID, NOT the GameObject ID. For GO comparisons always use `btn.gameObject.GetInstanceID()`.
- `btn.onClick = new Button.ButtonClickedEvent()` DOES kill persistent (prefab-baked) listeners — use this to suppress a game button's default behaviour.

## CRITICAL: FMOD audio (confirmed working)
- `AudioListener.volume` has **zero effect** on FMOD sounds — game audio is entirely FMOD.
- Master volume: `FMODUnity.RuntimeManager.GetBus("bus:/").setVolume(float 0–1)` ✓
- Per-channel VCA: `FMODUnity.RuntimeManager.GetVCA("vca:/Soundtrack").setVolume(float)` ✓ (same pattern for all VCA paths)
- VCA paths confirmed from strings bank: `vca:/Soundtrack`, `vca:/Ambience`, `vca:/Weather`, `vca:/Footsteps`, `vca:/Notifications`, `vca:/PA System`, `vca:/Other SFX`
- `FMODUnity.dll` is in `BepInEx/interop/` and must be referenced in `SoDVR.csproj` ✓
- Music toggle: `SetFamilyA("music", bool)` updates both in-memory `GameSetting.intValue` AND PlayerPrefs ✓

## CRITICAL: HDRP exposure workaround (Phase 9 solution — working)

`FrameSettingsField.ExposureControl` cannot be overridden per-camera in HDRP 12 IL2CPP.
**Workaround**: HDR material colour boost applied at material-creation time in `ForceUIZTestAlways`:

| Item type | Treatment |
|-----------|-----------|
| `isText` (TMP_Text or Text component) | `StrengthenMenuTextMaterial` → `_FaceColor`/`_Color` = `(32,32,32,1)` |
| `isAdditive` (shader name contains "Additive"/"Particle"/"Add") | Shader replaced with `UI/Default`, `renderQueue=3001`, `mat.color=(1,1,1,0.85)` |
| `isBg` (GO name contains "background" or equals "BG") | `renderQueue=3000`, `color.a = UIBackgroundAlpha (0.07)` |
| other (panels, borders, images) | `renderQueue=3008`, `color *= 4f` (min 0.01 per channel) |

`StrengthenMenuTextMaterial` is called for ALL text items regardless of canvas category.

## CRITICAL: CanvasScaler must be disabled before WorldSpace conversion (FIXED ✓)
`ConvertCanvasToWorldSpace` disables CanvasScaler BEFORE switching to WorldSpace.
`sizeDelta` stays at reference resolution (e.g. 1920×1080) and canvas scales correctly.

## CRITICAL: Game resets canvas localScale — enforce every scan cycle (FIXED ✓)
The game resets `WindowCanvas.localScale` to 1.0 when opening notebook/notes panels.
The reparent/ScaleFix pass re-applies correct `localScale` every 90-frame scan cycle for ALL
root managed canvases. Log line: `ScaleFix 'WindowCanvas': 1.0 → 0.000625`.

## CRITICAL: Mobile/Particles/Additive shader in HDRP WorldSpace (FIXED ✓)
Legacy mobile shader does not render in HDRP WorldSpace pipeline.
Any material whose shader name contains "Additive", "Particle", or "Add" (`isAdditive`) has
its shader replaced with `UI/Default` at material-creation time. Icons/borders/glows now visible.

## CRITICAL: HDRP flipYMode for Camera.Render() to RenderTexture (Phase 10)

When manually calling `Camera.Render()` to a RenderTexture in HDRP:
- **DO NOT** combine `GL.invertCulling = true` with projection matrix Y-negation — both reverse clip-space winding → double-correction → front-faces culled → walls/floors invisible.
- **DO** set `HDAdditionalCameraData.flipYMode = HDAdditionalCameraData.FlipYMode.ForceFlipY` — HDRP handles both image Y-flip AND face culling correction atomically.
- VR eye cameras **must** copy the game camera's `HDAdditionalCameraData` settings at runtime, especially `volumeLayerMask` (controls which HDRP Volumes affect the camera). Without it, VR cameras won't pick up scene lighting/sky/fog → dark/unlit rendering.
- `CopyGameCameraSettings(Camera src, Camera dst)` in VRCamera.cs does this when the game camera is found.

## Phase 10 status (COMPLETE — 2026-03-30)
- Stereo rendering, head tracking, swapchain: **working** ✓
- UI canvases WorldSpace, placed in front of head: **working** ✓
- Trigger click, cursor tracking: **working** ✓
- VR Settings Panel, FMOD audio: **working** ✓
- Canvas sizes (CanvasScaler fix): **working** ✓
- UI text visibility (HDR boost): **working** ✓
- Icons/symbols (Additive shader fix): **working** ✓
- Notebook/notes sizing (ScaleFix): **working** ✓
- Crash prevention (rescan rate-limit, material cache cap): **working** ✓
- CaseCanvas bright white background: **disabled** ✓
- **World graphics (walls, floors, lighting): working** ✓ (Phase 10)

## Phase 11 movement status (IN PROGRESS)

Already implemented in VRCamera.cs — **needs functional testing**:
| Feature | Status | Notes |
|---------|--------|-------|
| Thumbstick locomotion | Working ✓ | Left stick → `CharacterController.Move()` at 4 m/s |
| Snap turn | Working ✓ | Right stick X → ±30° VROrigin rotation |
| Menu button (ESC) | Working ✓ | Left Y/menu → ESC key simulation |
| Sprint | Working ✓ | Left stick click, wired to game's `alwaysRun` setting |
| Left laser beam | Working ✓ | LineRenderer on left controller, toggle in VR Settings |
| Held item tracking | Working ✓ | `InteractionController.carryingObject` → VR controller |
| Item hand selection | Working ✓ | VR Settings toggle: Left/Right hand |
| Jump | **Not implemented** | Needs A button binding in OpenXRManager |
| World interact | **Not implemented** | Needs button binding → 'E' key simulation |

OpenXR actions currently bound in OpenXRManager.cs:
- `_poseAction` — both hand aim poses
- `_triggerAction` — both triggers (right = UI click)
- `_thumbAction` — both thumbsticks (left = locomotion, right = snap turn)
- `_gripAction` — both grips (unused beyond binding)
- `_menuButtonAction` — left Y + left menu button → ESC

## CRITICAL: Held item tracking — two separate systems (discovered 2026-03-29)
The game has TWO independent item systems:

1. **First-person arms** (`3DUI` Canvas → `LagPivot` → `FirstPersonModels` → `Arms` → fingers/hands)
   - For inventory items with hand models (pen, lockpick, camera)
   - `FirstPersonItem` is a ScriptableObject (NOT a Component), `leftHandObject`/`rightHandObject` are prefab refs
   - `Arms` GO is often `active=False`; arm meshes at extreme positions (hundreds of units off)
   - Positioned via Canvas layout + Animator — LagPivot override alone does NOT work
   - `leftHandObjectParent`/`rightHandObjectParent` on `FirstPersonItemController` are `ItemContainer` transforms

2. **Carried world objects** (`InteractionController.carryingObject`)
   - For large items picked up from the world (boxes, lamps, books, evidence)
   - `InteractableController` is a MonoBehaviour ON the world object itself
   - The game repositions this GO relative to Camera.main each frame
   - **This is what players see most often** — override `carryingObject.transform` to the VR controller
   - Found via `InteractionController` component on FPSController

**VR approach**: override `InteractionController.carryingObject.transform.position/rotation` every frame
(in Update AND pre-render) to the VR controller position + 0.3m forward offset.

## Known canvas names (from LogOutput.log)
| Canvas | Size | Status |
|--------|------|--------|
| `MenuCanvas` | 1.20m | Working ✓ |
| `WindowCanvas` (notes/notebook) | 1.20m | Working ✓ |
| `ActionPanelCanvas` | 1.60m | Working ✓ |
| `DialogCanvas` | 1.20m | Working ✓ |
| `BioDisplayCanvas` | 1.80m | Working ✓ |
| `GameCanvas`/HUD | 1.50m | Working ✓ |
| `TooltipCanvas` | 0.80m | Working ✓ |
| `PopupMessage` | 1.20m | Working ✓ (scale enforced each cycle) |
| `VRSettingsPanelInternal` | 1.60m | Working ✓ |
| `CaseCanvas` | — | Disabled (was bright white background) |
| `MinimapCanvas` | 1.50m | Partially working |

## History
- git `1be2b0e` — full VDXR-internal patching approach (archived checkpoint, do not rebase)
- git `346a6df` — **Phase 5 complete**: standard loader working, stereo image in headset
- **Phase 6 complete**: camera positioning ✓, head tracking ✓, UI canvases visible in VR ✓
- **Phase 7 complete**: controller pose ✓, trigger click ✓, cursor dot tracking ✓
- **Phase 8 complete** (git `12172ad`): VR settings panel ✓ — 4 tabs, all settings wired, FMOD audio, Settings button intercept
- **Phase 9 complete** (2026-03-29): All canvas/UI issues resolved — see HANDOVER.md for full details
- **Phase 10 complete** (2026-03-30): World graphics — flipYMode + HDRP Volume config fix (`546a4b5`)
- **Phase 11 (current)**: Movement — locomotion testing + jump + world interact bindings
