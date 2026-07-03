using SVSAPME.Services;

namespace Koizumi.SVSAPME.Api;

public sealed class SvsapmeEnergyApi : ISvsapmeEnergyApi
{
    private readonly EnergyNetworkManager energy;

    internal SvsapmeEnergyApi(EnergyNetworkManager energy)
    {
        this.energy = energy;
    }

    public int ApiVersion => 1;

    public bool IsHostAuthority => this.energy.IsHostAuthority;

    public bool TryGetNetworkEnergy(Guid svsapNetworkId, out long storedWh, out long capacityWh, out SvsapmeEnergyErrorCode code)
    {
        return this.energy.TryGetNetworkEnergy(svsapNetworkId, out storedWh, out capacityWh, out code);
    }

    public bool TryDepositWh(
        Guid svsapNetworkId,
        long amountWh,
        string ownerModId,
        string reason,
        out long acceptedWh,
        out SvsapmeEnergyErrorCode code,
        out string message)
    {
        return this.energy.TryDepositWh(svsapNetworkId, amountWh, ownerModId, reason, out acceptedWh, out code, out message);
    }

    public bool TryConsumeWh(
        Guid svsapNetworkId,
        long amountWh,
        string ownerModId,
        string reason,
        bool allowPartial,
        out long consumedWh,
        out SvsapmeEnergyErrorCode code,
        out string message)
    {
        return this.energy.TryConsumeWh(svsapNetworkId, amountWh, ownerModId, reason, allowPartial, out consumedWh, out code, out message);
    }
}
