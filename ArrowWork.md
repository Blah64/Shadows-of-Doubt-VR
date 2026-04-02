# Awareness Compass Arrow — VR Fix Work Notes

## What it is

The "awareness compass" is a circle with directional arrows that appears at the **centre-bottom of the screen** in flat-screen mode.  It is NOT a UI canvas element — it is a **3D MeshRenderer system** parented to the game camera hierarchy.

## Scene structure (from decompiled `Assembly-CSharp.dll`)

```
compassContainer  (public serialized GameObject on InterfaceController)
  └── backgroundTransform  (Transform — the ring MeshRenderer)
        └── spawned  (Instantiate(PrefabControls.Instance.awarenessIndicator, backgroundTransform))
              ├── imageTransform  (quad facing camera)
              └── arrowTransform  (arrow pointing toward threat)
```

Key fields on `InterfaceController`:
```csharp
public GameObject    compassContainer;
public Transform     backgroundTransform;
public MeshRenderer  compassMeshRend;
public Material      compassMaterial;
public List<AwarenessIcon> awarenessIcons;
```

Icons are spawned by `InterfaceController.AddAwarenessIcon(...)`:
```csharp
awarenessIcon.spawned = Instantiate(PrefabControls.Instance.awarenessIndicator, backgroundTransform);
```

## How the game drives it each frame (InterfaceController.Update)

```csharp
// Ring faces world-forward
backgroundTransform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);

// Per-icon: spawned points FROM target TOWARD camera (horizontal only)
Vector3 forward = CameraController.Instance.cam.transform.position - targetPos;
forward.y = 0f;
awarenessIcon.spawned.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

// arrowTransform local Z slides between -3 and -6 (fade/spring/alert)
awarenessIcon.arrowTransform.localPosition = new Vector3(0f, 0f, num4);

// Image always faces game camera
awarenessIcon.imageTransform.rotation = CameraController.Instance.cam.transform.rotation;
```

## Why it's invisible in VR

1. `compassContainer` is parented to the game camera (or `camHeightParent`) in the scene prefab — its world position tracks the game camera.
2. In VR, the game camera (`_gameCamRef`) is suppressed (`cullingMask = 0`) and its **rotation** is set to the left controller direction (end of `Update()`).
3. With the game camera pointing at the controller, `compassContainer` ends up at a random world position relative to the VR eye cameras.
4. Even if it were at the right position, `backgroundTransform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up)` (world-forward) means the ring faces away from the VR cameras when you're not looking north.
5. `imageTransform.rotation = game cam rotation` = controller rotation, not VR head rotation → icons face the wrong way.

## The fix (implemented in VRCamera.cs, last build)

Added `UpdateCompass()` called from `LateUpdate()` before `_leftCam.Render()`.  It runs AFTER `InterfaceController.Update()` so our writes win.

```
1. Move compassContainer to: headPos + headFwd * 1.2 + headUp * (-0.55)
2. backgroundTransform.rotation = LookRotation(headFwd, headUp)   ← ring faces VR cam
3. For each awarenessIcon: imageTransform.rotation = VR cam rotation
```

Constants (tunable):
```csharp
private const float CompassDist    = 1.2f;   // metres in front of VR head
private const float CompassYOffset = -0.55f; // metres below eye level
```

New fields:
```csharp
private Transform? _compassContainer;  // cached in DiscoverMovementSystem (step 5h)
private bool       _compassDiagDone;
```

Discovery in `DiscoverMovementSystem` step 5h:
```csharp
_compassContainer = _interfaceCtrl.compassContainer.transform;
```

## Diagnostic logging (first LateUpdate after discovery)

On first call, `UpdateCompass()` logs:
- `compassContainer` world position, local position, parent name, grandparent name, lossyScale
- `backgroundTransform` world pos, local pos, local scale, lossy scale
- `compassMeshRend.gameObject.activeSelf`, `containerActive`, `awarenessIcons.Count`

**To trigger awareness icons**: get seen by an NPC enemy so that the game calls `InterfaceController.AddAwarenessIcon(...)`.

## What needs verification (next session)

1. Read `LogOutput.log` after a run — look for `[Compass]` lines:
   - What is `compassContainer`'s parent/grandparent? (confirms scene hierarchy theory)
   - What are the world + local positions? (confirms it tracks the game camera)
   - What is `lossyScale`? (tells us if scale is 1:1 or something weird)
   - Is the mesh renderer active?

2. With the positioning code running — does the compass ring appear at the bottom of the VR view?

3. If position is wrong, tune `CompassDist` / `CompassYOffset`.

4. If the arrow is invisible even with correct position:
   - Check the layer the compassContainer is on (may need to add to `_leftCam.cullingMask`)
   - Check the material shader — HDRP may not render the unlit mesh shader used by the compass
   - Try forcing `compassMeshRend.enabled = true` and `compassContainer.gameObject.SetActive(true)`

5. The `awarenessIcon.spawned.transform.rotation` is set by the game based on
   `CameraController.Instance.cam.transform.position - targetPos`.  In VR, the game camera
   position = VR head position (set in `UpdatePose`), so this direction should be roughly correct
   already.  If arrows point the wrong way, we may need to override `spawned.rotation` to use
   VR head position instead of game camera position.

## Commit state

Changes are in the latest uncommitted build (built and deployed, not yet committed).
The previous committed state is `f2652ae` ("Revert force-test, restore action text fix").
