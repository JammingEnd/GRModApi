# Client-Side vs Server-Side Game Code Inventory

Date: 2026-08-01
Source: `dump.cs` (Il2CppDumper) + Ghidra decompiles of `GunfireReborn`. Line
numbers are dump.cs anchor lines unless noted otherwise.

Purpose: document which functions/classes the inscription injector touches run on
the **client** (our BepInEx plugin) vs which run on the **server** (authoritative,
out of reach). We only ever patch client-side code; server behavior is
"as observed" and cannot be modified.

## Message flow (authoritative view)

```
Server (host/authoritative)
   roll inscription IDs, bake weapon stats (Att, ...), build dInfo
   send GS2CItemAdd / GS2CEquipAdd
        |
        v
Client
   basenet -> s2citemcon_GS2CItemAdd / s2citemcon_GS2CEquipAdd   (net msg dispatch)
   s2citemcon.GS2CItemAdd / GS2CEquipAdd                          (handler)
   ItemManager.AddItem / AddWeapon                                (item DB + UI)
   ItemPropCache.GetPropObjAndUpdate(propID, dInfo)               (builds NewItemProp)
   inscriptionData.*  (data tables)
   PCWeaponBaseTitle.GetAddAttrInfo / ShowInscriptionList         (UI + stat math)
```

The server sends only the `dInfo` payload inside the GS2C message. All
`NewItemProp` construction, inscription lookups, stat math and UI rendering happen
client-side.

## CLIENT (patchable — runs in our process)

### Message dispatch / network inbound
| Type / method | dump.cs line | Role |
|---|---|---|
| `basenet` | 1505348 / 1505354 | Network layer that routes GS2C frames to `s2citemcon` handlers |
| `s2citemcon_GS2CItemAdd(basenet)` | 1505348 | Static inbound dispatcher |
| `s2citemcon_GS2CEquipAdd(basenet)` | 1505354 | Static inbound dispatcher |
| `s2citemcon.GS2CItemAdd(data)` | 906793 | Handler for `s2citemcon_GS2CItemAddClass` |
| `s2citemcon.GS2CEquipAdd(data)` | 906799 | Handler for `s2citemcon_GS2CEquipAddClass` |
| `s2citemcon_GS2CItemAddClass` / `s2citemcon_GS2CEquipAddClass` | 1507386 / 1507406 | Message payload structs. **Note:** `GS2CEquipAddClass` has no `dInfo` field (verified). |

### Item database / prop construction
| Type / method | dump.cs line | Role |
|---|---|---|
| `ItemManager` (static) | 1185117 | Client item DB (`ItemDict`, `HeroItemInfoDict`) |
| `ItemManager.AddItem(ItemObject, heroID)` | 1185132 | Register an item client-side |
| `ItemManager.AddWeapon(ItemObject, heroID)` | 1185141 | Register a weapon client-side; invoked from the GS2C handlers. **Safe hook point.** |
| `ItemPropCache.UpdatePropByItem(itemid, attrName, value, isSetProp)` | 875542 | Write a single attribute onto a prop |
| `ItemPropCache.GetPropObjAndUpdate(propID, dInfo)` | 875545 | Builds a `NewItemProp` from server `dInfo`; iterates every `dInfo` key and calls `UpdatePropByItem`. **Our injector's hook.** |
| `NewItemProp` | 875561 | Prop data holder: `SID`, `Att` (float), `Inscription` (`List<int>`) |
| `ItemObject` | 912840 | In-world item wrapper (`SIProp` field at offset 0x28 holds the `NewItemProp` instance read by combat/UI) |
| `WarDropWeaponManager` (static) | 899500 | Client-side weapon drop spawn helper |

### Inscription data tables (client)
| Type / method | dump.cs line | Role |
|---|---|---|
| `inscriptionData` (Singleton) | 1432647 | Client inscription table accessor |
| `inscriptionData.GetWeaponAttrInfo(sid, attrName)` | 1432670 | Returns `WeaponAttrInfo` (MulValue/AddValue) for a sid+attr |
| `inscriptionData.GetWeaponInscription(weapinID)` | 1432673 | Candidate ID pool for a weapon sid |
| `inscriptionData.GetInscriptionData(sid)` / `GetAllInscription()` | (in class) | Resolve / enumerate inscription data |
| `inscriptionData.Init` | (in class) | Populates `m_InscriptionDict` from `GetData()` |
| `inscriptiondataclass` | (in class) | Inscription row (Name, Desc, ItemType, Weight, WeaponAddAttrInfo, LimitWeaponType, ...) |
| `TableData.inscriptiondata.GetData` / `GetNormalData` / `GetPressData` | (in class) | Static table accessors our `[TABLE_DATA]` injection writes into |

