# VR-Native Settings Panel — Revised Plan

Date: 2026-03-27
Updated: motionBlur mapping corrected, write strategy split by family, resolution row deferred.

---

## Why pivot

See FAILURES.md. Twelve approaches to fix the built-in settings UI text failed. The settings labels live inside a `Scroll View / Viewport / Content` hierarchy — late TMP material rebuilds overwrite our patches after we apply them. Fighting that hierarchy is not converging.

---

## Critical constraint: canvas visibility in HDRP

VRCamera.cs documents a hard-won discovery: game canvases are visible in-headset, but standalone WorldSpace canvases created from scratch are NOT. Our panel must be created as `ScreenSpaceOverlay` first, then picked up by `ScanAndConvertCanvases` which converts it through the normal pipeline, giving it proper HDRP registration.

---

## Critical constraint: isolation from legacy mutation passes

VRCamera has multiple passes that run on every managed canvas:
`RescanCanvasAlpha`, `ForceUIZTestAlways`, `RelaxMenuCanvasClipping`, `RelaxMenuTextMaterials`, `ApplyReadableTextBoost`, fallback text duplication.

These must not touch our panel.

**Fix:** Add `HashSet<int> _ownedCanvasIds` to VRCamera. Register the panel canvas ID at creation time. Gate every mutation pass with an early ownership check — one check per pass, not scattered name comparisons.

---

## Game API (from Mono assembly research)

### Read a setting
```csharp
int val = PlayerPrefsController.Instance.GetSettingInt("aaMode");
string val = PlayerPrefsController.Instance.GetSettingStr("language");
```

### Write pattern varies by setting family — see table below

### Complete PlayerPrefs identifiers

**Graphics:** `aaMode`, `aaQuality`, `bloom`, `colourGrading`, `depthBlur`, `dithering`,
`dlssMode`, `drawDist`, `dynamicResolution`, `enableFrameCap`, `filmGrain`,
`flickeringLights`, `fpsfov`, `frameCap`, `lightDistance`, `motionBlur`,
`rainDetail`, `screenSpaceReflection`, `vsync`

**Audio:** `ambienceVolume`, `bassReduction`, `footstepsVolume`, `hyperacusis`,
`interfaceVolume`, `licensedMusic`, `masterVolume`, `music`, `musicVolume`,
`notificationsVolume`, `otherVolume`, `paVolume`, `weatherVolume`

**Controls:** `alwaysRun`, `controlAutoSwitch`, `controlHints`, `controlMethod`,
`controllerSensitivityX`, `controllerSensitivityY`, `controllerSmoothing`,
`forceFeedback`, `headBob`, `invertX`, `invertY`, `mouseSensitivityX`,
`mouseSensitivityY`, `mouseSmoothing`, `toggleRun`, `virtualCursorSensitivity`

**Gameplay:** `gameDifficulty`, `gameLength`, `language`, `uiScale`, `textspeed`,
`wordByWordText`

---

## Setting families and write paths

Settings fall into distinct families. Use the correct write path per family — the generic `OnToggleChanged` path is NOT valid for all of them.

### Family A — Bool via `OnToggleChanged` only (no dedicated Game setter)

`OnToggleChanged` writes PlayerPrefs then directly manipulates the HDRP Volume component on `SessionData.Instance`. **Guard: `SessionData.Instance` is only valid during active gameplay, not in the main menu. Check for null before calling `OnToggleChanged` for these.**

```csharp
var gs = PlayerPrefsController.Instance.gameSettingControls
    .Find(s => s.identifier == id);
gs.intValue = newBool ? 1 : 0;
if (SessionData.Instance != null)
    PlayerPrefsController.Instance.OnToggleChanged(id, false);
else
    PlayerPrefs.SetInt(id, gs.intValue); // save only; effect applies on next session load
```

Members: `bloom`, `motionBlur`, `colourGrading`, `filmGrain`, `flickeringLights`,
`rainDetail`, `dynamicResolution`

Note: `motionBlur` is a Volume component toggle (`SessionData.Instance.motionBlur.active`),
NOT a call to `SetDepthBlur()`. The two are entirely separate effects.

### Family B — Bool with dedicated `Game.Instance` setter

These have proper Set methods — call them directly. Also write PlayerPrefs to persist across restarts.

```csharp
Game.Instance.SetXxx(newBool);
PlayerPrefs.SetInt(id, newBool ? 1 : 0);
PlayerPrefs.Save();
```

| Identifier | Setter |
|---|---|
| `vsync` | `Game.Instance.SetVsync(bool)` |
| `depthBlur` | `Game.Instance.SetDepthBlur(bool)` |
| `dithering` | `Game.Instance.SetDithering(bool)` |
| `screenSpaceReflection` | `Game.Instance.SetScreenSpaceReflection(bool)` |

