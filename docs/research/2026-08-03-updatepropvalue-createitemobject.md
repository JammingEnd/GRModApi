# Weapon Create / Update Pipeline: `CreateItemObject` & `UpdatePropValue`

Date: 2026-08-03
Status: Research complete (decompiled from native `GameAssembly.dll`)

This document explains the two functions at the heart of creating and updating
weapons in Gunfire Reborn, and the support types around them. It is written for
the GRModApi project so that mods can hook the correct points.

Raw decompiles: `decompiles/weapon-create-update.txt`, `decompiles/updatepropbyitem.txt`.

---

## 1. The big picture

```
Server message arrives (GS2CItemAdd / GS2CEquipAdd)
   -> s2citemcon.CreateItemObject(...)
        -> ItemPropCache.GetPropObjAndUpdate(propID, dInfo)   [BUILD]
             -> ItemPropCache.UpdatePropByItem(itemID, key, value)
                  -> PropObject.UpdatePropValue(key, value, itemID, ...)
                       -> PropItem.UpdatePropValue(value)     [STORE]
                       -> PropObjectOptimizing change-cache  [QUEUE]
        -> new ItemObject(...)                                [WRAPPER]
   -> ItemManager.AddItem / AddEquip / AddWeapon              [REGISTER]
```

Two independent concerns:

1. **Creating** a weapon object from raw server data (`dInfo` dict) — the
   `GetPropObjAndUpdate` → `CreateItemObject` path.
2. **Updating** a single prop key to a live weapon — the
   `UpdatePropByItem` → `UpdatePropValue` path, which is also how the
   `PropObjectOptimizing` change-cache gets notified.

---

## 2. Types and layout

### `ItemPropCache` (TypeDefIndex 15553)

| Field | Offset | Type |
|-------|--------|------|
| `m_PropInfo` | 0x0 | `static Dictionary<int, NewItemProp>` — the master cache propID → prop |
| `clientID` | 0x8 | `static int` — negative starting point for temporary preview IDs |

### `NewItemProp : PropObject` (TypeDefIndex 15554)

The actual weapon prop object. Contains every rolled/derived stat key:
`SID`, `Shape`, `Att`, `AttSpeed`, `Inscription` (List<int>), `MaxBullet`,
`FillTime`, `Grade`, `MaxGrade`, `SealedInscription`, `Enhance`,
`ShareInscription`, etc. It also inherits from `PropObject`:

### `PropObject` (TypeDefIndex 15560)

| Field | Offset | Type |
|-------|--------|------|
| `ObjectID` | 0x10 | `int` — the propID (negative for temp previews) |
| `m_EventStr` | 0x18 | `string` — event name fired on change |
| `m_PropDict` | 0x20 | `Dictionary<string, PropItem>` — the actual stat values |
| `IsClone` | 0x28 | `bool` |
| `curCacheIndex` | 0x2C | `int` — double-buffer selector (0/1) |
| `m_ValueChangedCache` | 0x30 | `Dictionary<string, PropItem>` |
| `m_ValueChangedCacheBackUp` | 0x38 | `Dictionary<string, PropItem>` |
| `UpdatedPropLst` | 0x40 | `HashSet<string>` |
| `pool` | 0x48 | `ClassPool` |

### `PropItem` (TypeDefIndex 15559)

| Field | Offset | Type |
|-------|--------|------|
| `m_PropValueType` | 0x10 | `PropType` enum — how `Value` is interpreted |
| `Value` | 0x18 | `object` — the boxed value |

### `PropObjectOptimizing` (TypeDefIndex 15567)

| Field | Offset | Type |
|-------|--------|------|
| `unNeedDealAttr` | 0x0 | `static List<string>` — keys that do NOT trigger a change-cache deal |
| `instance` | 0x8 | `static` |
| `curCacheIndex` | 0x10 | `int` — double-buffer selector |
| `cachePropObject` | 0x18 | `Dictionary<int, PropObject>` |
| `cachePropObjectBackUp` | 0x20 | `Dictionary<int, PropObject>` |
| `hasDealedData` | 0x28 | `Dictionary<int, Dictionary<string, bool>>` |

### `ItemObject` (TypeDefIndex 16372)

| Field | Offset | Type |
|-------|--------|------|
| `ItemID` | 0x10 | `int` |
| `Shape` | 0x14 | `int` |
| `SID` | 0x18 | `int` — weapon type ID |
| `Pos` | 0x1C | `int` |
| `ItemMainType` | 0x20 | `ObjectDefine.ItemType` |
| `ItemSubType` | 0x24 | `ObjectDefine.ItemType` |
| `SIProp` | 0x28 | `NewItemProp` — reference to the prop built above |

### `PropType` enum

