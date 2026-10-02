using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NetTriage.Core.Licensing;

namespace NetTriage.LicenseIssuer;

/// <summary>
/// The vendor's half of the licensing scheme: holds the private key and mints signed licence files.
/// It is kept in the repository so the trust model is auditable, but the private key itself is never
/// committed - see the .gitignore and tools/README.md.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage());
            return args.Length == 0 ? ExitError : ExitOk;
        }

        return args[0].ToLowerInvariant() switch
        {
            "keygen" => Keygen(args.Skip(1).ToArray()),
            "issue" => Issue(args.Skip(1).ToArray(), trial: false),
            "trial" => Issue(args.Skip(1).ToArray(), trial: true),
            "verify" => Verify(args.Skip(1).ToArray()),
            _ => Unknown(args[0]),
        };
    }

    private static int Keygen(string[] args)
    {
        var output = Value(args, "--out") ?? ".";
        Directory.CreateDirectory(output);

        var privatePath = Path.Combine(output, "nettriage-signing-private.pem");
        var publicPath = Path.Combine(output, "nettriage-signing-public.pem");

        if (File.Exists(privatePath))
        {
            Console.Error.WriteLine($"Refusing to overwrite the existing private key at {privatePath}.");
            Console.Error.WriteLine("Overwriting it would invalidate every licence already issued.");
            return ExitError;
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privatePath, ecdsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(publicPath, ecdsa.ExportSubjectPublicKeyInfoPem());

        TryRestrictPermissions(privatePath);

        var spki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        Console.WriteLine($"Private key  {privatePath}");
        Console.WriteLine($"Public key   {publicPath}");
        Console.WriteLine();
        Console.WriteLine("Paste this into LicenseKeys.cs in NetTriage.Core:");
        Console.WriteLine();
        Console.WriteLine(spki);
        Console.WriteLine();
        Console.WriteLine("Then keep the private key somewhere it cannot be committed, and publish only the public one.");

        return ExitOk;
    }

    private static int Issue(string[] args, bool trial)
    {
        var privatePath = Value(args, "--private-key");
        var holder = Value(args, "--holder");
        var organisation = Value(args, "--organisation") ?? Value(args, "--org") ?? "";
        var edition = Value(args, "--edition") ?? (trial ? Editions.Trial : Editions.Team);
        var output = Value(args, "--output") ?? Value(args, "-o");

        if (privatePath is null)
        {
            Console.Error.WriteLine("--private-key is required.");
            return ExitError;
        }

        if (holder is null)
        {
            Console.Error.WriteLine("--holder is required: the name of the person or company the licence is for.");
            return ExitError;
        }

        if (!File.Exists(privatePath))
        {
            Console.Error.WriteLine($"Private key not found: {privatePath}");
            return ExitError;
        }

        var daysText = Value(args, "--days");
        var days = trial ? 30 : 365;
        if (daysText is not null && (!int.TryParse(daysText, out days) || days < 1))
        {
            Console.Error.WriteLine("--days needs a positive integer.");
            return ExitError;
        }

        var features = Value(args, "--features")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                       ?? (edition switch
                       {
                           Editions.Trial => Editions.TrialFeatures.ToList(),
                           Editions.Site => Editions.SiteFeatures.ToList(),
                           _ => Editions.TeamFeatures.ToList(),
                       });

        var unknown = features.Where(f => !Features.All.Contains(f)).ToList();
        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"Unknown feature(s): {string.Join(", ", unknown)}.");
            Console.Error.WriteLine($"Known features: {string.Join(", ", Features.All)}.");
            return ExitError;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var expiresText = Value(args, "--expires");
        DateOnly expires;
        if (expiresText is not null)
        {
            if (!DateOnly.TryParse(expiresText, CultureInfo.InvariantCulture, DateTimeStyles.None, out expires))
            {
                Console.Error.WriteLine("--expires needs a date, for example 2027-06-30.");
                return ExitError;
            }
        }
        else
        {
            expires = today.AddDays(days);
        }

        var licence = new License(
            LicenseCanonical.CurrentSchema,
            Guid.NewGuid().ToString("N")[..12],
            holder,
            organisation,
            edition,
            today,
            expires,
            features);

        var payload = LicenseCanonical.Serialize(licence);

        byte[] signature;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(privatePath));
            signature = key.SignData(payload, HashAlgorithmName.SHA256);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not use the private key: {ex.Message}");
            return ExitError;
        }

        var file = LicenseVerifier.Wrap(payload, signature);

        // Never ship a licence we cannot verify ourselves.
        var check = LicenseVerifier.VerifyText(file);
        if (check.Status is not LicenseStatus.Valid)
        {
            Console.Error.WriteLine($"Refusing to write an invalid licence: {check.Message}");
            return ExitError;
        }

        var target = output ?? $"nettriage-{licence.LicenseId}.json";
        var full = Path.GetFullPath(target);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(full, file);

        Console.WriteLine($"Licence written to {full}");
        Console.WriteLine($"  Id          {licence.LicenseId}");
        Console.WriteLine($"  Holder      {licence.Holder}");
        Console.WriteLine($"  Edition     {licence.Edition}");
        Console.WriteLine($"  Issued      {licence.Issued:yyyy-MM-dd}");
        Console.WriteLine($"  Expires     {licence.Expires:yyyy-MM-dd}  ({days} day(s))");
        Console.WriteLine($"  Features    {string.Join(", ", licence.Features.OrderBy(f => f, StringComparer.Ordinal))}");
        Console.WriteLine();
        Console.WriteLine("Verified against the embedded public key before writing.");

        return ExitOk;
    }

    private static int Verify(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (path is null)
        {
            Console.Error.WriteLine("verify needs a licence file.");
            return ExitError;
        }

        var check = LicenseVerifier.VerifyFile(path);
        Console.WriteLine($"Status  {check.Status}");
        Console.WriteLine($"Detail  {check.Message}");

        return check.Status is LicenseStatus.Valid ? ExitOk : ExitError;
    }

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return i + 1 < args.Length ? args[i + 1] : null;
        }

        return null;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.Error.WriteLine();
        Console.WriteLine(Usage());
        return ExitError;
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort; the operator is told to keep it safe regardless.
        }
    }

    private static string Usage() => """
        nettriage-license-issuer — vendor tooling for signing nettriage licences

        USAGE
          nettriage-license-issuer keygen [--out <dir>]
          nettriage-license-issuer issue  --private-key <pem> --holder <name> [options]
          nettriage-license-issuer trial  --private-key <pem> --holder <name> [options]
          nettriage-license-issuer verify <licence-file>

        ISSUE OPTIONS
          --private-key <pem>        Required. PKCS#8 PEM private key from keygen.
          --holder <name>            Required. The person or company the licence is for.
          --organisation <name>      Optional. Shown in `license status`.
          --edition <name>           trial | team | site (default: team, or trial for `trial`)
          --days <n>                 Validity in days (default: 365, or 30 for `trial`)
          --expires <yyyy-MM-dd>     Explicit expiry, to align with a contract or PO end date
          --features <a,b,c>         Override the feature list. Known: baseline, estate, rules, exports
          -o, --output <file>        Where to write the licence (default: nettriage-<id>.json)

        The private key is the whole business: anyone holding it can mint licences. Keep it off the
        repository, off CI, and off any machine that does not need it.
        """;
}
