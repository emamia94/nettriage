using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NetTriage.Core.Licensing;

namespace NetTriage.Fulfilment;

/// <summary>
/// Mints a licence for one order. The private key is read from disk on every run and never
/// cached, copied or logged.
/// </summary>
internal sealed class LicenceFactory(string privateKeyPath, string edition)
{
    /// <summary>
    /// The licence id is derived from the order and the period rather than drawn at random, so
    /// re-running a run after a crash reproduces byte-identical output. A duplicate email then
    /// carries the same licence the customer already has, instead of a second one that silently
    /// invalidates nothing but confuses everyone.
    /// </summary>
    public static string DeriveLicenceId(string orderId, DateOnly expires)
    {
        var material = Encoding.UTF8.GetBytes($"{orderId}:{expires:yyyy-MM-dd}");
        var digest = SHA256.HashData(material);
        return Convert.ToHexString(digest)[..12].ToLowerInvariant();
    }

    public IssuedLicence Issue(string orderId, string holder, string organisation, DateOnly expires)
    {
        if (!File.Exists(privateKeyPath))
            throw new FulfilmentException($"Signing key not found: {privateKeyPath}");

        if (string.IsNullOrWhiteSpace(holder))
            throw new FulfilmentException("Refusing to issue a licence with no holder.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (expires <= today)
            throw new FulfilmentException($"Refusing to issue a licence that expires today or earlier ({expires:yyyy-MM-dd}).");

        var licence = new License(
            LicenseCanonical.CurrentSchema,
            DeriveLicenceId(orderId, expires),
            holder,
            organisation,
            edition,
            today,
            expires,
            Editions.TeamFeatures);

        var payload = LicenseCanonical.Serialize(licence);

        byte[] signature;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(privateKeyPath));
            signature = key.SignData(payload, HashAlgorithmName.SHA256);
        }
        catch (Exception ex)
        {
            throw new FulfilmentException($"Could not use the signing key at {privateKeyPath}: {ex.Message}");
        }

        var file = LicenseVerifier.Wrap(payload, signature);

        // Never send a licence the shipped verifier would reject.
        var check = LicenseVerifier.VerifyText(file);
        if (check.Status is not LicenseStatus.Valid)
            throw new FulfilmentException($"Refusing to send an invalid licence for order {orderId}: {check.Message}");

        return new IssuedLicence(licence, file);
    }
}

internal sealed record IssuedLicence(License Licence, string Json)
{
    public string FileName => $"nettriage-{Licence.LicenseId}.json";

    public string ExpiresText => Licence.Expires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
