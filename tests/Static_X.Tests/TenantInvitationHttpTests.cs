using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BLL.DTOs.Activations;
using BLL.Services.Interfaces;
using DAL;
using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed partial class AccountHttpTests
{
    private const string InvitationsRoute = "/api/accounts/invitations";

    public static IEnumerable<object[]> InvitationMatrix() =>
        from actor in new[] { AccountRole.GymAdmin, AccountRole.Trainer, AccountRole.Student, AccountRole.SuperAdmin }
        from grant in new[] { AccountRole.GymAdmin, AccountRole.Trainer, AccountRole.Student, AccountRole.SuperAdmin }
        select new object[] { actor, grant };

    [Theory]
    [MemberData(nameof(InvitationMatrix))]
    public async Task TenantInvite_ActorGrantMatrix_IsEnforcedWithoutTenantOrRoleSpoofing(AccountRole actor, AccountRole grant)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var other = await app.CreateGymAsync();
        var studentId = await InvitationStudentAsync(app, gym);
        if (actor == AccountRole.SuperAdmin) await app.CreateGlobalAsync("actor@example.invalid", "CorrectPassword1!");
        else await app.CreateAccountAsync(gym, "actor@example.invalid", "CorrectPassword1!", true, actor);
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, actor == AccountRole.SuperAdmin ? null : gym,
            "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        using var response = await app.PostAsync(client, InvitationsRoute, new
        { key = "matrix", email = "recipient@example.invalid", role = grant, studentId = grant == AccountRole.Student ? (int?)studentId : null, gymId = other, actorRole = 1 });
        var allowed = actor == AccountRole.GymAdmin && grant is AccountRole.Trainer or AccountRole.Student ||
            actor == AccountRole.Trainer && grant == AccountRole.Student;
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        if (!allowed)
        {
            Assert.Empty(await db.TenantInvitationRequests.ToListAsync());
            Assert.Empty(await db.ActivationInvitations.ToListAsync());
            Assert.Null((await db.Alumnos.SingleAsync()).AccountId);
            return;
        }
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "accountId" }, body.EnumerateObject().Select(p => p.Name));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var account = await db.Users.SingleAsync(u => u.Id == body.GetProperty("accountId").GetString());
        Assert.Equal((gym, grant, false, true), (account.GymId, account.Role, account.EmailConfirmed, account.IsEnabled));
        Assert.Null(account.PasswordHash);
        Assert.Equal(grant == AccountRole.Student ? account.Id : null, (await db.Alumnos.SingleAsync()).AccountId);
        Assert.Equal(grant, (await db.ActivationInvitations.SingleAsync()).ExpectedRole);
        Assert.Empty(await db.ProvisioningRequests.ToListAsync());
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("prelogin")]
    public async Task TenantInvite_RequiresAuthenticatedSessionBoundCsrf(string scenario)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        await app.CreateAccountAsync(gym, "actor@example.invalid", "CorrectPassword1!", true, AccountRole.GymAdmin);
        using var client = app.Client();
        var token = (await client.GetFromJsonAsync<JsonElement>("/api/account/csrf")).GetProperty("token").GetString();
        if (scenario != "anonymous") Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, InvitationsRoute)
        { Content = JsonContent.Create(new { key = "csrf", email = "recipient@example.invalid", role = 2 }) };
        if (scenario != "missing") request.Headers.Add("X-CSRF-TOKEN", scenario == "invalid" ? "invalid" : token);
        using var response = await client.SendAsync(request);
        Assert.Equal(scenario == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var scope = app.Factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().TenantInvitationRequests.ToListAsync());
    }

    [Theory]
    [InlineData("missing", 404)]
    [InlineData("foreign", 404)]
    [InlineData("inactive", 409)]
    [InlineData("student-required", 400)]
    [InlineData("student-zero", 400)]
    [InlineData("student-negative", 400)]
    [InlineData("trainer-prohibits-student", 400)]
    [InlineData("undefined-role", 400)]
    [InlineData("key-blank", 400)]
    [InlineData("key-long", 400)]
    [InlineData("email-format", 400)]
    [InlineData("email-display", 400)]
    [InlineData("email-long", 400)]
    public async Task TenantInvite_InvalidTargetsAndInputs_DoNotCreateOrLinkAccounts(string scenario, int expected)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var other = await app.CreateGymAsync();
        var selected = await InvitationStudentAsync(app, scenario == "foreign" ? other : gym, scenario != "inactive");
        using var client = await InvitationActorAsync(app, gym);
        using var response = await app.PostAsync(client, InvitationsRoute, new
        {
            key = scenario == "key-blank" ? " " : scenario == "key-long" ? new string('k', 129) : "invalid",
            email = scenario == "email-format" ? "invalid" : scenario == "email-display" ? "Name <mail@example.invalid>" :
                scenario == "email-long" ? new string('a', 257) + "@example.invalid" : "recipient@example.invalid",
            role = scenario == "undefined-role" ? 99 : scenario == "trainer-prohibits-student" ? 2 : 3,
            studentId = scenario == "student-required" ? (int?)null : scenario == "student-zero" ? 0 : scenario == "student-negative" ? -1 : scenario == "missing" ? int.MaxValue : selected
        });
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Single(await db.Users.ToListAsync());
        Assert.Null((await db.Alumnos.SingleAsync()).AccountId);
        Assert.Empty(await db.TenantInvitationRequests.ToListAsync());
        Assert.Empty(await db.ActivationOutbox.ToListAsync());
    }

    [Theory]
    [InlineData(AccountRole.Trainer)]
    [InlineData(AccountRole.Student)]
    public async Task TenantInvite_KeyRetryReissueAndActivation_PreserveSnapshotsAndSingleUse(AccountRole role)
    {
        var clock = new InvitationClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock);
        var gym = await app.CreateGymAsync();
        var student = role == AccountRole.Student ? (int?)await InvitationStudentAsync(app, gym) : null;
        using var actor = await InvitationActorAsync(app, gym);
        var firstBody = new InviteAccountRequest("first", " Mixed@Example.invalid ", role, student);
        using var first = await app.PostAsync(actor, InvitationsRoute, firstBody);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var accountId = (await first.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var oldToken = await InvitationTokenAsync(app, accountId);
        using var retry = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("first", "mixed@example.invalid", role, student));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == accountId);
            Assert.Equal("MIXED@EXAMPLE.INVALID", user.NormalizedEmail);
            user.AccessFailedCount = 3;
            await db.SaveChangesAsync();
            Assert.Single(await db.ActivationInvitations.ToListAsync());
            Assert.Single(await db.ActivationOutbox.ToListAsync());
            if (student is not null) Assert.Equal("contact@example.invalid", (await db.Alumnos.SingleAsync()).Email);
        }
        using var conflict = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("first", "different@example.invalid", role, student));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        clock.Now = clock.Now.AddHours(1);
        using var reissue = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("second", "mixed@example.invalid", role, student));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await reissue.Content.ReadAsStringAsync());
        var token = await InvitationTokenAsync(app, accountId);
        Assert.NotEqual(oldToken, token);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/activation", new { token = oldToken, password = "CorrectPassword1!" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/activation", new { token, password = "weak" })).StatusCode);
        using var activated = await app.PostAsync(anonymous, "/api/account/activation", new { token, password = "CorrectPassword1!", role = 4, gymId = 9999 });
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal("{\"gymId\":" + gym + "}", await activated.Content.ReadAsStringAsync());
        Assert.False(activated.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/activation", new { token, password = "DifferentPassword1!" })).StatusCode);
        using var afterActivationRetry = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("first", "mixed@example.invalid", role, student));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await afterActivationRetry.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("third", "mixed@example.invalid", role, student))).StatusCode);
        using var verification = app.Factory.Services.CreateScope();
        var context = verification.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var persisted = await context.Users.SingleAsync(u => u.Id == accountId);
        Assert.Equal((role, gym, true, 3), (persisted.Role, persisted.GymId, persisted.EmailConfirmed, persisted.AccessFailedCount));
        Assert.True(await verification.ServiceProvider.GetRequiredService<UserManager<GymAccount>>().CheckPasswordAsync(persisted, "CorrectPassword1!"));
        Assert.Equal(2, await context.TenantInvitationRequests.CountAsync());
        Assert.Equal(2, await context.ActivationOutbox.CountAsync());
        Assert.All(await context.ActivationOutbox.ToListAsync(), o => Assert.Empty(o.ProtectedToken));
    }

    [Theory]
    [InlineData("historical")]
    [InlineData("other-ficha")]
    [InlineData("other-role")]
    public async Task TenantInvite_DoesNotAdoptExistingIdentityOrInferLinkFromContactEmail(string scenario)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var first = await InvitationStudentAsync(app, gym);
        var second = await InvitationStudentAsync(app, gym, email: "recipient@example.invalid");
        using var actor = await InvitationActorAsync(app, gym);
        if (scenario == "historical")
        {
            using var scope = app.Factory.Services.CreateScope();
            var user = new GymAccount { Id = "historical", UserName = "historical", GymId = gym, Role = AccountRole.Student, Email = "recipient@example.invalid" };
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>().CreateAsync(user)).Succeeded);
        }
        else Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(actor, InvitationsRoute,
            new InviteAccountRequest("original", "recipient@example.invalid", scenario == "other-role" ? AccountRole.Trainer : AccountRole.Student,
                scenario == "other-role" ? null : first))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(actor, InvitationsRoute,
            new InviteAccountRequest("adopt", "recipient@example.invalid", AccountRole.Student, second))).StatusCode);
        using var check = app.Factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Null((await db.Alumnos.SingleAsync(s => s.Id == second)).AccountId);
        Assert.False(await db.TenantInvitationRequests.AnyAsync(r => r.Key == "adopt"));
    }

    [Fact]
    public async Task LinkedLifecycle_RevokesCurrentAndCopiedCookiesAndRecoveryWithoutRestoringOldStamp()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("lifecycle", "member@example.invalid", AccountRole.Student, student));
        var accountId = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var token = await InvitationTokenAsync(app, accountId);
        using var current = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(current, "/api/account/activation", new { token, password = "CorrectPassword1!" })).StatusCode);
        using var login = await app.LoginAsync(current, gym, "member@example.invalid", "CorrectPassword1!");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var copied = app.Factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        copied.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".AspNetCore.Identity.Application=")).Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await copied.GetAsync("/api/account/me")).StatusCode);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await app.PostAsync(anonymous, "/api/account/forgot-password", new { email = "member@example.invalid", gymId = gym })).StatusCode);
        string recovery;
        string stamp;
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var job = await db.PasswordRecoveryOutbox.SingleAsync();
            recovery = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Static-X.PasswordRecoveryDelivery.v1", job.Id.ToString("N")).Unprotect(job.ProtectedPayload);
            stamp = (await db.Users.SingleAsync(u => u.Id == accountId)).SecurityStamp!;
        }
        Assert.Equal(HttpStatusCode.OK, (await InvitationPatchAsync(app, actor, student, "deactivate")).StatusCode);
        foreach (var session in new[] { current, copied }) Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(anonymous, gym, "member@example.invalid", "CorrectPassword1!")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/reset-password", new { token = recovery, newPassword = "NewPassword2!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await app.PostAsync(anonymous, "/api/account/forgot-password", new { email = "member@example.invalid", gymId = gym })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await InvitationPatchAsync(app, actor, student, "reactivate")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/reset-password", new { token = recovery, newPassword = "NewPassword2!" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(anonymous, gym, "member@example.invalid", "CorrectPassword1!")).StatusCode);
        using var verify = app.Factory.Services.CreateScope();
        var context = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var user = await context.Users.SingleAsync(u => u.Id == accountId);
        Assert.True(user.IsEnabled);
        Assert.False(user.StudentAccessSuspended);
        Assert.NotEqual(stamp, user.SecurityStamp);
        Assert.Single(await context.PasswordRecoveryOutbox.ToListAsync());
        Assert.Null((await context.PasswordRecoveryOutbox.SingleAsync()).ConsumedAt);
    }

    [Fact]
    public async Task PendingLinkedLifecycle_PausesActivationAndMailWithoutConsumptionThenAllowsUnexpiredToken()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("pending", "member@example.invalid", AccountRole.Student, student));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var token = await InvitationTokenAsync(app, id);
        using var deliveryScope = app.Factory.Services.CreateScope();
        var deliveries = deliveryScope.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>();
        var lease = await deliveries.LeaseDueAsync(DateTimeOffset.UtcNow, default);
        Assert.NotNull(lease);
        Assert.Equal(HttpStatusCode.OK, (await InvitationPatchAsync(app, actor, student, "deactivate")).StatusCode);
        Assert.False(await deliveries.IsCurrentAsync(lease, DateTimeOffset.UtcNow, default));
        await deliveries.CompleteAsync(lease, DateTimeOffset.UtcNow, false, default);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsync(anonymous, "/api/account/activation", new { token, password = "CorrectPassword1!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("reissue-paused", "member@example.invalid", AccountRole.Student, student))).StatusCode);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            Assert.Null((await db.Users.SingleAsync(u => u.Id == id)).PasswordHash);
            Assert.Null((await db.ActivationInvitations.SingleAsync()).ConsumedAt);
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>().LeaseDueAsync(DateTimeOffset.UtcNow, default));
            Assert.NotEmpty((await db.ActivationOutbox.SingleAsync()).ProtectedToken);
        }
        Assert.Equal(HttpStatusCode.OK, (await InvitationPatchAsync(app, actor, student, "reactivate")).StatusCode);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == id);
            Assert.False(user.EmailConfirmed);
            Assert.Null(user.PasswordHash);
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>().LeaseDueAsync(DateTimeOffset.UtcNow.AddMinutes(3), default));
        }
        Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(anonymous, "/api/account/activation", new { token, password = "CorrectPassword1!" })).StatusCode);
    }

    [Theory]
    [InlineData(256, 200)]
    [InlineData(257, 400)]
    public async Task TenantInvite_RecipientBoundary_IsIndependentOf320CharacterContact(int length, int status)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var contact = new string('c', 64) + "@" + new string('d', 251) + ".com";
        Assert.Equal(320, contact.Length);
        var student = await InvitationStudentAsync(app, gym, email: contact);
        using var actor = await InvitationActorAsync(app, gym);
        var email = new string('a', 64) + "@" + new string('b', length - 69) + ".com";
        Assert.Equal(length, email.Length);
        using var response = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("boundary", " " + email + " ", AccountRole.Student, student));
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(contact, (await db.Alumnos.SingleAsync()).Email);
        if (status == 200) Assert.Equal(email.ToUpperInvariant(), (await db.Users.SingleAsync(u => u.Role == AccountRole.Student)).NormalizedEmail);
        else Assert.Null((await db.Alumnos.SingleAsync()).AccountId);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("gym")]
    [InlineData("link")]
    [InlineData("expired")]
    public async Task TenantActivation_ChangedPersistentBindingOrExpiry_FailsWithoutConsumptionOrDelivery(string scenario)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var other = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("binding", "member@example.invalid", AccountRole.Student, student));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var token = await InvitationTokenAsync(app, id);
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == id);
            if (scenario == "role") user.Role = AccountRole.Trainer;
            if (scenario == "gym") user.GymId = other;
            if (scenario == "link") (await db.Alumnos.SingleAsync()).AccountId = null;
            if (scenario == "expired") (await db.ActivationInvitations.SingleAsync()).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        using var anonymous = app.Client();
        using var response = await app.PostAsync(anonymous, "/api/account/activation", new { token, password = "CorrectPassword1!" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("gymId", await response.Content.ReadAsStringAsync());
        using var verify = app.Factory.Services.CreateScope();
        var context = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Null((await context.ActivationInvitations.SingleAsync()).ConsumedAt);
        Assert.Null((await context.Users.SingleAsync(u => u.Id == id)).PasswordHash);
        Assert.Null(await verify.ServiceProvider.GetRequiredService<IActivationDeliveryRepository>().LeaseDueAsync(DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task TenantInvite_SameKeyConcurrentRequests_HaveOneAccountLinkInvitationAndOutbox()
    {
        await using var app = await TestApp.CreateAsync(activation: true, recoveryConcurrency: true);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var first = await InvitationActorAsync(app, gym);
        using var second = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(second, gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        var body = new InviteAccountRequest("concurrent", "member@example.invalid", AccountRole.Student, student);
        var responses = await Task.WhenAll(app.PostAsync(first, InvitationsRoute, body), app.PostAsync(second, InvitationsRoute, body));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(await responses[0].Content.ReadAsStringAsync(), await responses[1].Content.ReadAsStringAsync());
        foreach (var response in responses) response.Dispose();
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var user = Assert.Single(await db.Users.Where(u => u.Role == AccountRole.Student).ToListAsync());
        Assert.Equal(user.Id, (await db.Alumnos.SingleAsync()).AccountId);
        Assert.Single(await db.TenantInvitationRequests.ToListAsync());
        Assert.Single(await db.ActivationInvitations.ToListAsync());
        Assert.Single(await db.ActivationOutbox.ToListAsync());
    }

    [Theory]
    [InlineData("activate")]
    [InlineData("reissue")]
    public async Task LinkedLifecycle_ConcurrentDisableAndInvitationWriter_EndsDisabledWithoutActivationBackdoor(string operation)
    {
        await using var app = await TestApp.CreateAsync(activation: true, recoveryConcurrency: true);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("race", "member@example.invalid", AccountRole.Student, student));
        var id = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var token = await InvitationTokenAsync(app, id);
        using var second = app.Client();
        if (operation == "reissue") Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(second, gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        var writer = operation == "activate"
            ? app.PostAsync(second, "/api/account/activation", new { token, password = "CorrectPassword1!" })
            : app.PostAsync(second, InvitationsRoute, new InviteAccountRequest("race-reissue", "member@example.invalid", AccountRole.Student, student));
        var results = await Task.WhenAll(writer, InvitationPatchAsync(app, actor, student, "deactivate"));
        Assert.Equal(HttpStatusCode.OK, results[1].StatusCode);
        Assert.Contains(results[0].StatusCode, new[] { HttpStatusCode.OK, operation == "activate" ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict });
        using var verify = app.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        Assert.False(user.IsEnabled);
        Assert.True(user.StudentAccessSuspended);
        Assert.False((await db.Alumnos.SingleAsync()).Activo);
        if (operation == "activate") Assert.Equal(results[0].IsSuccessStatusCode, user.EmailConfirmed);
        else Assert.Null(user.PasswordHash);
        foreach (var result in results) result.Dispose();
        using var login = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.LoginAsync(login, gym, "member@example.invalid", "CorrectPassword1!")).StatusCode);
    }

    [Fact]
    public async Task ContactEdit_DoesNotChangeLinkedLoginOrAccountIdentifier()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var actor = await InvitationActorAsync(app, gym);
        using var invite = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("contact", "login@example.invalid", AccountRole.Student, student));
        var accountId = (await invite.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/students/{student}")
        { Content = JsonContent.Create(new { nombre = "Updated", apellido = "Student", email = "newcontact@example.invalid", accountId = "spoof" }) };
        request.Headers.Add("X-CSRF-TOKEN", (await actor.GetFromJsonAsync<JsonElement>("/api/account/csrf")).GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await actor.SendAsync(request)).StatusCode);
        using var verify = app.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal((accountId, "NEWCONTACT@EXAMPLE.INVALID"), ((await db.Alumnos.SingleAsync()).AccountId, (await db.Alumnos.SingleAsync()).Email));
        Assert.Equal("login@example.invalid", (await db.Users.SingleAsync(u => u.Id == accountId)).Email);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenantInvite_CommitFailure_RollsBackNewAccountLinkOrReissueAndRetainsOriginalToken(bool reissue)
    {
        var interceptor = new TenantCommitFailure();
        var clock = new InvitationClock();
        await using var app = await TestApp.CreateAsync(activation: true, clock: clock, tenantCommitInterceptor: interceptor);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var actor = await InvitationActorAsync(app, gym);
        string? originalId = null;
        string? originalToken = null;
        if (reissue)
        {
            using var initial = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("before", "member@example.invalid", AccountRole.Student, student));
            originalId = (await initial.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
            originalToken = await InvitationTokenAsync(app, originalId);
        }
        clock.Now = clock.Now.AddHours(1);
        interceptor.Fail = true;
        using var failed = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("fail", "member@example.invalid", AccountRole.Student, student));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        using (var verify = app.Factory.Services.CreateScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
            Assert.Equal(reissue ? 2 : 1, await db.Users.CountAsync());
            Assert.Equal(originalId, (await db.Alumnos.SingleAsync()).AccountId);
            Assert.Equal(reissue ? 1 : 0, await db.TenantInvitationRequests.CountAsync());
            Assert.Equal(reissue ? 1 : 0, await db.ActivationInvitations.CountAsync());
            Assert.Equal(reissue ? 1 : 0, await db.ActivationOutbox.CountAsync());
            Assert.False(await db.ActivationInvitations.AnyAsync(i => i.RevokedAt != null));
        }
        interceptor.Fail = false;
        if (reissue)
        {
            Assert.Equal(originalToken, await InvitationTokenAsync(app, originalId!));
            using var anonymous = app.Client();
            Assert.Equal(HttpStatusCode.OK, (await app.PostAsync(anonymous, "/api/account/activation", new { token = originalToken, password = "CorrectPassword1!" })).StatusCode);
        }
    }

    [Fact]
    public async Task TenantInvite_ConcurrentDifferentRecipientsForSameFicha_OneWinsWithoutAccountAdoption()
    {
        await using var app = await TestApp.CreateAsync(activation: true, recoveryConcurrency: true);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var first = await InvitationActorAsync(app, gym);
        using var second = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(second, gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        var results = await Task.WhenAll(
            app.PostAsync(first, InvitationsRoute, new InviteAccountRequest("first", "first@example.invalid", AccountRole.Student, student)),
            app.PostAsync(second, InvitationsRoute, new InviteAccountRequest("second", "second@example.invalid", AccountRole.Student, student)));
        var winner = Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        var id = (await winner.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        using var verify = app.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal(id, Assert.Single(await db.Users.Where(u => u.Role == AccountRole.Student).ToListAsync()).Id);
        Assert.Equal(id, (await db.Alumnos.SingleAsync()).AccountId);
        Assert.Single(await db.TenantInvitationRequests.ToListAsync());
        Assert.Single(await db.ActivationOutbox.ToListAsync());
        foreach (var result in results) result.Dispose();
    }

    [Fact]
    public async Task TenantActivation_ConcurrentStudentClaims_CommitOnlyOneChosenPassword()
    {
        await using var app = await TestApp.CreateAsync(activation: true, recoveryConcurrency: true);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var actor = await InvitationActorAsync(app, gym);
        using var invitation = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("claims", "member@example.invalid", AccountRole.Student, student));
        var id = (await invitation.Content.ReadFromJsonAsync<AccountInvitationResponse>())!.AccountId;
        var token = await InvitationTokenAsync(app, id);
        using var first = app.Client();
        using var second = app.Client();
        var results = await Task.WhenAll(
            app.PostAsync(first, "/api/account/activation", new { token, password = "FirstPassword1!" }),
            app.PostAsync(second, "/api/account/activation", new { token, password = "SecondPassword1!" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.BadRequest);
        using var verify = app.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == id);
        var hasher = verify.ServiceProvider.GetRequiredService<UserManager<GymAccount>>().PasswordHasher;
        Assert.Equal(1, new[] { "FirstPassword1!", "SecondPassword1!" }.Count(password =>
            hasher.VerifyHashedPassword(user, user.PasswordHash!, password) != PasswordVerificationResult.Failed));
        Assert.NotNull((await db.ActivationInvitations.SingleAsync()).ConsumedAt);
        Assert.Empty((await db.ActivationOutbox.SingleAsync()).ProtectedToken);
        foreach (var result in results) result.Dispose();
    }

    [Fact]
    public async Task DisabledAccount_RejectsDirectPasswordChangeAndRecoveryDispatchWithoutCredentialOrLockoutChanges()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var account = await app.CreateAccountAsync(gym, "disabled@example.invalid", "CorrectPassword1!", true);
        using var anonymous = app.Client();
        Assert.Equal(HttpStatusCode.Accepted, (await app.PostAsync(anonymous, "/api/account/forgot-password", new { email = "disabled@example.invalid", gymId = gym })).StatusCode);
        string hash;
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var user = await db.Users.SingleAsync();
            hash = user.PasswordHash!;
            user.IsEnabled = false;
            user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
            user.AccessFailedCount = 4;
            await db.SaveChangesAsync();
        }
        using (var scope = app.Factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
            Assert.False(await repository.ChangePasswordAsync(gym, account.Id, "CorrectPassword1!", "NewPassword2!", default));
            Assert.Null(await repository.FindByIdAsync(gym, account.Id, default));
            Assert.Null(await repository.LeaseDueAsync(DateTimeOffset.UtcNow, default));
        }
        using var verify = app.Factory.Services.CreateScope();
        var context = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var persisted = await context.Users.SingleAsync();
        Assert.True(hash == persisted.PasswordHash, "Disabled password changes must not mutate credentials.");
        Assert.False(persisted.IsEnabled);
        Assert.Equal(4, persisted.AccessFailedCount);
        Assert.True(persisted.LockoutEnd > DateTimeOffset.UtcNow);
        var job = await context.PasswordRecoveryOutbox.SingleAsync();
        Assert.NotNull(job.RevokedAt);
        Assert.Empty(job.ProtectedPayload);
    }

    [Fact]
    public async Task GlobalPendingAdministrators_ExcludeDisabledTargetsAndReissueRemainsNotFound()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var provisioned = await app.ProvisionAsync();
        await app.CreateGlobalAsync("global@example.invalid", "CorrectPassword1!");
        string id;
        using (var scope = app.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            var admin = await db.Users.SingleAsync(u => u.GymId == provisioned.Gym);
            id = admin.Id;
            admin.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, null, "global@example.invalid", "CorrectPassword1!")).StatusCode);
        using var listed = await client.GetAsync($"/api/gyms/{provisioned.Gym}/administrators/pending");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var page = await listed.Content.ReadFromJsonAsync<BLL.DTOs.Gyms.PendingAdministratorListResponse>();
        Assert.Empty(page!.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsync(client, $"/api/gyms/{provisioned.Gym}/administrators/{id}/invitation", null)).StatusCode);
        using var verify = app.Factory.Services.CreateScope();
        Assert.Single(await verify.ServiceProvider.GetRequiredService<StaticXDbContext>().ActivationInvitations.ToListAsync());
    }

    [Theory]
    [InlineData(128, 200)]
    [InlineData(129, 400)]
    public async Task TenantInvite_KeyLengthBoundary_IsNotSilentlyTruncated(int length, int status)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        using var actor = await InvitationActorAsync(app, gym);
        var key = new string('k', length);
        using var response = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest(key, "trainer@example.invalid", AccountRole.Trainer));
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        using var verify = app.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        if (status == 200) Assert.Equal(key, (await db.TenantInvitationRequests.SingleAsync()).Key);
        else Assert.Empty(await db.TenantInvitationRequests.ToListAsync());
    }

    [Theory]
    [InlineData("role")]
    [InlineData("student")]
    public async Task TenantInvite_SameKeyChangedRoleOrStudentSnapshot_ConflictsWithoutReissue(string field)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var first = await InvitationStudentAsync(app, gym);
        var second = await InvitationStudentAsync(app, gym, email: "other-contact@example.invalid");
        using var actor = await InvitationActorAsync(app, gym);
        using var original = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("snapshot", "member@example.invalid", AccountRole.Student, first));
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("snapshot", "member@example.invalid",
            field == "role" ? AccountRole.Trainer : AccountRole.Student, field == "role" ? null : second))).StatusCode);
        using var verify = app.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var audit = await db.TenantInvitationRequests.SingleAsync();
        Assert.Equal((AccountRole.Student, first), (audit.Role, audit.StudentId));
        Assert.Single(await db.ActivationInvitations.ToListAsync());
        Assert.Null((await db.ActivationInvitations.SingleAsync()).RevokedAt);
        Assert.Null((await db.Alumnos.SingleAsync(s => s.Id == second)).AccountId);
    }

    [Fact]
    public async Task TenantInvite_ForeignAndMissingStudents_ReturnIndistinguishable404()
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        var other = await app.CreateGymAsync();
        var foreign = await InvitationStudentAsync(app, other);
        using var actor = await InvitationActorAsync(app, gym);
        using var first = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("foreign", "member@example.invalid", AccountRole.Student, foreign));
        using var second = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("missing", "member@example.invalid", AccountRole.Student, int.MaxValue));
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        using var verify = app.Factory.Services.CreateScope();
        Assert.Empty(await verify.ServiceProvider.GetRequiredService<StaticXDbContext>().TenantInvitationRequests.ToListAsync());
    }

    [Fact]
    public async Task TenantInvite_ActiveStateChangesBeforeBinding_RejectsAndRollsBackIdentity()
    {
        var interceptor = new DeactivateBeforeNewIdentitySave();
        await using var app = await TestApp.CreateAsync(activation: true, tenantSaveInterceptor: interceptor);
        var gym = await app.CreateGymAsync();
        var student = await InvitationStudentAsync(app, gym);
        using var actor = await InvitationActorAsync(app, gym);
        using var response = await app.PostAsync(actor, InvitationsRoute, new InviteAccountRequest("active-claim", "member@example.invalid", AccountRole.Student, student));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(interceptor.Applied);
        using var verify = app.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Single(await db.Users.ToListAsync());
        Assert.Null((await db.Alumnos.SingleAsync()).AccountId);
        Assert.True((await db.Alumnos.SingleAsync()).Activo);
        Assert.Empty(await db.TenantInvitationRequests.ToListAsync());
        Assert.Empty(await db.ActivationInvitations.ToListAsync());
        Assert.Empty(await db.ActivationOutbox.ToListAsync());
    }

    private sealed class DeactivateBeforeNewIdentitySave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Applied { get; private set; }
        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            if (db.ChangeTracker.Entries<GymAccount>().Any(e => e.State == EntityState.Added && e.Entity.Role == AccountRole.Student))
            {
                // Inject a state change inside the isolated transaction to exercise the write-time predicate, not SQL Server concurrency.
                Assert.Equal(1, await db.Set<Alumno>().ExecuteUpdateAsync(s => s.SetProperty(s => s.Activo, false), cancellationToken));
                Applied = true;
            }
            return result;
        }
    }

    private sealed class TenantCommitFailure : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        public bool Fail { get; set; }
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Isolated tenant commit failure.");
            return ValueTask.FromResult(result);
        }
    }

    private static async Task<int> InvitationStudentAsync(TestApp app, int gym, bool active = true, string email = "contact@example.invalid")
    {
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var student = new Alumno { GimnasioId = gym, Activo = active, Nombre = "Selected", Apellido = "Student", Email = email };
        db.Alumnos.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private static async Task<HttpClient> InvitationActorAsync(TestApp app, int gym)
    {
        await app.CreateAccountAsync(gym, "actor@example.invalid", "CorrectPassword1!", true, AccountRole.GymAdmin);
        var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await app.LoginAsync(client, gym, "actor@example.invalid", "CorrectPassword1!")).StatusCode);
        return client;
    }

    private static async Task<string> InvitationTokenAsync(TestApp app, string accountId)
    {
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var invitation = await db.ActivationInvitations.SingleAsync(i => i.AccountId == accountId && i.RevokedAt == null);
        var payload = await db.ActivationOutbox.SingleAsync(o => o.InvitationId == invitation.Id);
        return scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Static-X.ActivationDelivery.v1", invitation.Id.ToString("N")).Unprotect(payload.ProtectedToken);
    }

    private static async Task<HttpResponseMessage> InvitationPatchAsync(TestApp app, HttpClient actor, int student, string action)
    {
        var token = (await actor.GetFromJsonAsync<JsonElement>("/api/account/csrf")).GetProperty("token").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/students/{student}/{action}");
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await actor.SendAsync(request);
    }
}
