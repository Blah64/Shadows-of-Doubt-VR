# Plan: Full UI/Canvas System Redesign for VR

## Context

The current VR canvas management in `VRCamera.cs` (3386 lines) has accumulated significant complexity and two blocking root-cause bugs:
1. **CanvasScaler inflation** — all canvases are 2-4x too large because CanvasScaler runs before our WorldSpace conversion
2. **HDRP auto-exposure** — all UI is near-invisible because eye cameras inherit scene exposure (EV 8-12)

Additionally, the system lacks proper behavioral separation between canvas types (HUD vs menus vs case boards), has no grip-relocate support, and the interaction/cursor system fights with hidden canvases. This plan redesigns the entire canvas management system from the ground up.

## Architecture Overview

### Two-camera-per-eye rendering
```
VROrigin
  CameraOffset
    LeftEye (Camera) — renders scene (all layers EXCEPT UI layer 5)
    LeftEyeUI (Camera) — renders ONLY UI layer 5, clearFlags=Depth, no HDRP exposure
    RightEye (Camera) — scene
    RightEyeUI (Camera) — UI only
```

The UI overlay cameras solve the HDRP exposure problem architecturally: they render only layer 5 with a clean HDRP configuration (no post-processing, no exposure inheritance). Scene cameras exclude layer 5. This is a clean separation — no HDR color boosts, no material hacks, no brightness constants.

### Canvas lifecycle categories

| Category | Behavior | Positioning | Interactable | Examples |
|----------|----------|-------------|-------------|----------|
| **HUD** | Body-locked (CC pos + snap-turn yaw). Not head-tracked. Always visible. | Fixed offset from VROrigin | No | GameCanvas, OverlayCanvas, MinimapCanvas, StatusDisplayCanvas |
| **Menu** | Recenters in front of head on open. Disappears when closed. | Head-forward on activate | Yes (raycast) | MenuCanvas, DialogCanvas, WindowCanvas, PopupMessage |
| **CaseBoard** | Recenters on open, remembers relative layout. Grip-relocatable. | Head-forward on first open, then grip-draggable | Yes (raycast + grip) | CaseCanvas, LocationDetailsCanvas, BioDisplayCanvas |
| **Panel** | Recenters on open. Standard interactive. | Head-forward on activate | Yes (raycast) | ActionPanelCanvas, ControlsDisplayCanvas, UpgradesDisplayCanvas |
| **Tooltip** | Follows cursor depth, every frame. | Near cursor | No | TooltipCanvas |
| **Ignored** | Not converted to WorldSpace. Left as-is. | N/A | N/A | MapDuct*, MapButton*, Loading Icon, gameWorldCanvas (already WorldSpace) |

### HUD positioning detail
- HUD canvases parent to a `HUDanchor` transform under VROrigin (not CameraOffset)
- HUDanchor.position = VROrigin.position (follows CC movement)
- HUDanchor.rotation = VROrigin.rotation (follows snap-turn only, NOT head rotation)
- HUD placed 2.0m forward, -0.15m down from HUDanchor
- HUD scale computed dynamically from actual sizeDelta (see below)

### Case board grip-relocate
- When grip button is held AND controller ray hits a CaseBoard canvas: enter drag mode
- During drag: canvas follows controller offset (position + rotation delta from grab point)
- On grip release: canvas stays at new position
- Track relative offsets between CaseBoard canvases: when any CaseBoard is reopened, all are recentered to head-forward, maintaining their relative offsets to each other
- Store: `Dictionary<int, Vector3> _caseBoardRelativeOffsets` keyed by canvas ID, storing offset from the "primary" CaseBoard (CaseCanvas)

---

## Implementation Steps

### Step 1: Add UI overlay cameras (solves HDRP exposure)

**File**: `VRCamera.cs`, `BuildCameraRig()` (~line 699)

Add two new cameras:
```csharp
private Camera _leftUICam;
private Camera _rightUICam;
```

In `BuildCameraRig()`:
- After creating `_leftCam` / `_rightCam`, create `_leftUICam` / `_rightUICam`
- Scene cameras: `cullingMask = ~(1 << UILayer)` — render everything EXCEPT UI
- UI cameras: `cullingMask = (1 << UILayer)` — render ONLY UI
- UI cameras: `clearFlags = CameraClearFlags.Depth` (transparent background, composites over scene)
- UI cameras: same HDRP setup but with ALL post-processing disabled and `allowHDR = false`
- UI cameras: `depth` higher than scene cameras so they render on top
- UI cameras share the same RenderTexture as scene cameras (they composite via depth-clear)

