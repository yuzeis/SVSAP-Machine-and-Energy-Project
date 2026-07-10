using Koizumi.SVSAPME.Api;
using StardewModdingAPI;
using StardewValley;

namespace SVSAPME.Services;

internal sealed class EnergyTelemetryService
{
    private const int MaxEventsPerNetwork = 64;
    private readonly Dictionary<Guid, EnergyNetworkTelemetryState> networks = new();

    public void RecordDeposit(
        Guid networkId,
        string source,
        string reason,
        long requestedWh,
        long acceptedWh,
        SvsapmeEnergyErrorCode code,
        string message)
    {
        var state = this.GetState(networkId);
        state.ResetIfNewDay(GetCurrentDay());
        state.BeginTick(GetCurrentTick());
        var rejected = Math.Max(0, requestedWh - Math.Max(0, acceptedWh));
        state.CurrentGeneratedWh += Math.Max(0, acceptedWh);
        state.TodayGeneratedWh += Math.Max(0, acceptedWh);
        state.AddReason(source, reason, Math.Max(0, acceptedWh), generated: true);
        if (code != SvsapmeEnergyErrorCode.None || rejected > 0)
            state.LastWarning = string.IsNullOrWhiteSpace(message) ? code.ToString() : message;
        state.AddEvent(new EnergyTelemetryEventSnapshot(source, reason, Math.Max(0, acceptedWh), 0, rejected, code.ToString(), message));
    }

    public void RecordConsume(
        Guid networkId,
        string source,
        string reason,
        long requestedWh,
        long consumedWh,
        SvsapmeEnergyErrorCode code,
        string message)
    {
        var state = this.GetState(networkId);
        state.ResetIfNewDay(GetCurrentDay());
        state.BeginTick(GetCurrentTick());
        var rejected = Math.Max(0, requestedWh - Math.Max(0, consumedWh));
        state.CurrentConsumedWh += Math.Max(0, consumedWh);
        state.TodayConsumedWh += Math.Max(0, consumedWh);
        state.AddReason(source, reason, Math.Max(0, consumedWh), generated: false);
        if (code != SvsapmeEnergyErrorCode.None || rejected > 0)
            state.LastWarning = string.IsNullOrWhiteSpace(message) ? code.ToString() : message;
        state.AddEvent(new EnergyTelemetryEventSnapshot(source, reason, 0, Math.Max(0, consumedWh), rejected, code.ToString(), message));
    }

    public EnergyTelemetrySnapshot GetSnapshot(Guid networkId)
    {
        var state = this.GetState(networkId);
        state.ResetIfNewDay(GetCurrentDay());
        state.BeginTick(GetCurrentTick());
        return state.ToSnapshot();
    }

    private EnergyNetworkTelemetryState GetState(Guid networkId)
    {
        if (!this.networks.TryGetValue(networkId, out var state))
        {
            state = new EnergyNetworkTelemetryState(GetCurrentDay());
            this.networks[networkId] = state;
        }

        return state;
    }

    private static int GetCurrentDay()
    {
        return Context.IsWorldReady ? Game1.Date.TotalDays : 0;
    }

    private static int GetCurrentTick()
    {
        return Context.IsWorldReady ? Game1.ticks : 0;
    }

    private sealed class EnergyNetworkTelemetryState
    {
        private readonly Queue<EnergyTelemetryEventSnapshot> events = new();
        private readonly Dictionary<string, long> producers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> consumers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> deviceLabels = new(StringComparer.Ordinal);
        private int day;
        private int activeTick = -1;

        public EnergyNetworkTelemetryState(int day)
        {
            this.day = day;
        }

        public long LastGeneratedWh { get; set; }
        public long LastConsumedWh { get; set; }
        public long CurrentGeneratedWh { get; set; }
        public long CurrentConsumedWh { get; set; }
        public long TodayGeneratedWh { get; set; }
        public long TodayConsumedWh { get; set; }
        public string LastWarning { get; set; } = string.Empty;

        public void ResetIfNewDay(int currentDay)
        {
            if (currentDay == this.day)
                return;

            this.day = currentDay;
            this.LastGeneratedWh = 0;
            this.LastConsumedWh = 0;
            this.CurrentGeneratedWh = 0;
            this.CurrentConsumedWh = 0;
            this.TodayGeneratedWh = 0;
            this.TodayConsumedWh = 0;
            this.LastWarning = string.Empty;
            this.producers.Clear();
            this.consumers.Clear();
            this.deviceLabels.Clear();
            this.events.Clear();
            this.activeTick = -1;
        }

        public void BeginTick(int tick)
        {
            if (this.activeTick < 0)
            {
                this.activeTick = tick;
                return;
            }
            if (tick == this.activeTick)
                return;

            this.LastGeneratedWh = this.CurrentGeneratedWh;
            this.LastConsumedWh = this.CurrentConsumedWh;
            this.CurrentGeneratedWh = 0;
            this.CurrentConsumedWh = 0;
            this.activeTick = tick;
        }

        public void AddReason(string source, string reason, long wh, bool generated)
        {
            if (wh <= 0)
                return;

            var target = generated ? this.producers : this.consumers;
            var identity = GetDeviceIdentity(reason);
            var deviceId = string.IsNullOrWhiteSpace(identity) ? source : $"{source}:{identity}";
            target[deviceId] = target.GetValueOrDefault(deviceId) + wh;
            this.deviceLabels[deviceId] = string.IsNullOrWhiteSpace(identity) ? source : identity;
        }

        private static string GetDeviceIdentity(string reason)
        {
            var parts = reason.Split('|');
            return parts.Length >= 4 && string.Equals(parts[0], "machine", StringComparison.Ordinal)
                ? string.Join('|', parts.Take(3))
                : reason;
        }

        public void AddEvent(EnergyTelemetryEventSnapshot snapshot)
        {
            this.events.Enqueue(snapshot);
            while (this.events.Count > MaxEventsPerNetwork)
                this.events.Dequeue();
        }

        public EnergyTelemetrySnapshot ToSnapshot()
        {
            return new EnergyTelemetrySnapshot(
                this.LastGeneratedWh,
                this.LastConsumedWh,
                this.LastGeneratedWh - this.LastConsumedWh,
                this.TodayGeneratedWh,
                this.TodayConsumedWh,
                this.TodayGeneratedWh - this.TodayConsumedWh,
                this.Top(this.producers),
                this.Top(this.consumers),
                this.LastWarning,
                this.events.LastOrDefault());
        }

        private IReadOnlyList<EnergyTelemetryReasonTotal> Top(Dictionary<string, long> values)
        {
            return values
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(3)
                .Select(pair => new EnergyTelemetryReasonTotal(this.deviceLabels.GetValueOrDefault(pair.Key) ?? pair.Key, pair.Value, pair.Key))
                .ToList();
        }
    }
}

internal sealed record EnergyTelemetrySnapshot(
    long LastGeneratedWh,
    long LastConsumedWh,
    long LastNetWh,
    long TodayGeneratedWh,
    long TodayConsumedWh,
    long TodayNetWh,
    IReadOnlyList<EnergyTelemetryReasonTotal> TopProducers,
    IReadOnlyList<EnergyTelemetryReasonTotal> TopConsumers,
    string LastWarning,
    EnergyTelemetryEventSnapshot? LastEvent);

internal sealed record EnergyTelemetryReasonTotal(string Reason, long Wh, string DeviceId = "");

internal sealed record EnergyTelemetryEventSnapshot(
    string Source,
    string Reason,
    long GeneratedWh,
    long ConsumedWh,
    long RejectedWh,
    string Code,
    string Message);
