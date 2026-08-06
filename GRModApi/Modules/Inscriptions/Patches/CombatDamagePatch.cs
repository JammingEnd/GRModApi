using System.Reflection;
using BepInEx.Logging;
using DataHelper;
using HarmonyLib;
using Item;
using SkillBolt;

namespace GRModApi.Modules.Inscriptions.Patches;

public static class CombatDamagePatch
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static ManualLogSource? _log;
    private static bool _debug;
    private static readonly HashSet<string> _fired = new();
    private static long _lastCombatLogMs;
    private static long _lastPropLogMs;

    public static void Apply(Harmony harmony, bool debugEnabled, ManualLogSource log)
    {
        _log = log;
        _debug = debugEnabled;

        TryPatch(harmony, "GetCurWeaponAttr", nameof(PostfixGetCurWeaponAttr));
        TryPatch(harmony, "GetWeaponPerformAttr", nameof(PostfixGetWeaponPerformAttr));
        TryPatchPropByItem(harmony);
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
            long addr = 0;
            try { addr = (long)closed.MethodHandle.GetFunctionPointer(); } catch { }
            harmony.Patch(closed,
                postfix: new HarmonyMethod(typeof(CombatDamagePatch).GetMethod(postfixName, Flags)));
            _log?.LogInfo($"Patched CArgBase.{methodName}<int> at 0x{addr:X}");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"Failed to patch CArgBase.{methodName}: {ex.Message}");
        }
    }

    // GetPropByItem is the non-generic choke point that GetCurWeaponAttr /
    // GetWeaponPerformAttr (and every other prop read) calls internally, so a
    // postfix here fires regardless of which generic instantiation the game uses.
    private static void TryPatchPropByItem(Harmony harmony)
    {
        try
        {
            var original = AccessTools.Method(typeof(ItemPropCache),
                nameof(ItemPropCache.GetPropByItem), new System.Type[] { typeof(int) });
            if (original == null)
            {
                _log?.LogWarning("ItemPropCache.GetPropByItem not found");
                return;
            }
            harmony.Patch(original,
                postfix: new HarmonyMethod(typeof(CombatDamagePatch).GetMethod(nameof(PostGetPropByItem), Flags)));
            _log?.LogInfo("Patched ItemPropCache.GetPropByItem");
        }
        catch (System.Exception ex)
        {
            _log?.LogWarning($"Failed to patch ItemPropCache.GetPropByItem: {ex.Message}");
        }
    }

    private static void PostfixGetCurWeaponAttr(CSkillBase __0, STR_ENUM.INFO_PROP_LIST __1, ref int __result)
    {
        LogFired("GetCurWeaponAttr", __0, __1, __result);
        ApplyBonus(__0, __1, ref __result);
    }

    private static void PostfixGetWeaponPerformAttr(CSkillBase __0, STR_ENUM.INFO_PROP_LIST __1, ref int __result)
    {
        LogFired("GetWeaponPerformAttr", __0, __1, __result);
        ApplyBonus(__0, __1, ref __result);
    }

    // Log the FIRST call of each (method, attr) pair so one combat test tells us
    // whether these getters are on the damage path at all, and which attrs are read.
    private static void LogFired(string method, CSkillBase skill, STR_ENUM.INFO_PROP_LIST attr, int result)
    {
        if (!_debug || skill == null) return;
        var key = $"{method}:{attr}";
        if (_fired.Add(key))
            _log?.LogInfo($"[COMBAT] {key} first fired, result={result}, weapon={skill.Weapon}");
    }

    // Throttle so rapid-fire combat cannot flood the log / freeze the game.
    private static bool ShouldLogCombat()
    {
        if (!_debug) return false;
        var now = System.Environment.TickCount64;
        if (now - _lastCombatLogMs < 250) return false;
        _lastCombatLogMs = now;
        return true;
    }

    private static void PostGetPropByItem(int itemid, NewItemProp __result)
    {
        if (!_debug || __result == null) return;
        try
        {
            bool hasOurs = false;
            var list = __result.Inscription;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (InscriptionRegistry.Instance.IsCustom(list[i]))
                    {
                        hasOurs = true;
                        break;
                    }
                }
            }

            var now = System.Environment.TickCount64;
            if (hasOurs || now - _lastPropLogMs >= 500)
            {
                _lastPropLogMs = now;
                _log?.LogInfo($"[GETPROP] itemid={itemid} Att={__result.Att} hasOurs={hasOurs}");
            }
        }
        catch (System.Exception ex)
        {
            if (_debug) _log?.LogWarning($"[GETPROP] failed: {ex.Message}");
        }
    }

    private static void ApplyBonus(CSkillBase skill, STR_ENUM.INFO_PROP_LIST attr, ref int result)
    {
        // Only the Att stat drives damage; ignore the other getter calls.
        if (attr != STR_ENUM.INFO_PROP_LIST.Att) return;

        if (ShouldLogCombat()) _log?.LogInfo($"[COMBAT] Att getter fired, result={result}, weapon={skill?.Weapon}");
        if (skill == null)
        {
            if (ShouldLogCombat()) _log?.LogInfo("[COMBAT] skill=null");
            return;
        }

        try
        {
            // Read the weapon prop directly from the skill (the server sim populates
            // CSkillBase.ItemPropCache in-process during solo combat).
            var prop = skill.ItemPropCache;
            if (prop == null)
            {
                if (ShouldLogCombat()) _log?.LogInfo("[COMBAT] ItemPropCache=null");
                return;
            }

            var inscriptionList = prop.Inscription;
            if (inscriptionList == null)
            {
                if (ShouldLogCombat()) _log?.LogInfo("[COMBAT] Inscription=null");
                return;
            }
            if (inscriptionList.Count == 0)
            {
                if (ShouldLogCombat()) _log?.LogInfo("[COMBAT] Inscription empty");
                return;
            }

            var ids = new List<int>();
            for (int i = 0; i < inscriptionList.Count; i++)
                ids.Add(inscriptionList[i]);

            var totals = InscriptionStatAggregator.Aggregate(ids);
            if (!totals.TryGetValue(Game.ItempropEvent.Att, out var t))
            {
                if (ShouldLogCombat()) _log?.LogInfo("[COMBAT] no custom Att");
                return;
            }
            if (t.Mul == 0 && t.Add == 0) return;

            int baseVal = result;
            result = (int)((baseVal + t.Add) * (t.Mul + 10000) / 10000f);
            if (ShouldLogCombat())
                _log?.LogInfo($"[COMBAT] {attr} base={baseVal} -> {result} (mul={t.Mul}, add={t.Add})");
        }
        catch (System.Exception ex)
        {
            if (ShouldLogCombat())
                _log?.LogWarning($"[COMBAT] failed: {ex.Message}");
        }
    }
}
