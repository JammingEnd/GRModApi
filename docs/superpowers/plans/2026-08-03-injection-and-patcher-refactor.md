# Injection & Patcher Refactor + Real Combat Damage — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure the inscription module into clean units (essential data injection, gated diagnostics, new combat-damage patch) and make custom inscription damage actually apply in solo combat.

**Architecture:** Extract the production-critical table/instance injection and the debug logging out of `Test_StartingChestHook.cs` into `InscriptionDataPatch` (essential, always on) and `InscriptionDiagnostics` (gated by `Debug.Enabled`). Add `CombatDamagePatch` which postfixes the server-side damage getters `SkillBolt.CArgBase.GetCurWeaponAttr<int>` / `GetWeaponPerformAttr<int>` so custom inscription `MulValue`/`AddValue` for the `Att` stat boost the value the server damage formula reads (solo runs the server sim in-process). Delete dead scaffolding.

**Tech Stack:** C# / .NET 6, BepInEx Unity IL2CPP, Harmony, Il2CppInterop. Game: Gunfire Reborn. Build: `dotnet build GRModApi.sln -c Debug` from the worktree root.

**Worktree:** `/home/jammingend/RiderProjects/GRModApi/.worktrees/inscription-injector` (branch `feature/inscription-injector`). All paths below are relative to this worktree root.

---

## File structure

- `GRModApi/Modules/Inscriptions/InscriptionStatAggregator.cs` — NEW: shared helper that aggregates `MulValue`/`AddValue` totals across a list of inscription IDs. Single source of truth for stat aggregation (used by both `InscriptionInjector.RecomputeStats` and `CombatDamagePatch`).
- `GRModApi/Modules/Inscriptions/InscriptionDataPatch.cs` — NEW (extracted from `Test_StartingChestHook.cs`): the essential patches that make custom IDs resolve in `inscriptionData` / `TableData.inscriptiondata` tables + `MakeData` + injection helpers.
- `GRModApi/Modules/Inscriptions/InscriptionDiagnostics.cs` — NEW (extracted): gated debug-logging postfixes.
- `GRModApi/Modules/Inscriptions/Patches/CombatDamagePatch.cs` — NEW: server-side getter postfixes → real combat damage.
- `GRModApi/Modules/Inscriptions/InscriptionInjector.cs` — MODIFY: `RecomputeStats` uses `InscriptionStatAggregator`.
- `GRModApi/Modules/Inscriptions/InscriptionBase.cs` — MODIFY: remove `OnHit`/`OnShoot`/`OnKill` virtuals.
- `GRModApi/GRModApi.cs` — MODIFY: wire new modules, drop stub + test-hook calls.
- `GRModApi/Test/Test_StartingChestHook.cs` — DELETE (split into the two modules).
- `GRModApi/Modules/Inscriptions/Patches/WeaponStatsPatch.cs` — DELETE (empty stub).
- `GRModApi/Modules/Inscriptions/Patches/CombatEventsPatch.cs` — DELETE (empty stub).
- `GRModApi/Modules/Inscriptions/HitContext.cs` — DELETE.
- `GRModApi/Modules/Inscriptions/ShootContext.cs` — DELETE.
- `GRModApi/Modules/Inscriptions/KillContext.cs` — DELETE.
- `GRModApi/Test/Inscriptions/*` — KEEP (working examples).

Interop facts verified by reflection (do not re-derive):
- `SkillBolt.CArgBase` (namespace `SkillBolt`) has generic static `T GetCurWeaponAttr<T>(CSkillBase, STR_ENUM.INFO_PROP_LIST)` and `T GetWeaponPerformAttr<T>(CSkillBase, STR_ENUM.INFO_PROP_LIST)`; closed `<int>` forms exist.
- `SkillBolt.CSkillBase` has `NewItemProp ItemPropCache` property (0xA0), `int Weapon` (0xB0).
- `NewItemProp` has `Single Att` and `Il2CppSystem.Collections.Generic.List<int> Inscription` properties.
- `DataHelper.WeaponAttrInfo` has `MulValue` / `AddValue` / `AttrName` properties.
- `STR_ENUM.INFO_PROP_LIST.Att = 6`.
- `Game.ItempropEvent.Att` is the string stat key `"Att"` (already used in `WeaponStatContext`).
- `ItemPropCache.GetPropByItem(int)` → `NewItemProp` (used as fallback path).

Research facts from `docs/research/FINDINGS.txt` (in-game, UnityExplorer):
- **Stats are stored ×100 (INT100 scale)**: `ItemData.m_WeaponDataDict[SID].AttDamage = 12800` for a weapon dealing 128 damage. Same instances live in `m_AvailableData[]`.
- `ItemData.m_WeaponDataDict` is the authoritative **base** source and is re-synced from elsewhere (manual edits revert). Base weapons' `AttDamage` never increases with level; the per-instance **`ItemPropCache` prop IS leveled** (level lives outside `m_PropDict`).
- **Upgrading a weapon resets our recomputed affix stats** in the client prop (they get rebuilt from base).
- Implication: `CombatDamagePatch` (aggregates the bonus from the **live inscription list on every hit**) survives upgrade/level resets — it adds on top of whatever the current leveled `Att` is. `RecomputeStats` (client display mirror) is the part upgrades reset; that is display-only.

---

### Task 1: Add `InscriptionStatAggregator` and refactor `RecomputeStats` to use it

**Files:**
- Create: `GRModApi/Modules/Inscriptions/InscriptionStatAggregator.cs`
- Modify: `GRModApi/Modules/Inscriptions/InscriptionInjector.cs:91-135`

- [ ] **Step 1: Create `InscriptionStatAggregator.cs`**

```csharp
namespace GRModApi.Modules.Inscriptions;

public static class InscriptionStatAggregator
{
    public static Dictionary<string, (int Mul, int Add)> Aggregate(IEnumerable<int> inscriptionIds)
    {
        var totals = new Dictionary<string, (int Mul, int Add)>();
        foreach (var id in inscriptionIds)
        {
            var insc = InscriptionRegistry.Instance.GetById(id);
            if (insc == null) continue;

            var ctx = new WeaponStatContext();
            insc.ModifyStats(ctx);
            foreach (var kvp in ctx.Attrs)
            {
                if (totals.TryGetValue(kvp.Key, out var acc))
                    totals[kvp.Key] = (acc.Mul + kvp.Value.MulValue, acc.Add + kvp.Value.AddValue);
                else
                    totals[kvp.Key] = (kvp.Value.MulValue, kvp.Value.AddValue);
            }
        }
        return totals;
    }
}
```

Note: `GetById` returns null for non-custom / unknown IDs, so vanilla IDs are skipped automatically — callers can pass the whole inscription list.

- [ ] **Step 2: Replace the `RecomputeStats` body in `InscriptionInjector.cs`**

Replace lines 91-135 (the whole `RecomputeStats` method) with:

```csharp
    private static void RecomputeStats(
        Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.String, Il2CppSystem.Object> dInfo,
        Il2CppSystem.Collections.Generic.List<int> list)
    {
        var ids = new List<int>();
        for (int i = 0; i < list.Count; i++)
        {
            var id = list[i];
            if (id != 0 && InscriptionRegistry.Instance.IsCustom(id))
                ids.Add(id);
        }

        var totals = InscriptionStatAggregator.Aggregate(ids);

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
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build GRModApi.sln -c Debug`
Expected: Build succeeded, 0 Error(s).

- [ ] **Step 4: Commit**

```bash
git add GRModApi/Modules/Inscriptions/InscriptionStatAggregator.cs GRModApi/Modules/Inscriptions/InscriptionInjector.cs
git commit -m "refactor: extract InscriptionStatAggregator, use it in RecomputeStats"
```

---

### Task 2: Create `InscriptionDataPatch` (essential injection)

**Files:**
- Create: `GRModApi/Modules/Inscriptions/InscriptionDataPatch.cs`
- Delete: nothing yet (`Test_StartingChestHook.cs` stays until Task 6 to keep the build green)

- [ ] **Step 1: Create `InscriptionDataPatch.cs`**

This is the production-critical extraction from `Test_StartingChestHook.cs`. Namespace `GRModApi.Modules.Inscriptions`. Structure:

```csharp
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
```

