# Phase 1 Implementation Plan: VR Settings Panel Skeleton

Date: 2026-03-27

---

## Overview

Extract the Phase 0 test canvas block from `BuildCameraRig` in `VRCamera.cs` and replace it
with a proper skeleton panel in a new file `SoDVR/VR/VRSettingsPanel.cs`.

---

## New file: `SoDVR/VR/VRSettingsPanel.cs`

### Public API

```csharp
public static class VRSettingsPanel
{
    public static int    CanvasInstanceId { get; }          // registered in _ownedCanvasIds by caller
    public static GameObject? RootGO      { get; }

    // Called once from BuildCameraRig. Registers canvas ID into ownedCanvasIds before returning.
    public static GameObject Init(HashSet<int> ownedCanvasIds);

    public static void Show();    // activates RootGO, clears from _positionedCanvases via callback
    public static void Hide();
    public static void Toggle();
    public static void Destroy(); // called from VRCamera.OnDestroy
}
```

`Init` also accepts a `Action<int> removeFromPositioned` callback so `Show()` can
trigger re-centring without coupling to VRCamera internals.

### Canvas setup (same rules as cursor canvas)

```
new GameObject("VRSettingsPanelInternal")
  layer = UILayer (5)
  DontDestroyOnLoad                               // MANDATORY — survives scene transition
  AddComponent<Canvas>()
    renderMode  = ScreenSpaceOverlay
    sortingOrder = 50
  _ownedCanvasIds.Add(canvas.GetInstanceID())     // BEFORE scan fires
  AddComponent<CanvasScaler>()
    ScaleWithScreenSize, referenceResolution = (900, 700)
```

### Layout hierarchy

```
VRSettingsPanelInternal  (Canvas, sortOrder=50)
  Background              Image  full-stretch  #141422 88% opaque  raycastTarget=false
  Header
    Title                 TMP    "VR Settings"  font=44  white  centre-top  raycastTarget=false
    CloseButton           Image  top-right 80×50  Button component
      CloseLabel          TMP    "✕"  font=32  white  raycastTarget=false
  TabRow
    GraphicsTabBtn        Image  Button  200×50  accent colour when active
      GraphicsTabLabel    TMP    "Graphics"  font=30  white  raycastTarget=false
    GeneralTabBtn         Image  Button  200×50
      GeneralTabLabel     TMP    "General"   font=30  white  raycastTarget=false
  GraphicsPane            RectTransform  fills remaining area below tab row
    Row0 … Row3           (4 placeholder rows — see Row layout below)
  GeneralPane             RectTransform  same size, initially inactive
    Row0 … Row3           (4 placeholder rows)
```

### Row layout (placeholder, no wiring)

Each row is a horizontal strip 60px tall:
```
RowN
  RowLabel   TMP  left-aligned   "Label N"   font=32  white  raycastTarget=false
  RowValue   TMP  right-aligned  "[---]"     font=28  grey   raycastTarget=false
```

Graphics tab placeholder labels: "VSync", "Depth Blur", "Dithering", "Screen Space Refl."
General tab placeholder labels:  "FOV", "Head Bob", "Rain Detail", "Draw Distance"

### Tab switching logic

- `GraphicsPane.SetActive(true)` / `GeneralPane.SetActive(false)` and vice-versa
- Tab button colours: active = `#2E6DB4`, inactive = `#2A2A40`
- Called by `Button.onClick` listeners on each tab button
- Default active tab: Graphics

### AddComponent rules (IL2CPP constraints)

- NEVER `AddComponent<RectTransform>()` — returns null if Transform already exists.
  Always `AddComponent<Image>()` (or another Component) first, then `GetComponent<RectTransform>()`.
- NEVER `GetComponentInParent<Button>()` — always returns null in IL2CPP.
  Cache the Button reference at creation time.

---

## Changes to `VRCamera.cs` (≤30 lines net)

### 1. Remove Phase 0 block

Delete lines 550–632 (the entire Phase 0 try/catch block that creates `VRSettingsPanelInternal`).

### 2. Replace with Phase 1 call (inside `BuildCameraRig`, same location)

```csharp
// Phase 1: VR Settings Panel
try
{
    _settingsPanelGO = VRSettingsPanel.Init(
        _ownedCanvasIds,
        id => _positionedCanvases.Remove(id));
    Log.LogInfo("[VRCamera] VRSettingsPanel.Init complete.");
}
catch (Exception ex)
{
    Log.LogWarning($"[VRCamera] VRSettingsPanel.Init failed: {ex.Message}");
}
```

### 3. Update F10 handler (in `Update`)

Replace the ~10-line F10 block with:

```csharp
if (Input.GetKeyDown(KeyCode.F10))
    VRSettingsPanel.Toggle();
```

