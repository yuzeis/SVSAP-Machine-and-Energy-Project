namespace SVSAPME.Content;

internal static class ModItemCatalog
{
    public const string UniqueId = "Koizumi.SVSAPME";
    public const string Prefix = UniqueId + ".";
    public const string SvsapUniqueId = "Koizumi.SVSAP";
    public const string SvsapPrefix = SvsapUniqueId + ".";

    public const string SvsapBasicCircuit = SvsapPrefix + "BasicCircuit";
    public const string SvsapAdvancedCircuit = SvsapPrefix + "AdvancedCircuit";
    public const string SvsapEliteCircuit = SvsapPrefix + "EliteCircuit";
    public const string SvsapNetworkCable = SvsapPrefix + "NetworkCable";
    public const string SvsapLinkTool = SvsapPrefix + "LinkTool";
    public const string SvsapImporter = SvsapPrefix + "Importer";
    public const string SvsapExporter = SvsapPrefix + "Exporter";
    public const string SvsapMachineInterface = SvsapPrefix + "MachineInterface";
    public const string SvsapFilterCard = SvsapPrefix + "FilterCard";
    public const string SvsapSpeedCard = SvsapPrefix + "SpeedCard";
    public const string SvsapCapacityCard = SvsapPrefix + "CapacityCard";
    public const string SvsapQualityCard = SvsapPrefix + "QualityCard";

    public const string CarbonRod = Prefix + "CarbonRod";
    public const string CopperCoil = Prefix + "CopperCoil";
    public const string EnergyMatrix = Prefix + "EnergyMatrix";
    public const string FarmChamber = Prefix + "FarmChamber";
    public const string BasicGrowthLightModule = Prefix + "BasicGrowthLightModule";
    public const string AdvancedGrowthLightModule = Prefix + "AdvancedGrowthLightModule";
    public const string IridiumGrowthLightModule = Prefix + "IridiumGrowthLightModule";
    public const string BasicThermostatModule = Prefix + "BasicThermostatModule";
    public const string AdvancedThermostatModule = Prefix + "AdvancedThermostatModule";
    public const string IridiumThermostatModule = Prefix + "IridiumThermostatModule";
    public const string BasicSlowReleaseModule = Prefix + "BasicSlowReleaseModule";
    public const string AdvancedSlowReleaseModule = Prefix + "AdvancedSlowReleaseModule";
    public const string IridiumSlowReleaseModule = Prefix + "IridiumSlowReleaseModule";

    public const string CarbonGenerator = Prefix + "CarbonGenerator";
    public const string SolarNetworkPanel = Prefix + "SolarNetworkPanel";
    public const string CopperEnergyCell = Prefix + "CopperEnergyCell";
    public const string SteelEnergyCell = Prefix + "SteelEnergyCell";
    public const string GoldEnergyCell = Prefix + "GoldEnergyCell";
    public const string IridiumEnergyCell = Prefix + "IridiumEnergyCell";
    public const string BatterySynthesizer = Prefix + "BatterySynthesizer";
    public const string CopperFarm = Prefix + "CopperFarm";
    public const string SteelFarm = Prefix + "SteelFarm";
    public const string GoldFarm = Prefix + "GoldFarm";
    public const string IridiumFarm = Prefix + "IridiumFarm";

    public const string PoweredImporterCopper = Prefix + "PoweredImporterCopper";
    public const string PoweredImporterSteel = Prefix + "PoweredImporterSteel";
    public const string PoweredImporterGold = Prefix + "PoweredImporterGold";
    public const string PoweredImporterIridium = Prefix + "PoweredImporterIridium";
    public const string PoweredExporterCopper = Prefix + "PoweredExporterCopper";
    public const string PoweredExporterSteel = Prefix + "PoweredExporterSteel";
    public const string PoweredExporterGold = Prefix + "PoweredExporterGold";
    public const string PoweredExporterIridium = Prefix + "PoweredExporterIridium";
    public const string PoweredMachineInterfaceCopper = Prefix + "PoweredMachineInterfaceCopper";
    public const string PoweredMachineInterfaceSteel = Prefix + "PoweredMachineInterfaceSteel";
    public const string PoweredMachineInterfaceGold = Prefix + "PoweredMachineInterfaceGold";
    public const string PoweredMachineInterfaceIridium = Prefix + "PoweredMachineInterfaceIridium";
    public const string BatteryDischarger = Prefix + "BatteryDischarger";
    public const string EnergyMonitorTerminal = Prefix + "EnergyMonitorTerminal";
    public const string ElectricFurnace = Prefix + "ElectricFurnace";
    public const string ElectricGeodeCrusher = Prefix + "ElectricGeodeCrusher";
    public const string LightningCapacitor = Prefix + "LightningCapacitor";
    public const string ReclaimCrate = Prefix + "ReclaimCrate";

