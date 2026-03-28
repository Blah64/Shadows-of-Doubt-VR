# Phase 9 — VR Movement Plan

**Date**: 2026-03-28
**Goal**: Thumbstick locomotion, snap turn, and controller bindings so the player can move in VR without keyboard/mouse.

---

## Current State

### What works
- OpenXR action set already binds **both controllers**: aim pose, trigger (boolean), thumbstick (vector2f)
- Right controller: pose tracked, trigger fires clicks, cursor dot visible
- Right thumbstick Y: scrolls VR settings panel when open
- VROrigin follows game camera position each frame (`transform.position = _gameCam.position`)
- VROrigin rotation is **independent** of game camera — head tracking applies via local child transforms

### OpenXR bindings already in place (OpenXRManager.cs)
```
_poseAction    → /user/hand/{left,right}/input/aim/pose    (type 4 = POSE)
_triggerAction → /user/hand/{left,right}/input/trigger/value (type 1 = BOOLEAN)
_thumbAction   → /user/hand/{left,right}/input/thumbstick   (type 3 = VECTOR2F)
```
Both `_leftAimSpace` and `_rightAimSpace` are created and attached to the session.

### Public API already available
```csharp
OpenXRManager.GetControllerPose(bool right, long displayTime, out Quaternion ori, out Vector3 pos)
OpenXRManager.GetTriggerState(bool right, out bool pressed)
OpenXRManager.GetThumbstickState(bool right, out float x, out float y)
```

### Game character movement system (unknown — needs runtime discovery)
- Game has `RigidbodyFirstPersonController` (found in Assembly-CSharp interop)
- Also has `CharacterController`, `firstPersonController`, `ApplyMovement`, `GetInput`
- Rewired_Core.dll is referenced in csproj — game uses Rewired for input
- Rewired `CustomController.SetAxisValue(string, float)` and `SetButtonValue(string, bool)` exist
- Asset files contain `Horizontal` and `Vertical` strings (likely Rewired action names)
- **Unknown**: exact Rewired action names, whether to inject via Rewired or move transform directly

---

## Implementation Steps

### Step 1: Snap Turn (right stick X → VROrigin yaw rotation)

**No game API needed — pure VR rig rotation.**

Add to `VRCamera.cs`:

```csharp
// ── Snap turn state ──
private const float SnapTurnAngle    = 30f;   // degrees per snap
private const float SnapTurnDeadZone = 0.6f;  // thumbstick threshold
private const float SnapTurnCooldown = 0.25f; // seconds between snaps
private float _snapTurnCooldownTimer;
private bool  _snapTurnReady = true;
```

**Logic** (in `Update()`, after `SyncActions()`):
1. Read right thumbstick X via `OpenXRManager.GetThumbstickState(true, out tx, out _)`
2. If `|tx| > SnapTurnDeadZone` and cooldown expired:
   - Rotate `transform` (VROrigin) around Y by `sign(tx) * SnapTurnAngle`
   - Reset cooldown timer
3. If `|tx| < SnapTurnDeadZone * 0.5`: re-arm (hysteresis to prevent double-fire)
4. Subtract `Time.deltaTime` from cooldown timer each frame

**Why rotate VROrigin**: The game camera position is followed each frame, but VROrigin's **rotation** is independent — head tracking is applied as local rotations on child cameras. Rotating VROrigin around Y effectively turns the player's view.

**Canvas re-centering**: After snap turn, canvases should stay at their world positions (they already do — `_positionedCanvases` prevents re-placement). The Home key re-centres them if needed.

**Conflict with settings panel scroll**: Currently right stick Y scrolls the settings panel. When the panel is open, **skip snap turn** (already gated by `VRSettingsPanel.RootGO?.activeSelf`).

### Step 2: Left Controller Pose Tracking

**Trivial — same pattern as right controller.**

Add to `BuildCameraRig()`:
```csharp
var leftCtrlGO = new GameObject("LeftController");
leftCtrlGO.layer = UILayer;
leftCtrlGO.transform.SetParent(_cameraOffset, false);
_leftControllerGO = leftCtrlGO;
```

Add field:
```csharp
private GameObject? _leftControllerGO;
```

In `UpdateControllerPose()`, after right controller update:
```csharp
if (_leftControllerGO != null)
{
    if (OpenXRManager.GetControllerPose(false, displayTime, out Quaternion lOri, out Vector3 lPos))
    {
        var ulPos = new Vector3(lPos.x, lPos.y, -lPos.z);
        var ulOri = new Quaternion(-lOri.x, -lOri.y, lOri.z, lOri.w);
        _leftControllerGO.transform.position = transform.TransformPoint(ulPos);
        _leftControllerGO.transform.rotation = transform.rotation * ulOri;
    }
}
```

### Step 3: Runtime Discovery — Rewired Action Names & Character Controller

**One-shot debug logging** to discover the game's movement API.

Add a `_movementDiscoveryDone` flag. On the first frame where `_stereoReady` and `_gameCam != null`:

