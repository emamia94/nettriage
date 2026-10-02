using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetTriage.Fulfilment;

/// <summary>
/// A recorded set of orders, in Polar's own JSON shape, so the entire fulfilment path — minting,
/// email, attachment, state, idempotency — can be exercised without a live sale. It exists because
/// the one thing that must not be discovered in production is that the fulfilment path does not
/// work, and because a support re-send is easier to reason about against a known input.
/// </summary>
internal sealed class ReplayFile
{
    [JsonPropertyName("orders")] public List<PolarOrder> Orders { get; init; } = [];
    [JsonPropertyName("subscriptions")] public List<PolarSubscription> Subscriptions { get; init; } = [];

    private static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ReplayFile Load(string path)
    {
        if (!File.Exists(path)) throw new FulfilmentException($"Replay file not found: {path}");

        try
        {
            var replay = JsonSerializer.Deserialize<ReplayFile>(File.ReadAllText(path), Read);
            if (replay is null) throw new FulfilmentException($"{path} is empty.");
            if (replay.Orders.Count == 0) throw new FulfilmentException($"{path} contains no orders.");
            return replay;
        }
        catch (JsonException ex)
        {
            throw new FulfilmentException($"{path} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Stands in for the live subscription lookup, using the periods recorded in the file.</summary>
    public Task<PolarSubscription?> LookupSubscriptionAsync(string subscriptionId, CancellationToken ct) =>
        Task.FromResult(Subscriptions.FirstOrDefault(s => s.Id == subscriptionId));
}
