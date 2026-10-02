using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetTriage.Core.Baseline;

/// <summary>Reads and writes baseline files. The JSON is stable, diffable and safe to commit.</summary>
public static class BaselineStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(Baseline baseline) => JsonSerializer.Serialize(baseline, Options) + "\n";

    public static void Save(Baseline baseline, string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(full, Serialize(baseline));
    }

    public static Baseline? Load(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var text = File.ReadAllText(path);

            // Baseline.Format has a default, so deserializing on its own would happily accept any
            // JSON object as an empty baseline - and then report every project as new. Require the
            // marker to be present in the file before trusting anything else in it.
            using (var document = JsonDocument.Parse(text))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

                if (!document.RootElement.TryGetProperty("format", out var format)
                    && !document.RootElement.TryGetProperty("Format", out format))
                {
                    return null;
                }

                if (format.ValueKind != JsonValueKind.String) return null;
                if (!string.Equals(format.GetString(), Baseline.CurrentFormat, StringComparison.Ordinal)) return null;
            }

            var baseline = JsonSerializer.Deserialize<Baseline>(text, Options);
            if (baseline is null) return null;

            foreach (var project in baseline.Projects)
            {
                project.Findings = new Dictionary<string, int>(project.Findings, StringComparer.Ordinal);
            }

            return baseline;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Convenience for callers that want a parse failure to be visible.</summary>
    public static bool TryLoad(string path, out Baseline baseline, out string error)
    {
        baseline = new Baseline();
        error = "";

        if (!File.Exists(path))
        {
            error = $"Baseline file not found: {path}";
            return false;
        }

        var loaded = Load(path);
        if (loaded is null)
        {
            error = $"'{path}' is not a readable nettriage baseline (expected format {Baseline.CurrentFormat}).";
            return false;
        }

        baseline = loaded;
        return true;
    }
}
