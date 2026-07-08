namespace SVSAPME;

internal static class RecipeCostModes
{
    public const string Normal = "Normal";
    public const string Casual = "Casual";
    public const string Debug = "Debug";

    public static readonly string[] All = { Normal, Casual, Debug };

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Debug, StringComparison.OrdinalIgnoreCase))
            return Debug;

        if (string.Equals(value, Casual, StringComparison.OrdinalIgnoreCase))
            return Casual;

        return Normal;
    }
}

internal sealed class ModConfig
{
    public int EnergyTickInterval { get; set; } = 60;
    public bool EnableCarbonGenerator { get; set; } = true;
    public bool EnableExtendedGeneratorFuels { get; set; }
    public bool EnableSolarNetworkPanel { get; set; } = true;
    public bool EnableLightningCapacitor { get; set; } = true;
    public bool EnableSingleBlockFarm { get; set; } = true;
    public bool EnableBatterySynthesizer { get; set; } = true;
    public bool EnablePoweredTransfer { get; set; } = true;
    public bool EnableElectricMachines { get; set; } = true;
    public bool EnableAutomaticFarmOutputToNetwork { get; set; } = true;
    public bool EnableAutomaticProcessorInputFromNetwork { get; set; } = true;
    public bool EnableAutomaticProcessorOutputToNetwork { get; set; } = true;
    public bool AllowBatteryDischarge { get; set; }
    public double BatteryDischargeEfficiency { get; set; } = 0.8;
    public double GeneratorMultiplier { get; set; } = 1.0;
    public double MachineEnergyCostMultiplier { get; set; } = 1.0;
    public double FarmEnergyCostMultiplier { get; set; } = 1.0;
    public double BatterySynthesisEnergyMultiplier { get; set; } = 1.0;
    public bool DetailedEnergyLogs { get; set; }
    public bool DebugEnergyRouting { get; set; }
    public string? RecipeCostMode { get; set; }

    public string GetRecipeCostMode()
    {
        return RecipeCostModes.Normalize(this.RecipeCostMode);
    }

    public bool IsDebugRecipeCostMode()
    {
        return this.GetRecipeCostMode() == RecipeCostModes.Debug;
    }

    public void NormalizeRecipeCostMode()
    {
        this.RecipeCostMode = this.GetRecipeCostMode();
    }
}
