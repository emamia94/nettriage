using System.Net;
using System.Net.Mail;
using System.Text;

namespace NetTriage.Fulfilment;

internal sealed record LicenceMail(
    string To,
    string ToName,
    string Subject,
    string Body,
    string AttachmentName,
    string AttachmentContent);

internal interface IMailSender
{
    /// <summary>Describes the sink, for the run log. Never contains a credential.</summary>
    string Describe();

    Task SendAsync(LicenceMail mail, CancellationToken ct);
}

internal static class MailSenderFactory
{
    public static IMailSender Create(FulfilmentConfig config) => config.Mail.Mode switch
    {
        "smtp" => new SmtpMailSender(config),
        "file" => new FileMailSender(config),
        var other => throw new FulfilmentException($"Unknown mail mode '{other}'."),
    };

    /// <summary>Builds the message both senders share, so a rehearsal exercises the real thing.</summary>
    internal static MailMessage Build(FulfilmentConfig config, LicenceMail mail)
    {
        var message = new MailMessage(
            new MailAddress(Address(config.Mail.From), Name(config.Mail.From)),
            new MailAddress(mail.To, mail.ToName))
        {
            Subject = mail.Subject,
            Body = mail.Body,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };

        if (!string.IsNullOrWhiteSpace(config.Mail.ReplyTo))
            message.ReplyToList.Add(new MailAddress(config.Mail.ReplyTo));

        message.Attachments.Add(new Attachment(
            new MemoryStream(Encoding.UTF8.GetBytes(mail.AttachmentContent)),
            mail.AttachmentName,
            "application/json"));

        return message;
    }

    internal static string Address(string value)
    {
        var open = value.LastIndexOf('<');
        var close = value.LastIndexOf('>');
        return open >= 0 && close > open ? value[(open + 1)..close].Trim() : value.Trim();
    }

    internal static string Name(string value)
    {
        var open = value.LastIndexOf('<');
        return open > 0 ? value[..open].Trim().Trim('"') : "";
    }
}

/// <summary>
/// Drops the exact message that would be sent into a directory instead of sending it, using the
/// SMTP client's own pickup-directory delivery so the file is written by the same code path that
/// would have transmitted it. This is how the pipeline is rehearsed end to end, and how a support
/// re-send is inspected before it goes out.
/// </summary>
internal sealed class FileMailSender(FulfilmentConfig config) : IMailSender
{
    public string Describe() => $"file sink at {Directory()}";

    private string Directory() =>
        string.IsNullOrWhiteSpace(config.Mail.FileDirectory)
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.StatePath))!, "outbox")
            : config.Mail.FileDirectory;

    public Task SendAsync(LicenceMail mail, CancellationToken ct)
    {
        var directory = Directory();
        System.IO.Directory.CreateDirectory(directory);

        using var client = new SmtpClient
        {
            DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
            PickupDirectoryLocation = directory,
        };

        using var message = MailSenderFactory.Build(config, mail);
        client.Send(message);

        var written = System.IO.Directory.GetFiles(directory, "*.eml")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        Console.WriteLine($"      email written to {written ?? directory}");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sends through an ordinary authenticated SMTP submission port. No third-party SDK, and no
/// dependency added to the product: this is vendor tooling that never ships.
/// </summary>
internal sealed class SmtpMailSender : IMailSender
{
    private readonly FulfilmentConfig _config;

    public SmtpMailSender(FulfilmentConfig config) => _config = config;

    public string Describe() =>
        $"smtp {_config.Mail.Smtp.Host}:{_config.Mail.Smtp.Port} as {_config.Mail.Smtp.User}";

    public async Task SendAsync(LicenceMail mail, CancellationToken ct)
    {
        var smtp = _config.Mail.Smtp;

        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(smtp.User, _config.ReadSmtpPassword()),
            Timeout = 60_000,
        };

        using var message = MailSenderFactory.Build(_config, mail);

        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (SmtpException ex)
        {
            throw new FulfilmentException(
                $"SMTP rejected the message to {mail.To} ({ex.StatusCode}): {ex.Message}. " +
                "A 535 usually means the password is stale - app passwords are revoked when the account password changes.");
        }
    }
}
