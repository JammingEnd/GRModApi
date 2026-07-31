using DataHelper;

namespace GRModApi.Modules.Inscriptions;

public class WeaponStatContext
{
    public Dictionary<string, WeaponAttrInfo> Attrs { get; } = new();

    public void AddStat(string attrName, int mulValue = 0, int addValue = 0)
    {
        if (Attrs.TryGetValue(attrName, out var existing))
        {
            existing.MulValue += mulValue;
            existing.AddValue += addValue;
        }
        else
        {
            Attrs[attrName] = new WeaponAttrInfo
            {
                AttrName = attrName,
                MulValue = mulValue,
                AddValue = addValue
            };
        }
    }

    public void AddDamage(int percent) => AddStat(Game.ItempropEvent.Att, mulValue: percent);
    public void AddFireRate(int percent) => AddStat(Game.ItempropEvent.AttSpeed, mulValue: percent);
    public void AddMagazineSize(int percent) => AddStat(Game.ItempropEvent.MaxBullet, mulValue: percent);
    public void AddReloadSpeed(int percent) => AddStat(Game.ItempropEvent.FillTime, mulValue: percent);
    public void AddExplosionRange(int percent) => AddStat(Game.ItempropEvent.Radius, mulValue: percent);
    public void AddAccuracy(int percent) => AddStat("Accuracy", mulValue: percent);
}
