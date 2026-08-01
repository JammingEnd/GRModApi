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
            prefix: new HarmonyMethod(typeof(InscriptionInjector),
                nameof(PrefixGetPropObjAndUpdate)));
    }

    private static bool PrefixGetPropObjAndUpdate(int propID,
        Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> dInfo)
    {
        if (_chance == null || _log == null || dInfo == null) return true;
        if (dInfo.TryGetValue("Inscription", out var obj) == false) return true;
        var list = obj?.TryCast<Il2CppSystem.Collections.Generic.List<int>>();
        if (list == null || list.Count == 0) return true;

        if (!TryGetWeaponType(propID, out var weaponType))
            return true;

        if (!TryGetCandidateIds(weaponType, list, out var candidates) || candidates.Count == 0)
            return true;

        var chance = _chance.Value;
        for (int i = 0; i < list.Count; i++)
        {
            if (InscriptionRegistry.Instance.IsCustom(list[i])) continue;

            if (!Roll(chance)) continue;

            var candidate = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            if (list.Contains(candidate)) continue;

            _log.LogInfo($"[INJECTOR] weaponSid={propID} replaced slot {i}: {list[i]} -> {candidate}");
            list[i] = candidate;
        }

        return true;
    }

    private static bool Roll(float chance)
    {
        return UnityEngine.Random.Range(0f, 1f) < chance;
    }

    private static bool TryGetWeaponType(int propID, out int weaponType)
    {
        weaponType = 0;
        try
        {
            var data = ItemData.Instance?.GetWeaponData(propID);
            if (data == null) return false;
            weaponType = data.WeaponType;
            return true;
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[INJECTOR] GetWeaponData failed for sid={propID}: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetCandidateIds(int weaponType,
        Il2CppSystem.Collections.Generic.List<int> existing,
        out Il2CppSystem.Collections.Generic.List<int> candidates)
    {
        candidates = new Il2CppSystem.Collections.Generic.List<int>();
        foreach (var insc in InscriptionRegistry.Instance.GetForWeapon(weaponType))
        {
            if (existing.Contains(insc.Id)) continue;
            candidates.Add(insc.Id);
        }
        return candidates.Count > 0;
    }
}
