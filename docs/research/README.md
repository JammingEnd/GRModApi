# Research: Gunfire Reborn reverse-engineering notes

Reverse-engineering research for the GRModApi project. All findings come from
the native `GameAssembly.dll` via the Ghidra project:

- Project (copy used for headless export): `/tmp/opencode/GunfireReborn_decomp_23_7_2026.rep`
- Original: `/home/jammingend/Documents/gr-modding/Ghidra/GunfireReborn_decomp_23_7_2026.rep`
- Il2CppDumper output: `/home/jammingend/Documents/gr-modding/depot/Il2CppDumper_Output/`
- In-game log: `.../BepInEx/LogOutput.log`

## Decompiles (`decompiles/`)

Raw Ghidra C output for the functions studied:

| File | Contents |
|------|----------|
| `warinscriptionmanager.txt` | `WarInscriptionManager` (NPC inscription shop state), `OCSmithNpc` |
| `s2cnpc-interact.txt` | `s2cnpc.GS2CNpcItemInteract`, `GS2CNpcItemResult`, `C2GSNPCInteract` |
| `inscriptionshop-ui.txt` | `PCInscriptionShop_logic` (`Selecting`, `WeaponPropChange`, `RefreshPanel`, `ItemPropChange`, `SetUpDetailedPanel`) |
| `weapon-create-update.txt` | `s2citemcon.CreateItemObject`, `GS2CItemAdd`, `GS2CEquipAdd`, `ItemPropCache.GetPropObjAndUpdate`, `PropObject.UpdatePropValue`, `PropItem.UpdatePropValue` |
| `updatepropbyitem.txt` | `ItemPropCache.UpdatePropByItem` |

## Documents

- `2026-08-03-smith-npc-inscription-shop.md` — the in-game NPC that adds/rerolls weapon affixes.
- `2026-08-03-updatepropvalue-createitemobject.md` — deep dive into the weapon create/update pipeline.