```csharp
private bool _movementDiscoveryDone;

private void DiscoverMovementSystem()
{
    if (_movementDiscoveryDone || _gameCam == null) return;
    _movementDiscoveryDone = true;

    try
    {
        // 1. Log all Rewired actions
        var actions = Rewired.ReInput.mapping.Actions;
        foreach (var a in actions)
            Log.LogInfo($"[Movement] Rewired action: id={a.id} name='{a.name}' type={a.type}");

        // 2. Find the player object (Rewired player 0 = local player)
        var player = Rewired.ReInput.players.GetPlayer(0);
        Log.LogInfo($"[Movement] Rewired player0: name='{player?.name}' id={player?.id}");

        // 3. Walk up from _gameCam to find CharacterController or Rigidbody
        var t = _gameCam;
        for (int i = 0; i < 10 && t != null; i++)
        {
            var cc = t.GetComponent<CharacterController>();
            var rb = t.GetComponent<Rigidbody>();
            var rfpc = t.GetComponent<RigidbodyFirstPersonController>();
            Log.LogInfo($"[Movement] Ancestor[{i}] '{t.gameObject.name}': " +
                        $"CC={cc != null} RB={rb != null} RFPC={rfpc != null}");
            if (cc != null || rb != null || rfpc != null) break;
            t = t.parent;
        }

        // 4. Also try FindObjectOfType for the FPS controller
        var fpc = UnityEngine.Object.FindObjectOfType<RigidbodyFirstPersonController>();
        if (fpc != null)
            Log.LogInfo($"[Movement] Found RFPC: GO='{fpc.gameObject.name}' " +
                        $"pos={fpc.transform.position}");
    }
    catch (Exception ex)
    {
        Log.LogWarning($"[Movement] Discovery failed: {ex}");
    }
}
```

**Expected output**: Rewired action names (likely `Horizontal`, `Vertical`, `Jump`, `Sprint`, `Crouch`, `Interact`), player info, and which GO has the CharacterController/Rigidbody.

### Step 4: Thumbstick Locomotion (left stick → character movement)

**Two candidate approaches — chosen based on Step 3 results.**

#### Approach A: Rewired Virtual Input (preferred if action names found)

If Step 3 confirms action names (e.g. `"Horizontal"`, `"Vertical"`):

```csharp
// In Update(), after SyncActions():
OpenXRManager.GetThumbstickState(false, out float lx, out float ly); // left stick

if (Mathf.Abs(lx) > 0.15f || Mathf.Abs(ly) > 0.15f)
{
    // Transform thumbstick input from head-relative to world-relative
    // Head yaw = combined VROrigin yaw + HMD local yaw
    float headYaw = _leftCam.transform.eulerAngles.y;
    Vector3 forward = Quaternion.Euler(0, headYaw, 0) * Vector3.forward;
    Vector3 right   = Quaternion.Euler(0, headYaw, 0) * Vector3.right;
    Vector3 moveDir = (forward * ly + right * lx).normalized;

    // Inject into Rewired — game's own movement system handles physics, speed, collision
    var player = Rewired.ReInput.players.GetPlayer(0);
    // player.SetAxisValue(...) requires CustomController — may need alternative
}
```

**Problem**: Rewired's `SetAxisValue` is on `CustomController`, not `Player`. To inject virtual input we'd need to:
1. Create a `CustomController` via `ReInput.controllers.CreateCustomController(0)`
2. Add it to the player
3. Set axis values each frame

This may conflict with the game's existing controller setup. Needs testing.

#### Approach B: Direct CharacterController.Move() (simpler, guaranteed to work)

If Step 3 finds a `CharacterController` on the player GO:

```csharp
private CharacterController? _charController;
private float _moveSpeed = 3.5f; // m/s — calibrate to match game's walk speed

// In Update(), after SyncActions():
if (_charController != null)
{
    OpenXRManager.GetThumbstickState(false, out float lx, out float ly);
    if (Mathf.Abs(lx) > 0.15f || Mathf.Abs(ly) > 0.15f)
    {
        float headYaw = _leftCam.transform.eulerAngles.y;
        Vector3 forward = Quaternion.Euler(0, headYaw, 0) * Vector3.forward;
        Vector3 right   = Quaternion.Euler(0, headYaw, 0) * Vector3.right;
        Vector3 move = (forward * ly + right * lx) * _moveSpeed * Time.deltaTime;
        _charController.Move(move);
    }
}
```

**Pros**: Works regardless of Rewired. Collision detection built in.
**Cons**: Bypasses game's speed modifiers (sprint, crouch speed, drunk effects). May fight with game's own movement system if both call `CharacterController.Move()` in the same frame.

#### Approach C: Direct Rigidbody velocity (if RigidbodyFirstPersonController found)

```csharp
private Rigidbody? _playerRb;

// In Update():
if (_playerRb != null)
{
    OpenXRManager.GetThumbstickState(false, out float lx, out float ly);
    float headYaw = _leftCam.transform.eulerAngles.y;
    Vector3 forward = Quaternion.Euler(0, headYaw, 0) * Vector3.forward;
    Vector3 right   = Quaternion.Euler(0, headYaw, 0) * Vector3.right;
    Vector3 hVel = (forward * ly + right * lx) * _moveSpeed;
    _playerRb.velocity = new Vector3(hVel.x, _playerRb.velocity.y, hVel.z); // preserve gravity
}
```

