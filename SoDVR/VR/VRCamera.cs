using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.SceneManagement;
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
    private Camera        _leftCam    = null!;   // scene eye camera — renders everything (all layers)
    private Camera        _rightCam   = null!;
    private Camera        _leftUICam  = null!;   // UNUSED — overlay cameras removed (HDRP clearFlags=Depth broken)
    private Camera        _rightUICam = null!;   // UNUSED
    private RenderTexture _leftRT     = null!;
    private RenderTexture _rightRT    = null!;
    private Transform     _gameCam    = null!;   // original game camera transform; we follow its world position
    private Transform     _hudAnchor  = null!;   // body-locked HUD anchor: follows VROrigin pos+yaw only

    // Unity built-in UI layer.  Canvas GameObjects default to this layer.
    private const int UILayer       = 5;
    // Render throttle: call Camera.Render() every N stereo frames.
    // 1 = every frame (full quality). 2 = every other frame (half GPU load, slight judder).
    // The swapchain copy still runs every frame, so head tracking stays smooth via ATW.
    private const int RenderEveryNFrames = 1;

    // ── UI / Canvas constants ─────────────────────────────────────────────────
    private const float UIDistance       = 2.0f;    // metres in front of head (fallback for uncategorised)
    private const float UIVerticalOffset = 0.0f;    // metres up/down from eye level (fallback)
    // Scan rate reduced from 30 → 90 frames: the game can spawn hundreds of
    // map-component canvases; scanning them all at 2 Hz caused freezes.
    private const int   UICanvasScanRate = 90;      // Unity frames between canvas scans
    // Alpha applied to any Graphic whose GameObject name contains "background".
    // Makes the canvas backdrop semi-transparent so buttons and text show through.
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
    // Rate-limit RescanCanvasAlpha: maps canvas instance ID → frame number of last rescan.
    // Prevents rescanning the same canvas every 90 frames (huge canvases like MinimapCanvas
    // create 1300+ materials per rescan cycle, causing D3D device loss crashes).
    private readonly Dictionary<int, int> _lastRescanFrame = new();
    private const int RescanCooldownFrames = 600; // ~10 seconds at 60 fps

    // Tracks which canvas IDs have already been placed in world space.
    // Once in this set the canvas transform is never moved again by us.
    private readonly HashSet<int>         _positionedCanvases = new();
    // Tracks the last-known active state of each managed canvas (by instance ID).
    // When a canvas transitions false→true AND its name is in s_recentreOnActivate,
    // we clear it from _positionedCanvases so it gets repositioned at the current head.
    // HUD canvases (ActionPanelCanvas, CaseCanvas, …) are NOT in the whitelist and are
    // never auto-repositioned, regardless of how long they were inactive.
    private readonly Dictionary<int,bool> _canvasWasActive = new();
    // Nested canvas IDs: these live inside a parent canvas and must not be independently
    // positioned (their transform is driven by the parent canvas hierarchy).
    private readonly HashSet<int>         _nestedCanvasIds = new();
    // CaseCanvas content canvases: Content, Strings, Lines are nested under GameCanvas but
    // must follow CaseCanvas's transform (they hold the actual corkboard elements).
    private readonly HashSet<int>         _caseContentIds = new();
    private Canvas?                       _casePanelCanvas;
    private int                           _casePanelId = -1;

    // ── Canvas category system ────────────────────────────────────────────────
    // Each canvas is assigned a category that controls placement, scale, and interactability.
    // Ignored: transient/world-space canvases — never converted, never in depth scan.
    // HUD: body-locked (VROrigin yaw only), non-interactable, excluded from ray system.
    // CaseBoard: recentres on open, remembers relative layout, grip-relocatable.
    private enum CanvasCategory { HUD, Menu, CaseBoard, Panel, Tooltip, Ignored, Default }

    private readonly struct CanvasCategoryDefaults
    {
        public readonly float Distance;             // metres in front of head
        public readonly float VerticalOffset;       // metres above (+) / below (-) eye level
        public readonly float TargetWorldWidth;     // desired world width in metres; scale = this / sizeDelta.x
        public readonly bool  RecentreOnActivate;   // reposition when canvas becomes active
        public readonly bool  RepositionEveryFrame; // never mark positioned (tooltip behaviour)
        public readonly bool  IsHUD;                // body-locked; excluded from ray/click system
        public readonly bool  IsGripRelocatable;    // CaseBoard grip-drag support
        public CanvasCategoryDefaults(float dist, float vOff, float targetW,
                                      bool recentre, bool everyFrame = false,
                                      bool isHud = false, bool isGrip = false)
        { Distance = dist; VerticalOffset = vOff; TargetWorldWidth = targetW;
          RecentreOnActivate = recentre; RepositionEveryFrame = everyFrame;
          IsHUD = isHud; IsGripRelocatable = isGrip; }
    }

    private static CanvasCategory GetCanvasCategory(string name)
        => s_canvasCategories.TryGetValue(name, out var c) ? c : CanvasCategory.Default;

    private static CanvasCategoryDefaults GetCategoryDefaults(CanvasCategory cat)
        => s_categoryDefaults.TryGetValue(cat, out var d) ? d : s_categoryDefaults[CanvasCategory.Default];

    // Canvas name → category.  Names are matched case-insensitively.
    private static readonly Dictionary<string, CanvasCategory> s_canvasCategories =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // HUD — body-locked (VROrigin yaw), non-interactable, excluded from ray system
        ["hudCanvas"]                 = CanvasCategory.HUD,
        ["GameCanvas"]                = CanvasCategory.HUD,
        ["statusCanvas"]              = CanvasCategory.HUD,
        ["StatusDisplayCanvas"]       = CanvasCategory.HUD,
        ["interactionProgressCanvas"] = CanvasCategory.HUD,
        ["OverlayCanvas"]             = CanvasCategory.HUD,
        ["MinimapCanvas"]             = CanvasCategory.HUD,
        ["GameWorldDisplayCanvas"]    = CanvasCategory.Ignored,  // already WorldSpace, game manages position
        ["CentreDisplayCanvas"]       = CanvasCategory.HUD,
        ["MessageSystemCanvas"]       = CanvasCategory.HUD,

        // Menu — recentres in front of head on activate; always in front of Panel canvases
        ["MenuCanvas"]                = CanvasCategory.Menu,
        ["DialogCanvas"]              = CanvasCategory.Menu,
        ["WindowCanvas"]              = CanvasCategory.Menu,     // detail/notebook windows
        ["PopupMessage"]              = CanvasCategory.Menu,     // exit/confirm dialogs — placed 0.2m closer
        ["controlsCanvas"]            = CanvasCategory.Menu,
        ["upgradesCanvas"]            = CanvasCategory.Menu,
        ["VirtualCursorCanvas & EventSystem"] = CanvasCategory.Ignored, // game virtual cursor, not ours

        // CaseBoard — recentres on open, remembers relative layout, grip-relocatable
        ["CaseCanvas"]                = CanvasCategory.CaseBoard,
        ["caseCanvas"]                = CanvasCategory.CaseBoard,
        ["BioDisplayCanvas"]          = CanvasCategory.CaseBoard,
        ["LocationDetailsCanvas"]     = CanvasCategory.CaseBoard,

        // Panel — recentres on activate, interactable, behind Menu
        ["ActionPanelCanvas"]         = CanvasCategory.Panel,    // action buttons for board elements
        ["contentCanvas"]             = CanvasCategory.Panel,
        ["osCanvas"]                  = CanvasCategory.Panel,
        ["keyboardCanvas"]            = CanvasCategory.Panel,
        ["fingerprintDisplayCanvas"]  = CanvasCategory.Panel,
        ["mapLayerCanvas"]            = CanvasCategory.Panel,
        ["PrototypeBuilderCanvas"]    = CanvasCategory.Panel,
        ["ControlsDisplayCanvas"]     = CanvasCategory.Panel,
        ["UpgradesDisplayCanvas"]     = CanvasCategory.Panel,

        // Tooltip — tracks cursor depth, repositions every frame
        ["TooltipCanvas"]             = CanvasCategory.Tooltip,
        ["tooltipsCanvas"]            = CanvasCategory.Tooltip,
    };

    // Per-category placement defaults.
    // TargetWorldWidth: canvas scale is computed as TargetWorldWidth / sizeDelta.x at runtime,
    // so it's immune to CanvasScaler inflation (sizeDelta may be 2720 or 1280 — doesn't matter).
    // Distance ordering (front to back): Menu (1.8m) → Panel (2.1m) → CaseBoard (2.3m) → HUD (2.5m back)
    private static readonly Dictionary<CanvasCategory, CanvasCategoryDefaults> s_categoryDefaults = new()
    {
        [CanvasCategory.Menu]      = new(1.8f,  0.00f, 1.2f, recentre: true),
        [CanvasCategory.CaseBoard] = new(2.3f,  0.00f, 1.8f, recentre: true,  isGrip: true),
        [CanvasCategory.Panel]     = new(2.1f,  0.00f, 1.6f, recentre: true),
        [CanvasCategory.HUD]       = new(2.5f, -0.15f, 1.5f, recentre: false, isHud: true),
        [CanvasCategory.Tooltip]   = new(1.2f, -0.10f, 0.8f, recentre: false, everyFrame: true),
        [CanvasCategory.Default]   = new(2.0f,  0.00f, 1.6f, recentre: false),
        [CanvasCategory.Ignored]   = new(0f,    0.00f, 1.6f, recentre: false),  // placeholder; never used
    };

    // ── CaseBoard grip-relocate ───────────────────────────────────────────────
    private Canvas?    _gripDragCanvas;       // canvas currently being grip-dragged
    private Vector3    _gripDragOffset;       // world offset from controller to canvas at grab time
    private Quaternion _gripDragRotOffset;    // rotation offset at grab time
    private bool       _gripWasPressed;       // previous frame grip state (edge detection)
    // Relative offsets (position only) from primary CaseCanvas to other CaseBoard canvases.
    // Preserved across opens so the user's layout is maintained.
    private readonly Dictionary<int, Vector3> _caseBoardOffsets = new();
    private int _caseBoardPrimaryId = -1;     // CaseCanvas instance ID (primary anchor)

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

    // ── Controller / cursor dot / laser ─────────────────────────────────────
    private GameObject?   _rightControllerGO;
    private GameObject?   _leftControllerGO;
    private LineRenderer? _laserLine;         // laser pointer beam from right controller
    private LineRenderer? _leftLaserLine;     // laser pointer beam from left controller (interact)

    // ── Scene-change guard ────────────────────────────────────────────────────
    // When Unity loads a new scene (save load, new game) we stop calling
    // ScanAndConvertCanvases for 120 frames.  This gives SaveStateController
    // and other scene-init code a clean window before we start touching canvases
    // and camera components — the source of the "Can't remove Rigidbody" crash.
    private int  _lastSceneHandle;
    private int  _sceneLoadGrace;      // frames remaining in the grace period
    private bool _prevGameCamValid;    // was _gameCam non-null last frame? (same-scene reload detection)

    // ── Game camera deferred disable ─────────────────────────────────────────
    // We delay calling cam.enabled=false by several frames so that scene-load
    // code (SaveStateController:LoadSaveState) has time to finish using the
    // camera before we interfere with it.
    private Camera? _gameCamPending;       // found but not yet disabled
    private Camera? _gameCamRef;           // kept after suppress — used for VR rendering
    private int     _gameCamSavedMask;     // original cullingMask, restored during Render()
    private int     _gameCamDisableDelay;  // frames remaining before disable

    // ── Snap turn ─────────────────────────────────────────────────────────────
    private const float SnapTurnAngle    = 30f;   // degrees per snap
    private const float SnapTurnDeadZone = 0.6f;  // stick threshold to trigger
    private const float SnapTurnRearm    = 0.3f;  // stick must drop below this to re-arm
    private const float SnapTurnCooldown = 0.25f; // seconds between snaps
    private float _snapCooldown;
    private bool  _snapArmed = true;

    // ── Movement discovery + locomotion ──────────────────────────────────────
    private bool              _movementDiscoveryDone;
    private CharacterController? _playerCC;    // FPSController CharacterController (primary locomotion driver)
    private Rigidbody?        _playerRb;       // FPSController Rigidbody (kept for null-check / reset only)
    private Transform?        _fpsControllerTransform; // FPSController — controls player yaw
    private Transform?        _cameraPivotTransform;   // first child above Main Camera for pitch (CamTransitionModifier or CameraLeanPivot)
    private bool              _cameraLookDisabled;     // true after we've disabled the game's mouse-look components
    private FirstPersonItemController? _fpsItemController; // cached for item hand tracking
    private Transform? _lagPivotTransform;                  // LagPivot — reparented to controller
    private Transform? _lagPivotOrigParent;                 // original parent (3DUI) for restore
    private InteractionController? _interactionController;  // cached for carried-object tracking
    private const float MoveDeadZone = 0.15f;
    private const float MoveSpeed       = 4.0f;   // m/s at full deflection
    private const float SprintMultiplier = 1.8f;  // sprint speed = MoveSpeed * this
    // Cursor: ScreenSpaceOverlay canvas "VRCursorCanvasInternal" created at rig-build time.
    // ScanAndConvertCanvases converts it to WorldSpace via the normal pipeline — giving it proper
    // HDRP registration and the ZTest Always material patch from RescanCanvasAlpha.
    // It is NOT in _ownedCanvasIds so all mutation passes (including the ZTest patch) run on it.
    // PositionCanvases repositions it every frame (never added to _positionedCanvases).
    // The dot moves via anchoredPosition (2D) inside this fixed-distance canvas.
    private Canvas?        _cursorCanvas;         // VRCursorCanvasInternal once scan converts it
    private RectTransform? _cursorRect;           // the dot's RectTransform inside _cursorCanvas
    private Vector2        _cursorCanvasHalfSize; // half-size in canvas pixels; cached lazily
    private float          _cursorAimDepth = UIDistance - 0.01f; // head-fwd depth of nearest aimed-at canvas (for tooltips)
    private bool           _cursorHasTarget;      // true when depth scan found a canvas rect hit this frame
    private Vector3        _cursorTargetPos;      // world pos of nearest aimed-at canvas
    private Quaternion     _cursorTargetRot;      // world rot of nearest aimed-at canvas
    private Canvas?        _menuCanvasRef;       // MenuCanvas — hidden while VR settings panel is open
    private bool           _menuCanvasHidden;    // tracks last hide state to avoid per-frame toggles
    private int            _menuSettingsBtnId;   // instanceID of the patched Settings button in MenuCanvas
    private bool           _cursorVisible = false; // tracks SetActive state to avoid per-frame IL2CPP calls
    // ── New controller button state (edge detection) ──────────────────────────
    private bool _jumpBtnPrev;
    private bool _crouchBtnPrev;
    private bool _interactBtnPrev;
    private bool _notebookBtnPrev;
    private bool _flashlightBtnPrev;
    private bool _inventoryBtnPrev;
    private bool _sprintThumbPrev;
    private bool _sprintActive;       // true while Shift key is held down

    private bool        _prevTrigger;
    private bool        _triggerNeedsRelease;  // latch: must fully release before next click fires
    private int         _triggerFireFrame = -100; // frame of last trigger click (frame-gap guard)
    private float       _menuBtnCooldownUntil;   // Time.realtimeSinceStartup when lockout expires (wall-clock, not dt-scaled)
    private bool        _menuBtnNeedsRelease;    // true after fire; cleared only once button is physically released
    private int         _poseFrameCount;
    private bool        _poseEverValid;

    // Win32 keyboard simulation — used to forward left-controller menu button as ESC.
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    // Win32 mouse event simulation — used for flashlight toggle (middle mouse button).
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    // Maps (origMaterialInstanceID << 5 | tier<<1 | isBackground) → patched clone.
    // Lower 5 bits: tier (0-9, 4 bits) + isBackground (1 bit).
    // isBackground selects between semi-transparent (UIBackgroundAlpha) and boosted (UIColorBoost) clones.
    // Static so the table persists across scene loads and we never create duplicates.
    private static readonly Dictionary<long, Material> s_uiZTestMats = new();
    private const int MaxPatchedMaterials = 2000; // hard cap — stop creating materials beyond this
    private static bool s_matCapWarned = false;
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
    private const int UIMaterialVersion = 5;
    private static readonly HashSet<int> s_shaderSwappedMats = new();
    // (HDR boost tracking sets removed — UI overlay camera handles exposure isolation)
    // Tracks material instance IDs that have been stencil-patched by RelaxMenuTextMaterials.
    // Each new material instance (spawned when Unity rebuilds a canvas after a dirty-mark) is
    // patched exactly once.  Without this guard, patching marks the material dirty → canvas
    // rebuilds → new instance → patch again → exponential growth in "materials" count per scan.
    private static readonly HashSet<int> s_stencilNeutralizedMats = new();
    private static readonly HashSet<int> s_menuMaskRelaxedCanvases = new();
    private static readonly HashSet<int> s_menuMaskabilityRelaxed = new();
    // (s_menuTmpReadableMats and s_menuTextFallbacks removed — duplicate text overlay system no longer needed)
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

        // Detect scene changes and apply a grace period during which we skip
        // ScanAndConvertCanvases.  This prevents us from touching canvas/camera
        // components while SaveStateController is reconstructing the physics hierarchy.
        try
        {
            int sh = SceneManager.GetActiveScene().handle;
            if (_lastSceneHandle != 0 && sh != _lastSceneHandle)
            {
                _sceneLoadGrace = 120;  // ~2 s at 60 fps
                _canvasTick     = 0;
                _movementDiscoveryDone = false;
                Log.LogInfo($"[VRCamera] Scene changed (handle {_lastSceneHandle}→{sh}) — canvas scan paused for 120 frames.");
            }
            _lastSceneHandle = sh;
        }
        catch { }

        // Same-scene reload detection: the scene handle doesn't change when the player
        // loads a second save in the same session (game reuses the single Unity scene).
        // However, the game destroys and recreates "Main Camera" each load, so _gameCam
        // transitions from non-null → null.  Treat that edge as an implicit scene change:
        // apply the same 120-frame grace period, and reset all cached per-load state.
        {
            bool gcValid = (_gameCam != null);
            if (_prevGameCamValid && !gcValid)
            {
                _sceneLoadGrace = 120;
                _canvasTick     = 0;
                _movementDiscoveryDone = false;
                _playerRb       = null;
                _playerCC       = null;
                _fpsControllerTransform = null;
                _cameraPivotTransform   = null;
                _cameraLookDisabled     = false;
                // Restore LagPivot to original parent before clearing references
                if (_lagPivotTransform != null && _lagPivotOrigParent != null)
                {
                    try { _lagPivotTransform.SetParent(_lagPivotOrigParent, false); } catch { }
                }
                _fpsItemController      = null;
                _interactionController  = null;
                _lagPivotTransform      = null;
                _lagPivotOrigParent     = null;
                _armsActivated          = false;
                _armsTransform          = null;
                _firstPersonModelsTransform = null;
                _leftArmTransform       = null;
                _rightArmTransform      = null;
                _leftFistTransform      = null;
                _rightFistTransform     = null;
                StopSprint();
                Log.LogInfo("[VRCamera] Game camera lost — same-scene reload detected; canvas scan paused 120 frames, movement state reset.");
            }
            _prevGameCamValid = gcValid;
        }

        // Detect grace-period expiry so we can schedule movement rediscovery.
        // We do NOT set _movementDiscoveryDone=false at click time (that would trigger
        // immediate re-discovery in the same frame, before SaveStateController runs).
        // Instead we wait until the grace period finishes — by then the load is complete
        // and the player hierarchy is fully initialised.
        bool graceWasActive = _sceneLoadGrace > 0;
        if (_sceneLoadGrace > 0) _sceneLoadGrace--;
        if (graceWasActive && _sceneLoadGrace == 0)
        {
            Log.LogInfo($"[VRCamera] Grace expired at frame {_frameCount} — canvas scan will fire next tick.");
            if (_movementDiscoveryDone)
            {
                _movementDiscoveryDone = false;
                Log.LogInfo("[VRCamera] Grace expired — movement rediscovery scheduled.");
            }
        }

        // Scan for new screen-space canvases and convert them to WorldSpace.
        // Skipped during the post-scene-load grace period.
        if (++_canvasTick >= UICanvasScanRate && _sceneLoadGrace == 0)
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

        // Deferred camera suppress: fire after the countdown reaches zero.
        // We keep the camera ENABLED (so Camera.main remains non-null for game systems
        // like SaveStateController:LoadSaveState), but zero out its culling mask so it
        // renders nothing.  Fully disabling the camera makes Camera.main return null,
        // which causes LoadSaveState to crash on the second load with
        // "Can't remove Rigidbody because CharacterJoint depends on it".
        if (_gameCamPending != null && _gameCamDisableDelay > 0)
        {
            _gameCamDisableDelay--;
            if (_gameCamDisableDelay == 0)
            {
                try
                {
                    _gameCamPending.cullingMask = 0;
                    _gameCamPending.depth = -1000f;
                    Log.LogInfo($"[VRCamera] Game camera suppressed (cullingMask=0): '{_gameCamPending.gameObject.name}'");
                }
                catch { }
                _gameCamPending = null;
            }
        }

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

            // Sync game camera hierarchy to VR head direction so game systems
            // (interaction raycast, world HUD positioning) match where the player looks in VR.
            // Set rotations on the FPSController hierarchy, not just the camera:
            //   FPSController → yaw (Y rotation)
            //   Camera pivot  → pitch (X rotation)
            //   Main Camera   → world rotation (fallback / for Camera.main references)
            if (_fpsControllerTransform != null)
            {
                try
                {
                    Vector3 headEuler = _leftCam.transform.eulerAngles;
                    // FPSController handles yaw — this is what the game reads for player facing
                    _fpsControllerTransform.rotation = Quaternion.Euler(0f, headEuler.y, 0f);
                    // Camera pivot handles pitch
                    if (_cameraPivotTransform != null)
                        _cameraPivotTransform.localRotation = Quaternion.Euler(headEuler.x, 0f, 0f);
                    // Zero any remaining intermediate transforms
                    if (_gameCam != null && _gameCam.parent != null && _gameCam.parent != _cameraPivotTransform)
                        _gameCam.parent.localRotation = Quaternion.identity;
                    // Set camera itself for direct Camera.main users
                    if (_gameCamRef != null)
                        _gameCamRef.transform.rotation = _leftCam.transform.rotation;
                }
                catch { }
            }
            else if (_gameCamRef != null)
            {
                // Fallback: just set camera world rotation directly
                try { _gameCamRef.transform.rotation = _leftCam.transform.rotation; }
                catch { }
            }
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
            UpdateMenuButton();
            UpdateJump();
            UpdateInteract();
            UpdateCrouch();
            UpdateSprint();
            UpdateNotebook();
            UpdateFlashlight();
            UpdateInventory();
            UpdateHeldItemTracking();
        }

        // One-shot movement system discovery (runs once after stereo is ready and game cam found).
        // Skip discovery when game camera has cullingMask=0 — that means we're on the main menu,
        // where FPSController exists but there's no ground geometry → gravity would pull us through the floor.
        if (!_movementDiscoveryDone && _gameCam != null && _gameCamRef != null)
        {
            try
            {
                if (_gameCamRef.cullingMask != 0)
                    DiscoverMovementSystem();
            }
            catch { }
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
        s_matCapWarned = false;
        _lastRescanFrame.Clear();

        int w = OpenXRManager.SwapchainWidth;
        int h = OpenXRManager.SwapchainHeight;

        var offsetGO = new GameObject("CameraOffset");
        offsetGO.transform.SetParent(transform, false);
        _cameraOffset = offsetGO.transform;

        // ── HUD anchor — body-locked (follows VROrigin pos+yaw, not head pitch/roll) ──
        // Parented to VROrigin (this.transform), NOT to CameraOffset, so it tracks
        // snap-turn rotation but never tracks head pitch/roll from xrLocateViews.
        var hudAnchorGO = new GameObject("HUDAnchor");
        hudAnchorGO.transform.SetParent(transform, false);
        _hudAnchor = hudAnchorGO.transform;

        // ── Scene cameras — render EVERYTHING including UI layer ────────────────
        // UI overlay cameras were tried but HDRP clearFlags=Depth doesn't composite
        // correctly (overwrites scene with blue) and exposure still applies.
        // UI visibility is handled by HDR boost on text materials instead.
        var leftGO = new GameObject("LeftEye");
        leftGO.transform.SetParent(_cameraOffset, false);
        _leftCam = leftGO.AddComponent<Camera>();
        _leftRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Left" };
        _leftRT.Create();
        SetupEyeCam(_leftCam, _leftRT, isUiOverlay: false);

        var rightGO = new GameObject("RightEye");
        rightGO.transform.SetParent(_cameraOffset, false);
        _rightCam = rightGO.AddComponent<Camera>();
        _rightRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Right" };
        _rightRT.Create();
        SetupEyeCam(_rightCam, _rightRT, isUiOverlay: false);

        // UI overlay cameras removed — HDRP ignores clearFlags=Depth on explicit
        // Camera.Render() calls, causing the scene to be overwritten with blue/black.
        // UI visibility is handled by HDR text boost in StrengthenMenuTextMaterial().
        _leftUICam  = null!;
        _rightUICam = null!;

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

        // Laser pointer beam: a LineRenderer from the right controller forward.
        // Uses Sprites/Default with ZTest=Always so it renders over all 3D geometry.
        try
        {
            var laserGO = new GameObject("VRLaserBeam");
            UnityEngine.Object.DontDestroyOnLoad(laserGO);
            laserGO.transform.SetParent(ctrlGO.transform, false);
            _laserLine = laserGO.AddComponent<LineRenderer>();
            _laserLine.useWorldSpace  = true;
            _laserLine.positionCount  = 2;
            _laserLine.startWidth     = 0.012f;  // 12 mm at hand — thick enough to see
            _laserLine.endWidth       = 0.006f;  // 6 mm at target
            _laserLine.numCapVertices = 4;
            _laserLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _laserLine.receiveShadows = false;
            // HDRP/Unlit renders in the 3D scene without lighting; Sprites/Default is ignored by HDRP.
            var laserShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (laserShader != null)
            {
                var laserMat = new Material(laserShader);
                laserMat.name = "VRLaserMat";
                // UI overlay camera renders on its own layer without HDRP exposure — normal colors work.
                // HDR cyan — compensates for HDRP inherited auto-exposure (EV≈8–12).
                var laserColor = new Color(0f, 4096f, 4096f, 1f);
                laserMat.color = laserColor;
                try { laserMat.SetColor("_UnlitColor", laserColor); } catch { }
                try { laserMat.SetColor("_BaseColor",  laserColor); } catch { }
                laserMat.renderQueue = 5000;
                _laserLine.material = laserMat;
                Log.LogInfo($"[VRCamera] Laser shader: {laserShader.name}");
            }
            else Log.LogWarning("[VRCamera] Laser: no shader found");
            _laserLine.enabled = false; // hidden until controller pose is valid
            Log.LogInfo("[VRCamera] VRLaserBeam created");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] Laser beam creation failed: {ex.Message}"); }

        // Left hand laser pointer — same style, toggled via VR Settings "Left Laser".
        try
        {
            var leftLaserGO = new GameObject("VRLeftLaserBeam");
            UnityEngine.Object.DontDestroyOnLoad(leftLaserGO);
            leftLaserGO.transform.SetParent(leftCtrlGO.transform, false);
            _leftLaserLine = leftLaserGO.AddComponent<LineRenderer>();
            _leftLaserLine.useWorldSpace  = true;
            _leftLaserLine.positionCount  = 2;
            _leftLaserLine.startWidth     = 0.012f;
            _leftLaserLine.endWidth       = 0.006f;
            _leftLaserLine.numCapVertices = 4;
            _leftLaserLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _leftLaserLine.receiveShadows = false;
            var leftLaserShader = Shader.Find("HDRP/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (leftLaserShader != null)
            {
                var leftLaserMat = new Material(leftLaserShader);
                leftLaserMat.name = "VRLeftLaserMat";
                var leftLaserColor = new Color(0f, 4096f, 4096f, 1f); // same HDR cyan
                leftLaserMat.color = leftLaserColor;
                try { leftLaserMat.SetColor("_UnlitColor", leftLaserColor); } catch { }
                try { leftLaserMat.SetColor("_BaseColor",  leftLaserColor); } catch { }
                leftLaserMat.renderQueue = 5000;
                _leftLaserLine.material = leftLaserMat;
            }
            _leftLaserLine.enabled = false;
            Log.LogInfo("[VRCamera] VRLeftLaserBeam created");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] Left laser creation failed: {ex.Message}"); }

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
            _ownedCanvasIds.Add(cc.GetInstanceID()); // protect from RescanCanvasAlpha overwriting our material

            var dotGO = new GameObject("Dot");
            dotGO.layer = UILayer;
            // Set parent BEFORE adding Image so RectTransform is created in the right hierarchy.
            dotGO.transform.SetParent(ccGO.transform, false);
            // AddComponent<Image> creates a RectTransform internally; get it via GetComponent.
            // Do NOT call AddComponent<RectTransform>() on a fresh GO — it may return null in
            // IL2CPP because the GO already has a plain Transform component.
            var img = dotGO.AddComponent<Image>();
            img.raycastTarget = false;
            // HDR white — bright enough to overcome HDRP inherited auto-exposure.
            // Scene camera EV can be 8–12 (multiplier 1/256–1/4096).
            // Setting material._Color to (4096,4096,4096) means after ÷4096 exposure we get (1,1,1) white.
            // HDR magenta — bright enough to overcome HDRP inherited auto-exposure.
            // Scene camera EV≈8–12 → multiplier 1/256–1/4096.  (4096,0,4096) survives EV12.
            img.color = new Color(64f, 0f, 64f, 1f);
            try
            {
                var dotMat = new Material(img.material);
                dotMat.name = "VRCursorDotMat";
                dotMat.SetInt("unity_GUIZTestMode", 8);
                try { if (dotMat.HasProperty("_ZTestMode")) dotMat.SetInt("_ZTestMode", 8); } catch { }
                dotMat.color = new Color(64f, 0f, 64f, 1f);
                dotMat.renderQueue = 5000;
                img.material = dotMat;
            }
            catch (Exception ex) { Log.LogWarning($"[VRCamera] Cursor dot material: {ex.Message}"); }

            _cursorRect = dotGO.GetComponent<RectTransform>();
            if (_cursorRect != null)
            {
                _cursorRect.sizeDelta        = new Vector2(12f, 12f);  // 12 canvas units ≈ 15mm at default scale
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

    // Search active cameras for the game's "Main Camera".
    // Only accepts cameras explicitly named "Main Camera" — avoids disabling
    // scene-load helper cameras or NPC cameras that appear transiently.
    // The disable is deferred by 10 frames so SaveStateController:LoadSaveState
    // has time to complete before the camera is taken offline.
    private void TryFindGameCamera()
    {
        foreach (var cam in Camera.allCameras)
        {
            if (cam == _leftCam || cam == _rightCam) continue;
            if (!cam.gameObject.activeInHierarchy) continue;
            // Only accept the primary game camera; skip setup/NPC cameras.
            if (!cam.gameObject.name.Equals("Main Camera", StringComparison.OrdinalIgnoreCase)) continue;
            _gameCam          = cam.transform;
            _gameCamPending   = cam;
            _gameCamRef       = cam;
            _gameCamSavedMask = cam.cullingMask;
            _gameCamDisableDelay = 10;
            Log.LogInfo($"[VRCamera] Found game camera: '{cam.gameObject.name}' pos={cam.transform.position} (suppress in 10 frames)");

            // ── Diagnostic: dump game camera properties ──
            try
            {
                Log.LogInfo($"[VRCamera] GameCam diag: allowHDR={cam.allowHDR} allowMSAA={cam.allowMSAA}" +
                            $" clearFlags={cam.clearFlags} bgColor={cam.backgroundColor}" +
                            $" cullingMask=0x{cam.cullingMask:X8} near={cam.nearClipPlane} far={cam.farClipPlane}" +
                            $" depth={cam.depth} renderingPath={cam.renderingPath}");
                var gameHD = cam.gameObject.GetComponent<HDAdditionalCameraData>();
                if (gameHD != null)
                {
                    Log.LogInfo($"[VRCamera] GameCam HDRP: customRendering={gameHD.customRenderingSettings}" +
                                $" AA={gameHD.antialiasing} dither={gameHD.dithering}" +
                                $" volumeLayerMask=0x{gameHD.volumeLayerMask.value:X8}" +
                                $" probeLayerMask=0x{gameHD.probeLayerMask.value:X8}" +
                                $" clearColorMode={gameHD.clearColorMode}" +
                                $" bgHDR={gameHD.backgroundColorHDR}" +
                                $" stopNaNs={gameHD.stopNaNs}" +
                                $" invertFaceCulling={gameHD.invertFaceCulling}");
                }
                else
                {
                    Log.LogInfo("[VRCamera] GameCam has NO HDAdditionalCameraData");
                }
            }
            catch (Exception diagEx) { Log.LogWarning($"[VRCamera] Diagnostic failed: {diagEx.Message}"); }

            // ── Copy game camera settings to VR eye cameras ──
            CopyGameCameraSettings(cam, _leftCam);
            CopyGameCameraSettings(cam, _rightCam);
            Log.LogInfo($"[VRCamera] VRCam after copy: clearFlags={_leftCam.clearFlags}" +
                        $" cullingMask=0x{_leftCam.cullingMask:X8}" +
                        $" near={_leftCam.nearClipPlane} far={_leftCam.farClipPlane}" +
                        $" allowHDR={_leftCam.allowHDR}");

            transform.position = _gameCam.position;
            return;
        }
    }

    // Expensive HDRP passes to disable on VR eye cameras.
    private static readonly FrameSettingsField[] s_VrDisabledFields =
    {
        FrameSettingsField.Postprocess,         // master kill — disables exposure + all PP
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

    // UI overlay cameras use a superset of the scene camera's disabled passes — everything off.
    private static readonly FrameSettingsField[] s_UIOverlayDisabledFields =
    {
        FrameSettingsField.Postprocess,
        FrameSettingsField.SSAO,
        FrameSettingsField.SSR,
        FrameSettingsField.Volumetrics,
        FrameSettingsField.MotionVectors,
        FrameSettingsField.MotionBlur,
        FrameSettingsField.DepthOfField,
        FrameSettingsField.ChromaticAberration,
        FrameSettingsField.ContactShadows,
        FrameSettingsField.Tonemapping,
        FrameSettingsField.ExposureControl,
        FrameSettingsField.ColorGrading,
        FrameSettingsField.Bloom,
        FrameSettingsField.FilmGrain,
        FrameSettingsField.Dithering,
        FrameSettingsField.LensDistortion,
        FrameSettingsField.Vignette,
    };

    private void SetupEyeCam(Camera cam, RenderTexture rt, bool isUiOverlay)
    {
        // Minimal setup — only set what's strictly needed for manual rendering.
        cam.targetTexture = rt;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.enabled = false;  // manual render only

        if (isUiOverlay)
        {
            cam.clearFlags = CameraClearFlags.Depth;
            cam.backgroundColor = Color.clear;
        }

        try
        {
            var hd = cam.gameObject.GetComponent<HDAdditionalCameraData>()
                  ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();
            hd.customRenderingSettings = false;
            hd.flipYMode = HDAdditionalCameraData.FlipYMode.ForceFlipY;
            Log.LogInfo($"[VRCamera] HDRP setup: customRS={hd.customRenderingSettings}" +
                        $" flipY={hd.flipYMode}" +
                        $" volumeLayerMask=0x{hd.volumeLayerMask.value:X8}" +
                        $" on {cam.gameObject.name}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] HDAdditionalCameraData setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies essential camera and HDRP settings from the game camera to a VR eye camera.
    /// Called once when the game camera is discovered.  This ensures the VR cameras pick up
    /// the same HDRP Volume stack, culling mask, clip planes, and lighting configuration.
    /// </summary>
    private void CopyGameCameraSettings(Camera src, Camera dst)
    {
        try
        {
            // ── Core camera properties ──
            dst.clearFlags      = src.clearFlags;
            dst.backgroundColor = src.backgroundColor;
            dst.cullingMask     = src.cullingMask;
            dst.nearClipPlane   = src.nearClipPlane;
            dst.farClipPlane    = src.farClipPlane;
            dst.allowHDR        = src.allowHDR;
            dst.allowMSAA       = src.allowMSAA;
            dst.renderingPath   = src.renderingPath;

            // ── HDRP-specific settings ──
            var srcHD = src.gameObject.GetComponent<HDAdditionalCameraData>();
            var dstHD = dst.gameObject.GetComponent<HDAdditionalCameraData>();
            if (srcHD != null && dstHD != null)
            {
                // Volume layer mask — controls which HDRP Volumes affect this camera.
                // Without this, the VR camera won't pick up scene lighting, sky, or exposure.
                dstHD.volumeLayerMask = srcHD.volumeLayerMask;
                // Probe layer mask — controls which reflection probes affect this camera.
                dstHD.probeLayerMask  = srcHD.probeLayerMask;
                // Clear color mode (Sky, Color, None).
                dstHD.clearColorMode  = srcHD.clearColorMode;
                // Background color in HDR.
                dstHD.backgroundColorHDR = srcHD.backgroundColorHDR;
                // Anti-aliasing.
                dstHD.antialiasing    = srcHD.antialiasing;
                dstHD.dithering       = srcHD.dithering;
                dstHD.stopNaNs        = srcHD.stopNaNs;
                // Do NOT copy customRenderingSettings — keep it false so we inherit defaults.
                dstHD.customRenderingSettings = false;
                // Force HDRP to handle Y-flip for RT rendering.  This correctly
                // inverts both the image orientation and face culling internally,
                // without needing GL.invertCulling or projection matrix hacks.
                dstHD.flipYMode = HDAdditionalCameraData.FlipYMode.ForceFlipY;

                Log.LogInfo($"[VRCamera] Copied HDRP to {dst.gameObject.name}: " +
                            $"volumeLayerMask=0x{dstHD.volumeLayerMask.value:X8} " +
                            $"probeLayerMask=0x{dstHD.probeLayerMask.value:X8} " +
                            $"clearColorMode={dstHD.clearColorMode} " +
                            $"bgHDR={dstHD.backgroundColorHDR}");
            }
            else
            {
                Log.LogWarning($"[VRCamera] CopyGameCameraSettings: srcHD={srcHD != null} dstHD={dstHD != null}");
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[VRCamera] CopyGameCameraSettings failed for {dst.gameObject.name}: {ex.Message}");
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

            // Skip full renders when:
            // (a) the game camera is absent (scene transition / world gen), OR
            // (b) we are inside a save-reload grace period that was triggered while a game
            //     camera was live (_prevGameCamValid) — same GPU-overload risk as (a),
            //     because city generation runs during the reload.
            // ATW holds the last valid frame in the headset so the transition is invisible.
            bool inReloadGrace = _sceneLoadGrace > 0 && _prevGameCamValid;
            if (_gameCam == null || inReloadGrace)
            {
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            if ((_frameCount % RenderEveryNFrames) == 0)
            {
                if ((_frameCount % (RenderEveryNFrames * 4)) == 0)
                    Canvas.ForceUpdateCanvases();

                // Force item position AFTER canvas layout but BEFORE rendering.
                // Canvas.ForceUpdateCanvases() above recalculates RectTransform positions,
                // which overrides our LateUpdate position writes on LagPivot.
                ForceItemPositionPreRender();

                // No GL.invertCulling — HDRP flipYMode handles both Y-flip and culling.
                _rightCam.Render();
                _leftCam.Render();
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
        foreach (var k in dead) { _managedCanvases.Remove(k); _positionedCanvases.Remove(k); _canvasWasActive.Remove(k); _nestedCanvasIds.Remove(k); _caseContentIds.Remove(k); }
        if (dead.Contains(_casePanelId)) { _casePanelCanvas = null; _casePanelId = -1; }

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

        if (_frameCount <= 300 || (_frameCount % 300) == 0)
            Log.LogInfo($"[VRCamera] Canvas scan: found {all.Length} canvas(es), managed={_managedCanvases.Count}");

        foreach (var canvas in all)
        {
            if (canvas == null) continue;
            if (!canvas.isRootCanvas) continue;
            if (canvas.renderMode == RenderMode.WorldSpace) continue;
            int id = canvas.GetInstanceID();
            if (_managedCanvases.ContainsKey(id)) continue;

            string cname = canvas.gameObject.name ?? "";

            // Skip transient map-component canvases (high-churn, hundreds spawned/destroyed)
            if (cname.IndexOf("MapDuct",    StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("MapButton",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                cname.IndexOf("Loading Icon", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            // Skip Ignored-category canvases — already WorldSpace or not relevant to VR UI.
            if (GetCanvasCategory(cname) == CanvasCategory.Ignored)
                continue;

            // Skip CaseCanvas — it's just an empty background (Viewport, ContentContainer, BG)
            // with no visible investigation content. In VR it renders as a bright white wash
            // that obscures windows in front of it. Actual case-board content lives elsewhere.
            if (string.Equals(cname, "CaseCanvas", StringComparison.OrdinalIgnoreCase))
            {
                // Disable it so it doesn't render at all in ScreenSpace either
                try { canvas.enabled = false; } catch { }
                Log.LogInfo($"[VRCamera] Skipped CaseCanvas (disabled — empty background)");
                continue;
            }

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

        // Reparent pass: canvases physically inside GameCanvas (or any other scaled canvas)
        // inherit the parent's world scale AND get dragged whenever the parent moves.
        // Fix both problems by reparenting them to the scene root so they are truly
        // independent world-space objects.  Set localScale to the desired world scale directly.
        // Run after ALL root canvases have been converted so every parent has its final scale.
        foreach (var kvp in _managedCanvases)
        {
            var c = kvp.Value;
            if (c == null) continue;
            if (_nestedCanvasIds.Contains(kvp.Key)) continue;

            if (c.transform.parent != null)
            {
                float parentWS = 1.0f;
                try { parentWS = c.transform.parent.lossyScale.x; } catch { }
                if (parentWS < 0.0001f || Mathf.Approximately(parentWS, 1.0f)) continue;
                // Reparent to scene root — preserves world position/rotation.
                try { c.transform.SetParent(null, true); } catch { continue; }
            }

            // Enforce correct scale — the game may reset localScale when it opens/closes
            // UI panels (e.g. WindowCanvas when opening notebook). Re-apply every scan.
            var cd = GetCategoryDefaults(GetCanvasCategory(c.gameObject.name ?? ""));
            var rtAfter = c.GetComponent<RectTransform>();
            var sdAfter = rtAfter != null ? rtAfter.sizeDelta : Vector2.zero;
            // Skip canvases with zero sizeDelta — scale calculation would be invalid.
            if (sdAfter.x < 1f) continue;
            float dynScale = cd.TargetWorldWidth / sdAfter.x;
            float curScale = c.transform.localScale.x;
            if (!Mathf.Approximately(curScale, dynScale))
            {
                c.transform.localScale = Vector3.one * dynScale;
                Log.LogInfo($"[VRCamera] ScaleFix '{c.gameObject.name}': {curScale:F6} → {dynScale:F6} sizeDelta=({sdAfter.x:F0},{sdAfter.y:F0}) worldW={sdAfter.x*dynScale:F2}m");
            }
        }

        foreach (var kvp in _managedCanvases)
        {
            if (kvp.Value == null) continue;
            // Rate-limit rescans: skip canvases that were recently rescanned.
            // This prevents MinimapCanvas (1300+ graphics) from creating hundreds
            // of new Material instances every scan cycle, which causes D3D device loss.
            int cid = kvp.Key;
            if (_lastRescanFrame.TryGetValue(cid, out int lastFrame) &&
                (_frameCount - lastFrame) < RescanCooldownFrames)
                continue;
            _lastRescanFrame[cid] = _frameCount;
            RescanCanvasAlpha(kvp.Value);
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

                // Skip dynamically-instantiated prefab canvases (name contains "(Clone)").
                // The game spawns hundreds of these for map components (MapButtonComponent,
                // MapLayer, MapDuctComponent, Vent, Duct, Key etc.); tracking them inflates
                // _managedCanvases and causes ForceUIZTestAlways to break their materials.
                // Genuine UI sub-panels (WindowCanvas, LocationDetailsCanvas, ActionPanelCanvas)
                // are all root canvases, not nested, so this filter does not affect them.
                string ncName = nc.gameObject.name ?? "";
                if (ncName.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                // Also skip Loading Icon regardless of clone suffix — it is transient.
                if (ncName.IndexOf("Loading Icon", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                // Disable CanvasScaler on nested canvases too — same inflation bug
                // as root canvases. Without this, nested sizeDelta can be 2–4× too large.
                try
                {
                    var nestedScaler = nc.GetComponent<CanvasScaler>();
                    if (nestedScaler != null)
                    {
                        nestedScaler.enabled = false;
                        Log.LogInfo($"[VRCamera] Disabled CanvasScaler on nested '{ncName}'");
                    }
                }
                catch { }
                // Log nested canvas size for debugging — include world-space data (first time only)
                try
                {
                    var nrt = nc.GetComponent<RectTransform>();
                    if (nrt != null)
                    {
                        var nsd = nrt.sizeDelta;
                        var nls = nc.transform.localScale;
                        var wls = nc.transform.lossyScale;
                        var wpos = nc.transform.position;
                        float worldW = nsd.x * wls.x;
                        float worldH = nsd.y * wls.y;
                        Log.LogInfo($"[VRCamera] NestedSize '{ncName}': sizeDelta=({nsd.x:F0},{nsd.y:F0}) localScale=({nls.x:F4},{nls.y:F4},{nls.z:F4}) lossyScale=({wls.x:F6},{wls.y:F6},{wls.z:F6}) worldSize=({worldW:F2},{worldH:F2})m pos=({wpos.x:F2},{wpos.y:F2},{wpos.z:F2})");
                    }
                }
                catch { }
                int patched = ForceUIZTestAlways(nc, logQueueMap: true);
                // Only add to _managedCanvases if not already tracked — prevents NestedSize
                // logging spam on every scan cycle for the same canvas.
                if (!_managedCanvases.ContainsKey(nid))
                    _managedCanvases[nid] = nc;

                // NOTE: "Content", "Lines", "Strings" under GameCanvas are MAP overlay canvases
                // (PaperImg, CityText, DrawingBrush, Key, Vent, Duct etc.) — NOT the case board.
                // The actual case-board investigation content (pins, notes, connections) lives
                // elsewhere (likely in GameCanvas's direct graphic hierarchy, found dynamically).
                // PopupMessage and TutorialMessage are interactive dialogs nested under
                // TooltipCanvas but need independent positioning and ray interaction.
                bool isIndependentDialog =
                    ncName.Equals("PopupMessage",     StringComparison.OrdinalIgnoreCase) ||
                    ncName.Equals("TutorialMessage",  StringComparison.OrdinalIgnoreCase);
                if (!isIndependentDialog)
                    _nestedCanvasIds.Add(nid);   // mark as nested — don't independently position
                Log.LogInfo($"[VRCamera] NestedCanvas '{nc.gameObject.name}' in '{root.gameObject.name}' patched={patched} independent={isIndependentDialog}");
            }
        }

        // Ensure worldCamera is set on all managed canvases.
        var wcam = _leftCam;
        if (wcam != null)
        {
            foreach (var c in _managedCanvases.Values)
                if (c != null && c.worldCamera == null) c.worldCamera = wcam;
        }

        // Post-scan: dump MenuCanvas state so we can diagnose black-screen on startup.
        if (_menuCanvasRef != null && _frameCount <= 300)
        {
            try
            {
                bool mcEnabled  = _menuCanvasRef.enabled;
                bool mcActive   = _menuCanvasRef.gameObject.activeSelf;
                var  mcMode     = _menuCanvasRef.renderMode;
                int  mcFades    = _managedFades.Count;
                Log.LogInfo($"[VRCamera] MenuCanvasDiag: enabled={mcEnabled} active={mcActive} mode={mcMode} managedFades={mcFades}");
            }
            catch { }
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

    private static bool ShouldRelaxMenuClipping(Canvas canvas)
    {
        if (canvas == null) return false;
        var cat = GetCanvasCategory(canvas.gameObject.name);
        // Relax stencil/clip masking for Menu and Panel canvases.
        // Panel canvases (e.g. CaseCanvas) use ScrollRect Viewports with Mask components
        // which break in WorldSpace — must be disabled so their content is visible.
        return cat == CanvasCategory.Menu || cat == CanvasCategory.Panel || cat == CanvasCategory.CaseBoard;
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

        // HDR boost: HDRP auto-exposure is inherited even with ExposureControl=off.
        // 32× compensates EV≈5 (typical menu/indoor lighting).
        try { if (mat.HasProperty("_FaceColor")) mat.SetColor("_FaceColor", new Color(32f, 32f, 32f, 1f)); } catch { }
        try { if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     new Color(32f, 32f, 32f, 1f)); } catch { }

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
                        int mid = mat.GetInstanceID();
                        if (s_stencilNeutralizedMats.Add(mid))   // Add returns true if newly inserted
                        {
                            NeutralizeStencilMasking(mat);
                            StrengthenMenuTextMaterial(mat);
                            patchedMaterials++;
                        }
                    }
                }
                catch { }

                try
                {
                    var crMat = g.canvasRenderer.GetMaterial(0);
                    if (crMat != null)
                    {
                        int mid = crMat.GetInstanceID();
                        if (s_stencilNeutralizedMats.Add(mid))   // Add returns true if newly inserted
                        {
                            NeutralizeStencilMasking(crMat);
                            StrengthenMenuTextMaterial(crMat);
                            patchedMaterials++;
                        }
                    }
                }
                catch { }

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
        // Panel/Menu canvases: force raycastTarget=true on all Graphic children so the
        // VR controller ray can hit them (the game defaults many to false).
        var canvasCat = GetCanvasCategory(canvasName);
        bool forceRaycastTarget = canvasCat == CanvasCategory.Panel || canvasCat == CanvasCategory.Menu || canvasCat == CanvasCategory.CaseBoard;
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
                        // Skip the root CanvasGroup — the game uses this to hide/show the entire
                        // canvas (e.g. ActionPanelCanvas alpha=0 during pause). Overriding it
                        // would fight the game's visibility control.
                        if (cg.gameObject == canvas.gameObject) continue;
                        // Also skip CanvasGroups that belong to independently-managed root
                        // canvases nested inside this canvas (e.g. CaseCanvas inside GameCanvas).
                        // Their CanvasGroups are the game's visibility controllers for those panels.
                        try {
                            var cgCv = cg.gameObject.GetComponent<Canvas>();
                            if (cgCv != null && _managedCanvases.ContainsKey(cgCv.GetInstanceID())) continue;
                        } catch { }
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
                bool isBg = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.Equals("BG", StringComparison.OrdinalIgnoreCase);
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
                            Log.LogInfo($"[VRCamera] FadeSuppress(rescan) '{nm}' on '{canvasName}' alpha={g.color.a:F2}");
                        }
                        if (g.color.a > 0f)
                            g.color = new Color(g.color.r, g.color.g, g.color.b, 0f);
                    }
                    // Skip material patching for ALL fade-named graphics regardless.
                    continue;
                }

                // Skip cutscene/video images — their dynamic textures must not be patched.
                if (isCutSceneGraphic) continue;

                // Panel/Menu canvases: enable raycasting on all graphics so the VR
                // controller ray can register hits (game sets raycastTarget=false on many).
                if (forceRaycastTarget && !g.raycastTarget)
                {
                    try { g.raycastTarget = true; } catch { }
                }

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
                        // Hard cap: stop creating materials to prevent D3D device loss
                        if (s_uiZTestMats.Count >= MaxPatchedMaterials)
                        {
                            if (!s_matCapWarned)
                            {
                                Log.LogWarning($"[VRCamera] Material cache cap ({MaxPatchedMaterials}) reached — skipping new materials");
                                s_matCapWarned = true;
                            }
                            s_patchedGraphicPtrs.Add(ptr); // mark as processed so we don't retry
                            continue;
                        }
                        mat = new Material(orig);
                        mat.name = "VRPatch_" + orig.name;
                        mat.SetInt("unity_GUIZTestMode", 8);
                        try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                        try { if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", 8); } catch { }
                        if (isAdditive)
                        {
                            // Mobile/Particles/Additive is a legacy shader that doesn't render
                            // through HDRP's WorldSpace pipeline. Replace with UI/Default which
                            // Unity special-cases across all render pipelines.
                            var uiShader = Shader.Find("UI/Default");
                            if (uiShader != null) mat.shader = uiShader;
                            mat.renderQueue = 3001;  // just above background
                            // Additive items have mc=(0,0,0,0) by design — the visual content
                            // comes from vertex color (Graphic.color) × texture. Set material
                            // color to white so vertex color drives appearance.
                            mat.color = new Color(1f, 1f, 1f, 0.85f);
                        }
                        else
                        {
                            mat.renderQueue = queue;
                            if (isBg)
                            {
                                Color c = mat.color;
                                mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                            }
                            else if (isText)
                            {
                                // HDR boost for ALL text — compensate HDRP auto-exposure.
                                // Previously only applied via RelaxMenuTextMaterials for
                                // Menu/Panel/CaseBoard canvases; now covers Default etc.
                                StrengthenMenuTextMaterial(mat);
                            }
                            else
                            {
                                // HDR boost: compensate HDRP auto-exposure on non-text UI
                                // graphics (icons, borders, images). 4× is enough for panels
                                // without washing them out (32× was far too bright).
                                Color c = mat.color;
                                mat.color = new Color(
                                    Mathf.Max(c.r, 0.01f) * 4f,
                                    Mathf.Max(c.g, 0.01f) * 4f,
                                    Mathf.Max(c.b, 0.01f) * 4f,
                                    c.a);
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
        // CRITICAL: disable CanvasScaler FIRST.
        // Without this, CanvasScaler inflates sizeDelta from the reference resolution (e.g. 1280×720)
        // to match the display resolution (~2720×1680), making every canvas 2–4 m wide.
        // After disabling, sizeDelta stays at the authored reference size.
        var scaler = canvas.GetComponent<CanvasScaler>();
        if (scaler != null)
        {
            try { scaler.enabled = false; } catch { }
        }

        // Read sizeDelta NOW (after disabling scaler) — this is the reference size.
        var rt = canvas.GetComponent<RectTransform>();
        var sd = rt != null ? rt.sizeDelta : new Vector2(1920f, 1080f);
        float sizeW = sd.x > 0 ? sd.x : 1920f;
        float sizeH = sd.y > 0 ? sd.y : 1080f;

        canvas.renderMode = RenderMode.WorldSpace;

        var cat    = GetCanvasCategory(canvas.gameObject.name);
        var catDef = GetCategoryDefaults(cat);

        // Dynamic scale: world width = TargetWorldWidth regardless of actual sizeDelta.
        // This is immune to CanvasScaler inflation — whatever the actual pixel dimensions are,
        // the canvas ends up the intended physical width in the world.
        float scale = catDef.TargetWorldWidth / sizeW;
        canvas.transform.localScale = Vector3.one * scale;

        int patched = ForceUIZTestAlways(canvas);

        try
        {
            var gr = canvas.GetComponent<GraphicRaycaster>();
            if (gr == null) gr = canvas.gameObject.AddComponent<GraphicRaycaster>();
            gr.blockingMask = 0;
            if (_leftCam != null) canvas.worldCamera = _leftCam;
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] GraphicRaycaster setup: {ex.Message}"); }

        float worldW = sizeW * scale;
        float worldH = sizeH * scale;
        string parentName = canvas.transform.parent != null ? (canvas.transform.parent.name ?? "?") : "root";
        Log.LogInfo($"[VRCamera] Canvas '{canvas.gameObject.name}' [{cat}] -> WorldSpace ({sizeW:F0}x{sizeH:F0}px, scale={scale:F6}, world={worldW:F2}x{worldH:F2}m, parent='{parentName}', patched={patched})");
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
                bool isBg = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0
                         || nm.Equals("BG", StringComparison.OrdinalIgnoreCase);
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
                    if (s_uiZTestMats.Count >= MaxPatchedMaterials)
                    {
                        if (!s_matCapWarned)
                        {
                            Log.LogWarning($"[VRCamera] Material cache cap ({MaxPatchedMaterials}) reached — skipping new materials");
                            s_matCapWarned = true;
                        }
                        continue;
                    }
                    mat = new Material(orig);
                    mat.name = "VRPatch_" + orig.name;
                    mat.SetInt("unity_GUIZTestMode", 8);
                    try { if (mat.HasProperty("_ZTestMode")) mat.SetInt("_ZTestMode", 8); } catch { }
                    try { if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", 8); } catch { }
                    if (isAdditive)
                    {
                        var uiShader = Shader.Find("UI/Default");
                        if (uiShader != null) mat.shader = uiShader;
                        mat.renderQueue = 3001;
                        mat.color = new Color(1f, 1f, 1f, 0.85f);
                    }
                    else
                    {
                        mat.renderQueue = queue;
                        if (isBg)
                        {
                            Color c = mat.color;
                            mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                        }
                        else if (isText)
                        {
                            StrengthenMenuTextMaterial(mat);
                        }
                        else
                        {
                            Color c = mat.color;
                            mat.color = new Color(
                                Mathf.Max(c.r, 0.01f) * 4f,
                                Mathf.Max(c.g, 0.01f) * 4f,
                                Mathf.Max(c.b, 0.01f) * 4f,
                                c.a);
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

                // Detailed diagnostic for ActionPanelCanvas — dump shader, material color, vertex color
                string cnam = canvas.gameObject.name ?? "";
                if (cnam.Equals("ActionPanelCanvas", StringComparison.OrdinalIgnoreCase) ||
                    cnam.Equals("PopupMessage", StringComparison.OrdinalIgnoreCase))
                {
                    var dsb = new System.Text.StringBuilder();
                    dsb.Append($"[VRCamera] DetailDiag '{cnam}': ");
                    int limit = Mathf.Min(10, graphics2.Length);
                    for (int di = 0; di < limit; di++)
                    {
                        var dg = graphics2[di];
                        if (dg == null) continue;
                        try
                        {
                            var dm = dg.material;
                            string sn = dm?.shader?.name ?? "null";
                            Color mc = dm != null ? dm.color : Color.clear;
                            Color vc = dg.color;
                            bool rt = dg.raycastTarget;
                            bool act = dg.gameObject.activeSelf;
                            dsb.Append($"[{di}]'{dg.gameObject.name}' sh='{sn}' mc=({mc.r:F1},{mc.g:F1},{mc.b:F1},{mc.a:F2}) vc=({vc.r:F2},{vc.g:F2},{vc.b:F2},{vc.a:F2}) rt={rt} act={act} | ");
                        }
                        catch { dsb.Append($"[{di}]ERR | "); }
                    }
                    Log.LogInfo(dsb.ToString());
                }
            }
            catch { }
        }

        return count;
    }


    private void PositionCanvases()
    {
        // Suppress FadeOverlay graphics every 4 frames (prevents black screen flash).
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

        // Hide MenuCanvas while VR settings panel is open (only toggle on state change —
        // toggling every frame causes material instance flood → crash).
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

        // Head/body reference directions for placement.
        Vector3 headPos = _leftCam.transform.position;
        float   headYaw = _leftCam.transform.eulerAngles.y;
        Quaternion yawOnly = Quaternion.Euler(0f, headYaw, 0f);
        Vector3 forward = yawOnly * Vector3.forward;

        // Body-lock: keep HUDanchor aligned with VROrigin (snap-turn yaw, CC position).
        // VROrigin already follows snap-turn rotations; _hudAnchor is its child so it inherits yaw.
        // No explicit update needed — it tracks automatically via the transform hierarchy.

        int cursorId = _cursorCanvas != null ? _cursorCanvas.GetInstanceID() : -1;

        // Active-state tracking: detect false→true transitions to trigger recentre.
        foreach (var kvp in _managedCanvases)
        {
            if (kvp.Value == null) continue;
            int tid = kvp.Key;
            bool nowActive = IsCanvasVisible(kvp.Value);
            bool wasActive;
            bool hadTracking = _canvasWasActive.TryGetValue(tid, out wasActive);

            if (hadTracking && !wasActive && nowActive)
            {
                var cat = GetCanvasCategory(kvp.Value.gameObject.name);
                var catDef = GetCategoryDefaults(cat);
                if (catDef.RecentreOnActivate && !catDef.IsHUD)
                    _positionedCanvases.Remove(tid);

                // CaseBoard recentre: rebuild all CaseBoard canvases around head
                if (cat == CanvasCategory.CaseBoard)
                    _positionedCanvases.Remove(tid);
            }

            _canvasWasActive[tid] = nowActive;
        }

        foreach (var kvp in _managedCanvases)
        {
            var canvas = kvp.Value;
            if (canvas == null) continue;
            if (_nestedCanvasIds.Contains(kvp.Key)) continue;

            int id = kvp.Key;
            bool isCursorCanvas = (id == cursorId);
            var cat     = GetCanvasCategory(canvas.gameObject.name);
            var catDefs = GetCategoryDefaults(cat);

            // ── Cursor canvas ─────────────────────────────────────────────────
            if (isCursorCanvas)
            {
                if (_cursorHasTarget)
                {
                    Vector3 toHead = (headPos - _cursorTargetPos).normalized;
                    canvas.transform.position = _cursorTargetPos + toHead * 0.02f;
                    canvas.transform.rotation = _cursorTargetRot;
                }
                continue;
            }

            // ── Tooltip ───────────────────────────────────────────────────────
            if (catDefs.RepositionEveryFrame)
            {
                if (!canvas.gameObject.activeSelf || !canvas.enabled) continue;
                float tooltipDist = _cursorAimDepth - 0.02f;
                canvas.transform.position = headPos + forward * tooltipDist + Vector3.up * catDefs.VerticalOffset;
                canvas.transform.rotation = yawOnly;
                continue;
            }

            // ── HUD (body-locked) ─────────────────────────────────────────────
            // Parent to HUDanchor once; after that it follows body movement automatically.
            if (catDefs.IsHUD)
            {
                if (!_positionedCanvases.Contains(id) && IsCanvasVisible(canvas))
                {
                    try
                    {
                        canvas.transform.SetParent(_hudAnchor, false);
                        canvas.transform.localPosition = new Vector3(0f, catDefs.VerticalOffset, catDefs.Distance);
                        canvas.transform.localRotation = Quaternion.identity;
                        _positionedCanvases.Add(id);
                        Log.LogInfo($"[VRCamera] HUD parented '{canvas.gameObject.name}' to HUDAnchor dist={catDefs.Distance:F2}m");
                    }
                    catch (Exception ex) { Log.LogWarning($"[VRCamera] HUD parent: {ex.Message}"); }
                }
                continue;
            }

            // ── Menu, Panel, CaseBoard, Default ──────────────────────────────
            // Skip if already positioned and not needing recentre.
            if (_positionedCanvases.Contains(id)) continue;
            // Skip if not currently visible (will be placed when it activates).
            if (!IsCanvasVisible(canvas)) continue;

            float dist = catDefs.Distance;
            float vOff = catDefs.VerticalOffset;

            // PopupMessage: always 0.2m closer than base Menu distance (confirmation over pause menu)
            string cname = canvas.gameObject.name ?? "";
            if (cname.Equals("PopupMessage", StringComparison.OrdinalIgnoreCase))
                dist = GetCategoryDefaults(CanvasCategory.Menu).Distance - 0.2f;
            // ActionPanelCanvas: 0.15m closer than CaseBoard so action buttons are in front
            else if (cname.Equals("ActionPanelCanvas", StringComparison.OrdinalIgnoreCase))
                dist = GetCategoryDefaults(CanvasCategory.CaseBoard).Distance - 0.15f;

            // CaseBoard: primary anchors in front of head, others maintain relative offset
            if (cat == CanvasCategory.CaseBoard && id != _caseBoardPrimaryId)
            {
                if (_caseBoardPrimaryId >= 0 && _managedCanvases.TryGetValue(_caseBoardPrimaryId, out var primary) && primary != null
                    && _positionedCanvases.Contains(_caseBoardPrimaryId))
                {
                    // Apply stored relative offset from primary CaseCanvas
                    if (_caseBoardOffsets.TryGetValue(id, out var offset))
                    {
                        canvas.transform.position = primary.transform.position + offset;
                        canvas.transform.rotation = primary.transform.rotation;
                        _positionedCanvases.Add(id);
                        continue;
                    }
                    // No stored offset yet — place alongside primary
                    canvas.transform.position = primary.transform.position + primary.transform.right * 0.1f;
                    canvas.transform.rotation = primary.transform.rotation;
                    _positionedCanvases.Add(id);
                    continue;
                }
                // Primary not positioned yet — defer to next frame
                continue;
            }

            canvas.transform.position = headPos + forward * dist + Vector3.up * vOff;
            canvas.transform.rotation = yawOnly;
            _positionedCanvases.Add(id);

            Log.LogInfo($"[VRCamera] Placed '{cname}' [{cat}] dist={dist:F2}m yaw={headYaw:F1}°");
        }

        // Sync map/content nested canvases to CaseCanvas transform.
        if (_casePanelCanvas != null && _caseContentIds.Count > 0 && _positionedCanvases.Contains(_casePanelId))
        {
            Vector3    casePos = _casePanelCanvas.transform.position;
            Quaternion caseRot = _casePanelCanvas.transform.rotation;
            foreach (var cid in _caseContentIds)
            {
                if (!_managedCanvases.TryGetValue(cid, out var cc) || cc == null) continue;
                cc.transform.position = casePos;
                cc.transform.rotation = caseRot;
            }
        }
    }

    // Returns true if the canvas is active, enabled, and not faded out via CanvasGroup.
    private static bool IsCanvasVisible(Canvas canvas)
    {
        if (canvas == null) return false;
        if (!canvas.gameObject.activeSelf || !canvas.enabled) return false;
        try
        {
            var cg = canvas.GetComponent<CanvasGroup>();
            if (cg != null && cg.alpha < 0.1f) return false;
        }
        catch { }
        return true;
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

        // Grip-drag: move CaseBoard canvases with the grip button.
        UpdateGripDrag(displayTime);

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

        // Laser pointer: draw a beam from the controller forward to the cursor target (or 3 m).
        if (_laserLine != null && _rightControllerGO != null)
        {
            try
            {
                Vector3 laserStart = _rightControllerGO.transform.position;
                Vector3 laserEnd   = _cursorHasTarget
                    ? _cursorTargetPos
                    : (laserStart + _rightControllerGO.transform.forward * 3.0f);
                _laserLine.SetPosition(0, laserStart);
                _laserLine.SetPosition(1, laserEnd);
                if (!_laserLine.enabled) _laserLine.enabled = true;
            }
            catch { }
        }

        // Left laser pointer: toggled via VR Settings "Left Laser" toggle.
        if (_leftLaserLine != null && _leftControllerGO != null)
        {
            bool showLeft = VRSettingsPanel.LeftLaserEnabled;
            if (showLeft)
            {
                try
                {
                    Vector3 lStart = _leftControllerGO.transform.position;
                    Vector3 lEnd   = lStart + _leftControllerGO.transform.forward * 3.0f;
                    _leftLaserLine.SetPosition(0, lStart);
                    _leftLaserLine.SetPosition(1, lEnd);
                    if (!_leftLaserLine.enabled) _leftLaserLine.enabled = true;
                }
                catch { }
            }
            else if (_leftLaserLine.enabled)
            {
                _leftLaserLine.enabled = false;
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
            float   bestDepth     = float.MaxValue;
            Canvas? bestCanvas    = null;
            bool    foundHit      = false;
            // nearestPlane: depth of the closest facing canvas plane, regardless of bounds.
            // Seed to MaxValue so any canvas can update it.
            float nearestPlane = float.MaxValue;

            foreach (var kvp in _managedCanvases)
            {
                var c = kvp.Value;
                if (c == null) continue;
                if (!c.gameObject.activeSelf || !c.enabled) continue;
                // Skip canvases hidden via CanvasGroup.alpha (invisible wall prevention).
                // These canvases remain activeSelf=true but are visually transparent;
                // the ray should pass through them as if they don't exist.
                try
                {
                    var cg = c.GetComponent<CanvasGroup>();
                    if (cg != null && cg.alpha < 0.1f) continue;
                }
                catch { }
                // Exclude cursor canvas — by ref AND by name as belt-and-suspenders.
                if (_cursorCanvas != null && c.GetInstanceID() == _cursorCanvas.GetInstanceID()) continue;
                if (c.gameObject.name?.IndexOf("VRCursor", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                // Exclude every-frame canvases (tooltips) — they follow cursor depth, not the other way.
                if (GetCategoryDefaults(GetCanvasCategory(c.gameObject.name)).RepositionEveryFrame) continue;
                // Exclude HUD canvases — they sit far behind menus and must not pull cursor depth.
                if (GetCanvasCategory(c.gameObject.name) == CanvasCategory.HUD) continue;
                // Exclude nested canvases — their position is driven by the parent, not independently.
                if (_nestedCanvasIds.Contains(kvp.Key)) continue;

                var pl = new Plane(-c.transform.forward, c.transform.position);
                if (!pl.Raycast(new Ray(dCtrlPos, dCtrlFwd), out float hitDist) || hitDist <= 0f) continue;

                float depth = Vector3.Dot(c.transform.position - dHeadPos, dHeadFwd);
                if (depth > 0f && depth < nearestPlane) nearestPlane = depth;

                // Bounds check: only count as a hit when ray lands inside the canvas rect.
                Vector3 lp = c.transform.InverseTransformPoint(dCtrlPos + dCtrlFwd * hitDist);
                var rt = c.GetComponent<RectTransform>();
                if (rt != null)
                {
                    Vector2 hs = rt.sizeDelta * 0.5f;
                    if (Mathf.Abs(lp.x) > hs.x || Mathf.Abs(lp.y) > hs.y) continue;
                }
                if (!foundHit || depth < bestDepth) { bestDepth = depth; bestCanvas = c; foundHit = true; }
            }

            if (foundHit && bestCanvas != null)
            {
                _cursorHasTarget   = true;
                _cursorTargetPos   = bestCanvas.transform.position;
                _cursorTargetRot   = bestCanvas.transform.rotation;
                _cursorAimDepth    = bestDepth - 0.01f;
            }
            else
            {
                _cursorHasTarget = false;
                // Update depth from nearest facing plane so tooltips stay roughly right.
                // If no canvas plane found at all, hold the previous depth.
                if (nearestPlane < float.MaxValue) _cursorAimDepth = nearestPlane - 0.01f;
            }
        }

        OpenXRManager.GetTriggerState(true, out bool triggerNow);
        _prevTrigger = triggerNow;

        if (_triggerNeedsRelease)
        {
            if (!triggerNow) _triggerNeedsRelease = false;
        }
        else if (triggerNow && (Time.frameCount - _triggerFireFrame) >= 20)
        {
            _triggerNeedsRelease = true;
            _triggerFireFrame = Time.frameCount;
            TryClickCanvas(_rightControllerGO.transform.position, _rightControllerGO.transform.forward);
        }

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

    private void UpdateGripDrag(long displayTime)
    {
        bool gripNow = false;
        try { OpenXRManager.GetGripState(true, out gripNow); } catch { }

        bool gripPressed  = gripNow && !_gripWasPressed;
        bool gripReleased = !gripNow && _gripWasPressed;
        _gripWasPressed = gripNow;

        // Start drag: grip pressed while controller ray hits a CaseBoard canvas
        if (gripPressed && _gripDragCanvas == null && _rightControllerGO != null)
        {
            Vector3 ctrlPos = _rightControllerGO.transform.position;
            Vector3 ctrlFwd = _rightControllerGO.transform.forward;
            var ray = new Ray(ctrlPos, ctrlFwd);

            foreach (var kvp in _managedCanvases)
            {
                var c = kvp.Value;
                if (c == null || !IsCanvasVisible(c)) continue;
                var dragCat = GetCanvasCategory(c.gameObject.name);
                // Allow grip-drag on CaseBoard and Menu canvases (notebook, map, bio, location details)
                // but NOT on CaseCanvas (just the background board) or HUD/Tooltip/Ignored.
                if (dragCat != CanvasCategory.CaseBoard && dragCat != CanvasCategory.Menu) continue;
                string cName = c.gameObject.name ?? "";
                if (cName.Equals("CaseCanvas", StringComparison.OrdinalIgnoreCase)) continue;

                var pl = new Plane(-c.transform.forward, c.transform.position);
                if (!pl.Raycast(ray, out float dist) || dist <= 0f) continue;
                Vector3 lp = c.transform.InverseTransformPoint(ctrlPos + ctrlFwd * dist);
                var rt = c.GetComponent<RectTransform>();
                if (rt != null)
                {
                    Vector2 hs = rt.sizeDelta * 0.5f;
                    if (Mathf.Abs(lp.x) > hs.x || Mathf.Abs(lp.y) > hs.y) continue;
                }

                _gripDragCanvas    = c;
                _gripDragOffset    = c.transform.position - ctrlPos;
                _gripDragRotOffset = Quaternion.Inverse(_rightControllerGO.transform.rotation) * c.transform.rotation;
                Log.LogInfo($"[VRCamera] GripDrag start: '{c.gameObject.name}'");
                break;
            }
        }

        // While dragging: move canvas with controller
        if (gripNow && _gripDragCanvas != null && _rightControllerGO != null)
        {
            Vector3    ctrlPos = _rightControllerGO.transform.position;
            Quaternion ctrlRot = _rightControllerGO.transform.rotation;
            _gripDragCanvas.transform.position = ctrlPos + _gripDragOffset;
            _gripDragCanvas.transform.rotation = ctrlRot * _gripDragRotOffset;
        }

        // Release: store relative offsets from primary CaseCanvas
        if (gripReleased && _gripDragCanvas != null)
        {
            // Find primary (CaseCanvas) if not already known
            if (_caseBoardPrimaryId < 0)
            {
                foreach (var kvp in _managedCanvases)
                {
                    if (kvp.Value == null) continue;
                    string n = kvp.Value.gameObject.name ?? "";
                    if (n.Equals("CaseCanvas", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("caseCanvas", StringComparison.OrdinalIgnoreCase))
                    {
                        _caseBoardPrimaryId = kvp.Key;
                        break;
                    }
                }
            }

            if (_caseBoardPrimaryId >= 0 &&
                _managedCanvases.TryGetValue(_caseBoardPrimaryId, out var primary) &&
                primary != null)
            {
                int dragId = _gripDragCanvas.GetInstanceID();
                _caseBoardOffsets[dragId] = _gripDragCanvas.transform.position - primary.transform.position;
            }

            Log.LogInfo($"[VRCamera] GripDrag end: '{_gripDragCanvas.gameObject.name}'");
            _gripDragCanvas = null;
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
        if (_playerCC == null) return;
        // Refuse to drive the CharacterController during any reload grace period.
        if (_sceneLoadGrace > 0) return;
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;

        if (!OpenXRManager.GetThumbstickState(false, out float lx, out float ly)) return;
        if (Mathf.Abs(lx) <= MoveDeadZone && Mathf.Abs(ly) <= MoveDeadZone)
            return;

        // Apply dead-zone scaling so motion starts smoothly at the threshold
        float dx = Mathf.Abs(lx) > MoveDeadZone ? lx : 0f;
        float dy = Mathf.Abs(ly) > MoveDeadZone ? ly : 0f;

        // Head-relative direction: use HMD yaw (left eye camera world yaw)
        float headYaw = _leftCam != null ? _leftCam.transform.eulerAngles.y : transform.eulerAngles.y;
        Vector3 fwd   = Quaternion.Euler(0f, headYaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, headYaw, 0f) * Vector3.right;
        bool alwaysRun = PlayerPrefs.GetInt("alwaysRun", 0) != 0;
        float baseSpeed  = alwaysRun ? MoveSpeed * SprintMultiplier : MoveSpeed;
        float altSpeed   = alwaysRun ? MoveSpeed : MoveSpeed * SprintMultiplier;
        float speed = _sprintActive ? altSpeed : baseSpeed;
        Vector3 hMove = (fwd * dy + right * dx) * speed;

        // Call CharacterController.Move() to drive player locomotion.
        // Guard: verify CC is still alive before touching it (destroyed objects aren't null
        // in IL2CPP — the managed wrapper lingers after the native object is gone).
        try
        {
            if (_playerCC.gameObject == null || !_playerCC.gameObject.activeInHierarchy)
            {
                Log.LogInfo("[Movement] CC gameObject inactive/null — invalidating");
                _playerCC = null;
                return;
            }
            _playerCC.Move(hMove * Time.deltaTime);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[Movement] CC.Move failed: {ex.Message} — invalidating CC");
            _playerCC = null;
        }
    }

    /// <summary>
    /// Left-controller menu/Y button → ESC simulation.
    ///
    /// State machine (avoids rapid-fire from OpenXR button oscillation):
    ///   Idle          → on press: fire ESC, go to Held
    ///   Held          → on release: start 0.5 s post-release cooldown
    ///   ReleaseCooldown → after 0.5 s: back to Idle
    ///
    /// Canvas repositioning is handled automatically by PositionCanvases active-state
    /// tracking: when the pause-menu canvas transitions inactive→active it is removed
    /// from _positionedCanvases and placed at the current head pose next frame.
    /// We only need to force a scan tick so that any brand-new canvas (not yet in
    /// _managedCanvases) is discovered quickly rather than waiting up to 90 frames.
    /// </summary>
    private void UpdateMenuButton()
    {
        // Phase 1 — post-fire lockout (WALL-CLOCK time, NOT Time.deltaTime).
        // Time.deltaTime can be >> 1s when the game drops to <1fps processing the pause menu's
        // 2000+ canvas elements, which would evaporate a deltaTime-based 1s countdown in one frame.
        // Time.realtimeSinceStartup always advances at real-world speed regardless of frame rate.
        if (Time.realtimeSinceStartup < _menuBtnCooldownUntil) return;

        OpenXRManager.GetMenuButtonState(out bool menuNow);

        // Phase 2 — wait for physical release: after lockout, require the button to actually
        // read NOT-pressed before re-arming (guards against sustained oscillation post-lockout).
        if (_menuBtnNeedsRelease)
        {
            if (!menuNow) _menuBtnNeedsRelease = false;
            return;
        }

        // Phase 3 — armed: fire on press.
        if (!menuNow) return;

        _menuBtnNeedsRelease = true;
        _menuBtnCooldownUntil = Time.realtimeSinceStartup + 1.5f;  // 1.5 s real-time lockout
        FireMenuButton();
    }

    private void FireMenuButton()
    {
        try
        {
            const byte VK_ESCAPE = 0x1B;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_ESCAPE, 0, 0,               UIntPtr.Zero); // key down
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // key up
            Log.LogInfo("[VRCamera] Menu button → ESC");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateMenuButton: {ex.Message}"); }

        // Force immediate canvas scan to discover any brand-new pause-menu canvas.
        // Existing canvases are repositioned automatically when they become active
        // (see _canvasWasActive tracking in PositionCanvases).
        _canvasTick = UICanvasScanRate;
    }

    /// <summary>
    /// Right A → Jump.
    /// Drives CharacterController.Move() with upward velocity directly, since we've disabled
    /// FirstPersonController (which would normally process Space key jump).
    /// Also sends Space key as fallback for any other game systems that check it.
    /// </summary>
    private float _jumpVerticalVelocity;
    private const float JumpForce = 5.0f;   // m/s upward impulse
    private const float Gravity   = -15.0f; // m/s² (slightly stronger than real for game feel)
    private void UpdateJump()
    {
        if (_sceneLoadGrace > 0) { _jumpVerticalVelocity = 0f; return; }
        if (_playerCC == null) return;
        // Only apply gravity/jump when movement discovery is done (= we're in-game, not main menu)
        if (!_movementDiscoveryDone) { _jumpVerticalVelocity = 0f; return; }

        // Use unscaledDeltaTime so gravity works even when game is paused (timeScale=0).
        // Without this, the player floats in the air while ESC menu is open and doesn't
        // come back down when the menu is closed.
        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f || dt > 0.2f) return; // skip on zero-dt or huge spikes

        // Only read jump button when VR settings panel is NOT open
        bool pressed = false;
        bool edge = false;
        if (VRSettingsPanel.RootGO == null || !VRSettingsPanel.RootGO.activeSelf)
        {
            OpenXRManager.GetButtonAState(out pressed);
            edge = pressed && !_jumpBtnPrev;
        }
        _jumpBtnPrev = pressed;

        // Guard: verify CC is still alive
        try
        {
            if (_playerCC.gameObject == null || !_playerCC.gameObject.activeInHierarchy)
            { _playerCC = null; return; }
        }
        catch { _playerCC = null; return; }

        // Apply gravity every frame
        if (_playerCC.isGrounded)
            _jumpVerticalVelocity = -0.5f; // small downward to keep grounded
        else
            _jumpVerticalVelocity += Gravity * dt;

        // Clamp terminal velocity to prevent runaway falling
        if (_jumpVerticalVelocity < -20f) _jumpVerticalVelocity = -20f;

        // Jump impulse on press edge, only when grounded
        if (edge && _playerCC.isGrounded)
        {
            _jumpVerticalVelocity = JumpForce;
            Log.LogInfo("[VRCamera] Jump");
        }

        // Apply vertical movement — always, even during pause, to prevent floating
        if (Mathf.Abs(_jumpVerticalVelocity) > 0.01f)
        {
            try
            {
                _playerCC.Move(new Vector3(0f, _jumpVerticalVelocity * dt, 0f));
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[VRCamera] Jump Move failed: {ex.Message}");
                _jumpVerticalVelocity = 0f;
            }
        }
    }

    /// <summary>
    /// Left trigger → world interaction via left controller aiming.
    /// While trigger is held: points the game camera (Camera.main) at the left controller's
    /// aim direction so the game's InteractionController raycast follows the left hand.
    /// On press edge: simulates left mouse button click.
    /// On release: restores camera to VR head direction.
    /// </summary>
    private bool _interactAiming;  // true while left trigger is held and camera is redirected
    private void UpdateInteract()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        OpenXRManager.GetTriggerState(false, out bool pressed);

        // While trigger is held, point game camera at left controller aim direction.
        // The game's InteractionController raycasts from Camera.main each frame —
        // this makes it follow the left hand instead of VR head.
        if (pressed && _gameCamRef != null && _leftControllerGO != null)
        {
            _gameCamRef.transform.rotation = _leftControllerGO.transform.rotation;
            _interactAiming = true;
        }
        else if (_interactAiming && !pressed)
        {
            // Restore camera rotation to VR head when trigger released
            _interactAiming = false;
        }

        bool edge = pressed && !_interactBtnPrev;
        _interactBtnPrev = pressed;
        if (!edge) return;
        try
        {
            // Primary: left mouse button (game uses LMB for pick up, interact, attack)
            const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            const uint MOUSEEVENTF_LEFTUP   = 0x0004;
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP,   0, 0, 0, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Interact (LMB via left controller aim)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateInteract: {ex.Message}"); }
    }

    /// <summary>Left X → C (crouch toggle).</summary>
    private void UpdateCrouch()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        OpenXRManager.GetButtonXState(out bool pressed);
        bool edge = pressed && !_crouchBtnPrev;
        _crouchBtnPrev = pressed;
        if (!edge) return;
        try
        {
            const byte VK_C = 0x43;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_C, 0, 0,               UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Crouch (C)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateCrouch: {ex.Message}"); }
    }

    /// <summary>
    /// Left thumbstick click → Sprint toggle (Shift held/released).
    /// Also auto-stops sprint when the left stick returns to centre.
    /// </summary>
    private void UpdateSprint()
    {
        if (_playerCC == null) return;
        if (VRSettingsPanel.RootGO?.activeSelf == true)
        {
            StopSprint();
            return;
        }

        // Auto-stop when stick returns to centre (player stopped moving)
        if (_sprintActive)
        {
            OpenXRManager.GetThumbstickState(false, out float lx, out float ly);
            if (Mathf.Abs(lx) < MoveDeadZone && Mathf.Abs(ly) < MoveDeadZone)
            {
                StopSprint();
                return;
            }
        }

        OpenXRManager.GetThumbClickState(false, out bool clicked);
        bool edge = clicked && !_sprintThumbPrev;
        _sprintThumbPrev = clicked;
        if (!edge) return;

        if (_sprintActive) StopSprint();
        else               StartSprint();
    }

    private void StartSprint()
    {
        if (_sprintActive) return;
        _sprintActive = true;
        Log.LogInfo("[VRCamera] Sprint start");
    }

    private void StopSprint()
    {
        if (!_sprintActive) return;
        _sprintActive = false;
        Log.LogInfo("[VRCamera] Sprint stop");
    }

    /// <summary>Right B → Tab (notebook/map).</summary>
    private void UpdateNotebook()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        OpenXRManager.GetButtonBState(out bool pressed);
        bool edge = pressed && !_notebookBtnPrev;
        _notebookBtnPrev = pressed;
        if (!edge) return;
        try
        {
            const byte VK_TAB = 0x09;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_TAB, 0, 0,               UIntPtr.Zero);
            keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Notebook (Tab)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateNotebook: {ex.Message}"); }
    }

    /// <summary>Right thumbstick click → middle mouse button (flashlight toggle).</summary>
    private void UpdateFlashlight()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        OpenXRManager.GetThumbClickState(true, out bool pressed);
        bool edge = pressed && !_flashlightBtnPrev;
        _flashlightBtnPrev = pressed;
        if (!edge) return;
        try
        {
            const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
            const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
            mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_MIDDLEUP,   0, 0, 0, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Flashlight (middle mouse)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateFlashlight: {ex.Message}"); }
    }

    /// <summary>Left grip → X (inventory).</summary>
    private void UpdateInventory()
    {
        if (VRSettingsPanel.RootGO?.activeSelf == true) return;
        OpenXRManager.GetGripState(false, out bool pressed);
        bool edge = pressed && !_inventoryBtnPrev;
        _inventoryBtnPrev = pressed;
        if (!edge) return;
        try
        {
            const byte VK_X = 0x58;
            const uint KEYEVENTF_KEYUP = 0x0002;
            keybd_event(VK_X, 0, 0,               UIntPtr.Zero);
            keybd_event(VK_X, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Log.LogInfo("[VRCamera] Inventory (X)");
        }
        catch (Exception ex) { Log.LogWarning($"[VRCamera] UpdateInventory: {ex.Message}"); }
    }

    /// <summary>
    /// Syncs carried world objects to the VR controller.
    /// The game has two item systems:
    /// 1. First-person arms (LagPivot → Arms) — for inventory items (small, no world model)
    /// 2. Carried objects (InteractionController.carryingObject) — for large world items
    /// This method handles BOTH by overriding the carried object's transform.
    /// </summary>
    private bool _lagPivotReparented;
    private int _carryDiagCounter;
    private bool _armsActivated;
    private Transform? _armsTransform;              // 'Arms' GO (has Animator)
    private Transform? _firstPersonModelsTransform;  // 'FirstPersonModels' GO
    private Transform? _leftArmTransform;            // 'LeftArm' — tracks left VR controller
    private Transform? _rightArmTransform;           // 'RightArm' — tracks right VR controller
    private Transform? _leftFistTransform;           // 'LeftFist' — hand position (child of LeftArm)
    private Transform? _rightFistTransform;          // 'RightFist' — hand position (child of RightArm)
    private const float ArmScale = 0.0002f;          // pixel-space → world meters (tune as needed)
    // Rotation offset to align the arm model with the VR controller aim pose.
    // The game's arm mesh is oriented for flat-screen FPS (arm extends along local Y-down).
    // VR controller aim pose has Z-forward. These Euler offsets rotate each arm to match.
    // Right arm confirmed good at (90,90,0); left arm is mirrored so needs (90,-90,0).
    private static readonly Quaternion ArmRotOffsetRight = Quaternion.Euler(90f, 90f, 0f);
    private static readonly Quaternion ArmRotOffsetLeft  = Quaternion.Euler(90f, -90f, 0f);
    // Additional forward offset along controller forward to shift arm back so game hand
    // aligns with real hand (positive = push hand forward away from player).
    private const float ArmForwardOffset = -0.25f;
    private int _armsDiagCounter;
    private void UpdateHeldItemTracking()
    {
        // Strategy 1: Override carried world objects (InteractionController.carryingObject)
        if (_interactionController != null)
        {
            try
            {
                var co = _interactionController.carryingObject;
                if (co != null)
                {
                    var ctrlGO = VRSettingsPanel.ItemHandRight ? _rightControllerGO : _leftControllerGO;
                    if (ctrlGO != null)
                    {
                        var ctrlT = ctrlGO.transform;
                        co.transform.position = ctrlT.position + ctrlT.forward * 0.3f;
                        co.transform.rotation = ctrlT.rotation;

                        _carryDiagCounter++;
                        if (_carryDiagCounter % 300 == 1)
                            Log.LogInfo($"[VRCamera] Carrying '{co.gameObject.name}' → controller");
                    }
                }
            }
            catch { }
        }

        // Strategy 2: VR arm display — each arm independently tracks its controller
        if (_lagPivotTransform == null) return;
        try
        {
            // One-time setup: reparent LagPivot to VROrigin (not a specific controller),
            // activate Arms, cache LeftArm/RightArm, apply pixel→meter scale.
            if (!_armsActivated)
            {
                // Parent LagPivot to VROrigin so it's in world space but not tied to one controller
                if (!_lagPivotReparented)
                {
                    _lagPivotTransform.SetParent(transform, false); // this = VROrigin (VRCamera)
                    _lagPivotTransform.localPosition = Vector3.zero;
                    _lagPivotTransform.localRotation = Quaternion.identity;
                    _lagPivotTransform.localScale = Vector3.one;
                    _lagPivotReparented = true;
                    Log.LogInfo("[VRCamera] LagPivot → VROrigin");
                }

                if (_lagPivotTransform.childCount > 0)
                {
                    _firstPersonModelsTransform = _lagPivotTransform.GetChild(0); // FirstPersonModels
                    if (_firstPersonModelsTransform != null)
                    {
                        _firstPersonModelsTransform.localPosition = Vector3.zero;
                        _firstPersonModelsTransform.localRotation = Quaternion.identity;
                        // Scale: Animator drives bone positions in pixel-space (~100-3000 units).
                        // ArmScale converts to meters. Human hand ≈ 0.18m, pixel arm ≈ ~450px.
                        _firstPersonModelsTransform.localScale = new Vector3(ArmScale, ArmScale, ArmScale);

                        for (int i = 0; i < _firstPersonModelsTransform.childCount; i++)
                        {
                            var child = _firstPersonModelsTransform.GetChild(i);
                            if (child != null && child.gameObject.name == "Arms")
                            {
                                _armsTransform = child;
                                child.gameObject.SetActive(true);
                                child.localPosition = Vector3.zero;
                                child.localRotation = Quaternion.identity;
                                child.localScale = Vector3.one;

                                // Cache LeftArm, RightArm, and their Fist children
                                for (int j = 0; j < child.childCount; j++)
                                {
                                    var armChild = child.GetChild(j);
                                    if (armChild == null) continue;
                                    string armName = armChild.gameObject.name;
                                    if (armName == "LeftArm")
                                    {
                                        _leftArmTransform = armChild;
                                        // Find LeftFist child
                                        for (int k = 0; k < armChild.childCount; k++)
                                        {
                                            var fistChild = armChild.GetChild(k);
                                            if (fistChild != null && fistChild.gameObject.name == "LeftFist")
                                            { _leftFistTransform = fistChild; break; }
                                        }
                                    }
                                    else if (armName == "RightArm")
                                    {
                                        _rightArmTransform = armChild;
                                        for (int k = 0; k < armChild.childCount; k++)
                                        {
                                            var fistChild = armChild.GetChild(k);
                                            if (fistChild != null && fistChild.gameObject.name == "RightFist")
                                            { _rightFistTransform = fistChild; break; }
                                        }
                                    }
                                }

                                Log.LogInfo($"[VRCamera] Arms activated — LeftArm={(_leftArmTransform != null ? "found" : "NULL")} " +
                                            $"LeftFist={(_leftFistTransform != null ? "found" : "NULL")} " +
                                            $"RightArm={(_rightArmTransform != null ? "found" : "NULL")} " +
                                            $"RightFist={(_rightFistTransform != null ? "found" : "NULL")} scale={ArmScale}");
                                break;
                            }
                        }
                    }
                    _armsActivated = true;
                }
            }

            // Every frame: keep intermediate transforms zeroed, keep Arms active
            if (_firstPersonModelsTransform != null)
            {
                _firstPersonModelsTransform.localPosition = Vector3.zero;
                _firstPersonModelsTransform.localRotation = Quaternion.identity;
            }
            if (_armsTransform != null)
            {
                if (!_armsTransform.gameObject.activeSelf)
                    _armsTransform.gameObject.SetActive(true);
                _armsTransform.localPosition = Vector3.zero;
                _armsTransform.localRotation = Quaternion.identity;
            }

            // Position each arm so its FIST (hand) aligns with the VR controller.
            // The arm transform origin is at the elbow/upper arm, so we offset by the
            // fist-to-arm vector to put the hand at the controller position.
            PositionArmAtController(_leftArmTransform, _leftFistTransform, _leftControllerGO, ArmRotOffsetLeft);
            PositionArmAtController(_rightArmTransform, _rightFistTransform, _rightControllerGO, ArmRotOffsetRight);

            // Diagnostic
            _armsDiagCounter++;
            if (_armsDiagCounter % 300 == 1 && _leftArmTransform != null)
            {
                try
                {
                    var lp = _leftArmTransform.localPosition;
                    var wp = _leftArmTransform.position;
                    var rp = _rightArmTransform?.position ?? Vector3.zero;
                    Log.LogInfo($"[VRCamera] Arms diag: L.local=({lp.x:F0},{lp.y:F0},{lp.z:F0}) " +
                                $"L.world=({wp.x:F2},{wp.y:F2},{wp.z:F2}) R.world=({rp.x:F2},{rp.y:F2},{rp.z:F2}) " +
                                $"scale={(_firstPersonModelsTransform != null ? _firstPersonModelsTransform.localScale.x : 0f):F4}");
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Positions an arm so its fist (hand) aligns with the VR controller aim point.
    /// Uses per-arm rotation offset to align the flat-screen arm mesh with VR controller orientation.
    /// Arm origin is at the elbow — we offset so the fist child sits at the controller position,
    /// then apply ArmForwardOffset along the controller forward axis to fine-tune hand alignment.
    /// </summary>
    private static void PositionArmAtController(Transform? arm, Transform? fist, GameObject? ctrlGO, Quaternion rotOffset)
    {
        if (arm == null || ctrlGO == null) return;
        var ctrlT = ctrlGO.transform;
        // Apply rotation: controller aim rotation + model alignment offset
        arm.rotation = ctrlT.rotation * rotOffset;
        if (fist != null)
        {
            // After setting arm rotation, compute where the fist ended up,
            // then shift the arm so the fist lands exactly at the controller.
            Vector3 fistOffset = fist.position - arm.position;
            arm.position = ctrlT.position - fistOffset;
        }
        else
        {
            arm.position = ctrlT.position;
        }
        // Slide arm along controller forward to align game hand with real hand
        arm.position += ctrlT.forward * ArmForwardOffset;
    }

    /// <summary>Called right before each VR eye camera renders — last chance to position items + arms.</summary>
    private void ForceItemPositionPreRender()
    {
        // Override carried world object position right before render
        if (_interactionController != null)
        {
            try
            {
                var co = _interactionController.carryingObject;
                if (co != null)
                {
                    var ctrlGO = VRSettingsPanel.ItemHandRight ? _rightControllerGO : _leftControllerGO;
                    if (ctrlGO != null)
                    {
                        var ctrlT = ctrlGO.transform;
                        co.transform.position = ctrlT.position + ctrlT.forward * 0.3f;
                        co.transform.rotation = ctrlT.rotation;
                    }
                }
            }
            catch { }
        }

        // Final arm positioning right before render (Animator may have overwritten Update values)
        try
        {
            if (_firstPersonModelsTransform != null)
            {
                _firstPersonModelsTransform.localPosition = Vector3.zero;
                _firstPersonModelsTransform.localRotation = Quaternion.identity;
            }
            if (_armsTransform != null)
            {
                _armsTransform.localPosition = Vector3.zero;
                _armsTransform.localRotation = Quaternion.identity;
            }
            PositionArmAtController(_leftArmTransform, _leftFistTransform, _leftControllerGO, ArmRotOffsetLeft);
            PositionArmAtController(_rightArmTransform, _rightFistTransform, _rightControllerGO, ArmRotOffsetRight);
        }
        catch { }
    }

    private void DiscoverMovementSystem()
    {
        _movementDiscoveryDone = true;

        // 1. Enumerate all Rewired actions — probe common names via GetAction(name)
        try
        {
            // Probe common action names; GetAction returns null if not found
            string[] candidates = {
                "Horizontal", "Vertical", "MoveHorizontal", "MoveVertical",
                "Move Horizontal", "Move Vertical", "Strafe", "Forward",
                "Move X", "Move Y", "h", "v", "x", "y",
                "Walk", "Run", "Sprint", "Jump", "Interact", "Fire", "Aim",
                "CameraX", "CameraY", "Look X", "Look Y", "LookHorizontal", "LookVertical"
            };
            var sb = new System.Text.StringBuilder("[Movement] Rewired actions found:");
            foreach (var name in candidates)
            {
                try
                {
                    var act = Rewired.ReInput.mapping.GetAction(name);
                    if (act != null)
                        sb.Append($"\n  id={act.id} name='{act.name}' type={act.type}");
                }
                catch { }
            }
            Log.LogInfo(sb.ToString());
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Actions enum: {ex.Message}"); }

        // 2. Rewired player 0 info
        try
        {
            var player = Rewired.ReInput.players.GetPlayer(0);
            Log.LogInfo($"[Movement] Rewired player0: name='{player?.name}' id={player?.id}");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Player0: {ex.Message}"); }

        // 3. Walk up from game camera to find CharacterController / Rigidbody; cache both.
        //    Also enumerate all MonoBehaviours for diagnostic purposes (camera-look identification).
        //    Locomotion is driven via CharacterController.Move() — Rigidbody velocity is
        //    ignored by the game's kinematic FPS controller.
        try
        {
            var t = _gameCam;
            for (int i = 0; i < 10 && t != null; i++)
            {
                var cc = t.GetComponent<CharacterController>();
                var rb = t.GetComponent<Rigidbody>();
                // Enumerate ALL components for camera-look system identification
                var allComps = t.GetComponents<Component>();
                var compNames = new System.Text.StringBuilder();
                foreach (var c in allComps)
                {
                    if (c == null) continue;
                    try { compNames.Append($" {c.GetIl2CppType().Name}"); } catch { }
                }
                Log.LogInfo($"[Movement] Ancestor[{i}] '{t.gameObject.name}': CC={cc != null} RB={rb != null} components=[{compNames}]");
                if (cc != null)
                {
                    _playerCC = cc;
                    _playerRb = rb; // may be null; kept only for reset-on-reload checks
                    Log.LogInfo($"[Movement] Cached playerCC on '{t.gameObject.name}'" +
                                $" isKinematic={rb?.isKinematic}");
                    break;
                }
                t = t.parent;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Walk-up: {ex.Message}"); }

        // 3b. Cache hierarchy transforms for camera rotation sync.
        //     Disable game's camera-look MonoBehaviours so VR head rotation takes over.
        try
        {
            if (_playerCC != null)
            {
                _fpsControllerTransform = _playerCC.transform;

                // Walk DOWN from game camera to find the pitch pivot.
                // Hierarchy: FPSController → CameraLeanPivot → CamTransitionModifier → Main Camera
                // We cache the first parent above Main Camera as the pitch pivot.
                if (_gameCam != null && _gameCam.parent != null)
                    _cameraPivotTransform = _gameCam.parent;

                Log.LogInfo($"[Movement] Cached FPSController transform='{_fpsControllerTransform.gameObject.name}' " +
                            $"cameraPivot='{_cameraPivotTransform?.gameObject.name ?? "NULL"}'");

                // Disable camera-look MonoBehaviours that override VR head rotation.
                // CameraController (on Main Camera): handles mouse-look pitch/effects.
                // FirstPersonController (on FPSController): handles mouse-look yaw + WASD movement.
                //   We drive movement via CharacterController.Move() so this is safe to disable.
                // DO NOT disable: HDAdditionalCameraData (HDRP rendering), InteractionController (game interaction).
                var disableTargets = new[] { "CameraController", "FirstPersonController" };
                var t = _gameCam;
                for (int i = 0; i < 10 && t != null; i++)
                {
                    var behaviours = t.GetComponents<MonoBehaviour>();
                    foreach (var mb in behaviours)
                    {
                        if (mb == null) continue;
                        try
                        {
                            string typeName = mb.GetIl2CppType().Name;
                            bool shouldDisable = false;
                            foreach (var target in disableTargets)
                                if (typeName == target) { shouldDisable = true; break; }
                            if (shouldDisable)
                            {
                                mb.enabled = false;
                                Log.LogInfo($"[Movement] Disabled '{typeName}' on '{t.gameObject.name}'");
                            }
                        }
                        catch { }
                    }
                    if (t == _fpsControllerTransform) break;
                    t = t.parent;
                }
                _cameraLookDisabled = true;
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Camera hierarchy setup: {ex.Message}"); }

        // 4. FindObjectOfType for the FPS controller (diagnostic position check)
        try
        {
            var cc = FindObjectOfType<CharacterController>();
            if (cc != null)
                Log.LogInfo($"[Movement] FindObjectOfType<CC>: GO='{cc.gameObject.name}' pos={cc.transform.position}");
            else
                Log.LogInfo("[Movement] FindObjectOfType<CC>: not found");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] FindCC: {ex.Message}"); }

        // 5. Probe common Rewired axis names (diagnostic only — logs which axes the game uses
        //    so we can implement injection in a future session if needed)
        try
        {
            var player = Rewired.ReInput.players.GetPlayer(0);
            if (player != null)
            {
                string[] candidates = { "Horizontal", "Vertical", "MoveHorizontal", "MoveVertical",
                                        "Move Horizontal", "Move Vertical", "Strafe", "Forward",
                                        "Walk", "Run", "Move X", "Move Y" };
                var sb2 = new System.Text.StringBuilder("[Movement] Rewired axis probe:");
                foreach (var name in candidates)
                {
                    try
                    {
                        float v = player.GetAxis(name);
                        sb2.Append($" '{name}'={v:F2}");
                    }
                    catch { }
                }
                Log.LogInfo(sb2.ToString());
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Rewired probe: {ex.Message}"); }

        // 5b. Cache FirstPersonItemController for VR hand item tracking.
        try
        {
            if (_playerCC != null)
            {
                var fpsIC = _playerCC.GetComponent<FirstPersonItemController>();
                if (fpsIC != null)
                {
                    _fpsItemController = fpsIC;
                    var lp = fpsIC.leftHandObjectParent;
                    var rp = fpsIC.rightHandObjectParent;
                    var ci = fpsIC.currentItem;
                    // Log the full hierarchy from ItemContainer up to find what drives its position
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[Movement] FirstPersonItemController found. ");
                    sb.Append($"leftHandParent='{lp?.gameObject?.name ?? "NULL"}' ");
                    sb.Append($"rightHandParent='{rp?.gameObject?.name ?? "NULL"}' ");
                    sb.Append($"currentItem='{(ci != null ? "present" : "NULL")}' ");
                    sb.Append($"lagPivot='{fpsIC.lagPivotTransform?.gameObject?.name ?? "NULL"}'");
                    Log.LogInfo(sb.ToString());

                    // Walk up from ItemContainer to find its full parent chain
                    if (lp != null)
                    {
                        var walk = lp;
                        var chain = new System.Text.StringBuilder("[Movement] ItemContainer hierarchy: ");
                        for (int h = 0; h < 10 && walk != null; h++)
                        {
                            chain.Append($"'{walk.gameObject.name}' → ");
                            walk = walk.parent;
                        }
                        chain.Append("(root)");
                        Log.LogInfo(chain.ToString());
                    }
                    // Also walk from LagPivot
                    var lag = fpsIC.lagPivotTransform;
                    if (lag != null)
                    {
                        var walk2 = lag;
                        var chain2 = new System.Text.StringBuilder("[Movement] LagPivot hierarchy: ");
                        for (int h = 0; h < 10 && walk2 != null; h++)
                        {
                            chain2.Append($"'{walk2.gameObject.name}' → ");
                            walk2 = walk2.parent;
                        }
                        chain2.Append("(root)");
                        Log.LogInfo(chain2.ToString());

                        // Log all children of LagPivot
                        var childSb = new System.Text.StringBuilder("[Movement] LagPivot children: ");
                        for (int c2 = 0; c2 < lag.childCount; c2++)
                        {
                            var child = lag.GetChild(c2);
                            if (child != null) childSb.Append($"'{child.gameObject.name}' ");
                        }
                        Log.LogInfo(childSb.ToString());

                        // Log components on LagPivot and descendants to find what drives position
                        try
                        {
                            var diagNodes = new Transform[] { lag };
                            // Also check FirstPersonModels (child of LagPivot)
                            if (lag.childCount > 0) diagNodes = new Transform[] { lag, lag.GetChild(0) };
                            foreach (var dNode in diagNodes)
                            {
                                if (dNode == null) continue;
                                var comps = dNode.GetComponents<Component>();
                                var cSb = new System.Text.StringBuilder($"[Movement] Components on '{dNode.gameObject.name}': ");
                                foreach (var comp in comps)
                                {
                                    if (comp == null) continue;
                                    try
                                    {
                                        string tn = comp.GetIl2CppType().Name;
                                        cSb.Append($"{tn} ");
                                        // If MonoBehaviour, note if enabled
                                        var mb = comp.TryCast<MonoBehaviour>();
                                        if (mb != null) cSb.Append($"(enabled={mb.enabled}) ");
                                    }
                                    catch { }
                                }
                                Log.LogInfo(cSb.ToString());
                            }
                        }
                        catch { }

                        // Cache LagPivot for per-frame position override in UpdateHeldItemTracking.
                        // We do NOT reparent — the game's FirstPersonItemController sets LagPivot
                        // position in Update(). We override it in LateUpdate() so our write wins.
                        _lagPivotTransform = lag;
                        _lagPivotOrigParent = lag.parent;
                        Log.LogInfo($"[Movement] Cached LagPivot for hand tracking (parent='{lag.parent?.gameObject?.name ?? "NULL"}')");
                    }
                }
                else
                    Log.LogInfo("[Movement] FirstPersonItemController not found on FPSController.");
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] FPSItemController cache: {ex.Message}"); }

        // 5c. Cache InteractionController for carried-object tracking.
        // The game carries large objects by animating InteractableController.transform relative
        // to Camera.main. We need to override this to follow the VR hand controller instead.
        try
        {
            if (_playerCC != null)
            {
                var ic = _playerCC.GetComponent<InteractionController>();
                if (ic != null)
                {
                    _interactionController = ic;
                    Log.LogInfo($"[Movement] InteractionController found on '{_playerCC.gameObject.name}'");
                    var co = ic.carryingObject;
                    Log.LogInfo($"[Movement] carryingObject={(co != null ? co.gameObject.name : "NULL")}");
                }
                else
                    Log.LogInfo("[Movement] InteractionController not found on FPSController.");
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] InteractionController cache: {ex.Message}"); }

        // 6. Camera.main diagnostic — confirm it's non-null so SaveStateController won't crash
        try
        {
            var cm = Camera.main;
            Log.LogInfo($"[Movement] Camera.main='{cm?.gameObject?.name ?? "NULL"}'" +
                        $" enabled={cm?.enabled} cullingMask={cm?.cullingMask}");
        }
        catch (Exception ex) { Log.LogWarning($"[Movement] Camera.main check: {ex.Message}"); }
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
            // Skip HUD and Ignored canvases — not interactable, must not steal clicks.
            var clickCat = GetCanvasCategory(canvas.gameObject.name);
            if (clickCat == CanvasCategory.HUD || clickCat == CanvasCategory.Ignored) continue;
            // Skip alpha-hidden canvases (ghost canvas prevention).
            try { var cg = canvas.GetComponent<CanvasGroup>(); if (cg != null && cg.alpha < 0.1f) continue; } catch { }
            // Skip nested canvases — their clicks are handled via the parent canvas.
            if (_nestedCanvasIds.Contains(kvp.Key)) continue;
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

                    // Detect save-load button clicks.  When the user clicks "Continue" or any
                    // "New Game"-style button, SaveStateController:LoadSaveState is about to
                    // reconstruct the entire physics hierarchy.  Apply the grace period NOW —
                    // before ExecuteEvents propagates the click — so canvas scanning and
                    // locomotion are fully quiesced before the Rigidbody/CharacterJoint teardown.
                    {
                        string goNameLower = (go?.name ?? "").ToLowerInvariant();
                        bool isSaveLoad = goNameLower.Contains("continue")
                                       || goNameLower.Contains("new game")
                                       || goNameLower.Contains("new city");
                        if (isSaveLoad)
                        {
                            _sceneLoadGrace = 180;   // ~3 s at 60 fps
                            _canvasTick     = 0;
                            _playerRb       = null;
                            _playerCC       = null;
                            _fpsControllerTransform = null;
                            _cameraPivotTransform   = null;
                            _cameraLookDisabled     = false;
                            StopSprint();
                            // NOTE: do NOT set _movementDiscoveryDone = false here.
                            // Doing so would trigger DiscoverMovementSystem() at the bottom of
                            // this same Update() frame (before SaveStateController runs in the
                            // next frame), immediately re-caching _playerRb on the Rigidbody
                            // that the game is about to destroy.  Instead, just null _playerRb
                            // and guard UpdateLocomotion() with _sceneLoadGrace > 0.

                            Log.LogInfo($"[VRCamera] Save/load trigger '{go?.name}' — grace=180, playerRb cleared.");
                        }
                    }

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
        // No manual Y-flip — HDRP handles it via HDAdditionalCameraData.flipYMode.
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
        _canvasWasActive.Clear();
        _nestedCanvasIds.Clear();
        _caseContentIds.Clear();
        _casePanelCanvas = null; _casePanelId = -1;
        _managedFades.Clear();

        Log.LogInfo("[VRCamera] Destroyed.");
    }
}
