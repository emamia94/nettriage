using System.Globalization;

namespace NetTriage.Fulfilment;

internal enum RunOutcome
{
    NothingToDo,
    Fulfilled,
    Failed,
}

internal sealed record RunResult(RunOutcome Outcome, int Examined, int Issued, int Skipped, int Failed, List<string> Messages)
{
    public bool IsFailure => Outcome is RunOutcome.Failed;
}

/// <summary>
/// The whole fulfilment policy, in one place. It is deliberately a pull loop rather than a
/// webhook handler: it needs no public endpoint, it keeps the signing key on the machine that
/// already holds it, and if it is down for a day it catches up by itself instead of losing the
/// events that arrived while it was gone.
/// </summary>
internal sealed class FulfilmentRunner(FulfilmentConfig config, bool preview, Action<string> log)
{
    private readonly FulfilmentState _state = FulfilmentState.Load(config.StatePath);
    private readonly List<string> _messages = [];

    private delegate Task<PolarSubscription?> SubscriptionLookup(string subscriptionId, CancellationToken ct);

    public RunResult Run(CancellationToken ct) => RunAsync(ct).GetAwaiter().GetResult();

    private async Task<RunResult> RunAsync(CancellationToken ct)
    {
        var token = config.ReadPolarToken();
        using var polar = new PolarClient(config.Polar.BaseUrl, token);
        var mail = MailSenderFactory.Create(config);

        log($"source     polar {config.Polar.BaseUrl}");
        log($"state      {config.StatePath}");
        log($"cursor     {(_state.Cursor is { } c ? c.UtcDateTime.ToString("u") : "start of time (first run)")}");
        log($"mail       {mail.Describe()}");
        log($"product    {config.Polar.ProductId}");
        log("");

        var orders = await polar.ListPaidOrdersAsync(config.Polar.ProductId, _state.Cursor, ct);
        log($"paid orders for this product since the cursor: {orders.Count}");

        return await ProcessAsync(orders, polar.GetSubscriptionAsync, polar, mail, ct);
    }

    public RunResult Replay(string path, CancellationToken ct) => ReplayAsync(path, ct).GetAwaiter().GetResult();

    private async Task<RunResult> ReplayAsync(string path, CancellationToken ct)
    {
        var replay = ReplayFile.Load(path);
        var mail = MailSenderFactory.Create(config);

        log($"source     replay of {replay.Orders.Count} order(s) from {path}");
        log($"state      {config.StatePath}");
        log($"mail       {mail.Describe()}");
        log("");

        return await ProcessAsync(replay.Orders, replay.LookupSubscriptionAsync, polar: null, mail, ct);
    }

    private async Task<RunResult> ProcessAsync(
        List<PolarOrder> orders,
        SubscriptionLookup lookup,
        PolarClient? polar,
        IMailSender mail,
        CancellationToken ct)
    {
        var factory = new LicenceFactory(config.Signing.PrivateKeyPath, config.Licence.Edition);

        var issued = 0;
        var skipped = 0;
        var failed = 0;
        var examined = 0;
        var progressed = false;

        foreach (var order in orders.OrderBy(o => o.CreatedAt))
        {
            ct.ThrowIfCancellationRequested();

            if (_state.Processed.ContainsKey(order.Id))
            {
                skipped++;
                log($"  = {order.Id}  already fulfilled, skipping");
                // It was already handled, so the cursor may safely pass it.
                progressed |= AdvanceCursor(order.CreatedAt);
                continue;
            }

            examined++;

            try
            {
                var record = await FulfillOneAsync(polar, factory, mail, lookup, order, ct);
                if (record is null)
                {
                    skipped++;
                }
                else
                {
                    issued++;
                }

                progressed |= AdvanceCursor(order.CreatedAt);
            }
            catch (Exception ex) when (ex is FulfilmentException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Stop at the first failure: order matters, and skipping ahead would let a later
                // order overtake an unfulfilled one.
                failed++;
                var message = $"order {order.Id}: {ex.Message}";
                _state.RecordFailure(message);
                _messages.Add(message);
                log($"  ! {message}");
                break;
            }
        }

        if (!preview) _state.Save(config.StatePath);

        log("");
        log($"examined {examined}, issued {issued}, skipped {skipped}, failed {failed}");

        var outcome = failed > 0
            ? RunOutcome.Failed
            : issued > 0
                ? RunOutcome.Fulfilled
                : RunOutcome.NothingToDo;

        return new RunResult(outcome, examined, issued, skipped, failed, _messages);
    }

