using BepInEx.Logging;
using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SoDVR;

/// <summary>
/// Bootstraps OpenXR via the standard openxr_loader.dll P/Invoke path.
///
/// Init sequence:
///   TryInitializeProvider  — load openxr_loader.dll, grab xrGetInstanceProcAddr
///   CheckAndStart          — SetupDirect (once), then TryDirectPath each frame
///   SetupDirect            — xrCreateInstance → xrGetSystem → cache fn ptrs
///   TryDirectPath          — xrGetD3D11GfxReqs → xrCreateSession → xrBeginSession → frame thread
/// </summary>
public static class OpenXRManager
{
    private static ManualLogSource Log => Plugin.Log;
    public static bool IsRunning { get; private set; }

    // ── State ─────────────────────────────────────────────────────────────────

    private static IntPtr _gpaAddr;          // xrGetInstanceProcAddr from openxr_loader.dll
    private static ulong _instance, _systemId, _session;
    private static IntPtr _pfnGetSystem, _pfnCreateSession, _pfnBeginSession,
                          _pfnDestroySession, _pfnDestroyInstance, _pfnPollEvent,
                          _pfnGetD3D11GfxReqs;
    private static XrGetProcAddrDelegate? _gpa;
    private static bool _directReady;        // SetupDirect() completed
    private static bool _setupFailed;        // TryInitializeProvider or SetupDirect gave up — stop retrying
    private static bool _gfxReqsDone;        // xrGetD3D11GraphicsRequirementsKHR called
    private static int  _sessionRetries;     // counts xrCreateSession attempts (for log throttle)

    /// <summary>Highest OpenXR session state seen via xrPollEvent (0=unknown, 1=IDLE, 2=READY, 3=SYNCHRONIZED, 4=VISIBLE, 5=FOCUSED).</summary>
    public static int HighestSessionState { get; private set; }

    // Frame-submission loop (background thread — keeps VD in VR streaming mode)
    private static IntPtr _pfnWaitFrame, _pfnBeginFrame, _pfnEndFrame;
    private static System.Threading.Thread? _frameThread;
    private static volatile bool _frameThreadRunning;

    // Stereo rendering
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
    private static int     _stereoCallCount;


    // ── Delegate types ────────────────────────────────────────────────────────

