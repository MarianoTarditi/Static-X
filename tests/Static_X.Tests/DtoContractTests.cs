using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BLL;
using BLL.DTOs.Accounts;
using BLL.DTOs.Activations;
using BLL.DTOs.Gyms;
using BLL.DTOs.Students;
using BLL.DTOs.Payments;
using Entity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Static_X.API.Controllers;
using Xunit;

namespace Static_X.Tests;

public sealed class DtoContractTests
{
    private static readonly Type[] Contracts =
    [
        typeof(LoginRequest), typeof(AccountResponse), typeof(CsrfResponse), typeof(ChangePasswordRequest),
        typeof(ForgotPasswordRequest), typeof(ForgotPasswordResponse), typeof(ResetPasswordRequest),
        typeof(ActivateAccountRequest), typeof(CreateStudentRequest), typeof(UpdateStudentRequest), typeof(StudentResponse),
        typeof(CreateAccountRequest), typeof(ProvisionGymRequest), typeof(CreateGymRequest), typeof(GymResponse),
        typeof(StudentListRequest), typeof(StudentListResponse), typeof(ActivationResponse),
        typeof(DashboardResponse), typeof(DashboardStudentsResponse),
        typeof(GymListRequest), typeof(GymListResponse), typeof(PendingAdministratorResponse), typeof(PendingAdministratorListResponse),
        typeof(InviteAccountRequest), typeof(AccountInvitationResponse),
        typeof(InvitationListRequest), typeof(InvitationListResponse), typeof(InvitationLifecycleResponse),
        typeof(TrainerListRequest), typeof(TrainerListResponse), typeof(TrainerResponse),
        typeof(PaymentPreviewRequest), typeof(RegisterPaymentRequest), typeof(SetPassPriceRequest), typeof(VoidPaymentRequest),
        typeof(PaymentListRequest), typeof(PaymentResponse), typeof(PassPriceResponse), typeof(PaymentPreviewResponse),
        typeof(PaymentStudentResponse), typeof(PaymentListResponse), typeof(PaymentAuditResponse),
        typeof(PaymentSummaryResponse), typeof(PaymentCollectionRequest), typeof(PaymentCollectionResponse), typeof(PaymentCollectionMethodResponse),
        typeof(PublishRoutineRequest), typeof(RoutineDayRequest), typeof(RoutineExerciseRequest),
        typeof(RoutineEnvelopeResponse), typeof(RoutineResponse), typeof(RoutineDayResponse), typeof(RoutineExerciseResponse)
    ];

