# SoDVR — Technical Handover

**Date**: 2026-03-28
**Phase**: 9 (GUI & graphics polish) — Phase 10 will be movement

---

## IN-FLIGHT BUGS (next session must address these before movement work)

### Bug 1 — Right-eye jitter (UNCONFIRMED FIX deployed 2026-03-28)

**Symptom**: Left eye renders smoothly; right eye has visible per-frame jitter/shimmer.

**Last confirmed state**: `GL.Flush()` between eyes was previously confirmed fixed, but jitter returned after canvas whitelist code was added. Canvas code is CPU-only so a logical link is unlikely — more probable that the prior test was on the lighter main-menu state.

**Fix deployed** (not yet tested): Added `GL.InvalidateState()` before left render AND between the two renders:
```csharp
GL.invertCulling = true;
GL.InvalidateState();   // purge stale state from previous frame's P/Invoke CopyResource calls
_leftCam.Render();
GL.Flush();             // submit left-eye D3D11 work to GPU
GL.InvalidateState();   // clean Unity's GL cache before right-eye starts
_rightCam.Render();
GL.invertCulling = false;
```

**If still jittering after this fix**: swap render order (right first, then left) to determine whether jitter is always in the second-rendered camera (HDRP state contamination) or always in the right camera regardless of order.

---

### Bug 2 — Y/menu button multi-fire

**Symptom**: A single Y press fires `Menu button → ESC` multiple times (log lines 477, 481, 487, 496 on 2026-03-28 run).

**Current guard** (in `UpdateMenuButton`):
```csharp
private void UpdateMenuButton()
{
    if (Time.realtimeSinceStartup < _menuBtnCooldownUntil) return;
    OpenXRManager.GetMenuButtonState(out bool menuNow);
    if (_menuBtnNeedsRelease)
    {
        if (!menuNow) _menuBtnNeedsRelease = false;
        return;
    }
    if (!menuNow) return;
    _menuBtnNeedsRelease = true;
    _menuBtnCooldownUntil = Time.realtimeSinceStartup + 1.5f;
    FireMenuButton();
}
```

**Hypothesis**: `GetMenuButtonState` may return stale/cached values during the brief pause-state that ESC triggers (Unity may stall `Update()` scheduling, causing `realtimeSinceStartup` to jump unexpectedly), or the ESC keypress itself opens a dialog that fires a second ESC.

**Recommended fix**: Log `realtimeSinceStartup` at each call to `UpdateMenuButton` to confirm whether the cooldown is actually being evaluated when the extra fires happen.

---

### Bug 3 — Trigger click multi-fire

**Symptom**: Holding the trigger fires a click every frame. Log shows dozens of "Trigger click: 'Background' on 'MenuCanvas'" entries in a single hold.

**Root cause**: No release-latch or cooldown on the trigger path — `TryClickCanvas` is called from `UpdateControllerPose` every `Update()` frame the trigger is pressed.

**Fix needed**: Add `_triggerNeedsRelease` + `_triggerLastFireTime` fields and apply the same pattern as `UpdateMenuButton`:
```csharp
if (_triggerNeedsRelease)
{
    if (!triggerPressed) _triggerNeedsRelease = false;
    return;
}
if (!triggerPressed) return;
_triggerNeedsRelease = true;
TryClickCanvas(...);
```

---

### Bug 4 — "Blue box" on ESC (LIKELY FIXED, unconfirmed)

**Symptom**: A translucent blue rectangle appeared momentarily when pressing Y to open/close the pause menu. Cause: `ActionPanelCanvas` briefly goes inactive→active during the ESC transition, triggering a canvas reposition that placed it at the player's face.

**Fix deployed**: Canvas reposition whitelist (`s_recentreOnActivate`) now only contains `"MenuCanvas"` and `"DialogCanvas"`. `ActionPanelCanvas` and other HUD canvases are never auto-repositioned on activate. Confirmed via log that first placement of ActionPanelCanvas occurs once only (not on every ESC).

**Status**: Likely fixed but not yet confirmed by user with the latest build.

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
  - NOT in `_ownedCanvasIds` → `RescanCanvasAlpha` applies ZTest Always material patch
  - `DontDestroyOnLoad` — survives loading→menu scene transition
  - Dot moves via `anchoredPosition` (2D projection onto canvas plane)
  - `_cursorAimDepth` tracks depth of nearest active aimed-at canvas (hidden canvases excluded via `activeSelf` check)

### Phase 8 — VR Settings Panel (COMPLETE, git `12172ad`)
- `SoDVR/VR/VRSettingsPanel.cs` owns canvas, layout, all settings logic ✓
- **F10** or **main menu Settings button** opens/closes the panel ✓
- 4 tabs fully wired:
  - **Graphics**: VSync, Depth Blur, Dithering, Screen Space Refl., AA Mode/Quality, Dynamic Resolution, DLSS, Frame Cap, UI Scale
  - **Audio**: Master volume (FMOD bus:/), per-channel VCA volumes (7 channels), Music toggle, Licensed Music, Bass Reduction, Hyperacusis
  - **Controls**: Always Run, Toggle Run, Control Auto-Switch, Control Hints, Invert X/Y, Force Feedback, mouse/controller sensitivity & smoothing, virtual cursor sensitivity
  - **General**: FOV, Head Bob, Rain Detail, Draw Distance, Game Difficulty, Text Speed, Word-by-Word Text
