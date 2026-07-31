using BepInEx;
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

        InscriptionRegistry.Instance.Initialize(Log);
        WeaponStatsPatch.Apply();
        CombatEventsPatch.Apply();

        var harmony = new Harmony("JammingEnd.gr.grmodapi");
        Test_StartingChestHook.Apply(harmony);
    }
}
