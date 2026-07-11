#if DEBUG
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using SVSAPME.Content;
using SVSAPME.Models;
using SVSAPME.UI;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

/// <summary>Debug-only visual gate which renders every SVSAPME menu and saves the actual backbuffer.</summary>
internal sealed class GuiScreenshotE2EService
{
    private const string EnabledEnv = "STARDEW_SVSAP_GUI_CAPTURE";
    private const string OutputDirEnv = "STARDEW_SVSAP_GUI_CAPTURE_OUTPUT";
    private const string SvsapCompleteFileName = "svsap-gui-capture-complete.json";
    private const string SvsapmeCompleteFileName = "svsapme-gui-capture-complete.json";
    private const string AllCompleteFileName = "gui-capture-complete.json";
    private const int StartupDelayTicks = 180;
    private const int RenderFramesBeforeCapture = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly MachineRuntimeService runtime;
    private readonly SingleBlockFarmService farmService;
    private readonly SingleBlockProcessorService processorService;
    private readonly string outputDir;
    private readonly List<GuiCaptureResult> results = new();

    private List<GuiCaptureCase>? cases;
    private IClickableMenu? currentMenu;
    private int currentIndex;
    private int worldReadyTicks;
    private int renderedFrames;
    private bool currentCaptured;
    private bool started;
    private bool stopped;

