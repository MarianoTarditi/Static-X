using System.Globalization;
using BLL;

namespace Static_X.API;

public sealed record ActivationCommand(string Name, string? Key = null, string? GymName = null,
    string? Email = null, int GymId = 0, string? AccountId = null)
{
    public bool IsCommand => Name is "provision-gym" or "dispatch-activation" or "reissue-activation";
    public bool IsInvalid => Name == "invalid";
}

public static class ActivationCli
{
    public static ActivationCommand Parse(string[] args)
    {
        if (args.Length == 0) return new ActivationCommand("http");
        // Host configuration switches belong to ASP.NET, never to an operator command.
        if (args[0].StartsWith("--", StringComparison.Ordinal))
        {
            var operatorOptions = new[] { "--idempotency-key", "--gym-name", "--admin-email", "--gym-id", "--account-id" };
            if (args.Any(a => a is "provision-gym" or "dispatch-activation" or "reissue-activation" ||
                operatorOptions.Any(option => a == option || a.StartsWith(option + "=", StringComparison.Ordinal))))
                return new ActivationCommand("invalid");
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    return new ActivationCommand("invalid");
                if (!args[i].Contains('='))
                {
                    if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                        return new ActivationCommand("invalid");
                }
            }
            return new ActivationCommand("http");
        }
        if (args[0] is not ("provision-gym" or "dispatch-activation" or "reissue-activation"))
            return new ActivationCommand("invalid");

        var expected = args[0] switch
        {
            "provision-gym" => new[] { "--idempotency-key", "--gym-name", "--admin-email" },
            "reissue-activation" => new[] { "--gym-id", "--account-id" },
            _ => Array.Empty<string>()
        };
        if (args.Length != 1 + 2 * expected.Length) return new ActivationCommand("invalid");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (!expected.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1]) ||
                string.IsNullOrWhiteSpace(args[i + 1])) return new ActivationCommand("invalid");
        }
        if (args[0] == "dispatch-activation") return new ActivationCommand(args[0]);
        if (args[0] == "provision-gym")
        {
            var key = options["--idempotency-key"];
            var name = options["--gym-name"];
            var email = options["--admin-email"];
            if (key.Length > 128 || name.Length > 200 || email.Length > 256 ||
                !System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)
                return new ActivationCommand("invalid");
            return new ActivationCommand(args[0], key, name, email);
        }
        if (!int.TryParse(options["--gym-id"], NumberStyles.None, CultureInfo.InvariantCulture, out var gymId)
            || gymId <= 0 || options["--account-id"].Length > 450)
            return new ActivationCommand("invalid");
        return new ActivationCommand(args[0], GymId: gymId, AccountId: options["--account-id"]);
    }

    public static bool IsConfigured(ActivationCommand command, IConfiguration configuration)
    {
        var ring = configuration["Activation:KeyRingPath"];
        var frontend = configuration["Activation:FrontendUrl"];
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Static-X")) ||
            string.IsNullOrWhiteSpace(ring) || !Path.IsPathFullyQualified(ring) || !Directory.Exists(ring) ||
            !Uri.TryCreate(frontend, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        return command.Name != "dispatch-activation" ||
            (configuration["Resend:ApiKey"] is { } key && key.StartsWith("re_", StringComparison.Ordinal) &&
             configuration["Resend:FromAddress"] is { } sender &&
             System.Net.Mail.MailAddress.TryCreate(sender, out var address) && address.Address == sender);
    }

    public static async Task<bool> ExecuteAsync(ActivationCommand command, IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        switch (command.Name)
        {
            case "provision-gym":
                await services.GetRequiredService<ActivationService>()
                    .ProvisionAsync(command.Key!, command.GymName!, command.Email!, cancellationToken);
                return true;
            case "reissue-activation":
                return await services.GetRequiredService<ActivationService>()
                    .ReissueAsync(command.GymId, command.AccountId!, cancellationToken);
            case "dispatch-activation":
                return await services.GetRequiredService<ActivationDispatcher>().DispatchOneAsync(cancellationToken);
            default:
                throw new InvalidOperationException("Invalid operator command.");
        }
    }
}
