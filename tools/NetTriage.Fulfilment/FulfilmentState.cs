using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetTriage.Fulfilment;

/// <summary>
/// What the run remembers between invocations: how far it has read, and which orders it has
/// already dealt with. This file is the difference between "runs every fifteen minutes" and
/// "emails the customer every fifteen minutes".
/// </summary>
internal sealed class FulfilmentState
{
    /// <summary>Created-at of the newest order fully processed. Orders at or before this are not re-read.</summary>
    public DateTimeOffset? Cursor { get; set; }

    /// <summary>Order id to what was issued for it. Never pruned: it is the audit trail.</summary>
    public Dictionary<string, ProcessedOrder> Processed { get; set; } = new(StringComparer.Ordinal);

    public List<string> RecentFailures { get; set; } = [];

    private static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static FulfilmentState Load(string path)
    {
        if (!File.Exists(path)) return new FulfilmentState();

        try
        {
            return JsonSerializer.Deserialize<FulfilmentState>(File.ReadAllText(path), Read) ?? new FulfilmentState();
        }
        catch (JsonException ex)
        {
            throw new FulfilmentException(
                $"{path} is not readable as state: {ex.Message}. Refusing to continue, because starting from " +
                "an empty state would re-issue and re-send every licence in the account. Move the file aside to reset deliberately.");
        }
    }

    /// <summary>
    /// Written atomically, after every single order rather than at the end of the run: a crash
    /// halfway through must not cause the orders already emailed to be emailed again.
    /// </summary>
    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Write));
        File.Move(temporary, path, overwrite: true);
    }

    public void RecordFailure(string message)
    {
        RecentFailures.Insert(0, $"{DateTimeOffset.UtcNow:u}  {message}");
        if (RecentFailures.Count > 50) RecentFailures.RemoveRange(50, RecentFailures.Count - 50);
    }
}

internal sealed class ProcessedOrder
{
    public string OrderId { get; set; } = "";
    public string LicenceId { get; set; } = "";
    public string Holder { get; set; } = "";
    public string Organisation { get; set; } = "";
    public string Email { get; set; } = "";
    public string Expires { get; set; } = "";
    public string Kind { get; set; } = "";
    public string LicenceFile { get; set; } = "";
    public DateTimeOffset FulfilledAt { get; set; }
    public int SendCount { get; set; }

    [JsonIgnore]
    public bool IsRenewal => Kind == "renewal";
}
