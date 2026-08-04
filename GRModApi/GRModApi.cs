using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GRModApi.Modules.Inscriptions;
using GRModApi.Modules.Inscriptions.Patches;
using HarmonyLib;

namespace GRModApi;

[BepInPlugin("JammingEnd.gr.grmodapi", "GRModApi", "1.0.0")]
public class GRModApi : BasePlugin
{
    private static ManualLogSource Log;
    
    public override void Load()
    {
        Log = base.Log;
        Log.LogInfo("GRModApi loaded");

        var chance = Config.Bind("Injection", "Chance", 0.25f,
            new ConfigDescription("Per-slot chance that a rolled vanilla inscription is replaced with a custom one", new AcceptableValueRange<float>(0f, 1f)));
        var debugEnabled = Config.Bind("Debug", "Enabled", false,
            new ConfigDescription("Enable verbose inscription diagnostic logging")).Value;

        InscriptionRegistry.Instance.Initialize(Log);

        var harmony = new Harmony("JammingEnd.gr.grmodapi");
        InscriptionDataPatch.Apply(harmony, Log);
        InscriptionDiagnostics.Apply(harmony, debugEnabled);
        CombatDamagePatch.Apply(harmony, debugEnabled, Log);
        InscriptionInjector.Apply(harmony, chance, Log);
    }
}