    public GuiScreenshotE2EService(
        IModHelper helper,
        IMonitor monitor,
        MachineStateRepository repository,
        MachineRegistryService registry,
        MachineRuntimeService runtime,
        SingleBlockFarmService farmService,
        SingleBlockProcessorService processorService)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.repository = repository;
        this.registry = registry;
        this.runtime = runtime;
        this.farmService = farmService;
        this.processorService = processorService;
        this.outputDir = Environment.GetEnvironmentVariable(OutputDirEnv) ?? string.Empty;
    }

    private bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnabledEnv), "1", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(this.outputDir);

    public void Start()
    {
        if (!this.IsEnabled || this.started)
            return;

        this.started = true;
        Directory.CreateDirectory(this.GetScreenshotDirectory());
        this.helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        this.monitor.Log($"SVSAPME_GUI_CAPTURE started output=\"{this.outputDir}\"", LogLevel.Info);
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (this.stopped || !Context.IsWorldReady || !Context.IsMainPlayer)
            return;

        this.worldReadyTicks++;
        if (this.worldReadyTicks < StartupDelayTicks
            || !File.Exists(Path.Combine(this.outputDir, SvsapCompleteFileName)))
        {
            return;
        }

        if (this.cases is null)
        {
            try
            {
                this.cases = this.CreateCases();
                this.WriteProgress();
            }
            catch (Exception ex)
            {
                this.results.Add(GuiCaptureResult.Failed("setup", ex));
                this.Finish();
                return;
            }
        }

        if (this.currentMenu is not null && !this.currentCaptured)
        {
            this.renderedFrames++;
            if (this.renderedFrames < RenderFramesBeforeCapture)
                return;

            var captureCase = this.cases[this.currentIndex];
            try
            {
                this.results.Add(this.Capture(captureCase));
            }
            catch (Exception ex)
            {
                this.results.Add(GuiCaptureResult.Failed(captureCase.Name, ex));
            }

            this.currentCaptured = true;
            this.WriteProgress();
            return;
        }

        if (this.currentCaptured)
        {
            this.CloseCurrentMenu();
            this.currentIndex++;
            this.currentCaptured = false;
            this.renderedFrames = 0;
        }

        if (this.currentIndex >= this.cases.Count)
        {
            this.Finish();
            return;
        }

        if (this.currentMenu is not null || Game1.activeClickableMenu is not null)
            return;

        var currentCase = this.cases[this.currentIndex];
        try
        {
            this.currentMenu = currentCase.CreateMenu();
            Game1.activeClickableMenu = this.currentMenu;
            this.renderedFrames = 0;
            this.monitor.Log($"SVSAPME_GUI_CAPTURE showing {currentCase.Name}", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            this.results.Add(GuiCaptureResult.Failed(currentCase.Name, ex));
            this.currentIndex++;
            this.WriteProgress();
        }
    }

    private GuiCaptureResult Capture(GuiCaptureCase captureCase)
    {
        var name = captureCase.Name;
        var graphicsDevice = Game1.graphics.GraphicsDevice;
        var previousTargets = graphicsDevice.GetRenderTargets();
        var width = Math.Max(1, Game1.uiViewport.Width);
        var height = Math.Max(1, Game1.uiViewport.Height);
        var pixels = new Color[checked(width * height)];
        using var target = new RenderTarget2D(
            graphicsDevice,
            width,
            height,
            false,
            SurfaceFormat.Color,
            DepthFormat.None,
            0,
            RenderTargetUsage.PreserveContents);
        using var spriteBatch = new SpriteBatch(graphicsDevice);
        graphicsDevice.SetRenderTarget(target);
        graphicsDevice.Clear(Color.Black);
        SvsapmeUiText.ResetSlotGeometryDiagnostics();
        var batchBegun = false;
        try
        {
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            batchBegun = true;
            this.currentMenu!.draw(spriteBatch);
            spriteBatch.End();
            batchBegun = false;
        }
        finally
        {
            if (batchBegun)
            {
                try
                {
                    spriteBatch.End();
                }
                catch
                {
                    // Preserve the original render exception while restoring the graphics state.
                }
            }

            graphicsDevice.SetRenderTargets(previousTargets);
        }
        target.GetData(pixels);

        var sampledColors = new HashSet<uint>();
        var stride = Math.Max(1, pixels.Length / 8192);
        for (var i = 0; i < pixels.Length; i += stride)
            sampledColors.Add(pixels[i].PackedValue);

        var path = Path.Combine(this.GetScreenshotDirectory(), name + ".png");
        using (var texture = new Texture2D(graphicsDevice, width, height, false, SurfaceFormat.Color))
        {
            texture.SetData(pixels);
            using var stream = File.Create(path);
            texture.SaveAsPng(stream, width, height);
        }

        var info = new FileInfo(path);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var panelCoveragePermille = CalculatePanelCoveragePermille(pixels, width, height, this.currentMenu!);
        var blackPixelPermille = CalculatePanelBlackPixelPermille(pixels, width, height, this.currentMenu!);
        var slotGeometryErrors = SvsapmeUiText.GetSlotGeometryViolations();
        var qualityOverlayCount = SvsapmeUiText.GetQualityOverlayDrawCount();
        var pass = info.Length > 1024
            && sampledColors.Count >= 8
            && panelCoveragePermille >= 550
            && blackPixelPermille <= 50
            && slotGeometryErrors.Count == 0
            && qualityOverlayCount >= captureCase.MinimumQualityOverlays;
        this.monitor.Log(
            $"SVSAPME_GUI_CAPTURE {(pass ? "PASS" : "FAIL")} {name} {width}x{height} bytes={info.Length} sampledColors={sampledColors.Count} panelCoverage={panelCoveragePermille}/1000 blackPixels={blackPixelPermille}/1000 slotGeometryErrors={slotGeometryErrors.Count} qualityOverlays={qualityOverlayCount}/{captureCase.MinimumQualityOverlays}",
            pass ? LogLevel.Info : LogLevel.Error);
        return new GuiCaptureResult(
            name,
            pass,
            path,
            width,
            height,
            info.Length,
            sampledColors.Count,
            panelCoveragePermille,
            blackPixelPermille,
            slotGeometryErrors.Count,
            slotGeometryErrors,
            qualityOverlayCount,
            captureCase.MinimumQualityOverlays,
            hash,
            pass ? string.Empty : "Screenshot was blank, missing its menu panel, contained black render holes, was too small, omitted required quality stars, or drew item/count/quality content outside its slot.");
    }

    private List<GuiCaptureCase> CreateCases()
    {
        var location = Game1.currentLocation;
        var farm = this.CreateMachine(ModItemCatalog.IridiumFarm, location);
        var keg = this.CreateMachine(ModItemCatalog.IridiumKeg, location);
        var cask = this.CreateMachine(ModItemCatalog.IridiumCask, location);
        var importer = this.CreateMachine(ModItemCatalog.PoweredImporterIridium, location);
        var exporter = this.CreateMachine(ModItemCatalog.PoweredExporterIridium, location);
        var machineInterface = this.CreateMachine(ModItemCatalog.PoweredMachineInterfaceIridium, location);
        this.PopulateFarmState(farm.Object);
        this.PopulateProcessorState(keg.Object, location, keg.Tile, isCask: false);
        this.PopulateProcessorState(cask.Object, location, cask.Tile, isCask: true);

        return new List<GuiCaptureCase>
        {
            new("01-svsapme-status", () => new SvsapmeStatusMenu("SVSAPME Machine Diagnostics", CreateStatusLines, CreateStatusActions())),
            new("02-energy-monitor-local", () => new EnergyMonitorMenu(CreateEnergyMonitorView)),
            new("03-single-block-farm-local", () => new SingleBlockFarmMenu(farm.Object, location, farm.Tile, this.farmService)),
            new("04-single-block-keg-local", () => new SingleBlockProcessorMenu(keg.Object, location, keg.Tile, this.processorService)),
            new("05-single-block-cask-local", () => new SingleBlockProcessorMenu(cask.Object, location, cask.Tile, this.processorService), MinimumQualityOverlays: 3),
            new("06-powered-importer-local", () => new PoweredTransferMenu(importer.Object, location, importer.Tile, this.runtime)),
            new("07-powered-exporter-local", () => new PoweredTransferMenu(exporter.Object, location, exporter.Tile, this.runtime)),
            new("08-powered-interface-local", () => new PoweredTransferMenu(machineInterface.Object, location, machineInterface.Tile, this.runtime)),
            new("09-machine-control-remote-generic", () => CreateRemoteMenu(CreateRemoteGenericSnapshot())),
            new("10-machine-control-remote-farm", () => CreateRemoteMenu(CreateRemoteFarmSnapshot())),
            new("11-machine-control-remote-processor", () => CreateRemoteMenu(CreateRemoteProcessorSnapshot())),
            new("12-machine-control-remote-powered-transfer", () => CreateRemoteMenu(CreateRemotePoweredSnapshot())),
            new("13-machine-control-remote-energy-monitor", () => CreateRemoteMenu(CreateRemoteEnergySnapshot())),
            new("14-farm-clear-confirmation-local", () =>
            {
                var parent = new SingleBlockFarmMenu(farm.Object, location, farm.Tile, this.farmService);
                return new SvsapmeConfirmationMenu(
                    parent,
                    ModText.Get("ui.farm.clearAll.confirmTitle", "Confirm Clear All"),
                    new[]
                    {
                        ModText.Get("ui.farm.clearAll.confirmBody", "Clear all {{count}} planted farm plots?", new { count = "6" }),
                        ModText.Get("ui.farm.destructive.noRefund", "The crop, fertilizer, progress, and plot lock will not be returned."),
                        ModText.Get("ui.farm.clearAll.lockCount", "Plot locks to clear: {{count}}.", new { count = "2" }),
                        ModText.Get("ui.farm.destructive.autoPullWarning", "Automatic input is enabled, so empty plots may be planted again during the next farm cycle.")
                    },
                    () => { });
            })
        };
    }

    private (SObject Object, Vector2 Tile) CreateMachine(string itemId, GameLocation location)
    {
        var machine = (SObject)ItemRegistry.Create("(BC)" + itemId);
        var tile = this.FindFixtureTile(location);
        location.objects[tile] = machine;
        if (!this.registry.TryRegisterPlacedMachine(machine, location, tile))
            throw new InvalidOperationException($"Could not register GUI fixture machine {itemId}.");
        return (machine, tile);
    }

    private void PopulateFarmState(SObject machine)
    {
        var state = this.GetMachineState(machine);
        state.Farm.AutoPullFromNetwork = true;
        state.Farm.AutoPushOutputToNetwork = true;
        state.Farm.InputMode = MachineInputModes.Filter;
        state.Farm.FilterMode = MachineFilterModes.Whitelist;
        state.Farm.SeedFilterQualifiedItemIds = new List<string> { "(O)472", "(O)481", "(O)499" };
        state.Farm.InputBuffer = new List<BufferedItemStack>
        {
            Stack("(O)472", 64),
            Stack("(O)481", 128),
            Stack("(O)499", 32)
        };
        state.Farm.BoundFertilizerQualifiedItemId = "(O)368";
        state.Farm.InternalFertilizerCount = 96;
        state.Farm.InstalledModuleQualifiedItemIds = new List<string>
        {
            "(O)" + ModItemCatalog.IridiumGrowthLightModule,
            "(O)" + ModItemCatalog.IridiumThermostatModule,
            "(O)" + ModItemCatalog.IridiumSlowReleaseModule
        };
        state.Farm.Plots = new List<FarmPlotState>
        {
            Plot(0, "(O)472", "(O)24", 2_000, false, string.Empty),
            Plot(1, "(O)481", "(O)258", 4_500, false, string.Empty),
            Plot(2, "(O)499", "(O)454", 8_000, true, "(O)499"),
            Plot(3, "(O)472", "(O)24", 1_000_000, false, string.Empty),
            Plot(4, "(O)481", "(O)258", 3_200, true, "(O)481"),
            Plot(5, "(O)499", "(O)454", 12_000, false, string.Empty)
        };
        state.OutputBuffer = new List<BufferedItemStack>
        {
            Stack("(O)24", 42),
            Stack("(O)258", 18),
            Stack("(O)454", 9)
        };
    }

    private void PopulateProcessorState(SObject machine, GameLocation location, Vector2 tile, bool isCask)
    {
        this.processorService.GetSlotViews(machine, location, tile);
        var state = this.GetMachineState(machine);
        state.Processor.AutoPullFromNetwork = true;
        state.Processor.AutoPushOutputToNetwork = true;
        state.Processor.InputMode = MachineInputModes.Filter;
        state.Processor.FilterMode = MachineFilterModes.Whitelist;
        state.Processor.FilterQualifiedItemIds = new List<string> { "(O)454", "(O)268", "(O)348" };
        state.Processor.InstalledUpgradeQualifiedItemIds = isCask
            ? new List<string>
            {
                "(O)" + ModItemCatalog.SvsapSpeedCard,
                "(O)" + ModItemCatalog.SvsapCapacityCard
            }
            : new List<string>
            {
                "(O)" + ModItemCatalog.SvsapSpeedCard,
                "(O)" + ModItemCatalog.SvsapCapacityCard,
                "(O)" + ModItemCatalog.SvsapQualityCard
            };
        state.Processor.InputBuffer = new List<BufferedItemStack>
        {
            Stack(isCask ? "(O)348" : "(O)454", 96, isCask ? 1 : 0)
        };
        state.Processor.OutputBuffer = new List<BufferedItemStack>
        {
            Stack(isCask ? "(O)348" : "(O)344", 12, isCask ? 2 : 0)
        };

        var slots = state.Processor.Slots;
        if (isCask)
        {
            slots[0] = new SingleBlockProcessorSlotState
            {
                Input = Stack("(O)348", 1, 1),
                Output = Stack("(O)348", 1, 2),
                InputCount = 1,
                RemainingDays = 7,
                TotalDays = 14,
                TargetQuality = 4
            };
            slots[1] = new SingleBlockProcessorSlotState
            {
                Input = Stack("(O)348", 1, 2),
                Output = Stack("(O)348", 1, 4),
                InputCount = 1,
                RemainingDays = 0,
                TotalDays = 14,
                TargetQuality = 4
            };
        }
        else
        {
            slots[0] = new SingleBlockProcessorSlotState
            {
                Input = Stack("(O)454", 1),
                Output = FlavoredWine("(O)454", 1),
                InputCount = 1,
                RemainingMinutes = 800,
                TotalMinutes = 1_200
            };
            slots[1] = new SingleBlockProcessorSlotState
            {
                Input = Stack("(O)268", 1),
                Output = Stack("(O)344", 1),
                InputCount = 1,
                RemainingMinutes = 0,
                TotalMinutes = 1_200
            };
            slots[2] = new SingleBlockProcessorSlotState
            {
                Input = Stack("(O)433", 5),
                Output = Stack("(O)395", 1),
                InputCount = 5,
                RemainingMinutes = 500,
                TotalMinutes = 1_200
            };
        }
    }

    private MachineState GetMachineState(SObject machine)
    {
        if (!Guid.TryParse(machine.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey), out var machineGuid))
            throw new InvalidOperationException($"GUI fixture {machine.QualifiedItemId} has no machine GUID.");
        return this.repository.GetOrCreate(machineGuid);
    }

    private static FarmPlotState Plot(int index, string seed, string harvest, long progress, bool locked, string lockedSeed)
    {
        return new FarmPlotState
        {
            PlotIndex = index,
            SeedQualifiedItemId = seed,
            HarvestQualifiedItemId = harvest,
            ProgressUnits = progress,
            FertilizerQualifiedItemId = "(O)368",
            IsLocked = locked,
            LockedSeedQualifiedItemId = lockedSeed
        };
    }

    private static BufferedItemStack FlavoredWine(string parentQualifiedItemId, int stack)
    {
        return new BufferedItemStack
        {
            QualifiedItemId = "(O)348",
            Stack = stack,
            PreservedParentSheetIndex = parentQualifiedItemId,
            PreserveType = 2,
            Price = 4_620,
            Name = "Wine"
        };
    }

    private static BufferedItemStack Stack(string qualifiedItemId, int stack, int quality = 0)
    {
        return new BufferedItemStack
        {
            QualifiedItemId = qualifiedItemId,
            Stack = stack,
            Quality = quality
        };
    }

    private static IReadOnlyList<string> CreateStatusLines()
    {
        return new[]
        {
            "Machine network: online",
            "Stored energy: 450.25 / 640.00 kWh",
            "Last tick: +1.50 kWh / -0.85 kWh",
            "Today: +18.00 kWh / -12.35 kWh",
            "Active machines: 14",
            "Warning: output buffer at 82%"
        };
    }

    private static IEnumerable<SvsapmeMenuAction> CreateStatusActions()
    {
        return new[]
        {
            new SvsapmeMenuAction("Refresh", () => "Refreshed"),
            new SvsapmeMenuAction("Energy Report", () => "Report ready"),
            new SvsapmeMenuAction("Claim", () => "No reclaim pending", () => false)
        };
    }

    private static EnergyMonitorView CreateEnergyMonitorView()
    {
        return new EnergyMonitorView(
            true,
            "Online",
            450_250,
            640_000,
            1_500,
            850,
            18_000,
            12_350,
            "Output buffer at 82%",
            new[]
            {
                new EnergyMonitorDeviceView("solar", "Iridium Solar Panel", 15_000, new[] { "Farm (12, 8)", "Sunny" }),
                new EnergyMonitorDeviceView("coal", "Carbon Generator", 3_000, new[] { "Coal remaining: 24" })
            },
            new[]
            {
                new EnergyMonitorDeviceView("farm", "Iridium Single Block Farm", 8_000, new[] { "256 plots", "3 modules" }),
                new EnergyMonitorDeviceView("keg", "Iridium Single Block Keg", 4_350, new[] { "192 active slots" })
            });
    }

    private static RemoteMachineControlMenu CreateRemoteMenu(SvsapmeMachineSnapshotResponse snapshot)
    {
        return new RemoteMachineControlMenu(snapshot, _ => true, (_, _, _) => true, _ => false);
    }

    private static SvsapmeMachineSnapshotResponse CreateRemoteGenericSnapshot()
    {
        var snapshot = BaseRemoteSnapshot(SvsapmeMachineMenuKind.Generic, "Remote Carbon Generator");
        snapshot.StoredWh = 2_450;
        snapshot.CapacityWh = 10_000;
        snapshot.ProgressWh = 350;
        snapshot.Lines = new List<string>
        {
            "Status: processing",
            "Fuel input: Coal x24",
            "Energy output: 350 Wh / action",
            "Facing: east",
            "Network: online"
        };
        return snapshot;
    }

    private static SvsapmeMachineSnapshotResponse CreateRemoteFarmSnapshot()
    {
        var snapshot = BaseRemoteSnapshot(SvsapmeMachineMenuKind.Farm, "Remote Iridium Single Block Farm");
        snapshot.QualifiedItemId = "(BC)" + ModItemCatalog.IridiumFarm;
        snapshot.Farm = new SvsapmeFarmMenuSnapshot
        {
            PlotCapacity = 256,
            OccupiedPlots = 30,
            LockedPlots = 8,
            Offset = 0,
            AutoPullFromNetwork = true,
            AutoPushOutputToNetwork = true,
            AutoHarvest = true,
            InputMode = MachineInputModes.Filter,
            FilterMode = MachineFilterModes.Whitelist,
            SeedFilterQualifiedItemIds = new List<string> { "(O)472", "(O)481", "(O)499" },
            InputBuffer = new List<BufferedItemStack> { Stack("(O)499", 64) },
            FertilizerQualifiedItemId = "(O)368",
            FertilizerCount = 96,
            InstalledModuleQualifiedItemIds = new List<string>
            {
                "(O)" + ModItemCatalog.IridiumGrowthLightModule,
                "(O)" + ModItemCatalog.IridiumThermostatModule,
                "(O)" + ModItemCatalog.IridiumSlowReleaseModule
            },
            ModuleSlotCapacity = 5,
            OutputBuffer = new List<BufferedItemStack> { Stack("(O)454", 18) },
            EstimatedDailyValue = 125_400m,
            EstimatedDailyEnergyWh = 18_500,
            Plots = Enumerable.Range(0, 30).Select(index => new SvsapmeFarmPlotSnapshot
            {
                PlotIndex = index,
                SeedQualifiedItemId = (index % 3) switch { 0 => "(O)472", 1 => "(O)481", _ => "(O)499" },
                HarvestQualifiedItemId = (index % 3) switch { 0 => "(O)24", 1 => "(O)258", _ => "(O)454" },
                FertilizerQualifiedItemId = "(O)368",
                LockedSeedQualifiedItemId = index % 7 == 0 ? "(O)499" : string.Empty,
                ProgressUnits = index % 5 == 0 ? 10_000 : 2_500 + index * 100,
                RequiredUnits = 10_000,
                Ready = index % 5 == 0,
                IsLocked = index % 7 == 0
            }).ToList()
        };
        return snapshot;
    }

    private static SvsapmeMachineSnapshotResponse CreateRemoteProcessorSnapshot()
    {
        var snapshot = BaseRemoteSnapshot(SvsapmeMachineMenuKind.Processor, "Remote Iridium Single Block Keg");
        snapshot.QualifiedItemId = "(BC)" + ModItemCatalog.IridiumKeg;
        snapshot.CanCollectProcessorOutput = true;
        snapshot.ProcessorReadyStacks = 6;
        snapshot.Processor = new SvsapmeProcessorMenuSnapshot
        {
            SlotCapacity = 256,
            Offset = 0,
            NetworkOnline = true,
            EnergyOnline = true,
            StoredWh = 450_250,
            CapacityWh = 640_000,
            RequiredWhForNextStep = 120,
            AutoPullFromNetwork = true,
            AutoPushOutputToNetwork = true,
            InputMode = MachineInputModes.Filter,
            FilterMode = MachineFilterModes.Whitelist,
            FilterQualifiedItemIds = new List<string> { "(O)454", "(O)268", "(O)433" },
            InputBuffer = new List<BufferedItemStack> { Stack("(O)454", 96) },
            OutputBuffer = new List<BufferedItemStack> { FlavoredWine("(O)454", 12) },
            InstalledUpgradeQualifiedItemIds = new List<string>
            {
                "(O)" + ModItemCatalog.SvsapSpeedCard,
                "(O)" + ModItemCatalog.SvsapCapacityCard,
                "(O)" + ModItemCatalog.SvsapQualityCard
            },
            UpgradeSlotCapacity = 5,
            SpeedPermille = 1100,
            OutputBufferCapacityItems = 256,
            EstimatedDailyValue = 184_800m,
            Slots = Enumerable.Range(0, 30).Select(index => new SvsapmeProcessorSlotSnapshot
            {
                SlotIndex = index,
                Input = Stack((index % 3) switch { 0 => "(O)454", 1 => "(O)268", _ => "(O)433" }, index % 3 == 2 ? 5 : 1),
                Output = (index % 3) switch
                {
                    0 => FlavoredWine("(O)454", 1),
                    1 => Stack("(O)344", 1),
                    _ => Stack("(O)395", 1)
                },
                Ready = index % 6 == 0,
                CanEject = index % 6 == 0,
                CanCollect = index % 6 == 0,
                Remaining = index % 6 == 0 ? 0 : 800 - index * 10,
                Total = 1_200
            }).ToList()
        };
        return snapshot;
    }

    private static SvsapmeMachineSnapshotResponse CreateRemotePoweredSnapshot()
    {
        var snapshot = BaseRemoteSnapshot(SvsapmeMachineMenuKind.PoweredTransfer, "Remote Iridium Powered Exporter");
        snapshot.QualifiedItemId = "(BC)" + ModItemCatalog.PoweredExporterIridium;
        snapshot.PoweredTransfer = new SvsapmePoweredTransferMenuSnapshot
        {
            IsBlacklist = false,
            OreDictionaryEnabled = true,
            QualityStrategy = "LowQualityFirst",
            FacingDirection = 1,
            UpgradeSlotCapacity = 5,
            InstalledUpgradeQualifiedItemIds = new List<string>
            {
                "(O)" + ModItemCatalog.SvsapSpeedCard,
                "(O)" + ModItemCatalog.SvsapCapacityCard,
                "(O)" + ModItemCatalog.SvsapOreDictionaryCard,
                "(O)" + ModItemCatalog.SvsapQualityCard
            },
            FilterSlots = Enumerable.Range(0, 9).Select(index => new SvsapmeFilterSlotSnapshot
            {
                SlotIndex = index,
                QualifiedItemId = index switch { 0 => "(O)378", 1 => "(O)380", 2 => "(O)384", _ => string.Empty },
                DisplayName = index switch { 0 => "Copper Ore", 1 => "Iron Ore", 2 => "Gold Ore", _ => string.Empty },
                OreGroups = index < 3 ? new List<string> { "ore_item" } : new List<string>()
            }).ToList(),
            Throughput = 1_024,
            TransferIntervalTicks = 15,
            EnergyPerActionWh = 1m,
            NetworkOnline = true,
            StoredWh = 450_250,
            CapacityWh = 640_000
        };
        return snapshot;
    }

    private static SvsapmeMachineSnapshotResponse CreateRemoteEnergySnapshot()
    {
        var snapshot = BaseRemoteSnapshot(SvsapmeMachineMenuKind.EnergyMonitor, "Remote Energy Monitor");
        snapshot.QualifiedItemId = "(BC)" + ModItemCatalog.EnergyMonitorTerminal;
        snapshot.EnergyMonitor = new SvsapmeEnergyMonitorSnapshot
        {
            NetworkId = Guid.NewGuid(),
            Online = true,
            StatusText = "Online",
            StoredWh = 450_250,
            CapacityWh = 640_000,
            LastTickGeneratedWh = 1_500,
            LastTickConsumedWh = 850,
            TodayGeneratedWh = 18_000,
            TodayConsumedWh = 12_350,
            LastWarning = "Output buffer at 82%",
            Producers = new List<SvsapmeEnergyDeviceSnapshot>
            {
                new() { DeviceId = "solar", DisplayName = "Iridium Solar Panel", TotalWh = 15_000, Details = new List<string> { "Farm (12, 8)", "Sunny" } },
                new() { DeviceId = "coal", DisplayName = "Carbon Generator", TotalWh = 3_000, Details = new List<string> { "Coal remaining: 24" } }
            },
            Consumers = new List<SvsapmeEnergyDeviceSnapshot>
            {
                new() { DeviceId = "farm", DisplayName = "Iridium Single Block Farm", TotalWh = 8_000, Details = new List<string> { "256 plots", "3 modules" } },
                new() { DeviceId = "keg", DisplayName = "Iridium Single Block Keg", TotalWh = 4_350, Details = new List<string> { "192 active slots" } }
            }
        };
        return snapshot;
    }

    private static SvsapmeMachineSnapshotResponse BaseRemoteSnapshot(SvsapmeMachineMenuKind kind, string displayName)
    {
        return new SvsapmeMachineSnapshotResponse
        {
            MenuSessionId = Guid.NewGuid(),
            RequestSequence = 1,
            MachineGuid = Guid.NewGuid(),
            Success = true,
            DisplayName = displayName,
            QualifiedItemId = "(BC)" + ModItemCatalog.CarbonGenerator,
            LocationName = "Farm",
            TileX = 10,
            TileY = 10,
            StoredWh = 450_250,
            CapacityWh = 640_000,
            Revision = 1,
            MenuKind = kind
        };
    }

    private Vector2 FindFixtureTile(GameLocation location)
    {
        for (var y = 2; y < 80; y++)
        {
            for (var x = 2; x < 80; x++)
            {
                var tile = new Vector2(x, y);
                if (!location.objects.ContainsKey(tile))
                    return tile;
            }
        }

        return new Vector2(120 + location.objects.Count(), 120);
    }

    private string GetScreenshotDirectory() => Path.Combine(this.outputDir, "screenshots", "SVSAPME");

    private static int CalculatePanelCoveragePermille(Color[] pixels, int width, int height, IClickableMenu menu)
    {
        var left = Math.Clamp(menu.xPositionOnScreen, 0, width - 1);
        var top = Math.Clamp(menu.yPositionOnScreen, 0, height - 1);
        var right = Math.Clamp(menu.xPositionOnScreen + menu.width, left + 1, width);
        var bottom = Math.Clamp(menu.yPositionOnScreen + menu.height, top + 1, height);
        var sampled = 0;
        var colored = 0;
        for (var y = top; y < bottom; y += 2)
        {
            for (var x = left; x < right; x += 2)
            {
                var pixel = pixels[y * width + x];
                sampled++;
                if (pixel.A > 32 && pixel.R + pixel.G + pixel.B > 60)
                    colored++;
            }
        }

        return sampled == 0 ? 0 : colored * 1000 / sampled;
    }

    private static int CalculatePanelBlackPixelPermille(Color[] pixels, int width, int height, IClickableMenu menu)
    {
        var left = Math.Clamp(menu.xPositionOnScreen, 0, width - 1);
        var top = Math.Clamp(menu.yPositionOnScreen, 0, height - 1);
        var right = Math.Clamp(menu.xPositionOnScreen + menu.width, left + 1, width);
        var bottom = Math.Clamp(menu.yPositionOnScreen + menu.height, top + 1, height);
        var sampled = 0;
        var black = 0;
        for (var y = top; y < bottom; y += 2)
        {
            for (var x = left; x < right; x += 2)
            {
                var pixel = pixels[y * width + x];
                sampled++;
                if (pixel.A > 240 && pixel.R <= 3 && pixel.G <= 3 && pixel.B <= 3)
                    black++;
            }
        }

        return sampled == 0 ? 1000 : black * 1000 / sampled;
    }

    private void CloseCurrentMenu()
    {
        try
        {
            this.currentMenu?.exitThisMenuNoSound();
        }
        catch (Exception ex)
        {
            this.monitor.Log($"SVSAPME_GUI_CAPTURE cleanup warning: {ex.Message}", LogLevel.Warn);
        }

        Game1.activeClickableMenu = null;
        this.currentMenu = null;
    }

    private void WriteProgress()
    {
        this.WriteJson("svsapme-gui-capture-progress.json", new GuiCaptureReport(
            "SVSAPME",
            this.cases?.Count ?? 0,
            this.results.Count(result => result.Pass),
            this.results.Count(result => !result.Pass),
            false,
            this.results));
    }

    private void Finish()
    {
        if (this.stopped)
            return;

        this.CloseCurrentMenu();
        var expected = this.cases?.Count ?? 0;
        var localPass = expected > 0
            && this.results.Count == expected
            && this.results.All(result => result.Pass)
            && this.results.Select(result => result.Sha256).Distinct(StringComparer.Ordinal).Count() == expected;
        var report = new GuiCaptureReport(
            "SVSAPME",
            expected,
            this.results.Count(result => result.Pass),
            this.results.Count(result => !result.Pass),
            localPass,
            this.results);
        this.WriteJson(SvsapmeCompleteFileName, report);
        this.WriteCombinedReport(report);
        this.monitor.Log($"SVSAPME_GUI_CAPTURE {(localPass ? "COMPLETE" : "FAIL")} {report.Passed}/{report.Expected}", localPass ? LogLevel.Info : LogLevel.Error);

        this.stopped = true;
        this.helper.Events.GameLoop.UpdateTicked -= this.OnUpdateTicked;
    }

    private void WriteCombinedReport(GuiCaptureReport svsapmeReport)
    {
        var svsapPath = Path.Combine(this.outputDir, SvsapCompleteFileName);
        var svsapPass = false;
        var svsapExpected = 0;
        var svsapPassed = 0;
        if (File.Exists(svsapPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(svsapPath));
            var root = document.RootElement;
            svsapPass = root.TryGetProperty("Pass", out var passElement) && passElement.GetBoolean();
            svsapExpected = root.TryGetProperty("Expected", out var expectedElement) ? expectedElement.GetInt32() : 0;
            svsapPassed = root.TryGetProperty("Passed", out var passedElement) ? passedElement.GetInt32() : 0;
        }

        var allFiles = Directory.GetFiles(Path.Combine(this.outputDir, "screenshots"), "*.png", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var uniqueHashes = allFiles
            .Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
            .Distinct(StringComparer.Ordinal)
            .Count();
        this.WriteJson(AllCompleteFileName, new
        {
            Pass = svsapPass
                && svsapmeReport.Pass
                && allFiles.Count == svsapExpected + svsapmeReport.Expected
                && uniqueHashes == allFiles.Count,
            Expected = svsapExpected + svsapmeReport.Expected,
            Passed = svsapPassed + svsapmeReport.Passed,
            ScreenshotCount = allFiles.Count,
            UniqueScreenshotHashes = uniqueHashes,
            SvsapPass = svsapPass,
            SvsapmePass = svsapmeReport.Pass,
            Screenshots = allFiles
        });
    }

    private void WriteJson(string fileName, object payload)
    {
        Directory.CreateDirectory(this.outputDir);
        File.WriteAllText(Path.Combine(this.outputDir, fileName), JsonSerializer.Serialize(payload, JsonOptions));
    }

    private sealed record GuiCaptureCase(string Name, Func<IClickableMenu> CreateMenu, int MinimumQualityOverlays = 0);

    private sealed record GuiCaptureReport(
        string Mod,
        int Expected,
        int Passed,
        int Failed,
        bool Pass,
        IReadOnlyList<GuiCaptureResult> Results);

    private sealed record GuiCaptureResult(
        string Name,
        bool Pass,
        string Path,
        int Width,
        int Height,
        long Bytes,
        int SampledColors,
        int PanelCoveragePermille,
        int BlackPixelPermille,
        int SlotGeometryViolationCount,
        IReadOnlyList<string> SlotGeometryErrors,
        int QualityOverlayCount,
        int MinimumQualityOverlays,
        string Sha256,
        string Error)
    {
        public static GuiCaptureResult Failed(string name, Exception ex)
        {
            return new GuiCaptureResult(name, false, string.Empty, 0, 0, 0, 0, 0, 1000, 1, new[] { ex.Message }, 0, 0, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
#endif