    public static readonly IReadOnlyList<ObjectItemDefinition> ObjectItems =
    new List<ObjectItemDefinition>
    {
        new(CarbonRod, "Carbon Rod", "Carbon rod for SVSAPME energy machines.", 75, 382, -15, new[] { "item_material", "color_black" }),
        new(CopperCoil, "Copper Coil", "Copper winding used by SVSAPME energy machines.", 170, 334, -15, new[] { "item_material", "color_orange" }),
        new(EnergyMatrix, "Energy Matrix", "A compact matrix for routing SVSAPME machine power.", 1380, 787, -15, new[] { "item_material", "color_yellow" }),
        new(FarmChamber, "Farm Chamber", "A sealed chamber module for single-block farms.", 1390, 338, -15, new[] { "item_material", "color_green" }),
        new(BasicGrowthLightModule, "Basic Growth Light Module", "Farm module: +0.01 kWh per occupied plot and 0.95 growth factor.", 720, 93, -15, new[] { "item_material", "color_yellow" }),
        new(AdvancedGrowthLightModule, "Advanced Growth Light Module", "Farm module: +0.03 kWh per occupied plot and 0.90 growth factor.", 2480, 94, -15, new[] { "item_material", "color_yellow" }),
        new(IridiumGrowthLightModule, "Iridium Growth Light Module", "Farm module: +0.08 kWh per occupied plot and 0.80 growth factor.", 8120, 95, -15, new[] { "item_material", "color_purple" }),
        new(BasicThermostatModule, "Basic Thermostat Module", "Farm module: cross-season growth at 1.25 factor and +0.05 kWh per occupied plot.", 980, 93, -15, new[] { "item_material", "color_blue" }),
        new(AdvancedThermostatModule, "Advanced Thermostat Module", "Farm module: cross-season growth at 1.00 factor and +0.08 kWh per occupied plot.", 3160, 94, -15, new[] { "item_material", "color_blue" }),
        new(IridiumThermostatModule, "Iridium Thermostat Module", "Farm module: cross-season growth at 0.90 factor and +0.15 kWh per occupied plot.", 10480, 95, -15, new[] { "item_material", "color_purple" }),
        new(BasicSlowReleaseModule, "Basic Slow-Release Module", "Farm module: one fertilizer covers two plot-cycles.", 660, 93, -15, new[] { "item_material", "color_green" }),
        new(AdvancedSlowReleaseModule, "Advanced Slow-Release Module", "Farm module: one fertilizer covers four plot-cycles.", 2140, 94, -15, new[] { "item_material", "color_green" }),
        new(IridiumSlowReleaseModule, "Iridium Slow-Release Module", "Farm module: one fertilizer covers eight plot-cycles.", 6940, 95, -15, new[] { "item_material", "color_purple" })
    };

