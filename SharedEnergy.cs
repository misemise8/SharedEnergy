using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SharedEnergy;

[BepInPlugin("Misemise.SharedEnergy", "SharedEnergy", "1.0")]
public class SharedEnergy : BaseUnityPlugin
{
    internal static SharedEnergy Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger => Instance._logger;
    internal static readonly bool EnableDebugLogging = false;
    private ManualLogSource _logger => base.Logger;
    internal Harmony? Harmony { get; set; }

    internal static void LogDebug(string message)
    {
        if (EnableDebugLogging)
        {
            Logger.LogInfo(message);
        }
    }

    private void Awake()
    {
        Instance = this;
        this.gameObject.transform.parent = null;
        this.gameObject.hideFlags = HideFlags.HideAndDontSave;

        Patch();
        Logger.LogInfo($"{Info.Metadata.GUID} v{Info.Metadata.Version} has loaded!");
    }

    internal void Patch()
    {
        Harmony ??= new Harmony(Info.Metadata.GUID);
        Harmony.PatchAll();
    }

    internal void Unpatch()
    {
        Harmony?.UnpatchSelf();
    }

    private void Update()
    {
        // Code that runs every frame goes here
    }
}
