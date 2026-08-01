using BepInEx.Configuration;
using BepInEx.Logging;
using DataHelper;
using HarmonyLib;

namespace GRModApi.Modules.Inscriptions;

public static class InscriptionInjector
{
    private static ManualLogSource? _log;
    private static ConfigEntry<float>? _chance;

    public static void Apply(Harmony harmony, ConfigEntry<float> chance, ManualLogSource log)
    {
        _chance = chance;
        _log = log;

        var original = AccessTools.Method(typeof(ItemPropCache),
            nameof(ItemPropCache.GetPropObjAndUpdate));
        if (original == null)
        {
            log.LogWarning($"Method {typeof(ItemPropCache).Name}.{nameof(ItemPropCache.GetPropObjAndUpdate)} not found");
            return;
        }
        harmony.Patch(original,
            postfix: new HarmonyMethod(typeof(InscriptionInjector),
                nameof(PostfixGetPropObjAndUpdate)));
    }

    private static void PostfixGetPropObjAndUpdate(int propID, NewItemProp __result)
    {
        if (_chance == null || _log == null || __result == null) return;
        var list = __result.Inscription;
        if (list == null || list.Count == 0) return;

        var weaponSid = __result.SID;
        if (!TryGetCandidateIds(weaponSid, list, out var candidates) || candidates.Count == 0)
            return;

        var chance = _chance.Value;
        for (int i = 0; i < list.Count; i++)
        {
            var current = list[i];
            if (current == 0) continue;
            if (InscriptionRegistry.Instance.IsCustom(current)) continue;

            if (!Roll(chance, propID, i, current)) continue;

            var candidate = candidates[PickIndex(candidates.Count, propID, i, current)];
            if (list.Contains(candidate)) continue;

            _log.LogInfo($"[INJECTOR] propID={propID} weaponSid={weaponSid} replaced slot {i}: {current} -> {candidate}");
            list[i] = candidate;
        }
    }

    private static bool Roll(float chance, int dropId, int slot, int current)
    {
        if (chance >= 1f) return true;
        if (chance <= 0f) return false;
        var rng = new System.Random(StableSeed(dropId, slot, current));
        return rng.NextDouble() < chance;
    }

    private static int PickIndex(int count, int dropId, int slot, int current)
    {
        if (count <= 1) return 0;
        var rng = new System.Random(StableSeed(dropId, slot, current) + 0x1234);
        return rng.Next(count);
    }

    private static int StableSeed(int weaponSid, int slot, int current)
    {
        return unchecked(weaponSid * 0x45D9F3B + slot * 0x119De1 + current);
    }

    private static bool TryGetCandidateIds(int weaponSid,
        Il2CppSystem.Collections.Generic.List<int> existing,
        out Il2CppSystem.Collections.Generic.List<int> candidates)
    {
        candidates = new Il2CppSystem.Collections.Generic.List<int>();
        try
        {
            var pool = inscriptionData.Instance?.GetWeaponInscription(weaponSid);
            if (pool == null) return false;
            for (int i = 0; i < pool.Count; i++)
            {
                var id = pool[i];
                if (!InscriptionRegistry.Instance.IsCustom(id)) continue;
                if (existing.Contains(id)) continue;
                candidates.Add(id);
            }
            return candidates.Count > 0;
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[INJECTOR] GetWeaponInscription failed for sid={weaponSid}: {ex.Message}");
            return false;
        }
    }
}
