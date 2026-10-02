namespace NetTriage.Core.Licensing;

/// <summary>
/// The public half of the licence signing key. Only this half ships in the binary, which is what
/// lets verification work offline and air-gapped: there is no activation server to call, and the
/// private key never leaves the vendor.
///
/// The algorithm is ECDSA over NIST P-256 with SHA-256. Ed25519 was the original choice, but the
/// .NET base class library does not provide it (verified absent through .NET 10), and taking a
/// dependency on a third-party curve implementation to sign licence files is not a trade worth
/// making for a tool whose selling point is zero dependencies.
/// </summary>
public static class LicenseKeys
{
    public const string Algorithm = "ECDSA-P256-SHA256";

    /// <summary>SubjectPublicKeyInfo, base64. Regenerating the keypair invalidates every issued licence.</summary>
    public const string PublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEdEnenslxHW+1Prn0vq3TyZw5mR6gxfu9GqLkjG2vesCVHi2d0O80g0TcYoc3bA3l+mHBOVn8mqgZFJRrlIg9gw==";
}
