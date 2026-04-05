SoDVR — VR Controller Mapping
==============================

RIGHT CONTROLLER
----------------
Trigger              UI click (point at a menu canvas and pull)
A button             Jump (Space), or Right-click when aiming at a canvas
B button             Notebook / Map (hold to show — Tab held while B held)
                     Middle-click drag when aiming at a canvas
Grip                 Drag canvas panels (notes, map, location details, bio display)
Thumbstick left/right  Rotate (snap or smooth — configurable in VR Settings)
Thumbstick up/down   Scroll (when VR Settings panel is open)
Thumbstick click     Flashlight toggle

LEFT CONTROLLER
---------------
Menu button          Pause menu (ESC)
Y button             Alternate / Use item (F)
X button             Crouch toggle (C)
Trigger              World interact — doors, objects, NPCs (aims from left controller)
Grip                 Right mouse button (pick up evidence, secondary interact)
Thumbstick           Move player (head-relative, walk speed configurable)
Thumbstick click     Sprint toggle (auto-stops when stick returns to centre)

VR SETTINGS PANEL (F10 or in-game Settings button)
--------------------------------------------------
Opens on any canvas; has 5 tabs — Graphics, Audio, Controls, General, VR.

VR tab options:
  - Turn Mode: Snap (discrete angles) or Smooth (proportional to stick)
  - Snap Angle: 15°, 22.5°, 30°, 45°, 60°, 90° (default 30°)
  - Smooth Speed: 60–240°/s (default 120°/s)
  - Move Speed: 2–8 m/s (default 4.0 m/s)
  - Sprint Multiplier: 1.4×–3.0× (default 1.8×)
  - Left Laser pointer toggle
  - Item Hand: which hand holds picked-up items (left/right)

Right stick controls scrolling while the VR Settings panel is open.

NOTES
-----
- Sprint (left thumbstick click) holds Shift while active.
  It stops automatically when you release the left stick.
- Right trigger fires UI clicks when pointing at a canvas,
  and world-interact (LMB) when nothing is in view — use left
  trigger for reliable world interaction.
- B button: hold to keep the map/notebook open; release B to close.
  Right stick B-drag pans the map; trigger click opens evidence notes.
- A button right-click opens context menus on the case board and map.
- Right grip drag repositions floating canvases in 6DOF. Positions are
  saved and restored when the case board is reopened.
- Supported runtimes: any OpenXR runtime (Oculus Touch, Valve Index,
  HTC Vive, Windows Mixed Reality, and KHR Simple fallback bindings
  are all active). Tested with Virtual Desktop (VDXR).
