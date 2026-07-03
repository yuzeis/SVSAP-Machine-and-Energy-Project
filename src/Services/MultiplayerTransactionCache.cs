namespace SVSAPME.Services;

internal sealed class MultiplayerTransactionCache<TResponse>
{
    private readonly int limit;
    private readonly Dictionary<(long PlayerId, Guid TransactionId), TResponse> responses = new();
    private readonly Queue<(long PlayerId, Guid TransactionId)> order = new();

    public MultiplayerTransactionCache(int limit = 256)
    {
        this.limit = Math.Max(1, limit);
    }

    public int Count => this.responses.Count;

    public bool TryGet(long playerId, Guid transactionId, out TResponse response)
    {
        return this.responses.TryGetValue((playerId, transactionId), out response!);
    }

    public void Remember(long playerId, Guid transactionId, TResponse response)
    {
        var key = (playerId, transactionId);
        if (this.responses.ContainsKey(key))
        {
            this.responses[key] = response;
            return;
        }

        this.responses[key] = response;
        this.order.Enqueue(key);
        while (this.responses.Count > this.limit && this.order.Count > 0)
        {
            var evicted = this.order.Dequeue();
            this.responses.Remove(evicted);
        }
    }

    public void Clear()
    {
        this.responses.Clear();
        this.order.Clear();
    }
}
