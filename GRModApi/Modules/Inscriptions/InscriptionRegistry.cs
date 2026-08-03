using System.Reflection;
using BepInEx.Logging;
using DataHelper;

namespace GRModApi.Modules.Inscriptions;

public class InscriptionRegistry
{
    public static InscriptionRegistry Instance { get; } = new();

    private readonly List<WeaponInscription> _inscriptions = new();
    private ManualLogSource? _log;
    private int _nextId = 100000;
    private bool _idsAssigned;

    private static readonly Dictionary<WeaponCategory, GO_ENUM.WeaponType> CategoryMap = new()
    {
        [WeaponCategory.Rifle] = GO_ENUM.WeaponType.EQUIP_RIFLE,
        [WeaponCategory.SMG] = GO_ENUM.WeaponType.EQUIP_SMG,
        [WeaponCategory.Shotgun] = GO_ENUM.WeaponType.EQUIP_SHOTGUN,
        [WeaponCategory.Handgun] = GO_ENUM.WeaponType.EQUIP_HANDGUN,
        [WeaponCategory.Sniper] = GO_ENUM.WeaponType.EQUIP_SNIPER,
        [WeaponCategory.RocketLauncher] = GO_ENUM.WeaponType.EQUIP_ROCKET_LAUNCHER,
        [WeaponCategory.Laser] = GO_ENUM.WeaponType.EQUIP_LASER,
        [WeaponCategory.CloseWeapon] = GO_ENUM.WeaponType.EQUIP_TYPE_CLOSEWEAPON,
        [WeaponCategory.Amulet] = GO_ENUM.WeaponType.EQUIP_TYPE_AMULET,
    };

    public void Initialize(ManualLogSource log)
    {
        _log = log;

        var types = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(WeaponInscription)));

        foreach (var type in types)
        {
            Register(type);
        }

        if (_inscriptions.Count == 0)
            log.LogWarning("No inscriptions registered");
        else
            log.LogInfo($"Inscription registry initialized with {_inscriptions.Count} inscriptions");
    }

    public void Register<T>() where T : WeaponInscription, new()
    {
        var attr = typeof(T).GetCustomAttribute<InscriptionAttribute>(false);
        if (attr == null)
            throw new InvalidOperationException($"{typeof(T).Name} missing [Inscription] attribute");

        var instance = new T();
        AssignId(instance, attr, typeof(T).Name);
    }

    public void Register(Type type)
    {
        var attr = type.GetCustomAttribute<InscriptionAttribute>(false);
        if (attr == null)
        {
            _log?.LogWarning($"WeaponInscription subclass {type.FullName} missing [Inscription] attribute");
            return;
        }

        try
        {
            var instance = (WeaponInscription)Activator.CreateInstance(type)!;
            AssignId(instance, attr, type.Name);
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"Failed to register inscription {type.FullName}: {ex.Message}");
        }
    }

    private void AssignId(WeaponInscription instance, InscriptionAttribute attr, string typeName)
    {
        instance.Id = _nextId++;
        instance.Metadata = attr;
        _inscriptions.Add(instance);
        _log?.LogInfo($"Registered inscription [{instance.Id}] {typeName}: {attr.Description ?? "(no desc)"}");
    }

    public IEnumerable<WeaponInscription> GetAll() => _inscriptions;

    public WeaponInscription? GetById(int id) => _inscriptions.Find(i => i.Id == id);

    public bool IsCustom(int id) => GetById(id) != null;

    public int CustomIdCount() => _inscriptions.Count;

    public void AssignIdsFromTable(Il2CppSystem.Collections.Generic.Dictionary<int, inscriptiondataclass>? table)
    {
        if (_idsAssigned) return;
        _idsAssigned = true;

        // Keep the high IDs (starting at 100000) assigned at registration time.
        // Reassigning into the vanilla range (< 13000) caused the custom
        // inscriptions to be mistaken for real affixes (see b53ad2d which added
        // the low-ID reassignment). Only purge stale entries from the table.
        if (table != null)
        {
            var staleIds = _inscriptions.Select(i => i.Id).ToList();
            foreach (var oldId in staleIds)
            {
                if (table.ContainsKey(oldId))
                    table.Remove(oldId);
            }
        }
    }

    public void ResetIds()
    {
        _idsAssigned = false;
        _nextId = 100000;
    }

    public IEnumerable<WeaponInscription> GetForWeapon(int weaponType)
    {
        return _inscriptions.Where(i =>
        {
            var cats = i.Metadata?.WeaponCategories;
            return cats == null || cats.Length == 0 ||
                   cats.Any(c => CategoryMap.TryGetValue(c, out var wt) && (int)wt == weaponType);
        });
    }
}
