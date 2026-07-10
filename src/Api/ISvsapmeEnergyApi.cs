namespace Koizumi.SVSAPME.Api;

public enum SvsapmeEnergyErrorCode
{
    None = 0,
    NotHost = 1,
    NetworkUnknown = 2,
    InsufficientEnergy = 3,
    StorageFull = 4,
    SubsystemDisabled = 5,
    InternalError = 6,
    NoEnergyCell = 7
}

public interface ISvsapmeEnergyApi
{
    int ApiVersion { get; }
    bool IsHostAuthority { get; }

    bool TryGetNetworkEnergy(Guid svsapNetworkId, out long storedWh, out long capacityWh, out SvsapmeEnergyErrorCode code);

    bool TryDepositWh(
        Guid svsapNetworkId,
        long amountWh,
        string ownerModId,
        string reason,
        out long acceptedWh,
        out SvsapmeEnergyErrorCode code,
        out string message);

    bool TryConsumeWh(
        Guid svsapNetworkId,
        long amountWh,
        string ownerModId,
        string reason,
        bool allowPartial,
        out long consumedWh,
        out SvsapmeEnergyErrorCode code,
        out string message);
}
