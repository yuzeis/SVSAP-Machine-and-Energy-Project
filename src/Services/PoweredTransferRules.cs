namespace SVSAPME.Services;

internal enum PoweredMachineTier
{
    Copper,
    Steel,
    Gold,
    Iridium
}

internal enum PoweredTransferRunMode
{
    None,
    Powered,
    Prototype
}

internal static class PoweredTransferRules
{
    public static int GetPoweredThroughput(PoweredMachineTier tier)
    {
        return tier switch
        {
            PoweredMachineTier.Copper => 16,
            PoweredMachineTier.Steel => 64,
            PoweredMachineTier.Gold => 256,
            PoweredMachineTier.Iridium => 1024,
            _ => 16
        };
    }

    public static int GetEffectivePoweredThroughput(PoweredMachineTier tier, int prototypeThroughput)
    {
        return Math.Max(GetPoweredThroughput(tier), Math.Max(0, prototypeThroughput));
    }

    public static PoweredTransferPlan PlanImporterExporter(
        int sourceAvailable,
        int targetCapacity,
        PoweredMachineTier tier,
        long storedWh,
        int halfWhCredit,
        int prototypeThroughput,
        int poweredThroughputMultiplier = 1)
    {
        var multiplier = Math.Clamp(poweredThroughputMultiplier, 1, 2);
        var acceleratedItems = Math.Min(
            Math.Min(Math.Max(0, sourceAvailable), Math.Max(0, targetCapacity)),
            GetEffectivePoweredThroughput(tier, prototypeThroughput) * multiplier);
        if (acceleratedItems <= 0)
            return PoweredTransferPlan.None;

        var payment = CreatePayment(acceleratedItems, halfWhCredit);
        if (storedWh >= payment.WhToConsume)
        {
            return new PoweredTransferPlan(
                PoweredTransferRunMode.Powered,
                acceleratedItems,
                payment.RequiredHalfWh,
                payment.WhToConsume,
                payment.CreditAfterPrepay);
        }

        var prototypeItems = Math.Min(Math.Min(Math.Max(0, sourceAvailable), Math.Max(0, targetCapacity)), Math.Max(0, prototypeThroughput));
        return prototypeItems > 0
            ? new PoweredTransferPlan(PoweredTransferRunMode.Prototype, prototypeItems, 0, 0, Math.Clamp(halfWhCredit, 0, 1))
            : PoweredTransferPlan.None;
    }

    public static PoweredTransferPayment CreatePayment(int plannedItems, int halfWhCredit)
    {
        var normalizedCredit = Math.Clamp(halfWhCredit, 0, 1);
        var requiredHalfWh = Math.Max(0, plannedItems);
        var payableHalfWh = Math.Max(0, requiredHalfWh - normalizedCredit);
        var whToConsume = (payableHalfWh + 1) / 2;
        var creditAfterPrepay = whToConsume * 2 - payableHalfWh;
        return new PoweredTransferPayment(requiredHalfWh, whToConsume, creditAfterPrepay);
    }

    public static PoweredTransferRefund CalculateRefund(int plannedItems, int actualMoved, int creditAfterPrepay)
    {
        var unusedHalfWh = Math.Max(0, plannedItems - Math.Max(0, actualMoved)) + Math.Clamp(creditAfterPrepay, 0, 1);
        return new PoweredTransferRefund(unusedHalfWh / 2, unusedHalfWh % 2);
    }
}

internal readonly record struct PoweredTransferPlan(
    PoweredTransferRunMode Mode,
    int PlannedItems,
    int RequiredHalfWh,
    int WhToConsume,
    int CreditAfterPrepay)
{
    public static readonly PoweredTransferPlan None = new(PoweredTransferRunMode.None, 0, 0, 0, 0);
}

internal readonly record struct PoweredTransferPayment(int RequiredHalfWh, int WhToConsume, int CreditAfterPrepay);

internal readonly record struct PoweredTransferRefund(int RefundWh, int FinalHalfWhCredit);