(`Toggle` calls the `removeFromPositioned` callback internally on Show.)

### 4. Update `OnDestroy`

Replace:
```csharp
if (_settingsPanelGO != null) try { Destroy(_settingsPanelGO); } catch { }
```
With:
```csharp
VRSettingsPanel.Destroy();
```

---

## Constraints checklist

| Constraint | Where enforced |
|---|---|
| Canvas created as ScreenSpaceOverlay first | `VRSettingsPanel.Init` |
| `DontDestroyOnLoad` on root GO | `VRSettingsPanel.Init` |
| `_ownedCanvasIds.Add` before scan fires | `VRSettingsPanel.Init` (passed in) |
| `CanvasScaler` reference resolution (900,700) | `VRSettingsPanel.Init` |
| Never `AddComponent<RectTransform>()` | all UI construction in `VRSettingsPanel` |
| Never `GetComponentInParent<Button>()` | N/A — buttons referenced at creation |
| VRCamera additions ≤30 lines | enforced by plan (3 call sites only) |
| No real settings wiring yet | all RowValue text stays "[---]" |

---

## Files touched

| File | Change |
|---|---|
| `SoDVR/VR/VRSettingsPanel.cs` | **NEW** — all panel code |
| `SoDVR/VR/VRCamera.cs` | Replace Phase 0 block + F10 handler + OnDestroy (~net ≤30 lines) |

---

## Pass criteria (same as Phase 0, with tab interaction added)

- Panel appears in headset when F10 pressed ✓
- Dark background visible, title "VR Settings" readable ✓
- Close button hit-tested by cursor ray ✓
- "Graphics" / "General" tab buttons switch panes ✓
- Panel stays in position after Home recenter; re-centres when toggled ✓
- No mutation pass logs changes against `VRSettingsPanelInternal` ✓
- Placeholder rows visible in both tabs ✓

---

## Complete Settings API Reference (confirmed from mono Assembly-CSharp, 2026-03-28)

### Confirmed Game.Instance setters

| Method | Params | Notes |
|---|---|---|
| `SetVsync` | `bool` | |
| `SetDepthBlur` | `bool` | |
| `SetDithering` | `bool` | |
| `SetScreenSpaceReflection` | `bool` | |
| `SetAAMode` | `int` | 0=Off 1=SMAA 2=TAA 3=DLSS |
| `SetAAQuality` | `int` | 0–2 |
| `SetFOV` | `int` | 50–120 |
| `SetLightDistance` | `float` | |
| `SetDrawDistance` | `float` | |
| `SetEnableFrameCap` | `bool` | |
| `SetFrameCap` | `int` | |
| `SetFlickingLights` | `bool` | NOTE: "Flicking" not "Flickering" |
| `SetUIScale` | `int` | 0=Small 1=Normal 2=Large |
| `SetGameDifficulty` | `int` | 0=Easy 1=Normal 2=Hard 3=Extreme |
| `SetGameLength` | `int, bool, bool, bool` | SKIP — game-start function, not a settings setter |
| `SetAllowLicensedMusic` | `bool` | |
| `SetBassReduction` | `int` | 0 or 1 |
| `SetHyperacusisFilter` | `int` | 0 or 1 |

**Not on Game:** `SetDLSSMode`, `SetDynamicResolution`, `SetBassReduction` (actually IS on Game), `SetHeadBob`, `SetAlwaysRun`, `SetInvertX/Y`, `SetControllerSensitivity`, `SetMouseSensitivity`

### DynamicResolutionController.Instance methods

| Method | Params |
|---|---|
| `SetDynamicResolutionEnabled` | `bool` |
| `SetDLSSEnabled` | `bool` |
| `SetDLSSQualityMode` | `DLSSQuality` enum |

`DLSSQuality` enum: `MaximumPerformance=0, Balanced=1, MaximumQuality=2, UltraPerformance=3`

### PlayerPrefsController

- `OnToggleChanged(string id, bool value, MonoBehaviour caller)` — IL2CPP wrapper allows `caller` to be omitted (defaults null); use for Family A settings
- `GetSettingInt(string id)` — reads int setting
- `GetSettingStr(string id)` — reads string setting
- `gameSettingControls` — `List<GameSetting>` — iterate to find by `identifier` field
- **No `SaveSettings` method** — use `PlayerPrefs.Save()` directly

### String-stored settings (PlayerPrefs.GetString / SetString, NOT GetInt/SetInt)

| Key | Values |
|---|---|
| `gameDifficulty` | `"Easy"`, `"Normal"`, `"Hard"`, `"Extreme"` |
| `gameLength` | `"Very Short"`, `"Short"`, `"Normal"`, `"Long"`, `"Very Long"` |
| `language` | locale string |

### Settings with NO dedicated setter (PlayerPrefs-only, takes effect on reload)

