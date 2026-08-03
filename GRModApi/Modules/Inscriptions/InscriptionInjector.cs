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
        // PREFIX, not postfix: we need to mutate dInfo["Inscription"] BEFORE the
        // original method reads it to compute Att/AttSpeed/etc. A postfix only sees
        // the already-computed NewItemProp, too late to affect the real stat values.
        harmony.Patch(original,
            prefix: new HarmonyMethod(typeof(InscriptionInjector),
                nameof(PrefixGetPropObjAndUpdate)));
    }

    // NOTE: the exact interop type Harmony hands you for the dInfo parameter depends
    // on how Il2CppInterop generated the binding for Dictionary<string, object> here.
    // Try this signature first; if it fails to bind/patch at runtime (BepInEx will
    // log a patch failure on load), the likely fix is changing Il2CppSystem.Object
    // to Il2CppSystem.Object or dropping generics entirely and using
    // Il2CppSystem.Collections.IDictionary with untyped indexer access instead.
    private static void PrefixGetPropObjAndUpdate(int propID,
        Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.String, Il2CppSystem.Object> dInfo)
    {
        if (_chance == null || _log == null || dInfo == null) return;

        try
        {
            if (!dInfo.ContainsKey("Inscription") || !dInfo.ContainsKey("SID")) return;

            var inscriptionObj = dInfo["Inscription"];
            var list = inscriptionObj?.Cast<Il2CppSystem.Collections.Generic.List<int>>();
            if (list == null || list.Count == 0) return;

            var weaponSid = dInfo["SID"].Unbox<int>();

            if (!TryGetCandidateIds(weaponSid, list, out var candidates) || candidates.Count == 0)
                return;

            var chance = _chance.Value;
            for (int i = 0; i < list.Count; i++)
            {
                var current = list[i];
                if (current == 0) continue;
                if (InscriptionRegistry.Instance.IsCustom(current)) continue;

                // Seeded on weaponSid (weapon TYPE) + slot + the vanilla-rolled id,
                // NOT propID: propID is a transient view/cache id that differs between
                // a world-drop preview and the same item once picked up (see
                // ItemPropCache.cctor: clientID starts at -10000 for temp preview ids),
                // which was causing the "reroll on every pickup" behavior.
                if (!Roll(chance, weaponSid, i, current)) continue;

                var candidate = candidates[PickIndex(candidates.Count, weaponSid, i, current)];
                if (list.Contains(candidate)) continue;

                _log.LogInfo($"[INJECTOR] propID={propID} weaponSid={weaponSid} replaced slot {i}: {current} -> {candidate}");
                list[i] = candidate;
            }

            RecomputeStats(dInfo, list);
        }
        catch (System.Exception ex)
        {
            _log.LogWarning($"[INJECTOR] Prefix failed for propID={propID}: {ex}");
        }
    }

    // Mirrors UIScript.PCWeaponBaseTitle.GetAddAttrInfo accumulation (b__0):
    // positive MulValues sum, AddValues sum, then val = (base + addSum) * (mulSum + 10000) / 10000.
    // Rewriting dInfo stat keys here means the game's own GetPropObjAndUpdate ->
    // UpdatePropByItem -> UpdatePropValue routes the recomputed values through the
    // PropObjectOptimizing change cache, which both the tooltip and combat consume.
    private static void RecomputeStats(
        Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.String, Il2CppSystem.Object> dInfo,
        Il2CppSystem.Collections.Generic.List<int> list)
    {
        var totals = new Dictionary<string, (int mul, int add)>();
        for (int i = 0; i < list.Count; i++)
        {
            var id = list[i];
            if (id == 0) continue;
            if (!InscriptionRegistry.Instance.IsCustom(id)) continue;
            var insc = InscriptionRegistry.Instance.GetById(id);
            if (insc == null) continue;

            var ctx = new WeaponStatContext();
            insc.ModifyStats(ctx);
            foreach (var kvp in ctx.Attrs)
            {
                if (totals.TryGetValue(kvp.Key, out var acc))
                    totals[kvp.Key] = (acc.mul + kvp.Value.MulValue, acc.add + kvp.Value.AddValue);
                else
                    totals[kvp.Key] = (kvp.Value.MulValue, kvp.Value.AddValue);
            }
        }

        foreach (var kvp in totals)
        {
            var key = kvp.Key;
            if (!dInfo.ContainsKey(key)) continue;

            float baseVal;
            try
            {
                baseVal = dInfo[key].Unbox<float>();
            }
            catch
            {
                continue; // non-float stat key; skip
            }

            var (mul, add) = kvp.Value;
            float newVal = (baseVal + add) * (mul + 10000) / 10000f;
            dInfo[key] = new Il2CppSystem.Single { m_value = newVal }.BoxIl2CppObject();
            _log.LogInfo($"[INJECTOR] recomputed {key}: {baseVal} -> {newVal} (mul={mul}, add={add})");
        }
    }

    private static bool Roll(float chance, int weaponSid, int slot, int current)
    {
        if (chance >= 1f) return true;
        if (chance <= 0f) return false;
        var rng = new System.Random(StableSeed(weaponSid, slot, current));
        return rng.NextDouble() < chance;
    }

    private static int PickIndex(int count, int weaponSid, int slot, int current)
    {
        if (count <= 1) return 0;
        var rng = new System.Random(StableSeed(weaponSid, slot, current) + 0x1234);
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
            var weaponData = ItemData.Instance?.GetWeaponData(weaponSid);
            if (weaponData == null) return false;

            foreach (var insc in InscriptionRegistry.Instance.GetForWeapon(weaponData.WeaponType))
            {
                if (existing.Contains(insc.Id)) continue;
                candidates.Add(insc.Id);
            }
            return candidates.Count > 0;
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[INJECTOR] Candidate lookup failed for sid={weaponSid}: {ex.Message}");
            return false;
        }
    }
}