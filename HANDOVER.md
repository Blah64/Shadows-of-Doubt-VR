# SoDVR — Technical Handover

**Date**: 2026-03-25
**Phase**: 5 (Stereo rendering) — swapchain texture path being fixed

---

## Current Status

The stereo rendering loop is fully running:
- `xrCreateSwapchain` returns rc=0, 3 images per eye (3648×3936 format=87/DXGI_FORMAT_R8G8B8A8_UNORM_SRGB)
- `xrWaitFrame/BeginFrame/LocateViews/EndFrame` all working
- VRCamera renders stereo frames and reports "Stereo frame #N"
- **BLOCKER**: Swapchain image texture pointers are `0x0` (null) — copy src→dst does nothing, headset sees black

The null textures are caused by SC-member taking the wrong branch at +0x242 (streaming/null-texture path instead of compositor/real-texture path).

---

## Root Cause Chain

```
xrCreateSwapchain
  └─ SC-outer (wrapper, 3 guards patched: SC-jbe1/2, SC-jz)
       └─ SC-member (VDXR's real implementation)
            ├─ SC-member+0x225: CALL fn(robj, robj) → returns V in RAX (AL = 0x0F in streaming mode)
            ├─ SC-member+0x240: SUB AL, 0x0F       → AL = 0 (since V == 0x0F)
            ├─ SC-member+0x242: JNE +0x3A9         → NOT TAKEN (AL==0, so ZF=1)
            │                                          (if AL≠0, JNE IS taken → compositor path)
            ├─ [fall-through] XOR EAX,EAX          → RAX = 0
            ├─ SC-member+0x267: CMP [robj+0x990],0  → robj+0x990 is always NULL
            └─ → EBX = 6 → null texture pointers returned
```

**Why robj+0x990 is always NULL**: The compositor setup function (CSF, called at CS-member+0x1AA)
checks `[robj+0x38] != 0` at CSF+0x42; since `robj+0x38` (compositor handle) is pre-set
during `xrCreateInstance`, CSF always takes the JNE at CSF+0x47 to early return (+0x51A)
without populating `robj+0x990`. We confirmed this by calling CSF directly — result=1 (success)
but `robj+0x990` still 0 afterwards.

**The fix**: Force the JNE at SC-member+0x242 to unconditional JMP → always take the compositor
path at +0x3A9, which uses `robj+0x38` (confirmed non-null = 0x26CB4467200).

---

## The Current Patch (SC-force-compositor)

```
SC-member+0x242:
  Before: 0F 85 61 01 00 00  = JNE rel32(0x161) → target +0x3A9  [conditional]
  After:  E9 62 01 00 00 90  = JMP rel32(0x162) → target +0x3A9  [unconditional] + NOP

  Note: E9 is 5 bytes (not 6 like 0F 85), so rel32 is 0x162 (not 0x161) to reach same target.
```

**This patch was built and deployed but NOT yet run** as of this handover.

Previous attempt failed because the offset was 0x243 instead of 0x242 (off by one):
- log said `fn+0x243 = 0x85, expected 0x0F — skipping`
- The byte 0x0F (JNE prefix) is at +0x242; 0x85 (second byte) is at +0x243

---

## What to Check in the Log After Running

1. `SC-force-compositor: patched` — patch applied successfully
   OR `SC-force-compositor: fn+0x242 = XX, expected 0x0F — skipping` → need to investigate

2. SC+0x230..+0x260 dump — verify instruction layout:
   - Should see `0F 85 61 01 00 00` at bytes corresponding to +0x242 in the dump row
   - Confirms JNE boundary before patching output is shown

3. `copy: src=0x... dst=0x...` — dst should now be **non-zero** (real D3D11 texture pointers)

4. `Stereo frame #N` continues — game still running

5. Watch for any crash or xrEndFrame error after the patch lands

---

## If SC-force-compositor Lands at Wrong Place

The SC+0x230..+0x260 dump will show the actual instruction boundaries.
Calculate the correct offset for `0F 85` in that region.

