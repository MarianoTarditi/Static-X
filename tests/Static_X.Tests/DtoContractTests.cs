using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BLL;
using BLL.DTOs.Accounts;
using BLL.DTOs.Activations;
using BLL.DTOs.Gyms;
using BLL.DTOs.Students;
using Entity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed class DtoContractTests
{
    private static readonly Type[] Contracts =
    [
        typeof(LoginRequest), typeof(AccountResponse), typeof(CsrfResponse),
        typeof(ActivateAccountRequest), typeof(CreateStudentRequest), typeof(UpdateStudentRequest), typeof(StudentResponse),
        typeof(CreateAccountRequest), typeof(ProvisionGymRequest), typeof(CreateGymRequest), typeof(GymResponse),
        typeof(StudentListRequest), typeof(StudentListResponse), typeof(ActivationResponse),
        typeof(GymListRequest), typeof(GymListResponse), typeof(PendingAdministratorResponse), typeof(PendingAdministratorListResponse)
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
            (new ActivateAccountRequest { Token = "activation-token", Password = "example" },
                "{\"token\":\"activation-token\",\"password\":\"example\"}"),
            (new ActivationResponse(2), "{\"gymId\":2}"),
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
        Assert.Equal(15, actions.Length);
        Assert.All(actions, action => Assert.Equal(typeof(Program).Assembly, action.ControllerTypeInfo.Assembly));
        var provider = factory.Services.GetRequiredService<IModelMetadataProvider>();
        foreach (var type in new[] { typeof(LoginRequest), typeof(ActivateAccountRequest), typeof(CreateStudentRequest), typeof(UpdateStudentRequest) })
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
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var expected = new Dictionary<string, string[]>
        {
            ["LoginRequest"] = ["email", "gymId", "password"],
            ["AccountResponse"] = ["email", "gymId", "id", "role"],
            ["ActivateAccountRequest"] = ["password", "token"],
            ["ActivationResponse"] = ["gymId"],
            ["CreateStudentRequest"] = ["apellido", "email", "nombre"],
            ["UpdateStudentRequest"] = ["apellido", "email", "nombre"],
            ["StudentResponse"] = ["activo", "apellido", "email", "id", "nombre"],
            ["StudentListResponse"] = ["items", "page", "pageSize", "totalCount"],
            ["GymResponse"] = ["id", "nombre"],
            ["GymListResponse"] = ["items", "page", "pageSize", "totalCount"],
            ["PendingAdministratorResponse"] = ["accountId", "email"],
            ["PendingAdministratorListResponse"] = ["items", "page", "pageSize", "totalCount"]
        };
        foreach (var (name, fields) in expected)
        {
            var schema = schemas.GetProperty(name);
            Assert.Equal(fields, schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order());
        }
        Assert.Equal(new[] { "email", "password" }, schemas.GetProperty("LoginRequest")
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
