using System.Reflection;
using BepInEx.Logging;
using DataHelper;
using HarmonyLib;
using TableData;
using UIScript;

namespace GRModApi.Modules.Inscriptions;

public static class InscriptionDataPatch
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static ManualLogSource? _log;
    private static bool _injecting;
    private static bool _injected;

    public static void Apply(Harmony harmony, ManualLogSource log)
    {
        _log = log;

        // GetInscriptionData: essential PREFIX only (diagnostic postfix lives in InscriptionDiagnostics)
        TryPatch(harmony, typeof(inscriptionData), nameof(inscriptionData.GetInscriptionData),
            nameof(PreGetInscriptionData), null);

        TryPatch(harmony, typeof(inscriptionData), nameof(inscriptionData.GetWeaponAttrInfo),
            nameof(PreGetWeaponAttrInfo), null);

        TryPatch(harmony, typeof(inscriptionData), nameof(inscriptionData.GetAllInscription),
            null, nameof(PostGetAllInscription));

        TryPatch(harmony, typeof(inscriptionData), nameof(inscriptionData.GetWeaponInscription),
            null, nameof(PostGetWeaponInscription));

        TryPatch(harmony, typeof(inscriptionData), "Init",
            null, nameof(PostInscriptionDataInit));

        // These live on TableData.inscriptiondata, NOT inscriptionData
        TryPatch(harmony, typeof(TableData.inscriptiondata), "GetData",
            null, nameof(PostGetTableData));
        TryPatch(harmony, typeof(TableData.inscriptiondata), "GetNormalData",
            null, nameof(PostGetTableData));
        TryPatch(harmony, typeof(TableData.inscriptiondata), "GetPressData",
            null, nameof(PostGetTableData));
    }

    private static void TryPatch(Harmony harmony, System.Type targetType,
        string methodName, string? prefixName, string? postfixName)
    {
        try
        {
            var original = AccessTools.Method(targetType, methodName);
            if (original == null)
            {
                _log?.LogWarning($"Method {targetType.Name}.{methodName} not found");
                return;
            }
            harmony.Patch(original,
                prefix: prefixName != null ? new HarmonyMethod(typeof(InscriptionDataPatch).GetMethod(prefixName, Flags)) : null,
                postfix: postfixName != null ? new HarmonyMethod(typeof(InscriptionDataPatch).GetMethod(postfixName, Flags)) : null);
            _log?.LogInfo($"Patched {targetType.Name}.{methodName}");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"Failed to patch {targetType.Name}.{methodName}: {ex.Message}");
        }
    }

    private static readonly Dictionary<InscriptionRarity, int> RarityQuality = new()
    {
        [InscriptionRarity.Normal] = 1,
        [InscriptionRarity.Rare] = 2,
        [InscriptionRarity.DoubleIns] = 3,
        [InscriptionRarity.Exclusive] = 4,
    };

    private static readonly Dictionary<WeaponCategory, int> CategoryWeaponType = new()
    {
        [WeaponCategory.Rifle] = (int)GO_ENUM.WeaponType.EQUIP_RIFLE,
        [WeaponCategory.SMG] = (int)GO_ENUM.WeaponType.EQUIP_SMG,
        [WeaponCategory.Shotgun] = (int)GO_ENUM.WeaponType.EQUIP_SHOTGUN,
        [WeaponCategory.Handgun] = (int)GO_ENUM.WeaponType.EQUIP_HANDGUN,
        [WeaponCategory.Sniper] = (int)GO_ENUM.WeaponType.EQUIP_SNIPER,
        [WeaponCategory.RocketLauncher] = (int)GO_ENUM.WeaponType.EQUIP_ROCKET_LAUNCHER,
        [WeaponCategory.Laser] = (int)GO_ENUM.WeaponType.EQUIP_LASER,
        [WeaponCategory.CloseWeapon] = (int)GO_ENUM.WeaponType.EQUIP_TYPE_CLOSEWEAPON,
        [WeaponCategory.Amulet] = (int)GO_ENUM.WeaponType.EQUIP_TYPE_AMULET,
    };

    private static inscriptiondataclass MakeData(WeaponInscription insc)
    {
        var meta = insc.Metadata;
        var data = new inscriptiondataclass();
        data.Name = meta?.Description ?? "Custom Inscription";
        data.Desc = meta?.Description ?? "";
        data.ItemType = meta != null && RarityQuality.TryGetValue(meta.Rarity, out var rarity)
            ? rarity
            : 1;
        data.Weight = 10000;
        data.HurtType = "";
        data.DetailLabel = new Il2CppSystem.Collections.Generic.List<int>();
        data.LimitWeapon = new Il2CppSystem.Collections.Generic.List<int>();
        data.LimitWeaponType = new Il2CppSystem.Collections.Generic.List<int>();
        data.ExcludeWeapon = new Il2CppSystem.Collections.Generic.List<int>();
        data.ExcludeWeaponType = new Il2CppSystem.Collections.Generic.List<int>();
        data.ExcludeInscription = new Il2CppSystem.Collections.Generic.List<int>();
        data.AnyOfWeaponType = new Il2CppSystem.Collections.Generic.List<int>();
        data.WeaponAddAttrInfo = new Il2CppSystem.Collections.Generic.Dictionary<string, WeaponAttrInfo>();

        var ctx = new WeaponStatContext();
        insc.ModifyStats(ctx);
        foreach (var kvp in ctx.Attrs)
            data.WeaponAddAttrInfo.Add(kvp.Key, kvp.Value);

        if (meta?.WeaponCategories is { Length: > 0 } cats)
        {
            foreach (var cat in cats)
            {
                if (CategoryWeaponType.TryGetValue(cat, out var wt))
                {
                    data.LimitWeaponType.Add(wt);
                    data.AnyOfWeaponType.Add(wt);
                }
            }
        }
        return data;
    }

    private static void InjectIntoDict()
    {
        if (_injecting) return;
        _injecting = true;
        try
        {
            var instProp = AccessTools.Property(typeof(inscriptionData), "Instance");
            if (instProp?.GetValue(null) is inscriptionData inst)
                InjectIntoInstance(inst);
            else
                _log?.LogWarning("[INJECT] inscriptionData.Instance is null");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[INJECT] Failed: {ex.Message}");
        }
        finally
        {
            _injecting = false;
        }
    }

    private static void InjectIntoTableDict(Il2CppSystem.Collections.Generic.Dictionary<int, inscriptiondataclass> dict)
    {
        if (_injecting) return;
        _injecting = true;
        try
        {
            foreach (var insc in InscriptionRegistry.Instance.GetAll())
            {
                if (!dict.ContainsKey(insc.Id))
                    dict.Add(insc.Id, MakeData(insc));
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[TABLE_INJECT] Failed: {ex.Message}");
        }
        finally
        {
            _injecting = false;
        }
    }

    private static void PostInscriptionDataInit(inscriptionData __instance)
    {
        if (__instance != null)
            InjectIntoInstance(__instance);
    }

    private static void InjectIntoInstance(inscriptionData inst)
    {
        if (_injecting) return;
        _injecting = true;
        try
        {
            InscriptionRegistry.Instance.AssignIdsFromTable(inst.m_InscriptionDict);
            if (inst.m_InscriptionDict == null)
                inst.m_InscriptionDict = new Il2CppSystem.Collections.Generic.Dictionary<int, inscriptiondataclass>();
            foreach (var insc in InscriptionRegistry.Instance.GetAll())
            {
                if (!inst.m_InscriptionDict.ContainsKey(insc.Id))
                    inst.m_InscriptionDict.Add(insc.Id, MakeData(insc));
            }
            if (!_injected)
            {
                _injected = true;
                _log?.LogInfo($"[INJECT] Injected {InscriptionRegistry.Instance.GetAll().Count()} inscriptions");
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[INJECT] Failed: {ex.Message}");
        }
        finally
        {
            _injecting = false;
        }
    }

    private static bool PreGetInscriptionData(int sid, ref inscriptiondataclass __result)
    {
        if (!InscriptionRegistry.Instance.IsCustom(sid)) return true;
        var insc = InscriptionRegistry.Instance.GetById(sid);
        if (insc == null) return true;
        __result = MakeData(insc);
        return false;
    }

    private static bool PreGetWeaponAttrInfo(int sid, string attrName, ref WeaponAttrInfo __result)
    {
        if (!InscriptionRegistry.Instance.IsCustom(sid)) return true;
        var insc = InscriptionRegistry.Instance.GetById(sid);
        if (insc == null) return true;

        var ctx = new WeaponStatContext();
        insc.ModifyStats(ctx);
        if (ctx.Attrs.TryGetValue(attrName, out var info))
        {
            __result = info;
            return false;
        }
        return true;
    }

    private static void PostGetAllInscription(Il2CppSystem.Collections.Generic.List<int> __result)
    {
        if (__result == null) return;
        foreach (var insc in InscriptionRegistry.Instance.GetAll())
        {
            if (!__result.Contains(insc.Id))
                __result.Add(insc.Id);
        }
    }

    private static void PostGetWeaponInscription(int weapinID, Il2CppSystem.Collections.Generic.List<int> __result)
    {
        if (__result == null) return;
        foreach (var insc in InscriptionRegistry.Instance.GetAll())
        {
            if (!__result.Contains(insc.Id))
                __result.Add(insc.Id);
        }
    }

    private static void PostGetTableData(ref Il2CppSystem.Collections.Generic.Dictionary<int, inscriptiondataclass> __result)
    {
        if (__result == null) return;
        InjectIntoDict();
        InjectIntoTableDict(__result);
    }
}
