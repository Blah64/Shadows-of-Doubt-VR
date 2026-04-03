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

## Phase 11 status (COMPLETE — 2026-03-30)

All movement and interaction bindings implemented and tested:
| Feature | Status | Notes |
|---------|--------|-------|
| Thumbstick locomotion | Working ✓ | Left stick → `CharacterController.Move()` at 4 m/s |
| Snap turn | Working ✓ | Right stick X → ±30° VROrigin rotation |
| Menu button (ESC) | Working ✓ | Left Y/menu → ESC key simulation (1.5s cooldown) |
| Sprint | Working ✓ | Left stick click toggle, auto-stops on stick release |
| Left laser beam | Working ✓ | LineRenderer on left controller, toggle in VR Settings |
| Held item tracking | Working ✓ | `InteractionController.carryingObject` → VR controller |
| Item hand selection | Working ✓ | VR Settings toggle: Left/Right hand |
| VR arm display | Working ✓ | Both arms track controllers, per-hand rotation offsets |
| Jump | Working ✓ | Right A → CharacterController vertical velocity + gravity |
| World interact | Working ✓ | Left trigger → LMB + camera redirected to left controller aim |
| Crouch | Working ✓ | Left X → C key simulation |
| Notebook/map | Working ✓ | Right B → Tab key simulation |
| Flashlight | Working ✓ | Right stick click → middle mouse button |
| Inventory | Working ✓ | Left grip → X key simulation |

### VR controller button map
| Button | Action |
|--------|--------|
| Left stick | Locomotion (4 m/s walk) |
| Left stick click | Sprint toggle |
| Right stick X | Snap turn ±30° |
| Right stick click | Flashlight (middle mouse) |
| Left Y / menu | ESC (pause menu) |
| Left X | Crouch (C key) |
| Left trigger | World interact (LMB + left-hand aim) |
| Left grip | Inventory (X key) |
| Right trigger | UI click |
| Right A | Jump |
| Right B | Notebook/map (Tab key) |

### OpenXR actions bound in OpenXRManager.cs
- `_poseAction` — both hand aim poses
- `_triggerAction` — both triggers (right = UI click, left = interact)
- `_thumbAction` — both thumbsticks (left = locomotion, right = snap turn)
- `_gripAction` — both grips (left = inventory)
- `_menuButtonAction` — left Y + left menu button → ESC
- `_buttonAAction` — right A → jump
- `_buttonBAction` — right B → notebook
- `_buttonXAction` — left X → crouch
- `_thumbClickAction` — both thumbstick clicks (left = sprint, right = flashlight)

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

## CRITICAL: VR arm display — per-hand rotation and positioning (Phase 11)
The game's arm meshes are in pixel-space coordinates (hundreds of units) and oriented for flat-screen FPS.
VR arm display works by:
- Reparenting `LagPivot` to VROrigin, scaling `FirstPersonModels` by `ArmScale` (0.0002) for pixel→meter conversion
- Forcing `Arms` GO active, zeroing intermediate transforms every frame (Animator resets them)
- Per-hand rotation offsets to align FPS arm mesh with VR controller aim pose:
  - Right: `Quaternion.Euler(90, 90, 0)` — pitch + yaw
  - Left: `Quaternion.Euler(90, -90, 0)` — pitch + mirrored yaw
- Fist-offset positioning: arm positioned so the fist child (not elbow) aligns with the VR controller
- `ArmForwardOffset` (-0.25m) slides arm along controller forward axis so game hand matches real hand
- `PositionArmAtController(arm, fist, ctrlGO, rotOffset)` applies rotation then fist-offset then forward shift
- Called in both `UpdateHeldItemTracking` (frame update) and `ForceItemPositionPreRender` (pre-render)

## CRITICAL: Save/load same-scene reload — movement rediscovery (Phase 13)

When the player loads a save from the main menu (same Unity scene reused):
- The game camera is NOT destroyed/recreated — the "game camera lost" detection does NOT fire
- Save/load is detected by intercepting the "Continue" button click in `TryClickCanvas`
- **The GraphicRaycaster hits the child element (e.g. `'Border'`), NOT the button GO (`'Continue'`)** — must walk up the hierarchy (6 levels) to find the button name
- On trigger: null `_playerCC`, `_playerRb`, clear `_pauseMovementActive`, `_hasBeenGrounded`, `_jumpVerticalVelocity` — start grace period (180 frames)
- After grace: `_movementDiscoveryDone = false` BUT do NOT run `DiscoverMovementSystem()` immediately — player CC is still at origin `(0,1,0)`, game hasn't finished loading
- Rediscovery runs in the **per-frame gate** only once `_menuCanvasRef` and `_actionPanelCanvas` confirm the menu is fully hidden (load complete, player at correct save position)
- The per-frame gate normally checks `cullingMask != 0`, but our suppressed camera always has `cullingMask = 0` — bypass this check when `_playerCC == null` and menu is gone

