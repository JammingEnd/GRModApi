using GRModApi.Modules.Inscriptions;
namespace GRModApi.Test.Inscriptions;

[Inscription(Description = "RAAAH (+300% damage)",
    Rarity = InscriptionRarity.Exclusive,
    WeaponCategories = new[] { WeaponCategory.Sniper })]
public class SniperInscription : WeaponInscription
{
    public override void ModifyStats(WeaponStatContext ctx)
        => ctx.AddDamage(300);
}