Notes:
- This removes the `LogDataComparison()` call from the original `InjectIntoInstance` (it was a diagnostic; `InscriptionDiagnostics` re-implements it). The `[INS_DATA_POST]`, `[INS_INIT]`, `[TABLE_DATA]` log lines from the original are dropped here.
- The diagnostic postfix for `GetInscriptionData` (`PostGetInscriptionData`) moves to `InscriptionDiagnostics`.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build GRModApi.sln -c Debug`
Expected: Build succeeded, 0 Error(s). (`Test_StartingChestHook.cs` still exists, so no callers are broken.)

- [ ] **Step 3: Commit**

```bash
git add GRModApi/Modules/Inscriptions/InscriptionDataPatch.cs
git commit -m "feat: extract essential inscription data injection into InscriptionDataPatch"
```

---

### Task 3: Create `InscriptionDiagnostics` (gated debug logging)

**Files:**
- Create: `GRModApi/Modules/Inscriptions/InscriptionDiagnostics.cs`

- [ ] **Step 1: Create `InscriptionDiagnostics.cs`**

Debug-only logging postfixes extracted from `Test_StartingChestHook.cs`. Namespace `GRModApi.Modules.Inscriptions`. Only installs patches when `debugEnabled` is true.

```csharp
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
```

Notes:
- This re-implements the `[INS_INIT]`, `[TABLE_DATA]`, and `LogDataComparison` logging that was dropped from the essential module (it reads the injected tables directly, no dependency on `InscriptionDataPatch`).
- `PostGetPropObjAndUpdate` and `PostUpdatePropByItem` use the same interop signatures as the original test file (which compiled clean).

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build GRModApi.sln -c Debug`
Expected: Build succeeded, 0 Error(s).

- [ ] **Step 3: Commit**

```bash
git add GRModApi/Modules/Inscriptions/InscriptionDiagnostics.cs
git commit -m "feat: extract gated inscription diagnostics module"
```

---

### Task 4: Create `CombatDamagePatch` (real combat damage)

**Files:**
- Create: `GRModApi/Modules/Inscriptions/Patches/CombatDamagePatch.cs`

- [ ] **Step 1: Create `CombatDamagePatch.cs`**

Postfixes the server-side damage getters so custom inscription `Att` stats boost real combat damage (solo/offline — server sim runs in-process; online the getters run on the remote server and this postfix simply never fires).

```csharp
using System.Reflection;
using BepInEx.Logging;
using DataHelper;
using HarmonyLib;
using SkillBolt;

namespace GRModApi.Modules.Inscriptions.Patches;

public static class CombatDamagePatch
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static ManualLogSource? _log;
    private static bool _debug;

    public static void Apply(Harmony harmony, bool debugEnabled, ManualLogSource log)
    {
        _log = log;
        _debug = debugEnabled;

        TryPatch(harmony, "GetCurWeaponAttr", nameof(PostfixGetCurWeaponAttr));
        TryPatch(harmony, "GetWeaponPerformAttr", nameof(PostfixGetWeaponPerformAttr));
    }

    private static void TryPatch(Harmony harmony, string methodName, string postfixName)
    {
        try
        {
            var generic = AccessTools.Method(typeof(CArgBase), methodName,
                new System.Type[] { typeof(CSkillBase), typeof(STR_ENUM.INFO_PROP_LIST) });
            if (generic == null)
            {
                _log?.LogWarning($"CArgBase.{methodName} not found");
                return;
            }
            // Harmony cannot patch an open generic definition; the server calls the
            // closed <int> form, so patch that.
            var closed = generic.MakeGenericMethod(typeof(int));
            harmony.Patch(closed,
                postfix: new HarmonyMethod(typeof(CombatDamagePatch).GetMethod(postfixName, Flags)));
            _log?.LogInfo($"Patched CArgBase.{methodName}<int>");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"Failed to patch CArgBase.{methodName}: {ex.Message}");
        }
    }

    private static void PostfixGetCurWeaponAttr(CSkillBase __0, STR_ENUM.INFO_PROP_LIST __1, ref int __result)
    {
        ApplyBonus(__0, __1, ref __result);
    }

    private static void PostfixGetWeaponPerformAttr(CSkillBase __0, STR_ENUM.INFO_PROP_LIST __1, ref int __result)
    {
        ApplyBonus(__0, __1, ref __result);
    }

    private static void ApplyBonus(CSkillBase skill, STR_ENUM.INFO_PROP_LIST attr, ref int result)
    {
        if (skill == null || attr != STR_ENUM.INFO_PROP_LIST.Att) return;

        try
        {
            // Read the weapon prop directly from the skill (the server sim populates
            // CSkillBase.ItemPropCache in-process during solo combat).
            var prop = skill.ItemPropCache;
            if (prop == null) return;

            var inscriptionList = prop.Inscription;
            if (inscriptionList == null || inscriptionList.Count == 0) return;

            var ids = new List<int>();
            for (int i = 0; i < inscriptionList.Count; i++)
                ids.Add(inscriptionList[i]);

            var totals = InscriptionStatAggregator.Aggregate(ids);
            if (!totals.TryGetValue(Game.ItempropEvent.Att, out var t)) return;
            if (t.Mul == 0 && t.Add == 0) return;

            int baseVal = result;
            result = (int)((baseVal + t.Add) * (t.Mul + 10000) / 10000f);
            if (_debug)
                _log?.LogInfo($"[COMBAT] {attr} base={baseVal} -> {result} (mul={t.Mul}, add={t.Add})");
        }
        catch (System.Exception ex)
        {
            if (_debug)
                _log?.LogWarning($"[COMBAT] failed: {ex.Message}");
        }
    }
}
```