    // OpenXR runtime negotiation (bypasses Khronos loader + all implicit API layers)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XrNegotiateLoaderRuntimeDelegate(IntPtr loaderInfo, IntPtr runtimeReq);

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
    // DXGI COM vtable helpers — used to read the D3D11 adapter LUID
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  COMQueryInterfaceDelegate(IntPtr pThis, IntPtr riid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint COMReleaseDelegate(IntPtr pThis);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  DXGIGetAdapterDelegate(IntPtr pThis, out IntPtr ppAdapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int  DXGIGetDescDelegate(IntPtr pThis, IntPtr pDesc);
    // Frame-loop
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

    // ── Win32 ─────────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [DllImport("kernel32.dll")] static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string p);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr hMod, string name);
    // GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4
    [DllImport("kernel32.dll")] static extern bool GetModuleHandleEx(uint dwFlags, IntPtr lpModuleName, out IntPtr phModule);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetModuleFileNameW(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Called in Initializer.Start() — loads the active OpenXR runtime DLL directly and grabs xrGetInstanceProcAddr.
    /// We bypass openxr_loader.dll intentionally: the Khronos loader picks up implicit API layers from the Windows
    /// registry (e.g. UnityOpenXR), which intercept xrCreateSession and enforce their own GfxReqs-called check
    /// independently of VDXR's state.  Loading the runtime DLL directly skips the entire layer chain.</summary>
    public static bool TryInitializeProvider()
    {
        try
        {
            LogActiveRuntime();

            // Resolve the active runtime DLL path from the registry JSON manifest.
            string? runtimeDll = GetRuntimeDllPath();
            if (runtimeDll == null) { Log.LogError("  Could not determine active OpenXR runtime DLL path"); _setupFailed = true; return false; }
            Log.LogInfo($"  Loading runtime directly: {runtimeDll}");

            // VDXR does not export xrGetInstanceProcAddr and rejects direct negotiation.
            // We must go through the Khronos openxr_loader.dll, but we disable the Unity
            // implicit API layer (registered in the Windows registry) before xrCreateInstance
            // so it does not intercept xrCreateSession with its own GfxReqs-called check.
            _ = runtimeDll; // path resolved but we use the loader, not the runtime DLL directly

            string plugins = System.IO.Path.Combine(Application.dataPath, "Plugins/x86_64");
            IntPtr hLoader = GetModuleHandle("openxr_loader");
            if (hLoader == IntPtr.Zero)
                hLoader = LoadLibraryW(System.IO.Path.Combine(plugins, "openxr_loader.dll"));
            if (hLoader == IntPtr.Zero) { Log.LogError($"  openxr_loader.dll not found (LastError={Marshal.GetLastWin32Error()})"); _setupFailed = true; return false; }

            _gpaAddr = GetProcAddress(hLoader, "xrGetInstanceProcAddr");
            if (_gpaAddr == IntPtr.Zero) { Log.LogError("  xrGetInstanceProcAddr not found in openxr_loader.dll"); _setupFailed = true; return false; }
            Log.LogInfo($"  xrGetInstanceProcAddr: 0x{_gpaAddr:X}");

            return true;
        }
        catch (Exception ex) { Log.LogError($"TryInitializeProvider: {ex}"); return false; }
    }

    /// <summary>Reads HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime, parses the JSON for library_path,
    /// and returns the absolute path to the runtime DLL.  Returns null on any failure.</summary>
    private static string? GetRuntimeDllPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1");
            var jsonPath = key?.GetValue("ActiveRuntime") as string;
            if (string.IsNullOrEmpty(jsonPath) || !System.IO.File.Exists(jsonPath)) return null;

            string json = System.IO.File.ReadAllText(jsonPath);
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"library_path\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) return null;

            string libPath = m.Groups[1].Value.Replace('/', '\\');
            if (!System.IO.Path.IsPathRooted(libPath))
                libPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(jsonPath)!, libPath);
            return System.IO.Path.GetFullPath(libPath);
        }
        catch (Exception ex) { Log.LogWarning($"  GetRuntimeDllPath: {ex.Message}"); return null; }
    }

