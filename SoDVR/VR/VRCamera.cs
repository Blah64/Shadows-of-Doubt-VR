using BepInEx.Logging;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.UI;
using UnityEngine.EventSystems;

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
    private Camera        _leftCam  = null!;
    private Camera        _rightCam = null!;
    private RenderTexture _leftRT   = null!;
    private RenderTexture _rightRT  = null!;
    private Transform     _gameCam  = null!; // original game camera transform; we follow its world position

    // Render throttle: call Camera.Render() every N stereo frames.
    // 1 = every frame (full quality). 2 = every other frame (half GPU load, slight judder).
    // The swapchain copy still runs every frame, so head tracking stays smooth via ATW.
    private const int RenderEveryNFrames = 1;

    // ── UI / Canvas constants ─────────────────────────────────────────────────
    private const float UIDistance       = 2.0f;    // metres in front of head
    private const float UIVerticalOffset = 0.0f;    // metres up/down from eye level
    private const float UICanvasScale    = 0.0015f; // world-units per canvas pixel (1920px → 2.88 m wide)
    private const int   UICanvasScanRate = 30;      // Unity frames between canvas scans
    // HDRP tonemapping compresses UI colour values; multiply the material _Color by this
    // factor (pre-tonemap) so buttons/text arrive at an acceptable brightness in the headset.
    // Raise if UI is still dark, lower if it looks washed-out.
    private const float UIColorBoost     = 30.0f;  // text elements — must overcome HDRP exposure
    private const float UIImageBoost     =  6.0f;  // non-text visuals; 3× too dark, 10× collapses contrast
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

    // ── Controller / laser pointer ────────────────────────────────────────────
    private GameObject?  _rightControllerGO;
    private LineRenderer? _laserLine;
    private bool         _prevTrigger;
    private int          _poseFrameCount;
    private bool         _poseEverValid;
    private bool         _laserPosLogged;

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

        // Left eye — disabled for auto-render, called manually in LateUpdate
        var leftGO = new GameObject("LeftEye");
        leftGO.transform.SetParent(_cameraOffset, false);
        _leftCam = leftGO.AddComponent<Camera>();
        _leftRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Left" };
        _leftRT.Create();
        SetupEyeCam(_leftCam, _leftRT);

        var rightGO = new GameObject("RightEye");
        rightGO.transform.SetParent(_cameraOffset, false);
        _rightCam = rightGO.AddComponent<Camera>();
        _rightRT  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "SoDVR_Right" };
        _rightRT.Create();
        SetupEyeCam(_rightCam, _rightRT);

        // Try to find and disable the game camera now. If it's not available yet
        // (e.g. main menu hasn't spawned one), TryFindGameCamera() will keep retrying in Update().
        TryFindGameCamera();

        // Right-controller laser pointer
        var ctrlGO = new GameObject("RightController");
        ctrlGO.transform.SetParent(_cameraOffset, false);
        _rightControllerGO = ctrlGO;

        _laserLine = ctrlGO.AddComponent<LineRenderer>();
        _laserLine.positionCount = 2;
        _laserLine.startWidth    = 0.005f;
        _laserLine.endWidth      = 0.002f;
        _laserLine.useWorldSpace = true;
        var laserShader = Shader.Find("HDRP/Unlit");
        Log.LogInfo($"[VRCamera] LaserShader 'HDRP/Unlit' found={laserShader != null}");
        if (laserShader != null)
        {
            var laserMat = new Material(laserShader);
            // Use a very bright HDR red so HDRP tonemapping/exposure doesn't crush it to black.
            // _ExposureWeight=0 makes the material exposure-independent (always renders at full intensity).
            var brightRed = new Color(8f, 0f, 0f, 1f);  // HDR — survives scene exposure compression
            laserMat.SetColor("_UnlitColor", brightRed);
            laserMat.SetFloat("_ExposureWeight", 0f);    // bypass scene exposure
            // Double-sided: invertCulling=true during camera render flips LineRenderer quad winding.
            // CullMode.Off ensures quads render from both sides regardless.
            laserMat.SetFloat("_DoubleSidedEnable", 1.0f);
            laserMat.SetInt("_CullMode",        0);  // CullMode.Off
            laserMat.SetInt("_CullModeForward", 0);
            _laserLine.material = laserMat;
        }
        _laserLine.enabled = false; // hidden until first valid pose

        Log.LogInfo($"[VRCamera] Rig built: {w}x{h} ARGB32");
    }

    // Search all active cameras for one that isn't one of ours.
    // Disables it so it doesn't render independently, and tracks its transform.
    private void TryFindGameCamera()
    {
        foreach (var cam in Camera.allCameras)
        {
            if (cam == _leftCam || cam == _rightCam) continue;
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
                GL.invertCulling = true;
                _leftCam.Render();
                _rightCam.Render();
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
                            if (cur == null || !cur.name.StartsWith("VRPatch_", StringComparison.Ordinal))
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
                    if (!isBg)
                    {
                        try { if (g.color != Color.white) g.color = Color.white; }
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

                // Diagnostic: log first few new elements per scan.
                if (diagCount < 5)
                {
                    Log.LogInfo($"[VRCamera] RescanNew '{canvasName}' '{nm}' origName='{orig.name}' " +
                                $"shader='{shaderName}' isText={isText} gAlpha={g.color.a:F2}");
                    diagCount++;
                }

                // matKey encodes: origId × boost-type × isBackground
                // boost-type: 0=image(3×), 1=text(30×), 2=bg(alpha), 3=additive(none)
                int  boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                int  origId    = orig.GetInstanceID();
                long matKey    = ((long)origId << 3) | (long)boostType;

                if (!s_uiZTestMats.TryGetValue(matKey, out var mat))
                {
                    mat = new Material(orig);
                    mat.name = "VRPatch_" + orig.name;
                    mat.SetInt("unity_GUIZTestMode", 8);

                    if (!isAdditive)
                    {
                        // Text draws at queue 3009 (highest) so it renders in front of button images.
                        mat.renderQueue = isText ? 3009 : (isBg ? 3000 : 3004);
                        Color c = mat.color;
                        if (isBg)
                        {
                            mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                        }
                        else if (isText)
                        {
                            // Full boost — text must visually stand out against button panels.
                            mat.color = new Color(c.r * UIColorBoost, c.g * UIColorBoost,
                                                  c.b * UIColorBoost, c.a);
                            try
                            {
                                if (mat.HasProperty("_FaceColor"))
                                {
                                    Color fc = mat.GetColor("_FaceColor");
                                    mat.SetColor("_FaceColor", new Color(
                                        fc.r * UIColorBoost, fc.g * UIColorBoost,
                                        fc.b * UIColorBoost, fc.a));
                                }
                                if (mat.HasProperty("_ExposureWeight"))
                                    mat.SetFloat("_ExposureWeight", 0f);
                            }
                            catch { }
                        }
                        else
                        {
                            // Image / button panel: moderate boost so it's visible but darker than text.
                            mat.color = new Color(c.r * UIImageBoost, c.g * UIImageBoost,
                                                  c.b * UIImageBoost, c.a);
                        }
                    }

                    s_uiZTestMats[matKey] = mat;
                }

                try { g.material = mat; } catch { continue; }

                // Mark as patched using the native pointer — immune to wrapper recreation.
                s_patchedGraphicPtrs.Add(ptr);
                s_patchedMats[ptr] = mat;
                newCount++;

                // Force vertex colour to white for all non-background elements.
                if (!isBg)
                {
                    try { if (g.color != Color.white) g.color = Color.white; }
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
        int count = 0;
        try
        {
            var graphics = canvas.GetComponentsInChildren<Graphic>(true);
            int total = Mathf.Max(graphics.Length, 1);

            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null) continue;
                Material orig;
                try   { orig = g.material; }
                catch { continue; }
                if (orig == null) continue;

                int origId = orig.GetInstanceID();

                // Spread graphics across render-queue tiers 0-9 based on hierarchy order.
                int  tier  = (total > 1) ? (i * 9 / (total - 1)) : 0;
                int  queue = 3000 + tier;

                // Classify each element: additive shader, background, text, or generic image.
                string nm         = g.gameObject.name;
                string shaderName = orig.shader?.name ?? "";
                bool isBg       = nm.IndexOf("background", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isAdditive = shaderName.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0
                               || shaderName.IndexOf("Add",      StringComparison.OrdinalIgnoreCase) >= 0;
                bool isText     = !isBg && !isAdditive && IsTextGraphic(g);

                // matKey encodes origId × boost-type so text/image clones of the same source
                // material are kept separate.  boostType: 0=image, 1=text, 2=bg, 3=additive.
                int  boostType = isAdditive ? 3 : (isBg ? 2 : (isText ? 1 : 0));
                long matKey    = ((long)origId << 3) | (long)boostType | ((long)tier << 4);

                if (!s_uiZTestMats.TryGetValue(matKey, out var mat))
                {
                    mat = new Material(orig);
                    // Stamp name so RescanCanvasAlpha can detect already-patched elements.
                    mat.name = "VRPatch_" + orig.name;
                    mat.SetInt("unity_GUIZTestMode", 8); // CompareFunction.Always

                    if (!isAdditive)
                    {
                        mat.renderQueue = queue;
                        Color c = mat.color;
                        if (isBg)
                        {
                            // Semi-transparent backdrop — lets scene show through and avoids
                            // HDRP distance-sort burying text behind a solid panel.
                            mat.color = new Color(c.r, c.g, c.b, UIBackgroundAlpha);
                        }
                        else if (isText)
                        {
                            // Full boost — text must visually stand out against button panels.
                            mat.color = new Color(c.r * UIColorBoost, c.g * UIColorBoost,
                                                  c.b * UIColorBoost, c.a);

                            // TextMeshPro uses _FaceColor as its primary colour.
                            try
                            {
                                bool hasFace = mat.HasProperty("_FaceColor");
                                Color fc     = hasFace ? mat.GetColor("_FaceColor") : Color.white;
                                if (count < 20)
                                    Log.LogInfo($"[VRCamera] MatNew '{canvas.gameObject.name}' [{i}]'{nm}' " +
                                                $"shader='{shaderName}' isText=true hasFace={hasFace} " +
                                                $"fc=({fc.r:F2},{fc.g:F2},{fc.b:F2},{fc.a:F2})");
                                if (hasFace)
                                    mat.SetColor("_FaceColor", new Color(
                                        fc.r * UIColorBoost, fc.g * UIColorBoost,
                                        fc.b * UIColorBoost, fc.a));
                                if (mat.HasProperty("_ExposureWeight"))
                                    mat.SetFloat("_ExposureWeight", 0f);
                            }
                            catch { /* some shaders don't expose these — safe to ignore */ }
                        }
                        else
                        {
                            // Non-text image / button panel: moderate boost — visible but darker
                            // than text so the text reads against it.
                            mat.color = new Color(c.r * UIImageBoost, c.g * UIImageBoost,
                                                  c.b * UIImageBoost, c.a);
                        }
                    }
                    // additive: keep original renderQueue and colours; only ZTest was set above.

                    s_uiZTestMats[matKey] = mat;
                }

                try { g.material = mat; }
                catch { continue; }

                // Mark this graphic as patched so RescanCanvasAlpha won't apply a second
                // 30× boost on top of this one.  g.Pointer is the native C++ object address —
                // stable for the object's lifetime, unlike GetInstanceID() or material.name.
                s_patchedGraphicPtrs.Add(g.Pointer);
                s_patchedMats[g.Pointer] = mat; // store so RescanCanvasAlpha can restore if reset

                // Force vertex colour to full white so the mat.color boost takes full effect.
                // UI/Default shader: fragment = vertex_color × _Color × texture.
                // Force vertex colour to white so mat.color boost is fully expressed.
                // Native vertex rgb tints can be very dark, silently cancelling the entire boost.
                if (!isBg)
                {
                    try
                    {
                        if (g.color != Color.white) g.color = Color.white;
                    }
                    catch { /* ignore — some graphics reject color changes */ }
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
                int t2 = Mathf.Max(graphics2.Length, 1);
                var sb = new System.Text.StringBuilder();
                sb.Append($"[VRCamera] QueueMap '{canvas.gameObject.name}' ({graphics2.Length}): ");
                int show = Mathf.Min(5, graphics2.Length);
                for (int di = 0; di < show; di++)
                {
                    if (graphics2[di] == null) continue;
                    int tier = (t2 > 1) ? (di * 9 / (t2 - 1)) : 0;
                    float ca = graphics2[di].color.a;
                    sb.Append($"[{di}]'{graphics2[di].gameObject.name}'=q{3000 + tier},a{ca:F2} ");
                }
                if (graphics2.Length > 10) sb.Append("... ");
                for (int di = Mathf.Max(5, graphics2.Length - 5); di < graphics2.Length; di++)
                {
                    if (graphics2[di] == null) continue;
                    int tier = (t2 > 1) ? (di * 9 / (t2 - 1)) : 0;
                    float ca = graphics2[di].color.a;
                    sb.Append($"[{di}]'{graphics2[di].gameObject.name}'=q{3000 + tier},a{ca:F2} ");
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
            _positionedCanvases.Add(kvp.Key); // mark first so null canvas doesn't retry

            var canvas = kvp.Value;
            if (canvas == null) continue;

            // Higher sortingOrder → slightly closer to the viewer (5 mm per step).
            float   zNudge = -canvas.sortingOrder * 0.005f;
            Vector3 pos    = headPos
                           + forward  * (UIDistance + zNudge)
                           + Vector3.up * UIVerticalOffset;

            canvas.transform.position = pos;
            canvas.transform.rotation = yawOnly;
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
            if (_laserLine != null) _laserLine.enabled = false;
            // Log every 120 frames until pose becomes valid
            if (!_poseEverValid && _poseFrameCount % 120 == 0)
                Log.LogInfo($"[VRCamera] Controller pose not valid (frame {_poseFrameCount}) — waiting");
            return;
        }

        if (!_poseEverValid)
        {
            _poseEverValid = true;
            Log.LogInfo($"[VRCamera] Controller pose FIRST VALID at frame {_poseFrameCount}: pos={pos} ori={ori}");
        }

        // OpenXR → Unity coord flip: pos.z *= -1; quat = (-x,-y,z,w)
        var uPos = new Vector3( pos.x,  pos.y, -pos.z);
        var uOri = new Quaternion(-ori.x, -ori.y, ori.z, ori.w);

        // Controller is in the same LOCAL reference space as the eye cameras.
        // _cameraOffset is at VROrigin identity, so world = VROrigin.TransformPoint(local).
        _rightControllerGO.transform.position = transform.TransformPoint(uPos);
        _rightControllerGO.transform.rotation = transform.rotation * uOri;

        // Update laser beam
        if (_laserLine != null)
        {
            _laserLine.enabled = true;
            Vector3 origin  = _rightControllerGO.transform.position;
            Vector3 forward = _rightControllerGO.transform.forward;
            _laserLine.SetPosition(0, origin);
            _laserLine.SetPosition(1, origin + forward * 3f);
            if (!_laserPosLogged)
            {
                _laserPosLogged = true;
                Log.LogInfo($"[VRCamera] Laser: origin={origin} fwd={forward} end={origin + forward * 3f}  camL={(_leftCam != null ? _leftCam.transform.position.ToString() : "null")}");
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