### Family C — Enum/int with dedicated `Game.Instance` setter

```csharp
Game.Instance.SetXxx(newIndex);
PlayerPrefs.SetInt(id, newIndex);
PlayerPrefs.Save();
```

| Identifier | Setter | Range |
|---|---|---|
| `aaMode` | `Game.Instance.SetAAMode(int)` | 0=off, 1=SMAA, 2=TAA, 3=DLSS |
| `aaQuality` | `Game.Instance.SetAAQuality(int)` | 0–2 |
| `fpsfov` | `Game.Instance.SetFOV(int)` | 50–120 |

### Family D — Float scalar with dedicated `Game.Instance` setter

```csharp
Game.Instance.SetXxx(newFloat);
PlayerPrefs.SetFloat(id, newFloat);
PlayerPrefs.Save();
```

| Identifier | Setter | Suggested steps |
|---|---|---|
| `lightDistance` | `Game.Instance.SetLightDistance(float)` | 0.5, 0.75, 1.0, 1.5, 2.0 |

### Family E — Compound (frame cap)

```csharp
// Toggle:
Game.Instance.SetEnableFrameCap(newBool);
PlayerPrefs.SetInt("enableFrameCap", newBool ? 1 : 0);
// Value:
Game.Instance.SetFrameCap(newInt);          // → Application.targetFrameRate
PlayerPrefs.SetInt("frameCap", newInt);
PlayerPrefs.Save();
```

Suggested cap values: 30, 60, 90, 120, 144, 165, 240, Unlimited (0).

### Family F — DLSS (special controller)

```csharp
DynamicResolutionController.Instance.DLSSEnabled = newBool;
DynamicResolutionController.Instance.SetDLSSQualityMode(DLSSQuality.Balanced);
// DLSSQuality enum: MaximumPerformance, Balanced, MaximumQuality, UltraPerformance
PlayerPrefs.SetInt("dlssMode", (int)newMode);
PlayerPrefs.Save();
```

### Family G — Resolution (deferred to Phase 3)

Raw PlayerPrefs keys: `width`, `height`, `refresh`, `fullscreen`. Not wrapped by `gameSettingControls`.
```csharp
Screen.SetResolution(w, h, (FullScreenMode)fs, refresh);
// Must validate chosen (w, h, refresh) against Screen.resolutions before exposing
```
Defer until Phase 3. Do not build this row in Phase 2.

---

## Phases

### Phase 0: Visibility proof (hard gate — do not skip)

One build, one test before writing any real panel code.

Steps:
1. In VRCamera, create a `ScreenSpaceOverlay` canvas named `VRSettingsPanelInternal`
2. Register its instance ID in `_ownedCanvasIds` immediately
3. Add one `Image` (dark background) and one `TextMeshProUGUI` ("VR Settings — Test")
4. Set `sortingOrder = 50` (above game canvases, below cursor at 100)
5. After conversion, assert that a `GraphicRaycaster` exists on the canvas — add one if missing
6. Let `ScanAndConvertCanvases` pick it up — ownership check keeps mutation passes off it
7. Run and verify

**Pass criteria — all must hold:**
- Text is readable in-headset without exposure hacks
- Panel stays in stable position after Home-key recenter
- VR cursor can interact with it (a test button responds)
- No mutation pass logs changes against the panel canvas

**Failure path:** if text is dark, disable HDRP post-processing on eye cameras. Do not proceed to Phase 1 until all four criteria pass.

---

### Phase 1: Skeleton panel

Extract the test canvas into `VRSettingsPanel.cs`.

Deliver:
- `SoDVR/VR/VRSettingsPanel.cs` — owns canvas, all layout, all settings logic
- Dark semi-transparent `Image` background
- Title: "VR Settings"
- Close button
- Two tab buttons: **Graphics** | **General**
- 3–4 hardcoded rows per tab as layout proof (no scroll view)
- F10 toggles visibility
- Canvas registered in `_ownedCanvasIds` at creation time
- `GraphicRaycaster` asserted after conversion (not assumed)

Architecture:
- `VRSettingsPanel.Init()` creates the `ScreenSpaceOverlay` canvas and all elements
- VRCamera calls `Init()`, adds canvas ID to `_ownedCanvasIds`, wires F10 key
- VRCamera scans and converts the canvas normally
- `VRSettingsPanel` exposes `Show()` / `Hide()` / `Toggle()`

VRCamera additions: ≤30 lines. Panel owns everything else.

---

### Phase 2: Graphics tab bindings