Also check: does `0F 85 61 01 00 00` actually appear there, or is the rel32 different?
The displacement `61 01 00 00` was inferred from the previous session's dump (which we no
longer have). If it's wrong, the dump will show the true bytes and we can recalculate.

---

## If Patch Applies but dst Still 0x0

The compositor path at SC-member+0x3A9 may need additional preconditions. From the dump
already collected (SC+0x3A0..+0x480):

```
SC+0x3A0: EB 03 45 8B F2 85 DB 75 0A B8 E6 FF FF FF E9 37
SC+0x3B0: 05 00 00 8B 56 34 41 B8 02 00 00 00 83 FA 06 45
SC+0x3C0: 0F 45 C2 44 89 44 24 7C 4C 8B 4E 10 41 0F B6 C1
...
```

Key: at +0x3A5: `85 DB` = TEST EBX,EBX; `75 0A` = JNE if EBX≠0.
If EBX==0 at entry, it falls to `B8 E6 FF FF FF` (MOV EAX,-26) and returns error.
If EBX≠0, continues to +0x3B3 (real compositor texture allocation).

**What is EBX at +0x242?** — Unknown. Need to check what SC-member stores in EBX before
the JNE. If the compositor path always exits with error (EBX=0), need to understand what
sets EBX to non-zero in normal VDXR flow (non-streaming mode).

---

## What robj Looks Like (confirmed from logs)

```
robj+0x038 = 0x26CB4467200  ← compositor handle (non-null, key for compositor path)
robj+0x938 = 0x22CC         ← some flags
robj+0x940 = <D3D11 device ptr>
robj+0x948 = <D3D11 context ptr>
robj+0x990 = 0x0            ← always null (CSF short-circuits)
robj+0xDB0 = <ptr>
robj+0xDF0 = 0x2
robj+0xE38 = 0xFFFFFFFF
robj+0xF28 = <ptr>
```

---

## Pre-Run Setup (in Plugin.cs / OpenXRManager.cs)

Before SC-member runs (called each xrCreateSwapchain), we ensure:
```csharp
SetRuntimeGraphicsRequirements(dev);   // writes graphicsReqQueried=1 + LUID to robj
CallInitD3D11Direct(dev);              // stores D3D11 device/context at robj+0x940/948
CallCompositorSetupDirect();           // calls CS-member+0x1AA (CSF) — returns 1, noop
```

---

## File Locations

| File | Purpose |
|------|---------|
| `VRMod/SoDVR/OpenXRManager.cs` | All patching, VDXR direct calls, stereo frame API |
| `VRMod/SoDVR/VR/VRCamera.cs` | Stereo render loop: cameras, RenderTexture, CopyEye |
| `VRMod/SoDVR/Plugin.cs` | BepInEx entry point, scene load hook |
| `BepInEx/plugins/SoDVR.dll` | Deployed plugin |
| `BepInEx/LogOutput.log` | Runtime log |

---

## Phase Roadmap

- [x] Phase 1–4: OpenXR init, session, swapchain creation (all rc=0)
- [ ] **Phase 5 (current)**: Real D3D11 textures in swapchain images → see image in headset
- [ ] Phase 6: Input — head tracking → game camera, controller → movement/interaction
- [ ] Phase 7: UI world-space conversion + laser pointer
- [ ] Phase 8–11: Full input mapping, game-specific interactions, comfort, polish

---

## Key Terminology

| Term | Meaning |
|------|---------|
| `robj` | VDXR's `OpenXrRuntime*` internal object |
| SC-member | vtable slot 24 inner function = real xrCreateSwapchain implementation |
| CS-member | vtable slot 12 inner function = real xrCreateSession implementation |
| CSF | Compositor Setup Function, called from CS-member+0x1AA |
| GR-member | vtable slot 60 = xrGetD3D11GraphicsRequirementsKHR inner |
| `PatchBytes(fn, off, expected, patch, name)` | Helper: VirtualProtect RW, verify `expected` at `fn+off`, write `patch`, log result |
| EBX=6 | Code path inside SC-member that returns null texture descriptors |
