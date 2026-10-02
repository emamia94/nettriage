using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetTriage.Fulfilment;

/// <summary>
/// The slice of the Polar API this tool needs. Read-only apart from marking an order as
/// fulfilled, and deliberately narrow: it fetches paid orders, and it never touches money.
/// </summary>
internal sealed class PolarClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public PolarClient(string baseUrl, string token)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("nettriage-fulfilment");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Paid orders for one product, oldest first. Filtering by product here rather than in the
    /// caller is what stops a test purchase or a second product from ever being fulfilled.
    /// </summary>
    public async Task<List<PolarOrder>> ListPaidOrdersAsync(string productId, DateTimeOffset? createdAfter, CancellationToken ct)
    {
        var orders = new List<PolarOrder>();
        var page = 1;

        while (true)
        {
            var query = new List<string>
            {
                "limit=100",
                $"page={page}",
                "status=paid",
                $"product_id={Uri.EscapeDataString(productId)}",
                "sorting=-created_at",
            };

            if (createdAfter is { } after)
            {
                query.Add($"created_after={Uri.EscapeDataString(after.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}");
            }

            var url = $"{_baseUrl}/v1/orders/?{string.Join('&', query)}";
            var envelope = await GetAsync<PolarPage<PolarOrder>>(url, ct) ?? new PolarPage<PolarOrder>();

            orders.AddRange(envelope.Items);

            var pagination = envelope.Pagination;
            if (pagination is null || page >= pagination.MaxPage || envelope.Items.Count == 0) break;
            page++;
        }

        // Ascending, so a licence is never minted out of order and the cursor always moves forward.
        orders.Reverse();
        return orders;
    }

    public async Task<PolarSubscription?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        try
        {
            return await GetAsync<PolarSubscription>($"{_baseUrl}/v1/subscriptions/{Uri.EscapeDataString(subscriptionId)}", ct);
        }
        catch (FulfilmentException ex) when (ex.Message.Contains("404"))
        {
            // A one-off purchase, or a subscription that no longer exists. Not an error.
            return null;
        }
    }

    /// <summary>
    /// Records our own bookkeeping on the order. This is a write, but only ever of our metadata:
    /// no amount, status or customer field is touched. It exists so that an operator looking at
    /// the Polar dashboard can see which orders have been dealt with, without needing our state.
    /// </summary>
    public async Task MarkFulfilledAsync(string orderId, string licenceId, string expires, CancellationToken ct)
    {
        var payload = new
        {
            metadata = new Dictionary<string, object>
            {
                ["nettriage_licence_id"] = licenceId,
                ["nettriage_expires"] = expires,
                ["nettriage_fulfilled_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Patch, $"{_baseUrl}/v1/orders/{Uri.EscapeDataString(orderId)}")
        {
            Content = JsonContent.Create(payload),
        };

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new FulfilmentException(
                $"Could not mark order {orderId} as fulfilled: HTTP {(int)response.StatusCode} {Truncate(body)}");
        }
    }

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new FulfilmentException($"GET {url} failed: HTTP {(int)response.StatusCode} {Truncate(body)}");
        }

        return JsonSerializer.Deserialize<T>(body, Read);
    }

    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300] + "…";

    public void Dispose() => _http.Dispose();
}

internal sealed class PolarPage<T>
{
    [JsonPropertyName("items")] public List<T> Items { get; init; } = [];
    [JsonPropertyName("pagination")] public PolarPagination? Pagination { get; init; }
}

internal sealed class PolarPagination
{
    [JsonPropertyName("total_count")] public int TotalCount { get; init; }
    [JsonPropertyName("max_page")] public int MaxPage { get; init; }
}

/// <summary>The fields of a Polar order this tool reads. Everything else is ignored.</summary>
internal sealed class PolarOrder
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("paid")] public bool Paid { get; init; }
    [JsonPropertyName("billing_reason")] public string BillingReason { get; init; } = "";
    [JsonPropertyName("subscription_id")] public string? SubscriptionId { get; init; }
    [JsonPropertyName("customer_id")] public string CustomerId { get; init; } = "";
    [JsonPropertyName("customer")] public PolarCustomer? Customer { get; init; }
    [JsonPropertyName("custom_field_data")] public Dictionary<string, JsonElement>? CustomFieldData { get; init; }

    public bool IsFirstPayment => BillingReason is "purchase" or "subscription_create";

    public bool IsRenewal => BillingReason is "subscription_cycle";

    /// <summary>The organisation name the buyer typed at checkout, which goes on the licence.</summary>
    public string OrganisationFromCheckout()
    {
        if (CustomFieldData is null) return "";

        foreach (var value in CustomFieldData.Values)
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number => value.ToString(),
                _ => "",
            };

            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }

        return "";
    }
}

internal sealed class PolarCustomer
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("billing_name")] public string? BillingName { get; init; }
}

internal sealed class PolarSubscription
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("current_period_end")] public DateTimeOffset? CurrentPeriodEnd { get; init; }
    [JsonPropertyName("cancel_at_period_end")] public bool CancelAtPeriodEnd { get; init; }
}