Notes:
- `STR_ENUM` is a class in the global namespace with `INFO_PROP_LIST` nested inside it, so it must be fully qualified as `STR_ENUM.INFO_PROP_LIST` everywhere (`using STR_ENUM;` would not compile). `Game.ItempropEvent.Att` is the `"Att"` string key used by `WeaponStatContext`/`InscriptionStatAggregator`.
- **INT100 scale**: weapon stats are stored ×100 (`AttDamage = 12800` ≈ 128 damage, per FINDINGS.txt). The getter's `__result` is this scaled value; the formula `(base + addSum) * (mulSum + 10000) / 10000` preserves the scale (mulSum is already percent×100 via `WeaponStatContext.AddDamage`). Expect `__result` ≈ 12800-scale in the `[COMBAT]` log, not 128.
- **Upgrade/level resets**: because the bonus is recomputed from the *live inscription list on every hit*, this postfix keeps applying after the client prop's recomputed stats are reset by an upgrade — it adds on top of the current leveled base. If the inscription list itself is reset (server never knew the custom ID), the bonus stops; that is out of scope for the combat hook (a server-side concern).
- Postfix parameters `__0`/`__1` bind to `(CSkillBase, INFO_PROP_LIST)` and `ref int __result` to the return value of `GetCurWeaponAttr<int>` / `GetWeaponPerformAttr<int>`.
- If patching the closed `<int>` form fails at runtime, Harmony will log a patch failure on load; the `Log.LogWarning` in `TryPatch` surfaces it. (Known risk documented in the spec — verified in-game in Task 8.)
- The formula mirrors `InscriptionInjector.RecomputeStats`: `(base + addSum) * (mulSum + 10000) / 10000`. `WeaponStatContext.AddDamage(p)` stores `mul = p * 100`, so a `+300%` affix → `mul = 30000` → `base * 4`.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build GRModApi.sln -c Debug`
Expected: Build succeeded, 0 Error(s). If `INFO_PROP_LIST` or `Game.ItempropEvent` is unresolved, adjust usings (the interop may require fully-qualified `STR_ENUM.INFO_PROP_LIST`).

- [ ] **Step 3: Commit**

```bash
git add GRModApi/Modules/Inscriptions/Patches/CombatDamagePatch.cs
git commit -m "feat: combat damage patch boosting Att from custom inscriptions (solo)"
```

---

### Task 5: Wire everything in `GRModApi.cs`

**Files:**
- Modify: `GRModApi/GRModApi.cs`

- [ ] **Step 1: Replace the `Load()` body**

Replace the whole `Load()` method with:

```csharp
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
```

Remove the `using GRModApi.Test;` directive (line 7) and the `using GRModApi.Modules.Inscriptions.Patches;` directive (line 6) stays (needed for `CombatDamagePatch`).

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build GRModApi.sln -c Debug`
Expected: Build succeeded, 0 Error(s). `Test_StartingChestHook.cs` and the stub patches still exist but are no longer referenced (they still compile).

- [ ] **Step 3: Commit**

```bash
git add GRModApi/GRModApi.cs
git commit -m "refactor: wire InscriptionDataPatch, InscriptionDiagnostics, CombatDamagePatch"
```

---

### Task 6: Delete dead code

**Files:**
- Delete: `GRModApi/Test/Test_StartingChestHook.cs`
- Delete: `GRModApi/Modules/Inscriptions/Patches/WeaponStatsPatch.cs`
- Delete: `GRModApi/Modules/Inscriptions/Patches/CombatEventsPatch.cs`
- Delete: `GRModApi/Modules/Inscriptions/HitContext.cs`
- Delete: `GRModApi/Modules/Inscriptions/ShootContext.cs`
- Delete: `GRModApi/Modules/Inscriptions/KillContext.cs`
- Modify: `GRModApi/Modules/Inscriptions/InscriptionBase.cs`

- [ ] **Step 1: Remove the context types and stubs**

```bash
git rm GRModApi/Test/Test_StartingChestHook.cs \
       GRModApi/Modules/Inscriptions/Patches/WeaponStatsPatch.cs \
       GRModApi/Modules/Inscriptions/Patches/CombatEventsPatch.cs \
       GRModApi/Modules/Inscriptions/HitContext.cs \
       GRModApi/Modules/Inscriptions/ShootContext.cs \
       GRModApi/Modules/Inscriptions/KillContext.cs
```