    /// <summary>
    /// Reads HKLM and HKCU OpenXR implicit API layer registry keys, finds any layer whose JSON
    /// mentions "unity" (case-insensitive), extracts its disable_environment variable, and sets
    /// it to "1" so the Khronos loader skips it during xrCreateInstance.
    /// Must be called before any xrGetInstanceProcAddr / xrCreateInstance calls.
    /// </summary>
    private static void DisableUnityOpenXRLayer()
    {
        try
        {
            const string regPath = @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit";
            int total = 0, disabled = 0;
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(regPath);
                if (key == null) { Log.LogInfo($"  [{(hive == Microsoft.Win32.Registry.LocalMachine ? "HKLM" : "HKCU")}] {regPath} — key not found"); continue; }
                var names = key.GetValueNames();
                Log.LogInfo($"  [{(hive == Microsoft.Win32.Registry.LocalMachine ? "HKLM" : "HKCU")}] {regPath} — {names.Length} value(s)");
                foreach (var jsonPath in names)
                {
                    total++;
                    bool exists = System.IO.File.Exists(jsonPath);
                    Log.LogInfo($"    Layer JSON: {jsonPath} (exists={exists})");
                    if (!exists) continue;
                    string json = System.IO.File.ReadAllText(jsonPath);
                    var m = System.Text.RegularExpressions.Regex.Match(json,
                        "\"disable_environment\"\\s*:\\s*\"([^\"]+)\"",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string envVar = m.Groups[1].Value;
                        Environment.SetEnvironmentVariable(envVar, "1");
                        Log.LogInfo($"      → disabled: set {envVar}=1");
                        disabled++;
                    }
                    else
                        Log.LogWarning($"      → no disable_environment, cannot disable");
                }
            }
            if (total == 0) Log.LogInfo("  No implicit API layers registered in registry.");
        }
        catch (Exception ex) { Log.LogWarning($"  DisableUnityOpenXRLayer: {ex.Message}"); }
    }

    /// <summary>
    /// Calls xrNegotiateLoaderRuntimeInterface on the loaded runtime DLL to obtain the runtime's
    /// own xrGetInstanceProcAddr.  Struct layout (64-bit, both structs are 40 bytes):
    ///   XrNegotiateLoaderInfo:    [0] structType(4) [4] structVersion(4) [8] structSize(8)
    ///                             [16] minInterface(4) [20] maxInterface(4)
    ///                             [24] minApiVersion(8) [32] maxApiVersion(8)
    ///   XrNegotiateRuntimeRequest:[0] structType(4) [4] structVersion(4) [8] structSize(8)
    ///                             [16] runtimeInterface(4) [20] pad(4)
    ///                             [24] runtimeApiVersion(8) [32] getInstanceProcAddr(8)
    /// </summary>
    private static unsafe bool TryNegotiateRuntime(IntPtr hRuntime)
    {
        IntPtr pfn = GetProcAddress(hRuntime, "xrNegotiateLoaderRuntimeInterface");
        if (pfn == IntPtr.Zero) { Log.LogError("  xrNegotiateLoaderRuntimeInterface not found"); return false; }
        Log.LogInfo($"  xrNegotiateLoaderRuntimeInterface: 0x{pfn:X}");

        const int sz = 40;
        IntPtr info = Marshal.AllocHGlobal(sz);
        IntPtr req  = Marshal.AllocHGlobal(sz);
        try
        {
            for (int i = 0; i < sz; i++) { Marshal.WriteByte(info, i, 0); Marshal.WriteByte(req, i, 0); }

            // XrNegotiateLoaderInfo
            Marshal.WriteInt32(info,  0, 1);            // structType = LOADER_INFO
            Marshal.WriteInt32(info,  4, 1);            // structVersion = 1
            Marshal.WriteInt64(info,  8, sz);           // structSize
            Marshal.WriteInt32(info, 16, 1);            // minInterfaceVersion
            Marshal.WriteInt32(info, 20, 5);            // maxInterfaceVersion
            Marshal.WriteInt64(info, 24, (long)(1UL << 48));   // minApiVersion = XR_MAKE_VERSION(1,0,0)
            Marshal.WriteInt64(info, 32, unchecked((long)((1UL << 48) | 0xFFFFFFFFFFFFUL))); // maxApiVersion

            // XrNegotiateRuntimeRequest
            Marshal.WriteInt32(req,  0, 3);             // structType = RUNTIME_REQUEST
            Marshal.WriteInt32(req,  4, 1);             // structVersion = 1
            Marshal.WriteInt64(req,  8, sz);            // structSize

            int rc = Marshal.GetDelegateForFunctionPointer<XrNegotiateLoaderRuntimeDelegate>(pfn)(info, req);
            Log.LogInfo($"  xrNegotiateLoaderRuntimeInterface rc={rc}");
            if (rc != 0) return false;

            // getInstanceProcAddr is at offset 32 in XrNegotiateRuntimeRequest
            _gpaAddr = Marshal.ReadIntPtr(req, 32);
            return _gpaAddr != IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(req); }
    }

    public static void TriggerLoad() { }

    /// <summary>Called each frame until it returns true.</summary>
    public static bool CheckAndStart()
    {
        try
        {
            if (IsRunning) return true;
            if (_setupFailed) return false;
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
            _frameThreadRunning = false;
            if (_frameThread != null)
            {
                _frameThread.Join(2000);
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

    // ── Setup ─────────────────────────────────────────────────────────────────

    private static void SetupDirect()
    {
        try
        {
            if (_gpaAddr == IntPtr.Zero)
            {
                Log.LogError("  SetupDirect: _gpaAddr is zero — TryInitializeProvider did not succeed.");
                _setupFailed = true;
                return;
            }
            Log.LogInfo("  Setting up OpenXR via standard loader...");
            _gpa = Marshal.GetDelegateForFunctionPointer<XrGetProcAddrDelegate>(_gpaAddr);

            // Must run before EnumerateExtensions/CreateInstance — the Khronos loader reads
            // implicit API layer manifests from the Windows registry at xrCreateInstance time.
            // Unity's OpenXR layer intercepts xrCreateSession and enforces its own GfxReqs-called
            // check independently of VDXR's state.  Disabling it via its own disable_environment
            // variable prevents the -38 error.
            DisableUnityOpenXRLayer();

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

            // Log which DLL owns each function pointer — reveals if UnityOpenXR is in the chain.
            LogFnOwner("xrGetSystem",      _pfnGetSystem);
            LogFnOwner("xrCreateSession",  _pfnCreateSession);
            LogFnOwner("xrGetD3D11GfxReq", _pfnGetD3D11GfxReqs);
            DumpVtableFunctions();

            rc = XrGetSystem();
            if (rc != 0 || _systemId == 0) { Log.LogError($"  xrGetSystem rc={rc}"); return; }
            Log.LogInfo($"  xrSystemId=0x{_systemId:X}");

            _directReady = true;
        }
        catch (Exception ex) { Log.LogError($"  SetupDirect: {ex}"); _setupFailed = true; }
    }

    private static bool TryDirectPath()
    {
        if (_session == 0)
        {
            IntPtr dev = GetD3D11Device();
            if (dev == IntPtr.Zero) return false;

            if (!_gfxReqsDone)
            {
                int gfxRc = CallD3D11GraphicsRequirements();
                if (gfxRc == 0)
                {
                    Log.LogInfo("  xrGetD3D11GraphicsRequirementsKHR succeeded.");
                    _gfxReqsDone = true;
                }
                else if (_sessionRetries == 0)
                {
                    // First attempt failed — log and do robj write, keep retrying gfxReqs each frame
                    Log.LogWarning($"  xrGetD3D11GraphicsRequirementsKHR rc={gfxRc} — writing robj directly.");
                    TrySetGraphicsRequirementsDirectly(dev);
                }
            }

            int rc = XrCreateSession(dev);
            _sessionRetries++;
            if (rc != 0)
            {
                if (_sessionRetries == 1 || (_sessionRetries % 300) == 0)
                    Log.LogWarning($"  xrCreateSession rc={rc} (attempt {_sessionRetries})");
                return false;
            }
            if (_session == 0)
            {
                if (_sessionRetries == 1 || (_sessionRetries % 300) == 0)
                    Log.LogWarning($"  xrCreateSession rc=0 but session=0 (attempt {_sessionRetries})");
                return false;
            }
            Log.LogInfo($"  xrSession=0x{_session:X}");

            PollEvents();

            int rcBegin = XrBeginSession();
            Log.LogInfo($"  xrBeginSession rc={rcBegin}");
            if (rcBegin != 0 && rcBegin != -12) // -12 = XR_ERROR_SESSION_NOT_READY
            {
                Log.LogError($"  xrBeginSession fatal rc={rcBegin} — resetting session.");
                _session = 0; _gfxReqsDone = false; _sessionRetries = 0;
                return false;
            }

            if (rcBegin == -12)
            {
                Log.LogInfo("  xrBeginSession SESSION_NOT_READY — waiting for READY event.");
                // Fall through to event-polling loop
            }
            else
            {
                StartFrameThread();
                Log.LogInfo("  Direct OpenXR session running.");
                IsRunning = true;
                return true;
            }
        }

        // Poll for STATE_CHANGED events and retry xrBeginSession on READY (state=2).
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

    private static int CallD3D11GraphicsRequirements()
    {
        if (_pfnGetD3D11GfxReqs == IntPtr.Zero || _systemId == 0) return -1;
        const int sz = 32;
        IntPtr p = Marshal.AllocHGlobal(sz);
        try
        {
            for (int i = 0; i < sz; i++) Marshal.WriteByte(p, i, 0);
            Marshal.WriteInt32(p, 0, unchecked((int)0x3B9B337A)); // VDXR expects 0x3B9B337A, not spec 1000003002
            int rc = Marshal.GetDelegateForFunctionPointer<XrGetD3D11GfxReqsDelegate>(
                         _pfnGetD3D11GfxReqs)(_instance, _systemId, p);
            if (rc == 0)
            {
                int luidLo = Marshal.ReadInt32(p, 16);
                int luidHi = Marshal.ReadInt32(p, 20);
                Log.LogInfo($"  xrGetD3D11GraphicsRequirementsKHR rc=0 luid=0x{luidHi:X8}{luidLo:X8}");
            }
            else
                Log.LogWarning($"  xrGetD3D11GraphicsRequirementsKHR rc={rc}");
            return rc;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>
    /// Reads the CALL at xrGetD3D11GraphicsRequirementsKHR+0x143 to find the VDXR
    /// singleton getter, calls it, and returns the OpenXrRuntime* (the real XrInstance
    /// handle that VDXR's own functions expect).
    /// </summary>
    private static unsafe IntPtr GetVDXRRuntimeObject()
    {
        if (_pfnGetD3D11GfxReqs == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            byte* fn = (byte*)_pfnGetD3D11GfxReqs;
            if (fn[0x143] != 0xE8) return IntPtr.Zero;
            int disp = *(int*)(fn + 0x144);
            IntPtr getter = (IntPtr)((long)_pfnGetD3D11GfxReqs + 0x148 + disp);
            var robj = Marshal.GetDelegateForFunctionPointer<VDXRGetSingletonDelegate>(getter)();
            Log.LogInfo($"  GetVDXRRuntimeObject: robj=0x{robj:X}");
            return robj;
        }
        catch (Exception ex) { Log.LogWarning($"  GetVDXRRuntimeObject: {ex.Message}"); return IntPtr.Zero; }
    }

    /// <summary>
    /// Writes m_graphicsRequirementQueried=true and m_adapterLuid directly into the VDXR
    /// runtime object so that VDXR's own xrCreateSession check passes.
    /// Pattern: scan robj for (0x01, 0x01, 0x00×6) = (m_instanceCreated, m_systemCreated, padding).
    /// m_graphicsRequirementQueried is at pattern+32, m_adapterLuid at pattern+40.
    /// </summary>
    private static unsafe bool TrySetGraphicsRequirementsDirectly(IntPtr d3dDevice)
    {
        // Scan xrCreateSession for the -38 (0xFFFFFFDA) constant and dump context bytes.
        // This reveals exactly where the gfxReqs check is and what instruction pattern surrounds it.
        if (_pfnCreateSession != IntPtr.Zero)
        {
            byte* fn = (byte*)_pfnCreateSession;
            for (int i = 4; i < 3000; i++)
            {
                if (fn[i] == 0xDA && fn[i+1] == 0xFF && fn[i+2] == 0xFF && fn[i+3] == 0xFF)
                {
                    // Dump 16 bytes before and 8 bytes after the -38 constant
                    var sb2 = new System.Text.StringBuilder();
                    for (int j = i - 16; j < i + 8; j++) sb2.Append($"{fn[j]:X2} ");
                    Log.LogInfo($"  createSession+0x{i:X}: -38 const → [{sb2}]");
                }
            }
        }

        IntPtr robj = GetVDXRRuntimeObject();
        if (robj == IntPtr.Zero) { Log.LogWarning("  TrySetGfxReqsDirect: robj not found"); return false; }

        byte* p = (byte*)robj;
        int off = -1;
        try
        {
            for (int i = 0; i <= 4088; i++)
                if (p[i]==1 && p[i+1]==1 && p[i+2]==0 && p[i+3]==0 &&
                    p[i+4]==0 && p[i+5]==0 && p[i+6]==0 && p[i+7]==0)
                    { off = i; break; }
        }
        catch (Exception ex) { Log.LogWarning($"  TrySetGfxReqsDirect: scan fault: {ex.Message}"); return false; }

        if (off < 0) { Log.LogWarning("  TrySetGfxReqsDirect: pattern not found"); return false; }

        Log.LogInfo($"  TrySetGfxReqsDirect: pattern@robj+0x{off:X} → writing gfxQueried@+0x{off+32:X}");
        p[off + 32] = 0x01;                                   // m_graphicsRequirementQueried = true
        *(long*)(p + off + 40) = GetAdapterLuid(d3dDevice);   // m_adapterLuid
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VDXRGetSingletonDelegate();

    private static int XrCreateSession(IntPtr device)
    {
        const int bindSz = 24;
        IntPtr bind = Marshal.AllocHGlobal(bindSz);
        for (int i = 0; i < bindSz; i++) Marshal.WriteByte(bind, i, 0);
        Marshal.WriteInt32(bind, 0, unchecked((int)0x3B9B3378));  // VDXR: 0x3B9B3378 (spec 1000003000 + VDXR offset)
        Marshal.WriteIntPtr(bind, 16, device);

        const int infoSz = 32;
        IntPtr info = Marshal.AllocHGlobal(infoSz);
        for (int i = 0; i < infoSz; i++) Marshal.WriteByte(info, i, 0);
        Marshal.WriteInt32(info, 0, 8);           // XR_TYPE_SESSION_CREATE_INFO = 8
        Marshal.WriteIntPtr(info, 8, bind);        // next = &graphicsBinding
        Marshal.WriteInt64(info, 24, (long)_systemId);

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

    /// <summary>
    /// Returns the 64-bit adapter LUID for the given ID3D11Device by walking the
    /// COM vtable chain:  ID3D11Device → IDXGIDevice::GetAdapter → IDXGIAdapter::GetDesc.
    /// Returns 0 if any step fails.
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
            iidBuf = Marshal.AllocHGlobal(16);
            byte[] iid = { 0xFA,0x77,0xEC,0x54, 0x77,0x13, 0xE6,0x44,
                           0x8C,0x32,0x88,0xFD,0x5F,0x44,0xC8,0x4C };
            for (int i = 0; i < 16; i++) Marshal.WriteByte(iidBuf, i, iid[i]);

            IntPtr devVtbl = Marshal.ReadIntPtr(d3dDevice);
            int hr = Marshal.GetDelegateForFunctionPointer<COMQueryInterfaceDelegate>(
                         Marshal.ReadIntPtr(devVtbl, 0))(d3dDevice, iidBuf, out dxgiDev);
            if (hr != 0) { Log.LogWarning($"  GetAdapterLuid: QI hr=0x{(uint)hr:X8}"); return 0; }

            IntPtr dxgiVtbl = Marshal.ReadIntPtr(dxgiDev);
            int hr2 = Marshal.GetDelegateForFunctionPointer<DXGIGetAdapterDelegate>(
                          Marshal.ReadIntPtr(dxgiVtbl, 7 * IntPtr.Size))(dxgiDev, out adapter);
            if (hr2 != 0) { Log.LogWarning($"  GetAdapterLuid: GetAdapter hr=0x{(uint)hr2:X8}"); return 0; }

            IntPtr adapVtbl = Marshal.ReadIntPtr(adapter);
            descBuf = Marshal.AllocHGlobal(304);
            for (int i = 0; i < 304; i++) Marshal.WriteByte(descBuf, i, 0);
            int hr3 = Marshal.GetDelegateForFunctionPointer<DXGIGetDescDelegate>(
                          Marshal.ReadIntPtr(adapVtbl, 8 * IntPtr.Size))(adapter, descBuf);
            if (hr3 != 0) { Log.LogWarning($"  GetAdapterLuid: GetDesc hr=0x{(uint)hr3:X8}"); return 0; }

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

    // ── Public stereo rendering API ───────────────────────────────────────────

    /// <summary>
    /// Stops the background empty-frame thread. Call once before taking over
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
            // This call initialises VDXR's D3D11 swapchain backend.  It may fail
            // before the frame loop has ticked; without it xrEnumerateSwapchainFormats
            // returns count=0 and xrCreateSwapchain returns -8.
            if (_pfnGetD3D11GfxReqs != IntPtr.Zero)
            {
                const int reqSz = 32;
                IntPtr reqp = Marshal.AllocHGlobal(reqSz);
                for (int i = 0; i < reqSz; i++) Marshal.WriteByte(reqp, i, 0);
                Marshal.WriteInt32(reqp, 0, unchecked((int)0x3B9B337A)); // VDXR expects 0x3B9B337A
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
            // XrViewConfigurationView: type(4) pad(4) next(8) recW(4) maxW(4) recH(4) maxH(4) recSamples(4) maxSamples(4) = 40 bytes
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
                recH = Marshal.ReadInt32(vcvBuf, 24); // recommendedImageRectHeight
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
            for (int i = 0; i < LeftSwapchainImages.Length; i++)  Log.LogInfo($"  L[{i}]=0x{LeftSwapchainImages[i]:X}");
            for (int i = 0; i < RightSwapchainImages.Length; i++) Log.LogInfo($"  R[{i}]=0x{RightSwapchainImages[i]:X}");
            if (LeftSwapchainImages.Length == 0 || RightSwapchainImages.Length == 0)
            { Log.LogError("  Empty swapchain image arrays"); return false; }

            return true;
        }
        catch (Exception ex) { Log.LogError($"SetupStereo: {ex}"); return false; }
    }

    // Preferred DXGI formats for swapchain, in order.
    // 87 = DXGI_FORMAT_B8G8R8A8_UNORM       (matches Unity ARGB32 RT on D3D11 — first choice)
    // 91 = DXGI_FORMAT_B8G8R8A8_UNORM_SRGB  (same layout, gamma-correct)
    // 28 = DXGI_FORMAT_R8G8B8A8_UNORM       (channel-swapped vs ARGB32)
    // 29 = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
    private static readonly long[] _preferredFormats = { 28, 29, 87, 91 }; // prefer RGBA8 to match Unity ARGB32 RT (D3D11 fmt 28)

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
                Log.LogWarning("  ESF count=0 — falling back to format 28 (R8G8B8A8_UNORM)");
                return 28;
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
        Marshal.WriteInt64(p, 24, 0x31);                 // usageFlags = COLOR_ATTACHMENT|TRANSFER_DST|SAMPLED
        Marshal.WriteInt64(p, 32, fmt);
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

    private static IntPtr[] EnumSwapchainImages(ulong sc)
    {
        if (_pfnEnumSwapchainImages == IntPtr.Zero) return System.Array.Empty<IntPtr>();
        try
        {
            int rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainImagesDelegate>
                (_pfnEnumSwapchainImages)(sc, 0, out uint count, IntPtr.Zero);
            if (rc != 0 || count == 0) { Log.LogWarning($"  EnumSwapchainImages count rc={rc} n={count}"); return System.Array.Empty<IntPtr>(); }
            // XrSwapchainImageD3D11KHR: type(4) pad(4) next(8) texture(8) = 24 bytes
            const int imgSz = 24;
            IntPtr buf = Marshal.AllocHGlobal((int)(count * imgSz));
            for (int i = 0; i < (int)(count * imgSz); i++) Marshal.WriteByte(buf, i, 0);
            for (int i = 0; i < (int)count; i++)
                Marshal.WriteInt32(buf + i * imgSz, 0, unchecked((int)0x3B9B3379)); // VDXR: 0x3B9B3379 (spec 1000003001 + VDXR offset)
            rc = Marshal.GetDelegateForFunctionPointer<XrEnumSwapchainImagesDelegate>
                (_pfnEnumSwapchainImages)(sc, count, out uint countOut, buf);
            Log.LogInfo($"  ESI fill rc={rc} countOut={countOut}");
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

            // XrView[2]: 64 bytes each
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
                Log.LogInfo($"  [FS#{_stereoCallCount}] displayTime={displayTime}  session=0x{_session:X}");
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
        // subImage starts at +64
        Marshal.WriteInt64(p, 64, (long)sc);       // swapchain
        Marshal.WriteInt32(p, 72, 0);              // imageRect.offset.x
        Marshal.WriteInt32(p, 76, 0);              // imageRect.offset.y
        Marshal.WriteInt32(p, 80, SwapchainWidth); // imageRect.extent.width
        Marshal.WriteInt32(p, 84, SwapchainHeight);// imageRect.extent.height
        Marshal.WriteInt32(p, 88, 0);              // imageArrayIndex = 0
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

    private static unsafe void DumpVtableFunctions()
    {
        // Get robj via the singleton getter embedded in xrGetD3D11GfxReqs at +0x143
        IntPtr robj = GetVDXRRuntimeObject();
        if (robj == IntPtr.Zero) { Log.LogInfo("  DumpVtable: robj not found"); return; }
        Log.LogInfo($"  DumpVtable: robj=0x{robj:X}");

        // Dump robj[0..0x18] to read the vtable pointer
        var sb = new System.Text.StringBuilder();
        byte* p = (byte*)robj;
        for (int i = 0; i < 0x20; i++) sb.Append($"{p[i]:X2} ");
        Log.LogInfo($"  robj[0..0x20]: {sb}");

        // vtable pointer is at [robj+0] (first 8 bytes)
        IntPtr vtable = *(IntPtr*)p;
        Log.LogInfo($"  vtable=0x{vtable:X}");

        // Dump vtable slot 12 (offset 0x60) = xrCreateSession implementation
        IntPtr cs_impl = *(IntPtr*)((byte*)vtable + 0x60);
        Log.LogInfo($"  vtable[0x60] (createSession impl)=0x{cs_impl:X}");
        DumpFunctionBytes("createSession_impl", cs_impl, 600);
        // Scan for -38 constant (0xDA FF FF FF) in createSession_impl
        byte* csfn = (byte*)cs_impl;
        for (int i = 4; i < 600; i++)
        {
            if (csfn[i] == 0xDA && csfn[i+1] == 0xFF && csfn[i+2] == 0xFF && csfn[i+3] == 0xFF)
            {
                var sb2 = new System.Text.StringBuilder();
                int start = Math.Max(0, i - 32);
                for (int j = start; j < i + 8; j++) sb2.Append($"{csfn[j]:X2} ");
                Log.LogInfo($"  createSession: -38 at +0x{i:X}: [{sb2}]");
            }
        }

        // Dump vtable slot 60 (offset 0x1e0) = xrGetD3D11GfxReqs implementation
        IntPtr gfx_impl = *(IntPtr*)((byte*)vtable + 0x1e0);
        Log.LogInfo($"  vtable[0x1e0] (gfxReqs impl)=0x{gfx_impl:X}");
        DumpFunctionBytes("gfxReqs_impl", gfx_impl, 200);
    }

    private static unsafe void DumpFunctionBytes(string name, IntPtr addr, int count)
    {
        if (addr == IntPtr.Zero) return;
        var sb = new System.Text.StringBuilder();
        byte* p = (byte*)addr;
        for (int i = 0; i < count; i++) sb.Append($"{p[i]:X2} ");
        Log.LogInfo($"  DUMP {name} @0x{addr:X}: {sb}");
    }

    private static void LogFnOwner(string fnName, IntPtr addr)
    {
        if (addr == IntPtr.Zero) { Log.LogInfo($"  {fnName}: (null)"); return; }
        const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;
        const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002;
        if (GetModuleHandleEx(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, addr, out IntPtr hMod))
        {
            var sb = new System.Text.StringBuilder(512);
            GetModuleFileNameW(hMod, sb, (uint)sb.Capacity);
            Log.LogInfo($"  {fnName}: 0x{addr:X} → {System.IO.Path.GetFileName(sb.ToString())}");
        }
        else
            Log.LogInfo($"  {fnName}: 0x{addr:X} → (module unknown)");
    }

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
                long displayTime = FrameWait(out int waitRc);

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

                if (displayTime == 0) displayTime = 1;

                int rcBegin = (int)FrameBegin(out int _);
                int rcEnd   = (int)FrameEnd(displayTime, out int _);

                frameCount++;
                if (rcBegin != 0 || rcEnd != 0)
                    Log.LogWarning($"  FrameThread: frame={frameCount} rcBegin={rcBegin} rcEnd={rcEnd}");

                if (waitRc == 3) { Log.LogWarning("  FrameThread: XR_SESSION_LOSS_PENDING — exiting frame loop."); break; }
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"  FrameThread: unhandled exception after {frameCount} frames: {ex.Message}");
        }
        Log.LogInfo($"  FrameThread: exited (frames={frameCount}).");
    }

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

            Marshal.FreeHGlobal(waitInfo);
            Marshal.FreeHGlobal(frameState);
            return displayTime;
        }
        catch (Exception ex) { Log.LogWarning($"  FrameWait: {ex.Message}"); return 0; }
    }

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
            Marshal.WriteInt64(endInfo, 16, displayTime);
            Marshal.WriteInt32(endInfo, 24, 1);            // XR_ENVIRONMENT_BLEND_MODE_OPAQUE = 1
            Marshal.WriteInt32(endInfo, 28, 0);            // layerCount = 0
            Marshal.WriteIntPtr(endInfo, 32, IntPtr.Zero);
            rc = Marshal.GetDelegateForFunctionPointer<XrEndFrameDelegate>(_pfnEndFrame)(_session, endInfo);
            Marshal.FreeHGlobal(endInfo);
            return rc;
        }
        catch (Exception ex) { Log.LogWarning($"  FrameEnd: {ex.Message}"); return -1; }
    }
}
