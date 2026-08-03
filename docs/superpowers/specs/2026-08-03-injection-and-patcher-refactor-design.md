# Injection & Patcher Refactor + Real Combat Damage

Date: 2026-08-03
Status: Approved design

## Problem

Custom inscriptions currently show in the UI and "sort of" compute in the tooltip,
but **actual combat damage is unchanged**. The reason was only found after
reverse-engineering the combat pipeline:

1. **Affixes show** — `Test_StartingChestHook` injects custom IDs into the client's
   `inscriptionData` tables (`MakeData`, `InjectIntoDict/Instance/TableDict`).
2. **Tooltip "sort of" works** — the tooltip aggregates from the client inscription
   list via `GetWeaponAttrInfo` (which we intercept), so a `+300%` affix displays.
3. **Real damage is default** — combat damage is **server-authoritative**. The client
   only sends hit geometry (`C2GSTriggerCartoonList`); the server reconstructs its own
   `CSkillBase`, reads the weapon's `Att` from its **own** `ItemPropCache` prop, computes
   damage, and replies pre-computed via `GS2CShowDamageInfo`. Our client-side
   `dInfo` mutation (prefix on `ItemPropCache.GetPropObjAndUpdate`) never reaches the
   server's prop, so `RecomputeStats` cannot affect real damage.

Additionally, the codebase has structural debt:

- `Test_StartingChestHook.cs` (614 lines) mixes **essential production injection**
  (table/instance injection that makes custom IDs resolve) with ~20 **debug-only
  logging postfixes**, under a misleading `Test` name.
- `Patches/WeaponStatsPatch.cs` and `Patches/CombatEventsPatch.cs` are empty stubs.
- `HitContext.cs`, `ShootContext.cs`, `KillContext.cs` and the
  `OnHit`/`OnShoot`/`OnKill` virtuals are unwired scaffolding (combat is
  server-side, so client hit hooks cannot affect damage).

## Decision

1. **Fix real combat damage (solo)** by hooking the server-side damage-sourcing
   getters. Solo runs the server simulation in the same process, so a Harmony
   postfix can alter the `Att` value the server actually reads.
2. **Restructure** the inscription module into clean, single-purpose units:
   essential injection, gated diagnostics, and a new combat-damage patch.
3. **Remove dead code**: empty stubs, unwired context scaffolding.

## Reverse-engineering evidence

- Combat flow (decompiled): client fires → sends only `C2GSTriggerCartoonList` +
  `CartoonData` (geometry, no prop values). Server: `SkillManager.InitSkill`
  (0x181804770) reconstructs `CSkillBase`, reads weapon `NewItemProp` from
  `ItemPropCache.m_PropInfo`, computes damage, replies `GS2CShowDamageInfo`
  (pre-computed numbers). Damage never round-trips from client to server.
- `ItemPropCache` static dict on the client is a **mirror** built from
  `GS2CItemAdd` → `s2citemcon.CreateItemObject` (0x1805BE850) →
  `GetPropObjAndUpdate` (0x1812BE4C0). No combat caller writes to it mid-fight.
- Server-side damage getters read the weapon prop:
  - `CArgBase.GetCurWeaponAttr<int>` @ 0x1815DD640 — `itemid = skill.Weapon`
    (`CSkillBase.Weapon` @ 0xB0) → `ItemPropCache.GetPropByItem` →
    `GetPropValue`.
  - `CArgBase.GetWeaponPerformAttr<int>` @ 0x1815E2A50 — `itemid` from
    `skill.AttackSkill+0x28` → same lookup.
  - `GetAttackerAttr`/`CServerArg` variants are stubs (`return 0`) in this build.
- `INFO_PROP_LIST.Att = 6` (enum, TypeDefIndex 16844).
- `CSkillBase` (TypeDefIndex 23363): `Weapon` int @ 0xB0, `AttackSkill` @ 0x238.
- Interop types confirmed present in `Assembly-CSharp.dll`:
  `SkillBolt.CArgBase` with generic `T GetCurWeaponAttr<T>(CSkillBase, INFO_PROP_LIST)`
  and `T GetWeaponPerformAttr<T>(CSkillBase, INFO_PROP_LIST)`.

## Architecture

### Module layout