    [Fact]
    public void Dtos_AreSeparateConventionalBllClasses_NotApiOrEntityTypes()
    {
        var assembly = typeof(AccountService).Assembly;
        Assert.Equal(Contracts.OrderBy(type => type.FullName), assembly.GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("BLL.DTOs.", StringComparison.Ordinal) == true)
            .OrderBy(type => type.FullName));
        foreach (var type in Contracts)
        {
            Assert.Same(assembly, type.Assembly);
            Assert.True(type.IsClass && type.IsSealed && !type.IsNested);
            Assert.Null(type.GetMethod("<Clone>$"));
            Assert.Null(typeof(Program).Assembly.GetType("Static_X.API.Controllers." + type.Name));
            Assert.NotEmpty(type.GetConstructors());
        }
        Assert.Equal("BLL.DTOs.Activations", typeof(ActivateAccountRequest).Namespace);
        Assert.Equal("BLL.DTOs.Students", typeof(StudentResponse).Namespace);
        Assert.Equal("BLL.DTOs.Students", typeof(CreateStudentRequest).Namespace);
        Assert.All(new[] { typeof(LoginRequest), typeof(AccountResponse), typeof(CsrfResponse) },
            type => Assert.Equal("BLL.DTOs.Accounts", type.Namespace));
        Assert.NotSame(assembly, typeof(Account).Assembly);
    }

    [Fact]
    public void Json_WebContracts_RoundTripWithExactFieldsAndNumericRole()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var examples = new (object Value, string Json)[]
        {
            (new LoginRequest { GymId = 7, Email = "member@example.com", Password = "example" },
                "{\"gymId\":7,\"email\":\"member@example.com\",\"password\":\"example\"}"),
            (new AccountResponse(Id: "account-1", GymId: 7, Email: "member@example.com", Role: AccountRole.GymAdmin),
                "{\"id\":\"account-1\",\"gymId\":7,\"email\":\"member@example.com\",\"role\":1}"),
            (new CsrfResponse("request-token"), "{\"token\":\"request-token\"}"),
            (new DashboardStudentsResponse(42, 36, 6, 12), "{\"total\":42,\"active\":36,\"inactive\":6,\"activeWithoutAccount\":12}"),
            (new DashboardResponse(new DashboardStudentsResponse(42, 36, 6, 12)),
                "{\"students\":{\"total\":42,\"active\":36,\"inactive\":6,\"activeWithoutAccount\":12}}"),
            (new TrainerListRequest(), "{\"page\":1,\"pageSize\":20}"),
            (new TrainerResponse("opaque", "trainer@example.invalid", false, true),
                "{\"accountId\":\"opaque\",\"email\":\"trainer@example.invalid\",\"isEnabled\":false,\"emailConfirmed\":true}"),
            (new TrainerListResponse(new[] { new TrainerResponse("opaque", "trainer@example.invalid", true, false) }, 2, 1, 3),
                "{\"items\":[{\"accountId\":\"opaque\",\"email\":\"trainer@example.invalid\",\"isEnabled\":true,\"emailConfirmed\":false}],\"page\":2,\"pageSize\":1,\"totalCount\":3}"),
            (new ForgotPasswordRequest { Email = "member@example.invalid", GymId = null }, "{\"email\":\"member@example.invalid\",\"gymId\":null}"),
            (new ForgotPasswordResponse(), "{\"message\":\"If an eligible account exists, password reset instructions will be sent.\"}"),
            (new ResetPasswordRequest { Token = "opaque", NewPassword = " unchanged " }, "{\"token\":\"opaque\",\"newPassword\":\" unchanged \"}"),
            (new ChangePasswordRequest { CurrentPassword = " current ", NewPassword = " replacement " },
                "{\"currentPassword\":\" current \",\"newPassword\":\" replacement \"}"),
            (new ActivateAccountRequest { Token = "activation-token", Password = "example" },
                "{\"token\":\"activation-token\",\"password\":\"example\"}"),
            (new ActivationResponse(2), "{\"gymId\":2}"),
            (new InviteAccountRequest("opaque", "member@example.invalid", AccountRole.Student, 23),
                "{\"key\":\"opaque\",\"email\":\"member@example.invalid\",\"role\":3,\"studentId\":23}"),
            (new AccountInvitationResponse("persisted"), "{\"accountId\":\"persisted\"}"),
            (new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" },
                "{\"nombre\":\"Ana\",\"apellido\":\"Smith\",\"email\":\"ana@example.com\"}"),
            (new UpdateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" },
                "{\"nombre\":\"Ana\",\"apellido\":\"Smith\",\"email\":\"ana@example.com\"}"),
            (new StudentResponse(23, "Ana", "Smith", "ana@example.com", false),
                "{\"id\":23,\"nombre\":\"Ana\",\"apellido\":\"Smith\",\"email\":\"ana@example.com\",\"activo\":false}"),
            (new StudentListResponse(new[] { new StudentResponse(23, "Ana", "Smith", "ana@example.com", false) }, 2, 1, 42),
                "{\"items\":[{\"id\":23,\"nombre\":\"Ana\",\"apellido\":\"Smith\",\"email\":\"ana@example.com\",\"activo\":false}],\"page\":2,\"pageSize\":1,\"totalCount\":42}"),
            (new GymListRequest(), "{\"page\":1,\"pageSize\":20}"),
            (new GymListResponse([new GymResponse(2, "Example gym")], 1, 20, 1),
                "{\"items\":[{\"id\":2,\"nombre\":\"Example gym\"}],\"page\":1,\"pageSize\":20,\"totalCount\":1}"),
            (new PendingAdministratorResponse("opaque", "admin@example.invalid"),
                "{\"accountId\":\"opaque\",\"email\":\"admin@example.invalid\"}"),
            (new PendingAdministratorListResponse([new PendingAdministratorResponse("opaque", "admin@example.invalid")], 2, 1, 3),
                "{\"items\":[{\"accountId\":\"opaque\",\"email\":\"admin@example.invalid\"}],\"page\":2,\"pageSize\":1,\"totalCount\":3}")
        };
        foreach (var (value, json) in examples)
        {
            Assert.Equal(json, JsonSerializer.Serialize(value, value.GetType(), options));
            var restored = JsonSerializer.Deserialize(json, value.GetType(), options);
            Assert.Equal(json, JsonSerializer.Serialize(restored, value.GetType(), options));
        }
        Assert.Equal("{\"token\":null}", JsonSerializer.Serialize(new CsrfResponse(null), options));
        Assert.Equal("{\"token\":\"request-token\"}", JsonSerializer.Serialize(new CsrfResponse("request-token")));
    }

    [Fact]
    public void Requests_KeepRequiredMetadataDefaultsAndValidation()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LoginRequest>("{}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChangePasswordRequest>("{}", options));
        Assert.Equal(new[] { "CurrentPassword", "NewPassword" }, InvalidMembers(new ChangePasswordRequest { CurrentPassword = "", NewPassword = null! }));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ActivateAccountRequest>("{}", options));
        Assert.All(typeof(LoginRequest).GetProperties().Where(property => property.Name != "GymId"), property =>
            Assert.NotNull(property.GetCustomAttribute<RequiredMemberAttribute>()));
        Assert.All(typeof(ActivateAccountRequest).GetProperties(), property =>
            Assert.NotNull(property.GetCustomAttribute<RequiredMemberAttribute>()));
        var emptyStudent = JsonSerializer.Deserialize<CreateStudentRequest>("{}", options)!;
        Assert.Equal(string.Empty, emptyStudent.Nombre);
        Assert.Equal(string.Empty, emptyStudent.Apellido);
        Assert.Equal(string.Empty, emptyStudent.Email);
        Assert.Equal(new[] { "Apellido", "Email", "Nombre" }, InvalidMembers(emptyStudent));
        var emptyUpdate = JsonSerializer.Deserialize<UpdateStudentRequest>("{}", options)!;
        Assert.Equal(string.Empty, emptyUpdate.Nombre);
        Assert.Equal(string.Empty, emptyUpdate.Apellido);
        Assert.Equal(string.Empty, emptyUpdate.Email);
        Assert.Equal(new[] { "Apellido", "Email", "Nombre" }, InvalidMembers(emptyUpdate));
        Assert.Equal(new[] { "Email", "GymId", "Password" }, InvalidMembers(
            new LoginRequest { GymId = 0, Email = "invalid", Password = " " }));
        Assert.Empty(InvalidMembers(new LoginRequest { GymId = int.MaxValue, Email = "a@example.com", Password = "example" }));
        var global = JsonSerializer.Deserialize<LoginRequest>("{\"email\":\"global@example.com\",\"password\":\"example\"}", options)!;
        Assert.Null(global.GymId);
        Assert.Empty(InvalidMembers(global));
        Assert.Equal("{\"id\":\"global\",\"gymId\":null,\"email\":\"global@example.com\",\"role\":4}",
            JsonSerializer.Serialize(new AccountResponse("global", null, "global@example.com", AccountRole.SuperAdmin), options));
        Assert.Equal(new[] { "Password", "Token" }, InvalidMembers(new ActivateAccountRequest { Token = " ", Password = "" }));
        Assert.Empty(InvalidMembers(new ActivateAccountRequest { Token = "token", Password = "example" }));
        Assert.Empty(InvalidMembers(new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "invalid" }));
        Assert.Equal(NullabilityState.Nullable, new NullabilityInfoContext().Create(typeof(CsrfResponse).GetProperty("Token")!).ReadState);
    }

    [Fact]
    public void Mvc_DiscoveryUsesBllRequestMetadata_WithoutDtoActions()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
            .UseSetting("Activation:KeyRingPath", ""));
        var actions = factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .Cast<ControllerActionDescriptor>().ToArray();
        Assert.Equal(43, actions.Length);
        Assert.All(actions, action => Assert.Equal(typeof(Program).Assembly, action.ControllerTypeInfo.Assembly));
        var dashboardAction = Assert.Single(actions, action => action.ControllerTypeInfo.AsType() == typeof(DashboardController));
        Assert.Equal(nameof(DashboardController.Get), dashboardAction.ActionName);
        var trainerRoutineActions = actions.Where(action => action.ControllerTypeInfo.AsType() == typeof(StudentRoutinesController)).ToArray();
        Assert.Equal(new[] { nameof(StudentRoutinesController.Publish), nameof(StudentRoutinesController.Read) },
            trainerRoutineActions.Select(action => action.ActionName).Order());
        Assert.Contains(trainerRoutineActions.Single(action => action.ActionName == nameof(StudentRoutinesController.Publish)).Parameters,
            parameter => parameter.ParameterType == typeof(PublishRoutineRequest));
        var selfRoutineAction = Assert.Single(actions, action => action.ControllerTypeInfo.AsType() == typeof(MyRoutineController));
        Assert.Equal(nameof(MyRoutineController.Read), selfRoutineAction.ActionName);
        var provider = factory.Services.GetRequiredService<IModelMetadataProvider>();
        foreach (var type in new[] { typeof(LoginRequest), typeof(ChangePasswordRequest), typeof(ActivateAccountRequest), typeof(CreateStudentRequest), typeof(UpdateStudentRequest), typeof(InviteAccountRequest) })
        {
            Assert.Contains(actions.SelectMany(action => action.Parameters), parameter => parameter.ParameterType == type);
            var metadata = provider.GetMetadataForType(type);
            Assert.All(metadata.Properties.Where(property => property.ModelType == typeof(string)), property =>
                Assert.Contains(property.ValidatorMetadata, validator => validator is RequiredAttribute));
        }
    }

    [Fact]
    public async Task OpenApi_MovedDtosRetainSchemaNamesAndPublicFields()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
            .UseSetting("Activation:KeyRingPath", ""));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var logoutPath = document.RootElement.GetProperty("paths").GetProperty("/api/account/logout");
        Assert.False(logoutPath.TryGetProperty("get", out _));
        var logout = logoutPath.GetProperty("post");
        Assert.False(logout.TryGetProperty("requestBody", out _));
        Assert.Equal(new[] { "204", "400", "401" }, logout.GetProperty("responses")
            .EnumerateObject().Select(property => property.Name).Order());
        Assert.False(logout.GetProperty("responses").GetProperty("204").TryGetProperty("content", out _));
        var change = document.RootElement.GetProperty("paths").GetProperty("/api/account/change-password").GetProperty("post");
        Assert.True(change.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.Equal(new[] { "204", "400", "401" }, change.GetProperty("responses").EnumerateObject().Select(property => property.Name).Order());
        Assert.False(change.GetProperty("responses").GetProperty("204").TryGetProperty("content", out _));
        Assert.EndsWith("/ChangePasswordRequest", change.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var trainers = document.RootElement.GetProperty("paths").GetProperty("/api/trainers").GetProperty("get");
        Assert.False(trainers.TryGetProperty("requestBody", out _));
        Assert.Equal(new[] { "200", "400", "401", "403", "500" }, trainers.GetProperty("responses").EnumerateObject().Select(p => p.Name).Order());
        foreach (var action in new[] { "deactivate", "reactivate" })
        {
            var path = document.RootElement.GetProperty("paths").GetProperty("/api/trainers/{accountId}/" + action);
            Assert.Equal(new[] { "patch" }, path.EnumerateObject().Select(p => p.Name));
            var patch = path.GetProperty("patch");
            Assert.False(patch.TryGetProperty("requestBody", out _));
            Assert.Equal(new[] { "204", "400", "401", "403", "404", "500" }, patch.GetProperty("responses").EnumerateObject().Select(p => p.Name).Order());
            Assert.False(patch.GetProperty("responses").GetProperty("204").TryGetProperty("content", out _));
        }
        var forgot = document.RootElement.GetProperty("paths").GetProperty("/api/account/forgot-password").GetProperty("post");
        Assert.Equal(new[] { "202", "400", "429", "503" }, forgot.GetProperty("responses").EnumerateObject().Select(p => p.Name).Order());
        var reset = document.RootElement.GetProperty("paths").GetProperty("/api/account/reset-password").GetProperty("post");
        Assert.Equal(new[] { "204", "400", "503" }, reset.GetProperty("responses").EnumerateObject().Select(p => p.Name).Order());
        Assert.False(reset.GetProperty("responses").GetProperty("204").TryGetProperty("content", out _));
        var expected = new Dictionary<string, string[]>
        {
            ["LoginRequest"] = ["email", "gymId", "password"],
            ["ChangePasswordRequest"] = ["currentPassword", "newPassword"],
            ["ForgotPasswordRequest"] = ["email", "gymId"],
            ["ForgotPasswordResponse"] = ["message"],
            ["ResetPasswordRequest"] = ["newPassword", "token"],
            ["AccountResponse"] = ["email", "gymId", "id", "role"],
            ["ActivateAccountRequest"] = ["password", "token"],
            ["InviteAccountRequest"] = ["email", "key", "role", "studentId"],
            ["AccountInvitationResponse"] = ["accountId"],
            ["ActivationResponse"] = ["gymId"],
            ["CreateStudentRequest"] = ["apellido", "email", "nombre"],
            ["UpdateStudentRequest"] = ["apellido", "email", "nombre"],
            ["StudentResponse"] = ["activo", "apellido", "email", "id", "nombre"],
            ["StudentListResponse"] = ["items", "page", "pageSize", "totalCount"],
            ["DashboardResponse"] = ["students"],
            ["DashboardStudentsResponse"] = ["active", "activeWithoutAccount", "inactive", "total"],
            ["GymResponse"] = ["id", "nombre"],
            ["GymListResponse"] = ["items", "page", "pageSize", "totalCount"],
            ["PendingAdministratorResponse"] = ["accountId", "email"],
            ["PendingAdministratorListResponse"] = ["items", "page", "pageSize", "totalCount"],
            ["TrainerResponse"] = ["accountId", "email", "emailConfirmed", "isEnabled"],
            ["TrainerListResponse"] = ["items", "page", "pageSize", "totalCount"]
        };
        foreach (var (name, fields) in expected)
        {
            var schema = schemas.GetProperty(name);
            Assert.Equal(fields, schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order());
        }
        Assert.Equal(new[] { "email", "password" }, schemas.GetProperty("LoginRequest")
            .GetProperty("required").EnumerateArray().Select(field => field.GetString()).Order());
        Assert.Equal(new[] { "currentPassword", "newPassword" }, schemas.GetProperty("ChangePasswordRequest")
            .GetProperty("required").EnumerateArray().Select(field => field.GetString()).Order());
        Assert.Equal(new[] { "password", "token" }, schemas.GetProperty("ActivateAccountRequest")
            .GetProperty("required").EnumerateArray().Select(field => field.GetString()).Order());
        var listOperation = document.RootElement.GetProperty("paths").GetProperty("/api/students").GetProperty("get");
        foreach (var (path, responseType, parameters) in new[]
        {
            ("/api/gyms", "GymListResponse", new[] { "Page", "PageSize" }),
            ("/api/gyms/{gymId}/administrators/pending", "PendingAdministratorListResponse", new[] { "Page", "PageSize", "gymId" })
        })
        {
            var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");
            Assert.EndsWith("/" + responseType, operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.Equal(parameters.Order(), operation.GetProperty("parameters").EnumerateArray()
                .Select(parameter => parameter.GetProperty("name").GetString()).Order());
            foreach (var status in new[] { "400", "401", "403", "500" })
                Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        }
        var activationResponses = document.RootElement.GetProperty("paths").GetProperty("/api/account/activation")
            .GetProperty("post").GetProperty("responses");
        Assert.False(activationResponses.TryGetProperty("204", out _));
        Assert.EndsWith("/ActivationResponse", activationResponses.GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var listSchema = listOperation.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        Assert.EndsWith("/StudentListResponse", listSchema.GetProperty("$ref").GetString());
        Assert.EndsWith("/StudentResponse", schemas.GetProperty("StudentListResponse").GetProperty("properties")
            .GetProperty("items").GetProperty("items").GetProperty("$ref").GetString());
        Assert.Equal(new[] { "Activo", "Page", "PageSize", "Search" }, listOperation.GetProperty("parameters")
            .EnumerateArray().Select(p => p.GetProperty("name").GetString()).Order());
        using var csrf = await client.GetAsync("/api/account/csrf");
        csrf.EnsureSuccessStatusCode();
        using var token = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        var tokenProperty = Assert.Single(token.RootElement.EnumerateObject());
        Assert.Equal("token", tokenProperty.Name);
        Assert.False(string.IsNullOrWhiteSpace(tokenProperty.Value.GetString()));
        Assert.True(csrf.Headers.CacheControl?.NoStore);
        Assert.True(csrf.Headers.CacheControl?.NoCache);
    }

    private static string[] InvalidMembers(object value)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(value, new ValidationContext(value), errors, validateAllProperties: true);
        return errors.SelectMany(error => error.MemberNames).Distinct().Order().ToArray();
    }
}
