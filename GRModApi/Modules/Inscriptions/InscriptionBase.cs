namespace GRModApi.Modules.Inscriptions;

public abstract class WeaponInscription
{
    public int Id { get; internal set; }
    public InscriptionAttribute? Metadata { get; internal set; }

    public virtual void ModifyStats(WeaponStatContext ctx) { }
    public virtual void OnReload() { }
    public virtual void OnWeaponSwap() { }
}
