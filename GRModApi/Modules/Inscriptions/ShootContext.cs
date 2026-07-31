namespace GRModApi.Modules.Inscriptions;

public class ShootContext
{
    public int WeaponId { get; }
    public int RemainingAmmo { get; }

    public ShootContext(int weaponId, int remainingAmmo)
    {
        WeaponId = weaponId;
        RemainingAmmo = remainingAmmo;
    }
}
