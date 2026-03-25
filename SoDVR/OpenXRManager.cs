using BepInEx.Logging;
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace SoDVR;

/// <summary>
/// Bootstraps OpenXR via UnityOpenXR.dll — Unity's native XR plugin.
///
/// Root problem with XRSDKPreInit: UnityPluginLoad is never called on our loaded
/// copy of UnityOpenXR.dll, so the IUnityInterfaces* global inside the DLL is NULL.
/// fn_A (first call in XRSDKPreInit) reads that global → crash.
///
/// Fix:
///   1. Construct a fake IUnityInterfaces (stub vtable, stubs return NULL).
///   2. Call UnityPluginLoad(fakeInterfaces) to populate the DLL's global.
///   3. NOP-patch the fn_A call in XRSDKPreInit as belt-and-suspenders.
///   4. Call XRSDKPreInit — now runs without crashing.
///   5. CheckAndStart() tries SubsystemManager (Unity stereo path), then
///      falls back to direct xrCreateSession via openxr_loader.dll.
/// </summary>
public static class OpenXRManager
{
    private static ManualLogSource Log => Plugin.Log;
    public static bool IsRunning { get; private set; }

    // ── State ─────────────────────────────────────────────────────────────────

    private static IntPtr _hUnityOpenXR;
    private static IntPtr _gpaAddr;          // xrGetInstanceProcAddr from openxr_loader.dll

    // Direct-path fallback handles
    private static ulong _instance, _systemId, _session;
    private static IntPtr _pfnGetSystem, _pfnCreateSession, _pfnBeginSession,
                          _pfnDestroySession, _pfnDestroyInstance, _pfnPollEvent,
                          _pfnGetD3D11GfxReqs;
    // VDXR's internal xrCreateSession (obtained via xrNegotiateLoaderRuntimeInterface GPA
    // with instance=0x0).  GPA with instance=0x1 may return a wrapper in a different DLL
    // that crashes before reaching our patches; we call this direct function instead.
    private static IntPtr _pfnCreateSessionDirect;
    private static XrGetProcAddrDelegate? _gpa;
    private static bool _directReady;        // SetupDirect() completed
    private static bool _gfxReqsDone;        // xrGetD3D11GraphicsRequirementsKHR succeeded
    private static bool _robjWriteDone;      // SetRuntimeGraphicsRequirements wrote LUID into robj
    private static bool _robjDumped;         // robj dump emitted (not reset on retry)
    private static int  _subsystemRetries;   // frames we've tried subsystem path
    private static int  _directRetries;      // frames we've been in the direct retry loop
    private static int  _postSessionFrames;  // frames elapsed since xrCreateSession succeeded
    private static bool _unitySessTried;     // TryUnitySessionPath called (one-shot)
    /// <summary>Highest OpenXR session state seen via xrPollEvent (0=unknown, 1=IDLE, 2=READY, 3=SYNCHRONIZED, 4=VISIBLE, 5=FOCUSED).</summary>
    public static int HighestSessionState { get; private set; }

    // Frame-submission loop (background thread — keeps VD in VR streaming mode)
    private static IntPtr _pfnWaitFrame, _pfnBeginFrame, _pfnEndFrame;
    private static System.Threading.Thread? _frameThread;
    private static volatile bool _frameThreadRunning;

    // Stereo rendering (Phase 5)
    private static IntPtr _pfnCreateReferenceSpace, _pfnEnumViewConfigViews;
    private static IntPtr _pfnCreateSwapchain, _pfnEnumSwapchainImages, _pfnEnumSwapchainFormats;
    private static IntPtr _pfnAcquireSwapchainImage, _pfnWaitSwapchainImage, _pfnReleaseSwapchainImage;
    private static IntPtr _pfnLocateViews;
    public  static ulong   ReferenceSpace        { get; private set; }
    public  static ulong   LeftSwapchain         { get; private set; }
    public  static ulong   RightSwapchain        { get; private set; }
    public  static int     SwapchainWidth        { get; private set; }
    public  static int     SwapchainHeight       { get; private set; }
    public  static IntPtr[] LeftSwapchainImages  { get; private set; } = System.Array.Empty<IntPtr>();
    public  static IntPtr[] RightSwapchainImages { get; private set; } = System.Array.Empty<IntPtr>();
    public  static ulong   Session               => _session;
    public  static long    LastDisplayTime       { get; private set; }
    private static int     _stereoCallCount;     // counts FrameEndStereo invocations


    // ── Delegate types ────────────────────────────────────────────────────────

