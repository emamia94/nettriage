using System.Text;
using NetTriage.Core.Licensing;

namespace NetTriage.Fulfilment;

/// <summary>
/// The customer-facing copy. Plain text on purpose: it is a transactional message carrying a file,
/// and plain text survives every mail client and every spam filter that a designed HTML template
/// would have to argue with.
/// </summary>
internal static class LicenceEmail
{
    public static LicenceMail Compose(
        FulfilmentConfig config,
        PolarOrder order,
        IssuedLicence licence,
        string holder,
        string organisation,
        string kind,
        string email)
    {
        var who = string.IsNullOrWhiteSpace(organisation) ? holder : organisation;
        var renewal = kind == "renewal";

        var subject = renewal
            ? $"Your nettriage licence has been renewed — valid to {licence.ExpiresText}"
            : $"Your nettriage licence for {who} — valid to {licence.ExpiresText}";

        var body = new StringBuilder();

        if (renewal)
        {
            body.AppendLine($"Hello {holder},");
            body.AppendLine();
            body.AppendLine($"Your nettriage subscription for {who} has renewed, and a fresh licence file is attached.");
        }
        else
        {
            body.AppendLine($"Hello {holder},");
            body.AppendLine();
            body.AppendLine($"Thank you for buying a nettriage licence for {who}. Your licence file is attached.");
        }

        body.AppendLine();
        body.AppendLine($"  File      {licence.FileName}");
        body.AppendLine($"  Licence   {licence.Licence.LicenseId}");
        body.AppendLine($"  Licensed  {who}");
        body.AppendLine($"  Valid to  {licence.ExpiresText}");
        body.AppendLine($"  Covers    {string.Join(", ", licence.Licence.Features.OrderBy(f => f, StringComparer.Ordinal).Select(Features.Describe))}");
        body.AppendLine();
        body.AppendLine("INSTALL IT");
        body.AppendLine();
        body.AppendLine($"  nettriage license install {licence.FileName}");
        body.AppendLine();
        body.AppendLine("Or keep it wherever you like and point the tool at it:");
        body.AppendLine();
        body.AppendLine($"  export {LicenseVerifier.EnvironmentVariable}=/path/to/{licence.FileName}");
        body.AppendLine();
        body.AppendLine("Then confirm:");
        body.AppendLine();
        body.AppendLine("  nettriage license status");
        body.AppendLine();

        if (!renewal)
        {
            body.AppendLine("IF YOU DO NOT HAVE THE TOOL YET");
            body.AppendLine();
            body.AppendLine("  dotnet tool install --global NetTriage");
            body.AppendLine();
        }

        body.AppendLine("HOW THE LICENCE WORKS");
        body.AppendLine();
        body.AppendLine("It is a signed file, verified locally against a public key compiled into the tool.");
        body.AppendLine("There is no activation server and no telemetry: it works on a machine with no network");
        body.AppendLine("access at all, and it does not count seats or projects.");
        body.AppendLine();
        body.AppendLine("Nothing in the free edition is gated. Scanning, all detectors, the sequencing and the");
        body.AppendLine("effort estimates keep working with no licence, and always will.");
        body.AppendLine();
        body.AppendLine("RENEWALS");
        body.AppendLine();
        body.AppendLine("This is an annual subscription. When it renews, a new licence file is issued to this");
        body.AppendLine("address automatically - you do not need to do anything or ask. Keep this email: the");
        body.AppendLine("attachment is the licence.");
        body.AppendLine();
        body.AppendLine("-- ");
        body.AppendLine("nettriage - deterministic, offline triage for .NET Framework migrations");
        body.AppendLine("https://github.com/emamia94/nettriage");

        if (!string.IsNullOrWhiteSpace(config.Mail.ReplyTo))
        {
            body.AppendLine(config.Mail.ReplyTo);
        }

        return new LicenceMail(email, holder, subject, body.ToString(), licence.FileName, licence.Json);
    }

    public static LicenceMail ComposeResend(FulfilmentConfig config, ProcessedOrder record, string attachmentName)
    {
        var who = string.IsNullOrWhiteSpace(record.Organisation) ? record.Holder : record.Organisation;

        var subject = $"Your nettriage licence — resent — valid to {record.Expires}";

        var body = new StringBuilder();
        body.AppendLine($"Hello {record.Holder},");
        body.AppendLine();
        body.AppendLine($"Here is your nettriage licence for {who} again, as requested.");
        body.AppendLine();
        body.AppendLine($"  File      {attachmentName}");
        body.AppendLine($"  Licence   {record.LicenceId}");
        body.AppendLine($"  Licensed  {who}");
        body.AppendLine($"  Valid to  {record.Expires}");
        body.AppendLine();
        body.AppendLine("INSTALL IT");
        body.AppendLine();
        body.AppendLine($"  nettriage license install {attachmentName}");
        body.AppendLine();
        body.AppendLine("If you were expecting this to be for a different organisation, reply to this message");
        body.AppendLine("and we will reissue it.");
        body.AppendLine();
        body.AppendLine("-- ");
        body.AppendLine("nettriage - deterministic, offline triage for .NET Framework migrations");
        body.AppendLine("https://github.com/emamia94/nettriage");

        if (!string.IsNullOrWhiteSpace(config.Mail.ReplyTo))
        {
            body.AppendLine(config.Mail.ReplyTo);
        }

        return new LicenceMail(record.Email, record.Holder, subject, body.ToString(), attachmentName, "");
    }
}