## CRITICAL: ActionPanelCanvas grip-drag — do not make it draggable (Phase 13)

`ActionPanelCanvas` is the **anchor** for all other case board canvases. It must NOT be in the grip-draggable set:
- Other canvases store their position as ActionPanelCanvas-relative offsets (`_gripDragAnchorOffsets`)
- If ActionPanelCanvas itself is grip-dragged, it stores a self-referential (zero) offset
- On reopen: restore code waits for `_positionedCanvases.Contains(_actionPanelId)` before placing it — but ActionPanelCanvas was just removed for recentre → **infinite deferral deadlock**
- Fix: exclude `ActionPanelCanvas` from grip-drag candidates AND skip anchor-offset restore when `id == _actionPanelId`
- Draggable: `LocationDetailsCanvas`, `BioDisplayCanvas`, `WindowCanvas`, `MinimapCanvas`, etc.
- NOT draggable: `ActionPanelCanvas`, `CaseCanvas`

## Known canvas names (from LogOutput.log)
| Canvas | Size | Status |
|--------|------|--------|
| `MenuCanvas` | 1.20m | Working ✓ |
| `WindowCanvas` (notes/notebook) | 1.20m | Working ✓ |
| `ActionPanelCanvas` | 1.60m | Working ✓ (case board anchor, not grip-draggable) |
| `DialogCanvas` | 1.20m | Working ✓ |
| `BioDisplayCanvas` | 1.80m | Working ✓ |
| `GameCanvas`/HUD | 1.50m | Working ✓ |
| `TooltipCanvas` | 0.80m | Working ✓ |
| `PopupMessage` | 1.20m | Working ✓ (scale enforced each cycle) |
| `VRSettingsPanelInternal` | 1.60m | Working ✓ |
| `CaseCanvas` | 1.80m | Working ✓ (BG suppressed, pin board interactive) |
| `LocationDetailsCanvas` | 1.80m | Working ✓ (grip-draggable) |
| `MinimapCanvas` | 1.50m | Working ✓ (pan, location click, context menu) |

## Phase 12 status (COMPLETE — 2026-03-30)
- Case board pin drag (direct RectTransform manipulation) ✓
- Context menu world-lock (skip RepositionEveryFrame when ContextMenus active) ✓
- Save/load warp investigation started

## Phase 13 status (COMPLETE — 2026-03-30)
- Save/load button detection via hierarchy walk (`'Border'` hit → walk to `'Continue'`) ✓
- Movement rediscovery deferred until menu fully closes (load complete) ✓
- Gravity guard during load (`_hasBeenGrounded = false` on trigger) ✓
- ActionPanelCanvas grip-drag deadlock fixed ✓
- ActionPanelCanvas excluded from grip-draggable canvas set ✓
- Save-load warp eliminated ✓

## Phase 14 status (COMPLETE — 2026-04-03)
- A button → right-click on any aimed canvas (not just case board) ✓
- B button → middle-click drag on any aimed canvas ✓
- Jump/notebook suppressed when aiming at canvas ✓
- `_cursorTargetCanvas` field tracks nearest aimed-at canvas ✓
- HUD settings plan written (plan file at `C:\Users\blah6\.claude\plans\tender-wibbling-sunbeam.md`) — NOT YET IMPLEMENTED

## Phase 15 status (COMPLETE — 2026-04-03)
MinimapCanvas fully interactive:
- B button pans map (direct `content.anchoredPosition` — ExecuteEvents drag unavailable in IL2CPP) ✓
- Trigger click opens evidence note for aimed map location ✓
- A button right-click opens context menu (Plot Route, Open Evidence) ✓
- Floor navigation buttons (+/-) clickable in VR → rooms at load=1+ selectable ✓
- `_minimapCanvasRef` cached at scan; A/B and MapCursor raycast against it directly (bypasses `_cursorTargetCanvas` = WindowCanvas obstruction)
- `mapCursorNode` driven via `InverseTransformPoint` + `MapToNode` + `nodeMap` lookup (camera=null path broken)
- RectMask2D disable-all (removed `isScrollViewport` skip) — restored pinned note visibility ✓

