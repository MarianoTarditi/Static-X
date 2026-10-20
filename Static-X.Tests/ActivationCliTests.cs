using BLL;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Static_X.API;
using Xunit;

namespace Static_X.Tests;

public sealed class ActivationCliTests
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("dispatch-activation", "--batch", "10")]
    [InlineData("provision-gym", "--idempotency-key", "key", "--gym-name", "Gym", "--admin-email")]
    [InlineData("provision-gym", "--idempotency-key", "key", "--gym-name", "Gym", "--admin-email", "bad")]
    [InlineData("reissue-activation", "--gym-id", "0", "--account-id", "account")]
    [InlineData("reissue-activation", "--gym-id", "1", "--account-id", "account", "--account-id", "other")]
    [InlineData("--gym-id", "7")]
    [InlineData("--environment=Development", "provision-gym", "--gym-name", "Gym")]
    [InlineData("--environment=Development", "--idempotency-key", "key")]
    [InlineData("--gym-name=Gym")]
    [InlineData("--environment=Development", "provision-gymm")]
    [InlineData("--environment")]
    public void Parse_RejectsUnknownAndMalformedCommands(params string[] args) =>
        Assert.True(ActivationCli.Parse(args).IsInvalid);

    [Theory]
    [InlineData("--environment=Development", "--contentRoot=/app", "--Activation:KeyRingPath=/keys")]
    [InlineData("--environment", "Development", "--applicationName", "Static-X")]
    public void Parse_HostArgumentsRemainHttpWithoutOperatorDispatch(params string[] args)
    {
        var command = ActivationCli.Parse(args);
        Assert.Equal("http", command.Name);
        Assert.False(command.IsCommand);
        Assert.False(command.IsInvalid);
    }

    [Fact]
    public void Parse_RequiresExplicitCommandAndAcceptsOptionsInEitherOrder()
    {
        Assert.False(ActivationCli.Parse([]).IsCommand);
        var provision = ActivationCli.Parse(["provision-gym", "--admin-email", "admin@example.com",
            "--gym-name", "North", "--idempotency-key", "opaque-1"]);
        Assert.Equal("opaque-1", provision.Key);
        Assert.Equal("North", provision.GymName);
        Assert.Equal("admin@example.com", provision.Email);
        Assert.Equal(7, ActivationCli.Parse(["reissue-activation", "--gym-id", "7", "--account-id", "account"]).GymId);
        Assert.True(ActivationCli.Parse(["dispatch-activation"]).IsCommand);
    }

    [Fact]
    public void Configuration_OnlyDispatchRequiresResend()
    {
        var directory = Path.Combine(Path.GetTempPath(), "activation-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Static-X"] = "Server=example;Database=example;",
                ["Activation:KeyRingPath"] = directory,
                ["Activation:FrontendUrl"] = "https://app.example.com/activate"
            }).Build();
            Assert.True(ActivationCli.IsConfigured(ActivationCli.Parse(["provision-gym", "--idempotency-key", "key",
                "--gym-name", "North", "--admin-email", "admin@example.com"]), config));
            Assert.False(ActivationCli.IsConfigured(ActivationCli.Parse(["dispatch-activation"]), config));
            config["Resend:ApiKey"] = "re_fake";
            config["Resend:FromAddress"] = "sender@example.com";
            Assert.True(ActivationCli.IsConfigured(ActivationCli.Parse(["dispatch-activation"]), config));
            config["Activation:FrontendUrl"] = "http://app.example.com/activate";
            Assert.False(ActivationCli.IsConfigured(ActivationCli.Parse(["dispatch-activation"]), config));
        }
        finally { Directory.Delete(directory); }
    }

    [Fact]
    public async Task Execute_RoutesExplicitCommandsThroughDIWithoutDatabaseOrMail()
    {
        var directory = Path.Combine(Path.GetTempPath(), "activation-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new FakeStore();
            var delivery = new FakeDeliveryStore();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Activation:KeyRingPath"] = directory,
                ["Activation:FrontendUrl"] = "https://app.example.com/activate",
                ["Resend:ApiKey"] = "re_fake",
                ["Resend:FromAddress"] = "sender@example.com"
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IActivationStore>(store);
            services.AddSingleton<IActivationDeliveryStore>(delivery);
            services.AddSingleton<IActivationEmailSender>(new ThrowingSender());
            services.AddTransient<ActivationService>();
            services.AddTransient<ActivationDispatcher>();
            using var provider = services.BuildServiceProvider();
            var provision = ActivationCli.Parse(["provision-gym", "--idempotency-key", "opaque-1",
                "--gym-name", "North", "--admin-email", "admin@example.com"]);
            Assert.True(await ActivationCli.ExecuteAsync(provision, provider));
            Assert.True(await ActivationCli.ExecuteAsync(provision, provider));
            Assert.Equal(2, store.ProvisionCalls);
            Assert.Equal("opaque-1", store.Key);
            Assert.Equal("North", store.Name);
            Assert.Equal("admin@example.com", store.Email);
            Assert.True(await ActivationCli.ExecuteAsync(ActivationCli.Parse(["reissue-activation",
                "--gym-id", "7", "--account-id", "account"]), provider));
            Assert.Equal(7, store.ReissueGymId);
            Assert.Equal("account", store.ReissueAccountId);
            Assert.False(await ActivationCli.ExecuteAsync(ActivationCli.Parse(["dispatch-activation"]), provider));
            Assert.Equal(1, delivery.LeaseCalls);
        }
        finally { Directory.Delete(directory); }
    }

    private sealed class FakeStore : IActivationStore
    {
        public int ProvisionCalls { get; private set; }
        public string? Key { get; private set; }
        public string? Name { get; private set; }
        public string? Email { get; private set; }
        public int ReissueGymId { get; private set; }
        public string? ReissueAccountId { get; private set; }
        public Task<int> ProvisionAsync(string key, string gymName, string email, string tokenHash,
            Guid invitationId, string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
        {
            ProvisionCalls++;
            Key = key; Name = gymName; Email = email;
            return Task.FromResult(7);
        }
        public Task<bool> ReissueAsync(int gymId, string accountId, string tokenHash, Guid invitationId,
            string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken ct)
        {
            ReissueGymId = gymId; ReissueAccountId = accountId;
            return Task.FromResult(true);
        }
        public Task<bool> ActivateAsync(string hash, string password, DateTimeOffset now, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected activation.");
    }

    private sealed class FakeDeliveryStore : IActivationDeliveryStore
    {
        public int LeaseCalls { get; private set; }
        public Task<ActivationDelivery?> LeaseDueAsync(DateTimeOffset now, CancellationToken ct)
        {
            LeaseCalls++;
            return Task.FromResult<ActivationDelivery?>(null);
        }
        public Task<bool> IsCurrentAsync(ActivationDelivery delivery, DateTimeOffset now, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected delivery.");
        public Task CompleteAsync(ActivationDelivery delivery, DateTimeOffset now, bool accepted, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected delivery.");
    }

    private sealed class ThrowingSender : IActivationEmailSender
    {
        public Task<bool> SendAsync(string key, string from, string to, string link, CancellationToken ct) =>
            throw new InvalidOperationException("Real delivery must not run in CLI tests.");
    }
}