In `LateUpdate()`:
- After `_leftCam.Render()`, call `_leftUICam.Render()`
- After `_rightCam.Render()`, call `_rightUICam.Render()`
- Apply same pose to UI cameras as scene cameras each frame

### Step 2: Fix CanvasScaler + dynamic scale

**File**: `VRCamera.cs`, `ConvertCanvasToWorldSpace()` (~line 2261)

The current code already reads `scaler.referenceResolution` and sets `sizeDelta` — this is correct. But we need to also:
1. **Disable the CanvasScaler** after reading reference resolution (prevents it from re-inflating on subsequent frames)
2. **Compute scale dynamically** from target world width and actual sizeDelta

Replace static per-category `Scale` values with computed values:
```csharp
// Target world widths per category (metres)
HUD: 1.5m
Menu: 1.6m
CaseBoard: 1.8m
Panel: 1.6m
Tooltip: 0.8m
```

Scale = `targetWorldWidth / sizeDelta.x`

This makes the system immune to CanvasScaler inflation — whatever sizeDelta ends up being, we compute the right scale.

### Step 3: Restructure canvas categories

**File**: `VRCamera.cs`

Replace current `CanvasCategory` enum and mappings:
```csharp
private enum CanvasCategory { HUD, Menu, CaseBoard, Panel, Tooltip, Ignored, Default }
```

