using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetTriage.Core.Estate;

public sealed class AppSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Bucket { get; set; } = "";
    public int Projects { get; set; }
    public int Blockers { get; set; }
    public int Warnings { get; set; }
    public int Info { get; set; }
    public int SourceLines { get; set; }
    public int ProjectsOutOfSupport { get; set; }
    public double EffortHighDays { get; set; }
}

public sealed class EstateSnapshot
{
    public const string CurrentFormat = "nettriage-estate-history/1";

    public string Format { get; set; } = CurrentFormat;
    public string ToolVersion { get; set; } = "";
    public DateTimeOffset TakenAt { get; set; }
    public string Root { get; set; } = "";
    public List<AppSnapshot> Apps { get; set; } = new();
}

public sealed class EstateHistory
{
    public string Format { get; set; } = EstateSnapshot.CurrentFormat;
    public List<EstateSnapshot> Snapshots { get; set; } = new();
}

/// <summary>
/// A committed file holding one snapshot per run. This is what turns a scan into a trend: the
/// question a lead actually asks is not "how bad is it" but "is it getting better".
/// </summary>
public static class EstateSnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static EstateSnapshot Capture(EstateReport report) => new()
    {
        Format = EstateSnapshot.CurrentFormat,
        ToolVersion = report.ToolVersion,
        TakenAt = report.GeneratedAt,
        Root = report.Root,
        Apps = report.Apps
            .OrderBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new AppSnapshot
            {
                Id = a.Id,
                Name = a.Name,
                Bucket = a.Report.Summary.Red > 0 ? "red"
                    : a.Report.Summary.Yellow > 0 ? "yellow" : "green",
                Projects = a.Report.Summary.ProjectCount,
                Blockers = a.Report.Summary.BlockerFindings,
                Warnings = a.Report.Summary.WarningFindings,
                Info = a.Report.Summary.InfoFindings,
                SourceLines = a.Report.Summary.TotalSourceLines,
                ProjectsOutOfSupport = a.Report.Summary.ProjectsOutOfSupport,
                EffortHighDays = Math.Round(a.Report.Summary.EffortHighDays, 1),
            })
            .ToList(),
    };

    public static EstateHistory Load(string path)
    {
        if (!File.Exists(path)) return new EstateHistory();

        try
        {
            var history = JsonSerializer.Deserialize<EstateHistory>(File.ReadAllText(path), Options);
            return history ?? new EstateHistory();
        }
        catch
        {
            // A corrupt history must not stop a scan; it starts a fresh one.
            return new EstateHistory();
        }
    }

    public static void Append(EstateSnapshot snapshot, string path, int keepLast = 100)
    {
        var history = Load(path);
        history.Snapshots.Add(snapshot);

        if (history.Snapshots.Count > keepLast)
        {
            history.Snapshots = history.Snapshots.Skip(history.Snapshots.Count - keepLast).ToList();
        }

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(full, JsonSerializer.Serialize(history, Options) + "\n");
    }

    /// <summary>Compares the newest snapshot with the one before it. Null when there is no history yet.</summary>
    public static EstateTrend? BuildTrend(IList<EstateSnapshot> history)
    {
        if (history.Count < 2) return null;

        var previous = history[^2];
        var current = history[^1];

        var trend = new EstateTrend
        {
            PreviousTakenAt = previous.TakenAt,
            CurrentTakenAt = current.TakenAt,
        };

        var before = previous.Apps.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var now = current.Apps.ToDictionary(a => a.Id, StringComparer.Ordinal);

        foreach (var app in current.Apps)
        {
            before.TryGetValue(app.Id, out var was);
            trend.Entries.Add(new EstateTrendEntry(
                app.Id,
                app.Name,
                was?.Blockers ?? 0,
                app.Blockers,
                was?.EffortHighDays ?? 0,
                app.EffortHighDays));
        }

        return trend;
    }

    public static EstateTrend? BuildTrend(string historyPath) => BuildTrend(Load(historyPath).Snapshots);
}
