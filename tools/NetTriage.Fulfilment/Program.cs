using NetTriage.Fulfilment;

namespace NetTriage.Fulfilment;

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

        var command = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        var configPath = Value(rest, "--config")
                         ?? Environment.GetEnvironmentVariable("NETTRIAGE_FULFILMENT_CONFIG")
                         ?? Path.Combine(
                             Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                             ".nettriage-fulfilment", "config.json");

        var preview = command is "preview" || Has(rest, "--preview");

        try
        {
            var config = FulfilmentConfig.Load(configPath);

            switch (command)
            {
                case "run":
                case "preview":
                    return Run(config, preview);

                case "replay":
                {
                    var path = rest.FirstOrDefault(a => !a.StartsWith('-'));
                    if (path is null)
                    {
                        Console.Error.WriteLine("replay needs a path to a recorded order file.");
                        return ExitError;
                    }

                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    return Report(new FulfilmentRunner(config, preview, Log).Replay(path, cts.Token));
                }

                case "status":
                    new FulfilmentRunner(config, preview: true, Log).PrintStatus();
                    return ExitOk;

                case "resend":
                {
                    var orderId = rest.FirstOrDefault(a => !a.StartsWith('-'));
                    if (orderId is null)
                    {
                        Console.Error.WriteLine("resend needs an order id.");
                        return ExitError;
                    }

                    var runner = new FulfilmentRunner(config, preview, Log);
                    return runner.ResendAsync(orderId, CancellationToken.None).GetAwaiter().GetResult();
                }

                case "check":
                    return Check(config);

                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.");
                    Console.Error.WriteLine();
                    Console.WriteLine(Usage());
                    return ExitError;
            }
        }
        catch (FulfilmentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"unexpected failure: {ex.GetType().Name}: {ex.Message}");
            return ExitError;
        }
    }

    private static int Run(FulfilmentConfig config, bool preview)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var runner = new FulfilmentRunner(config, preview, Log);
        return Report(runner.Run(cts.Token));
    }

    private static int Report(RunResult result)
    {
        if (result.IsFailure)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"fulfilment failed on {result.Failed} order(s); the cursor was not advanced past them.");
            foreach (var message in result.Messages) Console.Error.WriteLine($"  {message}");
            return ExitError;
        }

        return ExitOk;
    }

    /// <summary>
    /// Verifies the parts that can be verified without contacting anyone: the configuration parses,
    /// the signing key loads, the mail sink is constructible. Run this after editing the config.
    /// </summary>
    private static int Check(FulfilmentConfig config)
    {
        Log($"config        {config.StatePath}");
        Log($"polar base    {config.Polar.BaseUrl}");
        Log($"product       {config.Polar.ProductId}");

        var token = config.ReadPolarToken();
        Log($"polar token   ok, {token.Length} characters (value never printed)");

        var factory = new LicenceFactory(config.Signing.PrivateKeyPath, config.Licence.Edition);
        Log($"signing key   {config.Signing.PrivateKeyPath}");

        var mail = MailSenderFactory.Create(config);
        Log($"mail sink     {mail.Describe()}");

        if (config.Mail.Mode == "smtp")
        {
            var password = config.ReadSmtpPassword();
            Log($"smtp password ok, {password.Length} characters (value never printed)");
        }

        Log("");
        Log("configuration is usable.");
        return ExitOk;
    }

    private static void Log(string message) => Console.WriteLine(message);

    private static bool Has(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return i + 1 < args.Length ? args[i + 1] : null;
        }

        return null;
    }

    private static string Usage() => """
        nettriage-fulfilment — turns a Polar order into a signed licence, without a human in the loop

        USAGE
          nettriage-fulfilment run     [--config <path>]
          nettriage-fulfilment preview [--config <path>]
          nettriage-fulfilment replay  <file> [--config <path>]
          nettriage-fulfilment status  [--config <path>]
          nettriage-fulfilment resend  <order-id> [--config <path>]
          nettriage-fulfilment check   [--config <path>]

        COMMANDS
          run       Read paid orders since the last cursor, mint a licence for each one that has not
                    been fulfilled yet, email it, and record it. This is what the scheduled job calls.
                    Exits non-zero if any order failed, so the failure is not silent.
          preview   Exactly what `run` would do, without minting, sending or recording anything.
          replay    Run the same pipeline over a recorded set of orders held in a file, so the whole
                    path can be rehearsed without a live sale. Combine with mail.mode "file".
          status    The cursor, the orders fulfilled so far, and the recent failures.
          resend    Email a licence again for an order already on record. Uses the stored file when
                    it is still there, and re-mints an identical one when it is not.
          check     Validate the configuration, the signing key and the mail credentials.

        CONFIGURATION
          Read from --config, else NETTRIAGE_FULFILMENT_CONFIG, else
          ~/.nettriage-fulfilment/config.json. See fulfilment.example.json.

        The signing key never leaves this machine, and no credential is ever printed.
        """;
}