Wire the Graphics tab. Use the correct family write path for each row.

**Row list — Graphics tab:**

| Label | Identifier | Family | Control |
|-------|-----------|--------|---------|
| VSync | `vsync` | B | Toggle |
| Depth Blur | `depthBlur` | B | Toggle |
| Dithering | `dithering` | B | Toggle |
| Screen Space Reflections | `screenSpaceReflection` | B | Toggle |
| Motion Blur | `motionBlur` | A (SessionData guard) | Toggle |
| Bloom | `bloom` | A (SessionData guard) | Toggle |
| Colour Grading | `colourGrading` | A (SessionData guard) | Toggle |
| Film Grain | `filmGrain` | A (SessionData guard) | Toggle |
| Flickering Lights | `flickeringLights` | A (SessionData guard) | Toggle |
| AA Mode | `aaMode` | C | Prev/Next |
| AA Quality | `aaQuality` | C | Prev/Next |
| DLSS | `dlssMode` | F | Prev/Next |
| Light Distance | `lightDistance` | D | Prev/Next |
| Frame Cap | `enableFrameCap` + `frameCap` | E | Toggle + Prev/Next |

**Verify Phase B setters before wiring the full tab.** Wire one Family B row first (e.g. `vsync`), confirm it works in-headset, then add the rest.

---

### Phase 3: General tab + resolution

**Row list — General tab:**

| Label | Identifier | Family | Control |
|-------|-----------|--------|---------|
| FOV | `fpsfov` | C | Prev/Next (50–120) |
| Head Bob | `headBob` | A | Toggle |
| Rain Detail | `rainDetail` | A | Toggle |
| Dynamic Resolution | `dynamicResolution` | A | Toggle |
| UI Scale | `uiScale` | A | Prev/Next |
| Text Speed | `textspeed` | A | Prev/Next |
| Word-by-word Text | `wordByWordText` | A | Toggle |
| Draw Distance | `drawDist` | A | Prev/Next |

**Resolution row (Family G):**
- Build a candidate list from `Screen.resolutions` filtered to the current display
- Present as Prev/Next with label showing `W×H @ RHz`
- Apply behind an explicit "Apply" button — no live preview
- Add only after all other rows are working

---

### Phase 4: Polish

- Suppress original settings scroll-view while VR panel is open
- Focus/highlight on interactive elements
- Home key re-centres panel with other canvases
- Clean up diagnostic logging

---

## UI design rules

### Layout
- Canvas reference resolution: 900×700
- Two tabs: Graphics | General — switch at top
- Single column per tab, label left-aligned, control right-aligned
- Row height ~60px, 10px padding between rows
- No scroll view in Phase 1 or 2

### Typography
- Labels: font size 36+
- Values/controls: font size 28+
- White text on dark background (#1A1A2E)
- Tab headers: accent colour

### Contrast
- Background: dark, 85% opaque
- Text: white or near-white
- Active/focused control: bright accent border
- Disabled items: 50% alpha

### Interaction
- Use existing VR cursor/ray system
- Large hit targets: minimum 200×50px
- `GraphicRaycaster` asserted on panel canvas after conversion (not assumed)

---

## File plan

| File | Change |
|------|--------|
| new: `SoDVR/VR/VRSettingsPanel.cs` | Canvas creation, layout, all settings bindings |
| touch: `SoDVR/VR/VRCamera.cs` | `_ownedCanvasIds`, `Init()` call, ownership gates, F10 key |
| touch: `SoDVR/Plugin.cs` | Only if earlier lifecycle than VRCamera is needed |

---

## Risks and mitigations

| Risk | Mitigation |
|------|-----------|
| Phase 0 text is dark | Disable HDRP post-process on eye cameras; do not build Phase 1 until fixed |
| `PlayerPrefsController.Instance` null at panel-open | Null guard; show "Settings unavailable"; retry on next open |
| `SessionData.Instance` null (main menu) for Family A | Already in write pattern — save to PlayerPrefs only, apply on next session load |
| `gameSettingControls.Find()` returns null | Skip that row silently; log once |
| Panel canvas mutated by legacy passes | `_ownedCanvasIds` gate on all mutation passes |
| Resolution apply causes flicker/crash | Behind explicit Apply button; validated against `Screen.resolutions` first |

---

## Order of work

1. **Phase 0** — one build, one test. All four criteria must pass before continuing.
2. **Phase 1** — skeleton with two tabs, `GraphicRaycaster` asserted.
3. **Phase 2** — wire one Family B row first to validate, then full Graphics tab.
4. **Phase 3** — General tab; resolution row last.
5. **Phase 4** — polish.

Do not skip Phase 0.
