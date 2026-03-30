# Review Of PLAN-Claude.md

Date: 2026-03-27


## Review of PLAN-1.md vs PLAN-2.md

Date: 2026-03-27

Both `PLAN-1.md` and `PLAN-2.md` provide approaches for Phase 8.1 (VR Settings Panel Skeleton) based on the constraints outlined in `CLAUDE.md` and `HANDOVER.md`. Both correctly handle the `DontDestroyOnLoad` requirement and the `_ownedCanvasIds` registration order.

### Comparison Points

1. **API Design & Coupling**:
   - **PLAN-1.md** proposes handling the recentering logic directly in `VRCamera`'s F10 handler.
   - **PLAN-2.md** introduces a callback pattern `Init(..., id => _positionedCanvases.Remove(id))` so `VRSettingsPanel` can trigger recentering independently during `Show()` or `Toggle()`. **PLAN-2's approach is superior** as it decouples the panel's internal visibility logic from the camera script.

2. **IL2CPP Constraints**:
   - `HANDOVER.md` explicitly warns against using `AddComponent<RectTransform>()` because it always returns null in this IL2CPP context. 
   - **PLAN-2.md** explicitly bakes this constraint into its implementation rules (e.g., adding `Image` first), whereas `PLAN-1.md` omits this critical detail. Because of the finicky nature of IL2CPP interop, PLAN-2.md is much safer.

3. **Level of Detail**:
   - **PLAN-1.md** is more of a high-level summary. It specifies *what* needs to be done.
   - **PLAN-2.md** provides concrete C# API signatures, a full UI hierarchy with expected canvas sizing and sorting specs, and the exact lines to delete/replace in `VRCamera.cs`. This makes it immediately actionable for developer use.

### Conclusion

**PLAN-2.md** is the clear winner and the recommended path forward. It strictly follows the IL2CPP constraints from `HANDOVER.md` and provides a more robust API for decoupling the settings panel from `VRCamera.cs`.

**Recommendation**: Proceed with the implementation exactly as laid out in `PLAN-2.md`, while remaining mindful of the previous `GraphicRaycaster` assertion recommendation.
