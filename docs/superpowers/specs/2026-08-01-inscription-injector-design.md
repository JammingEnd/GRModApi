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
IDs, and it runs *before* the prop's `set_Inscription` is applied. Mutating that
list there propagates to `NewItemProp`, UI, and effect code automatically.

This covers every weapon source (drops, equips, starting chest) through one hook,
and does not duplicate per-message (GS2CItemAdd / GS2CEquipAdd) handling.

### New component: `Modules/Inscriptions/InscriptionInjector.cs`

`Apply(Harmony)` installs a postfix on `ItemPropCache.GetPropObjAndUpdate`.

Postfix logic:
1. Read `dInfo["Inscription"]` as `List<int>`; skip if null or empty.
2. Resolve the weapon's type from `propID` via `ItemData`.
3. Candidate pool = `InscriptionRegistry.Instance.GetAll()` filtered for
   compatibility with this weapon, reusing the existing category/weapon-type
   mapping (`InscriptionRegistry.GetForWeapon(weaponType)`) plus the
   `LimitWeapon`/SID check for precision.
4. For each slot in the list, with configurable `Chance`, replace that slot's ID
   with a random compatible candidate. Never replace a slot that already holds a
   custom inscription or an exclusive/other-special inscription.
5. Log each replacement (`[INJECTOR] replaced slot i: X -> Y`).

### Config

BepInEx `ConfigEntry<float>` (e.g. `Injection.Chance`, default `0.25`, range 0-1)
exposed on `GRModApi`'s `Config` and passed into the injector.

### Wiring

`GRModApi.Load()` calls `InscriptionInjector.Apply(harmony)` after the existing
Test hook setup.

## Multiplayer note

This is client-only. The replaced IDs (4992/4993) already exist in the server-side
tables (`m_InscriptionDict`, `GetData()`), so no unknown-ID state is produced. Stat
and effect math is computed from inscription data client-side via the existing
patches, keeping the local client consistent. Authoritative server revalidation is
out of scope.

## Out of scope

- Server-side roll modification.
- C2S propagation of injected inscriptions to other clients.
- Non-weapon inscription sources (amulets) unless they flow through the same hook.
- Config for per-inscription replacement weight.

## Verification

- Drop weapons in-game; confirm `[INJECTOR]` log lines fire with correct ID swaps.
- Confirm UI shows our inscription on the replaced slot.
- Confirm no errors / no unknown inscription IDs.