```
Modules/Inscriptions/
  InscriptionAttribute.cs          keep
  InscriptionBase.cs               keep (remove OnHit/OnShoot/OnKill virtuals)
  InscriptionRegistry.cs           keep (minor cleanup)
  WeaponStatContext.cs             keep
  InscriptionDataPatch.cs          NEW: essential table/instance injection
                                   (extracted from Test_StartingChestHook)
  InscriptionInjector.cs           keep prefix + RecomputeStats, cleanup
  InscriptionDiagnostics.cs        NEW: gated debug postfixes
  Patches/
    CombatDamagePatch.cs           NEW: server-side getter postfixes
    WeaponStatsPatch.cs            DELETE (empty stub)
    CombatEventsPatch.cs           DELETE (empty stub)
  HitContext.cs                    DELETE
  ShootContext.cs                  DELETE
  KillContext.cs                   DELETE
Test/
  Test_StartingChestHook.cs        DELETE (split into the two modules above)
  Inscriptions/                    keep (working examples)
```

### Component: `InscriptionDataPatch` (essential)

Extracted from `Test_StartingChestHook`. Contains the production-critical path
that makes custom IDs resolve:

- Patches on `inscriptionData`: `GetInscriptionData` (**prefix only** — the
  essential half; the diagnostic postfix for the same original moves to
  `InscriptionDiagnostics`), `GetWeaponAttrInfo`, `GetAllInscription`,
  `GetWeaponInscription`, `Init`.
- Patches on `TableData.inscriptiondata`: `GetData`, `GetNormalData`,
  `GetPressData` (all postfixes; note these live on `TableData.inscriptiondata`,
  NOT `inscriptionData`).
- Helpers: `MakeData`, `InjectIntoDict`, `InjectIntoInstance`,
  `InjectIntoTableDict`, `AssignIdsFromTable` call.