    // OpenXR
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrGetProcAddrDelegate(
        ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr fn);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateInstanceDelegate(IntPtr createInfo, out ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrDestroyInstanceDelegate(ulong instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrGetSystemDelegate(ulong instance, IntPtr getInfo, out ulong systemId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateSessionDelegate(ulong instance, IntPtr createInfo, out ulong session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrBeginSessionDelegate(ulong session, IntPtr beginInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrDestroySessionDelegate(ulong session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrPollEventDelegate(ulong instance, IntPtr eventData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrGetD3D11GfxReqsDelegate(ulong instance, ulong systemId, IntPtr reqs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetD3D11DeviceDelegate(IntPtr pResource, out IntPtr ppDevice);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEnumerateExtensionsDelegate(IntPtr layerName, uint capacityInput, out uint countOutput, IntPtr properties);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrNegotiateLoaderRuntimeDelegate(IntPtr loaderInfo, IntPtr runtimeRequest);
    // VDXR internals — called with 'this' in RCX (x64 thiscall == Cdecl with extra first arg)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VDXRSingletonDelegate();
    // DXGI COM vtable helpers — used to read the D3D11 adapter LUID for m_adapterLuid.
    // On x64 Windows __stdcall == __cdecl (Microsoft x64 ABI), so either convention works.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  COMQueryInterfaceDelegate(IntPtr pThis, IntPtr riid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint COMReleaseDelegate(IntPtr pThis);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  DXGIGetAdapterDelegate(IntPtr pThis, out IntPtr ppAdapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  DXGIGetDescDelegate(IntPtr pThis, IntPtr pDesc);
    // Frame-loop (xrWaitFrame blocks until runtime is ready for the next frame)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrWaitFrameDelegate(ulong session, IntPtr frameWaitInfo, IntPtr frameState);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrBeginFrameDelegate(ulong session, IntPtr frameBeginInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEndFrameDelegate(ulong session, IntPtr frameEndInfo);
    // Stereo rendering
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateReferenceSpaceDelegate(ulong session, IntPtr createInfo, out ulong space);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEnumViewConfigViewsDelegate(ulong instance, ulong systemId, int viewConfigType, uint cap, out uint count, IntPtr views);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrCreateSwapchainDelegate(ulong session, IntPtr createInfo, out ulong swapchain);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEnumSwapchainImagesDelegate(ulong swapchain, uint cap, out uint count, IntPtr images);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrAcquireSwapchainImageDelegate(ulong swapchain, IntPtr acquireInfo, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrWaitSwapchainImageDelegate(ulong swapchain, IntPtr waitInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrReleaseSwapchainImageDelegate(ulong swapchain, IntPtr releaseInfo);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrLocateViewsDelegate(ulong session, IntPtr viewLocateInfo, IntPtr viewState, uint viewCap, out uint viewCount, IntPtr views);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrEnumSwapchainFormatsDelegate(ulong session, uint cap, out uint count, IntPtr formats);
    // D3D11 vtable helpers
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void D3D11GetImmediateContextDelegate(IntPtr device, out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void D3D11CopyResourceDelegate(IntPtr context, IntPtr dst, IntPtr src);

    // UnityOpenXR.dll exports
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XRSDKPreInitDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetStage1Delegate(IntPtr procAddr);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetProcAddrPtrDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool UnitySessionBoolDelegate();

    // ── Win32 ─────────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [DllImport("kernel32.dll")] static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string p);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr hMod, string name);
    [DllImport("kernel32.dll")] static extern bool   VirtualProtect(IntPtr addr, UIntPtr sz, uint prot, out uint old);
    [DllImport("kernel32.dll")] static extern bool   FlushInstructionCache(IntPtr hProc, IntPtr addr, UIntPtr sz);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4: resolve the module that contains the given address
    [DllImport("kernel32.dll")] static extern bool   GetModuleHandleEx(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetModuleFileNameW(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr sz, uint type, uint prot);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Called in Initializer.Start() — sets up UnityOpenXR.dll and calls XRSDKPreInit.</summary>
    public static bool TryInitializeProvider()
    {
        try
        {
            LogActiveRuntime();
            string plugins = System.IO.Path.Combine(Application.dataPath, "Plugins/x86_64");

            // 1. Load openxr_loader.dll
            IntPtr hLoader = GetModuleHandle("openxr_loader");
            if (hLoader == IntPtr.Zero)
                hLoader = LoadLibraryW(System.IO.Path.Combine(plugins, "openxr_loader.dll"));
            if (hLoader == IntPtr.Zero) { Log.LogError("  openxr_loader.dll not found"); return false; }

            _gpaAddr = GetProcAddress(hLoader, "xrGetInstanceProcAddr");
            if (_gpaAddr == IntPtr.Zero) { Log.LogError("  xrGetInstanceProcAddr not found"); return false; }
            Log.LogInfo($"  xrGetInstanceProcAddr: 0x{_gpaAddr:X}");

            // 2. Load UnityOpenXR.dll
            IntPtr hXR = GetModuleHandle("UnityOpenXR");
            if (hXR == IntPtr.Zero) hXR = LoadLibraryW(System.IO.Path.Combine(plugins, "UnityOpenXR.dll"));
            if (hXR == IntPtr.Zero) { Log.LogError("  UnityOpenXR.dll not found"); return false; }
            Log.LogInfo($"  UnityOpenXR.dll: 0x{hXR:X}");
            _hUnityOpenXR = hXR;

            // 3. Feed the real xrGetInstanceProcAddr into the DLL.
            // UnityPluginLoad is skipped — it crashes in our context (accesses Unity engine
            // globals that only exist inside Unity's XR plugin lifecycle).
            IntPtr fnSetStage1 = GetProcAddress(hXR, "NativeConfig_SetProcAddressPtrAndLoadStage1");
            if (fnSetStage1 != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<SetStage1Delegate>(fnSetStage1)(_gpaAddr);

            // 4. NOP-patch the fn_A call at XRSDKPreInit+4 (belt-and-suspenders)
            IntPtr fnPreInit = GetProcAddress(hXR, "XRSDKPreInit");
            if (fnPreInit == IntPtr.Zero) { Log.LogError("  XRSDKPreInit export not found"); return false; }
            NopFnACall(fnPreInit);

            // 5. Call XRSDKPreInit
            int preInitRc = Marshal.GetDelegateForFunctionPointer<XRSDKPreInitDelegate>(fnPreInit)();
            Log.LogInfo($"  XRSDKPreInit rc={preInitRc}");

            return true;
        }
        catch (Exception ex) { Log.LogError($"TryInitializeProvider: {ex}"); return false; }
    }

    public static void TriggerLoad() { }

    /// <summary>
    /// Called each frame until it returns true.
    /// Path A: XRDisplaySubsystem via SubsystemManager (full Unity stereo rendering).
    /// Path B: Direct xrCreateSession via openxr_loader.dll (fallback).
    /// </summary>
    public static bool CheckAndStart()
    {
        try
        {
            if (IsRunning) return true; // already initialised — don't re-enter

            // Path A — try for up to 60 frames before giving up
            if (_subsystemRetries < 60)
            {
                _subsystemRetries++;
                if (TrySubsystemPath()) return true;
            }
            else if (_subsystemRetries == 60)
            {
                _subsystemRetries++;
                Log.LogInfo("  SubsystemManager path not available after 60 frames — using direct fallback.");
            }

            // Path B — direct xrCreateSession
            if (!_directReady) SetupDirect();
            if (!_directReady) return false;
            return TryDirectPath();
        }
        catch (Exception ex) { Log.LogError($"CheckAndStart: {ex}"); return false; }
    }

    public static void Shutdown()
    {
        try
        {
            // Signal the frame thread to exit and wait for it briefly.
            _frameThreadRunning = false;
            if (_frameThread != null)
            {
                _frameThread.Join(2000); // 2-second grace period
                _frameThread = null;
            }
            if (_session != 0 && _pfnDestroySession != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<XrDestroySessionDelegate>(_pfnDestroySession)(_session);
            if (_instance != 0 && _pfnDestroyInstance != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<XrDestroyInstanceDelegate>(_pfnDestroyInstance)(_instance);
            _session = 0; _instance = 0; _systemId = 0;
            IsRunning = false;
            Log.LogInfo("OpenXR shut down.");
        }
        catch (Exception ex) { Log.LogWarning($"Shutdown: {ex.Message}"); }
    }

    // ── Path A: Unity XR subsystem ────────────────────────────────────────────

    private static bool TrySubsystemPath()
    {
        try
        {
            var descriptors = new Il2CppSystem.Collections.Generic.List<XRDisplaySubsystemDescriptor>();
            SubsystemManager.GetSubsystemDescriptors<XRDisplaySubsystemDescriptor>(descriptors);
            if (descriptors.Count == 0) return false;

            Log.LogInfo($"  XRDisplaySubsystemDescriptor found: {descriptors[0].id}");
            var display = descriptors[0].Create();
            if (display == null) return false;

            if (!display.running) display.Start();
            if (!display.running) return false;

            Log.LogInfo("  XRDisplaySubsystem running — full Unity stereo path active.");
            IsRunning = true;
            return true;
        }
        catch (Exception ex)
        {
            // Expected on first frames or if IL2CPP stripped XR subsystem types
            if (_subsystemRetries == 1) Log.LogInfo($"  SubsystemManager: {ex.Message}");
            return false;
        }
    }

    // ── Path B-pre: Unity session path (one-shot) ────────────────────────────

    /// <summary>
    /// One-shot attempt to call UnityOpenXR.dll's session_InitializeSession so that
    /// VDXR's streaming infrastructure (including robj+0x990) gets populated through
    /// Unity's proper channel.  Always returns false — TryDirectPath drives the actual
    /// session loop regardless of whether this succeeds.
    /// </summary>
    private static void TryUnitySessionPath()
    {
        if (_unitySessTried || _hUnityOpenXR == IntPtr.Zero) return;
        _unitySessTried = true;
        Log.LogInfo("  [UnityPath] Calling session_InitializeSession via UnityOpenXR.dll...");
        try
        {
            IntPtr fnInit = GetProcAddress(_hUnityOpenXR, "session_InitializeSession");
            Log.LogInfo($"  [UnityPath] session_InitializeSession @ 0x{fnInit:X}");
            if (fnInit != IntPtr.Zero)
            {
                bool r = Marshal.GetDelegateForFunctionPointer<UnitySessionBoolDelegate>(fnInit)();
                Log.LogInfo($"  [UnityPath] session_InitializeSession → {r}");
            }

            IntPtr robj = GetVDXRRuntimeObject();
            if (robj != IntPtr.Zero)
                Log.LogInfo($"  [UnityPath] robj+0x990 after InitializeSession = 0x{Marshal.ReadIntPtr(robj, 0x990):X}");

            IntPtr fnCreate = GetProcAddress(_hUnityOpenXR, "session_CreateSessionIfNeeded");
            Log.LogInfo($"  [UnityPath] session_CreateSessionIfNeeded @ 0x{fnCreate:X}");
            if (fnCreate != IntPtr.Zero)
            {
                bool r = Marshal.GetDelegateForFunctionPointer<UnitySessionBoolDelegate>(fnCreate)();
                Log.LogInfo($"  [UnityPath] session_CreateSessionIfNeeded → {r}");
            }

            if (robj != IntPtr.Zero)
                Log.LogInfo($"  [UnityPath] robj+0x990 after CreateSessionIfNeeded = 0x{Marshal.ReadIntPtr(robj, 0x990):X}");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"  [UnityPath] {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Path B: Direct OpenXR session ─────────────────────────────────────────

    private static void SetupDirect()
    {
        try
        {
            Log.LogInfo("  Setting up direct OpenXR session fallback...");
            _gpa = Marshal.GetDelegateForFunctionPointer<XrGetProcAddrDelegate>(_gpaAddr);

            EnumerateExtensions();

            int rc = CreateInstance();
            if (rc != 0 || _instance == 0) { Log.LogError($"  xrCreateInstance rc={rc}"); return; }
            Log.LogInfo($"  xrInstance=0x{_instance:X}");

            GetFn("xrGetSystem",                       out _pfnGetSystem);
            GetFn("xrCreateSession",                   out _pfnCreateSession);
            GetFn("xrBeginSession",                    out _pfnBeginSession);
            GetFn("xrDestroySession",                  out _pfnDestroySession);
            GetFn("xrDestroyInstance",                 out _pfnDestroyInstance);
            GetFn("xrPollEvent",                       out _pfnPollEvent);
            GetFn("xrGetD3D11GraphicsRequirementsKHR", out _pfnGetD3D11GfxReqs);
            GetFn("xrWaitFrame",                       out _pfnWaitFrame);
            GetFn("xrBeginFrame",                      out _pfnBeginFrame);
            GetFn("xrEndFrame",                        out _pfnEndFrame);
            GetFn("xrCreateReferenceSpace",            out _pfnCreateReferenceSpace);
            GetFn("xrEnumerateViewConfigurationViews", out _pfnEnumViewConfigViews);
            GetFn("xrCreateSwapchain",                 out _pfnCreateSwapchain);
            GetFn("xrEnumerateSwapchainImages",        out _pfnEnumSwapchainImages);
            GetFn("xrEnumerateSwapchainFormats",       out _pfnEnumSwapchainFormats);
            GetFn("xrAcquireSwapchainImage",           out _pfnAcquireSwapchainImage);
            GetFn("xrWaitSwapchainImage",              out _pfnWaitSwapchainImage);
            GetFn("xrReleaseSwapchainImage",           out _pfnReleaseSwapchainImage);
            GetFn("xrLocateViews",                     out _pfnLocateViews);


            // Patch the xrCreateSession MEMBER function (vtable slot 12) to bypass the
            // -38 (XR_ERROR_GRAPHICS_REQUIREMENTS_CALL_MISSING) guard.
            // Also dump its first 0x300 bytes to find what sets robj+0x990 (checked by SC-member).
            unsafe
            {
                IntPtr robj = GetVDXRRuntimeObject();
                if (robj != IntPtr.Zero)
                {
                    IntPtr* vtable = *(IntPtr**)robj;
                    IntPtr csMember = vtable[0x060 / 8]; // slot 12 = xrCreateSession member
                    ScanAndPatchMinus38(csMember, 512, "CS-member");
                    ScanAndPatchErrorCode(csMember, 512, "CS-member-v2", -2);

                    // Dump CS-member bytes to find write to [robj+0x990]
                    Log.LogInfo($"  CS-member @ 0x{csMember:X} (vtable slot 12)");
                    byte* csb = (byte*)csMember;
                    for (int ci = 0; ci < 0x300; ci += 16)
                    {
                        var csb2 = new System.Text.StringBuilder($"  CS+0x{ci:X3}:");
                        for (int cj = 0; cj < 16; cj++) csb2.Append($" {csb[ci+cj]:X2}");
                        Log.LogInfo(csb2.ToString());
                    }
                }
                else
                {
                    Log.LogWarning("  SetupDirect: runtime object null — cannot patch CS-member");
                }
            }

            // CS-loader JBE at +0x4C: cmp counter,5; jbe guard. NOP to fall through.
            PatchBytes(_pfnCreateSession, 0x4C, new byte[]{0x76, 0x1B}, new byte[]{0x90, 0x90}, "CS-jbe");

            // GR-outer has two JBEs checking the same "requirements-set" counter.
            // +0x3D: first  JBE (2 bytes): counter≤5 → skip the singleton-lookup call
            // +0x6F: second JBE (6 bytes): counter≤5 → bail to +0x143 (skip OVR setup call)
            // +0x77: JZ    (2 bytes): singleton-lookup returned AL=0 → bail to +0x09C
            // NOP all three so GR-outer falls through to the OVR setup call at +0x13C.
            PatchBytes(_pfnGetD3D11GfxReqs, 0x3D,
                new byte[]{0x76, 0x1D},
                new byte[]{0x90, 0x90}, "GR-jbe");
            PatchBytes(_pfnGetD3D11GfxReqs, 0x6F,
                new byte[]{0x0F, 0x86, 0xCE, 0x00, 0x00, 0x00},
                new byte[]{0x90, 0x90, 0x90, 0x90, 0x90, 0x90}, "GR-jbe2");
            PatchBytes(_pfnGetD3D11GfxReqs, 0x77,
                new byte[]{0x74, 0x23},
                new byte[]{0x90, 0x90}, "GR-jz");

            // GR-outer+0x15B: CALL R10 (vtable slot 0x1E0 = instance-handle validation).
            // vtable[60] returns -1 when called with any combination of args we can provide.
            // Patch it out with a fake stub that fills the XrGraphicsRequirementsD3D11KHR
            // output struct (R9) with a plausible LUID/feature-level and returns 0.
            // The D3D11 backend is initialised independently via CallInitD3D11Direct before
            // xrCreateSession, so this stub's only job is to satisfy the output-struct read.
            PatchVtableDispatch(_pfnGetD3D11GfxReqs, "GR-vtable",
                patchOffset: 0x15B, nextRipOffset: 0x160, fillReqsStruct: true);
            // Dump GR-member (vtable[60]) first 0x180 bytes (diagnostic — can remove once confirmed working).
            unsafe
            {
                IntPtr robj = GetVDXRRuntimeObject();
                if (robj != IntPtr.Zero) {
                    IntPtr* vtbl = *(IntPtr**)robj;
                    IntPtr grMember = vtbl[0x1E0 / 8];  // slot 60 = GR-member
                    Log.LogInfo($"  GR-member @ 0x{grMember:X} (vtable slot 60)");
                    byte* grb = (byte*)grMember;
                    for (int gi = 0; gi < 0x180; gi += 16) {
                        var gsb = new System.Text.StringBuilder($"  GR+0x{gi:X3}:");
                        for (int gj = 0; gj < 16; gj++) gsb.Append($" {grb[gi+gj]:X2}");
                        Log.LogInfo(gsb.ToString());
                    }
                }
            }

            // xrCreateSwapchain — same 3-guard structure as GR-outer, same offsets.
            PatchBytes(_pfnCreateSwapchain, 0x3D,
                new byte[]{0x76, 0x1D},
                new byte[]{0x90, 0x90}, "SC-jbe1");
            PatchBytes(_pfnCreateSwapchain, 0x6F,
                new byte[]{0x0F, 0x86, 0xCE, 0x00, 0x00, 0x00},
                new byte[]{0x90, 0x90, 0x90, 0x90, 0x90, 0x90}, "SC-jbe2");
            PatchBytes(_pfnCreateSwapchain, 0x77,
                new byte[]{0x74, 0x23},
                new byte[]{0x90, 0x90}, "SC-jz");

            // xrCreateSwapchain member function (SC-member): find via vtable slot encoded at
            // SC-outer+0x14B (same pattern as GR-outer), then patch out the -8 format check.
            // Pattern at SC-outer+0x14B: 4C 8B 91 <disp32> = MOV R10,[RCX+disp32]
            unsafe
            {
                IntPtr robj = GetVDXRRuntimeObject();
                if (robj != IntPtr.Zero && _pfnCreateSwapchain != IntPtr.Zero)
                {
                    byte* scOuter = (byte*)_pfnCreateSwapchain;
                    if (scOuter[0x14B] == 0x4C && scOuter[0x14C] == 0x8B && scOuter[0x14D] == 0x91)
                    {
                        int vtabOff = *(int*)(scOuter + 0x14E);
                        IntPtr* vtable = *(IntPtr**)robj;
                        IntPtr scMember = vtable[vtabOff / 8];
                        Log.LogInfo($"  SC-member @ 0x{scMember:X} (vtable+0x{vtabOff:X} slot={vtabOff/8})");
                        // Revert the wrong JNE→JMP patch at +0x1FC (jumped to -12 error path).
                        // The JNE at +0x1FC is a session-validity guard (R14==1 means don't jump).
                        // ScanAndPatchErrorCode incorrectly targeted this instead of the real -8 guard.
                        PatchBytes(scMember, 0x1FC,
                            new byte[]{0xE9, 0xE4, 0x06, 0x00, 0x00, 0x90},  // current wrong JMP
                            new byte[]{0x0F, 0x85, 0xE3, 0x06, 0x00, 0x00},  // original JNE
                            "SC-revert-jne");
                        // Patch the real -8 guard: JZ at +0x20A (short 74 0A) → unconditional JMP.
                        // Guard: CMP [robj+0x2F2],0 / JZ +0x216 / MOV EAX,-8
                        // robj+0x2F2 is non-zero → JZ not taken → -8 returned.
                        // Patch JZ→JMP so we always jump to +0x216 (the creation path).
                        PatchBytes(scMember, 0x20A,
                            new byte[]{0x74, 0x0A},  // JZ +10
                            new byte[]{0xEB, 0x0A},  // JMP +10 (always skip -8)
                            "SC-skip-minus8");
                        // SC-member+0x242: JNE +0x3A9 targets MOV EAX,-26 (error path for compositor
                        // mode which VDXR does not support).  Do NOT patch this JNE — always leave
                        // it as-is so the streaming path (JNE not taken, AL==0x0F) runs.
                        // The streaming path at +0x267 checks [robj+0x990]; that field is populated
                        // by CSF when it runs fully — see CSF-skip-earlyrtn in CallCompositorSetupDirect.
                        // Dump SC+0x230..+0x260 to confirm instruction layout around the JNE.
                        // Dump SC+0x3A0..+0x480 to verify the compositor path entry at +0x3A9.
                        unsafe {
                            byte* scb = (byte*)scMember;
                            // Full streaming-path dump: +0x230..+0x3B0
                            for (int si = 0x230; si < 0x3B0; si += 16) {
                                var ssb = new System.Text.StringBuilder($"  SC+0x{si:X3}:");
                                for (int sj = 0; sj < 16; sj++) ssb.Append($" {scb[si+sj]:X2}");
                                Log.LogInfo(ssb.ToString());
                            }
                        }
                    }
                    else
                        Log.LogWarning($"  SC-outer+0x14B: {scOuter[0x14B]:X2} {scOuter[0x14C]:X2} {scOuter[0x14D]:X2} — pattern not 4C 8B 91, cannot find SC-member");
                }
            }

            // ── xrEndFrame inner function: find via vtable, dump, and scan for -8 ──
            unsafe
            {
                IntPtr robj = GetVDXRRuntimeObject();
                if (robj != IntPtr.Zero && _pfnEndFrame != IntPtr.Zero)
                {
                    byte* efOuter = (byte*)_pfnEndFrame;
                    // Scan EF-outer for 4C 8B [89|91|81|99] disp32 = MOV R9/R10/R8/R11,[RCX+disp32]
                    // EF-outer uses 4C 8B 89 18 01 00 00 = MOV R9,[RCX+0x118] → vtable[35]
                    IntPtr efMember = IntPtr.Zero;
                    for (int i = 0x10; i < 0x1E0; i++)
                    {
                        if (efOuter[i] == 0x4C && efOuter[i+1] == 0x8B &&
                            (efOuter[i+2] == 0x89 || efOuter[i+2] == 0x91 ||
                             efOuter[i+2] == 0x81 || efOuter[i+2] == 0x99))
                        {
                            int off = *(int*)(efOuter + i + 3);
                            if (off <= 0 || off > 0x800) continue;
                            IntPtr* vtable = *(IntPtr**)robj;
                            efMember = vtable[off / 8];
                            Log.LogInfo($"  EF-member @ 0x{efMember:X} (vtable+0x{off:X} slot={off/8})");
                            break;
                        }
                    }
                    if (efMember != IntPtr.Zero)
                    {
                        // Revert the previously-applied bad JZ→JMP at +0x11D.
                        // That JZ guards (robj+0x38C==8) and targets +0x16DE (error/cleanup).
                        // Making it unconditional sends every call there → -16 SESSION_NOT_RUNNING.
                        PatchBytes(efMember, 0x11D,
                            new byte[]{0xE9, 0xBB, 0x15, 0x00, 0x00, 0x90},  // wrong JMP (was applied last run)
                            new byte[]{0x0F, 0x84, 0xBB, 0x15, 0x00, 0x00},  // original JZ
                            "EF-revert-jz");
                        // Patch the real -8 guard: JZ at +0x12A (74 10) → unconditional JMP.
                        // Guard: CMP [robj+0x2F2], R15B(=0) / JZ +0x13C / ... / MOV EAX,-8
                        // robj+0x2F2 is non-zero (same flag as SC-member). Patch JZ→JMP to
                        // always jump to +0x13C (the success/continue path).
                        PatchBytes(efMember, 0x12A,
                            new byte[]{0x74, 0x10},  // JZ +16
                            new byte[]{0xEB, 0x10},  // JMP +16
                            "EF-skip-minus8");
                        // Scan for XR_ERROR_VALIDATION_FAILURE (-1) returns in the layer
                        // processing section (starts at EF-member+0x16E after the header checks).
                        // Start the scan at +0x160 (past all early guards) so we don't
                        // accidentally hit the type check at +0x04E.
                        // ScanAndPatchErrorCode has a hard-coded inner start of +0x10, so the
                        // effective scan begins at efMember+0x170.
                        ScanAndPatchErrorCode(efMember + 0x160, 0x2000, "EF-minus1", -1);
                    }
                }
            }

            // xrEnumerateSwapchainFormats — same structure but prologue is 4 bytes longer
            // (extra PUSH R14), so all guard offsets shift by +4.
            PatchBytes(_pfnEnumSwapchainFormats, 0x41,
                new byte[]{0x76, 0x1D},
                new byte[]{0x90, 0x90}, "ESF-jbe1");
            PatchBytes(_pfnEnumSwapchainFormats, 0x73,
                new byte[]{0x0F, 0x86, 0xCE, 0x00, 0x00, 0x00},
                new byte[]{0x90, 0x90, 0x90, 0x90, 0x90, 0x90}, "ESF-jbe2");
            PatchBytes(_pfnEnumSwapchainFormats, 0x7B,
                new byte[]{0x74, 0x23},
                new byte[]{0x90, 0x90}, "ESF-jz");

            // xrEnumerateSwapchainImages — same prologue length as ESF, same guard offsets.
            PatchBytes(_pfnEnumSwapchainImages, 0x41,
                new byte[]{0x76, 0x1D},
                new byte[]{0x90, 0x90}, "ESI-jbe1");
            PatchBytes(_pfnEnumSwapchainImages, 0x73,
                new byte[]{0x0F, 0x86, 0xCE, 0x00, 0x00, 0x00},
                new byte[]{0x90, 0x90, 0x90, 0x90, 0x90, 0x90}, "ESI-jbe2");
            PatchBytes(_pfnEnumSwapchainImages, 0x7B,
                new byte[]{0x74, 0x23},
                new byte[]{0x90, 0x90}, "ESI-jz");

            rc = XrGetSystem();
            if (rc != 0 || _systemId == 0) { Log.LogError($"  xrGetSystem rc={rc}"); return; }
            Log.LogInfo($"  xrSystemId=0x{_systemId:X}");

            _directReady = true;
        }
        catch (Exception ex) { Log.LogError($"  SetupDirect: {ex}"); }
    }

    private static unsafe bool TryDirectPath()
    {
        // xrGetD3D11GraphicsRequirementsKHR: called each frame until satisfied.
        // If still failing after 180 frames (~3s) we proceed anyway, relying on the
        // -38 patch in xrCreateSession's member function to bypass the requirements check.
        if (_session == 0)
        {
            _directRetries++;

            IntPtr dev = GetD3D11Device();
            if (dev == IntPtr.Zero)
            {
                if ((_directRetries % 60) == 1) Log.LogInfo("  D3D11 device not ready yet...");
                return false;
            }

            if (!_gfxReqsDone)
            {
                int gfxRc = CallD3D11GraphicsRequirements();
                _gfxReqsDone = true;
                if (gfxRc == 0)
                    Log.LogInfo("  xrGetD3D11GraphicsRequirementsKHR succeeded.");
                else
                    Log.LogWarning($"  xrGetD3D11GraphicsRequirementsKHR failed (rc={gfxRc}) — will use direct write.");
            }

            // Pre-initialize robj BEFORE xrCreateSession.
            // CS-member's loop at +0x0E4 checks [createInfo->next].type == 0x3B9B3378 (D3D11).
            // When it matches, JE jumps to +0x140 (D3D11 init block):
            //   +0x144 checks graphicsReqQueried (robj+0x60) — must be 1 or we hit -50.
            //   +0x149 calls initD3D11(robj, binding) — stores device/context in robj+0x940/948.
            //   +0x1AA calls compositor setup (robj) — expected to populate robj-relative state
            //              needed by SC-member's [R15+0x990] null check.
            // We call initD3D11 and compositor setup directly first so the state is available
            // even if the CS-member path behaves differently on second call.
            if (!_robjWriteDone)
            {
                SetRuntimeGraphicsRequirements(dev);  // writes graphicsReqQueried=1 + LUID to robj
                CallInitD3D11Direct(dev);              // stores D3D11 device/context in robj+0x940/948
                CallCompositorSetupDirect();           // calls CS-member+0x1AA compositor init with robj
            }

            int rc = XrCreateSession(dev);
            if (rc != 0)
            {
                if ((_directRetries % 300) == 1)
                    Log.LogWarning($"  xrCreateSession rc={rc} — retrying (attempt {_directRetries})");
                _gfxReqsDone = false;
                return false;
            }
            if (_session == 0)
            {
                if ((_directRetries % 300) == 1)
                    Log.LogWarning("  xrCreateSession rc=0 but session=0 — retrying");
                _gfxReqsDone = false;
                return false;
            }
            Log.LogInfo($"  xrSession=0x{_session:X}");

            // Diagnostic: inspect format-list state in VDXR runtime object.
            // Format list is populated by CS-member's +0x1D7 path during xrCreateSession.
            // robj+0x4A8 = likely list count/size, robj+0x4B0 = pointer to format array.
            unsafe
            {
                IntPtr dr = GetVDXRRuntimeObject();
                if (dr != IntPtr.Zero)
                {
                    long v4A8 = Marshal.ReadInt64(dr, 0x4A8);
                    IntPtr v4B0 = Marshal.ReadIntPtr(dr, 0x4B0);
                    long v4B8 = Marshal.ReadInt64(dr, 0x4B8);
                    int v388 = Marshal.ReadInt32(dr, 0x388);
                    int v2F2 = Marshal.ReadInt32(dr, 0x2F2);
                    int v38C = Marshal.ReadInt32(dr, 0x38C);
                    byte v3C1 = Marshal.ReadByte(dr, 0x3C1);
                    Log.LogInfo($"  Post-CS robj+0x388=0x{v388:X}  +0x38C=0x{v38C:X}  +0x3C1=0x{v3C1:X}  +0x2F2=0x{v2F2:X}  +0x4B0=0x{v4B0:X}");
                    // Scan robj+0x940..0x9C0: D3D11 device and related pointers
                    var d3dSb = new System.Text.StringBuilder("  Post-CS robj D3D11 slots:");
                    for (int dx = 0x940; dx <= 0x9C0; dx += 8)
                        d3dSb.Append($" +0x{dx:X}=0x{Marshal.ReadInt64(dr, dx):X}");
                    Log.LogInfo(d3dSb.ToString());
                    // Wider scan: robj+0x100..0x2000, non-zero values only
                    var wideSb = new System.Text.StringBuilder("  Post-CS robj nonzero(0x100-0x2000):");
                    for (int wx = 0x100; wx <= 0x2000; wx += 8) {
                        long wv = Marshal.ReadInt64(dr, wx);
                        if (wv != 0) wideSb.Append($" +0x{wx:X}=0x{wv:X}");
                    }
                    Log.LogInfo(wideSb.ToString());
                    if (v4B0 != IntPtr.Zero)
                    {
                        // Dump raw bytes of the format-list object to understand its structure.
                        var raw = new System.Text.StringBuilder("  FmtObj raw:");
                        for (int fi = 0; fi < 8; fi++)
                            raw.Append($" [{fi*8:X}]={Marshal.ReadInt64(v4B0, fi * 8):X}");
                        Log.LogInfo(raw.ToString());
                        // Attempt int32 scan (in case direct array of DXGI formats).
                        var fsb = new System.Text.StringBuilder("  FmtObj int32[0..15]:");
                        for (int fi = 0; fi < 16; fi++)
                            fsb.Append($" {Marshal.ReadInt32(v4B0, fi * 4)}");
                        Log.LogInfo(fsb.ToString());
                    }
                }
            }

            // Poll for any initial state events that arrived with session creation.
            PollEvents();

            // Call xrBeginSession immediately.
            // VDXR accepts this even before the READY event; the forced test at
            // frame 300 confirmed rc=0.  We no longer need the 300-frame delay.
            int rcBegin = XrBeginSession();
            Log.LogInfo($"  xrBeginSession rc={rcBegin}");
            if (rcBegin != 0 && rcBegin != -12) // -12 = XR_ERROR_SESSION_NOT_READY (retry next frame)
            {
                Log.LogError($"  xrBeginSession fatal rc={rcBegin} — resetting session.");
                _session = 0; _gfxReqsDone = false;
                return false;
            }

            if (rcBegin == -12)
            {
                Log.LogInfo("  xrBeginSession returned SESSION_NOT_READY — will retry when READY event arrives.");
                // Fall through to the event-polling loop below.
            }
            else
            {
                // rc == 0: session begun.  Start the frame thread so VD sees activity
                // and switches from desktop to VR streaming mode.
                StartFrameThread();
                Log.LogInfo("  Direct OpenXR session running.");
                IsRunning = true;
                return true;
            }
        }

        // If xrBeginSession returned SESSION_NOT_READY we reach here each frame.
        // Poll for STATE_CHANGED events and retry xrBeginSession on READY (state=2).
        _postSessionFrames++;
        int state = PollEvents();
        if (state > HighestSessionState) { HighestSessionState = state; Log.LogInfo($"  XR state → {state}"); }

        if (state == 2) // XR_SESSION_STATE_READY
        {
            int rcBegin2 = XrBeginSession();
            Log.LogInfo($"  xrBeginSession on READY rc={rcBegin2}");
            if (rcBegin2 == 0)
            {
                StartFrameThread();
                Log.LogInfo("  Direct OpenXR session running (READY path).");
                IsRunning = true;
                return true;
            }
        }

        return false;
    }

    // ── NOP patch ─────────────────────────────────────────────────────────────

    private static unsafe void NopFnACall(IntPtr preInitFn)
    {
        try
        {
            byte* target = (byte*)preInitFn + 4;
            if (target[0] != 0xE8)
            {
                Log.LogWarning($"  NOP patch: byte at +4 is 0x{target[0]:X2}, expected E8 — skipping");
                return;
            }
            if (!VirtualProtect((IntPtr)target, (UIntPtr)5, 0x40 /*PAGE_EXECUTE_READWRITE*/, out uint oldProt))
            {
                Log.LogWarning("  NOP patch: VirtualProtect failed");
                return;
            }
            target[0] = target[1] = target[2] = target[3] = target[4] = 0x90;
            VirtualProtect((IntPtr)target, (UIntPtr)5, oldProt, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)target, (UIntPtr)5);
            Log.LogInfo("  NOP patch applied at XRSDKPreInit+4 (fn_A call removed).");
        }
        catch (Exception ex) { Log.LogWarning($"  NOP patch: {ex.Message}"); }
    }

    // ── OpenXR direct calls ───────────────────────────────────────────────────

    private static unsafe int CreateInstance()
    {
        IntPtr extStr = Marshal.StringToHGlobalAnsi("XR_KHR_D3D11_enable");
        IntPtr extArr = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(extArr, extStr);

        const int sz = 328;
        IntPtr p = Marshal.AllocHGlobal(sz);
        for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 3);                            // XR_TYPE_INSTANCE_CREATE_INFO
        byte[] an = Encoding.ASCII.GetBytes("ShadowsOfDoubt");
        for (int i = 0; i < an.Length; i++) Marshal.WriteByte(p, 24 + i, an[i]);
        Marshal.WriteInt32(p, 152, 1);
        byte[] en = Encoding.ASCII.GetBytes("Unity");
        for (int i = 0; i < en.Length; i++) Marshal.WriteByte(p, 156 + i, en[i]);
        Marshal.WriteInt64(p, 288, (long)(1UL << 48));          // apiVersion = 1.0.0
        Marshal.WriteInt32(p, 312, 1);                          // extensionCount
        Marshal.WriteIntPtr(p, 320, extArr);                    // extensionNames

        _gpa!(0, "xrCreateInstance", out IntPtr fn);
        int rc = Marshal.GetDelegateForFunctionPointer<XrCreateInstanceDelegate>(fn)(p, out _instance);
        Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(extArr); Marshal.FreeHGlobal(extStr);
        return rc;
    }

    private static int XrGetSystem()
    {
        const int sz = 24;
        IntPtr p = Marshal.AllocHGlobal(sz);
        for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 4);  // XR_TYPE_SYSTEM_GET_INFO
        Marshal.WriteInt32(p, 16, 1); // XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY
        int rc = Marshal.GetDelegateForFunctionPointer<XrGetSystemDelegate>(_pfnGetSystem)(_instance, p, out _systemId);
        Marshal.FreeHGlobal(p);
        return rc;
    }

    /// <summary>
    /// Calls xrGetD3D11GraphicsRequirementsKHR via _pfnGetD3D11GfxReqs (VDXR's outer
    /// function) with several candidate instance handles.
    ///
    /// The OpenXR loader returns VDXR's function pointer directly for extension functions
    /// (no loader trampoline), so the instance handle must be VDXR's internal handle, not
    /// the loader's handle.  VDXR commonly uses the OpenXrRuntime* object address as the
    /// instance handle.  We try: loader handle, runtime object ptr, and small integers.
    ///
    /// Returns 0 on success, or the last non-zero rc if all candidates fail.
    /// </summary>
    private static int CallD3D11GraphicsRequirements()
    {
        if (_pfnGetD3D11GfxReqs == IntPtr.Zero || _systemId == 0) return -1;
        const int sz = 32;
        IntPtr p = Marshal.AllocHGlobal(sz);
        try
        {
            // Log the VDXR internal "requirements-set" counter that GR-outer guards behind.
            // Instruction at fn+0x034: 8B 0D [disp32] = MOV ECX,[RIP+disp32]
            // Counter address = fn + 0x034 + 6 + disp32
            unsafe {
                byte* fn = (byte*)_pfnGetD3D11GfxReqs;
                int disp = *(int*)(fn + 0x034 + 2);
                int* counterPtr = (int*)(fn + 0x034 + 6 + disp);
                Log.LogInfo($"  GR-counter @ 0x{(long)counterPtr:X} = {*counterPtr}");
            }

            // Candidate instance handles: loader handle, runtime object ptr (VDXR often uses
            // the object address as the XrInstance value), and small sequential handles.
            IntPtr robj = GetVDXRRuntimeObject();
            ulong[] candidates = {
                _instance,                         // loader-level handle (=1 typically)
                robj == IntPtr.Zero ? 0 : (ulong)(long)robj,  // runtime object ptr
                0, 1, 2, 3
            };

            int lastRc = -1;
            foreach (ulong inst in candidates)
            {
                for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
                Marshal.WriteInt32(p, 0, 0x3B9B337A); // VDXR internal type for XrGraphicsRequirementsD3D11KHR
                int rc = Marshal.GetDelegateForFunctionPointer<XrGetD3D11GfxReqsDelegate>
                             (_pfnGetD3D11GfxReqs)(inst, _systemId, p);
                Log.LogInfo($"  CallD3D11GfxReqs: inst=0x{inst:X} rc={rc}");
                if (rc == 0)
                {
                    int luidLo = Marshal.ReadInt32(p, 16);
                    int luidHi = Marshal.ReadInt32(p, 20);
                    int fl     = Marshal.ReadInt32(p, 24);
                    Log.LogInfo($"  CallD3D11GfxReqs: luid=0x{luidHi:X8}{luidLo:X8}  minFL=0x{fl:X}  ✓");
                    return 0;
                }
                lastRc = rc;
            }
            return lastRc;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>
    /// Directly writes m_graphicsRequirementQueried=true and m_adapterLuid into the VDXR
    /// runtime object, simulating what a successful xrGetD3D11GraphicsRequirementsKHR call
    /// would do.  This allows xrCreateSession to pass its -38 guard and proceed through
    /// the D3D11 binding chain walk and initializeD3D11().
    ///
    /// VDXR runtime object layout (from openxr-runtime.h):
    ///   +64 : m_graphicsRequirementQueried  (bool, 1 byte)
    ///   +65–71: padding (7 bytes for 8-byte alignment)
    ///   +72 : m_adapterLuid                (LUID = 8 bytes)
    /// </summary>

    // initD3D11(OpenXrRuntime* this, XrGraphicsBindingD3D11KHR* binding)
    // binding+0x10 = ID3D11Device*
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitD3D11Delegate(IntPtr robj, IntPtr binding);

    /// <summary>
    /// Finds initD3D11 via the CALL displacement at CS-member+0x149 and calls it
    /// with robj + a minimal XrGraphicsBindingD3D11KHR containing Unity's D3D11 device.
    /// This registers swapchain formats without needing graphicsReqQueried=1 before CS.
    /// </summary>
    private static unsafe void CallInitD3D11Direct(IntPtr d3dDevice)
    {
        try
        {
            IntPtr robj = GetVDXRRuntimeObject();
            if (robj == IntPtr.Zero) { Log.LogWarning("  CallInitD3D11: robj null"); return; }

            IntPtr* vtable = *(IntPtr**)robj;
            IntPtr csMember = vtable[0x060 / 8];  // slot 12

            byte* cs = (byte*)csMember;
            if (cs[0x149] != 0xE8)
            {
                Log.LogWarning($"  CallInitD3D11: expected E8 at CS+0x149, got 0x{cs[0x149]:X2} — skipping");
                return;
            }
            int disp = *(int*)(cs + 0x14A);
            IntPtr initD3D11 = (IntPtr)((long)csMember + 0x14E + disp);
            Log.LogInfo($"  CallInitD3D11: initD3D11 @ 0x{initD3D11:X}");

            // Build a minimal XrGraphicsBindingD3D11KHR (24 bytes):
            //   +0x00: type  = 1000003000 (XR_TYPE_GRAPHICS_BINDING_D3D11_KHR)
            //   +0x08: next  = NULL
            //   +0x10: device = Unity's ID3D11Device*
            const int bindSz = 24;
            IntPtr bind = Marshal.AllocHGlobal(bindSz);
            for (int i = 0; i < bindSz; i++) Marshal.WriteByte(bind, i, 0);
            Marshal.WriteInt32(bind, 0, 0x3B9B3378);  // VDXR internal type for XrGraphicsBindingD3D11KHR
            Marshal.WriteIntPtr(bind, 16, d3dDevice);

            Log.LogInfo($"  CallInitD3D11: d3dDevice=0x{d3dDevice:X} bind=0x{bind:X} bind+16=0x{Marshal.ReadInt64(bind, 16):X}");
            var fn = Marshal.GetDelegateForFunctionPointer<InitD3D11Delegate>(initD3D11);
            int rc = fn(robj, bind);
            Marshal.FreeHGlobal(bind);
            Log.LogInfo($"  CallInitD3D11: rc={rc}");
            if (rc == 0)
            {
                // Verify device pointer was stored at robj+0x940
                IntPtr stored = Marshal.ReadIntPtr(robj, 0x940);
                Log.LogInfo($"  CallInitD3D11: robj+0x940=0x{stored:X} (match={stored == d3dDevice})");
            }
        }
        catch (Exception ex) { Log.LogWarning($"  CallInitD3D11: {ex.Message}"); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate byte CompositorSetupDelegate(IntPtr robj);

    /// <summary>
    /// Calls the compositor-setup function found at CS-member+0x1AA (E8 displacement).
    /// CS-member calls this with RCX=robj after initD3D11 succeeds; it is expected to
    /// populate the runtime object slot checked by SC-member's [R15+0x990] guard.
    /// We call it here directly so the state is ready regardless of CS-member's path.
    /// </summary>
    private static unsafe void CallCompositorSetupDirect()
    {
        try
        {
            IntPtr robj = GetVDXRRuntimeObject();
            if (robj == IntPtr.Zero) { Log.LogWarning("  CallCSF: robj null"); return; }

            IntPtr* vtable = *(IntPtr**)robj;
            IntPtr csMember = vtable[0x060 / 8];  // slot 12

            byte* cs = (byte*)csMember;
            if (cs[0x1AA] != 0xE8)
            {
                Log.LogWarning($"  CallCSF: expected E8 at CS+0x1AA, got 0x{cs[0x1AA]:X2} — skipping");
                return;
            }
            int disp = *(int*)(cs + 0x1AB);
            IntPtr compositorSetup = (IntPtr)((long)csMember + 0x1AF + disp);
            Log.LogInfo($"  CallCSF: fn @ 0x{compositorSetup:X}");

            // Dump first 0x60 bytes of compositor setup for diagnostics.
            byte* csf = (byte*)compositorSetup;
            for (int ci = 0; ci < 0x60; ci += 16)
            {
                var sb = new System.Text.StringBuilder($"  CSF+0x{ci:X2}:");
                for (int cj = 0; cj < 16; cj++) sb.Append($" {csf[ci+cj]:X2}");
                Log.LogInfo(sb.ToString());
            }

            // CSF+0x42: CMP [RCX+0x38],0  — non-null because robj+0x38 was set during xrCreateInstance.
            // CSF+0x47: JNE +0x51A        — early return that skips the full setup, leaving robj+0x990 = null.
            // Without robj+0x990, SC-member's streaming path (CMP [robj+0x990],0 at +0x267) produces
            // null swapchain image texture pointers.
            // Fix: NOP the 6-byte JNE so CSF always runs its full initialisation path.
            PatchBytes(compositorSetup, 0x47,
                new byte[]{0x0F, 0x85, 0xCC, 0x04, 0x00, 0x00},  // JNE +0x51A (early return)
                new byte[]{0x90, 0x90, 0x90, 0x90, 0x90, 0x90},  // NOP×6 — always run full CSF
                "CSF-skip-earlyrtn");

            // CSF+0x4E: CMP [RCX+0x28], 0
            // CSF+0x52: JNZ +0xD  → skip the CALL at +0x54 when robj+0x28 != 0.
            // robj+0x28 is already non-zero (OVR loaded flag), so the inner fn is never called.
            // That inner fn (at CSF+0x54's CALL target) is the one that calls ovr_Create()
            // to populate robj+0x990 (m_ovrSession).  Call it directly.
            Log.LogInfo($"  CallCSF: robj+0x28=0x{Marshal.ReadByte(robj, 0x28):X}");
            if (csf[0x52] == 0x75 && csf[0x54] == 0xE8)
            {
                int innerDisp = *(int*)(csf + 0x55);
                IntPtr innerFn = (IntPtr)((long)compositorSetup + 0x59 + innerDisp);
                Log.LogInfo($"  CallCSF: calling inner fn @ 0x{innerFn:X}");
                byte ir = Marshal.GetDelegateForFunctionPointer<CompositorSetupDelegate>(innerFn)(robj);
                Log.LogInfo($"  CallCSF inner: result=0x{ir:X}  robj+0x990=0x{Marshal.ReadInt64(robj, 0x990):X}");
            }
            else
                Log.LogWarning($"  CallCSF: inner fn pattern mismatch CSF[0x52]=0x{csf[0x52]:X2} CSF[0x54]=0x{csf[0x54]:X2}");

            var fn = Marshal.GetDelegateForFunctionPointer<CompositorSetupDelegate>(compositorSetup);
            byte result = fn(robj);
            Log.LogInfo($"  CallCSF: result=0x{result:X}");
            Log.LogInfo($"  CallCSF post: robj+0x990=0x{Marshal.ReadInt64(robj, 0x990):X}  +0x988=0x{Marshal.ReadInt64(robj, 0x988):X}  +0x998=0x{Marshal.ReadInt64(robj, 0x998):X}");
        }
        catch (Exception ex) { Log.LogWarning($"  CallCSF: {ex.Message}"); }
    }

    private static void SetRuntimeGraphicsRequirements(IntPtr d3dDevice)
    {
        if (_robjWriteDone) return;
        try
        {
            IntPtr robj = GetVDXRRuntimeObject();
            if (robj == IntPtr.Zero) { Log.LogWarning("  SetRuntimeReqs: runtime object null"); return; }

            // Dump first 256 bytes once so we can locate the sentinel (never repeated).
            if (!_robjDumped)
            {
                _robjDumped = true;
                var sb = new StringBuilder($"  SetRuntimeReqs: robj=0x{robj:X}  [+0..+FF]:");
                for (int i = 0; i < 256; i++)
                {
                    if (i % 16 == 0) sb.Append($"\n    +{i:X2}:");
                    sb.Append($" {Marshal.ReadByte(robj, i):X2}");
                }
                Log.LogInfo(sb.ToString());
            }

            // Dynamically locate m_graphicsRequirementQueried.
            //
            // OpenXrRuntime layout (from VDXR runtime.h, relative to OpenXrRuntime start):
            //   +32 : m_instanceCreated  (bool, 0x01 after xrCreateInstance)
            //   +33 : m_systemCreated    (bool, 0x01 after xrGetSystem)
            //   +34..+39 : padding (6 bytes to align vector to 8)
            //   +40..+63 : m_extensionsTable (std::vector<Extension>, 24 bytes)
            //   +64 : m_graphicsRequirementQueried (bool)
            //   +65..+71 : padding (7 bytes to align LUID to 8)
            //   +72 : m_adapterLuid (LUID, 8 bytes)
            //
            // Because OpenXrRuntime inherits from OpenXrApi (which has 31+ extension-flag
            // bools before the runtime's own members), all offsets above are shifted by
            // the size of OpenXrApi.  We find the sentinel dynamically:
            //   - Look for bytes[i]==0x01, bytes[i+1]==0x01, bytes[i+2..i+7]==0x00
            //     → this is (m_instanceCreated, m_systemCreated) + padding
            //   - m_graphicsRequirementQueried is at i+32, m_adapterLuid at i+40.
            int gfxReqOffset = -1;
            int luidOffset   = -1;
            for (int i = 8; i <= 240; i++)
            {
                if (Marshal.ReadByte(robj, i)   != 0x01) continue;
                if (Marshal.ReadByte(robj, i+1) != 0x01) continue;
                bool paddingOk = true;
                for (int p = 2; p < 8; p++)
                    if (Marshal.ReadByte(robj, i+p) != 0x00) { paddingOk = false; break; }
                if (!paddingOk) continue;
                gfxReqOffset = i + 32;
                luidOffset   = i + 36;  // LUID is 4 bytes after gfxReq (3 bytes padding, not 7)
                Log.LogInfo($"  SetRuntimeReqs: sentinel found at +{i} → gfxReq@+{gfxReqOffset}, luid@+{luidOffset}");
                break;
            }

            if (gfxReqOffset < 0)
            {
                Log.LogWarning("  SetRuntimeReqs: sentinel (m_instanceCreated,m_systemCreated) not found in first 256 bytes — falling back to +96/+100");
                gfxReqOffset = 96;
                luidOffset   = 100;
            }

            Marshal.WriteByte(robj, gfxReqOffset, 1);

            long luid = GetAdapterLuid(d3dDevice);
            if (luid != 0)
                Marshal.WriteInt64(robj, luidOffset, luid);

            Log.LogInfo($"  SetRuntimeReqs: wrote graphicsReqQueried=1 @+{gfxReqOffset}  adapterLuid=0x{luid:X16} @+{luidOffset}  ✓");
            _robjWriteDone = true;
        }
        catch (Exception ex) { Log.LogWarning($"  SetRuntimeReqs: {ex.Message}"); }
    }

    /// <summary>
    /// Returns the 64-bit adapter LUID for the given ID3D11Device by walking the
    /// COM vtable chain:  ID3D11Device → IDXGIDevice::GetAdapter → IDXGIAdapter::GetDesc.
    /// Returns 0 if any step fails (initializeD3D11 will then skip LUID validation).
    ///
    /// IID_IDXGIDevice  = {54ec77fa-1377-44e6-8c32-88fd5f44c84c}
    /// IDXGIDevice vtable index 7  = GetAdapter
    /// IDXGIAdapter vtable index 8 = GetDesc
    /// DXGI_ADAPTER_DESC.AdapterLuid is at byte offset 296 (256 WCHAR desc + 16 UINT + 24 SIZE_T)
    /// </summary>
    private static long GetAdapterLuid(IntPtr d3dDevice)
    {
        if (d3dDevice == IntPtr.Zero) return 0;
        IntPtr iidBuf  = IntPtr.Zero;
        IntPtr descBuf = IntPtr.Zero;
        IntPtr dxgiDev = IntPtr.Zero;
        IntPtr adapter = IntPtr.Zero;
        try
        {
            // Allocate IID_IDXGIDevice bytes in unmanaged memory.
            // GUID in-memory layout (little-endian): Data1(4 LE) Data2(2 LE) Data3(2 LE) Data4(8)
            iidBuf = Marshal.AllocHGlobal(16);
            byte[] iid = { 0xFA,0x77,0xEC,0x54, 0x77,0x13, 0xE6,0x44,
                           0x8C,0x32,0x88,0xFD,0x5F,0x44,0xC8,0x4C };
            for (int i = 0; i < 16; i++) Marshal.WriteByte(iidBuf, i, iid[i]);

            // vtable[0] = IUnknown::QueryInterface
            IntPtr devVtbl = Marshal.ReadIntPtr(d3dDevice);
            int hr = Marshal.GetDelegateForFunctionPointer<COMQueryInterfaceDelegate>(
                         Marshal.ReadIntPtr(devVtbl, 0))(d3dDevice, iidBuf, out dxgiDev);
            if (hr != 0) { Log.LogWarning($"  GetAdapterLuid: QI hr=0x{(uint)hr:X8}"); return 0; }

            // vtable[7] = IDXGIDevice::GetAdapter
            IntPtr dxgiVtbl = Marshal.ReadIntPtr(dxgiDev);
            int hr2 = Marshal.GetDelegateForFunctionPointer<DXGIGetAdapterDelegate>(
                          Marshal.ReadIntPtr(dxgiVtbl, 7 * IntPtr.Size))(dxgiDev, out adapter);
            if (hr2 != 0) { Log.LogWarning($"  GetAdapterLuid: GetAdapter hr=0x{(uint)hr2:X8}"); return 0; }

            // vtable[8] = IDXGIAdapter::GetDesc  — fill DXGI_ADAPTER_DESC (304 bytes)
            IntPtr adapVtbl = Marshal.ReadIntPtr(adapter);
            descBuf = Marshal.AllocHGlobal(304);
            for (int i = 0; i < 304; i++) Marshal.WriteByte(descBuf, i, 0);
            int hr3 = Marshal.GetDelegateForFunctionPointer<DXGIGetDescDelegate>(
                          Marshal.ReadIntPtr(adapVtbl, 8 * IntPtr.Size))(adapter, descBuf);
            if (hr3 != 0) { Log.LogWarning($"  GetAdapterLuid: GetDesc hr=0x{(uint)hr3:X8}"); return 0; }

            // AdapterLuid at offset 296 (256 + 16 + 24)
            long luid = Marshal.ReadInt64(descBuf, 296);
            Log.LogInfo($"  GetAdapterLuid: LUID=0x{luid:X16}");
            return luid;
        }
        catch (Exception ex) { Log.LogWarning($"  GetAdapterLuid: {ex.Message}"); return 0; }
        finally
        {
            if (iidBuf  != IntPtr.Zero) Marshal.FreeHGlobal(iidBuf);
            if (descBuf != IntPtr.Zero) Marshal.FreeHGlobal(descBuf);
            if (adapter != IntPtr.Zero)
            {
                try { IntPtr av = Marshal.ReadIntPtr(adapter);
                      Marshal.GetDelegateForFunctionPointer<COMReleaseDelegate>(
                          Marshal.ReadIntPtr(av, 2 * IntPtr.Size))(adapter); } catch { }
            }
            if (dxgiDev != IntPtr.Zero)
            {
                try { IntPtr dv = Marshal.ReadIntPtr(dxgiDev);
                      Marshal.GetDelegateForFunctionPointer<COMReleaseDelegate>(
                          Marshal.ReadIntPtr(dv, 2 * IntPtr.Size))(dxgiDev); } catch { }
            }
        }
    }

    /// <summary>
    /// Allocates an executable stub and redirects the shared requirements-check
    /// function pointer so both xrGetD3D11GfxReqs and xrCreateSession see "satisfied".
    ///
    /// Both callers share the pattern:
    ///   LEA  RDX, [RSP+0x48]       ; RDX → 32-byte requirements buffer
    ///   MOV  ECX, 3
    ///   CALL [ptrAddr]             ; ← we replace this target
    ///   MOVZX EAX, [RSP+0x44]     ; reads requirements-satisfied byte = [RDX-4]
    ///
    /// Then four CMP checks at +0x079..+0x093 gate on [RSP+0x58/5C/60/64] = [RDX+0x10/14/18/1C].
    /// All must be accounted for; any non-zero in [RDX+0x10/14/18] sets R9=&[RSP+0x58]
    /// (pointing to the D3D11 requirements block).
    ///
    /// Stub fills:
    ///   [RDX-4]       = 1                (requirements-satisfied flag)
    ///   [RDX+0x10]    = 1                (LUID LowPart, non-zero → triggers JNZ)
    ///   [RDX+0x14]    = 0                (LUID HighPart)
    ///   [RDX+0x18]    = 0x0000B000       (D3D_FEATURE_LEVEL_11_0)
    ///   [RDX+0x1C]    = 0                (padding)
    /// Returns 0.
    /// </summary>
    private static unsafe void PatchInnerRequirementsCheck(long ptrAddr)
    {
        try
        {
            // Verify the slot is non-null before patching
            IntPtr original = *(IntPtr*)ptrAddr;
            if (original == IntPtr.Zero)
            { Log.LogWarning("  PatchInnerReqs: slot is NULL, skipping"); return; }

            // Allocate executable memory for stub (MEM_COMMIT|MEM_RESERVE=0x3000, PAGE_EXECUTE_READWRITE=0x40)
            IntPtr stub = VirtualAlloc(IntPtr.Zero, (UIntPtr)64, 0x3000, 0x40);
            if (stub == IntPtr.Zero)
            { Log.LogWarning($"  PatchInnerReqs: VirtualAlloc failed"); return; }

            // Stub machine code:
            //  0F 57 C0              XORPS XMM0,XMM0            ; zero XMM0
            //  0F 11 02              MOVUPS [RDX],XMM0          ; zero [RDX+0x00..+0x0F] (was set by JBE path)
            //  0F 11 42 10           MOVUPS [RDX+0x10],XMM0     ; zero [RDX+0x10..+0x1F]
            //  C6 42 FC 01           MOV BYTE [RDX-4], 1        ; requirements-satisfied flag
            //  C7 42 10 01 00 00 00  MOV DWORD [RDX+0x10], 1    ; LUID LowPart (non-zero → JNZ taken)
            //  C7 42 14 00 00 00 00  MOV DWORD [RDX+0x14], 0    ; LUID HighPart
            //  C7 42 18 00 B0 00 00  MOV DWORD [RDX+0x18], 0xB000 ; D3D_FEATURE_LEVEL_11_0
            //  C7 42 1C 00 00 00 00  MOV DWORD [RDX+0x1C], 0    ; padding
            //  33 C0                 XOR EAX, EAX               ; return 0 = XR_SUCCESS
            //  C3                    RET
            byte* s = (byte*)stub;
            int i = 0;
            s[i++]=0x0F; s[i++]=0x57; s[i++]=0xC0;                           // XORPS XMM0,XMM0
            s[i++]=0x0F; s[i++]=0x11; s[i++]=0x02;                           // MOVUPS [RDX],XMM0        (zero +0x00..+0x0F)
            s[i++]=0x0F; s[i++]=0x11; s[i++]=0x42; s[i++]=0x10;             // MOVUPS [RDX+0x10],XMM0  (zero +0x10..+0x1F)
            s[i++]=0xC6; s[i++]=0x42; s[i++]=0xFC; s[i++]=0x01;              // MOV [RDX-4], 1
            s[i++]=0xC7; s[i++]=0x42; s[i++]=0x10; s[i++]=0x01; s[i++]=0x00; s[i++]=0x00; s[i++]=0x00; // MOV [RDX+0x10], 1
            s[i++]=0xC7; s[i++]=0x42; s[i++]=0x14; s[i++]=0x00; s[i++]=0x00; s[i++]=0x00; s[i++]=0x00; // MOV [RDX+0x14], 0
            s[i++]=0xC7; s[i++]=0x42; s[i++]=0x18; s[i++]=0x00; s[i++]=0xB0; s[i++]=0x00; s[i++]=0x00; // MOV [RDX+0x18], 0xB000
            s[i++]=0xC7; s[i++]=0x42; s[i++]=0x1C; s[i++]=0x00; s[i++]=0x00; s[i++]=0x00; s[i++]=0x00; // MOV [RDX+0x1C], 0
            s[i++]=0x33; s[i++]=0xC0;                                          // XOR EAX, EAX
            s[i++]=0xC3;                                                        // RET
            FlushInstructionCache(GetCurrentProcess(), stub, (UIntPtr)i);
            Log.LogInfo($"  PatchInnerReqs: stub at 0x{stub:X} ({i} bytes)");

            // Overwrite the function pointer slot (8 bytes on x64)
            if (!VirtualProtect((IntPtr)ptrAddr, (UIntPtr)8, 0x40, out uint oldProt))
            { Log.LogWarning($"  PatchInnerReqs: VirtualProtect(slot) failed"); return; }
            *(IntPtr*)ptrAddr = stub;
            VirtualProtect((IntPtr)ptrAddr, (UIntPtr)8, oldProt, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)ptrAddr, (UIntPtr)8);
            Log.LogInfo($"  PatchInnerReqs: slot 0x{ptrAddr:X} → stub  (was 0x{original:X})  ✓");
        }
        catch (Exception ex) { Log.LogWarning($"  PatchInnerReqs: {ex.Message}"); }
    }

    /// <summary>
    /// Patches the VD Streamer IPC dispatcher slot (slot2) to a minimal stub that
    /// returns XR_SUCCESS (0) immediately without contacting VD Streamer.
    /// After slot1 fills the requirements buffer, the outer function copies that
    /// data into the output struct once slot2 indicates success.
    /// Stub: XOR EAX,EAX (3 bytes) + RET — no arguments read, no side-effects.
    /// </summary>
    private static unsafe void PatchSecondCall(long ptrAddr)
    {
        try
        {
            IntPtr original = *(IntPtr*)ptrAddr;
            if (original == IntPtr.Zero)
            { Log.LogWarning("  PatchSecondCall: slot is NULL, skipping"); return; }

            IntPtr stub = VirtualAlloc(IntPtr.Zero, (UIntPtr)16, 0x3000, 0x40);
            if (stub == IntPtr.Zero)
            { Log.LogWarning("  PatchSecondCall: VirtualAlloc failed"); return; }

            // Stub machine code:
            //  33 C0   XOR EAX, EAX   ; return 0 = XR_SUCCESS
            //  C3      RET
            byte* s = (byte*)stub;
            s[0] = 0x33; s[1] = 0xC0; // XOR EAX, EAX
            s[2] = 0xC3;               // RET
            FlushInstructionCache(GetCurrentProcess(), stub, (UIntPtr)3);
            Log.LogInfo($"  PatchSecondCall: stub at 0x{stub:X} (3 bytes)");

            if (!VirtualProtect((IntPtr)ptrAddr, (UIntPtr)8, 0x40, out uint oldProt))
            { Log.LogWarning("  PatchSecondCall: VirtualProtect(slot) failed"); return; }
            *(IntPtr*)ptrAddr = stub;
            VirtualProtect((IntPtr)ptrAddr, (UIntPtr)8, oldProt, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)ptrAddr, (UIntPtr)8);
            Log.LogInfo($"  PatchSecondCall: slot 0x{ptrAddr:X} → stub  (was 0x{original:X})  ✓");
        }
        catch (Exception ex) { Log.LogWarning($"  PatchSecondCall: {ex.Message}"); }
    }

    /// <summary>
    /// Patches the vtable dispatch inside an xrGetD3D11GfxReqs/xrCreateSession-template fn.
    ///
    /// xrGetD3D11GfxReqs (fillReqsStruct=true, patchOffset=0x15B, nextRipOffset=0x160):
    ///   vtable[0x1E0] = GetD3D11Requirements — called to fill the output struct.
    ///   Stub fills XrGraphicsRequirementsD3D11KHR at R9 and returns 0.
    ///
    /// xrCreateSession (fillReqsStruct=false, patchOffset=0x159, nextRipOffset=0x15E):
    ///   vtable[0x60] = CheckD3D11Requirements — returns -38 if reqs not satisfied.
    ///   Stub returns 0 (satisfied) without filling any struct.
    ///
    /// In both cases we replace 6 bytes (MOV RCX,RAX; CALL R10) with:
    ///   E8 <rel32>  CALL stub  (5 bytes)
    ///   90          NOP        (1 byte)
    /// </summary>
    /// <summary>
    /// Allocates an executable page within ±512 MB of <paramref name="nearAddr"/> so that
    /// an E8 rel32 CALL instruction can reach it.  Tries 64 KB-aligned addresses in both
    /// directions from the target.  Returns IntPtr.Zero if all attempts fail.
    /// </summary>
    private static IntPtr AllocNear(long nearAddr, int size)
    {
        const long GRAN = 0x10000L; // 64 KB allocation granularity
        long baseAddr = nearAddr & ~(GRAN - 1);
        for (long delta = GRAN; delta < 0x20000000L; delta += GRAN) // ±512 MB
        {
            long lo = baseAddr - delta;
            if (lo > 0)
            {
                IntPtr a = VirtualAlloc((IntPtr)lo, (UIntPtr)size, 0x3000, 0x40);
                if (a != IntPtr.Zero) return a;
            }
            long hi = baseAddr + delta;
            {
                IntPtr a = VirtualAlloc((IntPtr)hi, (UIntPtr)size, 0x3000, 0x40);
                if (a != IntPtr.Zero) return a;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Patches a single byte in executable code, verifying the expected old value first.
    /// </summary>
    private static unsafe void PatchSingleByte(IntPtr fn, int offset, byte expected, byte replacement, string label)
    {
        try
        {
            byte* p = (byte*)fn + offset;
            if (*p != expected)
            { Log.LogWarning($"  PatchSingleByte({label}): fn+0x{offset:X} = 0x{*p:X2}, expected 0x{expected:X2} — skipping"); return; }
            if (!VirtualProtect((IntPtr)p, (UIntPtr)1, 0x40, out uint oldProt))
            { Log.LogWarning($"  PatchSingleByte({label}): VirtualProtect failed"); return; }
            *p = replacement;
            VirtualProtect((IntPtr)p, (UIntPtr)1, oldProt, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)p, (UIntPtr)1);
            Log.LogInfo($"  PatchSingleByte({label}): fn+0x{offset:X} 0x{expected:X2}→0x{replacement:X2}  ✓");
        }
        catch (Exception ex) { Log.LogWarning($"  PatchSingleByte({label}): {ex.Message}"); }
    }

    /// <summary>
    /// Patches multiple contiguous bytes in executable code, verifying the expected old
    /// bytes first.  oldBytes and newBytes must have equal length.
    /// </summary>
    private static unsafe void PatchBytes(IntPtr fn, int offset, byte[] oldBytes, byte[] newBytes, string label)
    {
        try
        {
            if (oldBytes.Length != newBytes.Length)
            { Log.LogWarning($"  PatchBytes({label}): length mismatch {oldBytes.Length} vs {newBytes.Length}"); return; }
            byte* p = (byte*)fn + offset;
            for (int i = 0; i < oldBytes.Length; i++)
            {
                if (p[i] != oldBytes[i])
                { Log.LogWarning($"  PatchBytes({label}): fn+0x{offset+i:X} = 0x{p[i]:X2}, expected 0x{oldBytes[i]:X2} — skipping"); return; }
            }
            if (!VirtualProtect((IntPtr)p, (UIntPtr)newBytes.Length, 0x40, out uint oldProt))
            { Log.LogWarning($"  PatchBytes({label}): VirtualProtect failed"); return; }
            for (int i = 0; i < newBytes.Length; i++) p[i] = newBytes[i];
            VirtualProtect((IntPtr)p, (UIntPtr)newBytes.Length, oldProt, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)p, (UIntPtr)newBytes.Length);
            Log.LogInfo($"  PatchBytes({label}): fn+0x{offset:X} {oldBytes.Length} bytes replaced  ✓");
        }
        catch (Exception ex) { Log.LogWarning($"  PatchBytes({label}): {ex.Message}"); }
    }

    /// <summary>
    /// Patches the 21-byte block at xrCreateSession+0x144..+0x158 that calls
    /// get_D3D11_context and sets up args for the vtable[0x60] dispatch.
    /// Since our slot2 stub returns 0 without initialising the D3D11 context
    /// object, get_D3D11_context returns NULL and the subsequent MOV RCX,[RAX]
    /// crashes.  We replace the whole block with: E8 stub + 16 NOPs.
    /// The stub returns EAX=0.  Execution then falls through into our separate
    /// vtable[0x60] stub already patched at fn+0x159.
    /// </summary>
    private static unsafe void PatchCreateSessionContextBlock(IntPtr fn)
    {
        try
        {
            // The CALL opcode (E8) is at fn+0x143; its rel32 field spans fn+0x144..+0x147.
            // Previous versions started the patch at fn+0x144, accidentally writing a new
            // E8 over rel32[0] and leaving the original E8 at fn+0x143 with a corrupted
            // displacement.  The correct patch starts at fn+0x143 (BlockLen=22, covering
            // fn+0x143..+0x158).  The natural JBE at fn+0x06F (taken when state≤5) goes
            // to fn+0x143, so this CALL must be valid there.
            const int BlockLen = 22; // +0x143 .. +0x158 inclusive
            // next-RIP for the E8 CALL is fn+0x143 + 5 = fn+0x148
            long nextRip = (long)fn + 0x148;
            IntPtr stub = AllocNear(nextRip, 8);
            if (stub == IntPtr.Zero)
            { Log.LogWarning("  PatchCSContextBlock: AllocNear failed"); return; }

            // Stub: XOR EAX,EAX; RET  (3 bytes)
            byte* s = (byte*)stub;
            s[0] = 0x33; s[1] = 0xC0; // XOR EAX,EAX
            s[2] = 0xC3;               // RET
            FlushInstructionCache(GetCurrentProcess(), stub, (UIntPtr)3);

            long rel32Long = (long)stub - nextRip;
            if (rel32Long < int.MinValue || rel32Long > int.MaxValue)
            { Log.LogWarning("  PatchCSContextBlock: stub out of rel32 range"); return; }
            int rel32 = (int)rel32Long;

            byte* patchAt = (byte*)fn + 0x143;
            if (!VirtualProtect((IntPtr)patchAt, (UIntPtr)BlockLen, 0x40, out uint oldProt))
            { Log.LogWarning("  PatchCSContextBlock: VirtualProtect failed"); return; }

            patchAt[0] = 0xE8;
            patchAt[1] = (byte)( rel32        & 0xFF);
            patchAt[2] = (byte)((rel32 >>  8) & 0xFF);
            patchAt[3] = (byte)((rel32 >> 16) & 0xFF);
            patchAt[4] = (byte)((rel32 >> 24) & 0xFF);
            for (int k = 5; k < BlockLen; k++) patchAt[k] = 0x90; // NOP

            VirtualProtect((IntPtr)patchAt, (UIntPtr)BlockLen, oldProt, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)patchAt, (UIntPtr)BlockLen);
            Log.LogInfo($"  PatchCSContextBlock: fn+0x143..+0x158 → CALL stub+NOPs  ✓  (stub=0x{stub:X})");
        }
        catch (Exception ex) { Log.LogWarning($"  PatchCSContextBlock: {ex.Message}"); }
    }

    private static unsafe void PatchVtableDispatch(IntPtr fn, string label,
        int patchOffset = 0x15B, int nextRipOffset = 0x160, bool fillReqsStruct = true)
    {
        try
        {
            // Stub must be within ±2 GB of nextRip (next-RIP after the 5-byte CALL).
            long nextRip = (long)fn + nextRipOffset;
            IntPtr stub = AllocNear(nextRip, 32);
            if (stub == IntPtr.Zero)
            { Log.LogWarning($"  PatchVtable({label}): AllocNear failed"); return; }

            byte* s = (byte*)stub;
            int i = 0;
            if (fillReqsStruct)
            {
                // xrGetD3D11GfxReqs path: fake-fill the XrGraphicsRequirementsD3D11KHR
                // output struct at R9 with a plausible LUID/feature-level and return 0.
                // vtable[60] consistently returns -1 for any args we can supply, so we
                // bypass the real call entirely.  D3D11 backend is initialised separately
                // via CallInitD3D11Direct before xrCreateSession.
                //
                // Struct layout (offsets from R9):
                //   +0x00: type(4) pad(4)   — already set by caller
                //   +0x08: next(8)          — already NULL
                //   +0x10: LUID.LowPart(4)  ← write 1 (non-zero triggers JNZ in GR-outer)
                //   +0x14: LUID.HighPart(4) ← write 0
                //   +0x18: minFeatureLevel  ← write D3D_FEATURE_LEVEL_11_0 = 0xB000
                // MOV DWORD PTR [R9+0x10], 1
                s[i++]=0x41; s[i++]=0xC7; s[i++]=0x41; s[i++]=0x10;
                s[i++]=0x01; s[i++]=0x00; s[i++]=0x00; s[i++]=0x00;
                // MOV DWORD PTR [R9+0x14], 0
                s[i++]=0x41; s[i++]=0xC7; s[i++]=0x41; s[i++]=0x14;
                s[i++]=0x00; s[i++]=0x00; s[i++]=0x00; s[i++]=0x00;
                // MOV DWORD PTR [R9+0x18], 0xB000
                s[i++]=0x41; s[i++]=0xC7; s[i++]=0x41; s[i++]=0x18;
                s[i++]=0x00; s[i++]=0xB0; s[i++]=0x00; s[i++]=0x00;
                s[i++]=0x33; s[i++]=0xC0; // XOR EAX, EAX
                s[i++]=0xC3;              // RET
            }
            else
            {
                // Non-fillReqs path (xrCreateSession): just succeed
                s[i++]=0x33; s[i++]=0xC0; // XOR EAX, EAX
                s[i++]=0xC3;              // RET
            }
            FlushInstructionCache(GetCurrentProcess(), stub, (UIntPtr)i);
            Log.LogInfo($"  PatchVtable({label}): stub at 0x{stub:X} ({i} bytes, fillReqs={fillReqsStruct})");

            // Patch patchOffset: 6 bytes = E8 <rel32> 90
            // CALL rel32: rel32 = stub − nextRip   [next-RIP after the 5-byte CALL]
            byte* patchAt = (byte*)fn + patchOffset;
            long rel32Long = (long)stub - nextRip;
            if (rel32Long < int.MinValue || rel32Long > int.MaxValue)
            { Log.LogWarning($"  PatchVtable({label}): stub address out of rel32 range"); return; }
            int rel32 = (int)rel32Long;

            if (!VirtualProtect((IntPtr)patchAt, (UIntPtr)6, 0x40 /*PAGE_EXECUTE_READWRITE*/, out uint oldProt))
            { Log.LogWarning($"  PatchVtable({label}): VirtualProtect failed"); return; }

            patchAt[0] = 0xE8;
            patchAt[1] = (byte)( rel32        & 0xFF);
            patchAt[2] = (byte)((rel32 >>  8) & 0xFF);
            patchAt[3] = (byte)((rel32 >> 16) & 0xFF);
            patchAt[4] = (byte)((rel32 >> 24) & 0xFF);
            patchAt[5] = 0x90; // NOP

            VirtualProtect((IntPtr)patchAt, (UIntPtr)6, oldProt, out _);
            FlushInstructionCache(GetCurrentProcess(), (IntPtr)patchAt, (UIntPtr)6);
            Log.LogInfo($"  PatchVtable({label}): fn+0x15B → CALL stub+NOP  ✓");
        }
        catch (Exception ex) { Log.LogWarning($"  PatchVtable({label}): {ex.Message}"); }
    }

    /// <summary>
    /// Reads the CALL displacement at outer-function+0x143 (the singleton helper call
    /// present in every VDXR outer trampoline) and invokes it to obtain the OpenXrApi*
    /// runtime object.  Both xrCreateSession and xrGetD3D11GfxReqs outer functions share
    /// the same helper at VDXR+0x6D210.  We derive the address from _pfnGetD3D11GfxReqs
    /// which is confirmed to be the raw VDXR function (not a loader trampoline).
    /// </summary>
    private static unsafe IntPtr GetVDXRRuntimeObject()
    {
        if (_pfnGetD3D11GfxReqs == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            byte* fn = (byte*)_pfnGetD3D11GfxReqs;
            // Outer function layout: ... E8 <disp32> ... at offset +0x143
            if (fn[0x143] != 0xE8)
            {
                Log.LogWarning($"  GetVDXRRuntimeObject: byte at +0x143 = 0x{fn[0x143]:X2} (expected E8 CALL) — cannot locate helper");
                return IntPtr.Zero;
            }
            int disp = *(int*)(fn + 0x144);
            IntPtr helperAddr = (IntPtr)((long)_pfnGetD3D11GfxReqs + 0x148 + disp);
            var helper = Marshal.GetDelegateForFunctionPointer<VDXRSingletonDelegate>(helperAddr);
            return helper();
        }
        catch (Exception ex) { Log.LogWarning($"  GetVDXRRuntimeObject: {ex.Message}"); return IntPtr.Zero; }
    }

    /// <summary>
    /// Locates the VDXR runtime's xrCreateSession for diagnostic logging and to save
    /// _pfnCreateSessionDirect (so XrCreateSession calls the runtime function directly
    /// rather than going through the loader trampoline).
    ///
    /// The -38 guard (XR_ERROR_GRAPHICS_REQUIREMENTS_CALL_MISSING) is patched out
    /// via ScanAndPatchMinus38 so xrCreateSession can proceed regardless of whether
    /// m_graphicsRequirementQueried was set in the runtime before this call.
    /// </summary>
    private static unsafe void PatchRuntimeCreateSession()
    {
        try
        {
            if (_pfnBeginSession == IntPtr.Zero) { Log.LogWarning("  PatchRuntime: no pfnBeginSession"); return; }
            if (!GetModuleHandleEx(0x4, _pfnBeginSession, out IntPtr hVDXR) || hVDXR == IntPtr.Zero)
            { Log.LogWarning("  PatchRuntime: GetModuleHandleEx failed"); return; }

            IntPtr negotiateAddr = FindPeExport(hVDXR, "xrNegotiateLoaderRuntimeInterface");
            if (negotiateAddr == IntPtr.Zero)
            { Log.LogWarning("  PatchRuntime: xrNegotiateLoaderRuntimeInterface not found"); return; }

            IntPtr rtCS = GetRuntimeCreateSessionViaGpa(negotiateAddr);
            if (rtCS == IntPtr.Zero) { Log.LogWarning("  PatchRuntime: could not resolve VDXR xrCreateSession"); return; }

            if (!GetModuleHandleEx(0x4, rtCS, out IntPtr hCheck) || hCheck != hVDXR)
            { Log.LogWarning("  PatchRuntime: resolved xrCreateSession not in VDXR — discarding"); return; }

            _pfnCreateSessionDirect = rtCS;

            // Patch out the -38 guard (XR_ERROR_GRAPHICS_REQUIREMENTS_CALL_MISSING).
            // The check lives in the MEMBER function (vtable[0x60]), NOT the outer trampoline.
            IntPtr runtimeObj = GetVDXRRuntimeObject();
            if (runtimeObj != IntPtr.Zero)
            {
                unsafe
                {
                    IntPtr* vtable = *(IntPtr**)runtimeObj;
                    IntPtr xrCSMember = vtable[0x060 / 8]; // slot 12 = xrCreateSession member
                    ScanAndPatchMinus38(xrCSMember, 512, "CS-member");
                    // Also patch any XR_ERROR_VALIDATION_FAILURE (-2) return.
                    // When m_graphicsRequirementQueried=1, initializeD3D11() runs inside CS-member
                    // and may fail with -2 (e.g. device/adapter validation).  Patching it out
                    // lets CS succeed so initializeD3D11() can register swapchain formats.
                    ScanAndPatchErrorCode(xrCSMember, 512, "CS-member-v2", -2);
                }
            }
            else
            {
                Log.LogWarning("  PatchRuntimeCreateSession: runtime object NULL — falling back to outer trampoline scan");
                ScanAndPatchMinus38(rtCS, 640, "CS-outer");
            }
        }
        catch (Exception ex) { Log.LogWarning($"  PatchRuntimeCreateSession: {ex.Message}"); }
    }

    /// <summary>
    /// Calls xrNegotiateLoaderRuntimeInterface on the VDXR DLL to obtain the runtime's
    /// own xrGetInstanceProcAddr, then queries it for "xrCreateSession" using several
    /// candidate instance handles.
    /// Returns IntPtr.Zero if the function address cannot be determined.
    /// </summary>
    private static unsafe IntPtr GetRuntimeCreateSessionViaGpa(IntPtr negotiateAddr)
    {
        // XrNegotiateLoaderInfo (40 bytes, all fields little-endian):
        //   [0]  uint32  structType            = 1 (XR_LOADER_INTERFACE_STRUCT_LOADER_INFO)
        //   [4]  uint32  structVersion         = 1
        //   [8]  size_t  structSize            = 40
        //   [16] uint32  minInterfaceVersion   = 1
        //   [20] uint32  maxInterfaceVersion   = 1
        //   [24] uint64  minApiVersion         = XR_MAKE_VERSION(1,0,0)
        //   [32] uint64  maxApiVersion         = XR_MAKE_VERSION(1,1,0)
        const int loaderInfoSz = 40;
        IntPtr li = Marshal.AllocHGlobal(loaderInfoSz);
        try
        {
            for (int i = 0; i < loaderInfoSz; i++) Marshal.WriteByte(li, i, 0);
            Marshal.WriteInt32(li,  0, 1);              // structType
            Marshal.WriteInt32(li,  4, 1);              // structVersion
            Marshal.WriteInt64(li,  8, loaderInfoSz);   // structSize (size_t)
            Marshal.WriteInt32(li, 16, 1);              // minInterfaceVersion
            Marshal.WriteInt32(li, 20, 1);              // maxInterfaceVersion
            Marshal.WriteInt64(li, 24, (long)(1UL << 48));                    // minApiVersion = 1.0.0
            Marshal.WriteInt64(li, 32, (long)((1UL << 48) | (1UL << 32)));   // maxApiVersion = 1.1.0

            // XrNegotiateRuntimeRequest (40 bytes):
            //   [0]  uint32  structType                = 3 (XR_LOADER_INTERFACE_STRUCT_RUNTIME_REQUEST)
            //   [4]  uint32  structVersion             = 1
            //   [8]  size_t  structSize                = 40
            //   [16] uint32  runtimeInterfaceVersion   (output)
            //   [20] uint32  padding
            //   [24] uint64  runtimeApiVersion         (output)
            //   [32] ptr     getInstanceProcAddr       (output)
            const int runtimeReqSz = 40;
            IntPtr rr = Marshal.AllocHGlobal(runtimeReqSz);
            try
            {
                for (int i = 0; i < runtimeReqSz; i++) Marshal.WriteByte(rr, i, 0);
                Marshal.WriteInt32(rr, 0, 3);             // structType
                Marshal.WriteInt32(rr, 4, 1);             // structVersion
                Marshal.WriteInt64(rr, 8, runtimeReqSz);  // structSize

                int negRc = Marshal.GetDelegateForFunctionPointer<XrNegotiateLoaderRuntimeDelegate>(negotiateAddr)(li, rr);
                Log.LogInfo($"  xrNegotiateLoaderRuntimeInterface rc={negRc}");
                if (negRc != 0) return IntPtr.Zero;

                IntPtr rtGpaAddr = Marshal.ReadIntPtr(rr, 32);
                uint rtVer   = (uint)Marshal.ReadInt32(rr, 16);
                ulong rtApi  = (ulong)Marshal.ReadInt64(rr, 24);
                Log.LogInfo($"  Runtime interface version={rtVer}  apiVersion=0x{rtApi:X}  GPA=0x{rtGpaAddr:X}");
                if (rtGpaAddr == IntPtr.Zero) return IntPtr.Zero;

                var rtGpa = Marshal.GetDelegateForFunctionPointer<XrGetProcAddrDelegate>(rtGpaAddr);

                // Try several candidate instance handles.
                // VDXR likely assigned itself the same small sequential handle the loader uses.
                ulong[] candidates = { 0, _instance, 1, 2 };
                foreach (ulong inst in candidates)
                {
                    rtGpa(inst, "xrCreateSession", out IntPtr addr);
                    Log.LogInfo($"  runtime_gpa(0x{inst:X}, \"xrCreateSession\") = 0x{addr:X}");
                    if (addr != IntPtr.Zero) return addr;
                }
                return IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(rr); }
        }
        finally { Marshal.FreeHGlobal(li); }
    }

    /// <summary>
    /// Last-resort: walk every executable PE section in the DLL and NOP all -38
    /// requirements guards found there.  Safe because XR_ERROR_GRAPHICS_REQUIREMENTS_CALL_MISSING
    /// appears exclusively in xrCreateSession for XR runtimes.
    /// </summary>
    /// <param name="anchorRVA">RVA of xrCreateSession inside the DLL (or -1 to scan all).</param>
    /// <param name="windowSize">Only patch guards within ±windowSize bytes of anchorRVA.</param>
    private static unsafe void ScanAndPatchMinus38InDll(IntPtr hModule, string label,
                                                         long anchorRVA = -1, long windowSize = long.MaxValue)
    {
        try
        {
            byte* p = (byte*)hModule;
            if (p[0] != 0x4D || p[1] != 0x5A) return;
            int peOff = *(int*)(p + 0x3C);
            byte* nt = p + peOff;
            if (nt[0] != 0x50 || nt[1] != 0x45) return;
            int numSec    = *(ushort*)(nt + 6);
            ushort optSz  = *(ushort*)(nt + 20);
            byte* sections = nt + 24 + optSz;

            int patchCount = 0;
            for (int s = 0; s < numSec; s++)
            {
                byte* sec  = sections + s * 40;
                uint chars = *(uint*)(sec + 36);
                // IMAGE_SCN_MEM_EXECUTE = 0x20000000
                if ((chars & 0x20000000) == 0) continue;

                uint virtOff = *(uint*)(sec + 12);
                uint rawSz   = *(uint*)(sec + 16);
                byte* data   = p + virtOff;
                Log.LogInfo($"  [{label}DLL] Code section at +0x{virtOff:X5} size=0x{rawSz:X5}");

                for (int i = 0; i < (int)rawSz - 6; i++)
                {
                    // Range filter: skip if too far from xrCreateSession
                    if (anchorRVA >= 0)
                    {
                        long byteRVA = (long)virtOff + i;
                        if (Math.Abs(byteRVA - anchorRVA) > windowSize) continue;
                    }
                    bool is6B = data[i] == 0x41
                        && (data[i+1] == 0xBC || data[i+1] == 0xBD || data[i+1] == 0xBE || data[i+1] == 0xBF)
                        && data[i+2] == 0xDA && data[i+3] == 0xFF && data[i+4] == 0xFF && data[i+5] == 0xFF;
                    bool is5B = (data[i] == 0xB8 || data[i] == 0xB9 || data[i] == 0xBA || data[i] == 0xBB)
                        && data[i+1] == 0xDA && data[i+2] == 0xFF && data[i+3] == 0xFF && data[i+4] == 0xFF;
                    if (!is6B && !is5B) continue;

                    Log.LogInfo($"  [{label}DLL] -38 at sec+0x{i:X5} VA=0x{(long)(data+i):X}");

                    // Scan backwards up to 32 bytes for a short Jcc (7x xx)
                    for (int j = i - 2; j >= Math.Max(0, i - 32); j--)
                    {
                        byte op = data[j];
                        if ((op & 0xF0) != 0x70) continue;
                        int tgt = j + 2 + (sbyte)data[j + 1];
                        if (tgt <= i) continue;   // branch skips forward past -38 assignment
                        Log.LogInfo($"  [{label}DLL] Jcc at sec+0x{j:X5}: {op:X2} {data[j+1]:X2} → sec+0x{tgt:X5}");
                        byte* pjcc = data + j;
                        if (!VirtualProtect((IntPtr)pjcc, (UIntPtr)2, 0x40, out uint oldP))
                        { Log.LogWarning($"  [{label}DLL] VirtualProtect failed"); continue; }
                        pjcc[0] = 0xEB; // Jcc → JMP (unconditional short), same disp — always skips -38
                        VirtualProtect((IntPtr)pjcc, (UIntPtr)2, oldP, out _);
                        FlushInstructionCache(GetCurrentProcess(), (IntPtr)pjcc, (UIntPtr)2);
                        Log.LogInfo($"  [{label}DLL] Jcc→JMP patched at VA=0x{(long)pjcc:X}  ✓");
                        patchCount++;
                        break;
                    }
                }
            }
            if (patchCount == 0)
                Log.LogWarning($"  [{label}DLL] No patchable -38 guards found — requirements check may be elsewhere.");
            else
                Log.LogInfo($"  [{label}DLL] {patchCount} -38 guard(s) patched.");
        }
        catch (Exception ex) { Log.LogWarning($"  ScanAndPatchMinus38InDll {label}: {ex.Message}"); }
    }

    /// <summary>Walk the PE export table of a loaded DLL and return the address of funcName, or Zero.</summary>
    private static unsafe IntPtr FindPeExport(IntPtr hModule, string funcName)
    {
        try
        {
            byte* p = (byte*)hModule;
            if (p[0] != 0x4D || p[1] != 0x5A) return IntPtr.Zero;
            int peOff = *(int*)(p + 0x3C);
            byte* nt = p + peOff;
            if (nt[0] != 0x50 || nt[1] != 0x45) return IntPtr.Zero;
            ushort magic = *(ushort*)(nt + 24);
            int expRVA = (magic == 0x20B) ? *(int*)(nt + 24 + 112) : *(int*)(nt + 24 + 96);
            if (expRVA == 0) return IntPtr.Zero;
            byte* exp = p + expRVA;
            int numNames = *(int*)(exp + 24);
            int rvaNames = *(int*)(exp + 32);
            int rvaOrds  = *(int*)(exp + 36);
            int rvaFuncs = *(int*)(exp + 28);
            for (int i = 0; i < numNames; i++)
            {
                int nameRVA = *(int*)(p + rvaNames + i * 4);
                string name = Marshal.PtrToStringAnsi((IntPtr)(p + nameRVA)) ?? "";
                if (name == funcName)
                {
                    ushort ord = *(ushort*)(p + rvaOrds + i * 2);
                    int funcRVA = *(int*)(p + rvaFuncs + ord * 4);
                    return (IntPtr)(p + funcRVA);
                }
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Scans a function for the -38 (XR_ERROR_GRAPHICS_REQUIREMENTS_CALL_MISSING)
    /// assignment and NOPs the conditional branch that leads to it, bypassing the requirements check.
    /// -38 = 0xFFFFFFDA; common patterns: 41 BC DA FF FF FF (MOV R12D,-38),
    ///   B8 DA FF FF FF (MOV EAX,-38), BA DA FF FF FF (MOV EDX,-38), etc.
    /// The conditional branch immediately before the assignment is: 7x xx (short Jcc).
    /// </summary>
    private static unsafe void ScanAndPatchMinus38(IntPtr fnPtr, int scanBytes, string label)
    {
        if (fnPtr == IntPtr.Zero) return;
        byte* p = (byte*)fnPtr;
        const int scanStart = 0x20;

        for (int i = scanStart; i < scanBytes - 6; i++)
        {
            bool is6ByteRex = p[i] == 0x41
                && (p[i+1] == 0xBC || p[i+1] == 0xBD || p[i+1] == 0xBE || p[i+1] == 0xBF)
                && p[i+2] == 0xDA && p[i+3] == 0xFF && p[i+4] == 0xFF && p[i+5] == 0xFF;
            bool is5Byte = (p[i] == 0xB8 || p[i] == 0xB9 || p[i] == 0xBA || p[i] == 0xBB)
                && p[i+1] == 0xDA && p[i+2] == 0xFF && p[i+3] == 0xFF && p[i+4] == 0xFF;
            if (!is6ByteRex && !is5Byte) continue;

            int movOffset = i;
            Log.LogInfo($"  [{label}] Found -38 at +0x{movOffset:X3}: {p[i]:X2} {p[i+1]:X2} {p[i+2]:X2}...");

            // ── Pass 1: near Jcc  0F 8x xx xx xx xx  (6 bytes, unambiguous) ──────────
            for (int j = movOffset - 6; j >= Math.Max(scanStart, movOffset - 64); j--)
            {
                if (p[j] != 0x0F) continue;
                byte op2 = p[j + 1];
                if ((op2 & 0xF0) != 0x80) continue;               // not 0F 80-8F
                int disp32 = *(int*)(p + j + 2);
                int target = j + 6 + disp32;
                if (target <= movOffset) continue;                 // must skip past -38
                Log.LogInfo($"  [{label}] Near Jcc at +0x{j:X3}: 0F {op2:X2} → +0x{target:X3}");

                // Replace 6 bytes with:  E9 <rel32>  90
                // new next-RIP = j+5, so rel32 = target-(j+5)
                int newDisp = target - (j + 5);
                byte* pj = p + j;
                if (!VirtualProtect((IntPtr)pj, (UIntPtr)6, 0x40, out uint oldProt))
                { Log.LogWarning($"  [{label}] VirtualProtect failed"); return; }
                pj[0] = 0xE9;
                pj[1] = (byte)( newDisp        & 0xFF);
                pj[2] = (byte)((newDisp >>  8) & 0xFF);
                pj[3] = (byte)((newDisp >> 16) & 0xFF);
                pj[4] = (byte)((newDisp >> 24) & 0xFF);
                pj[5] = 0x90;
                VirtualProtect((IntPtr)pj, (UIntPtr)6, oldProt, out _);
                FlushInstructionCache(GetCurrentProcess(), (IntPtr)pj, (UIntPtr)6);
                Log.LogInfo($"  [{label}] Near Jcc→JMP patched at +0x{j:X3}  ✓");
                return;
            }

            // ── Pass 2: short Jcc  7x xx  (2 bytes) — with false-positive guard ──────
            // Guard: if the byte before the candidate is an opcode that uses the next
            // byte as a ModRM (80/81/83 group1, 38-3F CMP variants, 84/85 TEST, F6/F7
            // group3), the 7x byte is a displacement or ModRM field, not a Jcc opcode.
            for (int j = movOffset - 2; j >= Math.Max(scanStart, movOffset - 32); j--)
            {
                byte op = p[j];
                if ((op & 0xF0) != 0x70) continue;
                if (j > 0)
                {
                    byte prev = p[j - 1];
                    bool isModRM = prev == 0x80 || prev == 0x81 || prev == 0x82 || prev == 0x83
                        || (prev >= 0x38 && prev <= 0x3F)
                        || prev == 0x84 || prev == 0x85
                        || prev == 0xF6 || prev == 0xF7;
                    if (isModRM)
                    {
                        Log.LogInfo($"  [{label}] Skipping false-positive at +0x{j:X3} (ModRM of 0x{prev:X2})");
                        continue;
                    }
                }
                int target = j + 2 + (sbyte)p[j + 1];
                if (target <= movOffset) continue;
                Log.LogInfo($"  [{label}] Short Jcc at +0x{j:X3}: {op:X2} {p[j+1]:X2} → +0x{target:X3}");
                byte* pj = p + j;
                if (!VirtualProtect((IntPtr)pj, (UIntPtr)2, 0x40, out uint oldProt))
                { Log.LogWarning($"  [{label}] VirtualProtect failed"); return; }
                pj[0] = 0xEB;
                VirtualProtect((IntPtr)pj, (UIntPtr)2, oldProt, out _);
                FlushInstructionCache(GetCurrentProcess(), (IntPtr)pj, (UIntPtr)2);
                Log.LogInfo($"  [{label}] Short Jcc→JMP patched at +0x{j:X3}  ✓");
                return;
            }

            Log.LogInfo($"  [{label}] No patchable Jcc found before -38 at +0x{movOffset:X3}.");
            return;
        }
        Log.LogInfo($"  [{label}] -38 pattern not found in +0x{scanStart:X3}..+0x{scanBytes:X3}.");
    }

    private static unsafe void DumpFunctionBytes(IntPtr fn, int count, string label)
    {
        if (fn == IntPtr.Zero) { Log.LogInfo($"  Dump[{label}]: null"); return; }
        byte* p = (byte*)fn;
        Log.LogInfo($"  Dump[{label}] @0x{fn:X} ({count} bytes):");
        for (int i = 0; i < count; i += 16)
        {
            var sb = new StringBuilder($"    +{i:X3}:");
            int end = Math.Min(i + 16, count);
            for (int j = i; j < end; j++) sb.Append($" {p[j]:X2}");
            Log.LogInfo(sb.ToString());
        }
    }

    /// <summary>
    /// Generalized version of ScanAndPatchMinus38 for any signed 32-bit error code.
    /// Scans fnPtr for MOV reg, errorCode then patches the preceding conditional jump
    /// to skip the error return, exactly as ScanAndPatchMinus38 does for -38.
    /// </summary>
    private static unsafe void ScanAndPatchErrorCode(IntPtr fnPtr, int scanBytes, string label, int errorCode)
    {
        if (fnPtr == IntPtr.Zero) return;
        byte* p = (byte*)fnPtr;
        const int scanStart = 0x10;
        uint ucode = (uint)errorCode;
        byte b0 = (byte)(ucode & 0xFF), b1 = (byte)((ucode >> 8) & 0xFF),
             b2 = (byte)((ucode >> 16) & 0xFF), b3 = (byte)((ucode >> 24) & 0xFF);

        for (int i = scanStart; i < scanBytes - 6; i++)
        {
            // 5-byte: B8/B9/BA/BB <imm32>
            bool is5 = (p[i] == 0xB8 || p[i] == 0xB9 || p[i] == 0xBA || p[i] == 0xBB)
                && p[i+1] == b0 && p[i+2] == b1 && p[i+3] == b2 && p[i+4] == b3;
            // 6-byte REX: 41 BC/BD/BE/BF <imm32>
            bool is6 = p[i] == 0x41
                && (p[i+1] == 0xBC || p[i+1] == 0xBD || p[i+1] == 0xBE || p[i+1] == 0xBF)
                && p[i+2] == b0 && p[i+3] == b1 && p[i+4] == b2 && p[i+5] == b3;
            if (!is5 && !is6) continue;

            int movOffset = i;
            Log.LogInfo($"  [{label}] Found {errorCode} at +0x{movOffset:X3}: {p[i]:X2} {p[i+1]:X2} {p[i+2]:X2}...");

            // Near Jcc (6-byte: 0F 8x xx xx xx xx)
            for (int j = movOffset - 6; j >= Math.Max(scanStart, movOffset - 64); j--)
            {
                if (p[j] != 0x0F) continue;
                byte op2 = p[j + 1];
                if ((op2 & 0xF0) != 0x80) continue;
                int disp32 = *(int*)(p + j + 2);
                int target = j + 6 + disp32;
                if (target <= movOffset) continue;
                Log.LogInfo($"  [{label}] Near Jcc at +0x{j:X3}: 0F {op2:X2} → +0x{target:X3}");
                int newDisp = target - (j + 5);
                byte* pj = p + j;
                if (!VirtualProtect((IntPtr)pj, (UIntPtr)6, 0x40, out uint oldProt)) { Log.LogWarning($"  [{label}] VirtualProtect failed"); return; }
                pj[0] = 0xE9;
                pj[1] = (byte)( newDisp        & 0xFF);
                pj[2] = (byte)((newDisp >>  8) & 0xFF);
                pj[3] = (byte)((newDisp >> 16) & 0xFF);
                pj[4] = (byte)((newDisp >> 24) & 0xFF);
                pj[5] = 0x90;
                VirtualProtect((IntPtr)pj, (UIntPtr)6, oldProt, out _);
                FlushInstructionCache(GetCurrentProcess(), (IntPtr)pj, (UIntPtr)6);
                Log.LogInfo($"  [{label}] Near Jcc→JMP patched at +0x{j:X3}  ✓");
                return;
            }

            // Short Jcc (2-byte: 7x xx)
            for (int j = movOffset - 2; j >= Math.Max(scanStart, movOffset - 32); j--)
            {
                byte op = p[j];
                if ((op & 0xF0) != 0x70) continue;
                if (j > 0) { byte prev = p[j-1]; bool isModRM = prev == 0x80 || prev == 0x81 || prev == 0x83 || (prev >= 0x38 && prev <= 0x3F) || prev == 0x84 || prev == 0x85 || prev == 0xF6 || prev == 0xF7; if (isModRM) continue; }
                int target = j + 2 + (sbyte)p[j + 1];
                if (target <= movOffset) continue;
                Log.LogInfo($"  [{label}] Short Jcc at +0x{j:X3}: {op:X2} {p[j+1]:X2} → +0x{target:X3}");
                byte* pj = p + j;
                if (!VirtualProtect((IntPtr)pj, (UIntPtr)2, 0x40, out uint oldProt)) { Log.LogWarning($"  [{label}] VirtualProtect failed"); return; }
                pj[0] = 0xEB;
                VirtualProtect((IntPtr)pj, (UIntPtr)2, oldProt, out _);
                FlushInstructionCache(GetCurrentProcess(), (IntPtr)pj, (UIntPtr)2);
                Log.LogInfo($"  [{label}] Short Jcc→JMP patched at +0x{j:X3}  ✓");
                return;
            }

            Log.LogInfo($"  [{label}] No patchable Jcc found before {errorCode} at +0x{movOffset:X3}.");
            return;
        }
        Log.LogInfo($"  [{label}] {errorCode} pattern not found in +0x{scanStart:X3}..+0x{scanBytes:X3}.");
    }

    private static void EnumerateExtensions()
    {
        try
        {
            _gpa!(0, "xrEnumerateInstanceExtensionProperties", out IntPtr fn);
            if (fn == IntPtr.Zero) { Log.LogInfo("  xrEnumerateInstanceExtensionProperties not found"); return; }
            var del = Marshal.GetDelegateForFunctionPointer<XrEnumerateExtensionsDelegate>(fn);

            int rc = del(IntPtr.Zero, 0, out uint count, IntPtr.Zero);
            Log.LogInfo($"  VDXR extensions (count={count}, rc={rc}):");

            // XrExtensionProperties: type(4)+pad(4)+next(8)+name(128)+version(4) = 148 bytes, aligned to 152
            const int propSz = 152;
            IntPtr props = Marshal.AllocHGlobal((int)(count * propSz));
            for (int i = 0; i < (int)(count * propSz); i++) Marshal.WriteByte(props + i, 0);
            for (int i = 0; i < (int)count; i++)
                Marshal.WriteInt32(props + i * propSz, 0, 2); // XR_TYPE_EXTENSION_PROPERTIES = 2

            rc = del(IntPtr.Zero, count, out uint countOut, props);
            for (int i = 0; i < (int)countOut; i++)
            {
                IntPtr entry = props + i * propSz;
                string name = Marshal.PtrToStringAnsi(entry + 16) ?? "(null)";
                Log.LogInfo($"    ext[{i}]: '{name}'");
            }
            Marshal.FreeHGlobal(props);
        }
        catch (Exception ex) { Log.LogWarning($"  EnumerateExtensions: {ex.Message}"); }
    }

    private static int XrCreateSession(IntPtr device)
    {
        const int bindSz = 24;
        IntPtr bind = Marshal.AllocHGlobal(bindSz);
        for (int i = 0; i < bindSz; i++) Marshal.WriteByte(bind, i, 0);
        Marshal.WriteInt32(bind, 0, 0x3B9B3378);  // VDXR internal type for XrGraphicsBindingD3D11KHR
        Marshal.WriteIntPtr(bind, 16, device);

        const int infoSz = 32;
        IntPtr info = Marshal.AllocHGlobal(infoSz);
        for (int i = 0; i < infoSz; i++) Marshal.WriteByte(info, i, 0);
        Marshal.WriteInt32(info, 0, 8);           // XR_TYPE_SESSION_CREATE_INFO = 8
        Marshal.WriteIntPtr(info, 8, bind);        // next = &graphicsBinding
        Marshal.WriteInt64(info, 24, (long)_systemId);

        // Use the loader trampoline (_pfnCreateSession) — it maps our loader instance handle
        // to VDXR's internal handle before calling the runtime function.  _pfnCreateSessionDirect
        // is VDXR's internal GPA entry which expects its OWN internal instance handle, not ours.
        int rc = Marshal.GetDelegateForFunctionPointer<XrCreateSessionDelegate>(_pfnCreateSession)(_instance, info, out _session);
        Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(bind);
        return rc;
    }

    private static int PollEvents()
    {
        if (_pfnPollEvent == IntPtr.Zero) return 0;
        const int bufSz = 4016;
        IntPtr buf = Marshal.AllocHGlobal(bufSz);
        int lastState = 0;
        while (true)
        {
            for (int i = 0; i < bufSz; i++) Marshal.WriteByte(buf, i, 0);
            Marshal.WriteInt32(buf, 0, 16); // XR_TYPE_EVENT_DATA_BUFFER
            int rc = Marshal.GetDelegateForFunctionPointer<XrPollEventDelegate>(_pfnPollEvent)(_instance, buf);
            if (rc != 0) break; // XR_EVENT_UNAVAILABLE (4) or error — no more events
            int evtType = Marshal.ReadInt32(buf, 0);
            if (evtType == 18) // XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED
            {
                int state = Marshal.ReadInt32(buf, 24);
                Log.LogInfo($"  XR session state → {state}");
                lastState = state;
                if (state > HighestSessionState) HighestSessionState = state;
            }
        }
        Marshal.FreeHGlobal(buf);
        return lastState;
    }

    /// <summary>Polls all pending XR events, updating HighestSessionState. Returns the latest state seen this call (0 if none).</summary>
    public static int PollEventsPublic() => PollEvents();

    private static int XrBeginSession()
    {
        const int sz = 24;
        IntPtr p = Marshal.AllocHGlobal(sz);
        for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 10); // XR_TYPE_SESSION_BEGIN_INFO = 10
        Marshal.WriteInt32(p, 16, 2); // XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO
        int rc = Marshal.GetDelegateForFunctionPointer<XrBeginSessionDelegate>(_pfnBeginSession)(_session, p);
        Marshal.FreeHGlobal(p);
        return rc;
    }

    private static void GetFn(string name, out IntPtr fn)
    {
        int rc = _gpa!(_instance, name, out fn);
        Log.LogInfo($"  {name}: 0x{fn:X} (rc={rc})");
    }

    private static unsafe IntPtr GetD3D11Device()
    {
        try
        {
            var tex = Texture2D.whiteTexture;
            if (tex == null) return IntPtr.Zero;
            IntPtr native = tex.GetNativeTexturePtr();
            if (native == IntPtr.Zero) return IntPtr.Zero;
            // ID3D11Texture2D vtable: [0]=QI [1]=AddRef [2]=Release [3]=GetDevice
            IntPtr* vtbl = *(IntPtr**)native;
            Marshal.GetDelegateForFunctionPointer<GetD3D11DeviceDelegate>(vtbl[3])(native, out IntPtr dev);
            return dev;
        }
        catch (Exception ex) { Log.LogWarning($"  GetD3D11Device: {ex.Message}"); return IntPtr.Zero; }
    }

    // ── Public stereo rendering API (Phase 5) ─────────────────────────────────

    /// <summary>
    /// Stops the background empty-frame thread.  Call once before taking over
    /// frame timing from the main thread.
    /// </summary>
    public static void StopFrameThread()
    {
        _frameThreadRunning = false;
        if (_frameThread != null) { _frameThread.Join(2000); _frameThread = null; }
        Log.LogInfo("  SoDVR: background frame thread stopped.");
    }

    /// <summary>
    /// One-shot setup: creates reference space, enumerates eye sizes, creates
    /// per-eye swapchains and enumerates their D3D11 image handles.
    /// Call once after the session is running.
    /// </summary>
    public static bool SetupStereo()
    {
        try
        {
            if (_session == 0) { Log.LogError("SetupStereo: no session"); return false; }

            // ── 0. Re-try xrGetD3D11GraphicsRequirementsKHR ────────────────────
            // This call initialises VDXR's D3D11 swapchain backend.  It fails with -1
            // before xrBeginSession, but succeeds once the frame loop has ticked at
            // least once.  Without it xrEnumerateSwapchainFormats returns count=0 and
            // xrCreateSwapchain returns -8.
            if (_pfnGetD3D11GfxReqs != IntPtr.Zero)
            {
                const int reqSz = 32; // XrGraphicsRequirementsD3D11KHR: type+pad+next+LUID+featureLevel
                IntPtr reqp = Marshal.AllocHGlobal(reqSz);
                for (int i = 0; i < reqSz; i++) Marshal.WriteByte(reqp, i, 0);
                Marshal.WriteInt32(reqp, 0, 1000003002); // XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR
                int reqRc = Marshal.GetDelegateForFunctionPointer<XrGetD3D11GfxReqsDelegate>
                    (_pfnGetD3D11GfxReqs)(_instance, _systemId, reqp);
                if (reqRc == 0)
                {
                    int luidLo = Marshal.ReadInt32(reqp, 16);
                    int luidHi = Marshal.ReadInt32(reqp, 20);
                    int fl     = Marshal.ReadInt32(reqp, 24);
                    Log.LogInfo($"  GfxReqs (post-session) rc=0  luid=0x{luidHi:X8}{luidLo:X8}  minFL=0x{fl:X}");
                }
                else
                {
                    Log.LogWarning($"  GfxReqs (post-session) rc={reqRc} — VDXR D3D11 backend may not be ready; swapchain creation will likely fail.");
                }
                Marshal.FreeHGlobal(reqp);
            }

            // ── 1. Reference space (LOCAL, identity pose) ──────────────────────
            // XrReferenceSpaceCreateInfo: type(4) pad(4) next(8) refSpaceType(4) pad(4) pose(28) = 48 bytes
            // XrPosef: orientation(x,y,z,w = 4 floats at +0/4/8/12) position(x,y,z = 3 floats at +16/20/24)
            // pose at offset 20: orientation.x=20,y=24,z=28,w=32  position.x=36,y=40,z=44
            const int rsiSz = 48;
            IntPtr rsi = Marshal.AllocHGlobal(rsiSz);
            for (int i = 0; i < rsiSz; i++) Marshal.WriteByte(rsi, i, 0);
            Marshal.WriteInt32(rsi,  0, 37); // XR_TYPE_REFERENCE_SPACE_CREATE_INFO
            Marshal.WriteInt32(rsi, 16,  2); // XR_REFERENCE_SPACE_TYPE_LOCAL
            WriteFloat(rsi, 32, 1.0f);       // pose.orientation.w = 1 (identity)
            int rc = Marshal.GetDelegateForFunctionPointer<XrCreateReferenceSpaceDelegate>
                (_pfnCreateReferenceSpace)(_session, rsi, out ulong refSpace);
            Marshal.FreeHGlobal(rsi);
            Log.LogInfo($"  xrCreateReferenceSpace rc={rc} space=0x{refSpace:X}");
            if (rc != 0) return false;
            ReferenceSpace = refSpace;

            // ── 2. Per-eye recommended resolution ──────────────────────────────
            // XrViewConfigurationView (OpenXR spec order):
            //   type(4) pad(4) next(8) recW(4) maxW(4) recH(4) maxH(4) recSamples(4) maxSamples(4) = 40 bytes
            //   +16=recommendedImageRectWidth  +20=maxImageRectWidth
            //   +24=recommendedImageRectHeight +28=maxImageRectHeight
            const int vcvSz = 40;
            IntPtr vcvBuf = Marshal.AllocHGlobal(2 * vcvSz);
            for (int i = 0; i < 2 * vcvSz; i++) Marshal.WriteByte(vcvBuf, i, 0);
            Marshal.WriteInt32(vcvBuf,          0, 41); // XR_TYPE_VIEW_CONFIGURATION_VIEW
            Marshal.WriteInt32(vcvBuf + vcvSz,  0, 41);
            rc = Marshal.GetDelegateForFunctionPointer<XrEnumViewConfigViewsDelegate>
                (_pfnEnumViewConfigViews)(_instance, _systemId, 2 /*PRIMARY_STEREO*/, 2, out uint vcvCount, vcvBuf);
            int recW = 0, recH = 0;
            if (rc == 0 && vcvCount >= 1)
            {
                recW = Marshal.ReadInt32(vcvBuf, 16); // recommendedImageRectWidth
                recH = Marshal.ReadInt32(vcvBuf, 24); // recommendedImageRectHeight (NOT +20, that is maxWidth)
                Log.LogInfo($"  Eye recommended: {recW}x{recH}");
            }
            Marshal.FreeHGlobal(vcvBuf);
            if (recW == 0) { recW = 1832; recH = 1920; Log.LogWarning($"  Fallback eye res {recW}x{recH}"); }
            SwapchainWidth  = recW;
            SwapchainHeight = recH;

            // ── 3. Create per-eye swapchains ────────────────────────────────────
            if (!CreateSwapchain(recW, recH, out ulong leftSC) ||
                !CreateSwapchain(recW, recH, out ulong rightSC))
            { Log.LogError("  xrCreateSwapchain failed"); return false; }
            LeftSwapchain  = leftSC;
            RightSwapchain = rightSC;

            // ── 4. Enumerate swapchain images ───────────────────────────────────
            LeftSwapchainImages  = EnumSwapchainImages(leftSC);
            RightSwapchainImages = EnumSwapchainImages(rightSC);
            Log.LogInfo($"  Swapchain images: L={LeftSwapchainImages.Length} R={RightSwapchainImages.Length}");
            for (int i=0;i<LeftSwapchainImages.Length;i++) Log.LogInfo($"  L[{i}]=0x{LeftSwapchainImages[i]:X}");
            for (int i=0;i<RightSwapchainImages.Length;i++) Log.LogInfo($"  R[{i}]=0x{RightSwapchainImages[i]:X}");
            if (LeftSwapchainImages.Length == 0 || RightSwapchainImages.Length == 0)
            { Log.LogError("  Empty swapchain image arrays"); return false; }

            return true;
        }
        catch (Exception ex) { Log.LogError($"SetupStereo: {ex}"); return false; }
    }

    // Preferred DXGI formats for swapchain, in order.
    // 87 = DXGI_FORMAT_B8G8R8A8_UNORM       (matches Unity ARGB32 RT on D3D11 — first choice)
    // 91 = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB  (same layout, gamma-correct)
    // 28 = DXGI_FORMAT_R8G8B8A8_UNORM       (channel-swapped vs ARGB32 — colours wrong but won't crash)
    // 29 = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB  (VDXR rejected this in earlier testing)
    private static readonly long[] _preferredFormats = { 87, 91, 28, 29 };

    private static long PickSwapchainFormat()
    {
        if (_pfnEnumSwapchainFormats == IntPtr.Zero) return 87;
        try
        {
            int rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainFormatsDelegate>
                (_pfnEnumSwapchainFormats)(_session, 0, out uint count, IntPtr.Zero);
            Log.LogInfo($"  xrEnumerateSwapchainFormats(count) rc={rc} count={count}");
            if (rc != 0 || count == 0)
            {
                Log.LogWarning("  ESF count=0 — trying format probe: 28,29,87,91");
                return 28;  // R8G8B8A8_UNORM — likely the format VDXR actually supports
            }

            IntPtr buf = Marshal.AllocHGlobal((int)(count * 8));
            try
            {
                rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainFormatsDelegate>
                    (_pfnEnumSwapchainFormats)(_session, count, out uint countOut, buf);
                if (rc != 0) return 87;

                var supported = new System.Collections.Generic.HashSet<long>();
                var sb = new StringBuilder("  Supported swapchain formats:");
                for (int i = 0; i < (int)countOut; i++)
                {
                    long fmt = Marshal.ReadInt64(buf, i * 8);
                    supported.Add(fmt);
                    sb.Append($" {fmt}");
                }
                Log.LogInfo(sb.ToString());

                foreach (long fmt in _preferredFormats)
                {
                    if (supported.Contains(fmt))
                    {
                        Log.LogInfo($"  Picked swapchain format: {fmt}");
                        return fmt;
                    }
                }

                // None of our preferred formats supported — use first available
                long first = Marshal.ReadInt64(buf, 0);
                Log.LogWarning($"  No preferred format found — using first available: {first}");
                return first;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Log.LogWarning($"  PickSwapchainFormat: {ex.Message}"); return 87; }
    }

    private static bool CreateSwapchain(int w, int h, out ulong swapchain)
    {
        swapchain = 0;
        if (_pfnCreateSwapchain == IntPtr.Zero) return false;
        long fmt = PickSwapchainFormat();
        // XrSwapchainCreateInfo: type(4) pad(4) next(8) createFlags(8) usageFlags(8) format(8)
        //   sampleCount(4) width(4) height(4) faceCount(4) arraySize(4) mipCount(4) = 64 bytes
        const int sz = 64;
        IntPtr p = Marshal.AllocHGlobal(sz);
        for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p,  0, 9);                    // XR_TYPE_SWAPCHAIN_CREATE_INFO
        Marshal.WriteInt64(p, 24, 0x31);                 // usageFlags = COLOR_ATTACHMENT(1)|TRANSFER_DST(0x10)|SAMPLED(0x20)
        Marshal.WriteInt64(p, 32, fmt);                  // DXGI format (selected via PickSwapchainFormat)
        Marshal.WriteInt32(p, 40, 1);                    // sampleCount
        Marshal.WriteInt32(p, 44, w);
        Marshal.WriteInt32(p, 48, h);
        Marshal.WriteInt32(p, 52, 1);                    // faceCount
        Marshal.WriteInt32(p, 56, 1);                    // arraySize
        Marshal.WriteInt32(p, 60, 1);                    // mipCount
        int rc = Marshal.GetDelegateForFunctionPointer<XrCreateSwapchainDelegate>
            (_pfnCreateSwapchain)(_session, p, out swapchain);
        Marshal.FreeHGlobal(p);
        Log.LogInfo($"  xrCreateSwapchain({w}x{h} fmt={fmt}) rc={rc} sc=0x{swapchain:X}");
        return rc == 0 && swapchain != 0;
    }

    private static bool _esiDumped;
    private static IntPtr[] EnumSwapchainImages(ulong sc)
    {
        if (_pfnEnumSwapchainImages == IntPtr.Zero) return System.Array.Empty<IntPtr>();
        try
        {
            // Dump ESI-outer once to check for unpatched guards (same jbe1/jbe2/jz pattern as SC-outer).
            if (!_esiDumped) {
                _esiDumped = true;
                unsafe {
                    byte* esi = (byte*)_pfnEnumSwapchainImages;
                    Log.LogInfo($"  ESI-outer @ 0x{_pfnEnumSwapchainImages:X}");
                    for (int di = 0; di < 0x100; di += 16) {
                        var dsb = new StringBuilder($"  ESI+0x{di:X3}:");
                        for (int dj = 0; dj < 16; dj++) dsb.Append($" {esi[di+dj]:X2}");
                        Log.LogInfo(dsb.ToString());
                    }
                }
            }

            // Dump the swapchain object raw memory (first 0x80 bytes) once.
            // The swapchain handle in VDXR is a direct pointer to the internal swapchain struct.
            // This shows us what SC-member actually stored — specifically whether D3D11
            // texture pointers are anywhere in the object.
            if (sc != 0) unsafe {
                byte* scObj = (byte*)(long)sc;
                Log.LogInfo($"  SC-obj 0x{sc:X} [+0..+7F]:");
                for (int di = 0; di < 0x80; di += 16) {
                    var dsb = new StringBuilder($"    +{di:X2}:");
                    for (int dj = 0; dj < 16; dj++) dsb.Append($" {scObj[di+dj]:X2}");
                    Log.LogInfo(dsb.ToString());
                }
            }

            int rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainImagesDelegate>
                (_pfnEnumSwapchainImages)(sc, 0, out uint count, IntPtr.Zero);
            if (rc != 0 || count == 0) { Log.LogWarning($"  EnumSwapchainImages count rc={rc} n={count}"); return System.Array.Empty<IntPtr>(); }
            // XrSwapchainImageD3D11KHR: type(4) pad(4) next(8) texture(8) = 24 bytes
            const int imgSz = 24;
            IntPtr buf = Marshal.AllocHGlobal((int)(count * imgSz));
            for (int i = 0; i < (int)(count * imgSz); i++) Marshal.WriteByte(buf, i, 0);
            for (int i = 0; i < (int)count; i++)
                Marshal.WriteInt32(buf + i * imgSz, 0, 1000003001); // XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR
            rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainImagesDelegate>
                (_pfnEnumSwapchainImages)(sc, count, out uint countOut, buf);
            Log.LogInfo($"  ESI fill rc={rc} countOut={countOut}");
            // Dump raw struct bytes to find where the texture pointer actually lands
            for (int i = 0; i < (int)countOut && i < 1; i++)
            {
                var sb2 = new StringBuilder($"  ESI raw[{i}]:");
                for (int b = 0; b < imgSz; b += 4)
                    sb2.Append($" +{b:X2}={Marshal.ReadInt32(buf + i*imgSz, b):X8}");
                Log.LogInfo(sb2.ToString());
            }
            var result = new IntPtr[countOut];
            for (int i = 0; i < (int)countOut; i++)
                result[i] = Marshal.ReadIntPtr(buf + i * imgSz, 16); // ID3D11Texture2D* at +16
            Marshal.FreeHGlobal(buf);
            return result;
        }
        catch (Exception ex) { Log.LogWarning($"  EnumSwapchainImages: {ex.Message}"); return System.Array.Empty<IntPtr>(); }
    }

    public static bool AcquireSwapchainImage(ulong sc, out uint index, out int rcOut)
    {
        index = 0; rcOut = -1;
        if (_pfnAcquireSwapchainImage == IntPtr.Zero) return false;
        // XrSwapchainImageAcquireInfo: type(4) pad(4) next(8) = 16 bytes
        IntPtr p = Marshal.AllocHGlobal(16);
        for (int i = 0; i < 16; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 55); // XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO
        rcOut = Marshal.GetDelegateForFunctionPointer<XrAcquireSwapchainImageDelegate>
            (_pfnAcquireSwapchainImage)(sc, p, out index);
        Marshal.FreeHGlobal(p);
        return rcOut == 0;
    }
    public static bool AcquireSwapchainImage(ulong sc, out uint index) => AcquireSwapchainImage(sc, out index, out _);

    public static bool WaitSwapchainImage(ulong sc)
    {
        if (_pfnWaitSwapchainImage == IntPtr.Zero) return false;
        // XrSwapchainImageWaitInfo: type(4) pad(4) next(8) timeout(8) = 24 bytes
        IntPtr p = Marshal.AllocHGlobal(24);
        for (int i = 0; i < 24; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 56);                             // XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO
        Marshal.WriteInt64(p, 16, long.MaxValue);                 // XR_INFINITE_DURATION
        int rc = Marshal.GetDelegateForFunctionPointer<XrWaitSwapchainImageDelegate>
            (_pfnWaitSwapchainImage)(sc, p);
        Marshal.FreeHGlobal(p);
        return rc == 0;
    }

    public static bool ReleaseSwapchainImage(ulong sc)
    {
        if (_pfnReleaseSwapchainImage == IntPtr.Zero) return false;
        // XrSwapchainImageReleaseInfo: type(4) pad(4) next(8) = 16 bytes
        IntPtr p = Marshal.AllocHGlobal(16);
        for (int i = 0; i < 16; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, 57); // XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO
        int rc = Marshal.GetDelegateForFunctionPointer<XrReleaseSwapchainImageDelegate>
            (_pfnReleaseSwapchainImage)(sc, p);
        Marshal.FreeHGlobal(p);
        return rc == 0;
    }

    public struct EyePose
    {
        public UnityEngine.Quaternion Orientation;
        public UnityEngine.Vector3    Position;
        public float FovLeft, FovRight, FovUp, FovDown; // tangent angles in radians
    }

    /// <summary>
    /// xrLocateViews for PRIMARY_STEREO at the given display time.
    /// Returns eye poses (LOCAL space) and per-eye FOV angles.
    /// XrView layout (x64): type(4) pad(4) next(8) pose(28) fov(16) pad(4) = 64 bytes
    ///   pose.orientation at +16, pose.position at +32, fov at +44
    /// </summary>
    public static bool LocateViews(long displayTime, out EyePose left, out EyePose right)
    {
        left = right = default;
        if (_pfnLocateViews == IntPtr.Zero || ReferenceSpace == 0) return false;
        try
        {
            // XrViewLocateInfo: type(4) pad(4) next(8) viewConfigType(4) pad(4) displayTime(8) space(8) = 40 bytes
            IntPtr li = Marshal.AllocHGlobal(40);
            for (int i = 0; i < 40; i++) Marshal.WriteByte(li, i, 0);
            Marshal.WriteInt32(li,  0, 6);                     // XR_TYPE_VIEW_LOCATE_INFO
            Marshal.WriteInt32(li, 16, 2);                     // PRIMARY_STEREO
            Marshal.WriteInt64(li, 24, displayTime);
            Marshal.WriteInt64(li, 32, (long)ReferenceSpace);

            // XrViewState: type(4) pad(4) next(8) viewStateFlags(8) = 24 bytes
            IntPtr vs = Marshal.AllocHGlobal(24);
            for (int i = 0; i < 24; i++) Marshal.WriteByte(vs, i, 0);
            Marshal.WriteInt32(vs, 0, 11); // XR_TYPE_VIEW_STATE

            // XrView[2]: 64 bytes each (see summary above)
            const int viewSz = 64;
            IntPtr vb = Marshal.AllocHGlobal(2 * viewSz);
            for (int i = 0; i < 2 * viewSz; i++) Marshal.WriteByte(vb, i, 0);
            Marshal.WriteInt32(vb + 0,       0, 7); // XR_TYPE_VIEW
            Marshal.WriteInt32(vb + viewSz,  0, 7);

            int rc = Marshal.GetDelegateForFunctionPointer<XrLocateViewsDelegate>(_pfnLocateViews)
                (_session, li, vs, 2, out uint countOut, vb);

            Marshal.FreeHGlobal(li); Marshal.FreeHGlobal(vs);
            if (rc != 0 || countOut < 2) { Marshal.FreeHGlobal(vb); return false; }

            left  = ParseEyeView(vb,             viewSz);
            right = ParseEyeView(vb + viewSz, viewSz);
            Marshal.FreeHGlobal(vb);
            return true;
        }
        catch (Exception ex) { Log.LogWarning($"  LocateViews: {ex.Message}"); return false; }
    }

    private static EyePose ParseEyeView(IntPtr v, int viewSz)
    {
        // Header 16 bytes, then pose.orientation(x,y,z,w) at +16,+20,+24,+28
        // pose.position(x,y,z) at +32,+36,+40 ; fov(L,R,U,D) at +44,+48,+52,+56
        var ep = new EyePose();
        ep.Orientation.x = ReadFloat(v, 16);
        ep.Orientation.y = ReadFloat(v, 20);
        ep.Orientation.z = ReadFloat(v, 24);
        ep.Orientation.w = ReadFloat(v, 28);
        ep.Position.x    = ReadFloat(v, 32);
        ep.Position.y    = ReadFloat(v, 36);
        ep.Position.z    = ReadFloat(v, 40);
        ep.FovLeft       = ReadFloat(v, 44);
        ep.FovRight      = ReadFloat(v, 48);
        ep.FovUp         = ReadFloat(v, 52);
        ep.FovDown       = ReadFloat(v, 56);
        return ep;
    }

    /// <summary>
    /// xrEndFrame with a XrCompositionLayerProjection for both eyes.
    /// XrCompositionLayerProjectionView layout (x64, 96 bytes):
    ///   type(4) pad(4) next(8) pose(28) fov(16) [pad 4] subImage(28+4pad) = 96 bytes
    ///   subImage: swapchain(8) at +64, rect.offset(8) at +72, rect.extent(8) at +80, arrayIdx(4) at +88
    /// XrCompositionLayerProjection (48 bytes):
    ///   type(4) pad(4) next(8) layerFlags(8) space(8) viewCount(4) pad(4) views*(8)
    /// </summary>
    public static void FrameEndStereo(long displayTime,
        EyePose leftEye, EyePose rightEye, uint leftIdx, uint rightIdx)
    {
        if (_pfnEndFrame == IntPtr.Zero || _session == 0) return;
        try
        {
            const int projViewSz = 96;
            IntPtr pv = Marshal.AllocHGlobal(2 * projViewSz);
            for (int i = 0; i < 2 * projViewSz; i++) Marshal.WriteByte(pv, i, 0);
            WriteProjectionView(pv,               leftEye,  LeftSwapchain,  leftIdx);
            WriteProjectionView(pv + projViewSz,  rightEye, RightSwapchain, rightIdx);

            const int plSz = 48;
            IntPtr pl = Marshal.AllocHGlobal(plSz);
            for (int i = 0; i < plSz; i++) Marshal.WriteByte(pl, i, 0);
            Marshal.WriteInt32(pl,  0, 35);                        // XR_TYPE_COMPOSITION_LAYER_PROJECTION
            Marshal.WriteInt64(pl, 24, (long)ReferenceSpace);      // space
            Marshal.WriteInt32(pl, 32, 2);                         // viewCount
            Marshal.WriteIntPtr(pl, 40, pv);                       // *views

            IntPtr lp = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(lp, pl);

            const int eiSz = 40;
            IntPtr ei = Marshal.AllocHGlobal(eiSz);
            for (int i = 0; i < eiSz; i++) Marshal.WriteByte(ei, i, 0);
            Marshal.WriteInt32(ei,  0, 12);           // XR_TYPE_FRAME_END_INFO
            Marshal.WriteInt64(ei, 16, displayTime);
            Marshal.WriteInt32(ei, 24, 1);            // OPAQUE
            Marshal.WriteInt32(ei, 28, 1);            // layerCount = 1
            Marshal.WriteIntPtr(ei, 32, lp);

            _stereoCallCount++;
            bool logThis = _stereoCallCount <= 3 || (_stereoCallCount % 300) == 0;
            if (logThis)
            {
                float qMag = (float)Math.Sqrt(leftEye.Orientation.x * leftEye.Orientation.x
                    + leftEye.Orientation.y * leftEye.Orientation.y
                    + leftEye.Orientation.z * leftEye.Orientation.z
                    + leftEye.Orientation.w * leftEye.Orientation.w);
                Log.LogInfo($"  [FS#{_stereoCallCount}] space=0x{ReferenceSpace:X} L-sc=0x{LeftSwapchain:X} R-sc=0x{RightSwapchain:X}");
                Log.LogInfo($"  [FS#{_stereoCallCount}] leftIdx={leftIdx} rightIdx={rightIdx}  W={SwapchainWidth} H={SwapchainHeight}");
                Log.LogInfo($"  [FS#{_stereoCallCount}] L-quat=({leftEye.Orientation.x:F3},{leftEye.Orientation.y:F3},{leftEye.Orientation.z:F3},{leftEye.Orientation.w:F3}) |q|={qMag:F4}");
                Log.LogInfo($"  [FS#{_stereoCallCount}] L-pos=({leftEye.Position.x:F3},{leftEye.Position.y:F3},{leftEye.Position.z:F3})");
                Log.LogInfo($"  [FS#{_stereoCallCount}] L-fov=({leftEye.FovLeft:F3},{leftEye.FovRight:F3},{leftEye.FovUp:F3},{leftEye.FovDown:F3})");
                Log.LogInfo($"  [FS#{_stereoCallCount}] displayTime={displayTime}  session=0x{_session:X}  lp=0x{lp:X}  pl=0x{pl:X}  pv=0x{pv:X}");
            }

            int rc = Marshal.GetDelegateForFunctionPointer<XrEndFrameDelegate>(_pfnEndFrame)(_session, ei);
            if (rc != 0)
                Log.LogWarning($"  xrEndFrame(stereo)#{_stereoCallCount} rc={rc}  space=0x{ReferenceSpace:X} L-sc=0x{LeftSwapchain:X} R-sc=0x{RightSwapchain:X} lidx={leftIdx} ridx={rightIdx}");

            Marshal.FreeHGlobal(ei); Marshal.FreeHGlobal(lp);
            Marshal.FreeHGlobal(pl); Marshal.FreeHGlobal(pv);
        }
        catch (Exception ex) { Log.LogWarning($"  FrameEndStereo: {ex.Message}"); }
    }

    private static void WriteProjectionView(IntPtr p, EyePose eye, ulong sc, uint imgIdx)
    {
        Marshal.WriteInt32(p, 0, 48);              // XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW
        WriteFloat(p, 16, eye.Orientation.x);
        WriteFloat(p, 20, eye.Orientation.y);
        WriteFloat(p, 24, eye.Orientation.z);
        WriteFloat(p, 28, eye.Orientation.w);
        WriteFloat(p, 32, eye.Position.x);
        WriteFloat(p, 36, eye.Position.y);
        WriteFloat(p, 40, eye.Position.z);
        WriteFloat(p, 44, eye.FovLeft);
        WriteFloat(p, 48, eye.FovRight);
        WriteFloat(p, 52, eye.FovUp);
        WriteFloat(p, 56, eye.FovDown);
        // subImage starts at +64 (4 bytes padding between fov and swapchain handle)
        Marshal.WriteInt64(p, 64, (long)sc);       // swapchain
        Marshal.WriteInt32(p, 72, 0);              // imageRect.offset.x
        Marshal.WriteInt32(p, 76, 0);              // imageRect.offset.y
        Marshal.WriteInt32(p, 80, SwapchainWidth); // imageRect.extent.width
        Marshal.WriteInt32(p, 84, SwapchainHeight);// imageRect.extent.height
        Marshal.WriteInt32(p, 88, 0);              // imageArrayIndex = 0 (non-array swapchain; ring-buffer index is tracked by runtime, not passed here)
    }

    // Public thin wrappers so VRCamera can call frame functions
    public static long FrameWaitPublic(out int rc) => FrameWait(out rc);
    public static int  FrameBeginPublic()           { FrameBegin(out int rc); return rc; }

    /// <summary>Submits xrEndFrame with zero composition layers (keeps session alive).</summary>
    public static void FrameEndEmpty(long displayTime)
    {
        if (_pfnEndFrame == IntPtr.Zero || _session == 0) return;
        try
        {
            IntPtr ei = Marshal.AllocHGlobal(40);
            for (int i = 0; i < 40; i++) Marshal.WriteByte(ei, i, 0);
            Marshal.WriteInt32(ei,  0, 12);          // XR_TYPE_FRAME_END_INFO
            Marshal.WriteInt64(ei, 16, displayTime);
            Marshal.WriteInt32(ei, 24, 1);           // OPAQUE
            Marshal.WriteInt32(ei, 28, 0);           // layerCount = 0
            Marshal.WriteIntPtr(ei, 32, IntPtr.Zero);
            Marshal.GetDelegateForFunctionPointer<XrEndFrameDelegate>(_pfnEndFrame)(_session, ei);
            Marshal.FreeHGlobal(ei);
        }
        catch (Exception ex) { Log.LogWarning($"  FrameEndEmpty: {ex.Message}"); }
    }

    // D3D11 copy helper — copies srcTex into dstTex via ID3D11DeviceContext::CopyResource
    // ID3D11Device vtable[40] = GetImmediateContext
    // ID3D11DeviceContext vtable[47] = CopyResource
    private static int _copyLogCount;
    public static unsafe void D3D11CopyTexture(IntPtr srcTex, IntPtr dstTex)
    {
        try
        {
            bool logCopy = ++_copyLogCount <= 6;
            if (logCopy) Log.LogInfo($"  D3D11Copy src=0x{srcTex:X} dst=0x{dstTex:X}");
            if (srcTex == IntPtr.Zero || dstTex == IntPtr.Zero) { if (logCopy) Log.LogWarning("  D3D11Copy: null pointer — skip"); return; }
            IntPtr dev = GetD3D11Device();
            if (dev == IntPtr.Zero) { if (logCopy) Log.LogWarning("  D3D11Copy: no device"); return; }
            IntPtr* devVtbl = *(IntPtr**)dev;
            Marshal.GetDelegateForFunctionPointer<D3D11GetImmediateContextDelegate>(devVtbl[40])(dev, out IntPtr ctx);
            if (ctx == IntPtr.Zero) { if (logCopy) Log.LogWarning("  D3D11Copy: no context"); return; }
            IntPtr* ctxVtbl = *(IntPtr**)ctx;
            if (logCopy) Log.LogInfo($"  D3D11Copy dev=0x{dev:X} ctx=0x{ctx:X} — calling CopyResource");
            Marshal.GetDelegateForFunctionPointer<D3D11CopyResourceDelegate>(ctxVtbl[47])(ctx, dstTex, srcTex);
            if (logCopy) Log.LogInfo("  D3D11Copy done");
        }
        catch (Exception ex) { Log.LogWarning($"  D3D11CopyTexture: {ex.Message}"); }
    }

    private static float ReadFloat(IntPtr p, int offset)
        => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, offset));

    private static void WriteFloat(IntPtr p, int offset, float value)
        => Marshal.WriteInt32(p, offset, BitConverter.SingleToInt32Bits(value));

    private static void LogActiveRuntime()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            var path = key?.GetValue("ActiveRuntime") as string;
            Log.LogInfo($"Active OpenXR runtime: {path ?? "not found"}");
        }
        catch { }
    }

    // ── Frame submission loop ─────────────────────────────────────────────────
    // Runs in a background thread.  Submits empty frames (0 composition layers)
    // so that VD detects an active OpenXR app and switches from desktop to VR
    // streaming mode.  xrWaitFrame may block until VD is ready; running in a
    // thread prevents Unity's main thread from stalling.

    private static void StartFrameThread()
    {
        if (_frameThread != null) return;
        if (_pfnWaitFrame == IntPtr.Zero || _pfnBeginFrame == IntPtr.Zero || _pfnEndFrame == IntPtr.Zero)
        {
            Log.LogWarning("  StartFrameThread: missing frame function pointers — VD may not switch to VR mode.");
            return;
        }
        _frameThreadRunning = true;
        _frameThread = new System.Threading.Thread(FrameThreadProc)
        {
            IsBackground = true,
            Name = "SoDVR-Frame"
        };
        _frameThread.Start();
        Log.LogInfo("  Frame submission thread started.");
    }

    private static void FrameThreadProc()
    {
        int frameCount = 0;
        int consecErrors = 0;
        try
        {
            while (_frameThreadRunning && _session != 0)
            {
                // xrWaitFrame — blocks until the runtime is ready for the next frame.
                // Returns the predicted display time we should pass to xrEndFrame.
                long displayTime = FrameWait(out int waitRc);

                // Negative rc = genuine error.  Positive rc (1=TIMEOUT_EXPIRED,
                // 3=SESSION_LOSS_PENDING) is still a valid return; displayTime
                // should be non-zero and we must still call Begin+End.
                if (waitRc < 0)
                {
                    consecErrors++;
                    if ((consecErrors % 60) == 1)
                        Log.LogWarning($"  FrameThread: xrWaitFrame rc={waitRc} (error #{consecErrors})");
                    if (consecErrors > 600)
                    {
                        Log.LogError("  FrameThread: too many consecutive xrWaitFrame errors — stopping.");
                        break;
                    }
                    System.Threading.Thread.Sleep(16);
                    continue;
                }
                consecErrors = 0;

                // Use a non-zero display time even if runtime returned 0
                // (shouldn't happen, but guard against it to avoid runtime assert)
                if (displayTime == 0) displayTime = 1;

                int rcBegin = (int)FrameBegin(out int _);
                int rcEnd   = (int)FrameEnd(displayTime, out int _);

                frameCount++;
                if (rcBegin != 0 || rcEnd != 0)
                    Log.LogWarning($"  FrameThread: frame={frameCount} rcBegin={rcBegin} rcEnd={rcEnd}");

                // XR_SESSION_LOSS_PENDING (3): session is being destroyed.
                if (waitRc == 3) { Log.LogWarning("  FrameThread: XR_SESSION_LOSS_PENDING — exiting frame loop."); break; }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"  FrameThread: unhandled exception after {frameCount} frames: {ex.Message}");
        }
        Log.LogInfo($"  FrameThread: exited (frames={frameCount}).");
    }

    /// <summary>
    /// Calls xrWaitFrame; returns predictedDisplayTime (may be 0 if runtime hasn't set it yet).
    /// rc &lt; 0 = error; rc = 0 = success; rc = 1 = TIMEOUT_EXPIRED; rc = 3 = SESSION_LOSS_PENDING.
    /// </summary>
    private static long FrameWait(out int rc)
    {
        rc = -1;
        if (_pfnWaitFrame == IntPtr.Zero || _session == 0) return 0;
        try
        {
            // XrFrameWaitInfo: type(4) pad(4) next(8) = 16 bytes
            IntPtr waitInfo = Marshal.AllocHGlobal(16);
            for (int i = 0; i < 16; i++) Marshal.WriteByte(waitInfo, i, 0);
            Marshal.WriteInt32(waitInfo, 0, 33); // XR_TYPE_FRAME_WAIT_INFO = 33

            // XrFrameState: type(4) pad(4) next(8) predictedDisplayTime(8) predictedDisplayPeriod(8) shouldRender(4) pad(4) = 40 bytes
            IntPtr frameState = Marshal.AllocHGlobal(40);
            for (int i = 0; i < 40; i++) Marshal.WriteByte(frameState, i, 0);
            Marshal.WriteInt32(frameState, 0, 44); // XR_TYPE_FRAME_STATE = 44

            rc = Marshal.GetDelegateForFunctionPointer<XrWaitFrameDelegate>(_pfnWaitFrame)(_session, waitInfo, frameState);
            long displayTime = Marshal.ReadInt64(frameState, 16); // predictedDisplayTime at offset 16
            int  shouldRender = Marshal.ReadInt32(frameState, 32);

            Marshal.FreeHGlobal(waitInfo);
            Marshal.FreeHGlobal(frameState);
            return displayTime;
        }
        catch (Exception ex) { Log.LogWarning($"  FrameWait: {ex.Message}"); return 0; }
    }

    /// <summary>Calls xrBeginFrame; rc = xrResult.</summary>
    private static long FrameBegin(out int rc)
    {
        rc = -1;
        if (_pfnBeginFrame == IntPtr.Zero || _session == 0) return -1;
        try
        {
            // XrFrameBeginInfo: type(4) pad(4) next(8) = 16 bytes
            IntPtr beginInfo = Marshal.AllocHGlobal(16);
            for (int i = 0; i < 16; i++) Marshal.WriteByte(beginInfo, i, 0);
            Marshal.WriteInt32(beginInfo, 0, 46); // XR_TYPE_FRAME_BEGIN_INFO = 46
            rc = Marshal.GetDelegateForFunctionPointer<XrBeginFrameDelegate>(_pfnBeginFrame)(_session, beginInfo);
            Marshal.FreeHGlobal(beginInfo);
            return rc;
        }
        catch (Exception ex) { Log.LogWarning($"  FrameBegin: {ex.Message}"); return -1; }
    }

    /// <summary>Calls xrEndFrame with zero composition layers; rc = xrResult.</summary>
    private static long FrameEnd(long displayTime, out int rc)
    {
        rc = -1;
        if (_pfnEndFrame == IntPtr.Zero || _session == 0) return -1;
        try
        {
            // XrFrameEndInfo: type(4) pad(4) next(8) displayTime(8) blendMode(4) layerCount(4) layers(8) = 40 bytes
            IntPtr endInfo = Marshal.AllocHGlobal(40);
            for (int i = 0; i < 40; i++) Marshal.WriteByte(endInfo, i, 0);
            Marshal.WriteInt32(endInfo,  0, 12);           // XR_TYPE_FRAME_END_INFO = 12
            Marshal.WriteInt64(endInfo, 16, displayTime);  // displayTime
            Marshal.WriteInt32(endInfo, 24, 1);            // XR_ENVIRONMENT_BLEND_MODE_OPAQUE = 1
            Marshal.WriteInt32(endInfo, 28, 0);            // layerCount = 0
            Marshal.WriteIntPtr(endInfo, 32, IntPtr.Zero); // layers = null
            rc = Marshal.GetDelegateForFunctionPointer<XrEndFrameDelegate>(_pfnEndFrame)(_session, endInfo);
            Marshal.FreeHGlobal(endInfo);
            return rc;
        }
        catch (Exception ex) { Log.LogWarning($"  FrameEnd: {ex.Message}"); return -1; }
    }
}
