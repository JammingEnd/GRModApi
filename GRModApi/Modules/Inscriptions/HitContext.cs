namespace GRModApi.Modules.Inscriptions;

public class HitContext
{
    public object Target { get; }
    public int DamageValue { get; }

    public HitContext(object target, int damageValue)
    {
        Target = target;
        DamageValue = damageValue;
    }

    public bool Roll(float chance) => Random.Shared.NextDouble() < chance;
}
