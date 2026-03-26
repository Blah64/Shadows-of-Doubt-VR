# Handover prompt — paste this to start a new session

---

I'm working on a BepInEx 6 IL2CPP VR mod for Shadows of Doubt (Unity 2021.3.45f1, HDRP).
The mod is called SoDVR. Read the project instructions and handover doc first:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\CLAUDE.md`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\HANDOVER.md`

Then read the two main source files:

- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\OpenXRManager.cs`
- `E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod\SoDVR\VR\VRCamera.cs`

We are starting **Phase 7: controller input**.

The goal is a laser-pointer style interaction:
1. Track the right controller pose via OpenXR action sets
2. Draw a laser beam `LineRenderer` from the controller forward
3. Detect intersections with WorldSpace canvases using Unity's `GraphicRaycaster`
4. Trigger button = send a pointer-click event to whatever the ray hits

All the technical details you need (action set setup, P/Invoke structs, per-frame calls, interaction profile path, coord-system flip) are in HANDOVER.md under "Phase 7".

The target headset is a Samsung Galaxy XR paired through Virtual Desktop — VDXR presents it as an Oculus Touch controller, so use interaction profile `/interaction_profiles/oculus/touch_controller`.

Work step by step. Build and deploy with:
```
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
When I say "done" it means I've run the game and the log is ready at:
`E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`
Always read it immediately.

Start by reading the four files above, then propose the first implementation step.
