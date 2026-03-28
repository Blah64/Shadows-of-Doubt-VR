using BepInEx.Logging;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace SoDVR.VR;

/// <summary>
/// Drives the OpenXR stereo rendering loop on the Unity main thread.
///
/// Frame sequence (per Unity frame):
///   Update():     xrPollEvent → [if stereo ready] xrWaitFrame → xrBeginFrame → xrLocateViews → apply poses
///   LateUpdate(): _leftCam.Render() → _rightCam.Render() → D3D11 copy → xrEndFrame
///
/// Before stereo is ready, Update() submits empty frames and waits for the session
/// to reach XR_SESSION_STATE_SYNCHRONIZED (state ≥ 3).  That state transition signals
/// that Virtual Desktop's streaming infrastructure (including robj+0x990) is live, so
/// xrCreateSwapchain will allocate real D3D11 textures.
///
/// Using explicit Camera.Render() in LateUpdate avoids the IL2CPP coroutine interop
/// complexity.  Both cameras are disabled for automatic rendering; we drive them manually.
///
/// D3D11 synchronisation: CopyResource is enqueued on the D3D11 immediate context
/// after the Render() commands, so the GPU processes them in order — no fence needed.
///
/// Camera rig hierarchy:
///   VROrigin (this.transform)
///     └─ CameraOffset
///          ├─ LeftEye  Camera → _leftRT
///          └─ RightEye Camera → _rightRT
/// </summary>
public class VRCamera : MonoBehaviour
{
    private static ManualLogSource Log => Plugin.Log;

    private Transform     _cameraOffset = null!;
    private Camera        _leftCam    = null!;   // main eye camera — renders scene + UI
    private Camera        _rightCam   = null!;
    private RenderTexture _leftRT     = null!;
    private RenderTexture _rightRT    = null!;
    private Transform     _gameCam    = null!;   // original game camera transform; we follow its world position

    // Unity built-in UI layer.  Canvas GameObjects default to this layer; all their
    // children inherit it.  Scene cameras exclude it so only UI cameras render UI.
    private const int UILayer       = 5;
    // Render throttle: call Camera.Render() every N stereo frames.
    // 1 = every frame (full quality). 2 = every other frame (half GPU load, slight judder).
    // The swapchain copy still runs every frame, so head tracking stays smooth via ATW.
    private const int RenderEveryNFrames = 1;

    // ── UI / Canvas constants ─────────────────────────────────────────────────
    private const float UIDistance       = 2.0f;    // metres in front of head
    private const float UIVerticalOffset = 0.0f;    // metres up/down from eye level
    private const float UICanvasScale    = 0.0015f; // world-units per canvas pixel (1920px → 2.88 m wide)
    private const int   UICanvasScanRate = 30;      // Unity frames between canvas scans
    // Alpha applied to any Graphic whose GameObject name contains "background".
    // Makes the canvas backdrop semi-transparent so buttons and text show through it
    // even when HDRP's distance sort happens to render the background last.
    private const float UIBackgroundAlpha = 0.25f;
    private const float UITextBrightnessBoost  = 4.0f;
    // Images can't hold HDR vertex colours (Color32 clamps to 1) — only their material
    // _Color can carry the HDR boost.  TMP gets ×4 vertex AND ×4 material → effective ×16,
    // so Images need UITextBrightnessBoost² to match.
    private const float UIImageBrightnessBoost = UITextBrightnessBoost * UITextBrightnessBoost;

    // Per-frame state
    private long  _displayTime;
    private bool  _frameOpen;
    private bool  _stereoReady;   // true once swapchains created and rig built
    private int   _frameCount;
    private int   _locateErrors;
    private int   _waitFrameCount; // empty frames submitted while waiting for SYNCHRONIZED

    private OpenXRManager.EyePose _leftEye, _rightEye;
    private bool _posesValid;

    // ── UI canvas tracking ────────────────────────────────────────────────────
    // Maps Canvas instanceID → Canvas for every screen-space canvas we've converted
    // to WorldSpace.  Each canvas is placed in front of the head ONCE on first appearance
    // and then stays at that world position so the player can look around it freely.
    private readonly Dictionary<int, Canvas> _managedCanvases = new();
    private int _canvasTick; // counts Update() calls; resets at UICanvasScanRate

    // Tracks which canvas IDs have already been placed in world space.
    // Once in this set the canvas transform is never moved again by us.
    private readonly HashSet<int> _positionedCanvases = new();

    // Canvas instance IDs that belong to VRMod itself (settings panel, cursor, etc.).
    // Every mutation pass (RescanCanvasAlpha, etc.) must skip canvases in this set —
    // we own those materials and manage them directly.
    private readonly HashSet<int> _ownedCanvasIds = new();

    // Fullscreen "fade overlay" Graphics that should stay at alpha=0 during VR gameplay.
    // The game uses these for fade-to-black transitions; they end up opaque and bury buttons.
    // We force their vertex alpha to 0 every frame so the menu remains readable.
    // Key = Graphic instanceID.
    private readonly Dictionary<int, Graphic> _managedFades = new();

    // ── VR settings panel (Phase 0 test canvas) ──────────────────────────────
    private GameObject?   _settingsPanelGO;   // root GO of VRSettingsPanelInternal

    // ── Controller / cursor dot ──────────────────────────────────────────────
    private GameObject?   _rightControllerGO;
    private GameObject?   _leftControllerGO;

    // ── Snap turn ─────────────────────────────────────────────────────────────
    private const float SnapTurnAngle    = 30f;   // degrees per snap
    private const float SnapTurnDeadZone = 0.6f;  // stick threshold to trigger
    private const float SnapTurnRearm    = 0.3f;  // stick must drop below this to re-arm
    private const float SnapTurnCooldown = 0.25f; // seconds between snaps
    private float _snapCooldown;
    private bool  _snapArmed = true;

    // ── Movement discovery + locomotion ──────────────────────────────────────
    private bool      _movementDiscoveryDone;
    private Rigidbody? _playerRb;               // FPSController rigidbody, cached at discovery
    private const float MoveDeadZone = 0.15f;
    private const float MoveSpeed    = 4.0f;   // m/s at full deflection
    // Cursor: ScreenSpaceOverlay canvas "VRCursorCanvasInternal" created at rig-build time.
    // ScanAndConvertCanvases converts it to WorldSpace via the normal pipeline — giving it proper
    // HDRP registration and the ZTest Always material patch from RescanCanvasAlpha.
    // It is NOT in _ownedCanvasIds so all mutation passes (including the ZTest patch) run on it.
    // PositionCanvases repositions it every frame (never added to _positionedCanvases).
    // The dot moves via anchoredPosition (2D) inside this fixed-distance canvas.
    private Canvas?        _cursorCanvas;         // VRCursorCanvasInternal once scan converts it
    private RectTransform? _cursorRect;           // the dot's RectTransform inside _cursorCanvas
    private Vector2        _cursorCanvasHalfSize; // half-size in canvas pixels; cached lazily
    private float          _cursorAimDepth = UIDistance - 0.01f; // head-fwd depth of cursor canvas; tracks nearest aimed-at canvas
    private Canvas?        _menuCanvasRef;       // MenuCanvas — hidden while VR settings panel is open
    private bool           _menuCanvasHidden;    // tracks last hide state to avoid per-frame toggles
    private int            _menuSettingsBtnId;   // instanceID of the patched Settings button in MenuCanvas
    private bool           _cursorVisible = false; // tracks SetActive state to avoid per-frame IL2CPP calls
    private bool        _prevTrigger;
    private int         _poseFrameCount;
    private bool        _poseEverValid;

