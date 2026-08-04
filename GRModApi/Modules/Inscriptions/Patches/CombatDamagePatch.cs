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