- Diagnostic log lines that currently sit inside essential methods
  (`PostInscriptionDataInit`'s `[INS_INIT]`, `PostGetTableData`'s
  `[TABLE_DATA]`, and `InjectIntoInstance`'s `LogDataComparison()` call) are
  **dropped from the essential module**. `InscriptionDiagnostics` re-triggers the
  same information by its own postfixes on the same originals (it patches `Init`
  and the `GetData` family too); no essential→diagnostics reference is kept.
- Moves out of the `GRModApi.Test` namespace into `GRModApi.Modules.Inscriptions`.

### Component: `InscriptionDiagnostics` (gated, default off)

Extracted from `Test_StartingChestHook`. Debug-only logging postfixes:

- `PostGetInscriptionData`, `OnAddWeapon`, `PostUpdatePropByItem`,
  `PostGetPropObjAndUpdate`, `PostGS2CItemAdd`, `PostGS2CEquipAdd`,
  `PostCheckInscWeapon`, `PostCheckInscTag`, `PostSetWeaponInscriptionPanel`,
  `PostShowInscription`, `PostShowInscriptionList`, `PostGetAddAttrInfo`,
  `PostAggregateB0`, `LogDataComparison`, `HasCustom`.

This module patches its own set of originals (including `GetInscriptionData`
postfix and the `GetData` family, which the essential module also touches) and
re-implements the `[INS_INIT]`/`[TABLE_DATA]`/`LogDataComparison` logging itself;
it does not depend on `InscriptionDataPatch`.

Only installs its patches when a config `Debug.Enabled` (default `false`) is set.
Uses `Logger.CreateLogSource` like the current code.

### Component: `CombatDamagePatch` (the real-damage fix)

Postfixes on the server-side damage getters so custom inscription stats are
reflected in real combat damage (solo/offline — server sim is in-process):

- `SkillBolt.CArgBase.GetCurWeaponAttr<T>` and
  `SkillBolt.CArgBase.GetWeaponPerformAttr<T>`.
- When `attr == STR_ENUM.INFO_PROP_LIST.Att` (6):
  1. Read the weapon prop from the skill (`ItemPropCache.GetPropByItem`), get its
     `Inscription` list.
  2. For each custom ID (`InscriptionRegistry.Instance.IsCustom`), aggregate
     `MulValue` / `AddValue` for the `Att` key via `WeaponStatContext`.
  3. Apply `__result = (base + addSum) * (mulSum + 10000) / 10000` to the base
     `Att` value.
- No config gate for the damage hook — `CombatDamagePatch` is applied
  unconditionally. `CombatDamagePatch.Apply(harmony, debugEnabled, log)`; the
  `[COMBAT]` verification log lines are emitted only when `Debug.Enabled` is on
  (threaded through).
- Hooks **unconditionally** (no solo/online guard); online co-op is a remote
  server so the getter postfix simply won't fire there. Verification step
  confirms this in-game.
- Generic-method patching: Harmony cannot patch an open generic definition, and
  the server calls the closed `<int>` form. Lead with
  `AccessTools.Method(typeof(CArgBase), "GetCurWeaponAttr", ...)` then
  `MakeGenericMethod(typeof(int))`.

### Component: `InscriptionInjector` (kept, cleaned)

- Keeps the prefix on `ItemPropCache.GetPropObjAndUpdate` (mutates
  `dInfo["Inscription"]` with the configurable per-slot chance + deterministic
  seeding keyed on `weaponSid + slot + current`) and `RecomputeStats` (rewrites
  `dInfo` stat keys routed through the change-cache so the client mirror + tooltip
  reflect the same numbers the combat patch will produce).
- Minor cleanup: comments, log levels, guard messaging.

### Config

```
[Injection] Chance    (existing)   per-slot replacement chance
[Debug]     Enabled   (new, false) enable diagnostic logging postfixes
```

### `GRModApi.cs` changes

- Register the `[Debug] Enabled` config entry.
- Call `InscriptionDataPatch.Apply(harmony)` (essential) always.
- Call `InscriptionDiagnostics.Apply(harmony, debugEnabled)` when enabled.
- Call `CombatDamagePatch.Apply(harmony, debugEnabled, log)`.
- Remove `WeaponStatsPatch.Apply()` / `CombatEventsPatch.Apply()` calls.

### `InscriptionBase.cs` changes

- Remove `OnHit(HitContext)`, `OnKill(KillContext)`, `OnShoot(ShootContext)`
  virtuals (and the context types they reference).

## Data flow after refactor

```
GS2CItemAdd/EquipAdd
  -> s2citemcon.CreateItemObject -> ItemPropCache.GetPropObjAndUpdate
       [InscriptionInjector.Prefix]  mutate dInfo["Inscription"] + recompute stats
                                     -> client mirror + tooltip (consistent numbers)
       [InscriptionDataPatch]        make custom IDs resolve in tables
                                     -> affixes show
  -> combat (solo, in-process server sim)
       [CombatDamagePatch.Postfix]   boost Att read by server getters
                                     -> REAL damage dealt
```

## Error handling / risks

- **Generic method patching** on Il2Cpp interop: patch the closed `<int>` form
  (`AccessTools.Method` + `MakeGenericMethod(typeof(int))`); the open generic
  definition is not patchable by Harmony. If neither binds, log a clear warning
  and continue.
- All patches wrapped in try/catch with `Log.LogWarning` on failure (existing
  pattern in `Test_StartingChestHook.TryPatch`).
- Postfix must guard null prop / null inscription list / non-custom lists (fast
  path returns early) to avoid per-hit overhead.
- Online co-op: getter postfix won't fire (remote server); documented and
  verified in-game, not a bug.

## Testing

1. `dotnet build GRModApi.sln -c Debug` must succeed (0 errors).
2. In-game (offline/solo):
   - Custom affix still appears (InscriptionDataPatch path intact).
   - Tooltip number for a `+300%` affix matches the recompute formula.
   - **Real damage dealt** on a hit with the custom affix reflects the bonus
     (CombatDamagePatch path). Log `[COMBAT]` lines on getter hits for
     verification, emitted only when `Debug.Enabled` is on.
3. Diagnostics gated off by default: startup log shows the module count but no
   `[PROP]`/`[BASE_UI]` spam unless `Debug.Enabled = true`.

## References

- Research: `docs/research/2026-08-03-updatepropvalue-createitemobject.md`,
  `docs/research/2026-08-03-smith-npc-inscription-shop.md`,
  `docs/research/README.md`.
- Combat RE output: `/tmp/opencode/out_combat.txt`, `out_getters.txt`,
  `out_itemflow.txt`, `out_trigger_cartoon.txt`, `out_caction.txt`.
- Addresses: `SkillManager.InitSkill` 0x181804770, `GetPropObjAndUpdate`
  0x1812BE4C0, `CreateItemObject` 0x1805BE850, `CArgBase.GetCurWeaponAttr<int>`
  0x1815DD640, `CArgBase.GetWeaponPerformAttr<int>` 0x1815E2A50.
