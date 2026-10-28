using System.Security.Cryptography;
using System.Text;
using BLL;
using BLL.DTOs.Activations;
using DAL.Repositories.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Static_X.Tests;

public sealed class ActivationDtoTests
{
    [Fact]
    public async Task Provision_MaximumDtoLengths_PreserveNormalizationProtectionExpiryAndCancellation()
    {
        var store = new Store();
        var protection = new EphemeralDataProtectionProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Activation:KeyRingPath"] = AppContext.BaseDirectory
        }).Build();
        var clock = new FixedClock();
        var service = new ActivationService(store, protection, configuration, clock);
        using var cancellation = new CancellationTokenSource();
        var key = new string('k', 128);
        var name = " " + new string('n', 198) + " ";
        var email = " " + new string('e', 242) + "@example.com ";
        Assert.Equal((128, 200, 256), (key.Length, name.Length, email.Length));

        Assert.Equal(29, await service.ProvisionAsync(new ProvisionGymRequest(key, name, email), cancellation.Token));

        Assert.Equal((key, new string('n', 198), new string('e', 242) + "@example.com"), (store.Key, store.Name, store.Email));
        Assert.Equal((clock.Now, clock.Now.AddHours(24), cancellation.Token), (store.Now, store.ExpiresAt, store.Token));
        Assert.NotEqual(Guid.Empty, store.Invitation);
        var token = protection.CreateProtector("Static-X.ActivationDelivery.v1", store.Invitation.ToString("N"))
            .Unprotect(store.ProtectedToken!);
        Assert.Equal(64, token.Length);
        Assert.All(token, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))), store.Hash);

        Assert.True(await service.ActivateAsync(new ActivateAccountRequest { Token = new string('a', 64), Password = " password " }, cancellation.Token));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(new string('A', 64)))), store.Hash);
        Assert.Equal(" password ", store.Password);
        Assert.Equal(clock.Now, store.Now);
        Assert.Equal(cancellation.Token, store.Token);
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; } = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Store : IActivationRepository
    {
        public string? Key { get; private set; }
        public string? Name { get; private set; }
        public string? Email { get; private set; }
        public string? Hash { get; private set; }
        public string? Password { get; private set; }
        public string? ProtectedToken { get; private set; }
        public Guid Invitation { get; private set; }
        public DateTimeOffset Now { get; private set; }
        public DateTimeOffset ExpiresAt { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task<int> ProvisionAsync(string key, string gymName, string email, string tokenHash, Guid invitationId,
            string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            Key = key;
            Name = gymName;
            Email = email;
            Hash = tokenHash;
            Invitation = invitationId;
            ProtectedToken = protectedToken;
            Now = now;
            ExpiresAt = expiresAt;
            Token = cancellationToken;
            return Task.FromResult(29);
        }

        public Task<bool> ActivateAsync(string tokenHash, string password, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Hash = tokenHash;
            Password = password;
            Now = now;
            Token = cancellationToken;
            return Task.FromResult(true);
        }

        public Task<bool> ReissueAsync(int gymId, string accountId, string tokenHash, Guid invitationId, string protectedToken,
            DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