- MenuCanvas hidden (canvas.enabled=false + GraphicRaycaster.enabled=false) while VR panel open ✓
  - State-tracked (`_menuCanvasHidden`) to avoid per-frame material rebuild crash
- Settings button intercept via `TryClickCanvas` GO instance ID comparison ✓
- Sensitivity/smoothing floats are PlayerPrefs-only (restart required) — accepted limitation

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

### Confirmed root cause
WorldSpace UI rendered by the HDRP overlay cameras is darkened by HDRP's scene auto-exposure. The scene cameras compute EV≈8–12 for the city environment (multiplier ≈ 1/256 to 1/4096). The UI overlay cameras inherit this exposure rather than computing their own, so unlit white vertex colours appear near-black in the headset.

### What was tried and CONFIRMED NOT WORKING
| Approach | Result |
|----------|--------|
| `FrameSettingsField.ExposureControl = false` per camera | Does not persist — `ExposureOff` always reads back `False`. Not overridable in HDRP 12 per-camera FS. |
| `FrameSettingsField.Tonemapping = false` | **Works** — `TonemapOff=True` confirmed on all cameras. |
| VRExposureOverride Volume (EV=0 Fixed, Tonemapping=None, layer 31, priority 1000) | Volume is **not applied** — confirmed: changing `fixedExposure` to `-10` produced zero visible change. |

### Recommended fix approaches (next session that tackles this)

**Option A — `FrameSettingsField.Postprocess` master toggle (try first, 5 min)**
Add `FrameSettingsField.Postprocess` to `s_VrDisabledFields`. If this field persists (unlike ExposureControl), it disables ALL post-processing including exposure.

**Option B — Overwrite HDRP's exposure texture via CommandBuffer**
HDRP stores the current frame's exposure as a 1×1 RFloat texture in `HDCamera` history.
A CommandBuffer can overwrite this to neutral before the post-process pass.

**Option C — Remove UI overlay cameras; add layer 5 to scene cameras**
Since `TonemapOff=True` works, test whether exposure alone makes text unreadable or if the combined effect is tolerable. Simplifies architecture.

**Option D — Read live exposure and boost vertex colours to compensate**
`HDCamera.GetOrCreate(_leftCam)` → current exposure value → `boost = 1/exposure` → apply to TMP vertex colours each scan cycle (rate-limited).

---

## IL2CPP Interop Pitfalls (full list)

| Pitfall | Detail |
|---------|--------|
| `GetComponentInParent<Button>()` | Always returns null in IL2CPP — walk up transform hierarchy manually |
| `AddListener` on `new Button.ButtonClickedEvent()` | Silently fails in IL2CPP — use `TryClickCanvas` GO instance ID intercept pattern instead |
| `btn.GetInstanceID()` | Returns COMPONENT id, not GO id — use `btn.gameObject.GetInstanceID()` for GO comparisons |
| `btn.onClick = new Button.ButtonClickedEvent()` | DOES kill persistent (prefab-baked) listeners — use to suppress game button default behaviour |
| `Graphic.color` vs `mat.color` | Separate in HDRP UI — must set both for transparency. `mat.color.a` alone does nothing. |
| `g.color = ...` per-frame | Calls `SetVertexDirty()` → canvas mesh rebuild → serious lag. Rate-limit or avoid. |
| `Resources.FindObjectsOfTypeAll<Canvas>()` | More reliable than `FindObjectsOfType` in IL2CPP |
| `TMP_Text.color` vs `Graphic.color` | TMP overrides base `Graphic.color` — must cast to `TMP_Text` and set `.color` directly |
| `FrameSettingsField.ExposureControl` | Cannot be overridden per-camera in HDRP 12 IL2CPP — bit never persists |
| `AddComponent<RectTransform>()` on new GO | Returns null — GO already has Transform. Use `AddComponent<Image>()` first, then `GetComponent<RectTransform>()` |
| `DontDestroyOnLoad` on VRMod GOs | **Required** — loading→menu scene transition destroys all non-persistent objects |
| `canvas.enabled` per-frame toggle | Causes Unity to rebuild all canvas materials every frame → floods material instances → crash. Always state-track changes. |
| `AudioListener.volume` | Has zero effect on FMOD audio — use `FMODUnity.RuntimeManager.GetBus("bus:/").setVolume(float)` |

---

## FMOD Audio API (confirmed working)