These do not appear on Game, PlayerPrefsController, or any controller as Set* methods.
Write to PlayerPrefs + Save. Effect applies when `LoadPlayerPrefs` runs (next game start or scene load).

Controls:
- `alwaysRun` (bool) — `SetFamilyA` (in gameSettingControls list)
- `toggleRun` (bool) — `SetFamilyA`
- `controlAutoSwitch` (bool) — `SetFamilyA`
- `controlHints` (bool) — `SetFamilyA`
- `invertX`, `invertY` (bool) — `SetFamilyA`
- `forceFeedback` (bool) — `SetFamilyA`
- `mouseSensitivityX/Y` (float) — PlayerPrefs.SetFloat only
- `controllerSensitivityX/Y` (float) — PlayerPrefs.SetFloat only
- `mouseSmoothing` (int) — PlayerPrefs.SetInt only
- `controllerSmoothing` (int) — PlayerPrefs.SetInt only
- `virtualCursorSensitivity` (float) — PlayerPrefs.SetFloat only

General/misc:
- `headBob` (bool) — `SetFamilyA`
- `rainDetail` (bool) — `SetFamilyA` (field not directly found; OnToggleChanged handles it)
- `wordByWordText` (bool) — `SetFamilyA`
- `textspeed` (int: 0=Slow 1=Normal 2=Fast) — PlayerPrefs.SetInt only

### Tab structure (4 tabs)

0 = Graphics, 1 = Audio, 2 = Controls, 3 = General
Tab button width: 130px, positions: -210, -70, +70, +210 (at canvas width 900px)

---

## Audio API (confirmed from Assembly-CSharp interop + FMOD strings bank)

### FMOD VCA paths (exact strings from Master Bank.strings.bank)

| Row label | VCA path | PlayerPrefs key |
|---|---|---|
| Music Volume | `vca:/Soundtrack` | `musicVolume` |
| Ambience Vol. | `vca:/Ambience` | `ambienceVolume` |
| Weather Vol. | `vca:/Weather` | `weatherVolume` |
| Footsteps Vol. | `vca:/Footsteps` | `footstepsVolume` |
| Notifications | `vca:/Notifications` | `notificationsVolume` |
| PA System Vol. | `vca:/PA System` | `paVolume` |
| Other SFX Vol. | `vca:/Other SFX` | `otherVolume` |

**No `vca:/Music`, no `vca:/Interface`, no `vca:/UI`** — those don't exist in the bank.

### Live volume change

```csharp
AudioController.Instance?.SetVCALevel("vca:/Soundtrack", vol);  // float 0..1
PlayerPrefs.SetFloat("musicVolume", vol); PlayerPrefs.Save();
```

### Master volume

```csharp
AudioListener.volume = vol;          // Unity built-in — takes effect immediately
PlayerPrefs.SetFloat("masterVolume", vol); PlayerPrefs.Save();
```

### Music toggle

```csharp
if (v) MusicController.Instance?.TurnOnMusic(null!, null!, null!);
else   MusicController.Instance?.TurnOffMusic(null!, null!, null!);
PlayerPrefs.SetInt("music", v ? 1 : 0); PlayerPrefs.Save();
```

### Licensed music

```csharp
MusicController.Instance?.SetAllowLicensedMusic(v);
PlayerPrefs.SetInt("licensedMusic", v ? 1 : 0); PlayerPrefs.Save();
```

### Bass reduction

```csharp
AudioController.Instance?.SetBassReduction(v);
PlayerPrefs.SetInt("bassReduction", v ? 1 : 0); PlayerPrefs.Save();
```

### Hyperacusis

```csharp
AudioController.Instance?.SetHyperacusisFilter(v);
PlayerPrefs.SetInt("hyperacusis", v ? 1 : 0); PlayerPrefs.Save();
```

### False PlayerPrefs keys (DO NOT USE for game effect)

`musicVolume`, `ambienceVolume`, `footstepsVolume`, `interfaceVolume`, `weatherVolume`,
`paVolume`, `otherVolume`, `notificationsVolume` — **not present in Assembly-CSharp.dll**.
Writing them persists across sessions (game reads via our PlayerPrefs key on load if
`PlayerPrefsController.LoadPlayerPrefs` calls SetVCALevel internally), but they do NOT
take immediate effect unless paired with `SetVCALevel`.

### FMOD snapshots (read-only reference)

`snapshot:/Setting Bass Reduction Heavy`, `snapshot:/Setting Bass Reduction Light`,
`snapshot:/Setting Hyperacusis Heavy`, `snapshot:/Setting Hyperacusis Medium`,
`snapshot:/Setting Hyperacusis Light` — triggered internally by `SetBassReduction` /
`SetHyperacusisFilter`; do not call directly.
