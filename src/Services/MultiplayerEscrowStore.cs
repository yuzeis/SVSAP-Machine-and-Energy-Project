namespace SVSAPME.Services;

internal sealed class MultiplayerEscrowStore<TItem>
    where TItem : class
{
    private readonly Dictionary<Guid, TItem> pending = new();

    public int Count => this.pending.Count;

    public bool Track(Guid transactionId, TItem item)
    {
        if (transactionId == Guid.Empty)
            return false;

        this.pending[transactionId] = item;
        return true;
    }

    public bool Resolve(Guid transactionId, bool restore, Action<TItem> restoreItem)
    {
        if (!this.pending.Remove(transactionId, out var item))
            return false;

        if (restore)
            restoreItem(item);

        return true;
    }

    public int RestoreAll(Action<TItem> restoreItem)
    {
        var items = this.pending.Values.ToList();
        this.pending.Clear();
        foreach (var item in items)
            restoreItem(item);

        return items.Count;
    }

    public void ClearWithoutRestore()
    {
        this.pending.Clear();
    }
}
