using Koizumi.SVSAP.Api;
using Microsoft.Xna.Framework;
using SVSAPME.Content;
using SVSAPME.Models;
using SVSAPME.UI;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using SObject = StardewValley.Object;

namespace SVSAPME.Services;

internal sealed class SingleBlockProcessorService
{
    private readonly MachineStateRepository repository;
    private readonly MachineRegistryService registry;
    private readonly EnergyNetworkManager energy;
    private readonly Func<ISvsapApi?> getSvsapApi;
    private readonly Func<ModConfig> getConfig;
    private readonly IInputHelper inputHelper;
    private readonly IMonitor monitor;
    private Func<SvsapmeMachineActionRequest, bool>? sendClientAction;
    private Func<Guid, bool>? sendSnapshotRequest;

    public SingleBlockProcessorService(
        MachineStateRepository repository,
        MachineRegistryService registry,
        EnergyNetworkManager energy,
        Func<ISvsapApi?> getSvsapApi,
        Func<ModConfig> getConfig,
        IInputHelper inputHelper,
        IMonitor monitor)
    {
        this.repository = repository;
        this.registry = registry;
        this.energy = energy;
        this.getSvsapApi = getSvsapApi;
        this.getConfig = getConfig;
        this.inputHelper = inputHelper;
        this.monitor = monitor;
    }

    public void SetClientActionSender(Func<SvsapmeMachineActionRequest, bool> sender)
    {
        this.sendClientAction = sender;
    }

    public void SetSnapshotRequestSender(Func<Guid, bool> sender)
    {
        this.sendSnapshotRequest = sender;
    }

