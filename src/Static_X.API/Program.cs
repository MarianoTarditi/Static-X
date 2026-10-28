using DAL;
using BLL;
using BLL.Interfaces;
using BLL.Mappings;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Implementation;
using Static_X.API.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Auth;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Resend;
using Static_X.API;

// Parse operator commands before constructing the HTTP host or touching configuration-backed services.
var command = ActivationCli.Parse(args);
if (command.IsInvalid)
{
    Console.Error.WriteLine("Invalid operator command or arguments. See Docs/ActivationOperations.md.");
    Environment.ExitCode = 2;
    return;
}
var builder = WebApplication.CreateBuilder(command.IsCommand ? Array.Empty<string>() : args);

// Add services to the container.

var connectionString = builder.Configuration.GetConnectionString("Static-X");
if (command.IsCommand && !ActivationCli.IsConfigured(command, builder.Configuration, builder.Environment))
{
    Console.Error.WriteLine("Operator configuration is missing or unsafe.");
    Environment.ExitCode = 2;
    return;
}
if (connectionString is null)
    throw new InvalidOperationException("Connection string 'Static-X' was not found.");

builder.Services.AddDbContext<StaticXDbContext>(options => options.UseSqlServer(connectionString,
    sql => sql.MigrationsAssembly(typeof(StaticXDbContext).Assembly.GetName().Name)));
var activationKeyRing = builder.Configuration["Activation:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(activationKeyRing))
{
    var contentRoot = Path.GetFullPath(builder.Environment.ContentRootPath)
        .TrimEnd(Path.DirectorySeparatorChar);
    var keyRoot = Path.GetFullPath(activationKeyRing).TrimEnd(Path.DirectorySeparatorChar);
    if (!Path.IsPathFullyQualified(activationKeyRing) || !Directory.Exists(activationKeyRing) ||
        keyRoot.Equals(contentRoot, StringComparison.OrdinalIgnoreCase) ||
        keyRoot.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Activation key ring must be an existing external absolute directory.");
    builder.Services.AddDataProtection().SetApplicationName("Static-X.Activation")
        .PersistKeysToFileSystem(new DirectoryInfo(activationKeyRing));
}
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddIdentityCore<GymAccount>(options =>
    {
        options.User.RequireUniqueEmail = false;
        options.SignIn.RequireConfirmedEmail = true;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<StaticXDbContext>()
    .AddSignInManager();
builder.Services.AddScoped<IUserClaimsPrincipalFactory<GymAccount>, GymClaimsPrincipalFactory>();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = async context =>
    {
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        if (context.Principal is null)
            return;
        var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var gymClaim = context.Principal?.FindFirstValue("gym_id");
        if (userId is null || !int.TryParse(gymClaim, NumberStyles.None, CultureInfo.InvariantCulture, out var gymId) || gymId <= 0)
        {
            context.RejectPrincipal();
            return;
        }
        var account = await context.HttpContext.RequestServices.GetRequiredService<IAccountService>()
            .FindByIdAsync(gymId, userId, context.HttpContext.RequestAborted);
        if (account is null || context.Principal?.FindFirstValue(ClaimTypes.Role) != account.Role.ToString())
            context.RejectPrincipal();
    };
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
});
builder.Services.AddAutoMapper(configuration =>
    configuration.LicenseKey = builder.Configuration["AutoMapper:LicenseKey"], typeof(StudentProfile));
builder.Services.AddScoped<IAccountRepository, AccountRepository>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddScoped<IStudentRepository, StudentRepository>();
builder.Services.AddScoped<IStudentService, StudentService>();
builder.Services.AddScoped<IGymRepository, GymRepository>();
builder.Services.AddScoped<IGymService, GymService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IActivationRepository, ActivationRepository>();
builder.Services.AddScoped<IActivationService, ActivationService>();
builder.Services.AddScoped<IActivationDeliveryRepository, ActivationDeliveryRepository>();
builder.Services.AddScoped<IActivationDispatcher, ActivationDispatcher>();
builder.Services.AddTransient<IActivationEmailSender, ResendActivationEmailSender>();
builder.Services.AddHttpClient<ResendClient>();
builder.Services.Configure<ResendClientOptions>(options =>
{
    options.ApiToken = builder.Configuration["Resend:ApiKey"] ?? string.Empty;
    options.ThrowExceptions = false;
});
builder.Services.AddTransient<IResend, ResendClient>();

if (command.Name == "dispatch-activation-local")
{
    // CLI operations must not leak SQL parameter values or provider diagnostics in Development.
    builder.Logging.ClearProviders();
    builder.Services.AddScoped<IActivationEmailSender, DevelopmentPickupEmailSender>();
    builder.Services.AddScoped<IActivationDispatcher>(services => new ActivationDispatcher(
        services.GetRequiredService<IActivationDeliveryRepository>(),
        services.GetRequiredService<IDataProtectionProvider>(), builder.Configuration,
        services.GetRequiredService<TimeProvider>(), services.GetRequiredService<IActivationEmailSender>(),
        requireResendConfiguration: false));
}

builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

builder.Services.AddOpenApi();

var app = builder.Build();
if (command.IsCommand)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var completed = await ActivationCli.ExecuteAsync(command, scope.ServiceProvider);
        Console.WriteLine(completed ? "Operator command completed." : "No eligible operation was found.");
        Environment.ExitCode = completed ? 0 : 3;
    }
    catch (Exception)
    {
        // Provider/database exceptions may include recipient addresses, tokens or connection details.
        Console.Error.WriteLine("Operator command failed. Inspect restricted service diagnostics without exposing secrets.");
        Environment.ExitCode = 1;
    }
    return;
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/students"), branch =>
    branch.UseMiddleware<Static_X.API.Middleware.StudentApiErrorsMiddleware>());

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }
