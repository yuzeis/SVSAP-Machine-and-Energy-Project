using SVSAPME.Models;

namespace SVSAPME.Services;

internal static class ExecutedMachineActionLedger
{
    public const int MaxEntries = 4096;

    public static bool TryGet(
        IReadOnlyList<ExecutedMachineAction> entries,
        long playerId,
        Guid transactionId,
        out ExecutedMachineAction entry)
    {
        entry = entries.FirstOrDefault(candidate =>
            candidate.PlayerId == playerId && candidate.TransactionId == transactionId)!;
        return entry is not null;
    }

    public static bool Remember(List<ExecutedMachineAction> entries, ExecutedMachineAction entry)
    {
        if (entry.PlayerId <= 0
            || entry.TransactionId == Guid.Empty
            || entry.MachineGuid == Guid.Empty
            || string.IsNullOrWhiteSpace(entry.ActionKind)
            || !entry.ConsumeEscrowedItem
            || TryGet(entries, entry.PlayerId, entry.TransactionId, out _))
        {
            return false;
        }

        entries.Add(entry);
        while (entries.Count > MaxEntries)
            entries.RemoveAt(0);
        return true;
    }

    public static bool Normalize(List<ExecutedMachineAction> entries)
    {
        var changed = false;
        var seen = new HashSet<(long PlayerId, Guid TransactionId)>();
        for (var index = 0; index < entries.Count;)
        {
            var entry = entries[index];
            if (entry is null
                || entry.PlayerId <= 0
                || entry.TransactionId == Guid.Empty
                || entry.MachineGuid == Guid.Empty
                || string.IsNullOrWhiteSpace(entry.ActionKind)
                || !entry.ConsumeEscrowedItem
                || !seen.Add((entry.PlayerId, entry.TransactionId)))
            {
                entries.RemoveAt(index);
                changed = true;
                continue;
            }

            entry.ActionKind ??= string.Empty;
            entry.Message ??= string.Empty;
            entry.ReturnedItems ??= new();
            index++;
        }

        while (entries.Count > MaxEntries)
        {
            entries.RemoveAt(0);
            changed = true;
        }

        return changed;
    }
}