| Value | Name | Notes |
|-------|------|-------|
| 0 | `TYPE_INTEGER` | int |
| 1 | `TYPE_VARCHAR` | string |
| 2 | `TYPE_VECTOR` | List<int> |
| 3 | `TYPE_FLOAT` | float |
| 4 | `TYPE_INT100` | int scaled ×100 |
| 5 | `TYPE_FORECASTATTR` | List<int> |
| 6 | `TYPE_FORECASTVAR` | int |
| 7 | `TYPE_ENHANCE` | List<List<int>> |
| 8 | `TYPE_INTD100` | int ÷100 |
| 9 | `TYPE_LONG` | long |
| 10 | `TYPE_NEWVECTOR` | List<int> |
| 15 | `TYPE_LIST_FLOAT` | List<float> |
| 16 | `TYPE_LIST_STR` | List<string> |

The `PropItem.UpdatePropValue` switch groups these: direct-assign types
(0,1,3,4,6,8,9,0xB,0xC,0xD,0xE) write `Value = newValue`; the list types
(2,5,10,7,0xF,0x10) **clear + AddRange** the existing list; anything else is
an error log.

---

## 3. `CreateItemObject` — building a weapon

```c
ItemObject s2citemcon.CreateItemObject(
    int itemId, int pos, int sid,
    int mainType, int subType, string MainTypeName,
    Dictionary<string, object> dInfo)
```

Steps (decompile @ 0x1805BE850):

1. `handler = ItemPropCache.GetPropObjAndUpdate(itemId, dInfo)` — build/refresh
   the `NewItemProp` from the raw dict. **If null, the whole function returns
   null and no item is created.**
2. `shape = PropObject.GetPropValue<int>(handler, "Shape")` — read the shape
   from the built prop.
3. `item = new ItemObject(itemId, sid, shape, pos, mainType, subType)`
4. `item.SetConnectFailCallback(handler)` — **this is what links the prop to
   the ItemObject**; the prop is retained as the item's live stat source.
5. Return the `ItemObject`.

Callers:
- `GS2CItemAdd` (0x1805BEDA0): reads `SID` from `dInfo["SID"]`, resolves
  item data → `CreateItemObject` → `ItemManager.AddItem`.
- `GS2CEquipAdd` (0x1805BEA80): same, but distinguishes weapon (0x21000 =
  `ItemMainType` weapon) → `ItemManager.AddWeapon`, else `AddEquip`. Weapon pos
  is `pos + subType*100`.

### `GetPropObjAndUpdate` (0x1812BE4C0) — the core builder

```c
NewItemProp ItemPropCache.GetPropObjAndUpdate(int propID, Dictionary<string, object> dInfo)
```

1. `GetPropByItem(propID)` — if a prop already exists in `m_PropInfo`, reuse it
   (jump to step 5) — this is why re-sending the same propID updates instead of
   creating.
2. Otherwise `ClassPoolManager.SpawnClass(2)` to get a pooled `NewItemProp` (or
   `new NewItemProp` on pool miss), `IsClone = false`, `SetID(propID)`.
3. `m_PropInfo[propID] = prop` — register in the master cache.
4. Enumerate `dInfo`:
   ```
   foreach (key, value) in dInfo:
       ItemPropCache.UpdatePropByItem(propID, key, value, isSetProp: false)
   ```
5. Return the prop.

This is the single choke point every server-driven item build passes through —
the prefix hook point used by `InscriptionInjector`. It is also why mutating
`dInfo["Inscription"]` (and other stat keys) *before* this runs is the correct
strategy: the prop is then built with our values from the start, and the
change-cache is notified via the same `UpdatePropByItem` path.

### `UpdatePropByItem` (0x1812BE9E0)

```c
void ItemPropCache.UpdatePropByItem(int itemID, string attrName,
                                    object attrValue, bool isSetProp)
```

1. If `m_PropInfo` is null → return.
2. If `!m_PropInfo.ContainsKey(itemID)` → return (prop not built yet).
3. `prop = m_PropInfo[itemID]`
4. `PropObject.UpdatePropValue(prop, attrName, attrValue, itemID, isSetProp, false)`

So `UpdatePropByItem` is just a thin, guarded dispatcher. All the interesting
behavior is in `UpdatePropValue`.

---

## 4. `UpdatePropValue` — updating a prop key

### `PropObject.UpdatePropValue` (0x180CA4EC0)

```c
void PropObject.UpdatePropValue(PropObject self,
    string key, object value, int itemID, bool isSetProp, bool noExec)
```

1. **SetReload special case**: if `isSetProp` and key is the reload-end key,
   call `SetReloadEndMsg(itemID)`.
2. `TryGetValue(self.m_PropDict, key, out propItem)` — **if the key is not a
   known stat key, return immediately.** (Unknown keys are silently ignored.)
3. Add key to `UpdatedPropLst`.
4. `PropItem.UpdatePropValue(propItem, value)` — actually store the value (see
   the switch table above; lists are clear+AddRange, scalars overwrite).
