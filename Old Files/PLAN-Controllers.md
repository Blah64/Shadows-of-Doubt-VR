# Phase 11: Full VR Motion Controller Implementation

## Context

Phase 10 completed world graphics. Movement infrastructure (locomotion + snap turn) exists in VRCamera.cs but the game has many more controls needed for actual gameplay. This plan adds all critical gameplay bindings to the VR controllers so the game is fully playable.

## Complete VR Controller Mapping

### Right Controller
| Button | Action | Key Simulated | Status |
|--------|--------|---------------|--------|
| Trigger | UI click (canvas raycast) | — | Done |
| Thumbstick X | Snap turn ±30° | — | Done |
| Thumbstick Y | Settings panel scroll | — | Done |
| Grip | CaseBoard/canvas drag | — | Done |
| **A button** | **Jump** | **Space (0x20)** | **NEW** |
| **B button** | **Notebook / Map** | **Tab (0x09)** | **NEW** |
| **Thumbstick click** | **Flashlight toggle** | **mouse middle click** | **NEW** |

### Left Controller
| Button | Action | Key Simulated | Status |
|--------|--------|---------------|--------|
| Y button | Menu / ESC | ESC (0x1B) | Done |
| Thumbstick | Locomotion (head-relative) | — | Done |
| **X button** | **Crouch toggle** | **C (0x43)** | **NEW** |
| **Trigger** | **Interact (doors/objects/NPCs)** | **E (0x45)** | **NEW** (reuse existing action) |
| **Grip** | **Inventory** | **X (0x58)** | **NEW** (reuse existing action) |
| **Thumbstick click** | **Sprint toggle** | **Shift toggle (0x10)** | **NEW** |

## Implementation Steps

### Step 1: New OpenXR Actions (`OpenXRManager.cs`)

Add 4 new action fields alongside existing ones (~line 56):
```csharp
private static ulong _buttonAAction, _buttonBAction, _buttonXAction, _thumbClickAction;
```

In `SetupActionSetsInstance()` (~line 1402), create actions:
```csharp
_buttonAAction    = CreateAction("button_a",     "A Button",     1, _rightHandPath);
_buttonBAction    = CreateAction("button_b",     "B Button",     1, _rightHandPath);
_buttonXAction    = CreateAction("button_x",     "X Button",     1, _leftHandPath);
_thumbClickAction = CreateAction("thumb_click",  "Thumb Click",  1, _leftHandPath, _rightHandPath);
```

Add 6 new binding paths (~line 1416):
```
/user/hand/right/input/a/click      → _buttonAAction
/user/hand/right/input/b/click      → _buttonBAction
/user/hand/left/input/x/click       → _buttonXAction
/user/hand/left/input/thumbstick/click  → _thumbClickAction
/user/hand/right/input/thumbstick/click → _thumbClickAction
```

Update `bindCount` from 10 → 15, add 5 WriteBinding calls, update log line.

### Step 2: New Public Get Methods (`OpenXRManager.cs`)

Add after existing GetMenuButtonState (~line 1655):
```csharp
public static bool GetButtonAState(out bool pressed)     // right A
public static bool GetButtonBState(out bool pressed)     // right B
public static bool GetButtonXState(out bool pressed)     // left X
public static bool GetThumbClickState(bool right, out bool pressed) // either thumbstick click
```

Each follows the exact same pattern as `GetGripState()` — write action handle + subaction path to `_bActionGi`, call `_dGetActionStateBool`, read `isActive + currentState`.

### Step 3: New P/Invoke for Mouse Events (`VRCamera.cs`)

Add alongside existing `keybd_event` DllImport (~line 291):
```csharp
[DllImport("user32.dll")]
private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
```

### Step 4: State Tracking Fields (`VRCamera.cs`)

