# SoDVR Failure Log

Date: 2026-03-27

Purpose: record approaches that did not solve the VR menu/settings readability problem, so future work does not repeat them blindly.

## Scope

This file covers two related problems:

1. Menu/nav button text is too dark or low-contrast in VR.
2. Settings/options text exists but is still invisible or unreadable in VR.

It includes both Claude-era work and the later follow-up work in this session.

## Things We Now Know For Sure

- The settings/options text is not missing.
  Runtime dumps proved strings like `Resolution`, `Display Mode`, `Motion Blur`, `Bloom`, and `Color Grading` are active under `MenuCanvas`.
- The settings/options text is not simply clipped by `RectMask2D`, `Mask`, `CanvasGroup.alpha`, `CanvasRenderer.cull`, or `CanvasRenderer` rect clipping.
  Those were all explicitly inspected and/or disabled, and the text still did not become visible.
- The problem is not solved by just brightening TMP vertex colors or changing TMP material properties.
- The current fallback-clone path is not reliable yet.
  Duplicated fallback labels were created, but multiple runs showed bad placement/local transform behavior.

## Claude-Era Approaches That Did Not Solve It

### 1. Per-camera HDRP exposure override

What was tried:
- `FrameSettingsField.ExposureControl = false`
- UI-only HDRP volume override (`VRExposureOverride`)
- fixed exposure / tonemapping override experiments on UI cameras

Result:
- `ExposureControl` did not persist per camera in HDRP 12 IL2CPP.
- The UI-only volume path had no visible effect.
- This did not fix readable menu/settings text.

Evidence:
- documented in [CLAUDE.md](/E:/SteamLibrary/steamapps/common/Shadows%20of%20Doubt/VRMod/CLAUDE.md)
- documented in [HANDOVER.md](/E:/SteamLibrary/steamapps/common/Shadows%20of%20Doubt/VRMod/HANDOVER.md)

Conclusion:
- Do not restart from “special UI cameras + special exposure volume” as the main plan.

### 2. Separate UI overlay camera architecture

What was tried:
- dedicated UI eye cameras
- scene cameras excluding UI layer
- UI cameras rendering only layer 5

Result:
- Increased complexity and made it harder to isolate whether failures came from camera path, exposure, TMP materials, or our own UI mutation.
- Did not produce reliably readable settings text.

Conclusion:
- This architecture did not pay for its complexity.

### 3. Global UI mutation strategy

What was tried:
- aggressive shader swapping
- broad material replacement
- repeated rescans restoring/replacing materials
- broad UI rewriting instead of narrow fixes

Result:
- Too many moving parts.
- Hard to reason about what was helping versus what was fighting Unity/TMP rebuild behavior.
- Did not solve invisible settings text.

Conclusion:
- Avoid returning to large, cross-cutting UI mutation passes.

## Follow-up Approaches In This Session That Did Not Solve It

### 4. Simplified main-eye-camera UI path by itself

What was tried:
- removed dedicated UI overlay cameras from active flow
- rendered UI through main eye cameras
- kept world-space canvases
- kept tonemapping disabled on eye cameras

Result:
- Simplified architecture, but did not by itself solve settings/options readability.

Conclusion:
- The simplification was still the right cleanup, but it was not sufficient.

### 5. Forced TMP whitening / brightness boosts

What was tried:
- `TMP_Text.color` boosts
- bright `Graphic.color`
- boosted `_FaceColor`
- boosted `_Color`

Result:
- Logs proved the boosts landed.
- Example dumps showed settings labels with bright values like `tmp.col=(10,10,10,1)` and later `tmp.col=(20,20,20,1)`.
- Even with bright values, settings labels remained invisible in-headset.

Conclusion:
- “Make TMP brighter” is not the missing piece by itself.

### 6. TMP outline / underlay / readable-material strengthening

What was tried:
- forced face color
- forced outline color
- forced outline width
- forced underlay color / offsets / dilate
- repeated `fontMaterial` / `fontSharedMaterial` reassignment
- repeated `canvasRenderer.SetMaterial(...)`

Result:
- Runtime logs showed the live renderer material often had the expected values.
- Example: `crFace=(8,8,8,1)` and `crOW=0.250` or stronger.
- Despite that, settings labels were still invisible.

