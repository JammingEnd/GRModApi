# Client-Side Inscription Injector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Intercept weapons client-side after creation and, with a configurable per-slot chance, replace a rolled vanilla inscription ID with one of our custom inscriptions.

**Architecture:** A Harmony **prefix** on `ItemPropCache.GetPropObjAndUpdate(propID, dInfo)` mutates `dInfo["Inscription"]` (a `List<int>`) **in place before** the original builds the `NewItemProp`, so the prop/UI/effects pick up our IDs automatically. Candidates are filtered by weapon-type compatibility via `InscriptionRegistry.GetForWeapon(weaponType)`, and the per-slot chance comes from a BepInEx `ConfigEntry<float>`.

**Tech Stack:** C# / .NET 6, BepInEx Unity IL2CPP, Harmony, Il2CppInterop.

**Reference:** Spec at `docs/superpowers/specs/2026-08-01-inscription-injector-design.md`.

---

## File Structure

- Create: `GRModApi/Modules/Inscriptions/InscriptionInjector.cs`
  - `InscriptionInjector.Apply(Harmony harmony, ConfigEntry<float> chance, ManualLogSource log)`
  - `InscriptionInjector.PrefixGetPropObjAndUpdate(...)` prefix method
  - Helper: `TryGetWeaponType(int propID, out int weaponType)`
  - Helper: `TryGetCandidateIds(int weaponType, List<int> existing, out List<int> candidates)`
- Modify: `GRModApi/GRModApi.cs`
  - Add `using BepInEx.Configuration;`
  - Add `ConfigEntry<float> _chance` field
  - Call `InscriptionInjector.Apply(harmony, _chance, Log)` in `Load()`

## Background facts (verified)

- `ItemPropCache.GetPropObjAndUpdate(int propID, Dictionary<string, object> dInfo)` is a static method; the existing postfix at `Test/Test_StartingChestHook.cs:372-402` proves the runtime signature is `Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> dInfo` and that `dInfo["Inscription"]` is an `Il2CppSystem.Collections.Generic.List<int>`.
- `propID` is the weapon/item SID. `DataHelper.ItemData` (Singleton) has `GetWeaponData(int ssid) -> itemdataclass`; `itemdataclass.WeaponType` is `int` (offset 0xD8) holding `GO_ENUM.WeaponType` values (e.g. `EQUIP_RIFLE = 69633`).
- `InscriptionRegistry.GetForWeapon(int weaponType)` (InscriptionRegistry.cs:147-155) filters registered inscriptions by comparing `(int)GO_ENUM.WeaponType` to the given `weaponType`. This matches `itemdataclass.WeaponType` semantics.
- Custom inscription IDs are `4992`, `4993` (reassigned by `AssignIdsFromTable`).
- Build: `dotnet build` (works, 2 pre-existing warnings).
- Accepted tradeoff (documented per spec): vanilla exclusive/special slots are **not** excluded at runtime — the duplicate-own-ID guard is the only protection. A vanilla exclusive inscription may be replaced by a custom ID. This is deliberate; detection is deferred (see spec "Out of scope").

---

### Task 1: Create the InscriptionInjector module

**Files:**
- Create: `GRModApi/Modules/Inscriptions/InscriptionInjector.cs`

- [ ] **Step 1: Write the module skeleton with the prefix and helpers**

```csharp
using BepInEx.Configuration;
using BepInEx.Logging;
using DataHelper;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;

namespace GRModApi.Modules.Inscriptions;

public static class InscriptionInjector
{
    private static ManualLogSource? _log;
    private static ConfigEntry<float>? _chance;

    public static void Apply(Harmony harmony, ConfigEntry<float> chance, ManualLogSource log)
    {
        _chance = chance;
        _log = log;

        harmony.Patch(
            AccessTools.Method(typeof(ItemPropCache), nameof(ItemPropCache.GetPropObjAndUpdate)),
            prefix: new HarmonyMethod(typeof(InscriptionInjector),
                nameof(PrefixGetPropObjAndUpdate)));
    }

    private static bool PrefixGetPropObjAndUpdate(int propID,
        Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> dInfo)
    {
        if (_chance == null || _log == null || dInfo == null) return true;
        if (dInfo.TryGetValue("Inscription", out var obj) == false) return true;
        var list = obj?.TryCast<List<int>>();
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

    private static bool TryGetCandidateIds(int weaponType, List<int> existing,
        out List<int> candidates)
    {
        candidates = new List<int>();
        foreach (var insc in InscriptionRegistry.Instance.GetForWeapon(weaponType))
        {
            if (existing.Contains(insc.Id)) continue;
            candidates.Add(insc.Id);
        }
        return candidates.Count > 0;
    }
}
```

Note: `candidates` is computed once per weapon and not rebuilt per slot, and the
`list.Contains(candidate)` check may skip a later slot's replacement even when
other candidates remain. This is intentional and harmless — the duplicate-own-ID
guard still holds; it merely makes replacement slightly conservative at high
chance values.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, only the 2 pre-existing warnings (CS0108, CS8618 in `GRModApi.cs`).

- [ ] **Step 3: Commit**

```bash
git add GRModApi/Modules/Inscriptions/InscriptionInjector.cs
git commit -m "feat: add InscriptionInjector module with prefix hook"
```

---

### Task 2: Wire the injector into GRModApi with a config entry

**Files:**
- Modify: `GRModApi/GRModApi.cs`

- [ ] **Step 1: Add the config entry and wire the injector**

Change `GRModApi.cs` to:

```csharp
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
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, only the 2 pre-existing warnings.

- [ ] **Step 3: Commit**

```bash
git add GRModApi/GRModApi.cs
git commit -m "feat: wire InscriptionInjector with configurable per-slot chance"
```

---

### Task 3: In-game verification

No automated test framework exists (BepInEx IL2CPP runtime plugin). Verification is manual in-game.

- [ ] **Step 1: Build and deploy**

Run: `dotnet build`
The csproj `CopyToPlugins` target copies `GRModApi.dll` to the BepInEx plugins dir automatically.

- [ ] **Step 2: Launch the game and verify the injector loads**

Expected: `GRModApi loaded` in BepInEx log, no patch errors.

- [ ] **Step 3: Drop weapons and verify replacement**

- Drop several weapons that roll vanilla inscriptions.
- Expected: `[INJECTOR] weaponSid=... replaced slot i: X -> Y` lines appear (with `Y` being 4992 or 4993) at roughly the configured 25% per-slot rate.
- Confirm the replaced weapon's UI shows our inscription description on the affected slot.
- Confirm no errors / no unknown inscription IDs in the log.

- [ ] **Step 4: Verify config works**

- Edit `BepInEx/config/JammingEnd.gr.grmodapi.cfg`, set `Chance = 1`, restart.
- Expected: every vanilla slot gets replaced (barring duplicate guard).

- [ ] **Step 5: Confirm the starting chest path (informational)**

- If the starting weapon ever shows our inscription without a drop, the starting chest flows through the hook too; if not, it simply doesn't (drops/equips unaffected). No action required either way.
