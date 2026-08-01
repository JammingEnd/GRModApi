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
    private static bool _injecting;
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

        TryPatch(harmony, typeof(inscriptionData), "Init",
            null, nameof(PostInscriptionDataInit));

        TryPatch(harmony, typeof(TableData.inscriptiondata), "GetData",
            null, nameof(PostGetTableData));

        TryPatch(harmony, typeof(TableData.inscriptiondata), "GetNormalData",
            null, nameof(PostGetTableData));

        TryPatch(harmony, typeof(TableData.inscriptiondata), "GetPressData",
            null, nameof(PostGetTableData));

        TryPatch(harmony, typeof(ItemManager), nameof(ItemManager.AddWeapon),
            null, nameof(OnAddWeapon));

        TryPatch(harmony, typeof(ItemPropCache), nameof(ItemPropCache.GetPropObjAndUpdate),
            null, nameof(PostGetPropObjAndUpdate));

        TryPatch(harmony, typeof(s2citemcon), nameof(s2citemcon.GS2CItemAdd),
            null, nameof(PostGS2CItemAdd));

        TryPatch(harmony, typeof(s2citemcon), nameof(s2citemcon.GS2CEquipAdd),
            null, nameof(PostGS2CEquipAdd));

        TryPatch(harmony, typeof(WarInscriptionManager), "CheckInscWeapon",
            null, nameof(PostCheckInscWeapon));

        TryPatch(harmony, typeof(WarInscriptionManager), "CheckInscTag",
            null, nameof(PostCheckInscTag));

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
                Log.LogWarning("[INJECT] inscriptionData.Instance is null");
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"[INJECT] Failed: {ex.Message}");
        }
        finally
        {
            _injecting = false;
        }
    }

    private static void LogDataComparison()
    {
        try
        {
            var dict = inscriptiondata.GetData();
            if (dict == null) return;
            var firstOurs = InscriptionRegistry.Instance.GetAll().FirstOrDefault()?.Id;
            if (firstOurs == null || !dict.TryGetValue(firstOurs.Value, out var ourData)) return;

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
        if (!InscriptionRegistry.Instance.IsCustom(sid)) return true;
        var insc = InscriptionRegistry.Instance.GetById(sid);
        if (insc == null) return true;
        __result = MakeData(insc);
        Log.LogWarning($"[INS_DATA] sid={sid} name={__result.Name}");
        return false;
    }

    private static void PostGetInscriptionData(int sid, inscriptiondataclass __result)
    {
        if (InscriptionRegistry.Instance.IsCustom(sid))
            Log.LogWarning($"[INS_DATA_POST] sid={sid} result={__result?.Name ?? "null"} ItemType={__result?.ItemType}");
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
        InjectIntoTableDict(__result);
        Log.LogWarning($"[TABLE_DATA] Registry injected (count={__result.Count})");
    }

    private static void InjectIntoTableDict(Il2CppSystem.Collections.Generic.Dictionary<int, inscriptiondataclass> dict)
    {
        if (_injecting) return;
        _injecting = true;
        try
        {
            var ids = InscriptionRegistry.Instance.GetAll().Select(i => i.Id).ToList();
            foreach (var insc in InscriptionRegistry.Instance.GetAll())
            {
                if (!dict.ContainsKey(insc.Id))
                    dict.Add(insc.Id, MakeData(insc));
            }
            Log.LogWarning($"[TABLE_INJECT] GetData dict count={dict.Count} " +
                $"ours={string.Join(",", ids)} present={ids.All(dict.ContainsKey)}");
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"[TABLE_INJECT] Failed: {ex.Message}");
        }
        finally
        {
            _injecting = false;
        }
    }

    private static void PostInscriptionDataInit(inscriptionData __instance)
    {
        Log.LogWarning($"[INS_INIT] called; m_InscriptionDict count={(__instance?.m_InscriptionDict?.Count.ToString() ?? "null")}");
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
            var ids = InscriptionRegistry.Instance.GetAll().Select(i => i.Id).ToList();
            foreach (var insc in InscriptionRegistry.Instance.GetAll())
            {
                if (!inst.m_InscriptionDict.ContainsKey(insc.Id))
                    inst.m_InscriptionDict.Add(insc.Id, MakeData(insc));
            }
            Log.LogWarning($"[INJECT] m_InscriptionDict count={inst.m_InscriptionDict.Count} " +
                $"ours={string.Join(",", ids)} present={ids.All(inst.m_InscriptionDict.ContainsKey)}");
            if (!_injected)
            {
                _injected = true;
                Log.LogWarning($"[INJECT] Injected {InscriptionRegistry.Instance.GetAll().Count()} inscriptions");
                LogDataComparison();
            }
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"[INJECT] Failed: {ex.Message}");
        }
        finally
        {
            _injecting = false;
        }
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

    private static void PostGetPropObjAndUpdate(int propID,
        Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> dInfo)
    {
        try
        {
            var items = new List<string>();
            var inscItems = new List<string>();
            if (dInfo != null)
            {
                foreach (var kvp in dInfo)
                {
                    items.Add(kvp.Key);
                    if (kvp.Key == "Inscription" && kvp.Value != null)
                    {
                        var list = kvp.Value.TryCast<Il2CppSystem.Collections.Generic.List<int>>();
                        if (list != null)
                            for (int i = 0; i < list.Count; i++)
                                inscItems.Add(list[i].ToString());
                        else
                            inscItems.Add($"({kvp.Value.GetType().Name})");
                    }
                }
            }
            Log.LogInfo($"[PROP] propID={propID} keys ({items.Count}): {string.Join(", ", items)}" +
                (inscItems.Count > 0 ? $"  Inscription=[{string.Join(", ", inscItems)}]" : ""));
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"[PROP] Failed for propID={propID}: {ex.Message}");
        }
    }

    private static void PostGS2CItemAdd(object __0)
    {
        LogGS2C("S2C_ADD", __0);
    }

    private static void PostGS2CEquipAdd(object __0)
    {
        LogGS2C("S2C_EQUIP", __0);
    }

    private static void LogGS2C(string tag, object data)
    {
        try
        {
            if (data == null)
            {
                Log.LogInfo($"[{tag}] data=null");
                return;
            }
            var dInfoField = data.GetType().GetField("dInfo",
                BindingFlags.Public | BindingFlags.Instance);
            if (dInfoField == null)
            {
                Log.LogInfo($"[{tag}] no dInfo field on {data.GetType().Name}");
                return;
            }
            var dInfo = dInfoField.GetValue(data) as
                Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object>;
            if (dInfo == null)
            {
                Log.LogInfo($"[{tag}] dInfo=null");
                return;
            }
            var parts = new List<string>();
            if (dInfo.ContainsKey("Inscription"))
            {
                var value = dInfo["Inscription"];
                var list = value.TryCast<Il2CppSystem.Collections.Generic.List<int>>();
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        parts.Add(list[i].ToString());
                else
                    parts.Add($"({value?.GetType().Name ?? "null"})");
            }
            else
                parts.Add("(no Inscription key)");
            Log.LogInfo($"[{tag}] itemId={data.GetType().GetField("iItemID")?.GetValue(data)} " +
                $"Inscription=[{string.Join(", ", parts)}]");
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"[{tag}] Failed: {ex.Message}");
        }
    }

    private static void PostCheckInscWeapon(int weaponSid, inscriptiondataclass inscriptionData,
        bool __result)
    {
        bool isOurs = false;
        if (inscriptionData != null)
        {
            var ourNames = InscriptionRegistry.Instance.GetAll()
                .Select(i => i.Metadata?.Description).Where(n => n != null);
            isOurs = ourNames.Any(n => n == inscriptionData.Name);
        }
        Log.LogInfo($"[CHECK_WPN] weaponSid={weaponSid} name={(inscriptionData?.Name ?? "null")} " +
            $"isOurs={isOurs} result={__result}");
    }

    private static void PostCheckInscTag(inscriptiondataclass inscriptionData, itemdataclass waponData,
        bool __result)
    {
        Log.LogInfo($"[CHECK_TAG] name={(inscriptionData?.Name ?? "null")} " +
            $"weaponId={waponData?.ID} result={__result}");
    }

    private static bool HasCustom(Il2CppSystem.Collections.Generic.List<int>? list)
    {
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            if (InscriptionRegistry.Instance.IsCustom(list[i]))
                return true;
        }
        return false;
    }

    private static void PostSetWeaponInscriptionPanel(Il2CppSystem.Collections.Generic.List<int> inscriptionList)
    {
        Log.LogWarning($"[UI_PANEL] SetWeaponInscriptionPanel hasOurs={HasCustom(inscriptionList)}");
    }

    private static void PostShowInscription(int WeaponId, string type, bool isResort, bool debugInfo, bool ignoreSpecial, string source)
    {
        Log.LogWarning($"[BASE_UI] ShowInscription(WeaponId={WeaponId}, type={type}, source={source})");
        if (_lastForgetList != null)
        {
            Log.LogWarning($"[BASE_UI]   ForgetList count={_lastForgetList.Count} hasOurs={HasCustom(_lastForgetList)}");
            foreach (var sid in _lastForgetList)
                Log.LogWarning($"[BASE_UI]     F[{sid}]");
        }
        if (_lastUIInstance != null)
        {
            var field = AccessTools.Field(typeof(PCWeaponBaseTitle), "inscriptionList");
            if (field?.GetValue(_lastUIInstance) is Il2CppSystem.Collections.Generic.List<int> list)
            {
                Log.LogWarning($"[BASE_UI]   inscriptionList count={list.Count} hasOurs={HasCustom(list)}");
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
            $"hasOurs={HasCustom(ForgetList)})");
        if (ForgetList != null)
            foreach (var sid in ForgetList)
                Log.LogWarning($"[BASE_UI]   F[{sid}]");
    }
}