5. **Change-cache notification** — unless the key is in
   `PropObjectOptimizing.unNeedDealAttr`:
   - If `hasDealedData[ObjectID]` already contains the key → the update is
     **deferred**: add `(ObjectID, key)` to `m_ValueChangedCache`
     (or `m_ValueChangedCacheBackUp`, whichever matches `curCacheIndex`), and
     return — this is the case when the object is currently mid-"deal".
   - Otherwise: record `hasDealedData[ObjectID][key] = true`, then
     `ScriptEventManager.ExecEvent(m_EventStr, key, ObjectID)` — fires the
     per-prop change event so listeners (UI, combat, etc.) refresh.
6. `noExec` short-circuits the event firing (used for pure cache registration).

Key takeaways for mods:

- **A key must already exist in `m_PropDict`** for the value to stick. Writing a
  brand-new key is a no-op. (The injector's `RecomputeStats` therefore only
  rewrites keys already present in `dInfo`.)
- **Lists are cleared and re-filled, not replaced** — the same
  `List<int>` instance is mutated. Mutating a list you got via
  `GetPropValue<List<int>>` in place will be picked up, but assigning a *new*
  list object replaces the reference only after clear+AddRange copies its
  contents.
- The `ExecEvent(m_EventStr, key, ObjectID)` call is how the game propagates
  "this weapon's Att changed" to everything that cares (UI tooltips, damage
  calc). Combat reads the live prop values and is refreshed by these events.
- `m_ValueChangedCache` double-buffering (via `curCacheIndex` and the
  `PropObjectOptimizing.cachePropObject*` dictionaries) means rapid successive
  updates are batched and flushed on the next optimization pass, avoiding
  redundant event storms.

### `PropItem.UpdatePropValue` (0x180CA3620)

Stores the value according to `m_PropValueType`:

- **Scalar types** (int/float/string/long variants): `Value = value`.
- **List types** (`List<int>`, `List<float>`, `List<string>`, `Enhance`):
  clear the existing list, then `AddRange(value)` — preserving list identity.
- Anything else: `LogE("PropType not supported: ...")`.

### `PropObjectOptimizing` — the flush

The optimizer holds double-buffered `cachePropObject` / `cachePropObjectBackUp`
dictionaries and `hasDealedData`. On its pass it walks props with pending
changes, applies them, and fires the relevant events / clears the buffers. The
net effect: stat writes are coalesced and consumers see one consistent update
per frame rather than per-key.

---

## 5. Mapping to our injector

| Our need | Game mechanism |
|----------|----------------|
| Inject custom inscription ID into the list | Prefix on `GetPropObjAndUpdate` mutating `dInfo["Inscription"]` (List<int>) before the builder runs |
| Make derived stat (e.g. `Att`) reflect the inscription | `RecomputeStats` rewriting `dInfo[key]` for keys already present — routed through `UpdatePropByItem` → `UpdatePropValue`, so the change-cache + `ExecEvent` notify UI and combat |
| Ensure inventory number updates | The inventory UI reads `NewItemProp.Att`; it is refreshed via the `ExecEvent(m_EventStr, key, ObjectID)` path (and/or layout rebuild) |
| Avoid invalid writes | Only touch keys present in `m_PropDict` (via `dInfo.ContainsKey`); box floats as `Il2CppSystem.Single` (`new Single { m_value = v }`); lists stay `List<int>` |

### Where inventory vs combat diverge

- **Drop preview / tooltip** recomputes stat text from the inscription list
  (`PCWeaponBaseTitle.GetAddAttrInfo` accumulation: `(base + addSum) ×
  (mulSum + 10000) / 10000`). This is display-only and does not touch the prop.
- **Inventory number & combat damage** read the live `NewItemProp` values. They
  only reflect a change if the prop value was actually updated through
  `UpdatePropValue` (or rebuilt), which is why rewriting `dInfo` in the prefix
  (so `UpdatePropByItem` stores our values) is the correct lever — a postfix or
  direct field write that bypasses `UpdatePropValue` never reaches combat.

---

## 6. References

- Decompiles: `decompiles/weapon-create-update.txt`, `decompiles/updatepropbyitem.txt`
- Addresses:
  - `s2citemcon.CreateItemObject` 0x1805BE850
  - `s2citemcon.GS2CItemAdd` 0x1805BEDA0
  - `s2citemcon.GS2CEquipAdd` 0x1805BEA80
  - `ItemPropCache.GetPropObjAndUpdate` 0x1812BE4C0
  - `ItemPropCache.UpdatePropByItem` 0x1812BE9E0
  - `PropObject.UpdatePropValue` 0x180CA4EC0
  - `PropItem.UpdatePropValue` 0x180CA3620
- Il2CppDumper dump.cs TypeDefIndex: `ItemPropCache` 15553, `NewItemProp` 15554,
  `PropItem` 15559, `PropObject` 15560, `PropObjectOptimizing` 15567,
  `ItemObject` 16372.