Update `s_canvasCategories` mapping:
- Move CaseCanvas, LocationDetailsCanvas, BioDisplayCanvas to `CaseBoard`
- Move ActionPanelCanvas from HUD to `Panel` (it's an interactive menu, not a HUD)
- Add `Ignored` for MapDuct/MapButton/Loading Icon (explicit skip)
- GameWorldDisplayCanvas → `Ignored` (already WorldSpace)

Update `CanvasCategoryDefaults` to include `TargetWorldWidth` instead of `Scale`:
```csharp
private readonly struct CanvasCategoryDefaults
{
    public readonly float Distance;
    public readonly float VerticalOffset;
    public readonly float TargetWorldWidth;  // metres — replaces Scale
    public readonly bool  RecentreOnActivate;
    public readonly bool  RepositionEveryFrame;
    public readonly bool  IsHUD;             // body-locked behavior
    public readonly bool  IsGripRelocatable; // CaseBoard grip-drag
}
```

### Step 4: HUD body-lock system

**File**: `VRCamera.cs`

Add a `HUDanchor` transform:
```csharp
private Transform _hudAnchor;  // child of VROrigin, NOT CameraOffset
```

In `BuildCameraRig()`:
- Create `HUDanchor` as child of `this.transform` (VROrigin)
- Position: Vector3.zero (same as VROrigin)

In `PositionCanvases()`:
- For HUD canvases: parent them to `_hudAnchor` instead of positioning independently
- `_hudAnchor.position = transform.position` (follows CC movement via VROrigin)
- `_hudAnchor.rotation = transform.rotation` (follows snap turn, NOT head rotation)
- HUD canvases have fixed local offset: forward 2.0m, down 0.15m relative to HUDanchor

In `UpdateSnapTurn()`:
- HUDanchor already follows VROrigin rotation, so no extra work needed

### Step 5: CaseBoard grip-relocate system

**File**: `VRCamera.cs`

New fields:
```csharp
private Canvas? _gripDragCanvas;           // currently being dragged
private Vector3 _gripDragOffset;           // controller-to-canvas offset at grab start
private Quaternion _gripDragRotOffset;     // rotation offset at grab start
private readonly Dictionary<int, Vector3> _caseBoardOffsets = new();  // relative to CaseCanvas
private int _caseBoardPrimaryId = -1;      // CaseCanvas instance ID
```

New method `UpdateGripDrag()` called from `UpdateControllerPose()`:
1. Read grip button state via `OpenXRManager.GetGripState(true, out bool gripNow)`
2. If grip pressed AND controller ray hits a CaseBoard canvas AND not already dragging:
   - Start drag: store offset between controller and canvas position/rotation
   - Set `_gripDragCanvas`
3. While dragging: move canvas by controller delta
4. On grip release:
   - Compute offset from primary CaseBoard (CaseCanvas) and store in `_caseBoardOffsets`
   - Clear `_gripDragCanvas`

In `PositionCanvases()` for CaseBoard canvases on reactivation:
- Recenter primary CaseBoard to head-forward
- Apply stored `_caseBoardOffsets` to maintain relative layout

**OpenXRManager change required**: Grip/squeeze is NOT currently bound. Need to add:
- `_gripAction` field (line 56 area, alongside `_triggerAction`)
- `CreateAction("grip", "Grip", 1, _leftHandPath, _rightHandPath)` in `SetupActionSetsInstance()`
- Binding paths: `/user/hand/right/input/squeeze/value` and `/user/hand/left/input/squeeze/value`
- `GetGripState(bool right, out bool pressed)` method (same pattern as `GetTriggerState`)

### Step 6: Remove HDR brightness boost system

**File**: `VRCamera.cs`

With the overlay camera approach, HDRP exposure no longer affects UI. Remove:
- `UITextBrightnessBoost` and `UIImageBrightnessBoost` constants
- `ApplyReadableTextBoost()` method
- `ApplyReadableImageBoost()` method
- `s_textBoostedGraphics`, `s_imageBoostedGraphics`, `s_imageBoostedMats` tracking sets
- All calls to these methods in `ForceUIZTestAlways()` and `RescanCanvasAlpha()`
- `StrengthenMenuTextMaterial()` HDR color overrides (the `Color(8f, 8f, 8f, 1f)` hacks)
- `EnsureMenuTextFallback()` — the duplicate text overlay system, which was a workaround for invisible text
- `s_menuTextFallbacks`, `s_menuTmpReadableMats` dictionaries
- HDR material colors on cursor dot (4096,0,4096) and laser beam (0,4096,4096) — replace with normal colors

Keep:
- ZTest Always material patches (still needed for UI-over-world rendering)
- Stencil neutralization (still needed for WorldSpace clipping issues)
- FadeOverlay suppression
- Background alpha dimming (`UIBackgroundAlpha`)

### Step 7: Simplify PositionCanvases

**File**: `VRCamera.cs`

Rewrite the placement logic with clear behavioral branches:

```
For each managed canvas:
  if category == HUD:
    Parent to _hudAnchor (one-time)
    Set local position (forward + down offset)
    Never reposition after initial setup

  if category == CaseBoard:
    On activate: recenter primary to head-forward
    Apply relative offsets to all CaseBoard canvases
    During grip-drag: driven by UpdateGripDrag()

  if category == Menu or Panel:
    On activate: center in front of head
    After placed: stays put until deactivated and reactivated

  if category == Tooltip:
    Every frame: position at cursor depth - 0.02m

  if category == Ignored:
    Skip entirely
```

### Step 8: Fix canvas ray-blocking and depth scan

**File**: `VRCamera.cs`

**Problem**: Many canvases can block the aiming dot and steal clicks:
- Hidden canvases (CanvasGroup.alpha=0 but still active) create invisible walls
- HUD canvases behind menus catch rays that miss the menu rect bounds
- Multiple canvases at similar depths create layered walls
- Canvases the user can't see intercept the ray before reaching the intended target

**Solution — layered interactability rules**:

1. **Only interactable categories participate in depth scan AND click raycast**:
   - Menu, Panel, CaseBoard → participate (interactable)
   - HUD, Tooltip, Ignored, cursor → never participate
   - This is the key change: HUD canvases are completely invisible to the ray system

2. **Ghost canvas prevention** (keep existing, strengthen):
   - `CanvasGroup.alpha < 0.1` → skip in depth scan (already exists)
   - Also skip in `TryClickCanvas` — a canvas with alpha < 0.1 should never receive clicks
   - Also skip canvases where `canvas.enabled == false` in both depth scan AND click

3. **Rect bounds filtering in TryClickCanvas**:
   - Currently `TryClickCanvas` collects ALL plane intersections and tries GraphicRaycaster on each
   - Add bounds check BEFORE calling GraphicRaycaster: if ray lands outside canvas rect, skip it entirely
   - This prevents a canvas plane from "consuming" the ray when the actual hit point is outside its content area

4. **Overlapping interactable canvases — ray pass-through**:
   Known overlap pairs that must BOTH remain clickable:
   - MenuCanvas + PopupMessage (exit confirmation over pause menu)
   - CaseCanvas + ActionPanelCanvas (action buttons over investigation board)
   - MenuCanvas + WindowCanvas (notebook over menu)

   **Solution**: `TryClickCanvas` already handles this correctly — it sorts all plane hits
   by distance and tries `GraphicRaycaster` on each. If the nearest canvas has no graphic
   under the cursor, it falls through to the next canvas. This is the right behavior.

   The **depth scan** (for cursor positioning) needs the same pass-through logic:
   - Don't stop at the first canvas plane hit
   - Instead, find the nearest canvas where the ray actually lands ON a graphic element
   - Use `GraphicRaycaster.Raycast()` in the depth scan too (not just plane + rect check)
   - Cache the result so `TryClickCanvas` can reuse it (avoid double raycasting)

   **Distance staggering** within categories:
   - Menu canvases: stagger by sortingOrder × 0.05m (not 0.005m — need visible separation)
   - PopupMessage always 0.2m closer than MenuCanvas (confirmation dialogs must be in front)
   - ActionPanelCanvas always 0.15m closer than CaseCanvas (action buttons in front of board)
   - WindowCanvas (SubPanel) already at 1.6m vs Menu at 1.8m — natural separation

5. **Cursor visibility**:
   - Cursor only visible when depth scan found an interactable canvas hit
   - Cursor hidden when aiming at HUD or empty space (no change from current behavior, but now explicitly by category)

6. **HUD canvases on separate collision layer** (defense in depth):
   - Consider moving HUD canvas GameObjects to a different layer (e.g. layer 6) so GraphicRaycaster on interactable canvases never even considers HUD graphics
   - This prevents edge cases where a HUD graphic's raycastTarget=true steals a click from a menu behind it

### Step 9: Clean up diagnostic/dead code

Remove accumulated debugging code that's no longer needed:
- The massive `End` key text diagnostic dump (lines 401-579) — replace with a concise version
- `CaseDiag` / `CaseDiag2` / `CaseGraphic` logging in RescanCanvasAlpha
- `EnsureMenuTextFallback` and all related helpers (`IsLikelyNavButtonText`, `IsLikelySettingsLabelText`, etc.)
- `SyncReadableFallbackRect`, `ForceReadableTmpMaterialState`, `ApplyMenuTmpReadableMaterial`
- `s_menuTextFallbacks` dictionary and all references

---

## Files Modified

| File | Changes |
|------|---------|
| `SoDVR/VR/VRCamera.cs` | All steps: overlay cameras, CanvasScaler fix, category restructure, HUD body-lock, CaseBoard grip, remove HDR boosts, simplify positioning |
| `SoDVR/OpenXRManager.cs` | Step 5: Add grip/squeeze action binding if not present |

---

## Verification

1. **Build**: `dotnet build SoDVR/SoDVR.csproj -c Release` + deploy
2. **Main menu**: All text legible without HDR boost hacks? (overlay camera test)
3. **In-game HUD**: Stays stable with thumbstick move + snap turn? Doesn't follow head look?
4. **Menu open (ESC)**: Recenters in front of head? Disappears when closed?
5. **Case board**: Opens in front of head? Grip-drag works? Relative layout preserved on reopen?
6. **Canvas sizes**: All canvases appropriately sized (~1.5-1.8m world width)?
7. **Cursor/laser**: Visible with normal colors? Only shows on interactable canvases?
8. **Tooltips**: Follow cursor depth near aimed canvas?
9. **Load game**: No crash, canvases survive scene transition?

---

## Execution Order

This is a large change. Implement in this order, testing after each step:

1. **Step 1** (overlay cameras) + **Step 6** (remove HDR boosts) — test: is UI visible without boost hacks?
2. **Step 2** (CanvasScaler fix + dynamic scale) — test: are canvases correctly sized?
3. **Step 3** (category restructure) + **Step 7** (simplified positioning) — test: do canvases appear at right distances?
4. **Step 4** (HUD body-lock) — test: does HUD follow movement but not head look?
5. **Step 5** (CaseBoard grip-relocate) — test: can you grab and move case boards?
6. **Step 8** (cursor cleanup) + **Step 9** (dead code removal) — test: full regression pass