    // Maps (origMaterialInstanceID << 5 | tier<<1 | isBackground) → patched clone.
    // Lower 5 bits: tier (0-9, 4 bits) + isBackground (1 bit).
    // isBackground selects between semi-transparent (UIBackgroundAlpha) and boosted (UIColorBoost) clones.
    // Static so the table persists across scene loads and we never create duplicates.
    private static readonly Dictionary<long, Material> s_uiZTestMats = new();
    // Tracks Graphic instance IDs whose vertex alpha (g.color.a) has been forced to 1.
    // Only set once per graphic to avoid per-frame canvas rebuilds.
    private static readonly HashSet<int> s_vertexAlphaFixed = new();
    // Instance IDs of every material clone WE created.  Used by RescanCanvasAlpha to
    // detect already-patched elements without reading shader properties (which is
    // unreliable for non-UI/Default shaders like Mobile/Particles/Additive).
    private static readonly HashSet<int> s_patchedMaterialIds = new();
    // Tracks canvas instance IDs whose QueueMap has already been logged, so rescans
    // don't spam the log every 30 frames.
    private readonly HashSet<int> _queueMapLogged = new();
    // Version stamp — bump to invalidate s_uiZTestMats cache and force material rebuild.
    private const int UIMaterialVersion = 3;
    private static readonly HashSet<int> s_shaderSwappedMats = new();
    private static readonly HashSet<IntPtr> s_textBoostedGraphics  = new();
    private static readonly HashSet<IntPtr> s_imageBoostedGraphics = new();
    // Tracks material instance IDs that have been brightness-boosted for Images.
    // Multiple Image components share the same ZTest-clone material — without this
    // guard each graphic would re-boost the shared material, compounding ×4 per
    // Image and producing extreme / broken colours.
    private static readonly HashSet<int> s_imageBoostedMats = new();
    private static readonly HashSet<int> s_menuMaskRelaxedCanvases = new();
    private static readonly HashSet<int> s_menuMaskabilityRelaxed = new();
    private static readonly Dictionary<int, Material> s_menuTmpReadableMats = new();
    private static readonly Dictionary<int, TextMeshProUGUI> s_menuTextFallbacks = new();
    // ── Awake ─────────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Stop the background frame thread; we take over the frame loop from here.
        OpenXRManager.StopFrameThread();
        Log.LogInfo("[VRCamera] Awake — polling for SYNCHRONIZED state before swapchain setup.");
    }

    // ── Start ─────────────────────────────────────────────────────────────────

    private void Start()
    {
        // No-op: setup deferred to Update() until session reaches SYNCHRONIZED.
    }

    // ── Update: event polling / empty frames / lazy setup / stereo loop ──────

    private void Update()
    {
        // Always drain the event queue so VDXR can advance the session state machine.
        OpenXRManager.PollEventsPublic();

        // Scan for new screen-space canvases and convert them to WorldSpace.
        // Runs unconditionally (before and after stereo is ready) so the main menu
        // and loading screens are picked up even while the headset session is starting.
        if (++_canvasTick >= UICanvasScanRate)
        {
            _canvasTick = 0;
            try { ScanAndConvertCanvases(); }
            catch (Exception ex) { Log.LogWarning($"[VRCamera] ScanAndConvertCanvases outer: {ex.GetType().Name}: {ex.Message}"); }
        }

        // F8: re-centre all canvases in front of the current head pose.
        // Useful after loading or if menus appear at the wrong height/direction.
        if (Input.GetKeyDown(KeyCode.F8))
        {
            _positionedCanvases.Clear();
            Log.LogInfo("[VRCamera] Recenter: canvases will be re-placed on next LateUpdate.");
        }

        // F10: toggle the VR settings panel.
        if (Input.GetKeyDown(KeyCode.F10))
            VRSettingsPanel.Toggle();

        // End key: diagnostic dump — logs active text graphics on all managed canvases.
        // Shows vertex colour, _FaceColor (TMP), mat.color and local-Z so we can diagnose
        // invisible text (colour too dark at EV=0, wrong alpha, z-fighting, etc.).
        if (Input.GetKeyDown(KeyCode.End))
        {
            Log.LogInfo("[VRCamera] === TEXT DIAGNOSTIC DUMP ===");
            foreach (var kvp in _managedCanvases)
            {
                var cv = kvp.Value;
                if (cv == null) continue;
                var graphics = cv.GetComponentsInChildren<Graphic>(true);
                int shown = 0;
                foreach (var g in graphics)
                {
                    if (g == null || !g.gameObject.activeInHierarchy) continue;
                    if (!IsTextGraphic(g)) continue;
                    // g.material returns the Graphic property — for TMP this is our VRPatch mat (irrelevant).
                    // cr.GetMaterial(0) returns what CanvasRenderer actually renders with — TMP's font mat.
                    Material crMat = null;
                    try { crMat = g.canvasRenderer.GetMaterial(0); } catch { }
                    Color vc   = g.color;  // Graphic.m_Color
                    Color cmc  = crMat != null ? crMat.color : Color.clear;
                    float fa   = -1f;
                    try { if (crMat != null && crMat.HasProperty("_FaceColor")) fa = crMat.GetColor("_FaceColor").a; } catch { }
                    float lz   = g.rectTransform?.localPosition.z ?? 0f;
                    float cra  = -1f;
                    try { cra = g.canvasRenderer.GetAlpha(); } catch { }
                    string crClip = "n/a";
                    try { crClip = g.canvasRenderer.hasRectClipping ? "on" : "off"; } catch { }
                    string crCull = "n/a";
                    try { crCull = g.canvasRenderer.cull ? "on" : "off"; } catch { }
                    float groupAlpha = 1f;
                    try
                    {
                        var groups = g.GetComponentsInParent<CanvasGroup>(true);
                        foreach (var group in groups)
                        {
                            if (group == null) continue;
                            groupAlpha *= group.alpha;
                        }
                    }
                    catch { }
                    string crMn = crMat?.name ?? "null";
                    string crSh = crMat?.shader?.name ?? "null";
                    int crId = crMat != null ? crMat.GetInstanceID() : 0;
                    string crFace = "n/a";
                    string crOutline = "n/a";
                    string crOutlineWidth = "n/a";
                    try
                    {
                        if (crMat != null && crMat.HasProperty("_FaceColor"))
                        {
                            var c = crMat.GetColor("_FaceColor");
                            crFace = $"({c.r:F2},{c.g:F2},{c.b:F2},a={c.a:F2})";
                        }
                    }
                    catch { }
                    try
                    {
                        if (crMat != null && crMat.HasProperty("_OutlineColor"))
                        {
                            var c = crMat.GetColor("_OutlineColor");
                            crOutline = $"({c.r:F2},{c.g:F2},{c.b:F2},a={c.a:F2})";
                        }
                    }
                    catch { }
                    try
                    {
                        if (crMat != null && crMat.HasProperty("_OutlineWidth"))
                            crOutlineWidth = crMat.GetFloat("_OutlineWidth").ToString("F3");
                    }
                    catch { }
                    // TMP-specific diagnostics
                    string tmpCast = "noTMP";
                    string tmpCol  = "n/a";
                    string tmpFace = "n/a";
                    string tmpFM = "n/a";
                    string tmpFSM = "n/a";
                    string tmpMatMatch = "n/a";
                    try
                    {
                        var tmp = g.TryCast<TMP_Text>();
                        if (tmp != null)
                        {
                            tmpCast = "OK";
                            var tc = tmp.color;
                            tmpCol = $"({tc.r:F2},{tc.g:F2},{tc.b:F2},a={tc.a:F2})";
                            try
                            {
                                var c = tmp.faceColor;
                                tmpFace = $"({c.r / 255f:F2},{c.g / 255f:F2},{c.b / 255f:F2},a={c.a / 255f:F2})";
                            }
                            catch { }
                            try
                            {
                                var fm = tmp.fontMaterial;
                                tmpFM = fm != null ? $"{fm.name}#{fm.GetInstanceID()}" : "null";
                                if (crMat != null && fm != null)
                                    tmpMatMatch = crMat.GetInstanceID() == fm.GetInstanceID() ? "fontMat" : "other";
                            }
                            catch { }
                            try
                            {
                                var fsm = tmp.fontSharedMaterial;
                                tmpFSM = fsm != null ? $"{fsm.name}#{fsm.GetInstanceID()}" : "null";
                                if (tmpMatMatch == "n/a" && crMat != null && fsm != null)
                                    tmpMatMatch = crMat.GetInstanceID() == fsm.GetInstanceID() ? "fontShared" : "other";
                            }
                            catch { }
                        }
                        else tmpCast = "NULL";
                    }
                    catch (Exception ex) { tmpCast = $"ERR:{ex.GetType().Name}"; }
                    bool patched = s_patchedGraphicPtrs.Contains(g.Pointer);
                    bool matSwapped = crMat != null && s_shaderSwappedMats.Contains(crMat.GetInstanceID());
                    // _TextureSampleAdd and font texture format — key for Alpha8 white-text fix
                    Vector4 tsa = Vector4.zero;
                    try { if (crMat != null && crMat.HasProperty("_TextureSampleAdd")) tsa = crMat.GetVector("_TextureSampleAdd"); } catch { }
                    string texFmt = "n/a";
                    try
                    {
                        var tmp2 = g.TryCast<TMP_Text>();
                        if (tmp2?.font?.atlasTexture != null)
                            texFmt = tmp2.font.atlasTexture.format.ToString();
                    }
                    catch { }
                    string snippet = GetVisibleTextSnippet(g);
                    bool watchTransform =
                        string.Equals(snippet, "Back", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(snippet, "Resolution", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(snippet, "Display Mode", StringComparison.OrdinalIgnoreCase);
                    string trSummary = watchTransform ? GetTransformSummary(g) : "";
                    string trParents = watchTransform ? GetParentChain(g) : "";
                    Log.LogInfo($"[VRCamera] TXT '{cv.gameObject.name}'/{g.gameObject.name} " +
                                $"text=\"{snippet}\" " +
                                $"g.col=({vc.r:F2},{vc.g:F2},{vc.b:F2},a={vc.a:F2}) " +
                                $"tmp.col={tmpCol} tmp.face={tmpFace} tmpCast={tmpCast} " +
                                $"tmp.fm={tmpFM} tmp.fsm={tmpFSM} tmpMatch={tmpMatMatch} " +
                                $"crA={cra:F2} grpA={groupAlpha:F2} crClip={crClip} crCull={crCull} lz={lz:F3} crMat={crMn}#{crId} crShader={crSh} " +
                                $"crFace={crFace} crOutline={crOutline} crOW={crOutlineWidth} " +
                                $"TSA=({tsa.x:F1},{tsa.y:F1},{tsa.z:F1},{tsa.w:F1}) texFmt={texFmt} " +
                                $"patched={patched} matSwapped={matSwapped}" +
                                (watchTransform ? $" tr={trSummary} chain={trParents}" : ""));
                    if (++shown >= 60) { Log.LogInfo($"[VRCamera] ... (truncated, >{shown} text elements active)"); break; }
                }
                // Also dump CanvasGroups so we can spot alpha fade animations.
                try
                {
                    var cgs = cv.GetComponentsInChildren<CanvasGroup>(true);
                    foreach (var cg in cgs)
                    {
                        if (cg == null) continue;
                        Log.LogInfo($"[VRCamera] CG '{cv.gameObject.name}'/{cg.gameObject.name} alpha={cg.alpha:F2} interactable={cg.interactable} blocksRaycasts={cg.blocksRaycasts}");
                    }
                }
                catch { }
                // Also dump first 10 active non-text Images so we can see if they're occluding text.
                int imgShown = 0;
                foreach (var g in graphics)
                {
                    if (g == null || !g.gameObject.activeInHierarchy) continue;
                    if (IsTextGraphic(g)) continue;
                    string nm2 = g.gameObject.name;
                    bool isBg2 = nm2.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
                    Material m2 = null;
                    try { m2 = g.material; } catch { }
                    Color vc2  = g.color;
                    float cra2 = -1f;
                    try { cra2 = g.canvasRenderer.GetAlpha(); } catch { }
                    float lz2  = g.rectTransform?.localPosition.z ?? 0f;
                    Log.LogInfo($"[VRCamera] IMG '{cv.gameObject.name}'/{nm2} isBg={isBg2} " +
                                $"vc=({vc2.r:F2},{vc2.g:F2},{vc2.b:F2},a={vc2.a:F2}) " +
                                $"crA={cra2:F2} lz={lz2:F3} mat={m2?.name ?? "null"}");
                    if (++imgShown >= 10) { Log.LogInfo($"[VRCamera] ... IMG truncated"); break; }
                }
                Log.LogInfo($"[VRCamera] Canvas '{cv.gameObject.name}': {shown} active text, {imgShown}+ images shown");
            }
            Log.LogInfo("[VRCamera] === END TEXT DUMP ===");
        }

        if (!_stereoReady)
        {
            // Waiting for session to reach SYNCHRONIZED (state ≥ 3).
            // Submit empty frames so VDXR / Virtual Desktop see activity and advance the state.
            _waitFrameCount++;

            long t = OpenXRManager.FrameWaitPublic(out int wrc);
            if (wrc >= 0)
            {
                OpenXRManager.FrameBeginPublic();
                OpenXRManager.FrameEndEmpty(t > 0 ? t : 1);
            }
            else if ((_waitFrameCount % 60) == 1)
            {
                Log.LogWarning($"[VRCamera] xrWaitFrame rc={wrc} (empty frame {_waitFrameCount})");
            }

            bool stateOk  = OpenXRManager.HighestSessionState >= 3;
            bool timedOut = _waitFrameCount >= 300; // ~5 s at 60 fps

            if (!stateOk && !timedOut) return;

            if (timedOut && !stateOk)
                Log.LogWarning($"[VRCamera] Setup timeout (highest state={OpenXRManager.HighestSessionState}) — attempting SetupStereo anyway.");
            else
                Log.LogInfo($"[VRCamera] Session state {OpenXRManager.HighestSessionState} ≥ 3 — creating swapchains.");

            if (!OpenXRManager.SetupStereo())
            {
                Log.LogError("[VRCamera] SetupStereo failed — disabling.");
                enabled = false;
                return;
            }
            Log.LogInfo($"[VRCamera] Swapchains: {OpenXRManager.SwapchainWidth}x{OpenXRManager.SwapchainHeight} " +
                        $"L={OpenXRManager.LeftSwapchainImages.Length} R={OpenXRManager.RightSwapchainImages.Length} images");
            BuildCameraRig();
            _stereoReady = true;
            Log.LogInfo("[VRCamera] Active.");
            return;
        }

        // ── Normal stereo frame loop ───────────────────────────────────────────

        // Follow the game character's camera position each frame.
        // Retry finding a game camera every 60 frames in case it wasn't available at rig build time.
        if (_gameCam == null && (_frameCount % 60) == 0) TryFindGameCamera();
        if (_gameCam != null) transform.position = _gameCam.position;

        _displayTime = OpenXRManager.FrameWaitPublic(out int waitRc);
        if (waitRc < 0)
        {
            if (_frameCount < 5 || (_frameCount % 300) == 0)
                Log.LogWarning($"[VRCamera] xrWaitFrame rc={waitRc}");
            _frameOpen  = false;
            _posesValid = false;
            return;
        }
        if (_displayTime == 0) _displayTime = 1;

        int beginRc = OpenXRManager.FrameBeginPublic();
        if (beginRc != 0 && (_frameCount < 5 || (_frameCount % 300) == 0))
            Log.LogWarning($"[VRCamera] xrBeginFrame rc={beginRc}");
        _frameOpen = true;

        if (OpenXRManager.LocateViews(_displayTime, out _leftEye, out _rightEye))
        {
            _locateErrors = 0;
            _posesValid   = true;
            ApplyCameraPose(_leftCam.transform,  _leftEye);
            ApplyCameraPose(_rightCam.transform, _rightEye);
            SetProjection(_leftCam,  _leftEye);
            SetProjection(_rightCam, _rightEye);
        }
        else
        {
            _posesValid = false;
            _locateErrors++;
            if (_locateErrors <= 5 || (_locateErrors % 300) == 0)
                Log.LogWarning($"[VRCamera] xrLocateViews failed #{_locateErrors}");
        }

        // Controller input — sync actions and update laser pointer each stereo frame
        if (OpenXRManager.ActionSetsReady)
        {
            OpenXRManager.SyncActions();
            UpdateControllerPose(_displayTime);
            UpdateSnapTurn();
            UpdateLocomotion();
        }

        // One-shot movement system discovery (runs once after stereo is ready and game cam found)
        if (!_movementDiscoveryDone && _gameCam != null)
            DiscoverMovementSystem();
    }

    private void BuildCameraRig()
    {
        // Clear cached UI material clones so they're rebuilt with current boost settings.
        s_uiZTestMats.Clear();
        s_vertexAlphaFixed.Clear();
        s_patchedMaterialIds.Clear();
        s_patchedGraphicPtrs.Clear();
        s_patchedMats.Clear();

        int w = OpenXRManager.SwapchainWidth;
        int h = OpenXRManager.SwapchainHeight;

        var offsetGO = new GameObject("CameraOffset");
        offsetGO.transform.SetParent(transform, false);
        _cameraOffset = offsetGO.transform;

        // ── Scene cameras (render everything EXCEPT the UI layer) ────────────────
        // Render the scene and world-space UI together through the same eye cameras.
        var leftGO = new GameObject("LeftEye");
        leftGO.transform.SetParent(_cameraOffset, false);
        _leftCam = leftGO.AddComponent<Camera>();
        _leftRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Left" };
        _leftRT.Create();
        SetupEyeCam(_leftCam, _leftRT);
        _leftCam.cullingMask = ~0;

        var rightGO = new GameObject("RightEye");
        rightGO.transform.SetParent(_cameraOffset, false);
        _rightCam = rightGO.AddComponent<Camera>();
        _rightRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Right" };
        _rightRT.Create();
        SetupEyeCam(_rightCam, _rightRT);
        _rightCam.cullingMask = ~0;

        // Try to find and disable the game camera now. If it's not available yet
        // (e.g. main menu hasn't spawned one), TryFindGameCamera() will keep retrying in Update().
        TryFindGameCamera();

        var ctrlGO = new GameObject("RightController");
        ctrlGO.layer = UILayer;
        ctrlGO.transform.SetParent(_cameraOffset, false);
        _rightControllerGO = ctrlGO;

        var leftCtrlGO = new GameObject("LeftController");
        leftCtrlGO.layer = UILayer;
        leftCtrlGO.transform.SetParent(_cameraOffset, false);
        _leftControllerGO = leftCtrlGO;

        // Cursor dot: ScreenSpaceOverlay canvas created here, converted to WorldSpace by
        // ScanAndConvertCanvases — same pipeline as all game canvases, giving HDRP registration.
        // NOT in _ownedCanvasIds so RescanCanvasAlpha applies the ZTest Always material patch.
        // PositionCanvases keeps it at UIDistance in front of the head every frame.
        // The dot moves via anchoredPosition inside the canvas (2D projection).
        try
        {
            var ccGO = new GameObject("VRCursorCanvasInternal");
            ccGO.layer = UILayer;
            UnityEngine.Object.DontDestroyOnLoad(ccGO); // survive loading→menu scene transition
            var cc = ccGO.AddComponent<Canvas>();
            cc.renderMode   = RenderMode.ScreenSpaceOverlay;
            cc.sortingOrder = 100; // above all game canvases
            _cursorCanvas = cc; // cache immediately — don't rely on name-lookup in PositionCanvases

            var dotGO = new GameObject("Dot");
            dotGO.layer = UILayer;
            // Set parent BEFORE adding Image so RectTransform is created in the right hierarchy.
            dotGO.transform.SetParent(ccGO.transform, false);
            // AddComponent<Image> creates a RectTransform internally; get it via GetComponent.
            // Do NOT call AddComponent<RectTransform>() on a fresh GO — it may return null in
            // IL2CPP because the GO already has a plain Transform component.
            var img = dotGO.AddComponent<Image>();
            img.raycastTarget = false;
            _cursorRect = dotGO.GetComponent<RectTransform>();
            if (_cursorRect != null)
            {
                _cursorRect.sizeDelta        = new Vector2(20f, 20f);
                _cursorRect.anchorMin        = new Vector2(0.5f, 0.5f);
                _cursorRect.anchorMax        = new Vector2(0.5f, 0.5f);
                _cursorRect.pivot            = new Vector2(0.5f, 0.5f);
                _cursorRect.anchoredPosition = Vector2.zero;
            }

            dotGO.SetActive(false); // hidden until controller pose is valid
            Log.LogInfo($"[VRCamera] VRCursorCanvasInternal created — cursorRect={_cursorRect != null}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] Cursor canvas creation failed: {ex.Message}");
        }

        // ── Phase 1: VR Settings Panel ───────────────────────────────────────────
        try
        {
            _settingsPanelGO = VRSettingsPanel.Init(
                id => _positionedCanvases.Remove(id));
            Log.LogInfo("[VRCamera] VRSettingsPanel.Init complete.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] VRSettingsPanel.Init failed: {ex.Message}");
        }

        Log.LogInfo($"[VRCamera] Rig built: {w}x{h} ARGB32");
    }

    // Search all active cameras for one that is not one of ours.
    // Disables it so it does not render independently, and tracks its transform.
    private void TryFindGameCamera()
    {
        foreach (var cam in Camera.allCameras)
        {
            if (cam == _leftCam || cam == _rightCam) continue;
            if (!cam.gameObject.activeInHierarchy) continue;
            _gameCam = cam.transform;
            cam.enabled = false;
            Log.LogInfo($"[VRCamera] Found game camera: '{cam.gameObject.name}' pos={cam.transform.position}");
            transform.position = _gameCam.position;
            return;
        }
    }

    // Expensive HDRP passes to disable on VR eye cameras.
    private static readonly FrameSettingsField[] s_VrDisabledFields =
    {
        FrameSettingsField.SSAO,
        FrameSettingsField.SSR,
        FrameSettingsField.Volumetrics,
        FrameSettingsField.MotionVectors,
        FrameSettingsField.MotionBlur,
        FrameSettingsField.DepthOfField,
        FrameSettingsField.ChromaticAberration,
        FrameSettingsField.ContactShadows,
        FrameSettingsField.Tonemapping,
    };

    private void SetupEyeCam(Camera cam, RenderTexture rt)
    {
        cam.targetTexture = rt;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 1000f;
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.enabled = false;

        try
        {
            var hd = cam.gameObject.GetComponent<HDAdditionalCameraData>()
                  ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();

            hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
            hd.dithering = false;
            hd.hasPersistentHistory = false;

            var om = hd.renderingPathCustomFrameSettingsOverrideMask;
            foreach (var f in s_VrDisabledFields)
                om.mask[(uint)(int)f] = true;
            hd.renderingPathCustomFrameSettingsOverrideMask = om;

            bool maskOk = hd.renderingPathCustomFrameSettingsOverrideMask
                            .mask[(uint)(int)FrameSettingsField.SSAO];

            if (maskOk)
            {
                var fs = hd.renderingPathCustomFrameSettings;
                foreach (var f in s_VrDisabledFields)
                    fs.SetEnabled(f, false);
                hd.renderingPathCustomFrameSettings = fs;
                hd.customRenderingSettings = true;
                Log.LogInfo($"[VRCamera] HDRP: AA=None, customFS=true, {s_VrDisabledFields.Length} passes off on {cam.gameObject.name}");

                try
                {
                    var fsRb = hd.renderingPathCustomFrameSettings;
                    bool tmapOff = !fsRb.IsEnabled(FrameSettingsField.Tonemapping);
                    Log.LogInfo($"[VRCamera] HDRP FS readback on {cam.gameObject.name}: TonemapOff={tmapOff}");
                }
                catch (Exception rbEx)
                {
                    Log.LogWarning($"[VRCamera] HDRP FS readback failed: {rbEx.Message}");
                }
            }
            else
            {
                Log.LogWarning($"[VRCamera] HDRP: override mask did not persist (IL2CPP struct copy) - skipping customFS on {cam.gameObject.name}");
                Log.LogInfo($"[VRCamera] HDRP: AA=None, no history on {cam.gameObject.name}");
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] HDAdditionalCameraData setup failed: {ex.Message}");
        }
    }

    private void LateUpdate()
    {
        if (!_stereoReady || !_frameOpen) return;
        _frameOpen = false;

        PositionCanvases();

        try
        {
            if (!_posesValid)
            {
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            // Skip full renders when the game camera is absent (scene transition / world
            // generation).  The GPU is saturated by city-gen work; Camera.Render() on top
            // of that causes DXGI_ERROR_DEVICE_REMOVED.  ATW holds the last valid frame in
            // the headset so the transition is invisible to the user.
            if (_gameCam == null)
            {
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            if ((_frameCount % RenderEveryNFrames) == 0)
            {
                Canvas.ForceUpdateCanvases();

                GL.invertCulling = true;
                _leftCam.Render();
                _rightCam.Render();
                GL.invertCulling = false;
            }

            bool leftOk = CopyEye(true, out uint leftIdx);
            bool rightOk = CopyEye(false, out uint rightIdx);

            if (!leftOk || !rightOk)
            {
                Log.LogWarning("[VRCamera] Swapchain copy failed - empty frame.");
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            OpenXRManager.FrameEndStereo(_displayTime, _leftEye, _rightEye, leftIdx, rightIdx);

            _frameCount++;
            if (_frameCount <= 10)
                Log.LogInfo($"[VRCamera] Stereo frame #{_frameCount}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] LateUpdate submit: {ex.Message}");
            try { OpenXRManager.FrameEndEmpty(_displayTime); } catch { }
        }
    }

    private bool CopyEye(bool left, out uint imageIndex)
    {
        imageIndex = 0;
        string eye = left ? "L" : "R";
        ulong sc = left ? OpenXRManager.LeftSwapchain : OpenXRManager.RightSwapchain;
        IntPtr[] images = left ? OpenXRManager.LeftSwapchainImages : OpenXRManager.RightSwapchainImages;
        RenderTexture rt = left ? _leftRT : _rightRT;

        if (sc == 0 || images == null || images.Length == 0 || rt == null)
        {
            Log.LogWarning($"[VRCamera] {eye} copy skipped - swapchain not ready");
            return false;
        }

        if (!OpenXRManager.AcquireSwapchainImage(sc, out imageIndex))
        {
            Log.LogWarning($"[VRCamera] {eye} AcquireSwapchainImage failed");
            return false;
        }

        if (!OpenXRManager.WaitSwapchainImage(sc))
        {
            Log.LogWarning($"[VRCamera] {eye} WaitSwapchainImage failed");
            OpenXRManager.ReleaseSwapchainImage(sc);
            return false;
        }

        if (imageIndex < (uint)images.Length)
        {
            IntPtr src = rt.GetNativeTexturePtr();
            IntPtr dst = images[imageIndex];
            if (_frameCount <= 3)
                Log.LogInfo($"[VRCamera] {eye} copy: src=0x{src:X} dst=0x{dst:X} rtCreated={rt.IsCreated()}");
            if (src != IntPtr.Zero && dst != IntPtr.Zero)
                OpenXRManager.D3D11CopyTexture(src, dst);
        }

        OpenXRManager.ReleaseSwapchainImage(sc);
        return true;
    }

    /// <summary>
    /// Finds all root Screen Space canvases that have not been converted yet and
    /// changes them to WorldSpace so the eye cameras can see them.
    /// Called every UICanvasScanRate frames (pre- and post-stereo).
    /// </summary>
    private void ScanAndConvertCanvases()
    {
        var dead = new List<int>();
        foreach (var kvp in _managedCanvases)
            if (kvp.Value == null) dead.Add(kvp.Key);
        foreach (var k in dead) { _managedCanvases.Remove(k); _positionedCanvases.Remove(k); }

        Canvas[] all;
        try
        {
            all = Resources.FindObjectsOfTypeAll<Canvas>();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] Canvas scan threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (_frameCount <= 60 || (_frameCount % 300) == 0)
            Log.LogInfo($"[VRCamera] Canvas scan: found {all.Length} canvas(es), managed={_managedCanvases.Count}");

        foreach (var canvas in all)
        {
            if (canvas == null) continue;
            if (!canvas.isRootCanvas) continue;
            if (canvas.renderMode == RenderMode.WorldSpace) continue;
            int id = canvas.GetInstanceID();
            if (_managedCanvases.ContainsKey(id)) continue;

            // Skip high-churn transient canvases that get constantly created/destroyed
            // by the city generator (MapDuctComponent, MapButtonComponent, etc.).
            // These would spam the log and waste material patch slots.
            string cname = canvas.gameObject.name ?? "";
            if (cname.IndexOf("MapDuct", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("MapButton", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("Loading Icon", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            ConvertCanvasToWorldSpace(canvas);
            _managedCanvases[id] = canvas;

            // Redirect the game's "Settings" button to open our VR Settings panel instead.
            // Also cache a reference so PositionCanvases can hide the menu while VR panel is open.
            if (cname == "MenuCanvas")
            {
                _menuCanvasRef = canvas;
                PatchMenuSettingsButton(canvas);
            }
        }

        foreach (var kvp in _managedCanvases)
        {
            if (kvp.Value != null) RescanCanvasAlpha(kvp.Value);
        }

        var rootList = new List<Canvas>(_managedCanvases.Values);
        foreach (var root in rootList)
        {
            if (root == null) continue;
            Canvas[] nested;
            try { nested = root.GetComponentsInChildren<Canvas>(true); }
            catch { continue; }
            foreach (var nc in nested)
            {
                if (nc == null) continue;
                int nid = nc.GetInstanceID();
                if (nid == root.GetInstanceID()) continue;
                if (_managedCanvases.ContainsKey(nid)) continue;

                int patched = ForceUIZTestAlways(nc, logQueueMap: true);
                _managedCanvases[nid] = nc;
                Log.LogInfo($"[VRCamera] NestedCanvas '{nc.gameObject.name}' in '{root.gameObject.name}' patched={patched}");
            }
        }

        if (_leftCam != null)
        {
            foreach (var c in _managedCanvases.Values)
                if (c != null && c.worldCamera == null) c.worldCamera = _leftCam;
        }
    }
    // Native IL2CPP object pointers of Graphics we have already patched.
    private static readonly HashSet<IntPtr> s_patchedGraphicPtrs = new();
    private static readonly Dictionary<IntPtr, Material> s_patchedMats = new();

    private static int ComputeDepth(Transform t, Transform root)
    {
        int depth = 0;
        while (t != null && t != root && depth < 9)
        {
            depth++;
            t = t.parent;
        }
        return depth;
    }

    private static bool IsTextGraphic(Graphic g)
    {
        try
        {
            if (g.TryCast<UnityEngine.UI.Text>() != null) return true;
            string tn = g.GetIl2CppType()?.Name ?? "";
            return tn.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0
                || tn == "TMP_Text" || tn == "TMP_SubMeshUI";
        }
        catch { return false; }
    }

    private static Color BoostRgb(Color c, float boost)
        => new(c.r * boost, c.g * boost, c.b * boost, Mathf.Max(c.a, 1f));

    private void ApplyReadableTextBoost(Graphic g)
    {
        if (g == null || !IsTextGraphic(g)) return;
        if (!s_textBoostedGraphics.Add(g.Pointer)) return;

        try
        {
            var tmp = g.TryCast<TMP_Text>();
            if (tmp != null)
                tmp.color = BoostRgb(tmp.color, UITextBrightnessBoost);
            else
                g.color = BoostRgb(g.color, UITextBrightnessBoost);
        }
        catch { }

        try
        {
            var mat = g.material;
            if (mat != null)
            {
                if (mat.HasProperty("_Color"))
                    mat.color = BoostRgb(mat.color, UITextBrightnessBoost);
                if (mat.HasProperty("_FaceColor"))
                    mat.SetColor("_FaceColor", BoostRgb(mat.GetColor("_FaceColor"), UITextBrightnessBoost));
            }
        }
        catch { }

        try
        {
            var crMat = g.canvasRenderer.GetMaterial(0);
            if (crMat != null)
            {
                if (crMat.HasProperty("_Color"))
                    crMat.color = BoostRgb(crMat.color, UITextBrightnessBoost);
                if (crMat.HasProperty("_FaceColor"))
                    crMat.SetColor("_FaceColor", BoostRgb(crMat.GetColor("_FaceColor"), UITextBrightnessBoost));
            }
        }
        catch { }
    }

    // Boosts the material _Color of non-text, non-background Image graphics by
    // UITextBrightnessBoost so they survive HDRP auto-exposure.
    // Unity UI vertex colours (image.color) are Color32 and clamp to [0,1] before
    // reaching the GPU — only the material's _Color float4 property can hold HDR values.
    // The vertex colour (set by RefreshColors) is then multiplied against this HDR
    // material colour to produce the final on-screen HDR tint.
    private void ApplyReadableImageBoost(Graphic g)
    {
        if (g == null || IsTextGraphic(g)) return;
        if (!s_imageBoostedGraphics.Add(g.Pointer)) return; // one-shot per graphic

        try
        {
            var mat = g.material;
            // Gate on material instance ID — multiple Graphics share the same ZTest-clone
            // material, so without this guard each one would compound the ×4 boost
            // (4^N after N images → broken/black screen).
            if (mat != null && mat.HasProperty("_Color") && s_imageBoostedMats.Add(mat.GetInstanceID()))
            {
                var c = mat.color;
                // Boost RGB only — preserve original alpha so transparent overlays
                // (vignettes, glow, decorative borders) stay transparent.
                mat.color = new Color(c.r * UIImageBrightnessBoost,
                                      c.g * UIImageBrightnessBoost,
                                      c.b * UIImageBrightnessBoost,
                                      c.a);
            }
        }
        catch { }

        try
        {
            var crMat = g.canvasRenderer.GetMaterial(0);
            if (crMat != null && crMat.HasProperty("_Color") && s_imageBoostedMats.Add(crMat.GetInstanceID()))
            {
                var c = crMat.color;
                crMat.color = new Color(c.r * UIImageBrightnessBoost,
                                        c.g * UIImageBrightnessBoost,
                                        c.b * UIImageBrightnessBoost,
                                        c.a);
            }
        }
        catch { }
    }

    private static bool ShouldRelaxMenuClipping(Canvas canvas)
    {
        if (canvas == null) return false;
        string name = canvas.gameObject.name;
        return string.Equals(name, "MenuCanvas", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetVisibleTextSnippet(Graphic g)
    {
        try
        {
            var tmp = g.TryCast<TMP_Text>();
            if (tmp != null)
            {
                string text = tmp.text ?? "";
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 48) text = text[..48];
                return text;
            }
        }
        catch { }

        try
        {
            var uguiText = g.TryCast<UnityEngine.UI.Text>();
            if (uguiText != null)
            {
                string text = uguiText.text ?? "";
                text = text.Replace("\r", " ").Replace("\n", " ").Trim();
                if (text.Length > 48) text = text[..48];
                return text;
            }
        }
        catch { }

        return "";
    }

    private static bool IsLikelyNavButtonText(Graphic g)
    {
        if (g == null) return false;

        string snippet = GetVisibleTextSnippet(g);
        if (string.Equals(snippet, "Back", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(snippet, "Continue", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(snippet, "Settings", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(snippet, "Options", StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            for (Transform t = g.transform; t != null; t = t.parent)
            {
                string n = t.gameObject.name ?? "";
                if (n.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (n.IndexOf("back", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (n.IndexOf("tab", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
        }
        catch { }

        return false;
    }

    private static bool IsLikelySettingsLabelText(Graphic g)
    {
        if (g == null) return false;

        string snippet = GetVisibleTextSnippet(g);
        if (string.IsNullOrWhiteSpace(snippet)) return false;

        try
        {
            string objName = g.gameObject.name ?? "";
            if (string.Equals(objName, "LabelText", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(objName, "Label", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }

        return IsScrollViewMenuText(g);
    }

    private static bool IsScrollViewMenuText(Graphic g)
    {
        if (g == null) return false;
        try
        {
            for (Transform t = g.transform; t != null; t = t.parent)
            {
                string n = t.gameObject.name ?? "";
                if (string.Equals(n, "Scroll View", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(n, "Viewport", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(n, "Content", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private static string GetTransformSummary(Graphic g)
    {
        if (g == null) return "n/a";
        try
        {
            var rt = g.rectTransform;
            var wp = rt.position;
            var lp = rt.localPosition;
            var sz = rt.rect.size;
            var sc = rt.lossyScale;
            return $"wp=({wp.x:F2},{wp.y:F2},{wp.z:F2}) lp=({lp.x:F2},{lp.y:F2},{lp.z:F2}) rs=({sz.x:F1},{sz.y:F1}) ls=({sc.x:F3},{sc.y:F3},{sc.z:F3})";
        }
        catch { return "n/a"; }
    }

    private static string GetParentChain(Graphic g, int maxDepth = 6)
    {
        if (g == null) return "n/a";
        try
        {
            var parts = new List<string>();
            int depth = 0;
            for (Transform t = g.transform; t != null && depth < maxDepth; t = t.parent, depth++)
                parts.Add(t.gameObject.name ?? "null");
            return string.Join(" <- ", parts);
        }
        catch { return "n/a"; }
    }

    private static Transform FindAncestorByName(Transform start, params string[] names)
    {
        try
        {
            for (Transform t = start; t != null; t = t.parent)
            {
                string n = t.gameObject.name ?? "";
                foreach (var want in names)
                {
                    if (string.Equals(n, want, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }
        }
        catch { }
        return null;
    }

    private static void SyncReadableFallbackRect(RectTransform src, RectTransform dst, float zOffset)
    {
        if (src == null || dst == null) return;
        try
        {
            var parent = dst.parent;
            Vector3 targetWorld = src.position + (src.forward * zOffset);

            dst.anchorMin = new Vector2(0.5f, 0.5f);
            dst.anchorMax = new Vector2(0.5f, 0.5f);
            dst.pivot = src.pivot;
            dst.sizeDelta = src.rect.size;

            if (parent != null)
            {
                dst.localPosition = parent.InverseTransformPoint(targetWorld);
                dst.localRotation = Quaternion.Inverse(parent.rotation) * src.rotation;
            }
            else
            {
                dst.position = targetWorld;
                dst.rotation = src.rotation;
            }

            dst.localScale = Vector3.one;
        }
        catch { }
    }

    private void EnsureMenuTextFallback(Graphic g)
    {
        if (g == null || !ShouldRelaxMenuClipping(g.canvas)) return;
        if (!IsLikelyNavButtonText(g) && !IsLikelySettingsLabelText(g)) return;

        var tmp = g.TryCast<TMP_Text>();
        if (tmp == null) return;

        string text = tmp.text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;

        int sourceId = g.GetInstanceID();
        if (!s_menuTextFallbacks.TryGetValue(sourceId, out var fallback) || fallback == null)
        {
            var go = new GameObject("VRReadableFallbackText");
            go.layer = g.gameObject.layer;
            try
            {
                Transform parent = g.transform.parent;
                if (IsLikelySettingsLabelText(g))
                    parent = FindAncestorByName(g.transform, "Components", "GraphicsSettingsPanel", "MainMenu") ?? parent;
                else if (IsLikelyNavButtonText(g))
                    parent = FindAncestorByName(g.transform, "ButtonArea", "GraphicsSettingsPanel", "MainMenu") ?? parent;
                if (parent == null && g.canvas != null) parent = g.canvas.transform;
                go.transform.SetParent(parent, true);
            }
            catch { }

            var rt = go.AddComponent<RectTransform>();
            fallback = go.AddComponent<TextMeshProUGUI>();
            fallback.raycastTarget = false;
            fallback.maskable = false;
            fallback.richText = false;
            fallback.enableWordWrapping = false;
            fallback.font = tmp.font;
            fallback.alignment = tmp.alignment;
            fallback.overflowMode = TextOverflowModes.Overflow;
            fallback.outlineColor = new Color32(0, 0, 0, 255);
            fallback.outlineWidth = 0.45f;
            fallback.faceColor = new Color32(255, 255, 255, 255);

            s_menuTextFallbacks[sourceId] = fallback;
            SyncReadableFallbackRect(g.rectTransform, rt, IsLikelySettingsLabelText(g) ? -0.03f : -0.02f);
        }

        try
        {
            bool highContrast = IsLikelyNavButtonText(g) || IsLikelySettingsLabelText(g);
            fallback.gameObject.layer = g.gameObject.layer;
            fallback.text = text;
            fallback.enabled = true;
            fallback.font = tmp.font;
            fallback.fontStyle = FontStyles.Bold;
            fallback.fontSize = tmp.fontSize;
            fallback.alignment = tmp.alignment;
            fallback.color = highContrast ? new Color(20f, 20f, 20f, 1f) : new Color(12f, 12f, 12f, 1f);
            fallback.faceColor = new Color32(255, 255, 255, 255);
            fallback.outlineColor = new Color32(0, 0, 0, 255);
            fallback.outlineWidth = 0.45f;
            fallback.canvasRenderer.cull = false;
            fallback.canvasRenderer.SetAlpha(1f);
            fallback.maskable = false;
            Material fbMat = null;
            try { fbMat = fallback.fontMaterial; } catch { }
            if (fbMat != null) ForceReadableTmpMaterialState(fbMat, tmp.font?.atlasTexture, true);
            SyncReadableFallbackRect(g.rectTransform, fallback.rectTransform, IsLikelySettingsLabelText(g) ? -0.03f : -0.02f);
            fallback.transform.SetAsLastSibling();
        }
        catch { }
    }

    private void RelaxMenuCanvasClipping(Canvas canvas)
    {
        if (!ShouldRelaxMenuClipping(canvas)) return;

        int canvasId = canvas.GetInstanceID();
        bool firstPass = s_menuMaskRelaxedCanvases.Add(canvasId);

        int disabledMasks = 0;
        try
        {
            var masks = canvas.GetComponentsInChildren<Mask>(true);
            foreach (var mask in masks)
            {
                if (mask == null || !mask.enabled) continue;
                mask.enabled = false;
                disabledMasks++;
            }
        }
        catch { }

        int disabledRectMasks = 0;
        try
        {
            var rectMasks = canvas.GetComponentsInChildren<RectMask2D>(true);
            foreach (var rectMask in rectMasks)
            {
                if (rectMask == null || !rectMask.enabled) continue;
                rectMask.enabled = false;
                disabledRectMasks++;
            }
        }
        catch { }

        int textUnmasked = 0;
        int rectClipCleared = 0;
        int rendererUnculled = 0;
        int canvasGroupsRaised = 0;
        try
        {
            var groups = canvas.GetComponentsInChildren<CanvasGroup>(true);
            foreach (var group in groups)
            {
                if (group == null) continue;
                if (group.alpha < 0.99f)
                {
                    group.alpha = 1f;
                    canvasGroupsRaised++;
                }
            }
        }
        catch { }
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            foreach (var g in graphics)
            {
                if (g == null || !IsTextGraphic(g)) continue;

                try
                {
                    if (g.canvasRenderer.hasRectClipping)
                    {
                        g.canvasRenderer.DisableRectClipping();
                        rectClipCleared++;
                    }
                }
                catch { }

                try
                {
                    if (g.canvasRenderer.cull)
                    {
                        g.canvasRenderer.cull = false;
                        rendererUnculled++;
                    }
                }
                catch { }

                var maskable = g.TryCast<MaskableGraphic>();
                if (maskable == null) continue;

                int gid = g.GetInstanceID();
                bool wasTracked = s_menuMaskabilityRelaxed.Contains(gid);
                if (maskable.maskable)
                {
                    maskable.maskable = false;
                    textUnmasked++;
                }
                if (!wasTracked) s_menuMaskabilityRelaxed.Add(gid);
            }
        }
        catch { }

        if (firstPass || disabledMasks > 0 || disabledRectMasks > 0 || textUnmasked > 0)
        {
            Log.LogInfo(
                $"[VRCamera] MenuClipRelax '{canvas.gameObject.name}': " +
                $"Mask={disabledMasks} RectMask2D={disabledRectMasks} TextMaskable={textUnmasked} RectClipCleared={rectClipCleared} " +
                $"TextUnculled={rendererUnculled} CanvasGroupAlpha={canvasGroupsRaised}");
        }
    }

    private static void NeutralizeStencilMasking(Material mat)
    {
        if (mat == null) return;

        try { if (mat.HasProperty("_Stencil")) mat.SetInt("_Stencil", 0); } catch { }
        try { if (mat.HasProperty("_StencilComp")) mat.SetInt("_StencilComp", (int)CompareFunction.Always); } catch { }
        try { if (mat.HasProperty("_StencilOp")) mat.SetInt("_StencilOp", (int)StencilOp.Keep); } catch { }
        try { if (mat.HasProperty("_StencilReadMask")) mat.SetInt("_StencilReadMask", 255); } catch { }
        try { if (mat.HasProperty("_StencilWriteMask")) mat.SetInt("_StencilWriteMask", 255); } catch { }
        try { if (mat.HasProperty("_ColorMask")) mat.SetInt("_ColorMask", 15); } catch { }
        try { if (mat.HasProperty("_UseUIAlphaClip")) mat.SetInt("_UseUIAlphaClip", 0); } catch { }
    }

    private static void StrengthenMenuTextMaterial(Material mat)
    {
        if (mat == null) return;

        try
        {
            if (mat.HasProperty("_FaceColor"))
                mat.SetColor("_FaceColor", new Color(8f, 8f, 8f, 1f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_Color"))
                mat.color = new Color(8f, 8f, 8f, 1f);
        }
        catch { }

        try
        {
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 1f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", 0.2f);
        }
        catch { }

        try
        {
            if (mat.HasProperty("_UnderlayColor"))
                mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.95f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_UnderlayOffsetX"))
                mat.SetFloat("_UnderlayOffsetX", 0.05f);
            if (mat.HasProperty("_UnderlayOffsetY"))
                mat.SetFloat("_UnderlayOffsetY", -0.05f);
            if (mat.HasProperty("_UnderlayDilate"))
                mat.SetFloat("_UnderlayDilate", 0.2f);
            if (mat.HasProperty("_UnderlaySoftness"))
                mat.SetFloat("_UnderlaySoftness", 0f);
        }
        catch { }

        try { mat.EnableKeyword("OUTLINE_ON"); } catch { }
        try { mat.EnableKeyword("UNDERLAY_ON"); } catch { }
    }

    private static void ForceReadableTmpMaterialState(Material mat, Texture atlasTexture, bool highContrast = false)
    {
        if (mat == null) return;

        NeutralizeStencilMasking(mat);
        StrengthenMenuTextMaterial(mat);

        try
        {
            if (atlasTexture != null && mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", atlasTexture);
        }
        catch { }

        try
        {
            if (atlasTexture != null && mat.HasProperty("_FaceTex"))
                mat.SetTexture("_FaceTex", atlasTexture);
        }
        catch { }

        try
        {
            if (mat.HasProperty("_FaceColor"))
                mat.SetColor("_FaceColor", highContrast ? new Color(20f, 20f, 20f, 1f) : new Color(8f, 8f, 8f, 1f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_Color"))
                mat.color = highContrast ? new Color(20f, 20f, 20f, 1f) : new Color(8f, 8f, 8f, 1f);
        }
        catch { }

        try
        {
            if (mat.HasProperty("_OutlineColor"))
                mat.SetColor("_OutlineColor", new Color(0f, 0f, 0f, 1f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_OutlineWidth"))
                mat.SetFloat("_OutlineWidth", highContrast ? 0.45f : 0.25f);
        }
        catch { }

        try
        {
            if (mat.HasProperty("_UnderlayColor"))
                mat.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 1f));
        }
        catch { }

        try
        {
            if (mat.HasProperty("_UnderlayOffsetX"))
                mat.SetFloat("_UnderlayOffsetX", highContrast ? 0.08f : 0.05f);
            if (mat.HasProperty("_UnderlayOffsetY"))
                mat.SetFloat("_UnderlayOffsetY", highContrast ? -0.08f : -0.05f);
            if (mat.HasProperty("_UnderlayDilate"))
                mat.SetFloat("_UnderlayDilate", highContrast ? 0.35f : 0.2f);
            if (mat.HasProperty("_UnderlaySoftness"))
                mat.SetFloat("_UnderlaySoftness", 0f);
        }
        catch { }

        try
        {
            if (mat.HasProperty("_TextureSampleAdd"))
                mat.SetVector("_TextureSampleAdd", Vector4.zero);
        }
        catch { }
    }

    private void ApplyMenuTextShaderFallback(Graphic g, Material mat)
    {
        // Disabled in favor of patching TMP's actual font materials directly.
    }

    private void ApplyMenuTmpReadableMaterial(Graphic g)
    {
        if (g == null) return;
        var tmp = g.TryCast<TMP_Text>();
        if (tmp == null) return;
        bool highContrast = IsLikelyNavButtonText(g) || IsLikelySettingsLabelText(g);

        try
        {
            tmp.color = highContrast ? new Color(20f, 20f, 20f, 1f) : new Color(10f, 10f, 10f, 1f);
        }
        catch { }

        try
        {
            tmp.faceColor = new Color32(255, 255, 255, 255);
        }
        catch { }

        try
        {
            tmp.outlineColor = new Color32(0, 0, 0, 255);
            tmp.outlineWidth = highContrast ? 0.45f : 0.2f;
        }
        catch { }

        Material source = null;
        try { source = tmp.fontMaterial; } catch { }
        if (source == null)
        {
            try { source = tmp.fontSharedMaterial; } catch { }
        }
        if (source == null) return;

        Texture atlasTexture = null;
        try { atlasTexture = tmp.font?.atlasTexture; } catch { }

        ForceReadableTmpMaterialState(source, atlasTexture, highContrast);

        int sourceId = source.GetInstanceID();
        if (!s_menuTmpReadableMats.TryGetValue(sourceId, out var readable))
        {
            readable = new Material(source);
            readable.name = "VRMenuTMP_" + source.name;
            s_menuTmpReadableMats[sourceId] = readable;
        }

        ForceReadableTmpMaterialState(readable, atlasTexture, highContrast);

        try { tmp.fontMaterial = readable; } catch { }
        try { tmp.fontSharedMaterial = readable; } catch { }
        try { tmp.havePropertiesChanged = true; } catch { }
        try { tmp.UpdateMeshPadding(); } catch { }
        try { tmp.UpdateMaterial(); } catch { }
        try { tmp.SetAllDirty(); } catch { }
        try { tmp.ForceMeshUpdate(false, false); } catch { }

        try { g.canvasRenderer.SetMaterial(readable, 0); } catch { }
        try { if (atlasTexture != null) g.canvasRenderer.SetTexture(atlasTexture); } catch { }
        try
        {
            var crMat = g.canvasRenderer.GetMaterial(0);
            if (crMat != null)
                ForceReadableTmpMaterialState(crMat, atlasTexture, highContrast);
        }
        catch { }
    }

    private void RelaxMenuTextMaterials(Canvas canvas)
    {
        if (!ShouldRelaxMenuClipping(canvas)) return;
        if (!canvas.enabled) return;  // skip hidden canvas — avoids mass material instantiation on enable/disable

        int patchedMaterials = 0;
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            foreach (var g in graphics)
            {
                if (g == null || !IsTextGraphic(g)) continue;

                try
                {
                    var mat = g.material;
                    if (mat != null)
                    {
                        NeutralizeStencilMasking(mat);
                        StrengthenMenuTextMaterial(mat);
                        patchedMaterials++;
                    }
                }
                catch { }

                try
                {
                    var crMat = g.canvasRenderer.GetMaterial(0);
                    if (crMat != null)
                    {
                        NeutralizeStencilMasking(crMat);
                        StrengthenMenuTextMaterial(crMat);
                        patchedMaterials++;
                    }
                }
                catch { }

                ApplyMenuTmpReadableMaterial(g);
                EnsureMenuTextFallback(g);
            }
        }
        catch { }

        if (patchedMaterials > 0)
            Log.LogInfo($"[VRCamera] MenuStencilRelax '{canvas.gameObject.name}': materials={patchedMaterials}");
    }

    private void RescanCanvasAlpha(Canvas canvas)
    {
        // Never mutate canvases owned by VRMod — we manage their materials directly.
        if (_ownedCanvasIds.Contains(canvas.GetInstanceID())) return;

        string canvasName = canvas.gameObject.name;
        try
        {
            RelaxMenuCanvasClipping(canvas);
            RelaxMenuTextMaterials(canvas);

            try
            {
                var transforms = canvas.GetComponentsInChildren<Transform>(true);
                int layerFixed = 0;
                foreach (var t in transforms)
                {
                    if (t == null) continue;
                    if (t.gameObject.layer != UILayer)
                    {
                        t.gameObject.layer = UILayer;
                        layerFixed++;
                    }
                }
                if (layerFixed > 0)
                    Log.LogInfo($"[VRCamera] LayerFix(rescan) '{canvasName}': {layerFixed} object(s) -> layer {UILayer}");
            }
            catch { }

            try
            {
                // Skip CanvasGroupFix for the VR Settings panel — its pane CanvasGroups are
                // intentionally set to interactable=false (not alpha=0) and must not be reset.
                bool isVrPanel = canvas.GetInstanceID() == VRSettingsPanel.CanvasInstanceId;
                if (!isVrPanel)
                {
                    var groups = canvas.GetComponentsInChildren<CanvasGroup>(true);
                    foreach (var cg in groups)
                    {
                        if (cg == null) continue;
                        if (cg.alpha < 0.99f)
                        {
                            Log.LogInfo($"[VRCamera] CanvasGroupFix '{cg.gameObject.name}' on '{canvasName}': {cg.alpha:F2}->1");
                            cg.alpha = 1f;
                        }
                    }
                }
            }
            catch { }

            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            int newCount = 0;
            foreach (var g in graphics)
            {
                if (g == null) continue;

                string nm = g.gameObject.name;
                bool isBg = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isFade = nm.IndexOf("fade", StringComparison.OrdinalIgnoreCase) >= 0;
                // Only suppress "FadeOverlay" (the menu's permanent black overlay).
                // Plain "Fade" (GameCanvas transition) must NOT be suppressed — the cutscene
                // state machine relies on it animating 0→1→0 before showing the video.
                bool isFadeOverlayRescan = nm.Equals("FadeOverlay", StringComparison.OrdinalIgnoreCase);
                bool isCutSceneGraphic = nm.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0
                                      || nm.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isText = IsTextGraphic(g);

                if (isFade)
                {
                    if (isFadeOverlayRescan)
                    {
                        int fid = g.GetInstanceID();
                        if (!_managedFades.ContainsKey(fid))
                        {
                            _managedFades[fid] = g;
                            Log.LogInfo($"[VRCamera] FadeSuppress(rescan) '{nm}' on '{canvasName}'");
                        }
                        if (g.color.a > 0f)
                            g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
                    }
                    // Skip material patching for ALL fade-named graphics regardless.
                    continue;
                }

                // Skip cutscene/video images — their dynamic textures must not be patched.
                if (isCutSceneGraphic) continue;

                IntPtr ptr = g.Pointer;
                if (!s_patchedGraphicPtrs.Contains(ptr))
                {
                    Material orig;
                    try { orig = g.material; }
                    catch { continue; }
                    if (orig == null) continue;

                    string shaderName = orig.shader?.name ?? "";
                    bool isAdditive = shaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                                   || shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                                   || shaderName.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0;
                    int boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                    int origId = orig.GetInstanceID();
                    int queue = isBg ? 3000 : (isText ? 3009 : (isAdditive ? 3001 : 3008));
                    long matKey = ((long)origId << 2) | (long)boostType;

                    if (!s_uiZTestMats.TryGetValue(matKey, out var mat))
                    {
                        mat = new Material(orig);
                        mat.name = "VRPatch_" + orig.name;
                        mat.SetInt("unity_GUIZTestMode", 8);
                        try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                        try { if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", 8); } catch { }
                        if (!isAdditive)
                        {
                            mat.renderQueue = queue;
                            if (isBg)
                            {
                                Color c = mat.color;
                                mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                            }
                        }
                        s_uiZTestMats[matKey] = mat;
                    }

                    try { g.material = mat; } catch { continue; }
                    s_patchedGraphicPtrs.Add(ptr);
                    s_patchedMats[ptr] = mat;
                    newCount++;
                }

                if (isBg)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas) ? 0.03f : 0.005f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (isText)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas)
                            ? (IsScrollViewMenuText(g) ? -0.08f : -0.03f)
                            : -0.005f;
                        if (lp.z > targetZ + 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (ShouldRelaxMenuClipping(canvas))
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        const float targetZ = 0.01f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }

                if (!isBg)
                {
                    try
                    {
                        var vc = g.color;
                        if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                    }
                    catch { }
                    if (isText) ApplyReadableTextBoost(g);
                    else        ApplyReadableImageBoost(g);
                }
            }

            if (newCount > 0)
                Log.LogInfo($"[VRCamera] RescanAlpha '{canvasName}': {newCount} new graphic(s) patched");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] RescanCanvasAlpha '{canvasName}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ConvertCanvasToWorldSpace(Canvas canvas)
    {
        var scaler = canvas.GetComponent<CanvasScaler>();
        float refW = scaler != null ? scaler.referenceResolution.x : Screen.width;
        float refH = scaler != null ? scaler.referenceResolution.y : Screen.height;
        if (refW <= 0) refW = 1920f;
        if (refH <= 0) refH = 1080f;

        canvas.renderMode = RenderMode.WorldSpace;

        var rt = canvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(refW, refH);
        canvas.transform.localScale = Vector3.one * UICanvasScale;

        int patched = ForceUIZTestAlways(canvas);

        try
        {
            var gr = canvas.GetComponent<GraphicRaycaster>();
            if (gr == null) gr = canvas.gameObject.AddComponent<GraphicRaycaster>();
            gr.blockingMask = 0;
            if (_leftCam != null) canvas.worldCamera = _leftCam;
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] GraphicRaycaster setup: {ex.Message}"); }

        Log.LogInfo($"[VRCamera] Canvas '{canvas.gameObject.name}' -> WorldSpace ({refW}x{refH}, scale={UICanvasScale}, sortOrder={canvas.sortingOrder}, patched={patched})");
    }

    // Finds buttons in MenuCanvas whose label text equals "Settings" and replaces their
    // onClick listener to open the VR Settings panel. Called once per MenuCanvas instance.
    // Cannot use GetComponentInParent<Button>() in IL2CPP — walks parents manually.
    private void PatchMenuSettingsButton(Canvas menuCanvas)
    {
        try
        {
            var texts = menuCanvas.GetComponentsInChildren<TMP_Text>(true);
            int patched = 0;
            foreach (var t in texts)
            {
                if (t == null) continue;
                string? s = t.text;
                if (s == null) continue;
                if (!s.Equals("Settings", StringComparison.OrdinalIgnoreCase)) continue;

                // Walk up the parent chain to find the Button (IL2CPP GetComponentInParent is broken).
                Button? btn = null;
                var tr = t.transform;
                for (int i = 0; i < 5 && tr != null; i++)
                {
                    btn = tr.gameObject.GetComponent<Button>();
                    if (btn != null) break;
                    tr = tr.parent;
                }
                if (btn == null) continue;

                // Replace onClick to suppress the game's persistent listener.
                // The actual VR panel open is handled in TryClickCanvas via _menuSettingsBtnId
                // to avoid IL2CPP AddListener reliability issues on freshly-created events.
                btn.onClick = new Button.ButtonClickedEvent();
                _menuSettingsBtnId = btn.gameObject.GetInstanceID();
                patched++;
                Log.LogInfo($"[VRCamera] Patched Settings button '{t.gameObject.name}' id={_menuSettingsBtnId}");
            }
            Log.LogInfo($"[VRCamera] PatchMenuSettingsButton: {patched} button(s) redirected to VR panel");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] PatchMenuSettingsButton failed: {ex.Message}");
        }
    }

    private int ForceUIZTestAlways(Canvas canvas, bool logQueueMap = true)
    {
        try
        {
            RelaxMenuCanvasClipping(canvas);
            RelaxMenuTextMaterials(canvas);

            var transforms = canvas.GetComponentsInChildren<Transform>(true);
            int layerFixed = 0;
            foreach (var t in transforms)
            {
                if (t == null) continue;
                if (t.gameObject.layer != UILayer)
                {
                    t.gameObject.layer = UILayer;
                    layerFixed++;
                }
            }
            if (layerFixed > 0)
                Log.LogInfo($"[VRCamera] LayerFix '{canvas.gameObject.name}': {layerFixed} object(s) -> layer {UILayer}");
        }
        catch { }

        int count = 0;
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null) continue;
                Material orig;
                try { orig = g.material; }
                catch { continue; }
                if (orig == null) continue;

                string nm = g.gameObject.name;
                string shaderName = orig.shader?.name ?? "";
                bool isBg = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isFadeGraphic = nm.IndexOf("fade", StringComparison.OrdinalIgnoreCase) >= 0;
                // "FadeOverlay" is the menu's permanent black overlay that must be suppressed.
                // Plain "Fade" (GameCanvas) is a momentary scene-transition element — do NOT
                // suppress it; let the game animate it freely so the cutscene state machine works.
                bool isFadeOverlay = nm.Equals("FadeOverlay", StringComparison.OrdinalIgnoreCase);
                bool isCutScene = nm.IndexOf("cutscene", StringComparison.OrdinalIgnoreCase) >= 0
                               || nm.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0;
                // Fade-named and cutscene/video graphics must not receive material patches or
                // brightness boosts. FadeOverlay is also added to _managedFades to keep alpha=0.
                if (isFadeGraphic || isCutScene)
                {
                    if (isFadeOverlay)
                    {
                        int fid = g.GetInstanceID();
                        if (!_managedFades.ContainsKey(fid))
                        {
                            _managedFades[fid] = g;
                            Log.LogInfo($"[VRCamera] FadeSuppress(ZTest) '{nm}' on '{canvas.gameObject.name}' (cur a={g.color.a:F2})");
                        }
                        // Immediately suppress — don't wait for PositionCanvases.
                        if (g.color.a > 0f)
                            g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
                    }
                    continue;
                }
                bool isAdditive = shaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Add", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isText = !isBg && !isAdditive && IsTextGraphic(g);
                int boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                int origId = orig.GetInstanceID();
                int queue = isBg ? 3000 : (isText ? 3009 : (isAdditive ? 3001 : 3008));
                long matKey = ((long)origId << 2) | (long)boostType;

                if (!s_uiZTestMats.TryGetValue(matKey, out var mat))
                {
                    mat = new Material(orig);
                    mat.name = "VRPatch_" + orig.name;
                    mat.SetInt("unity_GUIZTestMode", 8);
                    try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                    try { if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", 8); } catch { }
                    if (!isAdditive)
                    {
                        mat.renderQueue = queue;
                        if (isBg)
                        {
                            Color c = mat.color;
                            mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                        }
                    }
                    s_uiZTestMats[matKey] = mat;
                }

                try { g.material = mat; }
                catch { continue; }

                if (isBg)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas) ? 0.03f : 0.005f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (isText)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        float targetZ = ShouldRelaxMenuClipping(canvas)
                            ? (IsScrollViewMenuText(g) ? -0.08f : -0.03f)
                            : -0.005f;
                        if (lp.z > targetZ + 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }
                else if (ShouldRelaxMenuClipping(canvas))
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        const float targetZ = 0.01f;
                        if (lp.z < targetZ - 0.001f) rt.localPosition = new Vector3(lp.x, lp.y, targetZ);
                    }
                    catch { }
                }

                s_patchedGraphicPtrs.Add(g.Pointer);
                s_patchedMats[g.Pointer] = mat;

                if (!isBg)
                {
                    try
                    {
                        var vc = g.color;
                        if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                    }
                    catch { }
                    if (isText) ApplyReadableTextBoost(g);
                    else        ApplyReadableImageBoost(g);
                }
                count++;
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] ForceUIZTestAlways '{canvas.gameObject.name}': {ex.GetType().Name}: {ex.Message}");
        }

        int canvasId = canvas.GetInstanceID();
        if (logQueueMap && _queueMapLogged.Add(canvasId))
        {
            try
            {
                var graphics2 = canvas.GetComponentsInChildren<Graphic>(true);
                var sb = new System.Text.StringBuilder();
                sb.Append($"[VRCamera] QueueMap '{canvas.gameObject.name}' ({graphics2.Length}): ");
                int show = Mathf.Min(5, graphics2.Length);
                for (int di = 0; di < show; di++)
                {
                    if (graphics2[di] == null) continue;
                    var gm = graphics2[di].material;
                    int rq = gm != null ? gm.renderQueue : -1;
                    float ca = graphics2[di].color.a;
                    sb.Append($"[{di}]'{graphics2[di].gameObject.name}'=q{rq},a{ca:F2} ");
                }
                if (graphics2.Length > 10) sb.Append("... ");
                for (int di = Mathf.Max(5, graphics2.Length - 5); di < graphics2.Length; di++)
                {
                    if (graphics2[di] == null) continue;
                    var gm = graphics2[di].material;
                    int rq = gm != null ? gm.renderQueue : -1;
                    float ca = graphics2[di].color.a;
                    sb.Append($"[{di}]'{graphics2[di].gameObject.name}'=q{rq},a{ca:F2} ");
                }
                Log.LogInfo(sb.ToString());
            }
            catch { }
        }

        return count;
    }


    private void PositionCanvases()
    {
        if (_managedFades.Count > 0 && (_frameCount % 4) == 0)
        {
            foreach (var kvp in _managedFades)
            {
                var fg = kvp.Value;
                if (fg == null) continue;
                if (fg.color.a > 0f)
                    fg.color = new Color(fg.color.r, fg.color.g, fg.color.b, 0f);
            }
        }

        // Hide MenuCanvas while VR settings panel is open so it doesn't bleed through
        // behind the panel and confuse cursor tracking.
        // IMPORTANT: only toggle on state change — toggling canvas.enabled every frame causes
        // Unity to rebuild all MenuCanvas materials each cycle, flooding MenuStencilRelax
        // with ~986 new instances and crashing the game.
        if (_menuCanvasRef != null)
        {
            try
            {
                bool vrOpen = VRSettingsPanel.RootGO?.activeSelf == true;
                if (vrOpen != _menuCanvasHidden)
                {
                    _menuCanvasHidden = vrOpen;
                    _menuCanvasRef.enabled = !vrOpen;
                    var mgr = _menuCanvasRef.GetComponent<GraphicRaycaster>();
                    if (mgr != null) mgr.enabled = !vrOpen;
                }
            }
            catch { }
        }

        if (_leftCam == null || !_posesValid) return;

        bool anyUnplaced = false;
        foreach (var kvp in _managedCanvases)
            if (!_positionedCanvases.Contains(kvp.Key)) { anyUnplaced = true; break; }
        if (!anyUnplaced) return;

        Vector3 headPos = _leftCam.transform.position;
        float headYaw = _leftCam.transform.eulerAngles.y;
        Quaternion yawOnly = Quaternion.Euler(0f, headYaw, 0f);
        Vector3 forward = yawOnly * Vector3.forward;

        int suppressedPlacements = 0;
        foreach (var kvp in _managedCanvases)
        {
            var canvas = kvp.Value;
            bool isCursorCanvas = _cursorCanvas != null && canvas != null
                                  && canvas.GetInstanceID() == _cursorCanvas.GetInstanceID();

            // Non-cursor canvases (including settings panel): place once then mark positioned.
            // Cursor canvas: reposition every frame (never marked positioned) so it tracks the head.
            if (!isCursorCanvas)
            {
                if (_positionedCanvases.Contains(kvp.Key)) continue;
                _positionedCanvases.Add(kvp.Key);
            }

            if (canvas == null) continue;

            // sortingOrder nudge: higher order = closer to camera.
            // Cursor canvas uses _cursorAimDepth (updated each frame in UpdateControllerPose)
            // so it sits just 0.01m in front of whichever canvas is being aimed at — eliminating
            // the VR parallax gap that made the dot appear to float in front of the menu.
            float zNudge = isCursorCanvas ? 0f : -canvas.sortingOrder * 0.005f;
            Vector3 pos = isCursorCanvas
                ? headPos + forward * _cursorAimDepth + Vector3.up * UIVerticalOffset
                : headPos + forward * (UIDistance + zNudge) + Vector3.up * UIVerticalOffset;
            canvas.transform.position = pos;
            canvas.transform.rotation = yawOnly;
            if (!isCursorCanvas)
            {
                // Only log "notable" canvases; suppress generic/internal ones (Loading Icon, Content, etc.)
                string cn = canvas.gameObject.name;
                bool notable = cn.Contains("Canvas", StringComparison.OrdinalIgnoreCase)
                            || cn.StartsWith("VR",   StringComparison.OrdinalIgnoreCase)
                            || cn.Equals("3DUI",     StringComparison.OrdinalIgnoreCase);
                if (notable)
                    Log.LogInfo($"[VRCamera] Placed '{cn}' at {pos} yaw={headYaw:F1}deg");
                else
                    suppressedPlacements++;
            }
        }
        if (suppressedPlacements > 0)
            Log.LogInfo($"[VRCamera] Placed {suppressedPlacements} auxiliary canvas(es) (log suppressed)");
    }

    /// <summary>
    /// Reads the right controller pose, applies OpenXR-to-Unity coord flip, updates the
    /// cursor dot, and fires a canvas click on trigger press.
    /// </summary>
    private void UpdateControllerPose(long displayTime)
    {
        if (_rightControllerGO == null) return;

        bool poseOk = OpenXRManager.GetControllerPose(true, displayTime, out Quaternion ori, out Vector3 pos);

        _poseFrameCount++;
        if (!poseOk)
        {
            if (_cursorRect != null && _cursorVisible)
                try { _cursorVisible = false; _cursorRect.gameObject.SetActive(false); } catch { }
            if (!_poseEverValid && _poseFrameCount % 120 == 0)
                Log.LogInfo($"[VRCamera] Controller pose not valid (frame {_poseFrameCount}) - waiting");
            return;
        }

        if (!_poseEverValid)
        {
            _poseEverValid = true;
            Log.LogInfo($"[VRCamera] Controller pose FIRST VALID at frame {_poseFrameCount}: pos={pos} ori={ori}");
            Log.LogInfo($"[VRCamera] First valid pose: cursorRect={_cursorRect != null} cursorCanvas={_cursorCanvas != null} managedCanvases={_managedCanvases.Count}");
        }

        var uPos = new Vector3(pos.x, pos.y, -pos.z);
        var uOri = new Quaternion(-ori.x, -ori.y, ori.z, ori.w);

        _rightControllerGO.transform.position = transform.TransformPoint(uPos);
        _rightControllerGO.transform.rotation = transform.rotation * uOri;

        // Cursor dot: project controller ray onto the cursor canvas plane (always UIDistance
        // in front of the head), then set anchoredPosition to move the dot within the canvas.
        if (_cursorRect != null && _cursorCanvas != null)
        {
            // Lazily cache the cursor canvas half-size (available only after WorldSpace conversion).
            if (_cursorCanvasHalfSize == Vector2.zero)
            {
                var crt = _cursorCanvas.GetComponent<RectTransform>();
                if (crt != null && crt.sizeDelta.x > 0)
                    _cursorCanvasHalfSize = crt.sizeDelta * 0.5f;
            }

            Vector3 ctrlPos = _rightControllerGO.transform.position;
            Vector3 ctrlFwd = _rightControllerGO.transform.forward;
            var ray = new Ray(ctrlPos, ctrlFwd);

            var cursorPlane = new Plane(-_cursorCanvas.transform.forward, _cursorCanvas.transform.position);
            if (cursorPlane.Raycast(ray, out float cd) && cd > 0f)
            {
                Vector3 localHit = _cursorCanvas.transform.InverseTransformPoint(ctrlPos + ctrlFwd * cd);

                // Hide cursor if ray hits the canvas plane but lands outside the canvas rect.
                bool inBounds = _cursorCanvasHalfSize == Vector2.zero  // not yet cached — allow
                             || (Mathf.Abs(localHit.x) <= _cursorCanvasHalfSize.x
                              && Mathf.Abs(localHit.y) <= _cursorCanvasHalfSize.y);

                if (inBounds)
                {
                    _cursorRect.anchoredPosition = new Vector2(localHit.x, localHit.y);
                    if (!_cursorVisible) { _cursorVisible = true; _cursorRect.gameObject.SetActive(true); }

                    if (_poseFrameCount <= 5 || (_poseFrameCount % 120) == 0)
                        Log.LogInfo($"[VRCamera] Cursor: dist={cd:F2} px=({localHit.x:F0},{localHit.y:F0})");
                }
                else
                {
                    if (_cursorVisible) { _cursorVisible = false; _cursorRect.gameObject.SetActive(false); }
                }
            }
            else
            {
                if (_cursorVisible) { _cursorVisible = false; _cursorRect.gameObject.SetActive(false); }
            }
        }

        // Depth scan: find the nearest managed canvas (excluding cursor canvas) that the
        // controller ray actually hits within its rect. Store that canvas's head-forward depth
        // so PositionCanvases can place the cursor just 0.01m in front of it next frame.
        if (_rightControllerGO != null && _leftCam != null)
        {
            Vector3 dCtrlPos = _rightControllerGO.transform.position;
            Vector3 dCtrlFwd = _rightControllerGO.transform.forward;
            Vector3 dHeadPos = _leftCam.transform.position;
            Vector3 dHeadFwd = _leftCam.transform.forward;
            float   bestDepth = UIDistance - 0.01f; // fallback: just inside the default placement
            bool    foundHit  = false;

            foreach (var kvp in _managedCanvases)
            {
                var c = kvp.Value;
                if (c == null) continue;
                if (!c.gameObject.activeSelf) continue;  // skip hidden canvases (e.g. VR settings panel when closed)
                if (_cursorCanvas != null && c.GetInstanceID() == _cursorCanvas.GetInstanceID()) continue;

                var pl = new Plane(-c.transform.forward, c.transform.position);
                if (!pl.Raycast(new Ray(dCtrlPos, dCtrlFwd), out float hitDist) || hitDist <= 0f) continue;

                // Bounds check — only count canvases the ray actually falls inside.
                Vector3 lp = c.transform.InverseTransformPoint(dCtrlPos + dCtrlFwd * hitDist);
                var rt = c.GetComponent<RectTransform>();
                if (rt != null)
                {
                    Vector2 hs = rt.sizeDelta * 0.5f;
                    if (Mathf.Abs(lp.x) > hs.x || Mathf.Abs(lp.y) > hs.y) continue;
                }

                float depth = Vector3.Dot(c.transform.position - dHeadPos, dHeadFwd);
                if (!foundHit || depth < bestDepth) { bestDepth = depth; foundHit = true; }
            }

            _cursorAimDepth = bestDepth - 0.01f;
        }

        OpenXRManager.GetTriggerState(true, out bool triggerNow);
        bool triggerDown = triggerNow && !_prevTrigger;
        _prevTrigger = triggerNow;

        if (triggerDown)
            TryClickCanvas(_rightControllerGO.transform.position, _rightControllerGO.transform.forward);

        // Thumbstick Y scrolls the VR settings panel when it is open.
        // Dead-zone: ignore values < 0.2 to prevent drift.
        if (VRSettingsPanel.RootGO?.activeSelf == true)
        {
            if (OpenXRManager.GetThumbstickState(true, out float tx, out float ty))
            {
                const float deadZone   = 0.20f;
                const float scrollRate = 6.0f;  // pixels per frame at full deflection
                if (Mathf.Abs(ty) > deadZone)
                    VRSettingsPanel.Scroll(-ty * scrollRate); // negative: stick up → scroll up (lower y)
            }
        }

        // Left controller pose
        if (_leftControllerGO != null &&
            OpenXRManager.GetControllerPose(false, displayTime, out Quaternion lOri, out Vector3 lPos))
        {
            var ulPos = new Vector3(lPos.x, lPos.y, -lPos.z);
            var ulOri = new Quaternion(-lOri.x, -lOri.y, lOri.z, lOri.w);
            _leftControllerGO.transform.position = transform.TransformPoint(ulPos);
            _leftControllerGO.transform.rotation = transform.rotation * ulOri;
        }
    }

    /// <summary>
    /// Rotates VROrigin around Y by ±SnapTurnAngle when right stick X crosses the dead-zone.
    /// Skipped while the VR settings panel is open (right stick Y is used for scrolling there).
    /// </summary>
    private void UpdateSnapTurn()
    {
        _snapCooldown -= Time.deltaTime;

        // Don't snap while settings panel is open (right stick scrolls it instead)
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;

        if (!OpenXRManager.GetThumbstickState(true, out float tx, out float _)) return;

        float absTx = Mathf.Abs(tx);

        // Re-arm when stick returns to centre
        if (!_snapArmed && absTx < SnapTurnRearm)
        {
            _snapArmed = true;
            return;
        }

        if (_snapArmed && absTx > SnapTurnDeadZone && _snapCooldown <= 0f)
        {
            float angle = Mathf.Sign(tx) * SnapTurnAngle;
            transform.Rotate(Vector3.up, angle, Space.World);
            _snapCooldown = SnapTurnCooldown;
            _snapArmed    = false;
            Log.LogInfo($"[VRCamera] Snap turn {angle:+0;-0}° (stick={tx:F2})");
        }
    }

    /// <summary>
    /// One-shot: logs Rewired action names and walks up from the game camera to find
    /// CharacterController / Rigidbody / RigidbodyFirstPersonController.
    /// Results are read from LogOutput.log to choose the locomotion approach.
    /// </summary>
    /// <summary>
    /// Drives the player character via left thumbstick. Head-relative: forward/back follows
    /// HMD yaw, strafe follows HMD right. Preserves Rigidbody Y velocity (gravity/jumping).
    /// Skipped when VR settings panel is open.
    /// </summary>
    private void UpdateLocomotion()
    {
        if (_playerRb == null) return;
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;

        if (!OpenXRManager.GetThumbstickState(false, out float lx, out float ly)) return;
        if (Mathf.Abs(lx) <= MoveDeadZone && Mathf.Abs(ly) <= MoveDeadZone)
        {
            // No stick input — let the game own horizontal velocity fully
            return;
        }

        // Apply dead-zone scaling so motion starts smoothly at the threshold
        float dx = Mathf.Abs(lx) > MoveDeadZone ? lx : 0f;
        float dy = Mathf.Abs(ly) > MoveDeadZone ? ly : 0f;

        // Head-relative direction: use HMD yaw (left eye camera world yaw)
        float headYaw = _leftCam != null ? _leftCam.transform.eulerAngles.y : transform.eulerAngles.y;
        Vector3 fwd   = Quaternion.Euler(0f, headYaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, headYaw, 0f) * Vector3.right;

        Vector3 hMove = (fwd * dy + right * dx) * MoveSpeed;

        // Preserve vertical velocity so gravity and jumping are unaffected
        _playerRb.velocity = new Vector3(hMove.x, _playerRb.velocity.y, hMove.z);
    }

    private void DiscoverMovementSystem()
    {
        _movementDiscoveryDone = true;

        // 1. Enumerate all Rewired actions (index loop — IL2CPP IList<T> has no GetEnumerator)
        try
        {
            var actions = Rewired.ReInput.mapping.Actions;
            if (actions != null)
            {
                // Cast to Il2CppSystem list to get Count
                var list = actions.TryCast<Il2CppSystem.Collections.Generic.List<Rewired.InputAction>>();
                int cnt = list != null ? list.Count : -1;
                Log.LogInfo($"[Movement] Rewired actions (cnt={cnt}):");
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var a = list[i];
                        Log.LogInfo($"[Movement]   id={a?.id} name='{a?.name}' type={a?.type}");
                    }
            }
            else Log.LogWarning("[Movement] ReInput.mapping.Actions is null");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Actions enum: {ex.Message}"); }

        // 2. Rewired player 0 info
        try
        {
            var player = Rewired.ReInput.players.GetPlayer(0);
            Log.LogInfo($"[Movement] Rewired player0: name='{player?.name}' id={player?.id}");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Player0: {ex.Message}"); }

        // 3. Walk up from game camera to find CharacterController / Rigidbody; cache RB for locomotion
        try
        {
            var t = _gameCam;
            for (int i = 0; i < 10 && t != null; i++)
            {
                var cc = t.GetComponent<CharacterController>();
                var rb = t.GetComponent<Rigidbody>();
                Log.LogInfo($"[Movement] Ancestor[{i}] '{t.gameObject.name}': CC={cc != null} RB={rb != null}");
                if (rb != null)
                {
                    _playerRb = rb;
                    Log.LogInfo($"[Movement] Cached playerRb on '{t.gameObject.name}'");
                    break;
                }
                if (cc != null) break; // CC but no RB — won't use velocity approach
                t = t.parent;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Walk-up: {ex.Message}"); }

        // 4. FindObjectOfType for the FPS controllers
        try
        {
            var cc = FindObjectOfType<CharacterController>();
            if (cc != null)
                Log.LogInfo($"[Movement] FindObjectOfType<CC>: GO='{cc.gameObject.name}' pos={cc.transform.position}");
            else
                Log.LogInfo("[Movement] FindObjectOfType<CC>: not found");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] FindCC: {ex.Message}"); }
    }
    /// <summary>
    /// Casts a ray from the right controller into all managed WorldSpace canvases.
    /// Finds the closest canvas plane intersection, uses GraphicRaycaster to resolve
    /// the UI element under that point, and fires a pointer click event.
    /// </summary>
    private void TryClickCanvas(Vector3 origin, Vector3 direction)
    {
        if (_leftCam == null) return;

        // Collect all canvas plane intersections sorted by distance.
        // We fall through to the next canvas if the raycaster returns no results
        // (e.g. startup splash Canvas plane intercepts before MenuCanvas).
        var ray = new Ray(origin, direction);
        var hits = new System.Collections.Generic.List<(float dist, Canvas canvas, Vector3 wp)>();

        foreach (var kvp in _managedCanvases)
        {
            var canvas = kvp.Value;
            if (canvas == null) continue;
            var plane = new Plane(-canvas.transform.forward, canvas.transform.position);
            if (!plane.Raycast(ray, out float dist)) continue;
            if (dist <= 0f) continue;
            Vector3 wp = origin + direction * dist;
            if (_leftCam.WorldToScreenPoint(wp).z < 0f) continue;
            hits.Add((dist, canvas, wp));
        }

        if (hits.Count == 0) return;
        hits.Sort((a, b) => a.dist.CompareTo(b.dist));

        var es = EventSystem.current;
        if (es == null) return;

        foreach (var (dist, hitCanvas, hitWorld) in hits)
        {
            if (hitCanvas.worldCamera == null) hitCanvas.worldCamera = _leftCam;
            var gr = hitCanvas.GetComponent<GraphicRaycaster>();
            if (gr == null) continue;

            Vector3 screenPt = _leftCam.WorldToScreenPoint(hitWorld);
            var ped = new PointerEventData(es);
            ped.position = new Vector2(screenPt.x, screenPt.y);

            try
            {
                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                gr.Raycast(ped, results);

                if (results.Count > 0)
                {
                    var go = results[0].gameObject;
                    Log.LogInfo($"[VRCamera] Trigger click: '{go?.name}' on '{hitCanvas.gameObject.name}'");

                    // Check if the click landed on (or inside) the patched Settings button.
                    // Walk up the hierarchy — the raycasted GO may be a child label, not the button itself.
                    if (_menuSettingsBtnId != 0)
                    {
                        var tr = go?.transform;
                        for (int i = 0; i < 5 && tr != null; i++)
                        {
                            if (tr.gameObject.GetInstanceID() == _menuSettingsBtnId)
                            {
                                Log.LogInfo("[VRCamera] Settings button intercepted → VRSettingsPanel.Toggle");
                                VRSettingsPanel.Toggle();
                                return;
                            }
                            tr = tr.parent;
                        }
                    }

                    ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerEnterHandler);
                    ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerDownHandler);
                    ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerUpHandler);
                    ExecuteEvents.ExecuteHierarchy(go, ped, ExecuteEvents.pointerClickHandler);
                    return; // handled — stop checking further canvases
                }
                // No graphic at this canvas plane — fall through to next closest canvas
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[VRCamera] TryClickCanvas: {ex.Message}");
            }
        }
    }

    // OpenXR: right-handed (+Y up, +X right, -Z forward)
    // Unity:  left-handed  (+Y up, +X right, +Z forward)
    //
    // Position: flip Z  →  (x, y, -z)
    //
    // Quaternion: change-of-basis M = diag(1,1,-1), conjugation q' = M·R(q)·M yields
    //   q_Unity = (-qx, -qy, qz, qw)
    //   (negate X and Y; keep Z and W)
    //   Verified: pitch-up → -X ✓  yaw-left → -Y ✓  roll-right → -Z ✓
    private static void ApplyCameraPose(Transform t, OpenXRManager.EyePose eye)
    {
        t.localPosition = new Vector3( eye.Position.x,
                                        eye.Position.y,
                                       -eye.Position.z);
        t.localRotation = new Quaternion(-eye.Orientation.x,
                                         -eye.Orientation.y,
                                          eye.Orientation.z,
                                          eye.Orientation.w);
    }

    // Off-centre perspective from OpenXR tangent-angle FOV.
    // angleLeft ≤ 0, angleRight ≥ 0, angleUp ≥ 0, angleDown ≤ 0.
    // Row 1 (Y) is negated to compensate for Unity D3D11 storing RenderTextures Y-flipped.
    private static void SetProjection(Camera cam, OpenXRManager.EyePose eye)
    {
        float n = cam.nearClipPlane, f = cam.farClipPlane;
        float l = Mathf.Tan(eye.FovLeft)  * n;
        float r = Mathf.Tan(eye.FovRight) * n;
        float t = Mathf.Tan(eye.FovUp)    * n;
        float b = Mathf.Tan(eye.FovDown)  * n;
        var m = Matrix4x4.Frustum(l, r, b, t, n, f);
        m.m10 = -m.m10; m.m11 = -m.m11; m.m12 = -m.m12; m.m13 = -m.m13;
        cam.projectionMatrix = m;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnDestroy()
    {
        _stereoReady = false;
        if (_leftRT  != null) { _leftRT.Release();  Destroy(_leftRT);  }
        if (_rightRT != null) { _rightRT.Release(); Destroy(_rightRT); }

        VRSettingsPanel.Destroy();

        // Restore any canvases we converted so the desktop view isn't broken
        // if VR is disabled mid-session.
        foreach (var kvp in _managedCanvases)
            if (kvp.Value != null)
                kvp.Value.renderMode = RenderMode.ScreenSpaceOverlay;
        _managedCanvases.Clear();
        _positionedCanvases.Clear();
        _managedFades.Clear();

        Log.LogInfo("[VRCamera] Destroyed.");
    }
}
