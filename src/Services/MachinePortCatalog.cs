using SVSAPME.Content;

namespace SVSAPME.Services;

internal static class MachinePortCatalog
{
    public static IReadOnlyList<MachinePortDefinition> GetPorts(string qualifiedItemId)
    {
        return qualifiedItemId switch
        {
            "(BC)" + ModItemCatalog.CarbonGenerator => new[]
            {
                Port("ui.machine.port.fuelIn", "ui.machine.port.side.any", "ui.machine.port.desc.carbonFuel"),
                Port("ui.machine.port.energyOut", "ui.machine.port.side.network", "ui.machine.port.desc.energyOutput")
            },
            "(BC)" + ModItemCatalog.SolarNetworkPanel or "(BC)" + ModItemCatalog.LightningCapacitor => new[]
            {
                Port("ui.machine.port.energyOut", "ui.machine.port.side.network", "ui.machine.port.desc.dailyGenerator")
            },
            "(BC)" + ModItemCatalog.CopperEnergyCell
                or "(BC)" + ModItemCatalog.SteelEnergyCell
                or "(BC)" + ModItemCatalog.GoldEnergyCell
                or "(BC)" + ModItemCatalog.IridiumEnergyCell => new[]
            {
                Port("ui.machine.port.energyIn", "ui.machine.port.side.network", "ui.machine.port.desc.energyCellInput"),
                Port("ui.machine.port.energyOut", "ui.machine.port.side.network", "ui.machine.port.desc.energyCellOutput")
            },
            "(BC)" + ModItemCatalog.BatterySynthesizer => new[]
            {
                Port("ui.machine.port.energyIn", "ui.machine.port.side.network", "ui.machine.port.desc.energyInput"),
                Port("ui.machine.port.itemIn", "ui.machine.port.side.network", "ui.machine.port.desc.synthInput"),
                Port("ui.machine.port.itemOut", "ui.machine.port.side.network", "ui.machine.port.desc.itemOutput")
            },
            "(BC)" + ModItemCatalog.BatteryDischarger => new[]
            {
                Port("ui.machine.port.itemIn", "ui.machine.port.side.network", "ui.machine.port.desc.batteryInput"),
                Port("ui.machine.port.energyOut", "ui.machine.port.side.network", "ui.machine.port.desc.energyOutput")
            },
            "(BC)" + ModItemCatalog.ElectricFurnace or "(BC)" + ModItemCatalog.ElectricGeodeCrusher => new[]
            {
                Port("ui.machine.port.energyIn", "ui.machine.port.side.network", "ui.machine.port.desc.energyInput"),
                Port("ui.machine.port.itemIn", "ui.machine.port.side.any", "ui.machine.port.desc.manualOrNetworkInput"),
                Port("ui.machine.port.itemOut", "ui.machine.port.side.any", "ui.machine.port.desc.itemOutput")
            },
            "(BC)" + ModItemCatalog.PoweredImporterCopper
                or "(BC)" + ModItemCatalog.PoweredImporterSteel
                or "(BC)" + ModItemCatalog.PoweredImporterGold
                or "(BC)" + ModItemCatalog.PoweredImporterIridium => new[]
            {
                Port("ui.machine.port.itemIn", "ui.machine.port.side.configured", "ui.machine.port.desc.poweredImporter"),
                Port("ui.machine.port.energyIn", "ui.machine.port.side.network", "ui.machine.port.desc.energyInput")
            },
            "(BC)" + ModItemCatalog.PoweredExporterCopper
                or "(BC)" + ModItemCatalog.PoweredExporterSteel
                or "(BC)" + ModItemCatalog.PoweredExporterGold
                or "(BC)" + ModItemCatalog.PoweredExporterIridium => new[]
            {
                Port("ui.machine.port.itemOut", "ui.machine.port.side.configured", "ui.machine.port.desc.poweredExporter"),
                Port("ui.machine.port.energyIn", "ui.machine.port.side.network", "ui.machine.port.desc.energyInput")
            },
            "(BC)" + ModItemCatalog.PoweredMachineInterfaceCopper
                or "(BC)" + ModItemCatalog.PoweredMachineInterfaceSteel
                or "(BC)" + ModItemCatalog.PoweredMachineInterfaceGold
                or "(BC)" + ModItemCatalog.PoweredMachineInterfaceIridium => new[]
            {
                Port("ui.machine.port.itemIn", "ui.machine.port.side.area", "ui.machine.port.desc.poweredInterfaceInput"),
                Port("ui.machine.port.itemOut", "ui.machine.port.side.area", "ui.machine.port.desc.poweredInterfaceOutput"),
                Port("ui.machine.port.energyIn", "ui.machine.port.side.network", "ui.machine.port.desc.energyInput")
            },
            "(BC)" + ModItemCatalog.CopperFarm
                or "(BC)" + ModItemCatalog.SteelFarm
                or "(BC)" + ModItemCatalog.GoldFarm
                or "(BC)" + ModItemCatalog.IridiumFarm => new[]
            {
                Port("ui.machine.port.itemIn", "ui.machine.port.side.leftNetwork", "ui.machine.port.desc.farmInput"),
                Port("ui.machine.port.itemOut", "ui.machine.port.side.rightNetwork", "ui.machine.port.desc.farmOutput"),
                Port("ui.machine.port.energyIn", "ui.machine.port.side.network", "ui.machine.port.desc.energyInput")
            },
            "(BC)" + ModItemCatalog.CopperKeg
                or "(BC)" + ModItemCatalog.SteelKeg
                or "(BC)" + ModItemCatalog.GoldKeg
                or "(BC)" + ModItemCatalog.IridiumKeg
                or "(BC)" + ModItemCatalog.CopperCask
                or "(BC)" + ModItemCatalog.SteelCask
                or "(BC)" + ModItemCatalog.GoldCask
                or "(BC)" + ModItemCatalog.IridiumCask => new[]
            {
                Port("ui.machine.port.itemIn", "ui.machine.port.side.leftNetwork", "ui.machine.port.desc.processorInput"),
                Port("ui.machine.port.itemOut", "ui.machine.port.side.rightNetwork", "ui.machine.port.desc.processorOutput"),
                Port("ui.machine.port.energyIn", "ui.machine.port.side.network", "ui.machine.port.desc.energyInput")
            },
            "(BC)" + ModItemCatalog.EnergyMonitorTerminal => new[]
            {
                Port("ui.machine.port.networkLink", "ui.machine.port.side.network", "ui.machine.port.desc.monitor")
            },
            _ => Array.Empty<MachinePortDefinition>()
        };
    }

    public static bool HasRequiredPorts(string qualifiedItemId)
    {
        var ports = GetPorts(qualifiedItemId);
        if (qualifiedItemId == "(BC)" + ModItemCatalog.CarbonGenerator)
        {
            return ports.Any(port => port.RoleKey == "ui.machine.port.fuelIn")
                && ports.Any(port => port.RoleKey == "ui.machine.port.energyOut");
        }

        return ports.Count > 0;
    }

    private static MachinePortDefinition Port(string roleKey, string sideKey, string descriptionKey)
    {
        return new MachinePortDefinition(roleKey, sideKey, descriptionKey);
    }
}

internal readonly record struct MachinePortDefinition(string RoleKey, string SideKey, string DescriptionKey);