    /// <summary>Returns null when the order is deliberately not fulfilled.</summary>
    private async Task<ProcessedOrder?> FulfillOneAsync(
        PolarClient? polar,
        LicenceFactory factory,
        IMailSender mail,
        SubscriptionLookup lookup,
        PolarOrder order,
        CancellationToken ct)
    {
        var kind = order.IsRenewal ? "renewal" : "purchase";

        var email = order.Customer?.Email?.Trim() ?? "";
        if (email.Length == 0)
        {
            throw new FulfilmentException("the order carries no customer email, so there is nowhere to deliver the licence");
        }

        var organisation = order.OrganisationFromCheckout();
        var holder = FirstNonEmpty(organisation, order.Customer?.Name, order.Customer?.BillingName, email);

        var expires = await ResolveExpiryAsync(lookup, order, ct);

        log($"  + {order.Id}  {kind}  {email}  holder=\"{holder}\"  expires={expires:yyyy-MM-dd}");

        if (preview)
        {
            log("      preview only: no licence minted, no email sent, nothing recorded");
            return null;
        }

        var licence = factory.Issue(order.Id, holder, organisation, expires);

        var directory = Path.Combine(config.IssuedDirectory, expires.ToString("yyyy-MM"));
        Directory.CreateDirectory(directory);
        var licencePath = Path.Combine(directory, licence.FileName);
        File.WriteAllText(licencePath, licence.Json);

        var message = LicenceEmail.Compose(config, order, licence, holder, organisation, kind, email);

        var record = new ProcessedOrder
        {
            OrderId = order.Id,
            LicenceId = licence.Licence.LicenseId,
            Holder = holder,
            Organisation = organisation,
            Email = email,
            Expires = licence.ExpiresText,
            Kind = kind,
            LicenceFile = licencePath,
            FulfilledAt = DateTimeOffset.UtcNow,
            SendCount = 1,
        };

        await mail.SendAsync(message, ct);

        _state.Processed[order.Id] = record;
        _state.Save(config.StatePath);

        log($"      licence {licence.Licence.LicenseId} -> {licencePath}");

        // Best effort: our state is authoritative, this is only so the Polar dashboard agrees.
        if (polar is not null)
        {
            try
            {
                await polar.MarkFulfilledAsync(order.Id, licence.Licence.LicenseId, licence.ExpiresText, ct);
            }
            catch (FulfilmentException ex)
            {
                log($"      note: could not annotate the order in Polar ({ex.Message}); fulfilment is recorded locally anyway");
            }
        }

        return record;
    }

    /// <summary>
    /// The expiry comes from the subscription period, not from a fixed year, so a renewal extends
    /// the licence to the date the customer has actually paid through. A grace margin means a
    /// renewal that settles a day late never locks anyone out.
    /// </summary>
    private async Task<DateOnly> ResolveExpiryAsync(SubscriptionLookup lookup, PolarOrder order, CancellationToken ct)
    {
        var grace = Math.Max(0, config.Licence.GraceDays);

        if (!string.IsNullOrWhiteSpace(order.SubscriptionId))
        {
            var subscription = await lookup(order.SubscriptionId, ct);
            if (subscription?.CurrentPeriodEnd is { } periodEnd)
            {
                return DateOnly.FromDateTime(periodEnd.UtcDateTime).AddDays(grace);
            }
        }

        var days = Math.Max(1, config.Licence.FallbackDays);
        return DateOnly.FromDateTime(DateTime.UtcNow).AddDays(days + grace);
    }

    private bool AdvanceCursor(DateTimeOffset createdAt)
    {
        if (_state.Cursor is { } current && createdAt <= current) return false;
        _state.Cursor = createdAt;
        return true;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return "";
    }

    /// <summary>Re-sends a licence for an order already on record, minting it again only if the file is gone.</summary>
    public async Task<int> ResendAsync(string orderId, CancellationToken ct)
    {
        if (!_state.Processed.TryGetValue(orderId, out var record))
        {
            log($"order {orderId} has never been fulfilled by this state file.");
            log("Run 'run' first, or check the order id in the Polar dashboard.");
            return 1;
        }

        log($"re-sending licence {record.LicenceId} for order {orderId} to {record.Email}");

        if (preview)
        {
            log("preview only: nothing sent");
            return 0;
        }

        var factory = new LicenceFactory(config.Signing.PrivateKeyPath, config.Licence.Edition);
        var mail = MailSenderFactory.Create(config);

        string json;
        if (File.Exists(record.LicenceFile))
        {
            json = File.ReadAllText(record.LicenceFile);
            log($"  using the stored file {record.LicenceFile}");
        }
        else
        {
            var expires = DateOnly.ParseExact(record.Expires, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var licence = factory.Issue(record.OrderId, record.Holder, record.Organisation, expires);
            json = licence.Json;
            Directory.CreateDirectory(Path.GetDirectoryName(record.LicenceFile)!);
            File.WriteAllText(record.LicenceFile, json);
            log($"  the stored file was missing; re-minted {licence.Licence.LicenseId} (same id, same expiry)");
        }

        var attachmentName = $"nettriage-{record.LicenceId}.json";
        var message = LicenceEmail.ComposeResend(config, record, attachmentName) with { AttachmentContent = json };

        await mail.SendAsync(message, ct);

        record.SendCount++;
        _state.Save(config.StatePath);
        log($"  sent (send count now {record.SendCount})");

        return 0;
    }

    public void PrintStatus()
    {
        log($"state file      {config.StatePath}");
        log($"cursor          {(_state.Cursor is { } c ? c.UtcDateTime.ToString("u") : "never run")}");
        log($"orders recorded {_state.Processed.Count}");

        foreach (var (id, record) in _state.Processed.OrderByDescending(p => p.Value.FulfilledAt).Take(20))
        {
            log($"  {record.FulfilledAt:yyyy-MM-dd}  {id}  {record.Kind,-8} {record.Email,-32} {record.LicenceId}  expires {record.Expires}  sent {record.SendCount}x");
        }

        if (_state.RecentFailures.Count > 0)
        {
            log("");
            log("recent failures:");
            foreach (var failure in _state.RecentFailures.Take(10)) log($"  {failure}");
        }
    }
}
