# The Smith NPC: Inscription Add / Reroll Shop

Date: 2026-08-03
Status: Research complete (decompiled from native `GameAssembly.dll`)

## Summary

The game has a Smith NPC (`OCNpc.OCSmithNpc`) that lets the player add a new
affix (inscription) to a weapon and reroll/reforge existing ones. The flow is
**server-authoritative**: the client only requests an operation and displays the
server's result. Stat recomputation is done on the server; the client's
`NewItemProp` is updated through the normal prop-update pipeline
(`UpdatePropValue` → `PropObjectOptimizing` change cache).

## Types

| Class | TypeDefIndex | Role |
|-------|--------------|------|
| `OCNpc.OCSmithNpc` | 21916 | The NPC; entry point |
| `s2cnpc` | 16248 | Network message helper (C2G requests / GS2C responses) |
| `WarInscriptionManager` | 16086 | Client-side panel state + candidate filtering |
| `InscriptionUnit` / `InscriptionUnit.Option` | 16087 / 16088 | Weapon + selectable options |
| `UIScript.PCInscriptionShop_logic` | 22751 | The shop UI form (`INSCIPRTION_SHOP`) |
| `UIScript.PCInscriptionShop_logic.OptionType` | 22752 | `Enhance=0, Reforge=1, Etch=2, Reroll=3` |
| `UIScript.PCInscriptionShop_logic.OptionStatus` | 22753 | `Show=1, Hide=2, NonInteractive=3` |

## End-to-end flow

```
1. Interact
   OCSmithNpc.OP()
     -> s2cnpc.C2GSNPCInteract(ObjectID, NpcType, SID)
   OCSmithNpc.GetInteractMsg()
     -> 1023 (Msg_Npc_Inscription) if hero has a weapon
     -> 1021 (Msg_Npc_Noweapon) otherwise

2. Server response (option list)
   s2cnpc.GS2CNpcItemInteract(serverData)
     -> builds List<InscriptionUnit> (one per weapon)
        each Option = { optionID, cashType, cost, optionStatus }
     -> WarInscriptionManager.SetupInscriptionPanel(menuIdx, iNpc, objlist)
        - stashes m_GsPropList, m_MenuIndex
        - opens UIForm INSCIPRTION_SHOP

3. Shop UI
   PCInscriptionShop_logic (per weapon slot: Main / Deputy)
     RefreshPanel / BuildWeaponPanel / RefreshSetupOption
     4 operations: Enhance, Reforge, Etch, Reroll

4. Player picks an option
   Selecting() -> CheckMoneyFit()
     -> WarInscriptionManager.SendRequest(ItemID, Choice)
        - Choice -1: close (cancel)
        - Choice 0..3: map to OptionType
     -> s2cnpc.C2GSNpcItemInteract(m_MenuIndex, ItemID, Choice)

5. Server resolves
   s2cnpc.GS2CNpcItemResult(iNpc, iItem, iResult)
     -> ScriptEventManager.ExecEvent("InscriptionResult", iItem, iResult)
        (event string literal _StringLiteral_41727)

6. UI refreshes
   WeaponPropChange(object[] msg)  // subscribed to "InscriptionResult"
     -> RefreshPanel, ForceRebuildContentFilter, UpdateCurMouseEnterState
     -> RefreshSetupOption, RecordWeaponGradeAndTryPlayAnimator
     -> ShowConflictIns, SetMutexBtn
```

## Key observations

- `SetupInscriptionPanel` clears and replaces `m_GsPropList`, then (if the list is
  non-empty) opens `INSCIPRTION_SHOP` and registers the NPC component for update
  via `NewPlayerObject.AddUpdateCom`. If the list is empty it shows a "no weapon"
  common notification instead.
- `SendRequest` first plays a sound effect per choice (0x2335 cancel, 0x2344
  enhance, 0x2345 others), then forwards to the server. `Choice == -1` closes.
- `Selecting` checks `CheckMoneyFit` on the chosen option before sending; the
  choice value passed is the option's `optionID` (`param_3`), and the actual
  operation enum is looked up from the `InscriptionUnit.Option` list.
- `WeaponPropChange` handles two events:
  - `_StringLiteral_41726` → full panel refresh (list changed)
  - `_StringLiteral_41727` ("InscriptionResult") → per-item refresh of the
    changed weapon (grade tracking, setup options, layout rebuilds).
- `ItemPropChange` listens for a game itemprop event and calls
  `RecordWeaponGradeAndTryPlayAnimator` when an item's prop changes.

## Candidate / eligibility filtering (client-side)

`WarInscriptionManager` exposes the rules that determine which inscriptions can
be applied to a weapon. These mirror server logic and are used to build options:

| Function | Check |
|----------|-------|
| `CheckShareInscription` | iterate the other weapon's inscription list; skip IDs already present; skip `DoubleIns`; apply `CheckExcludeInsc` + `CheckInscTag` + `CheckInscWeapon`; collect into `returnLst` |
| `CheckExcludeInsc` | mutual-exclusion: skip an inscription that appears in an existing inscription's exclusion list, or whose own exclusion list contains an existing one (`inscriptiondataclass+0x60`) |
| `CheckInscTag` | weapon-tag matching: count matches against the weapon's tag list (`inscriptionData+0x38`), and enforce require/exclude lists (`+0x48` require, `+0x68` exclusive count) |
| `CheckInscWeapon` | allowed/disallowed weapon SIDs (`inscriptionData+0x30` allowed, `+0x40` disallowed) |

`NewItemProp.AddInscriptionTimes` (INFO_PROP_LIST 56) limits how many times the
Smith can add affixes to a weapon.

## Relevance to GRModApi (inscription injector)

- **Stat recompute is server-side.** When the Smith changes a weapon's
  inscriptions, the client receives only a result event; the actual
  `NewItemProp` values are refreshed through the same
  `UpdatePropByItem` → `UpdatePropValue` → `PropObjectOptimizing` change-cache
  pipeline our injector routes through. There is no separate client-side
  "recompute" code path we missed.
- The client-side candidate filters (`CheckInscWeapon`, `CheckInscTag`,
  `CheckExcludeInsc`) are a good reference for making our custom inscriptions
  appear legal / compatible.
- The event name for inscription changes is `Inscription_Event`
  (`0x6A0E028`); the operation result event is the "InscriptionResult" string.

## Decompiles

See `decompiles/`:
- `warinscriptionmanager.txt`
- `s2cnpc-interact.txt`
- `inscriptionshop-ui.txt`

Raw RVAs / addresses used:
- `OCSmithNpc.OP` 0x180A023E0, `GetInteractMsg` 0x180A02380
- `WarInscriptionManager.*` 0x180B7B7A0 – 0x180B7C7F0
- `s2cnpc.GS2CNpcItemInteract` 0x1808521F0, `GS2CNpcItemResult` 0x180852670
- `PCInscriptionShop_logic.*` 0x180C4BF10 – 0x180C4F330
