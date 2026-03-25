using BepInEx.Logging;
using System;
using UnityEngine;

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

    // Per-frame state
    private long  _displayTime;
    private bool  _frameOpen;
    private bool  _stereoReady;   // true once swapchains created and rig built
    private int   _frameCount;
    private int   _locateErrors;
    private int   _waitFrameCount; // empty frames submitted while waiting for SYNCHRONIZED

    private OpenXRManager.EyePose _leftEye, _rightEye;
    private bool _posesValid;

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
    }

    private void BuildCameraRig()
    {
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

        // Disable existing main camera — keep its GameObject alive for game code
        // that does Camera.main checks; reparent under CameraOffset.
        var mc = Camera.main;
        if (mc != null && mc != _leftCam && mc != _rightCam)
        {
            Log.LogInfo($"[VRCamera] Disabling original camera: {mc.gameObject.name}");
            mc.enabled = false;
            mc.transform.SetParent(_cameraOffset, false);
            mc.transform.localPosition = Vector3.zero;
            mc.transform.localRotation = Quaternion.identity;
        }

        Log.LogInfo($"[VRCamera] Rig built: {w}x{h} ARGB32");
    }

    private static void SetupEyeCam(Camera cam, RenderTexture rt)
    {
        cam.targetTexture   = rt;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        cam.nearClipPlane   = 0.01f;
        cam.farClipPlane    = 1000f;
        cam.allowHDR        = false;  // LDR matches UNORM_SRGB swapchain format
        cam.enabled         = false;  // we call Render() explicitly in LateUpdate
    }

    // ── LateUpdate: render → copy → end frame ────────────────────────────────

    private void LateUpdate()
    {
        if (!_stereoReady || !_frameOpen) return;
        _frameOpen = false;

        try
        {
            if (!_posesValid)
            {
                OpenXRManager.FrameEndEmpty(_displayTime);
                return;
            }

            // Render both eyes into their RenderTextures.
            _leftCam.Render();
            _rightCam.Render();

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

    // ── Coordinate conversion ─────────────────────────────────────────────────

    // OpenXR: right-handed (+Y up, +X right, -Z forward)
    // Unity:  left-handed  (+Y up, +X right, +Z forward)
    // Conversion: flip Z on position, negate X and Z on quaternion.
    private static void ApplyCameraPose(Transform t, OpenXRManager.EyePose eye)
    {
        t.localPosition = new Vector3( eye.Position.x,
                                        eye.Position.y,
                                       -eye.Position.z);
        t.localRotation = new Quaternion(-eye.Orientation.x,
                                          eye.Orientation.y,
                                         -eye.Orientation.z,
                                          eye.Orientation.w);
    }

    // Off-centre perspective from OpenXR tangent-angle FOV.
    // angleLeft ≤ 0, angleRight ≥ 0, angleUp ≥ 0, angleDown ≤ 0.
    private static void SetProjection(Camera cam, OpenXRManager.EyePose eye)
    {
        float n = cam.nearClipPlane, f = cam.farClipPlane;
        float l = Mathf.Tan(eye.FovLeft)  * n;
        float r = Mathf.Tan(eye.FovRight) * n;
        float t = Mathf.Tan(eye.FovUp)    * n;
        float b = Mathf.Tan(eye.FovDown)  * n;
        cam.projectionMatrix = Matrix4x4.Frustum(l, r, b, t, n, f);
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void OnDestroy()
    {
        _stereoReady = false;
        if (_leftRT  != null) { _leftRT.Release();  Destroy(_leftRT);  }
        if (_rightRT != null) { _rightRT.Release(); Destroy(_rightRT); }
        Log.LogInfo("[VRCamera] Destroyed.");
    }
}