Conclusion:
- Correct-looking TMP material state in logs does not guarantee visible text in the headset.

### 7. Shader fallback / shader swapping as the answer

What was tried:
- targeted or broader fallback behavior around TMP materials / shader treatment

Result:
- Did not produce a reliable, final fix.
- In practice, TMP and CanvasRenderer material ownership kept shifting.

Conclusion:
- Shader swapping alone is not a trustworthy endgame here.

### 8. Global mask and clip relaxation

What was tried:
- disabled `Mask`
- disabled `RectMask2D`
- set text to non-maskable
- neutralized stencil masking
- disabled rect clipping on `CanvasRenderer`

Result:
- Logs confirmed these changes applied.
- Example: `MenuClipRelax 'MenuCanvas': Mask=35 ... TextMaskable=464`
- Settings labels still remained invisible.

Conclusion:
- The settings text problem is not primarily caused by these mask/clip systems.

### 9. CanvasGroup alpha and renderer cull forcing

What was tried:
- force `CanvasGroup.alpha = 1`
- force `CanvasRenderer.cull = false`

Result:
- Logs showed settings labels with `grpA=1.00` and `crCull=off`.
- Still invisible.

Conclusion:
- Not a hidden alpha or renderer-cull issue.

### 10. Z-order and local Z pushing

What was tried:
- pushed text forward relative to background
- later pushed scroll-view settings text even farther forward

Result:
- Logs showed `Back` at `lz=-0.030`, settings labels later at `lz=-0.080`.
- Still invisible.

Conclusion:
- More negative local Z alone does not solve the settings-label visibility problem.

### 11. Fallback TMP clones under `MenuCanvas`

What was tried:
- created duplicate `TextMeshProUGUI` fallback labels (`VRReadableFallbackText`)
- reparented outside the scroll-view subtree
- tried to use them as a more controllable visible overlay

Result:
- Fallback objects were created.
- But several runs showed bad transform placement, including absurd local positions / bad local Z, and they still did not solve readability.
- Example bad state from logs: fallback entries under `MenuCanvas` with `lz=-13.343`.

Conclusion:
- The fallback-clone idea is not solved and should not be assumed to work just because objects exist.

### 12. Repeated rescans as the main recovery mechanism

What was tried:
- frequent rescans to catch late-opened settings text and reapply mutations

Result:
- Rescans did change state, but did not converge to a readable result.
- They also made it harder to know which state was authoritative.

Conclusion:
- Rescans are not a substitute for a stable rendering plan.

## Important Observations Worth Keeping

These are not solutions, but they are useful facts:

- The settings labels live under a different hierarchy than the visible `Back` text.
  Examples from logs:
  - `Back`: `Text <- Back <- ButtonArea <- GraphicsSettingsPanel <- MainMenu <- MenuCanvas`
  - `Resolution`: `LabelText <- ScreenResolutionDropdown <- Content <- Viewport <- Scroll View <- Components`
- The settings labels are ordinary TMP controls, not a hidden special system.
  This was confirmed from the Mono inspection of `MainMenuController`, `DropdownController`, `ToggleController`, `ButtonController`, and `MenuAutoTextController`.
- The game may load the wrong plugin if both deploy layouts exist.
  Keep both of these in sync when testing:
  - [SoDVR.dll](/E:/SteamLibrary/steamapps/common/Shadows%20of%20Doubt/BepInEx/plugins/SoDVR.dll)
  - [SoDVR.dll](/E:/SteamLibrary/steamapps/common/Shadows%20of%20Doubt/BepInEx/plugins/SoDVR/SoDVR.dll)

## Recommendation Going Forward

Do not spend another long cycle trying to “fix the original settings UI text rendering” with more TMP/material/mask/canvas hacks.

Best next direction:
- keep the simpler main-eye-camera UI path
- stop treating the built-in settings scroll view as the thing we must render correctly in VR
- build a VR-native settings panel that reads/writes the same underlying settings data

Reason:
- We now have strong evidence that the built-in settings UI path is costly to fight and low-confidence to stabilize.
- A VR-native replacement gives control over font, layout, contrast, interaction, and rendering path.