### PARKED issues (see NotesWork.md for full analysis)
Three case board interaction issues were investigated but not resolved:
1. **Context menu aim dot / visual misalignment** — game sets `ContextMenu(Clone).localPosition` to screen coords AND `localScale.z=0` AND non-identity localRotation every frame. Our zeroing may not win the race, and z=0 scale may break rendering. See NotesWork.md § Problem 1.
2. **Opened pinned notes (WindowCanvas) misalignment** — aim dot doesn't match visual. Not yet diagnosed. See NotesWork.md § Problem 2.
3. **Pin proximity stealing** — when 2+ pins present, wrong pin targeted. Fixed coordinate space (anchoredPosition → localPosition), but issue persists — hitLocal from InverseTransformPoint may drift after context menu freeze/unfreeze. See NotesWork.md § Problem 3.

## CRITICAL: ContextMenus child transform — game writes every frame (discovered 2026-04-01)
```
CM diag: child 'ContextMenu(Clone)'
  localPos=(-960.00, 540.00, 0.00)   ← screen coords (1920×1080 / 2)
  localRot=(0.00, 284.11, 0.00)      ← non-zero Y rotation set by game
  localScale=(1.00, 1.00, 0.00)      ← Z scale = ZERO set by game
```
The game overwrites ContextMenu(Clone) position/rotation/scale every frame. Our zeroing is in 3 locations (Update enforce, PositionCanvases LateUpdate, ForceItemPositionPreRender). The timing race and Z-scale=0 may be causing residual misalignment. Future fix: reparent ContextMenu(Clone) away from game control, or intercept the game's positioning MonoBehaviour.

## CRITICAL: PinnedQuickMenu vs ContextMenu(Clone) in ContextMenus
The `ContextMenus` container under `TooltipCanvas` has two types of children:
- `ContextMenu(Clone)` — actual right-click context menu (should trigger freeze/aim lock)
- `PinnedQuickMenu(Clone)` — hover tooltip (should NOT trigger freeze)
Filter by `childName.StartsWith("ContextMenu")` to distinguish them (implemented 2026-04-01).

## CRITICAL: Awareness compass — 3D MeshRenderer system (discovered 2026-04-02)

The "awareness compass" (circle with directional arrows at screen-bottom) is **NOT a UI canvas**.
It is a 3D MeshRenderer system on `InterfaceController`:

```
compassContainer (public serialized GameObject)
  └── backgroundTransform (Transform — the ring MeshRenderer)
        └── spawned icons (Instantiate(PrefabControls.Instance.awarenessIndicator, backgroundTransform))
              ├── imageTransform  — faces camera (billboard)
              └── arrowTransform  — points toward threat
```

`InterfaceController.Update()` drives it each frame:
```csharp
backgroundTransform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up); // world-forward
awarenessIcon.spawned.transform.rotation = Quaternion.LookRotation(camPos - targetPos, Vector3.up);
awarenessIcon.imageTransform.rotation = CameraController.Instance.cam.transform.rotation;
```

**In VR it's invisible** because: `compassContainer` tracks the suppressed game camera; `backgroundTransform.rotation` = world-forward (not VR-cam-forward); `imageTransform` faces the controller direction (game cam = controller aim).

**Fix**: `UpdateCompass()` in `LateUpdate()` (after `InterfaceController.Update()`, before `_leftCam.Render()`):
1. `_compassContainer.position = headPos + headFwd * CompassDist + headUp * CompassYOffset`
2. `backgroundTransform.rotation = LookRotation(headFwd, headUp)`
3. For each icon: `imageTransform.rotation = _leftCam.transform.rotation`

Constants (tunable): `CompassDist = 1.2f`, `CompassYOffset = -0.55f`.
Cache: `_compassContainer` set in `DiscoverMovementSystem` step 5h from `_interfaceCtrl.compassContainer`.
Full analysis: `VRMod/ArrowWork.md`.

## CRITICAL: Minimap interaction — direct canvas raycast required (Phase 15)

