using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using DataHelper;
using Item;
using PerformMSG;
using TableData;
using UIScript;
using GRModApi.Modules.Inscriptions;

namespace GRModApi.Test;

public static class Test_StartingChestHook
{
    private static ManualLogSource Log => Logger.CreateLogSource("GRModApi.Test");

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static bool _injected;
    private static Il2CppSystem.Collections.Generic.List<int>? _lastForgetList;
    private static PCWeaponBaseTitle? _lastUIInstance;

    public static void Apply(Harmony harmony)
    {
        TryPatch(harmony, typeof(inscriptionData), nameof(inscriptionData.GetInscriptionData),
            nameof(PreGetInscriptionData), nameof(PostGetInscriptionData));

        TryPatch(harmony, typeof(inscriptionData), nameof(inscriptionData.GetWeaponAttrInfo),
            nameof(PreGetWeaponAttrInfo), null);

        TryPatch(harmony, typeof(inscriptionData), nameof(inscriptionData.GetAllInscription),
            null, nameof(PostGetAllInscription));

        TryPatch(harmony, typeof(inscriptionData), nameof(inscriptionData.GetWeaponInscription),
            null, nameof(PostGetWeaponInscription));

        TryPatch(harmony, typeof(TableData.inscriptiondata), "GetData",
            null, nameof(PostGetTableData));

        TryPatch(harmony, typeof(ItemManager), nameof(ItemManager.AddWeapon),
            null, nameof(OnAddWeapon));

        TryPatch(harmony, typeof(PCWeaponPanel_Logic), "SetWeaponInscriptionPanel",
            null, nameof(PostSetWeaponInscriptionPanel));

        var showInscriptionMethod = AccessTools.Method(typeof(PCWeaponBaseTitle),
            "ShowInscription",
            new System.Type[] { typeof(int), typeof(string), typeof(bool), typeof(bool), typeof(bool), typeof(string) });
        if (showInscriptionMethod != null)
        {
            harmony.Patch(showInscriptionMethod,
                postfix: new HarmonyMethod(typeof(Test_StartingChestHook).GetMethod(nameof(PostShowInscription), Flags)));
            Log.LogInfo("Patched PCWeaponBaseTitle.ShowInscription(int,string,...)");
        }

        TryPatch(harmony, typeof(PCWeaponBaseTitle), "ShowInscriptionList",
            null, nameof(PostShowInscriptionList));
    }

