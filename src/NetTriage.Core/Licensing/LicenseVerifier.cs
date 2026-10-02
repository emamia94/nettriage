using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetTriage.Core.Licensing;

/// <summary>
/// Verifies a signed licence file using only the embedded public key. No network, no activation
/// server, no account: this is the whole point, because the target audience runs .NET Framework
/// estates in environments that are frequently air-gapped.
/// </summary>
public static class LicenseVerifier
{
    /// <summary>Where a licence is looked for, in order, when no path is given explicitly.</summary>
    public const string EnvironmentVariable = "NETTRIAGE_LICENSE";

    public static IEnumerable<string> DefaultSearchPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, ".nettriage", "license.json");
        }

        yield return Path.Combine(Environment.CurrentDirectory, "nettriage-license.json");
    }

    public static LicenseCheck VerifyFile(string path)
    {
        if (!File.Exists(path)) return LicenseCheck.Invalid($"Licence file not found: {path}");

        try
        {
            return VerifyText(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return LicenseCheck.Invalid($"Could not read the licence file: {ex.Message}");
        }
    }

    public static LicenseCheck VerifyText(string json)
        => VerifyText(json, publicKeySpkiBase64: null, DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>
    /// The testable core. <paramref name="publicKeySpkiBase64"/> defaults to the key built into this
    /// binary; <paramref name="today"/> defaults to the clock. Both are injectable so expiry and
    /// signature behaviour can be tested without waiting for a calendar or shipping a second key.
    /// </summary>
    public static LicenseCheck VerifyText(string json, string? publicKeySpkiBase64, DateOnly today)
    {
        byte[] payload;
        byte[] signature;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var format = root.TryGetProperty("format", out var f) ? f.GetString() : null;
            if (format != LicenseCanonical.Format)
            {
                return LicenseCheck.Invalid(
                    $"Unrecognised licence format '{format}'. Expected '{LicenseCanonical.Format}'.");
            }

            var algorithm = root.TryGetProperty("algorithm", out var a) ? a.GetString() : null;
            if (algorithm != LicenseCanonical.Algorithm)
            {
                return LicenseCheck.Invalid(
                    $"Unsupported licence algorithm '{algorithm}'. Expected '{LicenseCanonical.Algorithm}'.");
            }

            payload = Convert.FromBase64String(root.GetProperty("payload").GetString() ?? "");
            signature = Convert.FromBase64String(root.GetProperty("signature").GetString() ?? "");
        }
        catch (Exception ex)
        {
            return LicenseCheck.Invalid($"The licence file is not valid JSON: {ex.Message}");
        }

        if (!SignatureIsValid(payload, signature, publicKeySpkiBase64 ?? LicenseKeys.PublicKeySpkiBase64))
        {
            return LicenseCheck.Invalid(
                "The licence signature did not verify against this build's public key. " +
                "The file has been altered, or it was issued for a different product key.");
        }

        var licence = LicenseCanonical.Deserialize(payload);
        if (licence is null)
        {
            return LicenseCheck.Invalid("The signed licence payload could not be read.");
        }

        if (licence.SchemaVersion > LicenseCanonical.CurrentSchema)
        {
            return LicenseCheck.Invalid(
                $"The licence uses schema {licence.SchemaVersion}, but this build understands up to " +
                $"{LicenseCanonical.CurrentSchema}. Upgrade the tool.");
        }

        if (licence.Issued > today)
        {
            return new LicenseCheck(
                LicenseStatus.NotYetValid, licence,
                $"This licence is not valid until {licence.Issued:yyyy-MM-dd}. Check the system clock.");
        }

        if (licence.Expires < today)
        {
            return new LicenseCheck(
                LicenseStatus.Expired, licence,
                $"The licence expired on {licence.Expires:yyyy-MM-dd}. Renew to keep the licensed features.");
        }

        return new LicenseCheck(LicenseStatus.Valid, licence, Describe(licence, today));
    }

    private static bool SignatureIsValid(byte[] payload, byte[] signature, string publicKeySpkiBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            return ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    private static string Describe(License licence, DateOnly today)
    {
        var days = licence.Expires.DayNumber - today.DayNumber;
        var who = string.IsNullOrWhiteSpace(licence.Organisation) ? licence.Holder : licence.Organisation;
        return $"Licensed to {who} ({licence.Edition} edition), valid for {days} more day(s).";
    }

    /// <summary>Finds and verifies a licence without being told where it is.</summary>
    public static LicenseCheck Locate(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return VerifyFile(explicitPath);

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return VerifyFile(fromEnvironment);

        foreach (var candidate in DefaultSearchPaths())
        {
            if (File.Exists(candidate)) return VerifyFile(candidate);
        }

        return LicenseCheck.Missing();
    }

    /// <summary>Builds the file that <see cref="VerifyText"/> consumes. Used by the issuer, and by tests.</summary>
    public static string Wrap(byte[] payload, byte[] signature)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", LicenseCanonical.Format);
            writer.WriteString("algorithm", LicenseCanonical.Algorithm);
            writer.WriteString("payload", Convert.ToBase64String(payload));
            writer.WriteString("signature", Convert.ToBase64String(signature));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }
}
