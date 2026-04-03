# Plan: Fix Left Hand Interaction Marker + Action Labels

## Context

Three issues with the left hand interaction marker from last build:
1. **Dot not visible** — `CreatePrimitive(Sphere)` with HDRP/Unlit material isn't rendering in the VR eye cameras (likely HDRP rendering layer issue)
2. **Label shows when out of range / misaimed** — our raycast uses no layer mask and no distance check; the game uses `Toolbox.Instance.interactionRayLayerMask` and per-object `GetReachDistance()` (base 1.85m + modifiers)
3. **No action labels** — need to show what actions are available (e.g., "Open Door", "Pick Up") like the game's `ControlsDisplayController` does

4. **Objective arrow not visible** — `UIPointerController.Update()` positions arrows using `CameraController.Instance.cam.WorldToScreenPoint()` → `ScreenPointToLocalPointInRectangle(firstPersonUI, screenPt, null)`. Passing `null` camera produces garbage for WorldSpace canvases (which `firstPersonUI`'s parent `GameCanvas` now is). Need to override arrow positions each frame using VR camera projection.

---

## Files to Modify

- `SoDVR/VR/VRCamera.cs`

---

## Part 1 — Fix raycast: layer mask + range check

### 1a. Cache `Toolbox.Instance.interactionRayLayerMask` (in DiscoverMovementSystem, after Player cache)

```csharp
// 5e. Cache interaction ray layer mask for left-hand marker raycast.
private int _interactionLayerMask;
try
{
    _interactionLayerMask = Toolbox.Instance.interactionRayLayerMask;
    Log.LogInfo($"[Movement] Interaction layer mask: {_interactionLayerMask}");
}
catch { _interactionLayerMask = ~0; } // fallback: hit everything
```

Also cache `GameplayControls.Instance.interactionRange`:
```csharp
private float _baseInteractionRange = 1.85f;
try
{
    _baseInteractionRange = GameplayControls.Instance.interactionRange;
    Log.LogInfo($"[Movement] Base interaction range: {_baseInteractionRange}");
}
catch { }
```

### 1b. Update `UpdateLeftInteractMarker()` raycast

Replace:
```csharp
float lRange = 5.0f;
...
if (Physics.Raycast(new Ray(lStart, lDir), out lHit, lRange))
```

With:
```csharp
float lRange = 12.0f;  // same as game's raycast distance
...
if (Physics.Raycast(new Ray(lStart, lDir), out lHit, lRange, _interactionLayerMask))
```

### 1c. Add reach distance check

After finding `InteractableController`, check if the hit distance is within interaction range:

```csharp
if (ic != null)
{
    hitIC = ic;
    // Check if within interaction range (game's GetReachDistance)
    float reachDist = _baseInteractionRange;
    try
    {
        if (ic.interactable != null)
            reachDist = ic.interactable.GetReachDistance();
    }
    catch { }
    if (lHit.distance <= reachDist)
        isInteractable = true;
    break;
}
```

---

## Part 2 — Replace dot with world-space canvas dot (visible in HDRP)

The 3D sphere primitive doesn't render in VR eye cameras (HDRP rendering pipeline issue). Replace with a tiny WorldSpace canvas containing an Image — same approach as the right cursor dot which DOES render.

### 2a. Replace dot fields

Remove: `_leftDot`, `_leftDotRenderer`, `_leftDotMat`
Add:
```csharp
private Canvas?    _leftDotCanvas;    // tiny WorldSpace canvas for aim dot
private Image?     _leftDotImage;     // dot image (for color changes)
```

### 2b. Replace dot creation

Replace `CreatePrimitive(Sphere)` with:
```csharp
var dotCanvasGO = new GameObject("VRLeftDotCanvas");
dotCanvasGO.layer = UILayer;
UnityEngine.Object.DontDestroyOnLoad(dotCanvasGO);
var dotCanvas = dotCanvasGO.AddComponent<Canvas>();
dotCanvas.renderMode = RenderMode.WorldSpace;
dotCanvas.sortingOrder = 201;
var dotCanvasRT = dotCanvasGO.GetComponent<RectTransform>();
dotCanvasRT.sizeDelta = new Vector2(20f, 20f);
dotCanvasGO.transform.localScale = Vector3.one * 0.001f; // 20px * 0.001 = 0.02m = 2cm

var dotImgGO = new GameObject("DotImg");
dotImgGO.layer = UILayer;
dotImgGO.transform.SetParent(dotCanvasGO.transform, false);
var dotImg = dotImgGO.AddComponent<Image>();
dotImg.raycastTarget = false;
dotImg.color = new Color(0f, 64f, 64f, 1f); // HDR cyan
var dotImgRT = dotImgGO.GetComponent<RectTransform>();
dotImgRT.anchorMin = Vector2.zero; dotImgRT.anchorMax = Vector2.one;
dotImgRT.sizeDelta = Vector2.zero;
// Make it circular via sprite (or just use square — visible enough)

_leftDotCanvas = dotCanvas;
_leftDotImage = dotImg;
dotCanvasGO.SetActive(false);
```

### 2c. Update dot positioning in UpdateLeftInteractMarker

```csharp
if (_leftDotCanvas != null)
{
    if (didHit)
    {
        _leftDotCanvas.transform.position = lHit.point + lHit.normal * 0.005f; // slight offset from surface
        // Billboard toward VR head
        if (_leftCam != null)
            _leftDotCanvas.transform.rotation = _leftCam.transform.rotation;
        // Color: green when interactable, cyan otherwise
        if (_leftDotImage != null)
        {
            _leftDotImage.color = isInteractable
                ? new Color(0f, 64f, 0f, 1f)   // HDR green
                : new Color(0f, 64f, 64f, 1f);  // HDR cyan
        }
        if (!_leftDotVisible) { _leftDotCanvas.gameObject.SetActive(true); _leftDotVisible = true; }
    }
    else if (_leftDotVisible)
    {
        _leftDotCanvas.gameObject.SetActive(false);
        _leftDotVisible = false;
    }
}
```

---

## Part 3 — Show action text in label when in range

When pointing at an interactable within range, show available actions from `InteractionController.Instance.currentInteractions` instead of just the object name.

### 3a. Update label text logic in UpdateLeftInteractMarker

```csharp
if (isInteractable && hitIC != null)
{
    // Build label: object name + available actions
    string objName = "";
    try
    {
        if (hitIC.interactable != null)
            objName = hitIC.interactable.GetName();
        if (string.IsNullOrEmpty(objName))
            objName = hitIC.gameObject.name ?? "?";
    }
    catch { objName = hitIC.gameObject.name ?? "?"; }

    // Get action text from game's current interaction state
    // The game populates InteractionController.Instance.currentInteractions
    // when it detects an interactable via its own raycast (which we redirect
    // to left controller during trigger hold). We read whatever the game computed.
    string actionText = "";
    try
    {
        var ic2 = InteractionController.Instance;
        if (ic2 != null && ic2.currentInteractions != null)
        {
            foreach (var kvp in ic2.currentInteractions)
            {
                if (kvp.Value?.currentSetting == null) continue;
                if (!kvp.Value.currentSetting.enabled || !kvp.Value.currentSetting.display) continue;
                string aText = kvp.Value.actionText ?? "";
                if (!string.IsNullOrEmpty(aText))
                {
                    if (actionText.Length > 0) actionText += " | ";
                    actionText += aText;
                }
            }
        }
    }
    catch { }

    string fullLabel = string.IsNullOrEmpty(actionText) ? objName : $"{objName}\n{actionText}";
    if (_leftLabelText != null)
        _leftLabelText.text = fullLabel;
    // ... position + billboard as before
}
```

**Note:** The game only populates `currentInteractions` when its own `InteractionRaycastCheck` finds a target. Since we redirect Camera.main to the left controller only during trigger HOLD, the game's interaction data may be stale or aimed at what the head is looking at. To make the label accurate, we should check if the game's current target matches our raycast target:

```csharp
// Only show actions if the game is also looking at this object
bool gameAimedAtSame = false;
try
{
    var ic2 = InteractionController.Instance;
    if (ic2 != null && ic2.currentLookingAtInteractable == hitIC)
        gameAimedAtSame = true;
}
catch { }
```

If game is NOT aimed at same, show only the object name. If it IS, show name + actions.

---

## Part 4 — Clean up dead code

Remove `_leftDot` (sphere primitive), `_leftDotRenderer`, `_leftDotMat` fields and their creation code. Remove unused `LeftDotDefault`/`LeftDotInteract` Color constants (replaced by inline HDR colors on Image).

---

## Part 5 — Fix HUD objective arrow (UIPointerController)

The objective arrow is a `UIPointerController` component spawned by `InterfaceController.AddUIPointer(objective)` as a child of `InterfaceController.Instance.uiPointerContainer` (which is inside `firstPersonUI` → `GameWorldDisplay`).

**Root cause:** The game's `UIPointerController.Update()` positions arrows via:
1. `CameraController.Instance.cam.WorldToScreenPoint(objective.queueElement.pointerPosition)` → screen coords
2. `RectTransformUtility.ScreenPointToLocalPointInRectangle(firstPersonUI, screenPt, null)` → local coords

Step 2 passes `null` camera, which works for ScreenSpace-Overlay canvases but produces **garbage** for WorldSpace canvases. Since we converted `GameCanvas` (parent of `firstPersonUI`) to WorldSpace, all arrow positions are wrong.

**Fix:** Each frame, find active `UIPointerController` instances, read their `objective.queueElement.pointerPosition` (world position), re-project using VR camera, and override `rect.anchoredPosition`.

### 5a. Cache `InterfaceController` references (in DiscoverMovementSystem)

```csharp
private InterfaceController? _interfaceCtrl;
private RectTransform? _firstPersonUI;  // = GameWorldDisplay, sd=100x100

try
{
    _interfaceCtrl = UnityEngine.Object.FindObjectOfType<InterfaceController>();
    if (_interfaceCtrl != null)
    {
        _firstPersonUI = _interfaceCtrl.firstPersonUI;
        Log.LogInfo($"[Movement] InterfaceController found, firstPersonUI={(_firstPersonUI != null ? _firstPersonUI.gameObject.name : "null")}");
    }
}
catch { }
```

### 5b. New method: `UpdateUIPointers()` — called each frame from Update

```csharp
private void UpdateUIPointers()
{
    if (_interfaceCtrl == null || _firstPersonUI == null || _leftCam == null) return;

    var container = _interfaceCtrl.uiPointerContainer;
    if (container == null) return;

    Vector2 fpSize = _firstPersonUI.sizeDelta; // typically (100, 100)

    for (int i = 0; i < container.childCount; i++)
    {
        Transform child = container.GetChild(i);
        if (child == null || !child.gameObject.activeSelf) continue;

        UIPointerController upc = null;
        try { upc = child.GetComponent<UIPointerController>(); }
        catch { continue; }
        if (upc == null) continue;

        // Get target world position
        Vector3 worldPos = Vector3.zero;
        bool hasTarget = false;
        try
        {
            var obj = upc.objective;
            if (obj?.queueElement != null && obj.queueElement.usePointer)
            {
                worldPos = obj.queueElement.pointerPosition;
                hasTarget = true;
            }
        }
        catch { continue; }
        if (!hasTarget) continue;

        // Project world position to VR camera viewport (0-1)
        Vector3 vp = _leftCam.WorldToViewportPoint(worldPos);

        // Behind camera: flip to opposite edge
        if (vp.z < 0f)
        {
            vp.x = 1f - vp.x;
            vp.y = 1f - vp.y;
            vp.z = 0f;
        }

        // Check if on-screen (within viewport with small margin)
        bool onScreen = vp.x > 0.05f && vp.x < 0.95f && vp.y > 0.05f && vp.y < 0.95f && vp.z > 0f;

        // Clamp to edge when off-screen (arrow sits at border)
        if (!onScreen)
        {
            // Center-relative coords
            float cx = vp.x - 0.5f;
            float cy = vp.y - 0.5f;
            float maxAbs = Mathf.Max(Mathf.Abs(cx), Mathf.Abs(cy));
            if (maxAbs > 0.001f)
            {
                float scale = 0.45f / maxAbs; // clamp to 0.45 from center = 0.05..0.95
                cx *= scale;
                cy *= scale;
            }
            vp.x = cx + 0.5f;
            vp.y = cy + 0.5f;
        }

        // Map viewport to firstPersonUI local coords
        // firstPersonUI has pivot at center, sd=(100,100)
        float localX = (vp.x - 0.5f) * fpSize.x;
        float localY = (vp.y - 0.5f) * fpSize.y;

        // Override the arrow's position
        try
        {
            if (upc.rect != null)
                upc.rect.anchoredPosition = new Vector2(localX, localY);
        }
        catch { }

        // Rotation: point arrow toward target direction when off-screen
        if (!onScreen)
        {
            float angle = Mathf.Atan2(vp.y - 0.5f, vp.x - 0.5f) * Mathf.Rad2Deg;
            try { upc.rect.localRotation = Quaternion.Euler(0f, 0f, angle - 90f); }
            catch { }
        }
        else
        {
            try { upc.rect.localRotation = Quaternion.identity; }
            catch { }
        }
    }
}
```

### 5c. Call UpdateUIPointers from Update (after UpdateLeftInteractMarker)

```csharp
UpdateUIPointers();
```

**Note:** This runs every frame AFTER the game's `UIPointerController.Update()` sets its garbage positions, so our override wins the race. The game's Update runs during normal MonoBehaviour update, and our VRCamera.Update runs later (script execution order or same frame, our write is last).

---

## Design Notes

**Why WorldSpace Canvas dot instead of 3D sphere primitive?**
`CreatePrimitive(Sphere)` with HDRP/Unlit material doesn't render in our VR eye cameras (confirmed by user: dot not visible). WorldSpace Canvas with Image component DOES render — it goes through Unity's UI rendering pipeline which our HDRP cameras handle correctly (same as all other in-game UI elements).

**Why use the game's interaction layer mask?**
Without a layer mask, `Physics.Raycast` hits everything — walls, floors, invisible triggers, environmental colliders that happen to be children of interactable objects. The game uses `Toolbox.Instance.interactionRayLayerMask` which only includes layers with actual interactable objects. Using the same mask prevents false positives.

**Why check `GetReachDistance()` instead of fixed range?**
The game's interaction range is 1.85m base, but individual objects have `rangeModifier` and sync disk upgrades can increase range. Using `GetReachDistance()` matches what the game considers "in range" exactly.

**Why read `InteractionController.Instance.currentInteractions` for action text?**
Building our own action text from `interactable.currentActions` would require replicating the game's complex action resolution logic (priority, enabled/disabled, illegal checks, etc.). Reading the game's already-computed `currentInteractions` dictionary gets us the correct actions for free.

**Why override UIPointerController positions instead of patching the game's Update?**
IL2CPP — we can't patch `UIPointerController.Update()`. Instead, we override positions each frame after the game sets them. Our VRCamera.Update runs in the same frame; we just write last. The game already handles spawning/despawning arrows, fade-in/out, and linking to objectives — we only fix the broken projection math.

**Why `WorldToViewportPoint` instead of `WorldToScreenPoint`?**
Viewport coords (0-1) are resolution-independent and map cleanly to `firstPersonUI`'s local space (sd=100×100). `WorldToScreenPoint` gives pixel coords which would need dividing by pixel dimensions — an unnecessary extra step.

**Why clamp off-screen arrows to edge?**
The game's arrows already do this (clamped to a circle within `firstPersonUI`). Our viewport-based approach naturally handles this with the clamp logic. The arrow rotates to point toward the target direction when sitting at the edge.

---

## Verification

1. Build & deploy
2. Point left controller at a door:
   - Within ~2m: green dot appears at hit point, label shows "Front Door" + "Open" (if game aimed)
   - Beyond ~2m: cyan dot, no label (or just name with no actions)
   - Way off to the side of door: no dot, no label
3. Point at items on table: should only show label when close enough
4. Left trigger interact: doors should open, items should pick up (regression check)
5. No floating text appearing when pointing at random walls
6. Dot visible as small cyan/green square at surface contact point
7. Active objective: arrow visible on HUD pointing toward objective location
8. Turn away from objective: arrow clamps to HUD edge, rotates to point correct direction
9. Walk toward objective: arrow tracks correctly in 3D space
10. No regression: interaction, movement, UI clicks all still working
