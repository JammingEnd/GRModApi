using GRModApi.Modules.Inscriptions;
namespace GRModApi.Test.Inscriptions;

[Inscription(Description = "Lots More Damage",
    Rarity = InscriptionRarity.Rare,
    WeaponCategories = new[] { WeaponCategory.Handgun, WeaponCategory.RocketLauncher, WeaponCategory.CloseWeapon })]
public class Test_Inscription : WeaponInscription
{
    public override void ModifyStats(WeaponStatContext ctx)
        => ctx.AddDamage(200);
}