    public void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.activeClickableMenu is not null || !e.Button.IsActionButton())
            return;

        var location = Game1.currentLocation;
        var tile = e.Cursor.GrabTile;
        if (location is null
            || !location.Objects.TryGetValue(tile, out var placedObject)
            || !SingleBlockProcessorRules.IsProcessorMachine(placedObject.QualifiedItemId))
        {
            return;
        }

        if (Game1.player.CurrentItem?.QualifiedItemId == "(O)" + ModItemCatalog.SvsapLinkTool)
            return;

        var held = Game1.player.CurrentItem;
        if (held is null || held.Stack <= 0)
        {
            if (this.TryOpenProcessorMenu(placedObject, location, tile))
                this.Suppress(e);

            return;
        }

        var processorKind = SingleBlockProcessorRules.GetProcessorKind(placedObject.QualifiedItemId);
        if (!Context.IsMainPlayer)
        {
            if (!CanProcessorAcceptBufferedInput(processorKind, held, out var previewMessage))
            {
                Game1.addHUDMessage(new HUDMessage(previewMessage, HUDMessage.error_type));
                this.Suppress(e);
                return;
            }

            this.TrySendClientLoadAction(placedObject, held, held.Stack);
            this.Suppress(e);
            return;
        }

        if (!CanProcessorAcceptBufferedInput(processorKind, held, out var bufferedPreviewMessage))
        {
            Game1.addHUDMessage(new HUDMessage(bufferedPreviewMessage, HUDMessage.error_type));
            this.Suppress(e);
            return;
        }

        var result = this.TryBufferHeldProcessorInput(placedObject, location, tile, Game1.player);
        Game1.addHUDMessage(new HUDMessage(result.Message, result.Success ? HUDMessage.newQuest_type : HUDMessage.error_type));
        this.Suppress(e);
    }

    public void OnTimeChanged(object? sender, TimeChangedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady || !this.getConfig().EnableElectricMachines)
            return;

        var elapsedMinutes = GetElapsedMinutes(e.OldTime, e.NewTime);
        if (elapsedMinutes <= 0)
            return;

        var api = this.getSvsapApi();
        if (api is null)
            return;

        var changed = false;
        foreach (var machine in this.registry.MachinesByGuid.Values
            .Where(machine => SingleBlockProcessorRules.IsKegMachine(machine.QualifiedItemId))
            .OrderBy(machine => machine.MachineGuid)
            .ToList())
        {
            if (!this.repository.TryGet(machine.MachineGuid, out var state)
                || !TryGetActiveEndpoint(api, machine, out _, out var endpoint))
            {
                continue;
            }

            changed |= this.AdvanceKegMachine(endpoint.NetworkId, machine, state, elapsedMinutes);
            changed |= SetKegUpdateTime(state.Processor, e.NewTime);
            changed |= this.ProcessProcessorNetworkAutomation(api, endpoint.NetworkId, machine, state);
        }

        if (changed)
            this.repository.Save();
    }

    public void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady || !this.getConfig().EnableElectricMachines)
            return;

        var api = this.getSvsapApi();
        if (api is null)
            return;

        var changed = false;
        foreach (var machine in this.registry.MachinesByGuid.Values
            .Where(machine => SingleBlockProcessorRules.IsKegMachine(machine.QualifiedItemId))
            .OrderBy(machine => machine.MachineGuid)
            .ToList())
        {
            if (!this.repository.TryGet(machine.MachineGuid, out var state)
                || !TryGetActiveEndpoint(api, machine, out _, out var endpoint))
            {
                continue;
            }

            var overnightMinutes = GetKegMinutesUntilDayBoundary(state.Processor.LastKegUpdateTime);
            if (overnightMinutes > 0)
                changed |= this.AdvanceKegMachine(endpoint.NetworkId, machine, state, overnightMinutes);
            changed |= SetKegUpdateTime(state.Processor, 600);
            changed |= this.ProcessProcessorNetworkAutomation(api, endpoint.NetworkId, machine, state);
        }

        foreach (var machine in this.registry.MachinesByGuid.Values
            .Where(machine => SingleBlockProcessorRules.IsCaskMachine(machine.QualifiedItemId))
            .OrderBy(machine => machine.MachineGuid)
            .ToList())
        {
            if (!this.repository.TryGet(machine.MachineGuid, out var state)
                || !TryGetActiveEndpoint(api, machine, out _, out var endpoint))
            {
                continue;
            }

            changed |= this.AdvanceCaskMachine(endpoint.NetworkId, machine, state);
            changed |= this.ProcessProcessorNetworkAutomation(api, endpoint.NetworkId, machine, state);
        }

        if (changed)
            this.repository.Save();
    }

    public IReadOnlyList<ProcessorSlotView> GetSlotViews(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return Array.Empty<ProcessorSlotView>();
        }

        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        return state.Processor.Slots
            .Select((slot, index) => new ProcessorSlotView(
                index,
                slot.Input,
                slot.Output,
                SingleBlockProcessorRules.IsReady(slot),
                SingleBlockProcessorRules.FormatEta(slot),
                slot.RemainingDays > 0 ? slot.RemainingDays : slot.RemainingMinutes,
                slot.TotalDays > 0 ? slot.TotalDays : slot.TotalMinutes))
            .ToList();
    }

    public ProcessorDashboardView GetDashboard(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new ProcessorDashboardView(false, false, false, MachineInputModes.AllEligible, MachineFilterModes.Whitelist, 0, 0, 0, 0, 0, 0, 0m);
        }

        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        var active = SingleBlockProcessorRules.CountActive(state.Processor);
        var ready = SingleBlockProcessorRules.CountReady(state.Processor);
        var empty = SingleBlockProcessorRules.CountEmpty(state.Processor);
        var inputStacks = state.Processor.InputBuffer.Count;
        var outputStacks = state.Processor.OutputBuffer.Count;
        var dailyValue = EstimateProcessorDailyValue(state.Processor);
        return new ProcessorDashboardView(
            true,
            state.Processor.AutoPullFromNetwork,
            state.Processor.AutoPushOutputToNetwork,
            state.Processor.InputMode,
            state.Processor.FilterMode,
            state.Processor.FilterQualifiedItemIds.Count,
            active,
            ready,
            empty,
            inputStacks,
            outputStacks,
            dailyValue);
    }

    internal SvsapmeMachineActionApplyResult ToggleProcessorAutoPull(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateProcessorState(placedObject, location, tile, state =>
        {
            state.Processor.AutoPullFromNetwork = !state.Processor.AutoPullFromNetwork;
            return state.Processor.AutoPullFromNetwork
                ? ModText.Get("hud.processor.autoPullOn", "Processor network input enabled.")
                : ModText.Get("hud.processor.autoPullOff", "Processor network input disabled.");
        });
    }

    internal SvsapmeMachineActionApplyResult ToggleProcessorAutoPush(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateProcessorState(placedObject, location, tile, state =>
        {
            state.Processor.AutoPushOutputToNetwork = !state.Processor.AutoPushOutputToNetwork;
            return state.Processor.AutoPushOutputToNetwork
                ? ModText.Get("hud.processor.autoPushOn", "Processor network output enabled.")
                : ModText.Get("hud.processor.autoPushOff", "Processor network output disabled.");
        });
    }

    internal SvsapmeMachineActionApplyResult ToggleProcessorInputMode(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateProcessorState(placedObject, location, tile, state =>
        {
            state.Processor.InputMode = string.Equals(state.Processor.InputMode, MachineInputModes.Filter, StringComparison.Ordinal)
                ? MachineInputModes.AllEligible
                : MachineInputModes.Filter;
            return string.Equals(state.Processor.InputMode, MachineInputModes.Filter, StringComparison.Ordinal)
                ? ModText.Get("hud.processor.inputModeFilter", "Processor input mode: filter.")
                : ModText.Get("hud.processor.inputModeAll", "Processor input mode: all eligible.");
        });
    }

    internal SvsapmeMachineActionApplyResult ToggleProcessorFilterMode(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateProcessorState(placedObject, location, tile, state =>
        {
            state.Processor.FilterMode = string.Equals(state.Processor.FilterMode, MachineFilterModes.Blacklist, StringComparison.Ordinal)
                ? MachineFilterModes.Whitelist
                : MachineFilterModes.Blacklist;
            return string.Equals(state.Processor.FilterMode, MachineFilterModes.Blacklist, StringComparison.Ordinal)
                ? ModText.Get("hud.processor.filterModeBlacklist", "Processor filter mode: blacklist.")
                : ModText.Get("hud.processor.filterModeWhitelist", "Processor filter mode: whitelist.");
        });
    }

    internal SvsapmeMachineActionApplyResult AddHeldProcessorFilter(SObject placedObject, GameLocation location, Vector2 tile, Item? held)
    {
        if (held is null)
            return new(false, false, ModText.Get("hud.processor.filterHoldItem", "Hold an item to add it to the processor filter."));

        return this.MutateProcessorState(placedObject, location, tile, state =>
        {
            if (!state.Processor.FilterQualifiedItemIds.Contains(held.QualifiedItemId, StringComparer.Ordinal))
                state.Processor.FilterQualifiedItemIds.Add(held.QualifiedItemId);
            state.Processor.InputMode = MachineInputModes.Filter;
            return ModText.Get("hud.processor.filterAdded", "Processor filter added: {{item}}.", new { item = held.DisplayName });
        });
    }

    internal SvsapmeMachineActionApplyResult ClearProcessorFilter(SObject placedObject, GameLocation location, Vector2 tile)
    {
        return this.MutateProcessorState(placedObject, location, tile, state =>
        {
            state.Processor.FilterQualifiedItemIds.Clear();
            return ModText.Get("hud.processor.filterCleared", "Processor filter cleared.");
        });
    }

    internal SvsapmeMachineActionApplyResult TryBufferHeldProcessorInput(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        Farmer player)
    {
        var held = player.CurrentItem;
        if (held is null || held.Stack <= 0)
            return new(false, false, ModText.Get("hud.processor.inputHoldItem", "Hold an item to add it to the processor input buffer."));

        var kind = SingleBlockProcessorRules.GetProcessorKind(placedObject.QualifiedItemId);
        if (!CanProcessorAcceptBufferedInput(kind, held, out var message))
            return new(false, false, message);

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.processor.registerFailed", "SVSAPME could not register this processor."));
        }

        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        var bufferedCount = Math.Max(1, held.Stack);
        AddBufferedInput(state.Processor.InputBuffer, held);
        ConsumeHeldItem(player, bufferedCount);
        var filled = this.FillEmptyProcessorSlotsFromBuffer(state.Processor, kind);
        this.repository.Save();
        return new(
            true,
            false,
            ModText.Get(
                "hud.processor.inputBuffered",
                "Buffered {{count}} {{item}} for processing; filled {{filled}} slot(s).",
                new { count = bufferedCount.ToString("N0"), item = held.DisplayName, filled = filled.ToString("N0") }));
    }

    public IReadOnlyList<string> BuildProcessorStatusLines(SObject placedObject, GameLocation location, Vector2 tile, MachineState state)
    {
        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        var kind = SingleBlockProcessorRules.GetProcessorKind(placedObject.QualifiedItemId);
        var active = SingleBlockProcessorRules.CountActive(state.Processor);
        var ready = SingleBlockProcessorRules.CountReady(state.Processor);
        var empty = SingleBlockProcessorRules.CountEmpty(state.Processor);
        var lines = new List<string>
        {
            ModText.Get("ui.processor.name", "Processor: {{name}}", new { name = placedObject.DisplayName }),
            ModText.Get("ui.processor.location", "Location: {{location}} ({{x}}, {{y}})", new { location = location.NameOrUniqueName, x = tile.X.ToString("0"), y = tile.Y.ToString("0") }),
            ModText.Get("ui.processor.kind", "Kind: {{kind}}", new { kind = FormatKind(kind) }),
            ModText.Get("ui.processor.slots", "Slots: {{active}} active, {{ready}} ready, {{empty}} empty / {{capacity}}", new { active = active.ToString("N0"), ready = ready.ToString("N0"), empty = empty.ToString("N0"), capacity = tier.Slots.ToString("N0") }),
            kind == SingleBlockProcessorKind.Keg
                ? ModText.Get("ui.processor.energy.keg", "Energy: {{wh}} Wh/slot/hour while processing", new { wh = tier.KegWhPerSlotPerHour.ToString("N0") })
                : ModText.Get("ui.processor.energy.cask", "Energy: {{wh}} Wh/slot/day while aging", new { wh = tier.CaskWhPerSlotPerDay.ToString("N0") })
        };

        if (state.Processor.InputBuffer.Count > 0)
            lines.Add(ModText.Get("ui.processor.inputBuffer", "Input buffer: {{items}}", new { items = FormatBufferedItems(state.Processor.InputBuffer) }));

        if (state.Processor.OutputBuffer.Count > 0)
            lines.Add(ModText.Get("ui.processor.outputBuffer", "Overflow buffer: {{items}}", new { items = FormatBufferedItems(state.Processor.OutputBuffer) }));

        var api = this.getSvsapApi();
        if (api is not null
            && TryReadMachineGuid(placedObject, out var machineGuid)
            && TryGetActiveEndpoint(api, new MachineLocation(machineGuid, location.NameOrUniqueName, tile, placedObject.QualifiedItemId), out _, out var endpoint))
        {
            lines.Add(ModText.Get("ui.processor.network", "Network: {{network}}", new { network = endpoint.NetworkId.ToString("N") }));
            if (this.energy.TryGetNetworkEnergy(endpoint.NetworkId, out var storedWh, out var capacityWh, out _))
                lines.Add(ModText.Get("ui.processor.networkEnergy", "Network energy: {{stored}}/{{capacity}}", new { stored = FormatWh(storedWh), capacity = FormatWh(capacityWh) }));
        }
        else
        {
            lines.Add(api is null
                ? ModText.Get("ui.processor.network.apiUnavailable", "Network: SVSAP API unavailable")
                : ModText.Get("ui.processor.network.notLinked", "Network: not linked or inactive"));
        }

        foreach (var line in GetPreviewLines(state, limit: 8))
            lines.Add(line);

        return lines;
    }

    private SvsapmeMachineActionApplyResult MutateProcessorState(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        Func<MachineState, string> mutate)
    {
        if (!SingleBlockProcessorRules.IsProcessorMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.processor.notProcessor", "Target machine is not a Single-Block Keg or Cask."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.processor.registerFailed", "SVSAPME could not register this processor."));
        }

        var message = mutate(state);
        this.repository.Save();
        return new(true, false, message);
    }

    private static decimal EstimateProcessorDailyValue(SingleBlockProcessorMachineState processor)
    {
        decimal value = 0;
        foreach (var slot in processor.Slots)
        {
            if (slot.Output is null)
                continue;

            try
            {
                var output = BufferedItemCodec.CreateItem(slot.Output);
                var salePrice = Math.Max(0, output.salePrice(false));
                var total = slot.TotalDays > 0 ? slot.TotalDays : slot.TotalMinutes;
                var dailyFactor = slot.TotalDays > 0
                    ? 1m / Math.Max(1, total)
                    : 1200m / Math.Max(1, total);
                value += salePrice * Math.Max(0.01m, dailyFactor);
            }
            catch
            {
                // Ignore stale/custom outputs that cannot be materialized outside gameplay content.
            }
        }

        foreach (var stack in processor.OutputBuffer)
        {
            try
            {
                var output = BufferedItemCodec.CreateItem(stack);
                value += Math.Max(0, output.salePrice(false)) * Math.Max(1, stack.Stack);
            }
            catch
            {
                // Ignore stale/custom outputs that cannot be materialized outside gameplay content.
            }
        }

        return value;
    }

    internal SvsapmeMachineActionApplyResult TryLoadInputItem(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        Item input,
        out int consumedCount)
    {
        consumedCount = 0;
        if (!SingleBlockProcessorRules.TryCreateJob(
                SingleBlockProcessorRules.GetProcessorKind(placedObject.QualifiedItemId),
                input,
                out var job,
                out var message))
        {
            return new(false, false, message);
        }

        var result = this.TryLoadPreparedSlot(placedObject, location, tile, job);
        if (result.Success)
            consumedCount = job.InputCount;

        return result;
    }

    internal SvsapmeMachineActionApplyResult TryLoadInput(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        BufferedItemStack input)
    {
        if (!SingleBlockProcessorRules.IsProcessorMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.processor.notProcessor", "Target machine is not a Single-Block Keg or Cask."));

        var item = BufferedItemCodec.CreateItem(input);
        var kind = SingleBlockProcessorRules.GetProcessorKind(placedObject.QualifiedItemId);
        if (!CanProcessorAcceptBufferedInput(kind, item, out var message))
            return new(false, false, message);

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.processor.registerFailed", "SVSAPME could not register this processor."));
        }

        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        AddBufferedInput(state.Processor.InputBuffer, item);
        var filled = this.FillEmptyProcessorSlotsFromBuffer(state.Processor, kind);
        this.repository.Save();
        return new(
            true,
            true,
            ModText.Get(
                "hud.processor.inputBuffered",
                "Buffered {{count}} {{item}} for processing; filled {{filled}} slot(s).",
                new { count = Math.Max(1, item.Stack).ToString("N0"), item = item.DisplayName, filled = filled.ToString("N0") }));
    }

    internal SvsapmeMachineActionApplyResult TryCollectOutput(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        Farmer recipient,
        int slotNumber)
    {
        if (!SingleBlockProcessorRules.IsProcessorMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.processor.notProcessor", "Target machine is not a Single-Block Keg or Cask."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.processor.registerFailed", "SVSAPME could not register this processor."));
        }

        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        var collected = 0;
        if (slotNumber > 0)
        {
            var index = slotNumber - 1;
            if (index < 0 || index >= state.Processor.Slots.Count)
                return new(false, false, ModText.Get("hud.processor.invalidSlot", "That processor slot does not exist."));

            collected += this.CollectSlot(state.Processor.Slots[index], recipient);
        }
        else
        {
            foreach (var slot in state.Processor.Slots)
                collected += this.CollectSlot(slot, recipient);

            for (var i = 0; i < state.Processor.OutputBuffer.Count;)
            {
                var item = BufferedItemCodec.CreateItem(state.Processor.OutputBuffer[i]);
                if (!TryGiveItem(recipient, item))
                {
                    state.Processor.OutputBuffer[i] = BufferedItemCodec.FromItem(item);
                    i++;
                    continue;
                }

                state.Processor.OutputBuffer.RemoveAt(i);
                collected++;
            }
        }

        if (collected <= 0)
            return new(false, false, ModText.Get("hud.processor.noReadyOutput", "This processor has no ready output."));

        this.repository.Save();
        return new(true, false, ModText.Get("hud.processor.collected", "Collected {{count}} processor output stack(s).", new { count = collected.ToString("N0") }));
    }

    internal SvsapmeMachineActionApplyResult TryCollectOutputForRemotePlayer(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        int slotNumber)
    {
        if (!SingleBlockProcessorRules.IsProcessorMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.processor.notProcessor", "Target machine is not a Single-Block Keg or Cask."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.processor.registerFailed", "SVSAPME could not register this processor."));
        }

        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        var returned = new List<BufferedItemStack>();
        if (slotNumber > 0)
        {
            var index = slotNumber - 1;
            if (index < 0 || index >= state.Processor.Slots.Count)
                return new(false, false, ModText.Get("hud.processor.invalidSlot", "That processor slot does not exist."));

            CollectSlotToReturnedItems(state.Processor.Slots[index], returned);
        }
        else
        {
            foreach (var slot in state.Processor.Slots)
                CollectSlotToReturnedItems(slot, returned);

            returned.AddRange(state.Processor.OutputBuffer);
            state.Processor.OutputBuffer.Clear();
        }

        if (returned.Count <= 0)
            return new(false, false, ModText.Get("hud.processor.noReadyOutput", "This processor has no ready output."));

        this.repository.Save();
        return new SvsapmeMachineActionApplyResult(
            true,
            false,
            ModText.Get("hud.processor.collected", "Collected {{count}} processor output stack(s).", new { count = returned.Count.ToString("N0") }))
        {
            ReturnedItems = returned
        };
    }

    private SvsapmeMachineActionApplyResult TryLoadPreparedSlot(
        SObject placedObject,
        GameLocation location,
        Vector2 tile,
        SingleBlockProcessorSlotState job)
    {
        if (!SingleBlockProcessorRules.IsProcessorMachine(placedObject.QualifiedItemId))
            return new(false, false, ModText.Get("hud.processor.notProcessor", "Target machine is not a Single-Block Keg or Cask."));

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return new(false, false, ModText.Get("hud.processor.registerFailed", "SVSAPME could not register this processor."));
        }

        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        var empty = state.Processor.Slots.FirstOrDefault(slot => !SingleBlockProcessorRules.IsWorking(slot));
        if (empty is null)
            return new(false, false, ModText.Get("hud.processor.full", "This processor has no empty slot."));

        AssignSlot(empty, job);
        if (SingleBlockProcessorRules.GetProcessorKind(placedObject.QualifiedItemId) == SingleBlockProcessorKind.Keg)
            SetKegUpdateTime(state.Processor, Game1.timeOfDay);

        this.repository.Save();
        return new(true, true, ModText.Get("hud.processor.loaded", "{{item}} loaded. ETA: {{eta}}.", new { item = FormatItem(job.Output?.QualifiedItemId ?? string.Empty), eta = SingleBlockProcessorRules.FormatEta(empty) }));
    }

    private bool TryOpenProcessorMenu(SObject placedObject, GameLocation location, Vector2 tile)
    {
        if (!Context.IsMainPlayer)
        {
            if (TryReadMachineGuid(placedObject, out var remoteMachineGuid))
                return this.TrySendSnapshotRequest(remoteMachineGuid);

            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayer.unknownMachineGuid", "MachineGuid is unknown on the host."), HUDMessage.error_type));
            return true;
        }

        if (!this.registry.TryRegisterPlacedMachine(placedObject, location, tile)
            || !TryReadMachineGuid(placedObject, out var machineGuid)
            || !this.repository.TryGet(machineGuid, out var state))
        {
            return false;
        }

        var tier = SingleBlockProcessorRules.GetTier(placedObject.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        Game1.activeClickableMenu = new SingleBlockProcessorMenu(placedObject, location, tile, this);
        return true;
    }

    private bool TrySendSnapshotRequest(Guid machineGuid)
    {
        if (this.sendSnapshotRequest is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayerActionSenderNotReady", "SVSAPME multiplayer action sender is not ready."), HUDMessage.error_type));
            return true;
        }

        this.sendSnapshotRequest(machineGuid);
        return true;
    }

    private bool TrySendClientLoadAction(SObject placedObject, Item input, int inputCount)
    {
        if (this.sendClientAction is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayerActionSenderNotReady", "SVSAPME multiplayer action sender is not ready."), HUDMessage.error_type));
            return false;
        }

        if (!TryReadMachineGuid(placedObject, out var machineGuid))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.machineIdentityNotSynced", "SVSAPME machine identity has not synced from the host yet."), HUDMessage.error_type));
            return false;
        }

        var buffered = BufferedItemCodec.FromItem(input);
        buffered.Stack = inputCount;
        return this.sendClientAction(new SvsapmeMachineActionRequest
        {
            TransactionId = Guid.NewGuid(),
            MachineGuid = machineGuid,
            ActionKind = SvsapmeMachineActionKind.LoadProcessorInput,
            QualifiedItemId = buffered.QualifiedItemId,
            Count = inputCount,
            Quality = buffered.Quality,
            PreservedParentSheetIndex = buffered.PreservedParentSheetIndex,
            PreserveType = buffered.PreserveType,
            Price = buffered.Price,
            Edibility = buffered.Edibility,
            Category = buffered.Category,
            Type = buffered.Type,
            Name = buffered.Name,
            DisplayName = buffered.DisplayName,
            Color = buffered.Color,
            ModData = buffered.ModData
        });
    }

    internal bool TrySendClientCollectAction(SObject placedObject, int slotNumber)
    {
        if (this.sendClientAction is null)
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.multiplayerActionSenderNotReady", "SVSAPME multiplayer action sender is not ready."), HUDMessage.error_type));
            return false;
        }

        if (!TryReadMachineGuid(placedObject, out var machineGuid))
        {
            Game1.addHUDMessage(new HUDMessage(ModText.Get("hud.machineIdentityNotSynced", "SVSAPME machine identity has not synced from the host yet."), HUDMessage.error_type));
            return false;
        }

        return this.sendClientAction(new SvsapmeMachineActionRequest
        {
            TransactionId = Guid.NewGuid(),
            MachineGuid = machineGuid,
            ActionKind = SvsapmeMachineActionKind.CollectProcessorOutput,
            Count = Math.Max(0, slotNumber)
        });
    }

    private bool AdvanceKegMachine(Guid networkId, MachineLocation machine, MachineState state, int elapsedMinutes)
    {
        var tier = SingleBlockProcessorRules.GetTier(machine.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        var active = state.Processor.Slots.Count(slot => SingleBlockProcessorRules.IsWorking(slot) && slot.RemainingMinutes > 0);
        if (active <= 0)
            return false;

        var requiredWh = CalculateRequiredWh(active, tier.KegWhPerSlotPerHour, elapsedMinutes / 60.0);
        if (requiredWh > 0
            && !this.energy.TryConsumeWh(networkId, requiredWh, ModItemCatalog.UniqueId, "single-block-keg", allowPartial: false, out _, out _, out _))
        {
            return false;
        }

        foreach (var slot in state.Processor.Slots)
            SingleBlockProcessorRules.AdvanceKegSlot(slot, elapsedMinutes);

        return true;
    }

    private bool AdvanceCaskMachine(Guid networkId, MachineLocation machine, MachineState state)
    {
        var tier = SingleBlockProcessorRules.GetTier(machine.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);
        var active = state.Processor.Slots.Count(slot => SingleBlockProcessorRules.IsWorking(slot) && slot.RemainingDays > 0);
        if (active <= 0)
            return false;

        var requiredWh = CalculateRequiredWh(active, tier.CaskWhPerSlotPerDay, 1.0);
        if (requiredWh > 0
            && !this.energy.TryConsumeWh(networkId, requiredWh, ModItemCatalog.UniqueId, "single-block-cask", allowPartial: false, out _, out _, out _))
        {
            return false;
        }

        foreach (var slot in state.Processor.Slots)
            SingleBlockProcessorRules.AdvanceCaskSlot(slot, 1);

        return true;
    }

    private bool ProcessProcessorNetworkAutomation(ISvsapApi api, Guid networkId, MachineLocation machine, MachineState state)
    {
        var tier = SingleBlockProcessorRules.GetTier(machine.QualifiedItemId);
        SingleBlockProcessorRules.NormalizeSlots(state.Processor, tier);

        var changed = false;
        if (this.getConfig().EnableAutomaticProcessorOutputToNetwork && state.Processor.AutoPushOutputToNetwork)
        {
            changed |= this.FlushReadyProcessorSlotsToNetwork(api, state, networkId);
            changed |= this.FlushProcessorOutputBufferToNetwork(api, state, networkId);
        }

        changed |= this.FillEmptyProcessorSlotsFromBuffer(state.Processor, SingleBlockProcessorRules.GetProcessorKind(machine.QualifiedItemId)) > 0;

        if (this.getConfig().EnableAutomaticProcessorInputFromNetwork && state.Processor.AutoPullFromNetwork)
            changed |= this.FillEmptyProcessorSlotsFromNetwork(api, networkId, machine, state);

        changed |= this.FillEmptyProcessorSlotsFromBuffer(state.Processor, SingleBlockProcessorRules.GetProcessorKind(machine.QualifiedItemId)) > 0;

        return changed;
    }

    private bool FlushReadyProcessorSlotsToNetwork(ISvsapApi api, MachineState state, Guid networkId)
    {
        var changed = false;
        foreach (var slot in state.Processor.Slots)
        {
            if (!SingleBlockProcessorRules.IsReady(slot) || slot.Output is null)
                continue;

            var item = BufferedItemCodec.CreateItem(slot.Output);
            if (!api.TryInsertItem(networkId, item, out var remainder, out _, out _))
                continue;

            changed = true;
            if (remainder is null || remainder.Stack <= 0)
            {
                SingleBlockProcessorRules.ClearSlot(slot);
                continue;
            }

            slot.Output = BufferedItemCodec.FromItem(remainder);
        }

        return changed;
    }

    private bool FlushProcessorOutputBufferToNetwork(ISvsapApi api, MachineState state, Guid networkId)
    {
        var changed = false;
        for (var i = 0; i < state.Processor.OutputBuffer.Count;)
        {
            var item = BufferedItemCodec.CreateItem(state.Processor.OutputBuffer[i]);
            if (!api.TryInsertItem(networkId, item, out var remainder, out _, out _))
            {
                state.Processor.OutputBuffer[i] = BufferedItemCodec.FromItem(remainder ?? item);
                i++;
                continue;
            }

            changed = true;
            if (remainder is null || remainder.Stack <= 0)
            {
                state.Processor.OutputBuffer.RemoveAt(i);
                continue;
            }

            state.Processor.OutputBuffer[i] = BufferedItemCodec.FromItem(remainder);
            i++;
        }

        return changed;
    }

    private int FillEmptyProcessorSlotsFromBuffer(SingleBlockProcessorMachineState processor, SingleBlockProcessorKind kind)
    {
        if (kind == SingleBlockProcessorKind.None || processor.InputBuffer.Count <= 0)
            return 0;

        var filled = 0;
        foreach (var empty in processor.Slots.Where(slot => !SingleBlockProcessorRules.IsWorking(slot)).ToList())
        {
            if (!TryTakeBufferedProcessorJob(processor, kind, out var job))
                break;

            AssignSlot(empty, job);
            if (kind == SingleBlockProcessorKind.Keg)
                SetKegUpdateTime(processor, Game1.timeOfDay);

            filled++;
        }

        return filled;
    }

    private static bool TryTakeBufferedProcessorJob(
        SingleBlockProcessorMachineState processor,
        SingleBlockProcessorKind kind,
        out SingleBlockProcessorSlotState job)
    {
        for (var i = 0; i < processor.InputBuffer.Count; i++)
        {
            var stack = processor.InputBuffer[i];
            if (stack.Stack <= 0)
            {
                processor.InputBuffer.RemoveAt(i);
                i--;
                continue;
            }

            Item item;
            try
            {
                item = BufferedItemCodec.CreateItem(stack);
            }
            catch
            {
                continue;
            }

            if (!SingleBlockProcessorRules.TryCreateJob(kind, item, out job, out _))
                continue;

            processor.InputBuffer[i].Stack -= Math.Max(1, job.InputCount);
            if (processor.InputBuffer[i].Stack <= 0)
                processor.InputBuffer.RemoveAt(i);

            return true;
        }

        job = new SingleBlockProcessorSlotState();
        return false;
    }

    private bool FillEmptyProcessorSlotsFromNetwork(ISvsapApi api, Guid networkId, MachineLocation machine, MachineState state)
    {
        var kind = SingleBlockProcessorRules.GetProcessorKind(machine.QualifiedItemId);
        if (kind == SingleBlockProcessorKind.None)
            return false;

        var changed = false;
        foreach (var empty in state.Processor.Slots.Where(slot => !SingleBlockProcessorRules.IsWorking(slot)).ToList())
        {
            if (!api.TryExtractFirstMatchingItem(
                    networkId,
                    item => this.IsEligibleAutomatedProcessorInput(state.Processor, kind, item),
                    item => GetProcessorInputCount(kind, item),
                    highQualityFirst: false,
                    preserveGoldIridium: kind == SingleBlockProcessorKind.Keg,
                    out var extracted,
                    out _,
                    out _)
                || extracted is null
                || extracted.Stack <= 0)
            {
                break;
            }

            if (!SingleBlockProcessorRules.TryCreateJob(kind, extracted, out var job, out _))
            {
                if (!api.TryInsertItem(networkId, extracted, out var remainder, out _, out _)
                    || remainder is not null && remainder.Stack > 0)
                {
                    AddBufferedInput(state.Processor.InputBuffer, remainder ?? extracted);
                    changed = true;
                }

                break;
            }

            AssignSlot(empty, job);
            if (kind == SingleBlockProcessorKind.Keg)
                SetKegUpdateTime(state.Processor, Game1.timeOfDay);

            changed = true;
        }

        return changed;
    }

    private bool IsEligibleAutomatedProcessorInput(SingleBlockProcessorMachineState processor, SingleBlockProcessorKind kind, Item item)
    {
        return MatchesProcessorFilter(processor, item)
            && SingleBlockProcessorRules.TryCreateJob(kind, item, out var job, out _)
            && item.Stack >= job.InputCount;
    }

    private static int GetProcessorInputCount(SingleBlockProcessorKind kind, Item item)
    {
        return SingleBlockProcessorRules.TryCreateJob(kind, item, out var job, out _)
            ? Math.Max(1, job.InputCount)
            : 0;
    }

    private static bool MatchesProcessorFilter(SingleBlockProcessorMachineState processor, Item item)
    {
        if (!string.Equals(processor.InputMode, MachineInputModes.Filter, StringComparison.Ordinal))
            return true;

        var ids = processor.FilterQualifiedItemIds ?? new();
        var matched = ids.Contains(item.QualifiedItemId, StringComparer.Ordinal);
        return string.Equals(processor.FilterMode, MachineFilterModes.Blacklist, StringComparison.Ordinal)
            ? !matched
            : matched;
    }

    private static bool CanProcessorAcceptBufferedInput(SingleBlockProcessorKind kind, Item item, out string message)
    {
        var probe = item.getOne();
        probe.Stack = 999;
        return SingleBlockProcessorRules.TryCreateJob(kind, probe, out _, out message);
    }

    private static void AddBufferedInput(IList<BufferedItemStack> buffer, Item item)
    {
        var toAdd = item.getOne();
        toAdd.Stack = Math.Max(1, item.Stack);
        foreach (var stack in buffer)
        {
            var existing = BufferedItemCodec.CreateItem(stack);
            if (!existing.canStackWith(toAdd))
                continue;

            stack.Stack += toAdd.Stack;
            return;
        }

        buffer.Add(BufferedItemCodec.FromItem(toAdd));
    }

    private int CollectSlot(SingleBlockProcessorSlotState slot, Farmer recipient)
    {
        if (!SingleBlockProcessorRules.IsReady(slot) || slot.Output is null)
            return 0;

        var output = BufferedItemCodec.CreateItem(slot.Output);
        if (!TryGiveItem(recipient, output))
        {
            slot.Output = BufferedItemCodec.FromItem(output);
            return 0;
        }

        SingleBlockProcessorRules.ClearSlot(slot);
        return 1;
    }

    private static void CollectSlotToReturnedItems(SingleBlockProcessorSlotState slot, ICollection<BufferedItemStack> returned)
    {
        if (!SingleBlockProcessorRules.IsReady(slot) || slot.Output is null)
            return;

        returned.Add(slot.Output);
        SingleBlockProcessorRules.ClearSlot(slot);
    }

    private long CalculateRequiredWh(int activeSlots, long whPerSlot, double units)
    {
        var multiplier = Math.Max(0.0, this.getConfig().MachineEnergyCostMultiplier);
        return Math.Max(0, (long)Math.Ceiling(Math.Max(0, activeSlots) * Math.Max(0, whPerSlot) * Math.Max(0.0, units) * multiplier));
    }

    private static int GetElapsedMinutes(int oldTime, int newTime)
    {
        var oldMinutes = ToMinutes(oldTime);
        var newMinutes = ToMinutes(newTime);
        return newMinutes >= oldMinutes
            ? newMinutes - oldMinutes
            : Math.Max(0, (24 * 60 - oldMinutes) + newMinutes);
    }

    private static int GetKegMinutesUntilDayBoundary(int lastKegUpdateTime)
    {
        var last = Math.Clamp(lastKegUpdateTime <= 0 ? 600 : lastKegUpdateTime, 600, 2600);
        return Math.Max(0, ToMinutes(2600) - ToMinutes(last));
    }

    private static bool SetKegUpdateTime(SingleBlockProcessorMachineState processor, int timeOfDay)
    {
        var normalized = Math.Clamp(timeOfDay <= 0 ? 600 : timeOfDay, 600, 2600);
        if (processor.LastKegUpdateTime == normalized)
            return false;

        processor.LastKegUpdateTime = normalized;
        return true;
    }

    private static int ToMinutes(int time)
    {
        var hour = Math.Max(0, time / 100);
        var minute = Math.Max(0, time % 100);
        return hour * 60 + minute;
    }

    private static bool TryReadMachineGuid(SObject placedObject, out Guid machineGuid)
    {
        return Guid.TryParse(placedObject.modData.GetValueOrDefault(MachineRegistryService.MachineGuidKey), out machineGuid);
    }

    private static bool TryGetActiveEndpoint(
        ISvsapApi api,
        MachineLocation machine,
        out GameLocation location,
        out ISvsapEndpointInfo endpoint)
    {
        endpoint = null!;
        location = Game1.getLocationFromName(machine.LocationName);
        if (location is null || !location.Objects.TryGetValue(machine.Tile, out _))
            return false;

        if (!api.TryGetLinkedEndpoint(location, machine.Tile, out var found, out _, out _)
            || found is null
            || !found.Active)
        {
            return false;
        }

        endpoint = found;
        return true;
    }

    private static void ConsumeHeldItem(Farmer player, int count)
    {
        var held = player.CurrentItem;
        if (held is null || count <= 0)
            return;

        var slot = player.Items.IndexOf(held);
        held.Stack -= count;
        if (held.Stack <= 0 && slot >= 0)
            player.Items[slot] = null;
    }

    private static bool TryGiveItem(Farmer recipient, Item item)
    {
        if (recipient.addItemToInventoryBool(item))
            return true;

        var location = recipient.currentLocation ?? Game1.currentLocation;
        if (location is null)
            return false;

        Game1.createItemDebris(item, recipient.getStandingPosition(), -1, location);
        return true;
    }

    private static void AssignSlot(SingleBlockProcessorSlotState target, SingleBlockProcessorSlotState source)
    {
        target.Input = source.Input;
        target.Output = source.Output;
        target.InputCount = source.InputCount;
        target.RemainingMinutes = source.RemainingMinutes;
        target.TotalMinutes = source.TotalMinutes;
        target.RemainingDays = source.RemainingDays;
        target.TotalDays = source.TotalDays;
        target.TargetQuality = source.TargetQuality;
    }

    private static IEnumerable<string> GetPreviewLines(MachineState state, int limit)
    {
        var lines = new List<string>();
        foreach (var pair in state.Processor.Slots
            .Select((slot, index) => (slot, index))
            .Where(pair => SingleBlockProcessorRules.IsWorking(pair.slot))
            .Take(Math.Max(0, limit)))
        {
            lines.Add(ModText.Get(
                "ui.processor.slot.line",
                "#{{slot}} {{input}} -> {{output}} ETA {{eta}}",
                new
                {
                    slot = (pair.index + 1).ToString("N0"),
                    input = FormatItem(pair.slot.Input?.QualifiedItemId ?? string.Empty),
                    output = FormatItem(pair.slot.Output?.QualifiedItemId ?? string.Empty),
                    eta = SingleBlockProcessorRules.FormatEta(pair.slot)
                }));
        }

        return lines;
    }

    private static string FormatBufferedItems(IEnumerable<BufferedItemStack> stacks)
    {
        var parts = stacks
            .Where(stack => stack.Stack > 0)
            .Select(stack => $"{FormatItem(stack.QualifiedItemId)} x{stack.Stack:N0}")
            .ToList();
        return parts.Count == 0 ? ModText.Get("ui.common.empty", "empty") : string.Join(", ", parts);
    }

    private static string FormatKind(SingleBlockProcessorKind kind)
    {
        return kind switch
        {
            SingleBlockProcessorKind.Keg => ModText.Get("ui.processor.kind.keg", "Keg"),
            SingleBlockProcessorKind.Cask => ModText.Get("ui.processor.kind.cask", "Cask"),
            _ => ModText.Get("ui.common.none", "none")
        };
    }

    internal static string FormatItem(string qualifiedItemId)
    {
        if (string.IsNullOrWhiteSpace(qualifiedItemId))
            return ModText.Get("ui.common.none", "none");

        try
        {
            return ItemRegistry.Create(qualifiedItemId).DisplayName;
        }
        catch
        {
            return qualifiedItemId;
        }
    }

    private static string FormatWh(long wh)
    {
        return $"{Math.Max(0, wh) / 1000m:0.00} kWh";
    }

    private void Suppress(ButtonPressedEventArgs e)
    {
        this.inputHelper.Suppress(e.Button);
    }
}

internal readonly record struct ProcessorSlotView(
    int SlotIndex,
    BufferedItemStack? Input,
    BufferedItemStack? Output,
    bool Ready,
    string Eta,
    int Remaining,
    int Total);

internal readonly record struct ProcessorDashboardView(
    bool Available,
    bool AutoPullFromNetwork,
    bool AutoPushOutputToNetwork,
    string InputMode,
    string FilterMode,
    int FilterCount,
    int ActiveSlots,
    int ReadySlots,
    int EmptySlots,
    int InputBufferStacks,
    int OutputBufferStacks,
    decimal EstimatedDailyValue);