    private static void TryPatch(Harmony harmony, System.Type targetType,
        string methodName, string? prefixName, string? postfixName)
    {
        try
        {
            var original = AccessTools.Method(targetType, methodName);
            if (original == null)
            {
                Log.LogWarning($"Method {targetType.Name}.{methodName} not found");
                return;
            }
            harmony.Patch(original,
                prefix: prefixName != null ? new HarmonyMethod(typeof(Test_StartingChestHook).GetMethod(prefixName, Flags)) : null,
                postfix: postfixName != null ? new HarmonyMethod(typeof(Test_StartingChestHook).GetMethod(postfixName, Flags)) : null);
            Log.LogInfo($"Patched {targetType.Name}.{methodName}");
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"Failed to patch {targetType.Name}.{methodName}: {ex.Message}");
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
        data.ItemType = 1;
        data.Weight = 3;
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
                    data.LimitWeaponType.Add(wt);
            }
        }
        return data;
    }

    private static void InjectIntoDict()
    {
        if (_injected) return;
        _injected = true;

        try
        {
            var staticDict = inscriptiondata.GetData();
            if (staticDict != null)
            {
                foreach (var insc in InscriptionRegistry.Instance.GetAll())
                {
                    if (!staticDict.ContainsKey(insc.Id))
                        staticDict.Add(insc.Id, MakeData(insc));
                }
            }

            var instProp = AccessTools.Property(typeof(inscriptionData), "Instance");
            if (instProp?.GetValue(null) is inscriptionData inst)
            {
                if (inst.m_InscriptionDict == null)
                    inst.m_InscriptionDict = new Il2CppSystem.Collections.Generic.Dictionary<int, inscriptiondataclass>();
                foreach (var insc in InscriptionRegistry.Instance.GetAll())
                {
                    if (!inst.m_InscriptionDict.ContainsKey(insc.Id))
                        inst.m_InscriptionDict.Add(insc.Id, MakeData(insc));
                }
            }

            Log.LogWarning($"[INJECT] Injected {InscriptionRegistry.Instance.GetAll().Count()} inscriptions");
            LogDataComparison();
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"[INJECT] Failed: {ex.Message}");
        }
    }

    private static void LogDataComparison()
    {
        try
        {
            var dict = inscriptiondata.GetData();
            if (dict == null) return;
            if (!dict.TryGetValue(100000, out var ourData)) return;

            int[] testSids = new int[] { 1, 13078, 4846, 1001, 2 };
            foreach (var testSid in testSids)
            {
                if (dict.TryGetValue(testSid, out var realData) && realData != null)
                {
                    Log.LogWarning($"[COMPARE] vs sid={testSid} Name={realData.Name} ItemType={realData.ItemType}");
                    foreach (var field in typeof(inscriptiondataclass).GetFields(
                        BindingFlags.Public | BindingFlags.Instance))
                    {
                        var ourVal = field.GetValue(ourData);
                        var realVal = field.GetValue(realData);
                        if (!Equals(ourVal, realVal))
                            Log.LogWarning($"[COMPARE]   {field.Name}: ours={ourVal ?? "null"} real={realVal ?? "null"}");
                    }
                    return;
                }
            }
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"[COMPARE] Failed: {ex.Message}");
        }
    }

    private static bool PreGetInscriptionData(int sid, ref inscriptiondataclass __result)
    {
        if (sid < 100000) return true;
        var insc = InscriptionRegistry.Instance.GetById(sid);
        if (insc == null) return true;
        __result = MakeData(insc);
        Log.LogWarning($"[INS_DATA] sid={sid} name={__result.Name}");
        return false;
    }

    private static void PostGetInscriptionData(int sid, inscriptiondataclass __result)
    {
        if (sid == 100000)
            Log.LogWarning($"[INS_DATA_POST] sid={sid} result={__result?.Name ?? "null"} ItemType={__result?.ItemType}");
    }

    private static bool PreGetWeaponAttrInfo(int sid, string attrName, ref WeaponAttrInfo __result)
    {
        if (sid < 100000) return true;
        var insc = InscriptionRegistry.Instance.GetById(sid);
        if (insc == null) return true;

        var ctx = new WeaponStatContext();
        insc.ModifyStats(ctx);
        if (ctx.Attrs.TryGetValue(attrName, out var info))
        {
            __result = info;
            Log.LogWarning($"[GET_ATTR] sid={sid} attr={attrName} Mul={info.MulValue} Add={info.AddValue}");
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
            {
                __result.Add(insc.Id);
                Log.LogWarning($"[GET_ALL] Added {insc.Id} (total={__result.Count})");
            }
        }
    }

    private static void PostGetWeaponInscription(int weapinID, Il2CppSystem.Collections.Generic.List<int> __result)
    {
        if (__result == null) return;
        foreach (var insc in InscriptionRegistry.Instance.GetAll())
        {
            if (!__result.Contains(insc.Id))
            {
                __result.Add(insc.Id);
                Log.LogWarning($"[GET_WPN] SID={weapinID} added {insc.Id} (total={__result.Count})");
            }
        }
    }

    private static void PostGetTableData(ref Il2CppSystem.Collections.Generic.Dictionary<int, inscriptiondataclass> __result)
    {
        if (__result == null) return;
        InjectIntoDict();
        Log.LogWarning($"[TABLE_DATA] Registry injected (count={__result.Count})");
    }

    private static void OnAddWeapon(ItemObject it, int heroID)
    {
        if (it == null) return;
        try
        {
            var list = inscriptionData.Instance.GetWeaponInscription(it.SID);
            var items = new List<string>();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    items.Add(list[i].ToString());
            Log.LogInfo($"[ADDWPN] SID={it.SID} available inscriptions ({list?.Count ?? 0}): {string.Join(", ", items)}");
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"[ADDWPN] Failed for SID={it?.SID}: {ex.Message}");
        }
    }

    private static void PostSetWeaponInscriptionPanel(Il2CppSystem.Collections.Generic.List<int> inscriptionList)
    {
        bool hasOurs = false;
        try { hasOurs = inscriptionList?.Contains(100000) == true; } catch { }
        Log.LogWarning($"[UI_PANEL] SetWeaponInscriptionPanel hasOurs={hasOurs}");
    }

    private static void PostShowInscription(int WeaponId, string type, bool isResort, bool debugInfo, bool ignoreSpecial, string source)
    {
        Log.LogWarning($"[BASE_UI] ShowInscription(WeaponId={WeaponId}, type={type}, source={source})");
        if (_lastForgetList != null)
        {
            Log.LogWarning($"[BASE_UI]   ForgetList count={_lastForgetList.Count} hasOurs={_lastForgetList.Contains(100000)}");
            foreach (var sid in _lastForgetList)
                Log.LogWarning($"[BASE_UI]     F[{sid}]");
        }
        if (_lastUIInstance != null)
        {
            var field = AccessTools.Field(typeof(PCWeaponBaseTitle), "inscriptionList");
            if (field?.GetValue(_lastUIInstance) is Il2CppSystem.Collections.Generic.List<int> list)
            {
                Log.LogWarning($"[BASE_UI]   inscriptionList count={list.Count} hasOurs={list.Contains(100000)}");
                foreach (var sid in list)
                    Log.LogWarning($"[BASE_UI]     L[{sid}]");
            }
            else
                Log.LogWarning("[BASE_UI]   inscriptionList field is null or empty");
        }
    }

    private static void PostShowInscriptionList(
        PCWeaponBaseTitle __instance,
        Il2CppSystem.Collections.Generic.List<int> ForgetList,
        string type,
        bool isResort,
        Il2CppSystem.Collections.Generic.List<int> sealedList,
        Il2CppSystem.Collections.Generic.List<int> disableList,
        bool ignoreSpecial,
        string source)
    {
        _lastUIInstance = __instance;
        _lastForgetList = ForgetList;
        Log.LogWarning($"[BASE_UI] ShowInscriptionList(type={type}, source={source}, " +
            $"Forget={ForgetList?.Count ?? 0}, sealed={sealedList?.Count ?? 0}, " +
            $"disabled={disableList?.Count ?? 0}, " +
            $"hasOurs={(ForgetList?.Contains(100000) == true)})");
        if (ForgetList != null)
            foreach (var sid in ForgetList)
                Log.LogWarning($"[BASE_UI]   F[{sid}]");
    }
}