### UI + stat aggregation (client)
| Type / method | dump.cs line | Role |
|---|---|---|
| `PCWeaponBaseTitle` | 1167865 | Weapon base title UI form |
| `PCWeaponBaseTitle.GetAddAttrInfo(inscriptionLst, enhanceLst, grade, attrName, ref val)` | 1168025 | Aggregates inscription damage for an attr. **Aggregation path.** |
| `PCWeaponBaseTitle.<GetAddAttrInfo>b__100_1(x)` | 1168162 | `Where` filter — decompiles to `x != 0` (passes our sids; NOT a blocker) |
| `PCWeaponBaseTitle.<GetAddAttrInfo>b__0(WeaponAttrInfo)` | 1168180 | Closure accumulating `mulPositive` / `mulNegative` / `addValue` |
| `PCWeaponBaseTitle.ShowInscription` / `ShowInscriptionList` | (in class) | Renders the inscription list in the UI |
| `PCWeaponPanel_Logic.SetWeaponInscriptionPanel(List<int>)` | (in class) | Panel list binding |
| `HeroWeaponPropCtrl` (static) | 902751 | Hero/weapon prop presentation control |
| `WarInscriptionManager` (static) | 899691 | `CheckInscWeapon` / `CheckInscTag` validation used by UI/forge |
| `WeaponInspPretreatment` | 905667 | Weapon inscription UI pretreat |
| `IncreaseAtt.GetPropObjectData` | 877420 | Passive-skill data cache via `ClassPoolManager.SpawnClass(7)` — **not** the weapon inscription damage path (ruled out) |
| `CurWeaponSS` / `DepWeaponSS` | 920260 / 920411 | Skill slot classes (passive skill dict) — unrelated to weapon inscription slots |

## SERVER (authoritative — NOT in client binary, cannot be patched)

| Item | Where | Role |
|---|---|---|
| Server-side inscription roll | Not in `dump.cs` | Enumerates the global inscription dict, filters by weapon compatibility, and picks rolled IDs. Observed cutoff: every rolled ID `<= 4991` while ours are `4992/4993` — ours are never picked server-side. Roll call chain is inlined inside `SteamPeerManager$$Update` so no logic frames are visible on the stack. |
| `dInfo` producer | Not in `dump.cs` | Builds the `dInfo` payload for `GS2CItemAdd` / `GS2CEquipAdd`. Server bakes `Att` (base stats) and the `Inscription` list into this payload; for weapons the payload has ~39 keys incl. `Inscription`, for skill props (propIDs 2..51) ~20/2 keys with no `Inscription`. |
| Weapon stat baking (`Att`) | Not in `dump.cs` | Weapon `Att` arrives already-computed from the server. A client postfix that mutates only `Inscription` never changes `Att`. |

## Implication for the injector

- We **can** patch: everything above the line — `GetPropObjAndUpdate`,
  `AddWeapon`, `inscriptionData.*`, `PCWeaponBaseTitle.*`.
- We **cannot** change the server roll. The injector must therefore take a
  vanilla-rolled weapon and swap an ID client-side, which is what
  `InscriptionInjector` does via a postfix on `GetPropObjAndUpdate`.
- Because the server re-sends a fresh `dInfo` on pick-up, the client rebuilds the
  prop (`GetPropObjAndUpdate` runs again) and a non-deterministic re-roll can
  revert the swap — this is the "revert on pick-up" symptom, and the reason the
  injector's roll was made deterministic.

## Verified runtime signatures (client)

- `ItemPropCache.GetPropObjAndUpdate(int propID, Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> dInfo) -> NewItemProp`
- `ItemPropCache.UpdatePropByItem(int itemid, string attrName, Il2CppSystem.Object attrValue, bool isSetProp)`
- `s2citemcon_GS2CEquipAddClass` — no `dInfo` field (logged `[S2C_EQUIP] no dInfo field`)
- `PCWeaponBaseTitle.GetAddAttrInfo` postfix signature in this repo:
  `(Il2CppSystem.Collections.Generic.List<int> inscriptionLst, string attrName, ref float val)`
