using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Preloader.Core.Patching;

namespace SoDVR.Preload;

/// <summary>
/// BepInEx 6 patcher that runs before any game code loads.
/// Bootstraps the OpenXR subsystem by:
///   1. Creating the UnitySubsystems manifest so Unity discovers the OpenXR provider
///   2. Copying native OpenXR DLLs (UnityOpenXR.dll, openxr_loader.dll) into the game's plugin directory
/// </summary>
[PatcherPluginInfo("com.sodvr.preload", "SoDVR.Preload", "0.1.0")]
public class Preload : BasePatcher
{
    private string GameDataDir => Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "..", "..", "..", "Shadows of Doubt_Data");

    private string PatcherDir => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

    public override void Initialize()
    {
        try
        {
            CreateSubsystemManifest();
            CopyNativePlugins();
            Log.LogInfo("SoDVR Preload complete.");
        }
        catch (Exception ex)
        {
            Log.LogError($"SoDVR Preload ERROR: {ex}");
        }
    }

    private void CreateSubsystemManifest()
    {
        var subsystemDir = Path.Combine(GameDataDir, "UnitySubsystems", "UnityOpenXR");
        Directory.CreateDirectory(subsystemDir);

        var manifestPath = Path.Combine(subsystemDir, "UnitySubsystemsManifest.json");
        if (File.Exists(manifestPath))
        {
            Log.LogInfo("UnitySubsystems manifest already exists, skipping.");
            return;
        }

        const string manifest =
            "{\n" +
            "    \"name\": \"OpenXR XR Plugin\",\n" +
            "    \"version\": \"1.7.0\",\n" +
            "    \"libraryName\": \"UnityOpenXR\",\n" +
            "    \"displays\": [ { \"id\": \"OpenXR Display\" } ],\n" +
            "    \"inputs\": [ { \"id\": \"OpenXR Input\" } ]\n" +
            "}";

        File.WriteAllText(manifestPath, manifest);
        Log.LogInfo($"Created subsystem manifest at: {manifestPath}");
    }

    private void CopyNativePlugins()
    {
        var pluginsDir = Path.Combine(GameDataDir, "Plugins", "x86_64");
        var runtimeDepsDir = Path.Combine(PatcherDir, "RuntimeDeps", "Native");

        if (!Directory.Exists(runtimeDepsDir))
        {
            Log.LogWarning($"RuntimeDeps not found at {runtimeDepsDir} — skipping native plugin copy.");
            return;
        }

        foreach (var nativeDll in new[] { "UnityOpenXR.dll", "openxr_loader.dll" })
        {
            var src = Path.Combine(runtimeDepsDir, nativeDll);
            var dst = Path.Combine(pluginsDir, nativeDll);

            if (!File.Exists(src))
            {
                Log.LogWarning($"{nativeDll} not found in RuntimeDeps — VR will not work without it.");
                continue;
            }

            if (!File.Exists(dst))
            {
                File.Copy(src, dst);
                Log.LogInfo($"Copied {nativeDll} to game plugins.");
            }
            else
            {
                Log.LogInfo($"{nativeDll} already present in game plugins.");
            }
        }
    }
}
