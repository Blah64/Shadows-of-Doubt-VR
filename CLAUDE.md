# SoDVR — Claude Project Instructions

Remember that VDXR and OpenXR are open source.

## What this is
A BepInEx 6 IL2CPP plugin adding full 6DOF VR support to **Shadows of Doubt** (Unity 2021.3.45f2, HDRP).
Target runtime: **VDXR** (`virtualdesktop-openxr.dll`) via Samsung Galaxy XR headset.

## Key constraints
- **IL2CPP** — no managed game assembly, all game interaction via Il2CppInterop
- **BepInEx 6** — plugin entry point is `Plugin.cs`, all patching in `Awake()`
- **Direct OpenXR** (not Unity XR subsystem) — we call VDXR's internal functions directly via vtable hacks
- **x86-64 machine code patching** — `PatchBytes()` helper does before/after byte verification and `VirtualProtect`
- **No coroutines** — IL2CPP coroutine interop is avoided; camera drives via `Update()`/`LateUpdate()`

## Repo layout
```
VRMod/
  SoDVR/
    Plugin.cs              — BepInEx entry; creates VROrigin GameObject on scene load
    OpenXRManager.cs       — All OpenXR init, VDXR vtable patching, stereo frame loop
    VR/VRCamera.cs         — Drives stereo rendering: xrWaitFrame/BeginFrame/Render/EndFrame
  SoDVR.csproj
  SoDVR.sln
```

## Build & deploy
```bash
cd "E:\SteamLibrary\steamapps\common\Shadows of Doubt\VRMod"
dotnet build SoDVR/SoDVR.csproj -c Release
cp SoDVR/bin/Release/net6.0/SoDVR.dll "../BepInEx/plugins/SoDVR.dll"
```
Log output: `E:\SteamLibrary\steamapps\common\Shadows of Doubt\BepInEx\LogOutput.log`

## Working conventions
- User says **"done"** = game has been run, log is ready to read
- Always read `LogOutput.log` immediately when user says "done"
- **Never skip PatchBytes verification** — the expected-bytes check is essential safety
- All x86-64 offsets in comments are from the **start of the function** (not the DLL base)
- Delegate all builds to assistant (user doesn't run builds manually)
- Prefer focused log dumps over wide ones; add dumps adjacent to patches being tested

## VDXR internal object model
- `robj` = `OpenXrRuntime*` obtained via `GetVDXRRuntimeObject()` (reads from VDXR's TLS/global)
- vtable layout (slot = byte_offset / 8):
  - slot 12  (0x060): `xrCreateSession` outer → CS-member at CS+0x1AA
  - slot 24  (0x0C0): `xrCreateSwapchain` outer → SC-member (vtable offset encoded at SC-outer+0x14E)
  - slot 60  (0x1E0): `xrGetD3D11GraphicsRequirementsKHR` outer → GR-member
- `robj+0x38`  = compositor handle (non-null, set during `xrCreateInstance`)
- `robj+0x940` = D3D11 device ptr (written by `CallInitD3D11Direct`)
- `robj+0x948` = D3D11 context ptr (written by `CallInitD3D11Direct`)
- `robj+0x990` = streaming texture pool ptr (populated by CSF when it runs fully)

## Active patch list
| Name | Location | Before | After | Purpose |
|------|----------|--------|-------|---------|
| CS-jbe | CS-outer+0x4C | `76 1B` | `90 90` | Skip session-type guard |
| GR-jbe/jbe2/jz/vtable | GR-outer | various | various | Allow GR path |
| SC-jbe1 | SC-outer+0x3D | `76 1D` | `90 90` | Skip outer guard 1 |
| SC-jbe2 | SC-outer+0x6F | `0F 86 CE...` | 6×NOP | Skip outer guard 2 |
| SC-jz | SC-outer+0x77 | `74 23` | `90 90` | Skip outer guard 3 |
| SC-revert-jne | SC-member+0x1FC | `E9 E4 06...90` | `0F 85 E3 06...` | Revert prior wrong patch |
| SC-skip-minus8 | SC-member+0x20A | `74 0A` | `EB 0A` | Skip -8 return guard |
| CSF-skip-earlyrtn | CSF+0x47 | `0F 85 CC 04 00 00` | 6×NOP | Let CSF run fully → populate robj+0x990 |
| ESF-jbe1 | ESF-outer+0x41 | `76 1D` | `90 90` | Skip ESF guard 1 |
| ESF-jbe2 | ESF-outer+0x73 | `0F 86 CE...` | 6×NOP | Skip ESF guard 2 |
| ESF-jz | ESF-outer+0x7B | `74 23` | `90 90` | Skip ESF guard 3 |
| ESI-jbe1 | ESI-outer+0x41 | `76 1D` | `90 90` | Skip ESI guard 1 (was causing ESI fill rc=-1) |
| ESI-jbe2 | ESI-outer+0x73 | `0F 86 CE...` | 6×NOP | Skip ESI guard 2 |
| ESI-jz | ESI-outer+0x7B | `74 23` | `90 90` | Skip ESI guard 3 |
