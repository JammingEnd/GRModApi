namespace GRModApi.Modules.Inscriptions;

public enum InscriptionRarity
{
    Normal,
    Rare,
    DoubleIns,
    Exclusive
}

public enum WeaponCategory
{
    Rifle,
    SMG,
    Shotgun,
    Handgun,
    Sniper,
    RocketLauncher,
    Laser,
    CloseWeapon,
    Amulet
}

[AttributeUsage(AttributeTargets.Class)]
public class InscriptionAttribute : Attribute
{
    public string? Description { get; init; }
    public InscriptionRarity Rarity { get; set; } = InscriptionRarity.Normal;
    public WeaponCategory[]? WeaponCategories { get; set; }
    public float Weight { get; set; } = 1f;
}
