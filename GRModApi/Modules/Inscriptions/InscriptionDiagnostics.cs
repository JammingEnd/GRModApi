using System.Reflection;
using BepInEx.Logging;
using DataHelper;
using HarmonyLib;
using Item;
using PerformMSG;
using UIScript;

namespace GRModApi.Modules.Inscriptions;

public static class InscriptionDiagnostics
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static ManualLogSource? _log;
    private static Il2CppSystem.Collections.Generic.List<int>? _lastForgetList;
    private static PCWeaponBaseTitle? _lastUIInstance;
    private static bool _comparing;

    public static void Apply(Harmony harmony, bool enabled)
    {
        if (!enabled) return;
        _log = Logger.CreateLogSource("GRModApi.Inscriptions.Diagnostics");
        _log.LogInfo("Diagnostics enabled");

        TryPatch(harmony, typeof(inscriptionData), nameof(inscriptionData.GetInscriptionData),
            null, nameof(PostGetInscriptionData));

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

        TryPatch(harmony, typeof(ItemPropCache), nameof(ItemPropCache.UpdatePropByItem),
            null, nameof(PostUpdatePropByItem));

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
                postfix: new HarmonyMethod(typeof(InscriptionDiagnostics).GetMethod(nameof(PostShowInscription), Flags)));
            _log.LogInfo("Patched PCWeaponBaseTitle.ShowInscription(int,string,...)");
        }

        TryPatch(harmony, typeof(PCWeaponBaseTitle), "ShowInscriptionList",
            null, nameof(PostShowInscriptionList));

        TryPatch(harmony, typeof(PCWeaponBaseTitle), "GetAddAttrInfo",
            null, nameof(PostGetAddAttrInfo));

        var displayClass100 = AccessTools.Inner(typeof(PCWeaponBaseTitle), "<>c__DisplayClass100_0");
        if (displayClass100 == null)
        {
            _log.LogWarning("PCWeaponBaseTitle.<>c__DisplayClass100_0 not found");
        }
        else
        {
            var b0 = AccessTools.Method(displayClass100, "<GetAddAttrInfo>b__0");
            if (b0 == null)
            {
                _log.LogWarning("DisplayClass100_0.<GetAddAttrInfo>b__0 not found");
            }
            else
            {
                harmony.Patch(b0,
                    postfix: new HarmonyMethod(typeof(InscriptionDiagnostics).GetMethod(nameof(PostAggregateB0), Flags)));
                _log.LogInfo("Patched DisplayClass100_0.<GetAddAttrInfo>b__0");
            }
        }
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
                prefix: prefixName != null ? new HarmonyMethod(typeof(InscriptionDiagnostics).GetMethod(prefixName, Flags)) : null,
                postfix: postfixName != null ? new HarmonyMethod(typeof(InscriptionDiagnostics).GetMethod(postfixName, Flags)) : null);
            _log?.LogInfo($"Patched {targetType.Name}.{methodName}");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"Failed to patch {targetType.Name}.{methodName}: {ex.Message}");
        }
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

    private static void PostGetInscriptionData(int sid, inscriptiondataclass __result)
    {
        if (InscriptionRegistry.Instance.IsCustom(sid))
            _log?.LogWarning($"[INS_DATA_POST] sid={sid} result={__result?.Name ?? "null"} ItemType={__result?.ItemType}");
    }

    private static void PostInscriptionDataInit(inscriptionData __instance)
    {
        _log?.LogWarning($"[INS_INIT] called; m_InscriptionDict count={(__instance?.m_InscriptionDict?.Count.ToString() ?? "null")}");
        LogDataComparison();
    }

    private static void PostGetTableData(ref Il2CppSystem.Collections.Generic.Dictionary<int, inscriptiondataclass> __result)
    {
        if (__result == null) return;
        var ids = InscriptionRegistry.Instance.GetAll().Select(i => i.Id).ToList();
        _log?.LogWarning($"[TABLE_DATA] Registry injected (count={__result.Count}) " +
            $"ours={string.Join(",", ids)} present={ids.All(__result.ContainsKey)}");
        LogDataComparison();
    }

    private static void LogDataComparison()
    {
        // LogDataComparison reads TableData.inscriptiondata.GetData(), which is one
        // of the methods we postfix (PostGetTableData). Calling it from inside that
        // postfix recurses infinitely, so guard against reentrancy.
        if (_comparing) return;
        _comparing = true;
        try
        {
            var dict = TableData.inscriptiondata.GetData();
            if (dict == null) return;
            var firstOurs = InscriptionRegistry.Instance.GetAll().FirstOrDefault()?.Id;
            if (firstOurs == null || !dict.TryGetValue(firstOurs.Value, out var ourData)) return;

            int[] testSids = new int[] { 1, 13078, 4846, 1001, 2 };
            foreach (var testSid in testSids)
            {
                if (dict.TryGetValue(testSid, out var realData) && realData != null)
                {
                    _log?.LogWarning($"[COMPARE] vs sid={testSid} Name={realData.Name} ItemType={realData.ItemType}");
                    foreach (var field in typeof(inscriptiondataclass).GetFields(
                        BindingFlags.Public | BindingFlags.Instance))
                    {
                        var ourVal = field.GetValue(ourData);
                        var realVal = field.GetValue(realData);
                        if (!Equals(ourVal, realVal))
                            _log?.LogWarning($"[COMPARE]   {field.Name}: ours={ourVal ?? "null"} real={realVal ?? "null"}");
                    }
                    return;
                }
            }
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[COMPARE] Failed: {ex.Message}");
        }
        finally
        {
            _comparing = false;
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
            _log?.LogInfo($"[ADDWPN] SID={it.SID} available inscriptions ({list?.Count ?? 0}): {string.Join(", ", items)}");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[ADDWPN] Failed for SID={it?.SID}: {ex.Message}");
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
            _log?.LogInfo($"[PROP] propID={propID} keys ({items.Count}): {string.Join(", ", items)}" +
                (inscItems.Count > 0 ? $"  Inscription=[{string.Join(", ", inscItems)}]" : ""));
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[PROP] Failed for propID={propID}: {ex.Message}");
        }
    }

    private static void PostGS2CItemAdd(object __0) => LogGS2C("S2C_ADD", __0);

    private static void PostGS2CEquipAdd(object __0) => LogGS2C("S2C_EQUIP", __0);

    private static void LogGS2C(string tag, object data)
    {
        try
        {
            if (data == null)
            {
                _log?.LogInfo($"[{tag}] data=null");
                return;
            }
            var dInfoField = data.GetType().GetField("dInfo",
                BindingFlags.Public | BindingFlags.Instance);
            if (dInfoField == null)
            {
                _log?.LogInfo($"[{tag}] no dInfo field on {data.GetType().Name}");
                return;
            }
            var dInfo = dInfoField.GetValue(data) as
                Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object>;
            if (dInfo == null)
            {
                _log?.LogInfo($"[{tag}] dInfo=null");
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
            _log?.LogInfo($"[{tag}] itemId={data.GetType().GetField("iItemID")?.GetValue(data)} " +
                $"Inscription=[{string.Join(", ", parts)}]");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[{tag}] Failed: {ex.Message}");
        }
    }

    private static void PostUpdatePropByItem(int itemid, string attrName, Il2CppSystem.Object attrValue,
        bool isSetProp)
    {
        if (attrName != "Inscription") return;
        try
        {
            var list = attrValue.TryCast<Il2CppSystem.Collections.Generic.List<int>>();
            var parts = new List<string>();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    parts.Add(list[i].ToString());
            _log?.LogWarning($"[UPD_PROP] itemid={itemid} Inscription=[{string.Join(", ", parts)}] " +
                $"hasOurs={HasCustom(list)}");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[UPD_PROP] Failed for itemid={itemid}: {ex.Message}");
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
        _log?.LogInfo($"[CHECK_WPN] weaponSid={weaponSid} name={(inscriptionData?.Name ?? "null")} " +
            $"isOurs={isOurs} result={__result}");
    }

    private static void PostCheckInscTag(inscriptiondataclass inscriptionData, itemdataclass waponData,
        bool __result)
    {
        _log?.LogInfo($"[CHECK_TAG] name={(inscriptionData?.Name ?? "null")} " +
            $"weaponId={waponData?.ID} result={__result}");
    }

    private static void PostSetWeaponInscriptionPanel(Il2CppSystem.Collections.Generic.List<int> inscriptionList)
    {
        _log?.LogWarning($"[UI_PANEL] SetWeaponInscriptionPanel hasOurs={HasCustom(inscriptionList)}");
    }

    private static void PostShowInscription(int WeaponId, string type, bool isResort, bool debugInfo, bool ignoreSpecial, string source)
    {
        _log?.LogWarning($"[BASE_UI] ShowInscription(WeaponId={WeaponId}, type={type}, source={source})");
        if (_lastForgetList != null)
        {
            _log?.LogWarning($"[BASE_UI]   ForgetList count={_lastForgetList.Count} hasOurs={HasCustom(_lastForgetList)}");
            foreach (var sid in _lastForgetList)
                _log?.LogWarning($"[BASE_UI]     F[{sid}]");
        }
        if (_lastUIInstance != null)
        {
            var field = AccessTools.Field(typeof(PCWeaponBaseTitle), "inscriptionList");
            if (field?.GetValue(_lastUIInstance) is Il2CppSystem.Collections.Generic.List<int> list)
            {
                _log?.LogWarning($"[BASE_UI]   inscriptionList count={list.Count} hasOurs={HasCustom(list)}");
                foreach (var sid in list)
                    _log?.LogWarning($"[BASE_UI]     L[{sid}]");
            }
            else
                _log?.LogWarning("[BASE_UI]   inscriptionList field is null or empty");
        }
    }

    private static void PostGetAddAttrInfo(
        Il2CppSystem.Collections.Generic.List<int> inscriptionLst,
        string attrName,
        ref float val)
    {
        try
        {
            var items = new List<string>();
            if (inscriptionLst != null)
                for (int i = 0; i < inscriptionLst.Count; i++)
                    items.Add(inscriptionLst[i].ToString());
            _log?.LogWarning($"[GET_ADD_ATTR] attrName={attrName} val={val} " +
                $"hasOurs={HasCustom(inscriptionLst)} list=[{string.Join(", ", items)}]");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"[GET_ADD_ATTR] Failed: {ex.Message}");
        }
    }

    private static void PostAggregateB0(WeaponAttrInfo weaponAttrData)
    {
        if (weaponAttrData == null) return;
        _log?.LogWarning($"[AGG] b__0 attr={weaponAttrData.AttrName} Mul={weaponAttrData.MulValue} " +
            $"Add={weaponAttrData.AddValue}");
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
        _log?.LogWarning($"[BASE_UI] ShowInscriptionList(type={type}, source={source}, " +
            $"Forget={ForgetList?.Count ?? 0}, sealed={sealedList?.Count ?? 0}, " +
            $"disabled={disableList?.Count ?? 0}, " +
            $"hasOurs={HasCustom(ForgetList)})");
        if (ForgetList != null)
            foreach (var sid in ForgetList)
                _log?.LogWarning($"[BASE_UI]   F[{sid}]");
    }
}
