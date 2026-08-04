using DataHelper;

namespace GRModApi.Modules.Inscriptions;

public static class InscriptionStatAggregator
{
    public static Dictionary<string, (int Mul, int Add)> Aggregate(IEnumerable<int> inscriptionIds)
    {
        var totals = new Dictionary<string, (int Mul, int Add)>();
        foreach (var id in inscriptionIds)
        {
            var insc = InscriptionRegistry.Instance.GetById(id);
            if (insc == null) continue;

            var ctx = new WeaponStatContext();
            insc.ModifyStats(ctx);
            foreach (var kvp in ctx.Attrs)
            {
                if (totals.TryGetValue(kvp.Key, out var acc))
                    totals[kvp.Key] = (acc.Mul + kvp.Value.MulValue, acc.Add + kvp.Value.AddValue);
                else
                    totals[kvp.Key] = (kvp.Value.MulValue, kvp.Value.AddValue);
            }
        }
        return totals;
    }
}
