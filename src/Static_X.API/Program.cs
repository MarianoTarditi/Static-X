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
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Resend;
using System.Threading.RateLimiting;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

var connectionString = builder.Configuration.GetConnectionString("Static-X");
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
        options.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider.ProviderName;
    })
    .AddEntityFrameworkStores<StaticXDbContext>()
    .AddSignInManager()
    .AddTokenProvider<PasswordResetTokenProvider>(PasswordResetTokenProvider.ProviderName);
builder.Services.Configure<PasswordResetTokenProviderOptions>(_ => { });
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
        // Check every request, even at the exact cookie issuance time; the periodic validator alone delays revocation.
        var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<GymAccount>>();
        if (await signIn.ValidateSecurityStampAsync(context.Principal) is null)
        {
            context.RejectPrincipal();
            await signIn.SignOutAsync();
            return;
        }
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        if (context.Principal is null)
            return;
        var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null || !AccountScope.TryGetGymId(context.Principal!, out var gymId))
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
builder.Services.AddScoped<IDashboardRepository, DashboardRepository>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IRoutineRepository, RoutineRepository>();
builder.Services.AddScoped<IRoutineService, RoutineService>();
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IGymRepository, GymRepository>();
builder.Services.AddScoped<IGymService, GymService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IActivationRepository, ActivationRepository>();
builder.Services.AddScoped<IActivationService, ActivationService>();
builder.Services.AddScoped<IActivationDeliveryRepository, ActivationDeliveryRepository>();
builder.Services.AddScoped<IActivationDispatcher, ActivationDispatcher>();
builder.Services.AddScoped<IPasswordRecoveryDispatcher, PasswordRecoveryDispatcher>();
builder.Services.AddTransient<IPasswordRecoveryEmailSender, ResendPasswordRecoveryEmailSender>();
builder.Services.AddHostedService<PasswordRecoveryOutboxWorker>();
builder.Services.AddHostedService<Static_X.API.Integrations.ActivationOutboxWorker>();
builder.Services.AddTransient<IActivationEmailSender, ResendActivationEmailSender>();
builder.Services.AddHttpClient<ResendClient>();
builder.Services.Configure<ResendClientOptions>(options =>
{
    options.ApiToken = builder.Configuration["Resend:ApiKey"] ?? string.Empty;
    options.ThrowExceptions = false;
});
builder.Services.AddTransient<IResend, ResendClient>();

builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("password-recovery", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5, Window = TimeSpan.FromMinutes(15), QueueLimit = 0, AutoReplenishment = true
        }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry.TotalSeconds : 900;
        context.HttpContext.Response.StatusCode = 429;
        context.HttpContext.Response.Headers.RetryAfter = Math.Clamp(Math.Ceiling(seconds), 1, 900).ToString(CultureInfo.InvariantCulture);
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails { Status = 429, Title = "Too many recovery requests." }, cancellationToken);
    };
});

var app = builder.Build();
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    Static_X.API.ActivationPage.ActivationPageEndpoints.Map(app);
}

app.UseHttpsRedirection();

app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/students") ||
    context.Request.Path.StartsWithSegments("/api/student/me/routine"), branch =>
    branch.UseMiddleware<Static_X.API.Middleware.StudentApiErrorsMiddleware>());
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/accounts/invitations"), branch =>
    branch.UseMiddleware<Static_X.API.Middleware.InvitationApiErrorsMiddleware>());

// Apply only to the new administrative GET endpoints, including authorization short circuits.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<Static_X.API.Filters.AdministrativeQueryResponseAttribute>() is not null)
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<Static_X.API.Middleware.PaymentAccessMiddleware>();
app.UseRateLimiter();

app.MapControllers();

app.Run();

public partial class Program { }