```csharp
// Master volume
FMODUnity.RuntimeManager.GetBus("bus:/").setVolume(vol);   // float 0..1
PlayerPrefs.SetFloat("masterVolume", vol); PlayerPrefs.Save();

// Per-channel VCA (same pattern for all)
FMODUnity.RuntimeManager.GetVCA("vca:/Soundtrack").setVolume(vol);
PlayerPrefs.SetFloat("musicVolume", vol); PlayerPrefs.Save();

// Music toggle (updates in-memory GameSetting + PlayerPrefs)
SetFamilyA("music", v);   // via PlayerPrefsController.OnToggleChanged

// Bass reduction / Hyperacusis (triggers FMOD snapshot internally)
Game.Instance.SetBassReduction(v ? 1 : 0);
Game.Instance.SetHyperacusisFilter(v ? 1 : 0);
```

VCA paths confirmed from Master Bank.strings.bank:
`vca:/Soundtrack`, `vca:/Ambience`, `vca:/Weather`, `vca:/Footsteps`, `vca:/Notifications`, `vca:/PA System`, `vca:/Other SFX`

FMODUnity.dll is in `BepInEx/interop/` — referenced in `SoDVR.csproj`.

---

## Settings Panel Button Intercept Pattern

The game's Settings button has a persistent (prefab-baked) onClick listener that `RemoveAllListeners()` cannot remove. Pattern to suppress it and replace with our handler:

```csharp
// 1. Kill persistent listener by replacing the entire event object
btn.onClick = new Button.ButtonClickedEvent();
// 2. Store the button GO's instance ID (NOT btn.GetInstanceID() — that's component id)
_menuSettingsBtnId = btn.gameObject.GetInstanceID();

// 3. In TryClickCanvas, before ExecuteEvents:
var tr = go?.transform;
for (int i = 0; i < 5 && tr != null; i++)
{
    if (tr.gameObject.GetInstanceID() == _menuSettingsBtnId)
    {
        VRSettingsPanel.Toggle();
        return;
    }
    tr = tr.parent;
}
```

---

## Phase 9 — GUI & Graphics Polish (CURRENT)

### Priority order
1. **Confirm right-eye jitter fix** (GL.InvalidateState — deployed, test first)
2. **Trigger click debounce** (add `_triggerNeedsRelease` latch)
3. **Y-button multi-fire** (diagnose with realtimeSinceStartup logging)
4. **UI brightness** (HDRP auto-exposure — see §BLOCKING ISSUE: UI Brightness)
5. **Confirm blue-box fix** (canvas whitelist — deployed, needs user confirmation)

---

## Phase 10 — Movement (DEFERRED)

### Goal
Thumbstick locomotion and snap-turn so the player can move around in VR without keyboard.

### Required OpenXR bindings (add to OpenXRManager.cs)
- Left thumbstick X/Y → character movement (forward/back/strafe)
- Right thumbstick X → snap turn (configurable degrees, e.g. 30°)
- Left controller grip or A button → jump / interact
- Right controller B button → sprint (or map to alwaysRun)

### Architecture approach
- Add left controller pose tracking (same pattern as right controller in Phase 7)
- Add thumbstick action bindings in `CreateActionSets()` / `SuggestBindings()` / `SyncActions()`
- In `Update()`, read thumbstick values and call into Rewired player input OR directly move the character transform
- Snap turn: rotate VROrigin around Y by ±N degrees when thumbstick crosses threshold; add dead-zone and cooldown to prevent continuous spinning

### Key unknowns
- How the game's character controller works (Rewired vs direct transform) — may need to read `Plugin.cs` and find the player GO
- Whether injecting movement via Rewired player `SetAxisValue` or via direct transform is more compatible with the game's physics

---

## File Locations

| File | Purpose |
|------|---------|
| `VRMod/SoDVR/OpenXRManager.cs` | OpenXR init, session, swapchain, frame loop, action sets |
| `VRMod/SoDVR/VR/VRCamera.cs` | Stereo render loop, UI canvas management, controller click |
| `VRMod/SoDVR/VR/VRSettingsPanel.cs` | VR Settings panel — 4 tabs, all settings wired |
| `VRMod/SoDVR/Plugin.cs` | BepInEx entry point — do not change |
| `VRMod/PLAN-2.md` | Phase 8 plan + full settings API reference |
| `BepInEx/plugins/SoDVR.dll` | Deployed plugin (flat layout only) |
| `BepInEx/LogOutput.log` | Runtime log |

---

## Phase Roadmap

- [x] Phase 5: Standard loader → real swapchain textures → stereo image in headset (`346a6df`)
- [x] Phase 6: Camera positioning, head tracking, UI canvases visible in VR
- [x] Phase 7: Controller pose + trigger click + cursor dot visible on all screens
- [x] **Phase 8**: VR Settings Panel — 4 tabs, all settings wired, FMOD audio, Settings button intercept (`12172ad`)
- [ ] **Phase 9 (current)**: GUI & graphics polish — right-eye jitter, trigger debounce, Y-button multi-fire, UI brightness
- [ ] Phase 10: Movement — thumbstick locomotion, snap turn, jump, interact bindings
- [ ] Phase 11: Comfort options (vignette, snap-turn degrees, IPD, dominant hand)
- [ ] Phase 12: Left controller full tracking + dual-hand interactions
- [ ] Phase 13: Final polish, performance tuning
