using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetTriage.Fulfilment;

/// <summary>
/// Everything a fulfilment run needs, read from a file that lives outside the repository. The
/// file names the Polar token, the signing key and the mail credentials; none of those values
/// are ever written here, only the paths to them, so the configuration itself is safe to keep
/// next to the keys.
/// </summary>
internal sealed class FulfilmentConfig
{
    public PolarOptions Polar { get; init; } = new();
    public SigningOptions Signing { get; init; } = new();
    public MailOptions Mail { get; init; } = new();
    public LicenceOptions Licence { get; init; } = new();

    /// <summary>Where the processed-order cursor and the issued-licence log live.</summary>
    public string StatePath { get; init; } = "";

    /// <summary>Every licence is also written here, so a re-send never has to re-mint.</summary>
    public string IssuedDirectory { get; init; } = "";

    private static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static FulfilmentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FulfilmentException(
                $"Configuration not found: {path}. Copy tools/NetTriage.Fulfilment/fulfilment.example.json and fill it in.");
        }

        FulfilmentConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<FulfilmentConfig>(File.ReadAllText(path), Read);
        }
        catch (JsonException ex)
        {
            throw new FulfilmentException($"{path} is not valid JSON: {ex.Message}");
        }

        if (config is null) throw new FulfilmentException($"{path} is empty.");

        config.Validate();
        return config;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Polar.ProductId))
            throw new FulfilmentException("polar.productId is required: without it every order in the account looks like ours.");

        if (string.IsNullOrWhiteSpace(Signing.PrivateKeyPath))
            throw new FulfilmentException("signing.privateKeyPath is required.");

        if (!File.Exists(Signing.PrivateKeyPath))
            throw new FulfilmentException($"Signing key not found: {Signing.PrivateKeyPath}");

        if (string.IsNullOrWhiteSpace(StatePath))
            throw new FulfilmentException("statePath is required.");

        if (string.IsNullOrWhiteSpace(IssuedDirectory))
            throw new FulfilmentException("issuedDirectory is required.");

        if (string.IsNullOrWhiteSpace(Mail.From))
            throw new FulfilmentException("mail.from is required: a licence email with no sender will be rejected.");

        if (Mail.Mode is not ("smtp" or "file"))
            throw new FulfilmentException($"mail.mode must be 'smtp' or 'file', not '{Mail.Mode}'.");

        if (Mail.Mode == "smtp")
        {
            if (string.IsNullOrWhiteSpace(Mail.Smtp.Host))
                throw new FulfilmentException("mail.smtp.host is required when mail.mode is 'smtp'.");
            if (string.IsNullOrWhiteSpace(Mail.Smtp.User))
                throw new FulfilmentException("mail.smtp.user is required when mail.mode is 'smtp'.");
            if (string.IsNullOrWhiteSpace(Mail.Smtp.PasswordFile) && string.IsNullOrWhiteSpace(Mail.Smtp.Password))
                throw new FulfilmentException("mail.smtp.passwordFile (preferred) or mail.smtp.password is required.");
        }
    }

    /// <summary>Resolves the Polar token, reading it from disk so it never appears in the config.</summary>
    public string ReadPolarToken()
    {
        if (string.IsNullOrWhiteSpace(Polar.TokenFile))
            throw new FulfilmentException("polar.tokenFile is required.");

        if (!File.Exists(Polar.TokenFile))
            throw new FulfilmentException($"Polar token file not found: {Polar.TokenFile}");

        var token = File.ReadAllText(Polar.TokenFile).Trim();
        if (token.Length == 0) throw new FulfilmentException($"Polar token file is empty: {Polar.TokenFile}");
        return token;
    }

    /// <summary>Resolves the SMTP password, reading it from disk when a file was configured.</summary>
    public string ReadSmtpPassword()
    {
        if (!string.IsNullOrWhiteSpace(Mail.Smtp.PasswordFile))
        {
            if (!File.Exists(Mail.Smtp.PasswordFile))
                throw new FulfilmentException($"SMTP password file not found: {Mail.Smtp.PasswordFile}");

            var fromFile = File.ReadAllText(Mail.Smtp.PasswordFile).Trim();
            if (fromFile.Length == 0) throw new FulfilmentException($"SMTP password file is empty: {Mail.Smtp.PasswordFile}");
            return fromFile;
        }

        return Mail.Smtp.Password;
    }
}

internal sealed class PolarOptions
{
    public string BaseUrl { get; init; } = "https://api.polar.sh";

    /// <summary>File holding the organisation access token. Never the token itself.</summary>
    public string TokenFile { get; init; } = "";

    /// <summary>The licensed product. Orders for anything else are ignored, never fulfilled.</summary>
    public string ProductId { get; init; } = "";
}

internal sealed class SigningOptions
{
    public string PrivateKeyPath { get; init; } = "";
}

internal sealed class MailOptions
{
    /// <summary><c>smtp</c> sends for real; <c>file</c> writes a .eml to <see cref="FileDirectory"/> so a
    /// full run can be rehearsed without credentials and without emailing a customer.</summary>
    public string Mode { get; init; } = "file";

    public string From { get; init; } = "";
    public string ReplyTo { get; init; } = "";

    /// <summary>Used when <see cref="Mode"/> is <c>file</c>.</summary>
    public string FileDirectory { get; init; } = "";

    public SmtpOptions Smtp { get; init; } = new();
}

internal sealed class SmtpOptions
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public string User { get; init; } = "";

    /// <summary>Path to a file holding the password. Preferred over <see cref="Password"/>.</summary>
    public string PasswordFile { get; init; } = "";

    /// <summary>Inline password. Supported, but a file keeps it out of shell history and backups.</summary>
    public string Password { get; init; } = "";

    /// <summary>STARTTLS on 587, or implicit TLS on 465.</summary>
    public bool UseStartTls { get; init; } = true;
}

internal sealed class LicenceOptions
{
    public string Edition { get; init; } = "team";

    /// <summary>Validity for a one-off order that carries no subscription period.</summary>
    public int FallbackDays { get; init; } = 365;

    /// <summary>Grace days added on top of the paid period, so a slow renewal never locks anyone out.</summary>
    public int GraceDays { get; init; } = 14;
}

/// <summary>An expected failure: a message for the operator, not a stack trace.</summary>
internal sealed class FulfilmentException(string message) : Exception(message);
