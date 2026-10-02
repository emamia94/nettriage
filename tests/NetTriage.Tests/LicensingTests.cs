using System.Security.Cryptography;
using NetTriage.Core.Licensing;
using Xunit;

namespace NetTriage.Tests;

/// <summary>
/// The licence is the one thing a customer cannot verify by reading the output, so its behaviour is
/// pinned down here: a valid signature must be accepted, and every way of getting around it must not.
/// </summary>
public class LicensingTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static (string PrivatePem, string PublicSpki) NewKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    private static License ALicence(
        DateOnly? issued = null, DateOnly? expires = null, IReadOnlyList<string>? features = null) =>
        new(
            LicenseCanonical.CurrentSchema,
            "test00000001",
            "Ema",
            "Studio Ema",
            Editions.Team,
            issued ?? Today.AddDays(-1),
            expires ?? Today.AddDays(365),
            (features ?? Features.All).ToList());

    private static string Sign(License licence, string privatePem)
    {
        var payload = LicenseCanonical.Serialize(licence);
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        return LicenseVerifier.Wrap(payload, key.SignData(payload, HashAlgorithmName.SHA256));
    }

    private static LicenseCheck Verify(string file, string publicSpki) =>
        LicenseVerifier.VerifyText(file, publicSpki, Today);

    [Fact]
    public void AProperlySignedLicenceIsValid()
    {
        var (privatePem, publicSpki) = NewKey();

        var check = Verify(Sign(ALicence(), privatePem), publicSpki);

        Assert.Equal(LicenseStatus.Valid, check.Status);
        Assert.Equal("Ema", check.License!.Holder);
        Assert.Contains("365", check.Message);
    }

    [Fact]
    public void EditingThePayloadWithoutResigningIsRejected()
    {
        var (privatePem, publicSpki) = NewKey();
        var licence = ALicence();

        // Sign the honest licence, then swap in a more generous one under the same signature.
        var payload = LicenseCanonical.Serialize(licence);
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256);

        var forged = LicenseCanonical.Serialize(licence with { Expires = Today.AddYears(50) });
        var file = LicenseVerifier.Wrap(forged, signature);

        var check = Verify(file, publicSpki);

        Assert.Equal(LicenseStatus.Invalid, check.Status);
        Assert.Contains("signature", check.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ALicenceSignedBySomebodyElsesKeyIsRejected()
    {
        var (_, publicSpki) = NewKey();
        var (attackerPrivate, _) = NewKey();

        var check = Verify(Sign(ALicence(), attackerPrivate), publicSpki);

        Assert.Equal(LicenseStatus.Invalid, check.Status);
    }

    [Fact]
    public void AnExpiredLicenceIsExpiredAndGrantsNothing()
    {
        var (privatePem, publicSpki) = NewKey();
        var file = Sign(ALicence(expires: Today.AddDays(-1)), privatePem);

        var check = Verify(file, publicSpki);

        Assert.Equal(LicenseStatus.Expired, check.Status);
        Assert.False(Entitlements.From(check).IsLicensed);
        Assert.False(Entitlements.From(check).Allows(Features.Baseline));
    }

    [Fact]
    public void ALicenceThatStartsInTheFutureIsNotYetValid()
    {
        var (privatePem, publicSpki) = NewKey();
        var file = Sign(ALicence(issued: Today.AddDays(7), expires: Today.AddYears(1)), privatePem);

        var check = Verify(file, publicSpki);

        Assert.Equal(LicenseStatus.NotYetValid, check.Status);
        Assert.False(Entitlements.From(check).Allows(Features.Estate));
    }

    [Fact]
    public void ExpiryOnTheLastValidDayStillCounts()
    {
        var (privatePem, publicSpki) = NewKey();

        var check = Verify(Sign(ALicence(expires: Today), privatePem), publicSpki);

        Assert.Equal(LicenseStatus.Valid, check.Status);
    }

    [Fact]
    public void JunkIsRejectedWithAReadableReason()
    {
        var (_, publicSpki) = NewKey();

        Assert.Equal(LicenseStatus.Invalid, Verify("not json at all", publicSpki).Status);
        Assert.Equal(LicenseStatus.Invalid, Verify("{}", publicSpki).Status);
        Assert.Equal(LicenseStatus.Invalid,
            Verify("""{"format":"nettriage-license/1","algorithm":"ES256","payload":"!!!","signature":"!!!"}""",
                publicSpki).Status);
    }

    [Fact]
    public void AMissingLicenceGrantsNothingAndSaysSo()
    {
        var entitlements = Entitlements.From(LicenseCheck.Missing());

        Assert.False(entitlements.IsLicensed);
        Assert.All(Features.All, feature => Assert.False(entitlements.Allows(feature)));
        Assert.Contains("No licence", entitlements.Check.Message);
    }

    [Fact]
    public void EntitlementsGrantExactlyTheLicensedFeatures()
    {
        var (privatePem, publicSpki) = NewKey();
        var file = Sign(ALicence(features: new[] { Features.Baseline }), privatePem);

        var entitlements = Entitlements.From(Verify(file, publicSpki));

        Assert.True(entitlements.Allows(Features.Baseline));
        Assert.False(entitlements.Allows(Features.Estate));
        Assert.False(entitlements.Allows(Features.Rules));
        Assert.False(entitlements.Allows(Features.Exports));
        Assert.Equal(new[] { Features.Baseline }, entitlements.Granted);
    }

    [Fact]
    public void AnInventedFeatureNameIsCarriedButGrantsNothingReal()
    {
        var (privatePem, publicSpki) = NewKey();
        var file = Sign(ALicence(features: new[] { "baseline", "unlimited-everything" }), privatePem);

        var entitlements = Entitlements.From(Verify(file, publicSpki));

        Assert.True(entitlements.Allows(Features.Baseline));
        Assert.False(entitlements.Allows(Features.Estate));
        Assert.False(entitlements.Allows(Features.Exports));
    }

    [Fact]
    public void TheRefusalMessageNamesTheFeatureAndWhatStillWorks()
    {
        var entitlements = Entitlements.From(LicenseCheck.Missing());

        var message = entitlements.RefusalMessage(Features.Baseline);

        Assert.Contains("baseline", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("keeps working", message);
        Assert.Contains("license install", message);
    }

    [Fact]
    public void CanonicalSerializationIsByteStable()
    {
        // The signature covers these bytes, so two serializations of equal licences must be identical.
        var licence = ALicence();

        Assert.Equal(LicenseCanonical.Serialize(licence), LicenseCanonical.Serialize(licence));

        var roundTripped = LicenseCanonical.Deserialize(LicenseCanonical.Serialize(licence));

        Assert.NotNull(roundTripped);
        Assert.Equal(licence.SchemaVersion, roundTripped!.SchemaVersion);
        Assert.Equal(licence.LicenseId, roundTripped.LicenseId);
        Assert.Equal(licence.Holder, roundTripped.Holder);
        Assert.Equal(licence.Organisation, roundTripped.Organisation);
        Assert.Equal(licence.Edition, roundTripped.Edition);
        Assert.Equal(licence.Issued, roundTripped.Issued);
        Assert.Equal(licence.Expires, roundTripped.Expires);
        // Serialization sorts the feature list so the signature is stable, so compare as sets.
        Assert.Equal(licence.Features.OrderBy(f => f, StringComparer.Ordinal),
            roundTripped.Features.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Fact]
    public void FeatureOrderDoesNotChangeTheSignature()
    {
        var a = ALicence(features: new[] { Features.Baseline, Features.Estate });
        var b = ALicence(features: new[] { Features.Estate, Features.Baseline });

        Assert.Equal(LicenseCanonical.Serialize(a), LicenseCanonical.Serialize(b));
    }

    [Fact]
    public void ThePublicKeyShippedInTheBinaryIsAUsableP256Key()
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(LicenseKeys.PublicKeySpkiBase64), out _);

        Assert.Equal(256, ecdsa.KeySize);
    }

    [Fact]
    public void TheIssuerAndTheVerifierAgreeOnTheWrapperFormat()
    {
        var (privatePem, publicSpki) = NewKey();
        var file = Sign(ALicence(), privatePem);

        // Round-trip the wrapper itself: what Wrap writes, VerifyText must read.
        Assert.Contains("\"format\": \"nettriage-license/1\"", file);
        Assert.Equal(LicenseStatus.Valid, Verify(file, publicSpki).Status);
        Assert.Equal(LicenseStatus.Valid, Verify(file.TrimEnd('\n'), publicSpki).Status);
    }
}
