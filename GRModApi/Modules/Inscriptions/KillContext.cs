namespace GRModApi.Modules.Inscriptions;

public class KillContext
{
    public object Target { get; }
    public int FinalDamage { get; }

    public KillContext(object target, int finalDamage)
    {
        Target = target;
        FinalDamage = finalDamage;
    }
}