- [ ] **Step 2: Update `InscriptionBase.cs`**

Replace the file content with:

```csharp
namespace GRModApi.Modules.Inscriptions;

public abstract class WeaponInscription
{
    public int Id { get; internal set; }
    public InscriptionAttribute? Metadata { get; internal set; }

    public virtual void ModifyStats(WeaponStatContext ctx) { }
    public virtual void OnReload() { }
    public virtual void OnWeaponSwap() { }
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build GRModApi.sln -c Debug`
Expected: Build succeeded, 0 Error(s). (Only warnings about nullable `_log` fields may remain; verify none reference deleted types.)

- [ ] **Step 4: Commit**

```bash
git add -u
git commit -m "refactor: remove dead scaffolding (empty stubs, unwired context types)"
```

---

### Task 7: Full verification

**Files:**
- None (verification only)

- [ ] **Step 1: Clean build**

Run: `dotnet build GRModApi.sln -c Debug`
Expected: Build succeeded, 0 Error(s), 0 Warning(s).

- [ ] **Step 2: Grep for dangling references**

Run:
```bash
rg -n "Test_StartingChestHook|WeaponStatsPatch|CombatEventsPatch|HitContext|ShootContext|KillContext|OnHit|OnShoot|OnKill" GRModApi/ --glob '!obj/**'
```
Expected: no matches.

- [ ] **Step 3: Review the final diff**

Run: `git status --short && git log --oneline -8`
Expected: working tree clean; 6 new commits (one per Task 1-6) on `feature/inscription-injector`.

---

### Task 8: In-game verification (manual, gated behind Debug)

**Files:**
- None (manual test)

- [ ] **Step 1: Deploy and launch**

Copy the built plugin to `$BepInEx/plugins/GRModApi/` (the csproj `CopyToPlugins` target does this on build). Launch Gunfire Reborn offline.

- [ ] **Step 2: Verify affixes still show**

Start a run, obtain a weapon, open the inscription panel. Custom affixes must still appear (regression check for `InscriptionDataPatch`).

- [ ] **Step 3: Verify real damage reflects custom stats**

Enable `Debug.Enabled = true` in `BepInEx/config/JammingEnd.gr.grmodapi.cfg`, restart. Shoot an enemy with a weapon carrying a custom `+300%` affix. Check `LogOutput.log`:
- `[COMBAT] Att base=... -> ...` lines confirm `CombatDamagePatch` fires and boosts the value the server reads. The base should be INT100-scaled (≈12800 for a 128-damage weapon), and `+300%` (mul 30000) should give `base * 4`.
- Damage numbers dealt to enemies should now reflect the bonus (compare against a weapon without the custom affix).
- If no `[COMBAT]` lines appear, the closed `<int>` generic forms are not being hit by the damage path in this build; investigate whether the server uses `GetCurWeaponAttr<object>` or a different getter, and extend the postfix set.

- [ ] **Step 4: Verify the bonus survives an upgrade**

Upgrade the weapon carrying the custom affix, then shoot an enemy again. Per FINDINGS.txt, upgrades reset the client prop's recomputed stats — but `CombatDamagePatch` reads the live inscription list each hit, so `[COMBAT]` should still show the bonus on top of the new leveled base. If it stops, the inscription list itself was reset by the upgrade (server-side concern, out of scope for this hook).

- [ ] **Step 5: Confirm diagnostics gating**

With `Debug.Enabled = false` (default), startup log shows the module registration but no `[PROP]`/`[BASE_UI]`/`[UPD_PROP]` spam.

---

## Reference

- Spec: `docs/superpowers/specs/2026-08-03-injection-and-patcher-refactor-design.md`
- Research: `docs/research/FINDINGS.txt` (INT100 scale, base-vs-leveled stats, upgrade reset), `docs/research/2026-08-03-updatepropvalue-createitemobject.md`, `docs/research/2026-08-03-smith-npc-inscription-shop.md`, `docs/research/README.md`
- Combat RE: `/tmp/opencode/out_combat.txt`, `/tmp/opencode/out_getters.txt`, `/tmp/opencode/out_itemflow.txt`
- Addresses: `SkillManager.InitSkill` 0x181804770, `GetPropObjAndUpdate` 0x1812BE4C0, `CreateItemObject` 0x1805BE850, `CArgBase.GetCurWeaponAttr<int>` 0x1815DD640, `CArgBase.GetWeaponPerformAttr<int>` 0x1815E2A50
