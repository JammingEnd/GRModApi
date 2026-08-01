using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using GRModApi.Modules.Inscriptions;
using GRModApi.Modules.Inscriptions.Patches;
using GRModApi.Test;
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

        InscriptionRegistry.Instance.Initialize(Log);
        WeaponStatsPatch.Apply();
        CombatEventsPatch.Apply();

        var harmony = new Harmony("JammingEnd.gr.grmodapi");
        Test_StartingChestHook.Apply(harmony);
        InscriptionInjector.Apply(harmony, chance, Log);
    }
}
