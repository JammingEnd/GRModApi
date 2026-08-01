# Client-Side Inscription Injector

Date: 2026-08-01

## Problem

Custom inscriptions registered via `InscriptionRegistry` (IDs 4992/4993) are fully
functional once they appear in a weapon's inscription list — they resolve through the
`GetInscriptionData` / `GetWeaponAttrInfo` / `GetAllInscription` patches, and the
`m_InscriptionDict` / `GetData()` tables are injected with them.

However, the server-side roll **never picks them**. It enumerates the global dict and
filters by weapon compatibility, but every rolled ID observed was `<= 4991` while ours
are `4992/4993`. The exact server-side cutoff was not located despite extensive
x64dbg breakpoint work (the roll call chain is inlined inside
`SteamPeerManager$$Update`, so the game-logic frames are not visible on the stack).
The `Weight` hypothesis was falsified by code inspection: `MakeData` sets
`Weight = 10000` (Test_StartingChestHook.cs:136), so a weighted roll would *favor*
ours, never skip them. This indicates a hard range/filter somewhere unreachable.

## Decision

Stop fighting the server roll. Instead, intercept weapons **after creation on the
client** and replace a rolled vanilla inscription ID with one of our own, with a
configurable per-slot chance.

## Architecture

### Interception point

`ItemPropCache.GetPropObjAndUpdate(int propID, Dictionary<string, object> dInfo)`
— the single choke point where the client builds a `NewItemProp` from a received
`dInfo` dict. At this point `dInfo["Inscription"]` is a `List<int>` of the rolled
IDs. We install a **prefix** that mutates the list *before* the original runs, so
the prop is built with our IDs from the start — guaranteeing propagation to
`NewItemProp`, UI, and effect code. A postfix would run after the prop is built
and could miss if the original copies the list, so a prefix is used.

This covers every weapon source (drops, equips, starting chest) through one hook,
and does not duplicate per-message (GS2CItemAdd / GS2CEquipAdd) handling.

Note: whether the starting chest flows through `GetPropObjAndUpdate` is unverified.
Drops and equips are confirmed to pass through it (the `[PROP]` logs); the starting
chest path should be confirmed during implementation. If it does not flow through
the hook, it simply won't be injected — drops/equips are unaffected.

### New component: `Modules/Inscriptions/InscriptionInjector.cs`

`Apply(Harmony)` installs a prefix on `ItemPropCache.GetPropObjAndUpdate`.

Prefix logic:
1. Guard `dInfo.ContainsKey("Inscription")` first (non-weapon items may lack the
   key; the Il2Cpp `Dictionary` indexer throws for a missing key), then read
   `dInfo["Inscription"]` as `List<int>`; skip if null or empty.
2. Resolve the weapon's type from `propID` via `ItemData`.
3. Candidate pool = `InscriptionRegistry.Instance.GetAll()` filtered for
   compatibility with this weapon, reusing the existing category/weapon-type
   mapping (`InscriptionRegistry.GetForWeapon(weaponType)`).
4. For each slot in the list, with configurable `Chance`, replace that slot's ID
   with a random compatible candidate. Guard rules: never replace a slot whose ID
   is already a custom inscription, and never introduce a duplicate custom ID
   (skip a candidate already present in another slot). If exclusive/special slot
   detection is feasible at runtime, also exclude those slots; otherwise rely on
   the duplicate-own-ID guard alone and document the limitation (see Out of scope).
5. Log each replacement (`[INJECTOR] replaced slot i: X -> Y`).

Prefix semantics: the prefix **must return `true`** (never skip the original) and
mutate the `List<int>` **in place**. It uses the real Il2Cpp types:
`Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> dInfo`
and the `Il2CppSystem.Collections.Generic.List<int>` stored under
`dInfo["Inscription"]`, matching the existing postfix signature
(Test_StartingChestHook.cs:372-373).

Note: resolving the weapon's type from `propID` via `ItemData` is currently
unverified — the plan must confirm the actual `propID -> GO_ENUM.WeaponType`
lookup path (the test hook only references `itemdataclass`, e.g.
Test_StartingChestHook.cs:473) before relying on `GetForWeapon`.

### Config

BepInEx `ConfigEntry<float>` (e.g. `Injection.Chance`, default `0.25`, range 0-1)
exposed on `GRModApi`'s `Config` and passed into the injector.

### Wiring

`GRModApi.Load()` calls `InscriptionInjector.Apply(harmony)` after the existing
Test hook setup.

## Multiplayer note

This is client-only. The replaced IDs (4992/4993) already exist in the client-side
tables (`m_InscriptionDict`, `GetData()`) injected by the test hook, so no
unknown-ID state is produced. Stat and effect math is computed from inscription
data client-side via the existing patches, keeping the local client consistent.
Authoritative server revalidation is out of scope.

## Out of scope

- Server-side roll modification.
- C2S propagation of injected inscriptions to other clients.
- Non-weapon inscription sources (amulets) unless they flow through the same hook.
- Config for per-inscription replacement weight.
- Replacing exclusive/special slots: detect rarity at runtime and exclude
  exclusives from the replaceable set if feasible; otherwise only guard against
  duplicating our own IDs and document the limitation.

## Verification

- Drop weapons in-game; confirm `[INJECTOR]` log lines fire with correct ID swaps.
- Confirm UI shows our inscription on the replaced slot.
- Confirm no errors / no unknown inscription IDs.
