using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Resend;
using Static_X.API.Integrations;
using Xunit;

namespace Static_X.Tests;

public sealed class PasswordRecoveryEmailSenderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryEmailAdapter_UsesStableKeyAndRecoveryBodyWithOnlyFakeHttpTransport(bool accepted)
    {
        var handler = new RecordingHandler(accepted);
        var services = new ServiceCollection().AddLogging();
        services.Configure<ResendClientOptions>(options => { options.ApiToken = "re_fake_never_sent"; options.ThrowExceptions = false; });
        services.AddHttpClient<ResendClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();
        var sender = new ResendPasswordRecoveryEmailSender(provider.GetRequiredService<ResendClient>());
        const string link = "https://frontend.example.invalid/reset#reset-password=fake-envelope";
        var result = await sender.SendAsync("password-recovery/test-id", "sender@example.invalid", "recipient@example.invalid", link, default);
        Assert.Equal(accepted, result);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("password-recovery/test-id", handler.Key);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Reset your Static-X password", body.RootElement.GetProperty("subject").GetString());
        Assert.Equal("sender@example.invalid", body.RootElement.GetProperty("from").GetString());
        Assert.Contains(link, body.RootElement.GetProperty("text").GetString());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly bool accepted;
        public int Calls { get; private set; }
        public string? Key { get; private set; }
        public string? Body { get; private set; }
        public RecordingHandler(bool accepted) { this.accepted = accepted; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Key = request.Headers.GetValues("Idempotency-Key").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(accepted ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(accepted ? "{\"id\":\"00000000-0000-0000-0000-000000000001\"}" :
                    "{\"statusCode\":503,\"name\":\"application_error\",\"message\":\"test only\"}", Encoding.UTF8, "application/json")
            };
        }
    }
}