    public static readonly IReadOnlyList<BigCraftableDefinition> BigCraftables =
    new List<BigCraftableDefinition>
    {
        new(CarbonGenerator, "Carbon Generator", "Burns coal into network energy.", 1590, 13),
        new(SolarNetworkPanel, "Solar Network Panel", "Produces daily network energy from weather.", 2400, 160),
        new(CopperEnergyCell, "Copper Energy Cell", "Stores 10.00 kWh for an SVSAP network.", 1080, 130),
        new(SteelEnergyCell, "Steel Energy Cell", "Stores 40.00 kWh for an SVSAP network.", 4850, 130),
        new(GoldEnergyCell, "Gold Energy Cell", "Stores 160.00 kWh for an SVSAP network.", 12440, 130),
        new(IridiumEnergyCell, "Iridium Energy Cell", "Stores 640.00 kWh for an SVSAP network.", 40120, 130),
        new(BatterySynthesizer, "Battery Synthesizer", "Assembles Battery Packs from materials and 10.00 kWh.", 4060, 114),
        new(CopperFarm, "Copper Single-Block Farm", "Runs 16 internal crop plots.", 3410, 129),
        new(SteelFarm, "Steel Single-Block Farm", "Runs 64 internal crop plots.", 10560, 129),
        new(GoldFarm, "Gold Single-Block Farm", "Runs 144 internal crop plots.", 24960, 129),
        new(IridiumFarm, "Iridium Single-Block Farm", "Runs 256 internal crop plots.", 68760, 129),
        new(PoweredImporterCopper, "Copper Powered Importer", "Powered upgrade for the SVSAP Importer.", 810, 105),
        new(PoweredImporterSteel, "Steel Powered Importer", "Powered upgrade for the SVSAP Importer.", 3270, 105),
        new(PoweredImporterGold, "Gold Powered Importer", "Powered upgrade for the SVSAP Importer.", 4420, 105),
        new(PoweredImporterIridium, "Iridium Powered Importer", "Powered upgrade for the SVSAP Importer.", 14340, 105),
        new(PoweredExporterCopper, "Copper Powered Exporter", "Powered upgrade for the SVSAP Exporter.", 810, 105),
        new(PoweredExporterSteel, "Steel Powered Exporter", "Powered upgrade for the SVSAP Exporter.", 3270, 105),
        new(PoweredExporterGold, "Gold Powered Exporter", "Powered upgrade for the SVSAP Exporter.", 4420, 105),
        new(PoweredExporterIridium, "Iridium Powered Exporter", "Powered upgrade for the SVSAP Exporter.", 14340, 105),
        new(PoweredMachineInterfaceCopper, "Copper Powered Machine Interface", "Powered upgrade for the SVSAP Machine Interface.", 810, 105),
        new(PoweredMachineInterfaceSteel, "Steel Powered Machine Interface", "Powered upgrade for the SVSAP Machine Interface.", 3270, 105),
        new(PoweredMachineInterfaceGold, "Gold Powered Machine Interface", "Powered upgrade for the SVSAP Machine Interface.", 4420, 105),
        new(PoweredMachineInterfaceIridium, "Iridium Powered Machine Interface", "Powered upgrade for the SVSAP Machine Interface.", 14340, 105),
        new(BatteryDischarger, "Battery Discharger", "Converts Battery Packs back into energy when enabled.", 1900, 114),
        new(EnergyMonitorTerminal, "Energy Monitor Terminal", "Displays network energy totals.", 1250, 129),
        new(ElectricFurnace, "Electric Furnace", "Powered upgrade for the vanilla Furnace.", 1900, 13),
        new(ElectricGeodeCrusher, "Electric Geode Crusher", "Powered upgrade for the vanilla Geode Crusher.", 3510, 182),
        new(LightningCapacitor, "Lightning Capacitor", "Generates energy on storm days.", 0, 150),
        new(ReclaimCrate, "SVSAPME Reclaim Crate", "Holds recovered SVSAPME machines and buffers.", 0, 130)
    };

