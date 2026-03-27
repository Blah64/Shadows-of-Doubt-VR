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
    private Camera        _leftCam    = null!;   // scene camera — all layers except UI
    private Camera        _rightCam   = null!;
    private Camera        _leftUICam  = null!;   // UI overlay camera — layer 5 only, no exposure/tonemapping
    private Camera        _rightUICam = null!;
    private RenderTexture _leftRT     = null!;
    private RenderTexture _rightRT    = null!;
    private Transform     _gameCam    = null!;   // original game camera transform; we follow its world position

    // Unity built-in UI layer.  Canvas GameObjects default to this layer; all their
    // children inherit it.  Scene cameras exclude it so only UI cameras render UI.
    private const int UILayer       = 5;
    // Private layer used exclusively for the VRExposureOverride Volume.
    // UI cameras set volumeLayerMask = 1<<UIVolumeLayer so only they see EV=0/NoTonemapping.
    // Scene cameras set volumeLayerMask = ~(1<<UIVolumeLayer) so they never see it.
    private const int UIVolumeLayer = 31;

    // Render throttle: call Camera.Render() every N stereo frames.
    // 1 = every frame (full quality). 2 = every other frame (half GPU load, slight judder).
    // The swapchain copy still runs every frame, so head tracking stays smooth via ATW.
    private const int RenderEveryNFrames = 1;

    // ── UI / Canvas constants ─────────────────────────────────────────────────
    private const float UIDistance       = 2.0f;    // metres in front of head
    private const float UIVerticalOffset = 0.0f;    // metres up/down from eye level
    private const float UICanvasScale    = 0.0015f; // world-units per canvas pixel (1920px → 2.88 m wide)
    private const int   UICanvasScanRate = 30;      // Unity frames between canvas scans
    // ExposureControl + Tonemapping are now disabled in s_VrDisabledFields for the eye cameras,
    // so linear UI colours map directly to LDR output.  Only a very small boost is needed:
    //   1.0 = literal material colour (white text → white in headset)
    //   1.5 = slight contrast lift for text so it pops against mid-grey backgrounds
    // UI cameras use a dedicated Volume: EV=0, Tonemapping=None.
    // Linear output clips to [0,1] directly.  A boost >1 guarantees near-white even if
    // the original material colour is slightly less than 1.  Background is kept dim.
    private const float UIColorBoost     = 2.0f;  // (unused — text colour no longer overridden; kept for reference)
    private const float UIImageBoost     = 0.7f;  // images: slightly darker than original → better contrast with text
    // Alpha applied to any Graphic whose GameObject name contains "background".
    // Makes the canvas backdrop semi-transparent so buttons and text show through it
    // even when HDRP's distance sort happens to render the background last.
    private const float UIBackgroundAlpha = 0.25f;

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

    // Fullscreen "fade overlay" Graphics that should stay at alpha=0 during VR gameplay.
    // The game uses these for fade-to-black transitions; they end up opaque and bury buttons.
    // We force their vertex alpha to 0 every frame so the menu remains readable.
    // Key = Graphic instanceID.
    private readonly Dictionary<int, Graphic> _managedFades = new();

    // ── Controller / cursor dot ───────────────────────────────────────────────
    private GameObject?   _rightControllerGO;
    // Cursor approach: create a ScreenSpaceOverlay canvas "VRCursorCanvasInternal" at startup.
    // ScanAndConvertCanvases picks it up and converts it to WorldSpace through the SAME pipeline
    // as all game canvases — this gives it proper HDRP registration, which is why game canvases
    // ARE visible while standalone WorldSpace canvases created from scratch are NOT.
    // sortingOrder=100 ensures it is drawn on top of all game canvases (max sortingOrder ≈ 10).
    private Canvas?        _cursorCanvas;   // VRCursorCanvasInternal once scan converts it
    private RectTransform? _cursorRect;     // the dot's RectTransform inside _cursorCanvas
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
    private const int UIMaterialVersion = 2;
    // Material instance IDs whose shader has been swapped from TMP/Distance Field to UI/Default.
    // Populated once per unique font material; TMP rebuilds reuse the same material object so
    // the swap persists across redraws without triggering a rebuild loop.
    private static readonly HashSet<int> s_shaderSwappedMats = new();
    // Tracks RectMask2D component native pointers we have already disabled, to avoid
    // re-logging them every 30 frames.
    private static readonly HashSet<IntPtr> s_disabledRectMasks = new();

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

        // Home key: re-centre all canvases in front of the current head pose.
        // Useful after loading or if menus appear at the wrong height/direction.
        if (Input.GetKeyDown(KeyCode.Home))
        {
            _positionedCanvases.Clear();
            Log.LogInfo("[VRCamera] Recenter: canvases will be re-placed on next LateUpdate.");
        }

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
                    string crMn = crMat?.name ?? "null";
                    string crSh = crMat?.shader?.name ?? "null";
                    // TMP-specific diagnostics
                    string tmpCast = "noTMP";
                    string tmpCol  = "n/a";
                    try
                    {
                        var tmp = g.TryCast<TMP_Text>();
                        if (tmp != null)
                        {
                            tmpCast = "OK";
                            var tc = tmp.color;
                            tmpCol = $"({tc.r:F2},{tc.g:F2},{tc.b:F2},a={tc.a:F2})";
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
                    Log.LogInfo($"[VRCamera] TXT '{cv.gameObject.name}'/{g.gameObject.name} " +
                                $"g.col=({vc.r:F2},{vc.g:F2},{vc.b:F2},a={vc.a:F2}) " +
                                $"tmp.col={tmpCol} tmpCast={tmpCast} " +
                                $"crA={cra:F2} lz={lz:F3} crShader={crSh} " +
                                $"TSA=({tsa.x:F1},{tsa.y:F1},{tsa.z:F1},{tsa.w:F1}) texFmt={texFmt} " +
                                $"patched={patched} matSwapped={matSwapped}");
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
            SetProjection(_leftCam,    _leftEye);
            SetProjection(_rightCam,   _rightEye);
            // UI cameras inherit world pose from scene cameras (parented to them),
            // but need their projection matrices set explicitly each frame.
            SetProjection(_leftUICam,  _leftEye);
            SetProjection(_rightUICam, _rightEye);
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
        }
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
        // They use the game's own auto-exposure + tonemapping volumes untouched.
        var leftGO = new GameObject("LeftEye");
        leftGO.transform.SetParent(_cameraOffset, false);
        _leftCam = leftGO.AddComponent<Camera>();
        _leftRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Left" };
        _leftRT.Create();
        SetupEyeCam(_leftCam, _leftRT);
        _leftCam.cullingMask = ~(1 << UILayer); // exclude UI layer — rendered by UI cameras

        var rightGO = new GameObject("RightEye");
        rightGO.transform.SetParent(_cameraOffset, false);
        _rightCam = rightGO.AddComponent<Camera>();
        _rightRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Right" };
        _rightRT.Create();
        SetupEyeCam(_rightCam, _rightRT);
        _rightCam.cullingMask = ~(1 << UILayer);

        // Prevent scene cameras from seeing the VRExposureOverride volume (layer UIVolumeLayer).
        // We set the volumeLayerMask to all layers except UIVolumeLayer so the game's own
        // exposure/tonemapping volumes are used unchanged for scene rendering.
        try
        {
            var hdL = _leftCam.gameObject.GetComponent<HDAdditionalCameraData>();
            var hdR = _rightCam.gameObject.GetComponent<HDAdditionalCameraData>();
            int sceneMask = ~(1 << UIVolumeLayer);
            if (hdL != null) hdL.volumeLayerMask = sceneMask;
            if (hdR != null) hdR.volumeLayerMask = sceneMask;
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] Scene camera volumeLayerMask: {ex.Message}"); }

        // ── VRExposureOverride Volume (layer UIVolumeLayer = 31) ─────────────────
        // Lives on layer 31 — only the UI cameras' volumeLayerMask includes this layer.
        // EV=0 + Tonemapping=None → UI colours output as direct linear LDR:
        //   material (2,2,2,a) → clamps to (1,1,1) → white in headset.
        // Scene cameras' volumeLayerMask excludes layer 31, so their rendering is
        // completely unaffected and uses the game's own exposure/tonemapping.
        try
        {
            var volGO = new GameObject("VRExposureOverride");
            volGO.layer = UIVolumeLayer;           // only UI cameras can query it
            volGO.transform.SetParent(transform, false);
            var vol     = volGO.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 1000f;
            var profile  = ScriptableObject.CreateInstance<VolumeProfile>();

            var exp = profile.Add<Exposure>(true);
            exp.mode.Override(ExposureMode.Fixed);
            exp.fixedExposure.Override(-10f);      // EV=-10 diagnostic: if text brightens, volume IS applied but EV=0 was wrong

            var tm = profile.Add<Tonemapping>(true);
            tm.mode.Override(TonemappingMode.None); // no tonemapping — linear→LDR clamp

            vol.sharedProfile = profile;
            Log.LogInfo("[VRCamera] VRExposureOverride volume on layer 31: EV=0, Tonemapping=None");
        }
        catch (Exception volEx)
        {
            Log.LogWarning($"[VRCamera] VRExposureOverride volume setup failed: {volEx.Message}");
        }

        // ── UI overlay cameras (render ONLY the UI layer) ────────────────────────
        // Parented to their scene-camera sibling so they automatically share the same
        // world pose after ApplyCameraPose + SetProjection are called in Update.
        // clearFlags=Depth keeps scene colour and clears depth so UI never z-fights.
        // volumeLayerMask = 1<<UIVolumeLayer → only the VRExposureOverride volume,
        // giving EV=0 + Tonemapping=None → full-brightness direct LDR output for UI.
        _leftUICam  = CreateUIOverlayCam("LeftEyeUI",  _leftCam,  _leftRT);
        _rightUICam = CreateUIOverlayCam("RightEyeUI", _rightCam, _rightRT);

        // Try to find and disable the game camera now. If it's not available yet
        // (e.g. main menu hasn't spawned one), TryFindGameCamera() will keep retrying in Update().
        TryFindGameCamera();

        // Right-controller cursor dot.
        // Strategy: create the cursor canvas as ScreenSpaceOverlay so that
        // ScanAndConvertCanvases finds it and converts it through the normal pipeline.
        // All game canvases ARE visible because they go through that pipeline.
        // Standalone WorldSpace canvases created from scratch do NOT render (HDRP registration).
        var ctrlGO = new GameObject("RightController");
        ctrlGO.layer = UILayer;
        ctrlGO.transform.SetParent(_cameraOffset, false);
        _rightControllerGO = ctrlGO;

        try
        {
            var ccGO = new GameObject("VRCursorCanvasInternal");
            ccGO.layer = UILayer;
            UnityEngine.Object.DontDestroyOnLoad(ccGO); // survive scene transitions
            var cc = ccGO.AddComponent<Canvas>();
            cc.renderMode  = RenderMode.ScreenSpaceOverlay; // ScanAndConvertCanvases will convert it
            cc.sortingOrder = 100; // drawn on top of all game canvases (max game sortingOrder ≈ 10)

            var dotGO = new GameObject("VRCursorDot");
            dotGO.layer = UILayer;
            _cursorRect = dotGO.AddComponent<RectTransform>();
            _cursorRect.SetParent(ccGO.transform, false);
            _cursorRect.sizeDelta       = new Vector2(20f, 20f); // 20 canvas px ≈ 3 cm at UICanvasScale
            _cursorRect.anchorMin       = new Vector2(0.5f, 0.5f);
            _cursorRect.anchorMax       = new Vector2(0.5f, 0.5f);
            _cursorRect.pivot           = new Vector2(0.5f, 0.5f);
            _cursorRect.anchoredPosition = Vector2.zero;

            var img = dotGO.AddComponent<Image>();
            img.raycastTarget = false;
            // Material + color set by ForceUIZTestAlways when canvas is converted.
            // Will render as a bright white/boosted dot — visible on any background.

            _cursorRect.gameObject.SetActive(false); // hidden until controller pose is valid
            Log.LogInfo("[VRCamera] VRCursorCanvasInternal created (ScreenSpaceOverlay → will be converted by scan)");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] Cursor canvas creation failed: {ex.Message}");
        }

        Log.LogInfo($"[VRCamera] Rig built: {w}x{h} ARGB32");
    }

    /// <summary>
    /// Creates a UI overlay camera that renders on top of <paramref name="sceneCam"/>'s output.
    /// The new GO is parented to <paramref name="sceneCam"/>'s transform so it automatically
    /// inherits whatever pose ApplyCameraPose + SetProjection set each frame.
    /// </summary>
    private Camera CreateUIOverlayCam(string goName, Camera sceneCam, RenderTexture rt)
    {
        var go  = new GameObject(goName);
        go.transform.SetParent(sceneCam.transform, false); // inherits scene cam pose
        var cam = go.AddComponent<Camera>();
        cam.targetTexture   = rt;
        cam.clearFlags      = CameraClearFlags.Depth; // keep scene colour; clear depth
        cam.cullingMask     = 1 << UILayer;           // UI layer only
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.nearClipPlane   = 0.01f;
        cam.farClipPlane    = 1000f;
        cam.allowHDR        = false;
        cam.allowMSAA       = false;
        cam.enabled         = false; // driven manually in LateUpdate
        cam.depth           = sceneCam.depth + 1; // render after scene camera
        try
        {
            var hd = go.AddComponent<HDAdditionalCameraData>();
            hd.antialiasing         = HDAdditionalCameraData.AntialiasingMode.None;
            hd.dithering            = false;
            hd.hasPersistentHistory = false;
            // ClearColorMode.None = don't clear the colour buffer; composite UI on top of
            // whatever the scene camera already wrote to the shared RenderTexture.
            // Default is ClearColorMode.Sky which would erase the scene render.
            hd.clearColorMode       = HDAdditionalCameraData.ClearColorMode.None;
            // Only query the VRExposureOverride volume (layer 31).
            // This gives EV=0 + Tonemapping=None → UI linear colours map directly to LDR.
            hd.volumeLayerMask      = 1 << UIVolumeLayer;
            Log.LogInfo($"[VRCamera] UI overlay cam '{goName}': layer5-only, volumeMask=layer{UIVolumeLayer}");

            // Disable ExposureControl + Tonemapping via FrameSettings so HDRP's exposure
            // correction doesn't darken unlit UI/Default vertex colours (white → ~0.1 grey).
            // Same pattern as SetupEyeCam where TonemapOff=True confirmed the write sticks.
            try
            {
                var uiFS = new[] { FrameSettingsField.ExposureControl, FrameSettingsField.Tonemapping };
                var om   = hd.renderingPathCustomFrameSettingsOverrideMask;
                foreach (var f in uiFS) om.mask[(uint)(int)f] = true;
                hd.renderingPathCustomFrameSettingsOverrideMask = om;
                bool maskOk = hd.renderingPathCustomFrameSettingsOverrideMask
                                .mask[(uint)(int)FrameSettingsField.ExposureControl];
                if (maskOk)
                {
                    var fs = hd.renderingPathCustomFrameSettings;
                    foreach (var f in uiFS) fs.SetEnabled(f, false);
                    hd.renderingPathCustomFrameSettings = fs;
                    hd.customRenderingSettings = true;
                    var rb     = hd.renderingPathCustomFrameSettings;
                    bool expOff = !rb.IsEnabled(FrameSettingsField.ExposureControl);
                    bool tmOff  = !rb.IsEnabled(FrameSettingsField.Tonemapping);
                    Log.LogInfo($"[VRCamera] UI cam '{goName}' FS: ExposureOff={expOff} TonemapOff={tmOff}");
                }
                else Log.LogWarning($"[VRCamera] UI cam '{goName}': FS override mask didn't persist");
            }
            catch (Exception fsEx)
            {
                Log.LogWarning($"[VRCamera] UI cam '{goName}' FS setup: {fsEx.Message}");
            }
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UI cam HDData failed: {ex.Message}"); }
        return cam;
    }

    // Search all active cameras for one that isn't one of ours.
    // Disables it so it doesn't render independently, and tracks its transform.
    private void TryFindGameCamera()
    {
        foreach (var cam in Camera.allCameras)
        {
            if (cam == _leftCam || cam == _rightCam) continue;
            if (cam == _leftUICam || cam == _rightUICam) continue;
            if (!cam.gameObject.activeInHierarchy)  continue;
            _gameCam = cam.transform;
            cam.enabled = false;
            Log.LogInfo($"[VRCamera] Found game camera: '{cam.gameObject.name}' pos={cam.transform.position}");
            transform.position = _gameCam.position;
            return;
        }
    }

    // Expensive HDRP passes to disable on VR eye cameras.
    // None of these contribute meaningfully to VR immersion at the cost they incur.
    private static readonly FrameSettingsField[] s_VrDisabledFields =
    {
        FrameSettingsField.SSAO,                // Screen-space ambient occlusion
        FrameSettingsField.SSR,                 // Screen-space reflections
        FrameSettingsField.Volumetrics,         // Volumetric fog/lighting
        FrameSettingsField.MotionVectors,       // Only needed for TAA/motion-blur (both off)
        FrameSettingsField.MotionBlur,          // Causes VR sickness, not useful
        FrameSettingsField.DepthOfField,        // Conflicts with VR focus distance
        FrameSettingsField.ChromaticAberration, // Post-process noise, no VR benefit
        FrameSettingsField.ContactShadows,      // Screen-space shadow detail pass
        // Exposure + tonemapping compress linear (1,1,1) UI colours to ~0.5 grey in the headset.
        // Disabling both gives direct linear→LDR output so UI/text renders at its literal colour.
        // Scene geometry may lose auto-exposure correction but remains fully readable in VR.
        FrameSettingsField.ExposureControl,     // Bypass HDRP auto-exposure on eye cameras
        FrameSettingsField.Tonemapping,         // Bypass tonemapping; LDR clamp gives correct UI white
    };

    private void SetupEyeCam(Camera cam, RenderTexture rt)
    {
        cam.targetTexture   = rt;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.nearClipPlane   = 0.01f;
        cam.farClipPlane    = 1000f;
        cam.allowHDR        = false;  // LDR matches UNORM_SRGB swapchain format
        cam.allowMSAA       = false;  // HDRP manages AA itself; disabled below
        cam.enabled         = false;  // we call Render() explicitly in LateUpdate

        try
        {
            var hd = cam.gameObject.GetComponent<HDAdditionalCameraData>()
                  ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();

            // Disable TAA — causes per-frame history buffer writes (memory leak) and VR ghosting.
            hd.antialiasing     = HDAdditionalCameraData.AntialiasingMode.None;
            hd.dithering        = false;
            hd.hasPersistentHistory = false;

            // ── FrameSettings: disable expensive passes ────────────────────────
            // Step 1: build override mask — get the struct, set bits, write back via setter.
            // renderingPathCustomFrameSettingsOverrideMask has a real setter in HDRP 13,
            // so the get→modify→set-back pattern works correctly here.
            var om = hd.renderingPathCustomFrameSettingsOverrideMask;
            foreach (var f in s_VrDisabledFields)
                om.mask[(uint)(int)f] = true;
            hd.renderingPathCustomFrameSettingsOverrideMask = om;

            // Step 2: verify at least the first bit stuck (guards against IL2CPP struct copy issues).
            bool maskOk = hd.renderingPathCustomFrameSettingsOverrideMask
                            .mask[(uint)(int)FrameSettingsField.SSAO];

            if (maskOk)
            {
                // Step 3: disable the passes in the custom FrameSettings.
                // renderingPathCustomFrameSettings is a ref-returning property in HDRP 13;
                // Il2CppInterop exposes this as a value-returning getter, so we must
                // get a copy, modify it, and write it back.
                var fs = hd.renderingPathCustomFrameSettings;
                foreach (var f in s_VrDisabledFields)
                    fs.SetEnabled(f, false);
                hd.renderingPathCustomFrameSettings = fs;

                // Step 4: enable custom frame settings only now that both mask and values are set.
                hd.customRenderingSettings = true;
                Log.LogInfo($"[VRCamera] HDRP: AA=None, customFS=true, {s_VrDisabledFields.Length} passes off on {cam.gameObject.name}");

                // Readback to verify the IL2CPP struct write actually persisted.
                // If ExposureOff/TonemapOff print False here, the setter is a no-op and
                // we must rely on the Volume override instead.
                try
                {
                    var fsRb = hd.renderingPathCustomFrameSettings;
                    bool expOff  = !fsRb.IsEnabled(FrameSettingsField.ExposureControl);
                    bool tmapOff = !fsRb.IsEnabled(FrameSettingsField.Tonemapping);
                    Log.LogInfo($"[VRCamera] HDRP FS readback on {cam.gameObject.name}: ExposureOff={expOff} TonemapOff={tmapOff}");
                }
                catch (Exception rbEx)
                {
                    Log.LogWarning($"[VRCamera] HDRP FS readback failed: {rbEx.Message}");
                }
            }
            else
            {
                Log.LogWarning($"[VRCamera] HDRP: override mask did not persist (IL2CPP struct copy) — skipping customFS on {cam.gameObject.name}");
                Log.LogInfo($"[VRCamera] HDRP: AA=None, no history on {cam.gameObject.name}");
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] HDAdditionalCameraData setup failed: {ex.Message}");
        }
    }

    // ── LateUpdate: render → copy → end frame ────────────────────────────────

    private void LateUpdate()
    {
        if (!_stereoReady || !_frameOpen) return;
        _frameOpen = false;

        // Reposition all managed canvases in front of the current head pose.
        PositionCanvases();

        try
        {
            if (!_posesValid)
            {
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            // Render both eyes into their RenderTextures.
            // Throttled by RenderEveryNFrames (1 = every frame, 2 = every other, etc.).
            // The D3D11 copy still runs every frame, so ATW can correct head motion between renders.
            // Unity D3D11 stores RenderTextures Y-flipped; invertCulling compensates
            // for the negated projection Y row (see SetProjection).
            if ((_frameCount % RenderEveryNFrames) == 0)
            {
                // Flush all pending canvas dirty flags before rendering so the eye cameras
                // see the current frame's canvas materials (not last frame's state).
                // Without this, canvas rebuilds triggered by our material assignments fire
                // AFTER LateUpdate via Canvas.willRenderCanvases, meaning eye cameras always
                // see stale (default/dark) materials.
                Canvas.ForceUpdateCanvases();

                GL.invertCulling = true;
                _leftCam.Render();    // scene (all layers except UI)
                _leftUICam.Render();  // UI overlay (layer 5 only, EV=0/no tonemapping)
                _rightCam.Render();
                _rightUICam.Render();
                GL.invertCulling = false;
            }

            // Acquire swapchain images, copy, release, then submit.
            bool leftOk  = CopyEye(true,  out uint leftIdx);
            bool rightOk = CopyEye(false, out uint rightIdx);

            if (!leftOk || !rightOk)
            {
                Log.LogWarning("[VRCamera] Swapchain copy failed — empty frame.");
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            OpenXRManager.FrameEndStereo(_displayTime, _leftEye, _rightEye, leftIdx, rightIdx);

            _frameCount++;
            if (_frameCount <= 10 || (_frameCount % 60) == 0)
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
        ulong sc     = left ? OpenXRManager.LeftSwapchain         : OpenXRManager.RightSwapchain;
        var   images = left ? OpenXRManager.LeftSwapchainImages    : OpenXRManager.RightSwapchainImages;
        var   rt     = left ? _leftRT : _rightRT;

        if (!OpenXRManager.AcquireSwapchainImage(sc, out imageIndex, out int acquireRc))
        {
            if (_frameCount < 5) Log.LogWarning($"[VRCamera] {eye} AcquireSwapchainImage rc={acquireRc}");
            return false;
        }
        if (!OpenXRManager.WaitSwapchainImage(sc))
        {
            if (_frameCount < 5) Log.LogWarning($"[VRCamera] {eye} WaitSwapchainImage failed");
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

    // ── UI canvas management ──────────────────────────────────────────────────

    /// <summary>
    /// Finds all root Screen Space canvases that haven't been converted yet and
    /// changes them to WorldSpace so the eye cameras can see them.
    /// Called every UICanvasScanRate frames (pre- and post-stereo).
    /// </summary>
    private void ScanAndConvertCanvases()
    {
        // Prune entries whose GameObjects have been destroyed.
        var dead = new List<int>();
        foreach (var kvp in _managedCanvases)
            if (kvp.Value == null) dead.Add(kvp.Key);
        foreach (var k in dead) { _managedCanvases.Remove(k); _positionedCanvases.Remove(k); }

        Canvas[] all;
        try
        {
            // Resources.FindObjectsOfTypeAll is more reliable than FindObjectsOfType in
            // IL2CPP for types outside UnityEngine.CoreModule (like UnityEngine.UI.Canvas).
            all = Resources.FindObjectsOfTypeAll<Canvas>();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] Canvas scan threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Diagnostic: log scan result counts so we can tell if the call is working.
        if (_frameCount <= 60 || (_frameCount % 300) == 0)
            Log.LogInfo($"[VRCamera] Canvas scan: found {all.Length} canvas(es), managed={_managedCanvases.Count}");

        foreach (var canvas in all)
        {
            if (canvas == null) continue;

            if (!canvas.isRootCanvas) continue;                     // skip nested canvases
            if (canvas.renderMode == RenderMode.WorldSpace) continue; // already converted
            int id = canvas.GetInstanceID();
            if (_managedCanvases.ContainsKey(id)) continue;         // already converted

            ConvertCanvasToWorldSpace(canvas);
            _managedCanvases[id] = canvas;
        }

        // Rescan already-managed root canvases for newly added or activated child graphics.
        // Secondary menus often activate child panels *after* the root canvas was converted,
        // so their graphics would never have been material-boosted or alpha-fixed otherwise.
        // RescanCanvasAlpha is a lightweight pass: material cache hits are free, and vertex
        // alpha is only set for graphics not yet in s_vertexAlphaFixed.
        foreach (var kvp in _managedCanvases)
        {
            if (kvp.Value != null) RescanCanvasAlpha(kvp.Value);
        }

        // Also look for nested Canvas components inside managed root canvases.
        // Some games place secondary-menu panels as child canvases (not root), which the
        // main scan skips.  We convert their graphics but leave renderMode as-is (they
        // inherit WorldSpace from the root).
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
                if (nid == root.GetInstanceID()) continue; // skip self
                if (_managedCanvases.ContainsKey(nid)) continue; // already tracked

                // Patch graphics in this nested canvas.
                int patched = ForceUIZTestAlways(nc, logQueueMap: true);
                _managedCanvases[nid] = nc;
                Log.LogInfo($"[VRCamera] NestedCanvas '{nc.gameObject.name}' " +
                            $"in '{root.gameObject.name}' patched={patched}");
            }
        }

        // Lazily assign worldCamera once the stereo rig is ready (canvases may be
        // converted before _leftCam exists, so we back-fill here each scan cycle).
        if (_leftCam != null)
        {
            foreach (var c in _managedCanvases.Values)
                if (c != null && c.worldCamera == null) c.worldCamera = _leftCam;
        }
    }

    /// <summary>
    /// Lightweight per-scan-cycle pass over an already-managed canvas that patches any
    /// graphics not yet processed (newly added or late-activated children).
    ///
    /// "Already processed" is detected by reading <c>unity_GUIZTestMode</c> from the
    /// graphic's current material: if it equals 8 (Always), we set it — skip.  This is
    /// more reliable than <c>GetInstanceID()</c> in IL2CPP, where the managed wrapper
    /// objects returned by <c>GetComponentsInChildren</c> are re-created each call and
    /// may have different C# identity even for the same native object.
    ///
    /// New background elements → renderQueue 3000 (drawn first, always behind).
    /// New foreground elements → renderQueue 3009 (drawn last, always in front).
    /// This prevents re-scan materials from colliding with the initial 0-9 tier spread
    /// and avoids backgrounds burying text via HDRP distance sort.
    /// </summary>
    // Native IL2CPP object pointers of Graphics we have already patched.
    // g.Pointer is the raw address of the native C++ component — stable for the object's
    // lifetime and immune to C# wrapper recreation (unlike GetInstanceID() or material.name).
    private static readonly HashSet<IntPtr> s_patchedGraphicPtrs = new();
    // Maps native Graphic pointer → the VRPatch material we assigned.
    // Used by RescanCanvasAlpha to restore the material if Unity resets it on SetActive.
    private static readonly Dictionary<IntPtr, Material> s_patchedMats = new();

    /// <summary>
    /// Returns the number of Transform steps from <paramref name="t"/> up to (but not including)
    /// <paramref name="root"/>, clamped to [0, 9].  Used to assign renderQueue tiers so that
    /// parent elements always get a lower queue than their children — guaranteeing correct
    /// painter order regardless of where elements appear in the flat GetComponentsInChildren list.
    /// </summary>
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

    /// <summary>Returns true if the Graphic is a text-rendering component (legacy Text or TMP).</summary>
    private static bool IsTextGraphic(Graphic g)
    {
        try
        {
            // Legacy UnityEngine.UI.Text — TryCast is reliable for managed interop types.
            if (g.TryCast<UnityEngine.UI.Text>() != null) return true;
            // TextMeshPro family — may not be in interop assemblies; use type name check.
            string tn = g.GetIl2CppType()?.Name ?? "";
            return tn.IndexOf("TextMeshPro", StringComparison.OrdinalIgnoreCase) >= 0
                || tn == "TMP_Text" || tn == "TMP_SubMeshUI";
        }
        catch { return false; }
    }

    private void RescanCanvasAlpha(Canvas canvas)
    {
        string canvasName = canvas.gameObject.name;
        try
        {
            // ── Mask / RectMask2D disable ─────────────────────────────────────────
            // Both Mask (stencil) and RectMask2D (software scissor) break in WorldSpace:
            // stencil conflicts with HDRP; RectMask2D uses stale screen-space rects that
            // clip ScrollRect content to nothing.  Disable both.
            try
            {
                var masks = canvas.GetComponentsInChildren<UnityEngine.UI.Mask>(true);
                foreach (var m in masks)
                {
                    if (m == null || !m.enabled) continue;
                    m.enabled = false;
                    Log.LogInfo($"[VRCamera] MaskDisabled(rescan) '{m.gameObject.name}' on '{canvasName}'");
                }
            }
            catch { }
            try
            {
                var rm2ds = canvas.GetComponentsInChildren<UnityEngine.UI.RectMask2D>(true);
                foreach (var rm in rm2ds)
                {
                    if (rm == null) continue;
                    var rmPtr = rm.Pointer;
                    if (!rm.enabled && s_disabledRectMasks.Contains(rmPtr)) continue;
                    if (rm.enabled) rm.enabled = false;
                    if (s_disabledRectMasks.Add(rmPtr))
                        Log.LogInfo($"[VRCamera] RectMask2DDisabled '{rm.gameObject.name}' on '{canvasName}'");
                }
            }
            catch { }

            // ── Force all canvas children onto UILayer ────────────────────────────────
            // Catches dynamically-created option items that spawn on Layer 0 after the
            // initial ForceUIZTestAlways pass.
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
                    Log.LogInfo($"[VRCamera] LayerFix(rescan) '{canvasName}': {layerFixed} object(s) → layer {UILayer}");
            }
            catch { }

            // ── CanvasGroup alpha fix ─────────────────────────────────────────────────
            // A CanvasGroup with alpha<1 makes ALL children invisible regardless of
            // individual Graphic.color or material settings.  Force any such groups to 1.
            try
            {
                var groups = canvas.GetComponentsInChildren<CanvasGroup>(true);
                foreach (var cg in groups)
                {
                    if (cg == null) continue;
                    if (cg.alpha < 0.99f)
                    {
                        Log.LogInfo($"[VRCamera] CanvasGroupFix '{cg.gameObject.name}' on '{canvasName}': {cg.alpha:F2}→1");
                        cg.alpha = 1f;
                    }
                }
            }
            catch { /* safe to ignore — CanvasGroup fix is best-effort */ }

            // ── Graphic material + vertex-alpha patch ─────────────────────────────────
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            int newCount  = 0;
            int diagCount = 0; // limit per-element diagnostic lines per call

            foreach (var g in graphics)
            {
                if (g == null) continue;

                string nm    = g.gameObject.name;
                bool   isBg  = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
                bool   isFade = nm.IndexOf("fade",       StringComparison.OrdinalIgnoreCase) >= 0;
                bool   isText = IsTextGraphic(g); // computed early so the already-patched path can use it

                // Register fade elements so PositionCanvases can zero their alpha each frame.
                if (isFade)
                {
                    int fid = g.GetInstanceID();
                    if (!_managedFades.ContainsKey(fid))
                    {
                        _managedFades[fid] = g;
                        Log.LogInfo($"[VRCamera] FadeSuppress(rescan) '{nm}' on '{canvasName}'");
                    }
                }

                // Use the raw native pointer as the "already patched" key.
                // g.Pointer (Il2CppObjectBase.Pointer) is the address of the native C++ object —
                // it never changes while the object is alive, unlike GetInstanceID() or material.name
                // which are unreliable through IL2CppInterop wrapper churn.
                IntPtr ptr = g.Pointer;
                if (s_patchedGraphicPtrs.Contains(ptr))
                {
                    // Already patched — restore material if Unity reset it (e.g. on SetActive),
                    // then re-check vertex colour.
                    if (s_patchedMats.TryGetValue(ptr, out var savedMat))
                    {
                        try
                        {
                            var cur = g.material;
                            // Accept our own VRCursorMat as well as VRPatch_ prefixes.
                            bool matOk = cur != null &&
                                         (cur.name.StartsWith("VRPatch_", StringComparison.Ordinal) ||
                                          cur.name.StartsWith("VRCursor",  StringComparison.Ordinal));
                            if (!matOk)
                            {
                                g.material = savedMat;
                                if (diagCount < 5)
                                {
                                    Log.LogInfo($"[VRCamera] MatRestored '{canvasName}' '{nm}' was='{cur?.name}'");
                                    diagCount++;
                                }
                            }
                        }
                        catch { }
                    }
                    if (!isBg && !isText)
                    {
                        // Preserve original vertex colour — forcing white destroys contrast when
                        // item-row backgrounds use a solid-colour Image (white text on white = invisible).
                        // Only force alpha=1 so fade-animations don't hide graphics.
                        try
                        {
                            var vc = g.color;
                            if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                        }
                        catch { }
                    }
                    else if (!isBg) // isText
                    {
                        // Force white vertex colour via TMP_Text.color (not Graphic.color).
                        // TMP hides Graphic.color with "new color" — setting via Graphic only
                        // changes m_Color which TMP ignores; TMP uses m_fontColor instead.
                        try
                        {
                            var tmp = g.TryCast<TMP_Text>();
                            if (tmp != null)
                            {
                                var tc = tmp.color;
                                if (tc.r < 0.99f || tc.g < 0.99f || tc.b < 0.99f || tc.a < 0.99f)
                                    tmp.color = Color.white;
                            }
                        }
                        catch { }
                        // TMP's Distance Field shader does not render visibly in this HDRP setup.
                        // Swap it in-place to UI/Default (which does work): change the shader on
                        // the font material object directly.  TMP rebuilds keep calling
                        // cr.SetMaterial(sameMat, 0) — the same material, now with UI/Default shader.
                        // No cr.SetMaterial call from our side → no rebuild loop.
                        // Tracked by material instance ID so the swap only fires once per font asset.
                        try
                        {
                            var crm = g.canvasRenderer.GetMaterial(0);
                            if (crm != null && !s_shaderSwappedMats.Contains(crm.GetInstanceID()))
                            {
                                var sn = crm.shader?.name ?? "";
                                if (sn.IndexOf("Distance Field", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var uiSh = Shader.Find("UI/Default");
                                    if (uiSh != null)
                                    {
                                        crm.shader = uiSh;
                                        crm.SetInt("unity_GUIZTestMode", 8); // ZTest Always in WorldSpace
                                        crm.SetVector("_TextureSampleAdd", new Vector4(1f, 1f, 1f, 0f)); // Alpha8 atlas → white text
                                        s_shaderSwappedMats.Add(crm.GetInstanceID());
                                        Log.LogInfo($"[VRCamera] TMP-Swap '{crm.name}' → UI/Default");
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    continue;
                }

                // ── Not in pointer set — read material to decide what to do ───────────
                Material orig;
                try   { orig = g.material; }
                catch { continue; }
                if (orig == null) continue;

                // If the material already has our VRPatch_ prefix it was stamped by
                // ForceUIZTestAlways before s_patchedGraphicPtrs was populated for it.
                // Adopt it into the pointer set and skip re-patching (avoids 30×30=900× boost).
                if (orig.name?.StartsWith("VRPatch_") == true)
                {
                    s_patchedGraphicPtrs.Add(ptr);
                    s_patchedMats[ptr] = orig; // record the VRPatch material
                    if (!isBg)
                    {
                        try
                        {
                            var vc = g.color;
                            if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                        }
                        catch { }
                    }
                    continue;
                }

                string shaderName = orig.shader?.name ?? "";
                bool isAdditive = shaderName.IndexOf("Additive",  StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Particle",  StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Add",       StringComparison.OrdinalIgnoreCase) >= 0;

                // isText already computed above (before the pointer early-continue).

                // Diagnostic: log first 3 new elements per scan (reduced from 10 to cut noise).
                if (diagCount < 3)
                {
                    Log.LogInfo($"[VRCamera] RescanNew '{canvasName}' '{nm}' origName='{orig.name}' " +
                                $"shader='{shaderName}' isText={isText} gAlpha={g.color.a:F2}");
                    diagCount++;
                }

                // Key by (origId, boostType) — same as ForceUIZTestAlways — so each
                // element gets a per-source-material colour boost and fixed renderQueue.
                // Painter order: bg(3000) < image(3008) < text(3009).
                int  boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                int  origId    = orig.GetInstanceID();
                int  queue     = isBg ? 3000 : (isText ? 3009 : (isAdditive ? 3001 : 3008));
                long matKey    = ((long)origId << 2) | (long)boostType;

                if (!s_uiZTestMats.TryGetValue(matKey, out var mat))
                {
                    mat = new Material(orig);
                    mat.name = "VRPatch_" + orig.name;
                    mat.SetInt("unity_GUIZTestMode", 8);
                    try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                    try { if (mat.HasProperty("_ZTest"))     mat.SetInt("_ZTest",     8); } catch { }

                    if (!isAdditive)
                    {
                        mat.renderQueue = queue;
                        Color c = mat.color;
                        if (isBg)
                            mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                        else if (isText)
                        {
                            // Do NOT override mat.color or _FaceColor.
                            // Game artists chose text colours with contrast against their backgrounds in mind.
                            // Forcing white makes dark-on-light text invisible (options menus, settings panels).
                            // With ExposureControl+Tonemapping disabled on UI cameras, original colours
                            // map directly to linear LDR output — no boost needed.
                            // Only disable _ExposureWeight so HDRP doesn't dim text via exposure.
                            try
                            {
                                if (mat.HasProperty("_ExposureWeight"))
                                    mat.SetFloat("_ExposureWeight", 0f);
                            }
                            catch { }
                        }
                        else
                            mat.color = new Color(UIImageBoost, UIImageBoost, UIImageBoost, c.a);
                    }

                    s_uiZTestMats[matKey] = mat;
                }

                try { g.material = mat; } catch { continue; }

                // Mark as patched using the native pointer — immune to wrapper recreation.
                s_patchedGraphicPtrs.Add(ptr);
                s_patchedMats[ptr] = mat;
                newCount++;

                // Z-nudge (mirrors ForceUIZTestAlways three-tier depth ordering).
                if (isBg)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        if (lp.z < 0.004f) rt.localPosition = new Vector3(lp.x, lp.y, 0.005f);
                    }
                    catch { }
                }
                else if (isText)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        if (lp.z > -0.004f) rt.localPosition = new Vector3(lp.x, lp.y, -0.005f);
                    }
                    catch { }
                }

                // Vertex colour: for text, force white via TMP_Text.color (TMP hides Graphic.color
                // with "new" — Graphic.color sets m_Color which TMP ignores for vertex generation).
                // For non-text elements preserve original RGB and only force alpha=1.
                if (!isBg)
                {
                    try
                    {
                        if (isText)
                        {
                            var tmp = g.TryCast<TMP_Text>();
                            if (tmp != null) tmp.color = Color.white;
                        }
                        else
                        {
                            var vc = g.color;
                            if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                        }
                    }
                    catch { }
                }

                // For TMP Distance Field text: swap shader to UI/Default (same as already-patched path).
                if (isText)
                {
                    try
                    {
                        var crm = g.canvasRenderer.GetMaterial(0);
                        if (crm != null && !s_shaderSwappedMats.Contains(crm.GetInstanceID()))
                        {
                            var sn = crm.shader?.name ?? "";
                            if (sn.IndexOf("Distance Field", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var uiSh = Shader.Find("UI/Default");
                                if (uiSh != null)
                                {
                                    crm.shader = uiSh;
                                    crm.SetInt("unity_GUIZTestMode", 8);
                                    crm.SetVector("_TextureSampleAdd", new Vector4(1f, 1f, 1f, 0f)); // Alpha8 atlas → white text
                                    s_shaderSwappedMats.Add(crm.GetInstanceID());
                                    Log.LogInfo($"[VRCamera] TMP-Swap(new) '{crm.name}' → UI/Default");
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            if (newCount > 0)
                Log.LogInfo($"[VRCamera] RescanAlpha '{canvasName}': {newCount} new graphic(s) patched");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] RescanCanvasAlpha '{canvasName}': " +
                           $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Converts a single canvas to WorldSpace so the VR eye cameras see it as a
    /// floating panel in front of the player.
    /// <para>
    /// HDRP sorts transparent objects back-to-front by spherical distance from the
    /// camera.  In WorldSpace the background Image (at canvas centre) sits exactly
    /// at <c>UIDistance</c>, while off-centre buttons are fractionally further away.
    /// HDRP therefore renders the background LAST and it buries every button under
    /// it.  <see cref="ForceUIZTestAlways"/> fixes this by assigning ascending
    /// <c>renderQueue</c> values (3000 → 3009) based on hierarchy order so that
    /// elements later in the draw order always paint over earlier ones, overriding
    /// the HDRP distance sort.
    /// </para>
    /// </summary>
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

        // Assign ascending render-queue tiers so painter order is respected despite
        // HDRP's spherical-distance sort (see method documentation above).
        int patched = ForceUIZTestAlways(canvas);

        // Ensure a GraphicRaycaster exists so the laser pointer can hit-test this canvas.
        // blockingMask=0 skips physics blockers — we only care about UI elements.
        try
        {
            var gr = canvas.GetComponent<GraphicRaycaster>();
            if (gr == null) gr = canvas.gameObject.AddComponent<GraphicRaycaster>();
            gr.blockingMask = 0;
            if (_leftCam != null) canvas.worldCamera = _leftCam;
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] GraphicRaycaster setup: {ex.Message}"); }

        Log.LogInfo($"[VRCamera] Canvas '{canvas.gameObject.name}' → WorldSpace " +
                    $"({refW}x{refH}, scale={UICanvasScale}, sortOrder={canvas.sortingOrder}, patched={patched})");
    }

    /// <summary>
    /// Walks all Graphic children of <paramref name="canvas"/> in hierarchy order
    /// and replaces each material with a clone that:
    /// <list type="bullet">
    ///   <item><description><c>unity_GUIZTestMode = 8</c> (Always) — never occluded by scene geometry.</description></item>
    ///   <item><description><c>renderQueue = 3000 + tier</c> — explicit painter ordering by hierarchy depth.</description></item>
    ///   <item><description>Graphics named "background*" → alpha reduced to <c>UIBackgroundAlpha</c> (semi-transparent
    ///   backdrop so interactive elements remain visible even if HDRP sorts them behind).</description></item>
    ///   <item><description>All other graphics → <c>_Color</c> multiplied by <c>UIColorBoost</c> to compensate
    ///   for HDRP tonemapping compressing UI values in the headset.</description></item>
    /// </list>
    /// </summary>
    /// <returns>Number of graphics patched.</returns>
    private int ForceUIZTestAlways(Canvas canvas, bool logQueueMap = true)
    {
        // ── Disable ALL mask/clip components ──────────────────────────────────────
        // Unity's Mask component clips children via stencil buffer writes.
        // HDRP uses the stencil buffer internally; UI stencil masks conflict with it
        // in WorldSpace, causing masked children to be completely invisible.
        // RectMask2D uses screen-space scissor rect clipping which produces wrong/zero
        // rect coordinates in WorldSpace — text inside ScrollRect viewports becomes
        // completely invisible.  Disable both mask types so nothing gets clipped.
        try
        {
            var masks = canvas.GetComponentsInChildren<UnityEngine.UI.Mask>(true);
            foreach (var m in masks)
            {
                if (m == null || !m.enabled) continue;
                m.enabled = false;
                Log.LogInfo($"[VRCamera] MaskDisabled '{m.gameObject.name}' on '{canvas.gameObject.name}'");
            }
        }
        catch { /* best-effort */ }
        try
        {
            var rm2ds = canvas.GetComponentsInChildren<UnityEngine.UI.RectMask2D>(true);
            foreach (var rm in rm2ds)
            {
                if (rm == null || !rm.enabled) continue;
                rm.enabled = false;
                Log.LogInfo($"[VRCamera] RectMask2DDisabled '{rm.gameObject.name}' on '{canvas.gameObject.name}'");
            }
        }
        catch { /* best-effort */ }

        // ── Force all canvas children onto UILayer so the UI overlay camera sees them ─
        // Dynamically-created option/settings items spawn on Layer 0 (Default) and are
        // therefore invisible to the UI overlay camera whose cullingMask = 1 << UILayer.
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
                Log.LogInfo($"[VRCamera] LayerFix '{canvas.gameObject.name}': {layerFixed} object(s) → layer {UILayer}");
        }
        catch { /* best-effort */ }

        int count = 0;
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);

            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null) continue;
                Material orig;
                try   { orig = g.material; }
                catch { continue; }
                if (orig == null) continue;

                // Classify each element: additive shader, background, text, or generic image.
                string nm         = g.gameObject.name;
                string shaderName = orig.shader?.name ?? "";
                bool isBg       = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isAdditive = shaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Add",      StringComparison.OrdinalIgnoreCase) >= 0;
                bool isText     = !isBg && !isAdditive && IsTextGraphic(g);

                // Key by (origId, boostType): per-element material gives the correct
                // per-element _Color boost.  No tier in the key — queue is one of three
                // fixed values so painter order is: bg(3000) < image(3008) < text(3009)
                // regardless of hierarchy position, ensuring text is never occluded.
                // boostType: 0=image, 1=text, 2=bg, 3=additive
                int  boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                int  origId    = orig.GetInstanceID();
                int  queue     = isBg ? 3000 : (isText ? 3009 : (isAdditive ? 3001 : 3008));
                long matKey    = ((long)origId << 2) | (long)boostType;

                if (!s_uiZTestMats.TryGetValue(matKey, out var mat))
                {
                    mat = new Material(orig);
                    mat.name = "VRPatch_" + orig.name;
                    // unity_GUIZTestMode=8 → CompareFunction.Always for UI/Default shader.
                    // TMP shaders use _ZTestMode instead — set both so all shader families pass.
                    mat.SetInt("unity_GUIZTestMode", 8);
                    try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                    try { if (mat.HasProperty("_ZTest"))     mat.SetInt("_ZTest",     8); } catch { }

                    if (!isAdditive)
                    {
                        mat.renderQueue = queue;
                        Color c = mat.color;
                        if (isBg)
                            mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                        else if (isText)
                        {
                            // Do NOT override mat.color or _FaceColor — preserve game-authored colours.
                            // Settings/options panels often use dark text on light backgrounds;
                            // forcing white makes that text invisible.
                            try
                            {
                                if (mat.HasProperty("_ExposureWeight"))
                                    mat.SetFloat("_ExposureWeight", 0f);
                            }
                            catch { }
                        }
                        else
                            mat.color = new Color(UIImageBoost, UIImageBoost, UIImageBoost, c.a);
                    }
                    // additive: keep original renderQueue and colours; only ZTest was set above.

                    s_uiZTestMats[matKey] = mat;
                }

                try { g.material = mat; }
                catch { continue; }

                // ── Z-nudge: three-tier depth ordering ──────────────────────────────────
                // HDRP sorts transparent objects back-to-front by camera distance.
                // We exploit this to guarantee painter order without relying on renderQueue:
                //   bg    → z = +0.005 (furthest from camera → drawn first  → always behind)
                //   image → z =  0     (middle distance      → drawn second)
                //   text  → z = -0.005 (closest to camera    → drawn last   → always in front)
                //
                // TextMeshProUGUI bypasses Graphic.material and calls CanvasRenderer.SetMaterial()
                // directly on every canvas rebuild, so we cannot patch its renderQueue reliably.
                // The z-nudge is set on the Transform (a one-time persistent change) and is
                // therefore immune to TMP's internal material management.
                if (isBg)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        if (lp.z < 0.004f)
                            rt.localPosition = new Vector3(lp.x, lp.y, 0.005f);
                    }
                    catch { }
                }
                else if (isText)
                {
                    try
                    {
                        var rt = g.rectTransform;
                        var lp = rt.localPosition;
                        if (lp.z > -0.004f)
                            rt.localPosition = new Vector3(lp.x, lp.y, -0.005f);
                    }
                    catch { }
                }

                // Mark this graphic as patched so RescanCanvasAlpha won't apply a second
                // 30× boost on top of this one.  g.Pointer is the native C++ object address —
                // stable for the object's lifetime, unlike GetInstanceID() or material.name.
                s_patchedGraphicPtrs.Add(g.Pointer);
                s_patchedMats[g.Pointer] = mat; // store so RescanCanvasAlpha can restore if reset

                // Vertex colour: preserve original RGB for all non-background elements;
                // only force alpha=1 so fade-animations don't hide graphics or text.
                // (Forcing white destroyed contrast: solid-colour item rows became white,
                // making white text invisible against them.)
                if (!isBg)
                {
                    try
                    {
                        var vc = g.color;
                        if (vc.a < 1f) g.color = new Color(vc.r, vc.g, vc.b, 1f);
                    }
                    catch { }
                }
                count++;
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] ForceUIZTestAlways '{canvas.gameObject.name}': " +
                           $"{ex.GetType().Name}: {ex.Message}");
        }

        // Register any full-screen fade overlay elements so PositionCanvases can zero
        // their vertex alpha every frame.  We identify them by name ("fade" / "overlay")
        // OR by being the very last sibling in a large canvas (≥100 graphics).  These
        // elements are intentionally opaque during loading but invisible during play;
        // because our WorldSpace conversion freezes their material, the game's tween
        // may not reach alpha=0 before the user looks at the menu in VR.
        try
        {
            var allG = canvas.GetComponentsInChildren<Graphic>(true);
            foreach (var g in allG)
            {
                if (g == null) continue;
                string nm = g.gameObject.name;
                // Only suppress elements explicitly named with "fade" — not "overlay" generics
                // like HighlightedTextOverlay or Active Overlay which are button state visuals.
                bool isFade = nm.IndexOf("fade", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isFade)
                {
                    int gid = g.GetInstanceID();
                    if (!_managedFades.ContainsKey(gid))
                    {
                        _managedFades[gid] = g;
                        Log.LogInfo($"[VRCamera] FadeSuppress '{nm}' on '{canvas.gameObject.name}' " +
                                    $"(cur a={g.color.a:F2})");
                    }
                }
            }
        }
        catch { /* diagnostic only */ }

        // Diagnostic: log names and assigned queues for first-5 and last-5 elements.
        // Suppressed on rescans (logQueueMap=false) or if already logged for this canvas.
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
            catch { /* diagnostic only */ }
        }

        return count;
    }

    /// <summary>
    /// Repositions every managed WorldSpace canvas in front of the current head pose.
    /// Uses yaw-only rotation so the panel stays upright regardless of head pitch/roll.
    /// Called from <see cref="LateUpdate"/> after <c>ApplyCameraPose</c> has run.
    /// </summary>
    /// <summary>
    /// Called every LateUpdate.  Two responsibilities:
    /// <list type="number">
    ///   <item><description>
    ///     <b>Fade suppression (every frame)</b> — zero vertex-alpha of any registered
    ///     "fade overlay" Graphic so the game's loading fade never permanently buries buttons.
    ///   </description></item>
    ///   <item><description>
    ///     <b>One-shot placement</b> — the first time each canvas is seen (once
    ///     <c>_leftCam</c> has a valid world pose), position it <c>UIDistance</c> metres
    ///     in front of the head and <em>leave it there</em>.  The player can look around
    ///     freely; the canvas does not track the head after placement.
    ///   </description></item>
    /// </list>
    /// </summary>
    private void PositionCanvases()
    {
        // ── Every-few-frames: zero fade overlays ─────────────────────────────
        // Suppresses game fade-to-black graphics that would bury the menu in VR.
        // Rate-limited to every 4 frames so we don't mark the Canvas dirty every frame
        // (SetVertexDirty() → full mesh rebuild is expensive at 60 Hz).
        // 4 frames ≈ 67 ms at 60 fps — brief enough that any flicker is imperceptible.
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

        // ── One-shot: place new canvases in front of current head pose ────────
        // Requires a valid tracked head pose; deferred until the first LocateViews succeeds
        // so we don't place canvases at world-origin (0,0,0) before tracking starts.
        if (_leftCam == null || !_posesValid) return;

        bool anyUnplaced = false;
        foreach (var kvp in _managedCanvases)
            if (!_positionedCanvases.Contains(kvp.Key)) { anyUnplaced = true; break; }
        if (!anyUnplaced) return;

        // Compute head pose once for the whole batch.
        Vector3    headPos = _leftCam.transform.position;
        float      headYaw = _leftCam.transform.eulerAngles.y;
        Quaternion yawOnly = Quaternion.Euler(0f, headYaw, 0f);
        Vector3    forward = yawOnly * Vector3.forward;

        foreach (var kvp in _managedCanvases)
        {
            if (_positionedCanvases.Contains(kvp.Key)) continue;

            var canvas = kvp.Value;

            // The cursor canvas must follow the head every frame so it stays in view
            // even after VROrigin moves when the character spawns.  All other canvases
            // are one-shot placed and never moved again.
            // Check by name (not reference) because _cursorCanvas may not be assigned yet
            // on the very first frame the scan adds it to _managedCanvases.
            bool isCursorCanvas = (canvas != null &&
                                   canvas.gameObject.name == "VRCursorCanvasInternal");
            if (isCursorCanvas && _cursorCanvas == null)
                _cursorCanvas = canvas; // cache early for UpdateControllerPose
            if (!isCursorCanvas)
                _positionedCanvases.Add(kvp.Key); // mark first so null canvas doesn't retry

            if (canvas == null) continue;

            // Higher sortingOrder → slightly closer to the viewer (5 mm per step).
            // Exception: the cursor canvas is placed at exactly the same world depth as
            // the button-image plane (z=0 relative to canvas centre) so there is zero
            // stereo parallax between the dot and the button it overlaps.
            // sortingOrder=100 ensures it renders on top of all game canvases (≤ sortingOrder 10)
            // when HDRP falls back to sort-order for same-distance transparent objects.
            float   zNudge = isCursorCanvas ? 0f : -canvas.sortingOrder * 0.005f;
            Vector3 pos    = headPos
                           + forward  * (UIDistance + zNudge)
                           + Vector3.up * UIVerticalOffset;

            canvas.transform.position = pos;
            canvas.transform.rotation = yawOnly;
            if (!isCursorCanvas)
                Log.LogInfo($"[VRCamera] Placed '{canvas.gameObject.name}' at {pos} yaw={headYaw:F1}°");
        }
    }

    // ── Coordinate conversion ─────────────────────────────────────────────────

    // ── Controller laser pointer ──────────────────────────────────────────────

    /// <summary>
    /// Reads the right controller pose, applies OpenXR→Unity coord flip, updates the
    /// LineRenderer laser beam, and fires a canvas click on trigger press.
    /// </summary>
    private void UpdateControllerPose(long displayTime)
    {
        if (_rightControllerGO == null) return;

        bool poseOk = OpenXRManager.GetControllerPose(true, displayTime,
            out Quaternion ori, out Vector3 pos);

        _poseFrameCount++;
        if (!poseOk)
        {
            if (_cursorRect != null) try { _cursorRect.gameObject.SetActive(false); } catch { }
            // Log every 120 frames until pose becomes valid
            if (!_poseEverValid && _poseFrameCount % 120 == 0)
                Log.LogInfo($"[VRCamera] Controller pose not valid (frame {_poseFrameCount}) — waiting");
            return;
        }

        if (!_poseEverValid)
        {
            _poseEverValid = true;
            Log.LogInfo($"[VRCamera] Controller pose FIRST VALID at frame {_poseFrameCount}: pos={pos} ori={ori}");
            Log.LogInfo($"[VRCamera] First valid pose: cursorRect={_cursorRect != null} cursorCanvas={_cursorCanvas != null} managedCanvases={_managedCanvases.Count}");
        }

        // OpenXR → Unity coord flip: pos.z *= -1; quat = (-x,-y,z,w)
        var uPos = new Vector3( pos.x,  pos.y, -pos.z);
        var uOri = new Quaternion(-ori.x, -ori.y, ori.z, ori.w);

        // Controller is in the same LOCAL reference space as the eye cameras.
        // _cameraOffset is at VROrigin identity, so world = VROrigin.TransformPoint(local).
        _rightControllerGO.transform.position = transform.TransformPoint(uPos);
        _rightControllerGO.transform.rotation = transform.rotation * uOri;

        // ── Cursor dot: ray-cast against managed canvases, place dot at nearest hit ──
        // ── Cursor dot ────────────────────────────────────────────────────────
        // VRCursorCanvasInternal is created as ScreenSpaceOverlay at rig build time.
        // ScanAndConvertCanvases converts it to WorldSpace on its next cycle (~30 frames later).
        // Once converted, it appears in _managedCanvases and we store the reference here.
        if (_cursorCanvas == null)
        {
            foreach (var kvp in _managedCanvases)
            {
                if (kvp.Value != null && kvp.Value.gameObject.name == "VRCursorCanvasInternal")
                {
                    _cursorCanvas = kvp.Value;
                    Log.LogInfo("[VRCamera] CursorCanvas detected — cursor active");
                    break;
                }
            }
        }

        if (_cursorRect != null && _cursorCanvas != null)
        {
            Vector3 ctrlPos = _rightControllerGO.transform.position;
            Vector3 ctrlFwd = _rightControllerGO.transform.forward;
            var ray = new Ray(ctrlPos, ctrlFwd);

            // Project the controller ray onto the cursor canvas plane.
            // The cursor canvas is always repositioned 1.5 m in front of the head each frame,
            // so the ray will always intersect it (unless the controller points exactly
            // parallel to the canvas surface, which is degenerate).
            // We no longer gate visibility on "pointing at a menu canvas" — the pointingAtMenu
            // check was unreliable because canvases may be null/destroyed in _managedCanvases.
            var cursorPlane = new Plane(-_cursorCanvas.transform.forward, _cursorCanvas.transform.position);
            if (cursorPlane.Raycast(ray, out float cd) && cd > 0f)
            {
                Vector3 localHit = _cursorCanvas.transform.InverseTransformPoint(ctrlPos + ctrlFwd * cd);
                _cursorRect.anchoredPosition = new Vector2(localHit.x, localHit.y);
                _cursorRect.gameObject.SetActive(true);

                if (_poseFrameCount <= 5 || (_poseFrameCount % 120) == 0)
                    Log.LogInfo($"[VRCamera] Cursor: dist={cd:F2} px=({localHit.x:F0},{localHit.y:F0})");
            }
            else
            {
                // Ray exactly parallel to canvas plane — hide dot.
                _cursorRect.gameObject.SetActive(false);
                if (_poseFrameCount <= 5 || (_poseFrameCount % 120) == 0)
                    Log.LogInfo($"[VRCamera] Cursor: no plane hit (parallel ray?)");
            }
        }

        // Trigger press edge detection → canvas click
        OpenXRManager.GetTriggerState(true, out bool triggerNow);
        bool triggerDown = triggerNow && !_prevTrigger;
        _prevTrigger = triggerNow;

        if (triggerDown)
            TryClickCanvas(_rightControllerGO.transform.position,
                           _rightControllerGO.transform.forward);
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
            if (canvas == _cursorCanvas) continue; // cursor canvas is not clickable
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

        if (_cursorRect != null)
            try { Destroy(_cursorRect.gameObject); } catch { }

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