**Pros**: Preserves gravity/jumping. More compatible with physics-based movement.
**Cons**: Overrides horizontal velocity — may fight with game movement.

### Step 5: Additional Button Bindings (optional, deferred)

These need new OpenXR actions added to `SetupActionSetsInstance()`:

| Action | Type | Binding | Purpose |
|--------|------|---------|---------|
| `grip_left` | BOOLEAN | `/user/hand/left/input/squeeze/value` | Interact / pickup |
| `grip_right` | BOOLEAN | `/user/hand/right/input/squeeze/value` | Interact / pickup |
| `button_a` | BOOLEAN | `/user/hand/right/input/a/click` | Jump |
| `button_b` | BOOLEAN | `/user/hand/right/input/b/click` | Sprint toggle |
| `button_x` | BOOLEAN | `/user/hand/left/input/x/click` | Crouch toggle |
| `button_y` | BOOLEAN | `/user/hand/left/input/y/click` | Menu / inventory |

**Defer** until Steps 1-4 are confirmed working. These are additive — they don't change existing bindings, just add new actions to the same action set.

**Note**: Adding actions to the action set requires modifying `SetupActionSetsInstance()` (instance-level, before session). The action set can only have actions added before `xrAttachSessionActionSets` is called. So all new actions must be created in the same call.

**IMPORTANT**: OpenXR spec says action sets are immutable after `xrAttachSessionActionSets`. All button bindings must be added in the same pass as the existing pose/trigger/thumbstick actions. This means Step 5 requires modifying `SetupActionSetsInstance()` to create the new actions and add them to the suggested bindings array.

---

## Build Order

### Build 1: Snap turn + left controller + discovery logging
**Files changed**: `VRCamera.cs` only
- Add snap turn logic (right stick X → VROrigin yaw)
- Add left controller GO and pose update
- Add `DiscoverMovementSystem()` one-shot logger
- **No game API changes** — safe, isolated

**Expected log output**:
```
[Movement] Rewired action: id=0 name='Horizontal' type=Axis
[Movement] Rewired action: id=1 name='Vertical' type=Axis
[Movement] Rewired action: id=2 name='Jump' type=Button
...
[Movement] Ancestor[0] 'FPSController': CC=True RB=False RFPC=False
```

### Build 2: Thumbstick locomotion
**Files changed**: `VRCamera.cs`
- Implement locomotion based on Build 1 log results
- Choose Approach A, B, or C based on discovered APIs
- Wire left thumbstick to character movement
- Add dead zone, speed scaling

### Build 3: Button bindings (optional)
**Files changed**: `OpenXRManager.cs` + `VRCamera.cs`
- Add grip/A/B/X/Y actions to `SetupActionSetsInstance()`
- Add suggested bindings
- Add `GetGripState()`, `GetButtonState()` public API
- Wire to game actions (jump, interact, sprint, crouch)
- Add pre-allocated buffers for new action states in `InitPerFrameResources()`

---

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Rewired action names don't match `Horizontal`/`Vertical` | Can't use Approach A | Build 1 discovery logging reveals correct names |
| `SetAxisValue` on CustomController conflicts with game input | Movement jitters or doubles | Fall back to Approach B or C |
| `CharacterController.Move()` fights with game's own movement | Double-speed or jittering | Only apply when game's own input is zero (idle) |
| Snap turn causes nausea | Comfort issue | Configurable angle (settings panel), smooth turn option later |
| VROrigin rotation breaks canvas placement | Canvases drift | Already handled — positioned canvases are world-fixed |
| Adding OpenXR actions breaks existing bindings | Controllers stop working | Actions are additive — won't affect existing pose/trigger/thumb |
| IL2CPP interop for `ReInput.mapping.Actions` throws | Can't enumerate | Wrap in try/catch; fall back to hardcoded names from asset dump |

---

## Constants & Configuration

```csharp
// Snap turn
const float SnapTurnAngle    = 30f;    // degrees
const float SnapTurnDeadZone = 0.6f;   // stick threshold (0-1)
const float SnapTurnCooldown = 0.25f;  // seconds

// Locomotion
const float MoveDeadZone     = 0.15f;  // stick threshold
const float MoveSpeed        = 3.5f;   // m/s (calibrate to game walk speed)
const float SprintMultiplier = 1.8f;   // when sprint button held

// Smooth turn (future option)
const float SmoothTurnSpeed  = 120f;   // degrees/second
```

All of these should eventually be exposed in the VR Settings panel (Phase 10 comfort options).

---

## Files to Modify

| File | Changes |
|------|---------|
| `SoDVR/VR/VRCamera.cs` | Snap turn, left controller pose, discovery logging, thumbstick locomotion |
| `SoDVR/OpenXRManager.cs` | (Build 3 only) Additional button actions, grip/A/B/X/Y bindings |
| `SoDVR/SoDVR.csproj` | No changes — `Rewired_Core` already referenced |