    public static readonly IReadOnlyDictionary<string, string> CraftingRecipes = new Dictionary<string, string>
    {
        [CarbonRod] = "(O)382 5/Home/(O)" + CarbonRod + " 1/false/null",
        [CopperCoil] = "(O)334 2 (O)338 1/Home/(O)" + CopperCoil + " 1/false/null",
        [EnergyMatrix] = "(O)787 1 (O)" + SvsapBasicCircuit + " 1 (O)338 5 (O)" + CopperCoil + " 1/Home/(O)" + EnergyMatrix + " 1/false/null",
        [FarmChamber] = "(O)335 5 (O)338 5 (O)" + SvsapNetworkCable + " 4 (O)" + SvsapBasicCircuit + " 1/Home/(O)" + FarmChamber + " 1/false/null",
        [BasicGrowthLightModule] = "(O)338 5 (O)" + CopperCoil + " 1 (O)" + SvsapBasicCircuit + " 1/Home/(O)" + BasicGrowthLightModule + " 1/false/null",
        [AdvancedGrowthLightModule] = "(O)" + BasicGrowthLightModule + " 1 (O)335 5 (O)" + SvsapAdvancedCircuit + " 1 (O)787 1/Home/(O)" + AdvancedGrowthLightModule + " 1/false/null",
        [IridiumGrowthLightModule] = "(O)" + AdvancedGrowthLightModule + " 1 (O)337 2 (O)" + SvsapEliteCircuit + " 1 (O)787 2/Home/(O)" + IridiumGrowthLightModule + " 1/false/null",
        [BasicThermostatModule] = "(O)334 5 (O)" + CopperCoil + " 1 (O)" + SvsapBasicCircuit + " 1/Home/(O)" + BasicThermostatModule + " 1/false/null",
        [AdvancedThermostatModule] = "(O)" + BasicThermostatModule + " 1 (O)335 5 (O)" + SvsapAdvancedCircuit + " 1 (O)787 1/Home/(O)" + AdvancedThermostatModule + " 1/false/null",
        [IridiumThermostatModule] = "(O)" + AdvancedThermostatModule + " 1 (O)337 2 (O)" + SvsapEliteCircuit + " 1 (O)787 2/Home/(O)" + IridiumThermostatModule + " 1/false/null",
        [BasicSlowReleaseModule] = "(O)771 20 (O)" + CopperCoil + " 1 (O)" + SvsapBasicCircuit + " 1/Home/(O)" + BasicSlowReleaseModule + " 1/false/null",
        [AdvancedSlowReleaseModule] = "(O)" + BasicSlowReleaseModule + " 1 (O)335 5 (O)" + SvsapAdvancedCircuit + " 1/Home/(O)" + AdvancedSlowReleaseModule + " 1/false/null",
        [IridiumSlowReleaseModule] = "(O)" + AdvancedSlowReleaseModule + " 1 (O)337 2 (O)" + SvsapEliteCircuit + " 1 (O)787 1/Home/(O)" + IridiumSlowReleaseModule + " 1/false/null",

        [CarbonGenerator] = "(O)335 5 (O)334 5 (O)" + CarbonRod + " 2 (O)" + SvsapBasicCircuit + " 1 (O)" + SvsapNetworkCable + " 4/Home/(BC)" + CarbonGenerator + " 1/true/null",
        [SolarNetworkPanel] = "(O)338 10 (O)335 5 (O)" + CopperCoil + " 2 (O)" + SvsapBasicCircuit + " 1 (O)787 1/Home/(BC)" + SolarNetworkPanel + " 1/true/null",
        [CopperEnergyCell] = "(O)334 5 (O)335 2 (O)" + SvsapBasicCircuit + " 1 (O)" + SvsapNetworkCable + " 4/Home/(BC)" + CopperEnergyCell + " 1/true/null",
        [SteelEnergyCell] = "(O)335 5 (BC)" + CopperEnergyCell + " 1 (O)" + SvsapAdvancedCircuit + " 1 (O)787 1/Home/(BC)" + SteelEnergyCell + " 1/true/null",
        [GoldEnergyCell] = "(O)336 5 (BC)" + SteelEnergyCell + " 1 (O)" + SvsapAdvancedCircuit + " 2 (O)787 2/Home/(BC)" + GoldEnergyCell + " 1/true/null",
        [IridiumEnergyCell] = "(O)337 5 (BC)" + GoldEnergyCell + " 1 (O)" + SvsapEliteCircuit + " 2 (O)787 4/Home/(BC)" + IridiumEnergyCell + " 1/true/null",
        [BatterySynthesizer] = "(O)335 5 (O)336 2 (O)787 1 (O)" + SvsapBasicCircuit + " 2 (O)" + SvsapNetworkCable + " 8 (O)" + EnergyMatrix + " 1/Home/(BC)" + BatterySynthesizer + " 1/true/null",
        [CopperFarm] = "(O)" + FarmChamber + " 1 (O)334 10 (O)" + SvsapBasicCircuit + " 2 (O)787 1/Home/(BC)" + CopperFarm + " 1/true/null",
        [SteelFarm] = "(O)" + FarmChamber + " 2 (O)335 10 (BC)" + CopperFarm + " 1 (O)" + SvsapAdvancedCircuit + " 1 (O)787 1/Home/(BC)" + SteelFarm + " 1/true/null",
        [GoldFarm] = "(O)" + FarmChamber + " 4 (O)336 10 (BC)" + SteelFarm + " 1 (O)" + SvsapAdvancedCircuit + " 2 (O)787 2/Home/(BC)" + GoldFarm + " 1/true/null",
        [IridiumFarm] = "(O)" + FarmChamber + " 8 (O)337 10 (BC)" + GoldFarm + " 1 (O)" + SvsapEliteCircuit + " 2 (O)787 4/Home/(BC)" + IridiumFarm + " 1/true/null",

        [PoweredImporterCopper] = "(BC)" + SvsapImporter + " 1 (O)334 3 (O)" + CopperCoil + " 1 (O)" + SvsapBasicCircuit + " 1/Home/(BC)" + PoweredImporterCopper + " 1/true/null",
        [PoweredImporterSteel] = "(BC)" + PoweredImporterCopper + " 1 (O)335 5 (O)" + SvsapAdvancedCircuit + " 1/Home/(BC)" + PoweredImporterSteel + " 1/true/null",
        [PoweredImporterGold] = "(BC)" + PoweredImporterSteel + " 1 (O)336 5 (O)" + SvsapAdvancedCircuit + " 1 (O)787 1/Home/(BC)" + PoweredImporterGold + " 1/true/null",
        [PoweredImporterIridium] = "(BC)" + PoweredImporterGold + " 1 (O)337 3 (O)" + SvsapEliteCircuit + " 1 (O)787 2/Home/(BC)" + PoweredImporterIridium + " 1/true/null",
        [PoweredExporterCopper] = "(BC)" + SvsapExporter + " 1 (O)334 3 (O)" + CopperCoil + " 1 (O)" + SvsapBasicCircuit + " 1/Home/(BC)" + PoweredExporterCopper + " 1/true/null",
        [PoweredExporterSteel] = "(BC)" + PoweredExporterCopper + " 1 (O)335 5 (O)" + SvsapAdvancedCircuit + " 1/Home/(BC)" + PoweredExporterSteel + " 1/true/null",
        [PoweredExporterGold] = "(BC)" + PoweredExporterSteel + " 1 (O)336 5 (O)" + SvsapAdvancedCircuit + " 1 (O)787 1/Home/(BC)" + PoweredExporterGold + " 1/true/null",
        [PoweredExporterIridium] = "(BC)" + PoweredExporterGold + " 1 (O)337 3 (O)" + SvsapEliteCircuit + " 1 (O)787 2/Home/(BC)" + PoweredExporterIridium + " 1/true/null",
        [PoweredMachineInterfaceCopper] = "(BC)" + SvsapMachineInterface + " 1 (O)334 3 (O)" + CopperCoil + " 1 (O)" + SvsapBasicCircuit + " 1/Home/(BC)" + PoweredMachineInterfaceCopper + " 1/true/null",
        [PoweredMachineInterfaceSteel] = "(BC)" + PoweredMachineInterfaceCopper + " 1 (O)335 5 (O)" + SvsapAdvancedCircuit + " 1/Home/(BC)" + PoweredMachineInterfaceSteel + " 1/true/null",
        [PoweredMachineInterfaceGold] = "(BC)" + PoweredMachineInterfaceSteel + " 1 (O)336 5 (O)" + SvsapAdvancedCircuit + " 1 (O)787 1/Home/(BC)" + PoweredMachineInterfaceGold + " 1/true/null",
        [PoweredMachineInterfaceIridium] = "(BC)" + PoweredMachineInterfaceGold + " 1 (O)337 3 (O)" + SvsapEliteCircuit + " 1 (O)787 2/Home/(BC)" + PoweredMachineInterfaceIridium + " 1/true/null",
        [BatteryDischarger] = "(O)335 5 (O)" + CopperCoil + " 2 (O)" + SvsapBasicCircuit + " 1 (O)787 1/Home/(BC)" + BatteryDischarger + " 1/true/null",
        [EnergyMonitorTerminal] = "(O)338 5 (O)" + SvsapBasicCircuit + " 2 (O)" + SvsapNetworkCable + " 4/Home/(BC)" + EnergyMonitorTerminal + " 1/true/null",
        [ElectricFurnace] = "(BC)13 1 (O)335 5 (O)" + CopperCoil + " 2 (O)" + SvsapBasicCircuit + " 1 (O)787 1/Home/(BC)" + ElectricFurnace + " 1/true/null",
        [ElectricGeodeCrusher] = "(BC)182 1 (O)336 2 (O)" + SvsapAdvancedCircuit + " 1 (O)" + CopperCoil + " 2/Home/(BC)" + ElectricGeodeCrusher + " 1/true/null"
    };

