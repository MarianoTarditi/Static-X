using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using BLL.Services.Interfaces;
using Static_X.API;
using Static_X.API.Integrations;
using Xunit;

namespace Static_X.Tests;

public sealed class DevelopmentPickupTests
{
    [WindowsTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task WindowsPowerShell_HelperUsesUtf8BodiesAndCsrf_WithoutSecretOutput(bool failActivation, bool loginOnly)
    {
        using var fixture = new PickupFixture();
        Assert.True(await fixture.SendAsync());
        var root = FindRepository();
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-File");
        start.ArgumentList.Add(System.IO.Path.Combine(root, "tests", "Static_X.Tests", "ManualActivationHarness.ps1"));
        start.ArgumentList.Add("-HelperPath"); start.ArgumentList.Add(System.IO.Path.Combine(root, "Docs", "ActivateLocalAccount.ps1"));
        if (failActivation) start.ArgumentList.Add("-FailActivation");
        if (loginOnly) start.ArgumentList.Add("-LoginOnly");
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["Activation__DevelopmentPickupPath"] = fixture.Path;
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        Assert.Equal(string.Empty, error);
        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain(fixture.Link, output);
        Assert.Equal(failActivation ? "Offline failure sanitization verified." :
            "Local login completed. Session and CSRF headers remain in memory; do not print them." + System.Environment.NewLine + "Offline helper verified.", output.Trim());
        Assert.Equal(failActivation ? 1 : 0, Directory.GetFiles(fixture.Path, "*.json").Length);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Executable_LocalCommandOutsideDevelopment_ExitsTwoWithOnlySafeDiagnostic(string environment)
    {
        using var fixture = new PickupFixture(environment);
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = FindRepository()
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("dispatch-activation-local");
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["Activation__DevelopmentPickupEnabled"] = "true";
        start.Environment["Activation__DevelopmentDatabaseConfirmed"] = "true";
        start.Environment["Activation__DevelopmentPickupPath"] = fixture.Path;
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.Equal(2, process.ExitCode);
        Assert.Equal(string.Empty, await process.StandardOutput.ReadToEndAsync());
        Assert.Equal("Operator configuration is missing or unsafe.", (await process.StandardError.ReadToEndAsync()).Trim());
        Assert.False(Directory.Exists(fixture.Path));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task LocalCommandAndAdapter_DenyOutsideDevelopmentWithoutWriting(string environment)
    {
        using var fixture = new PickupFixture(environment);
        Assert.False(ActivationCli.IsConfigured(ActivationCli.Parse(["dispatch-activation-local"]), fixture.Configuration, fixture.Environment));
        Assert.False(await fixture.SendAsync());
        Assert.False(Directory.Exists(fixture.Path));
        var dispatcher = new RecordingDispatcher();
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(fixture.Configuration)
            .AddSingleton<IHostEnvironment>(fixture.Environment)
            .AddSingleton<IActivationDispatcher>(dispatcher).BuildServiceProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ActivationCli.ExecuteAsync(
            ActivationCli.Parse(["dispatch-activation-local"]), services));
        Assert.False(dispatcher.Called);
    }

    [Theory]
    [InlineData("Activation:DevelopmentPickupEnabled")]
    [InlineData("Activation:DevelopmentDatabaseConfirmed")]
    [InlineData("Activation:DevelopmentPickupPath")]
    public async Task LocalDelivery_RequiresEveryExplicitOptIn(string key)
    {
        using var fixture = new PickupFixture();
        fixture.Configuration[key] = null;
        Assert.False(ActivationCli.IsConfigured(ActivationCli.Parse(["dispatch-activation-local"]), fixture.Configuration, fixture.Environment));
        Assert.False(await fixture.SendAsync());
        Assert.False(Directory.Exists(fixture.Path));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("repo")]
    [InlineData("temporary-root")]
    [InlineData("profile-root")]
    [InlineData("relative")]
    [InlineData("key-ring")]
    public async Task LocalDelivery_RejectsUnsafePathsWithoutSecretDiagnostics(string scenario)
    {
        using var fixture = new PickupFixture();
        var repository = FindRepository();
        fixture.Environment.ContentRootPath = System.IO.Path.Combine(repository, "src", "Static_X.API");
        fixture.Configuration["Activation:DevelopmentPickupPath"] = scenario switch
        {
            "content" => fixture.Environment.ContentRootPath,
            "repo" => System.IO.Path.Combine(repository, "Docs", "pickup"),
            "temporary-root" => System.IO.Path.GetTempPath(),
            "profile-root" => System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "relative" => "pickup",
            _ => fixture.Configuration["Activation:KeyRingPath"]
        };
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Sender.ValidateDirectory());
        Assert.Equal("Development pickup configuration is missing or unsafe.", error.Message);
        Assert.Null(error.InnerException);
        Assert.False(await fixture.SendAsync());
    }

    [Fact]
    public async Task Pickup_AtomicPrivateAndIdempotent_NeverOverwritesConflictingPayload()
    {
        using var fixture = new PickupFixture();
        Assert.True(await fixture.SendAsync());
        var file = Assert.Single(Directory.GetFiles(fixture.Path));
        Assert.Equal(fixture.Job.ToString("N") + ".json", System.IO.Path.GetFileName(file));
        var original = await File.ReadAllTextAsync(file);
        using var json = JsonDocument.Parse(original);
        Assert.Equal(fixture.Link, json.RootElement.GetProperty("Link").GetString());
        Assert.Equal("admin@example.invalid", json.RootElement.GetProperty("Email").GetString());
        Assert.Equal("activation/" + fixture.Job.ToString("N"), json.RootElement.GetProperty("IdempotencyKey").GetString());
        Assert.True(await fixture.SendAsync());
        Assert.False(await fixture.Sender.SendAsync("activation/" + fixture.Job.ToString("N"), "", "other@example.invalid", fixture.Link, default));
        Assert.Equal(original, await File.ReadAllTextAsync(file));
        Assert.Single(Directory.GetFiles(fixture.Path));
        AssertPrivate(fixture.Path, directory: true);
        AssertPrivate(file, directory: false);
    }

    [Fact]
    public async Task Pickup_CancellationAndInvalidKey_DoNotPublishFiles()
    {
        using var fixture = new PickupFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Sender.SendAsync(
            "activation/" + fixture.Job.ToString("N"), "", "admin@example.invalid", fixture.Link, cancellation.Token));
        Assert.False(await fixture.Sender.SendAsync("../../escape", "", "admin@example.invalid", fixture.Link, default));
        Assert.False(Directory.Exists(fixture.Path));
    }

