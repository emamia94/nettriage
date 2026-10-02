using System.Globalization;
using System.Text.Json;

namespace NetTriage.Core.Licensing;

/// <summary>Feature keys that a licence can grant. Free features are never listed here.</summary>
public static class Features
{
    public const string Baseline = "baseline";
    public const string Estate = "estate";
    public const string Rules = "rules";
    public const string Exports = "exports";

    public static readonly IReadOnlyList<string> All = new[] { Baseline, Estate, Rules, Exports };

    public static string Describe(string feature) => feature switch
    {
        Baseline => "baseline & drift gate",
        Estate => "estate roll-up",
        Rules => "custom rules",
        Exports => "organisation exports",
        _ => feature,
    };
}

public static class Editions
{
    public const string Trial = "trial";
    public const string Team = "team";
    public const string Site = "site";

    /// <summary>What a freshly issued trial gets: everything, for a limited time.</summary>
    public static IReadOnlyList<string> TrialFeatures => Features.All;

    public static IReadOnlyList<string> TeamFeatures => Features.All;

    public static IReadOnlyList<string> SiteFeatures => Features.All;
}

/// <summary>The signed claims. Everything here is covered by the signature.</summary>
public sealed record License(
    int SchemaVersion,
    string LicenseId,
    string Holder,
    string Organisation,
    string Edition,
    DateOnly Issued,
    DateOnly Expires,
    IReadOnlyList<string> Features);

public enum LicenseStatus
{
    /// <summary>No licence file was found. The free feature set still works.</summary>
    Missing,
    Valid,
    Expired,
    /// <summary>Not valid yet - the issue date is in the future.</summary>
    NotYetValid,
    /// <summary>The file could not be parsed, or the signature did not verify.</summary>
    Invalid,
}

public sealed record LicenseCheck(LicenseStatus Status, License? License, string Message)
{
    public static LicenseCheck Missing() => new(
        LicenseStatus.Missing, null,
        "No licence file found. Running with the free feature set.");

    public static LicenseCheck Invalid(string reason) => new(LicenseStatus.Invalid, null, reason);
}

/// <summary>
/// Canonical serialisation of the licence claims. The signature covers exactly these bytes, so
/// the field order and the date format are part of the format and must never change.
/// </summary>
public static class LicenseCanonical
{
    public const string Format = "nettriage-license/1";
    public const string Algorithm = "ECDSA-P256-SHA256";
    public const int CurrentSchema = 1;

    public static byte[] Serialize(License licence)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", licence.SchemaVersion);
            writer.WriteString("licenseId", licence.LicenseId);
            writer.WriteString("holder", licence.Holder);
            writer.WriteString("organisation", licence.Organisation);
            writer.WriteString("edition", licence.Edition);
            writer.WriteString("issued", licence.Issued.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteString("expires", licence.Expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            writer.WriteStartArray("features");
            foreach (var feature in licence.Features.OrderBy(f => f, StringComparer.Ordinal))
            {
                writer.WriteStringValue(feature);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static License? Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            var root = document.RootElement;

            var features = new List<string>();
            if (root.TryGetProperty("features", out var featureArray)
                && featureArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in featureArray.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) features.Add(value);
                }
            }

            return new License(
                root.TryGetProperty("schemaVersion", out var schema) ? schema.GetInt32() : 1,
                root.GetProperty("licenseId").GetString() ?? "",
                root.GetProperty("holder").GetString() ?? "",
                root.TryGetProperty("organisation", out var org) ? org.GetString() ?? "" : "",
                root.TryGetProperty("edition", out var edition) ? edition.GetString() ?? "" : "",
                ParseDate(root.GetProperty("issued").GetString()),
                ParseDate(root.GetProperty("expires").GetString()),
                features);
        }
        catch
        {
            return null;
        }
    }

    private static DateOnly ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateOnly.MinValue;
}
