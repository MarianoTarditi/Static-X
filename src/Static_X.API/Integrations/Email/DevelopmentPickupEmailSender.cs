using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using BLL.Interfaces;

namespace Static_X.API.Integrations;

// Selected only by the explicit local CLI command, never by HTTP startup or normal dispatch.
public sealed class DevelopmentPickupEmailSender : IActivationEmailSender
{
    private readonly IConfiguration configuration;
    private readonly IHostEnvironment environment;

    public DevelopmentPickupEmailSender(IConfiguration configuration, IHostEnvironment environment)
    {
        this.configuration = configuration;
        this.environment = environment;
    }

    public string ValidateDirectory()
    {
        try
        {
            if (!environment.IsDevelopment() || configuration["Activation:DevelopmentPickupEnabled"] != "true" ||
                configuration["Activation:DevelopmentDatabaseConfirmed"] != "true")
                throw new InvalidOperationException();
            var configured = configuration["Activation:DevelopmentPickupPath"];
            if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
                throw new InvalidOperationException();
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local) || !IsWithin(path, Path.GetFullPath(local)) ||
                IsWithinOrEqual(path, environment.ContentRootPath) ||
                IsWithinOrEqual(environment.ContentRootPath, path) ||
                Path.TrimEndingDirectorySeparator(Path.GetTempPath()).Equals(path, PathComparison))
                throw new InvalidOperationException();
            // Reject the repository root and all its descendants, including sibling projects/docs.
            for (var root = new DirectoryInfo(environment.ContentRootPath); root is not null; root = root.Parent)
                if (Directory.Exists(Path.Combine(root.FullName, ".git")) || File.Exists(Path.Combine(root.FullName, ".git")))
                {
                    if (IsWithinOrEqual(path, root.FullName)) throw new InvalidOperationException();
                    break;
                }
            var ring = configuration["Activation:KeyRingPath"];
            if (!string.IsNullOrWhiteSpace(ring) &&
                (IsWithinOrEqual(path, ring) || IsWithinOrEqual(ring, path)))
                throw new InvalidOperationException();
            var parent = Directory.GetParent(path);
            if (parent is null || !parent.Exists) throw new InvalidOperationException();
            RejectLinks(parent.FullName);
            if (Directory.Exists(path))
            {
                RejectLinks(path);
                VerifyPrivate(path, directory: true);
            }
            else if (File.Exists(path)) throw new InvalidOperationException();
            return path;
        }
        catch (Exception)
        {
            // Never propagate filesystem exceptions, which can contain sensitive paths.
            throw new InvalidOperationException("Development pickup configuration is missing or unsafe.");
        }
    }

    public async Task<bool> SendAsync(string idempotencyKey, string from, string to, string link,
        CancellationToken cancellationToken)
    {
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = ValidateDirectory(); // Independent runtime environment/opt-in gate.
            if (!idempotencyKey.StartsWith("activation/", StringComparison.Ordinal) ||
                !Guid.TryParseExact(idempotencyKey[11..], "N", out var job)) return false;
            CreatePrivateDirectory(directory);
            RejectLinks(directory);
            VerifyPrivate(directory, directory: true);
            var destination = Path.Combine(directory, job.ToString("N") + ".json");
            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["IdempotencyKey"] = idempotencyKey, ["Email"] = to, ["Link"] = link
            });
            if (File.Exists(destination)) return await MatchesAsync(destination, payload, cancellationToken);
            temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pending");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(payload.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            VerifyPrivate(temporary, directory: false);
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination))
            {
                return await MatchesAsync(destination, payload, cancellationToken);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception) { /* No secret-bearing diagnostics. */ }
        }
    }

    private static async Task<bool> MatchesAsync(string path, string payload, CancellationToken ct)
    {
        RejectLinks(path);
        VerifyPrivate(path, directory: false);
        return new FileInfo(path).Length <= 16384 && await File.ReadAllTextAsync(path, ct) == payload;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsWithin(string path, string root) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, PathComparison);

    private static bool IsWithinOrEqual(string path, string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), PathComparison) || IsWithin(path, root);

    private static void RejectLinks(string path)
    {
        for (var item = new FileInfo(path); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException();
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else throw new PlatformNotSupportedException();
    }

    private static void VerifyPrivate(string path, bool directory)
    {
        if (OperatingSystem.IsWindows()) VerifyWindowsPrivate(path, directory);
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                (directory ? UnixFileMode.UserExecute : 0);
            if (File.GetUnixFileMode(path) != expected) throw new InvalidOperationException();
        }
        else throw new PlatformNotSupportedException();
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsPrivate(string path, bool directory)
    {
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
        if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
            (directory && !security.AreAccessRulesProtected)) throw new InvalidOperationException();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (rules.Count == 0) throw new InvalidOperationException();
        foreach (FileSystemAccessRule rule in rules)
            if (!rule.IdentityReference.Equals(owner) || rule.AccessControlType != AccessControlType.Allow)
                throw new InvalidOperationException();
    }
}