    public static readonly IReadOnlyDictionary<string, string> CraftingRecipeSkillRequirements = new Dictionary<string, string>
    {
        [CarbonRod] = "Mining 1",
        [CopperCoil] = "Mining 1",
        [EnergyMatrix] = "Mining 1",
        [FarmChamber] = "Farming 2",
        [BasicGrowthLightModule] = "Farming 2",
        [AdvancedGrowthLightModule] = "Farming 5",
        [IridiumGrowthLightModule] = "Farming 8",
        [BasicThermostatModule] = "Farming 2",
        [AdvancedThermostatModule] = "Farming 5",
        [IridiumThermostatModule] = "Farming 8",
        [BasicSlowReleaseModule] = "Farming 2",
        [AdvancedSlowReleaseModule] = "Farming 5",
        [IridiumSlowReleaseModule] = "Farming 8",
        [CarbonGenerator] = "Mining 1",
        [SolarNetworkPanel] = "Mining 1",
        [CopperEnergyCell] = "Mining 1",
        [SteelEnergyCell] = "Mining 3",
        [GoldEnergyCell] = "Mining 5",
        [IridiumEnergyCell] = "Mining 8",
        [BatterySynthesizer] = "Mining 5",
        [CopperFarm] = "Farming 2",
        [SteelFarm] = "Farming 5",
        [GoldFarm] = "Farming 8",
        [IridiumFarm] = "Farming 10",
        [PoweredImporterCopper] = "Mining 3",
        [PoweredImporterSteel] = "Mining 4",
        [PoweredImporterGold] = "Mining 6",
        [PoweredImporterIridium] = "Mining 8",
        [PoweredExporterCopper] = "Mining 3",
        [PoweredExporterSteel] = "Mining 4",
        [PoweredExporterGold] = "Mining 6",
        [PoweredExporterIridium] = "Mining 8",
        [PoweredMachineInterfaceCopper] = "Mining 3",
        [PoweredMachineInterfaceSteel] = "Mining 4",
        [PoweredMachineInterfaceGold] = "Mining 6",
        [PoweredMachineInterfaceIridium] = "Mining 8",
        [BatteryDischarger] = "Mining 5",
        [EnergyMonitorTerminal] = "Mining 3",
        [ElectricFurnace] = "Mining 6",
        [ElectricGeodeCrusher] = "Mining 8"
    };

    public static IEnumerable<string> ObjectItemIds => ObjectItems.Select(item => item.Id);

    public static IEnumerable<string> BigCraftableIds => BigCraftables.Select(item => item.Id);

    public static bool IsSvsapmeBigCraftable(string qualifiedItemId)
    {
        return BigCraftables.Any(item => qualifiedItemId == "(BC)" + item.Id);
    }

    public static string GetLocalKey(string itemId)
    {
        return itemId.StartsWith(Prefix, StringComparison.Ordinal)
            ? itemId[Prefix.Length..]
            : itemId;
    }
}

internal sealed record ObjectItemDefinition(
    string Id,
    string DisplayName,
    string Description,
    int Price,
    int SpriteIndex,
    int Category,
    IReadOnlyList<string> ContextTags);

internal sealed record BigCraftableDefinition(
    string Id,
    string DisplayName,
    string Description,
    int Price,
    int SpriteIndex);