`_cursorTargetCanvas` is unreliable for minimap when an evidence note (WindowCanvas) is open in front of it.
All minimap-specific paths use `_minimapCanvasRef` (cached at scan time) with a direct plane-raycast + bounds check:

```csharp
// In per-frame MapCursor update — always runs against _minimapCanvasRef regardless of _cursorTargetCanvas:
var mmPlane = new Plane(-_minimapCanvasRef.transform.forward, _minimapCanvasRef.transform.position);
if (mmPlane.Raycast(new Ray(rPos, rFwd), out float mmDist) && mmDist > 0f)
{
    Vector3 localXYZ = mapCtrl.overlayAll.InverseTransformPoint(rPos + rFwd * mmDist);
    Vector2 nodeCoords = mapCtrl.MapToNode(new Vector2(localXYZ.x, localXYZ.y));
    var key = new Vector3(Mathf.RoundToInt(nodeCoords.x), Mathf.RoundToInt(nodeCoords.y), mapCtrl.load);
    PathFinder.Instance.nodeMap.TryGetValue(key, out NewNode node);
    mapCtrl.mapCursorNode = node;
    _minimapLastKnownNode = node;  // trigger-click fallback (MapController resets node after our Update)
}
```

`rmbTarget` (A button) and `mmbTarget` (B button) both apply the same bounds-check override:
if ray hits `_minimapCanvasRef` rect → set target to `_minimapCanvasRef` regardless of `_cursorTargetCanvas`.

**Node floors**: `mapCtrl.load` = currently viewed floor on map. Floor 0 = streets + building entries.
Rooms are at load=1+. Floor buttons on MinimapCanvas are standard UI Buttons — clickable via trigger.
`MapToNode(localPos2D)` = `(Floor(x/32f), Floor(y/32f))`. nodeMap key = `Vector3(nodeX, nodeY, load)`.

**TryRightClickCanvas** computes a fresh node from the ray at click time (not stale `_minimapLastKnownNode`)
to ensure the context menu always opens for the node currently under the cursor.

## CRITICAL: GPU TDR — do NOT set Camera.main rotation in Update() every frame (discovered 2026-04-02)

Setting `_gameCamRef.transform.rotation = _leftControllerGO.transform.rotation` in `Update()` every frame
while grip is held drives expensive HDRP shadow/volumetric recalculations → GPU TDR (nvlddmkm.sys Blackwell).

**Safe locations for Camera.main rotation override:**
- **End of `Update()`** (after all movement logic) — safe; game's `InteractionRaycastCheck` runs in a
  later Update and reads it, so action text / interact raycasts use controller direction ✓
- **`LateUpdate` / post-`FrameEndStereo`** — safe; used for held item tracking ✓

**NEVER** set it inside a per-frame conditional block that runs for extended periods (e.g., "while grip held").
The `UpdateInventory()` grip-camera-redirect was removed as the TDR source (2026-04-02).

## History
- git `1be2b0e` — full VDXR-internal patching approach (archived checkpoint, do not rebase)
- git `346a6df` — **Phase 5 complete**: standard loader working, stereo image in headset
- **Phase 6 complete**: camera positioning ✓, head tracking ✓, UI canvases visible in VR ✓
- **Phase 7 complete**: controller pose ✓, trigger click ✓, cursor dot tracking ✓
- **Phase 8 complete** (git `12172ad`): VR settings panel ✓ — 4 tabs, all settings wired, FMOD audio, Settings button intercept
- **Phase 9 complete** (2026-03-29): All canvas/UI issues resolved — see HANDOVER.md for full details
- **Phase 10 complete** (2026-03-30): World graphics — flipYMode + HDRP Volume config fix (`546a4b5`)
- **Phase 11 complete** (2026-03-30): Movement — all controls bound, held item + arm tracking, VR arm display (`255dafc`)
- **Phase 12 complete** (2026-03-30): Case board pin drag, context menu world-lock (`1ee8e9d`)
- **Phase 13 complete** (2026-03-30): Save-load warp fix, ActionPanelCanvas grip-drag deadlock fix (`d0bd328`)
- **Phase 14 complete** (2026-04-01/02): A/B button canvas clicks, GPU TDR fix, action text fix, awareness compass VR fix, HUD settings plan written (`79fc4dd`, `f040235`)
- **Phase 15 complete** (2026-04-03): Minimap pan/click/right-click, pinned note visibility restored, `_minimapCanvasRef` direct raycast (`4648a66`)