    [Fact]
    public async Task Pickup_RejectsReparseDirectoryAndExistingFileWithoutFollowingThem()
    {
        using var fixture = new PickupFixture();
        Assert.True(await fixture.SendAsync());
        var target = Directory.GetFiles(fixture.Path).Single();
        var linkDirectory = fixture.Path + "-link";
        // Windows directory junctions require no symlink privilege; Unix uses a symbolic link.
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
            start.ArgumentList.Add(linkDirectory); start.ArgumentList.Add(fixture.Path);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(linkDirectory, fixture.Path);
        try
        {
            fixture.Configuration["Activation:DevelopmentPickupPath"] = linkDirectory;
            Assert.False(await fixture.SendAsync());
            fixture.Configuration["Activation:DevelopmentPickupPath"] = fixture.Path;
            var contents = await File.ReadAllTextAsync(target);
            File.Delete(target);
            // A directory at the output file location cannot be overwritten or accepted.
            Directory.CreateDirectory(target);
            Assert.False(await fixture.SendAsync());
            Assert.Empty(Directory.GetFiles(fixture.Path));
            Assert.Contains("Link", contents);
        }
        finally { Directory.Delete(linkDirectory); }
    }

    [Fact]
    public async Task Pickup_RejectsExistingBroadPermissions()
    {
        using var fixture = new PickupFixture();
        Directory.CreateDirectory(fixture.Path);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectoryInfo(fixture.Path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            new DirectoryInfo(fixture.Path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(fixture.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        Assert.False(await fixture.SendAsync());
        Assert.Empty(Directory.GetFiles(fixture.Path));
    }

    [Fact]
    public void Parser_LocalCommandIsExplicitAndNeverWeakensNormalResendConfiguration()
    {
        using var fixture = new PickupFixture();
        Assert.True(ActivationCli.Parse(["dispatch-activation-local"]).IsCommand);
        Assert.True(ActivationCli.Parse(["dispatch-activation-local", "--environment", "Development"]).IsInvalid);
        Assert.True(ActivationCli.Parse(["--environment=Development", "dispatch-activation-local"]).IsInvalid);
        Assert.True(ActivationCli.IsConfigured(ActivationCli.Parse(["dispatch-activation-local"]), fixture.Configuration, fixture.Environment));
        Assert.False(ActivationCli.IsConfigured(ActivationCli.Parse(["dispatch-activation"]), fixture.Configuration, fixture.Environment));
        Assert.False(ActivationCli.IsConfigured(ActivationCli.Parse(["dispatch-activation-local"]), fixture.Configuration));
    }

    [Theory]
    [InlineData("http://localhost/activate")]
    [InlineData("https://app.example.invalid/activate")]
    [InlineData("https://localhost/activate?token=not-a-token")]
    [InlineData("https://localhost/activate#fragment")]
    [InlineData("https://user@localhost/activate")]
    public void LocalCommand_RequiresSafeNominalLoopbackHttpsUrl(string frontend)
    {
        using var fixture = new PickupFixture();
        fixture.Configuration["Activation:FrontendUrl"] = frontend;
        Assert.False(ActivationCli.IsConfigured(ActivationCli.Parse(["dispatch-activation-local"]), fixture.Configuration, fixture.Environment));
        Assert.False(Directory.Exists(fixture.Path));
    }

    private static void AssertPrivate(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var owner = WindowsIdentity.GetCurrent().User;
            Assert.Equal(owner, acl.GetOwner(typeof(SecurityIdentifier)));
            if (directory) Assert.True(acl.AreAccessRulesProtected);
            Assert.NotEmpty(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                Assert.Equal(owner, rule.IdentityReference);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            }
        }
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | (directory ? UnixFileMode.UserExecute : 0), File.GetUnixFileMode(path));
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Static-X.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository not found.");
    }

    private sealed class PickupFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "static-x-local-" + Guid.NewGuid().ToString("N"));
        public Guid Job { get; } = Guid.NewGuid();
        public string Link { get; } = "https://localhost/activate#activation=" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        public IConfigurationRoot Configuration { get; }
        public TestEnvironment Environment { get; }
        public DevelopmentPickupEmailSender Sender { get; }

        public PickupFixture(string environment = "Development")
        {
            Environment = new TestEnvironment { EnvironmentName = environment, ContentRootPath = FindRepository() };
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Activation:DevelopmentPickupEnabled"] = "true",
                ["Activation:DevelopmentDatabaseConfirmed"] = "true",
                ["Activation:DevelopmentPickupPath"] = Path,
                ["Activation:KeyRingPath"] = System.IO.Path.GetTempPath(),
                ["Activation:FrontendUrl"] = "https://localhost/activate",
                ["ConnectionStrings:Static-X"] = "Server=unused;Database=unused;"
            }).Build();
            // Keep the configured ring disjoint from pickup, as required in real operation.
            Configuration["Activation:KeyRingPath"] = FindRepository();
            Sender = new DevelopmentPickupEmailSender(Configuration, Environment);
        }

        public Task<bool> SendAsync() => Sender.SendAsync("activation/" + Job.ToString("N"), "", "admin@example.invalid", Link, default);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Static-X.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingDispatcher : IActivationDispatcher
    {
        public bool Called { get; private set; }
        public Task<bool> DispatchOneAsync(CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(true);
        }
    }

    private sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "The manual helper requires Windows PowerShell 5.1 and Windows ACLs.";
        }
    }
}
