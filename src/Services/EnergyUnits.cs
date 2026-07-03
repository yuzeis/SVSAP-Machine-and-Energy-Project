namespace SVSAPME.Services;

internal readonly record struct EnergyWh(long Value)
{
    public static EnergyWh Zero => new(0);

    public decimal KWh => this.Value / 1000m;

    public static EnergyWh FromKWh(decimal value)
    {
        return new EnergyWh(checked((long)Math.Round(value * 1000m, MidpointRounding.AwayFromZero)));
    }

    public override string ToString()
    {
        return $"{this.KWh:0.00} kWh";
    }
}

internal enum EnergyTier
{
    Copper = 1,
    Steel = 2,
    Gold = 3,
    Iridium = 4
}