Add near existing movement fields (~line 260):
```csharp
// Edge-detection for new buttons (all use press-edge + cooldown pattern)
private bool  _jumpBtnPrev;
private bool  _crouchBtnPrev;
private bool  _interactBtnPrev;
private bool  _notebookBtnPrev;
private bool  _flashlightBtnPrev;
private bool  _inventoryBtnPrev;
private bool  _sprintActive;       // tracks whether Shift is currently held down
```

### Step 5: New Update Methods (`VRCamera.cs`)

All follow the same press-edge pattern used by `UpdateMenuButton()`:

**`UpdateJump()`** — Right A → Space key
- Detect press edge (pressed && !_jumpBtnPrev)
- `keybd_event(0x20, 0, 0, ...)` down + `keybd_event(0x20, 0, KEYEVENTF_KEYUP, ...)` up
- Skip during settings panel open / scene load grace

**`UpdateInteract()`** — Left Trigger → E key
- Uses existing `GetTriggerState(false, out pressed)` — no new OpenXR action needed
- Press edge → `keybd_event(0x45, ...)` down+up
- Skip during settings panel open

**`UpdateCrouch()`** — Left X → C key (toggle)
- Press edge → `keybd_event(0x43, ...)` down+up (game handles toggle internally)
- Skip during settings panel open

**`UpdateSprint()`** — Left Thumbstick Click → Shift key (toggle)
- Uses new `GetThumbClickState(false, out pressed)` action
- Press edge → toggle `_sprintActive`:
  - If starting: `keybd_event(0x10, 0, 0, ...)` — Shift DOWN
  - If stopping: `keybd_event(0x10, 0, KEYEVENTF_KEYUP, ...)` — Shift UP
- Auto-stop sprint when left stick returns to center (both axes < MoveDeadZone)
- Track `_sprintActive` so we always release Shift on scene load / settings open
- Skip if no `_playerCC` (not in game world)

**`UpdateNotebook()`** — Right B → Tab key
- Press edge → `keybd_event(0x09, ...)` down+up

**`UpdateFlashlight()`** — Right Thumbstick Click → Middle mouse button
- Press edge → `mouse_event(MIDDLEDOWN)` + `mouse_event(MIDDLEUP)`

**`UpdateInventory()`** — Left Grip → X key
- Uses existing `GetGripState(false, out pressed)` — no new OpenXR action needed
- Press edge → `keybd_event(0x58, ...)` down+up

### Step 6: Wire Into LateUpdate (`VRCamera.cs`)

In the `if (OpenXRManager.ActionSetsReady)` block (~line 703-710), add after existing calls:
```csharp
UpdateJump();
UpdateInteract();
UpdateCrouch();
UpdateSprint();
UpdateNotebook();
UpdateFlashlight();
UpdateInventory();
```

### Step 7: Safety — Release Sprint on Scene Load

In the scene load grace / BuildCameraRig reset code, ensure Shift is released:
```csharp
if (_sprintActive)
{
    keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    _sprintActive = false;
}
```

## Files Modified

| File | Changes |
|------|---------|
| `SoDVR/OpenXRManager.cs` | 4 new action fields, 4 CreateAction calls, 5 new bindings, 4 new GetState methods |
| `SoDVR/VR/VRCamera.cs` | 1 new DllImport, 7 state fields, 7 Update methods, wire into LateUpdate, sprint safety |

## Verification

1. Build: `dotnet build SoDVR/SoDVR.csproj -c Release`
2. Deploy: `cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"`
3. Launch game, check `LogOutput.log` for:
   - `SetupActionSetsInstance complete:` — should now show button_a, button_b, etc.
   - `[Movement] Cached playerCC` — confirms CharacterController found
4. In-game test each binding:
   - Right A → character jumps
   - Left trigger → doors open, objects picked up
   - Left X → character crouches
   - Left grip hold → character sprints (faster movement)
   - Right B → notebook/map opens
   - Right thumbstick click → flashlight toggles
   - Left thumbstick click → inventory opens
5. Verify existing controls still work (snap turn, locomotion, menu, UI click, grip drag)
