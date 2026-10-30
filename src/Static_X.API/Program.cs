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
builder.Services.AddScoped<IGymRepository, GymRepository>();
builder.Services.AddScoped<IGymService, GymService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IActivationRepository, ActivationRepository>();
builder.Services.AddScoped<IActivationService, ActivationService>();
builder.Services.AddScoped<IActivationDeliveryRepository, ActivationDeliveryRepository>();
builder.Services.AddScoped<IActivationDispatcher, ActivationDispatcher>();
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

var app = builder.Build();
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/students"), branch =>
    branch.UseMiddleware<Static_X.API.Middleware.StudentApiErrorsMiddleware>());

// Apply only to the new administrative GET endpoints, including authorization short circuits.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<Static_X.API.Filters.AdministrativeQueryResponseAttribute>() is not null)
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }
