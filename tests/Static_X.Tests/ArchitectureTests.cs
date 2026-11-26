using BLL;
using AutoMapper;
using AutoMapper.Internal;
using BLL.Interfaces;
using BLL.Services.Interfaces;
using BLL.DTOs.Accounts;
using BLL.DTOs.Activations;
using BLL.DTOs.Gyms;
using BLL.DTOs.Students;
using DAL;
using DAL.Repositories.Implementation;
using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Static_X.Tests;

public class ArchitectureTests
{
    [Fact]
    public void RenamedEntitySurface_UsesRequestedClrNamesWithoutObsoleteAliases()
    {
        var assembly = typeof(Account).Assembly;
        var expected = new Dictionary<string, string[]>
        {
            ["Entity.Student"] = ["GymId", "FirstName", "LastName", "IsActive"],
            ["Entity.Gym"] = ["Name"],
            ["DAL.IdentityAccount"] = ["GymId", "Role", "IsEnabled"],
            ["DAL.GymProvisioningRecord"] = ["IdempotencyKey", "InitialAdminAccountId"],
            ["DAL.Repositories.Interfaces.ActivationDelivery"] = ["LeaseId", "RecipientEmail", "OutboxEntryId"]
        };
        foreach (var (name, properties) in expected)
        {
            var type = assembly.GetType(name);
            Assert.NotNull(type);
            foreach (var property in properties) Assert.NotNull(type.GetProperty(property));
        }
        foreach (var name in new[] { "Entity.Alumno", "Entity.Gimnasio", "DAL.GymAccount", "DAL.ProvisioningRequest" })
            Assert.Null(assembly.GetType(name));
        Assert.All(new[] { "GimnasioId", "Nombre", "Apellido", "Activo" }, name => Assert.Null(typeof(Student).GetProperty(name)));
        Assert.Null(typeof(Gym).GetProperty("Nombre"));
        Assert.All(new[] { "Key", "AccountId" }, name => Assert.Null(typeof(GymProvisioningRecord).GetProperty(name)));
        Assert.All(new[] { "LeaseOwner", "Email", "JobId" }, name => Assert.Null(typeof(ActivationDelivery).GetProperty(name)));
        Assert.Equal(new[] { "OutboxEntryId", "InvitationId", "LeaseId", "RecipientEmail", "ProtectedToken" },
            typeof(ActivationDelivery).GetConstructors().Single().GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(new[] { 1, 2, 3, 4 }, Enum.GetValues<AccountRole>().Select(role => (int)role));
    }

    [Fact]
    public void RenamedProperties_RetainLegacyColumnsKeysAndIndexes()
    {
        using var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var model = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var student = model.FindEntityType(typeof(Student))!;
        Assert.Equal("Alumnos", student.GetTableName());
        foreach (var (property, column) in new[] { ("GymId", "GimnasioId"), ("FirstName", "Nombre"), ("LastName", "Apellido"), ("IsActive", "Activo") })
            Assert.Equal(column, student.FindProperty(property)!.GetColumnName());
        Assert.Equal("AK_Alumnos_GimnasioId_Id", student.GetKeys().Single(key => key.Properties.Select(p => p.Name).SequenceEqual(new[] { "GymId", "Id" })).GetName());
        Assert.Equal("IX_Alumnos_GimnasioId_Email", student.GetIndexes().Single(index => index.Properties.Select(p => p.Name).SequenceEqual(new[] { "GymId", "Email" })).GetDatabaseName());
        Assert.Equal("FK_Alumnos_Gimnasios_GimnasioId", student.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(Gym)).GetConstraintName());
        var gym = model.FindEntityType(typeof(Gym))!;
        Assert.Equal("Gimnasios", gym.GetTableName());
        Assert.Equal("Nombre", gym.FindProperty(nameof(Gym.Name))!.GetColumnName());
        var request = model.FindEntityType(typeof(GymProvisioningRecord))!;
        Assert.Equal("ProvisioningRequests", request.GetTableName());
        Assert.Equal("Key", request.FindProperty(nameof(GymProvisioningRecord.IdempotencyKey))!.GetColumnName());
        Assert.Equal("AccountId", request.FindProperty(nameof(GymProvisioningRecord.InitialAdminAccountId))!.GetColumnName());
        Assert.Equal("PK_ProvisioningRequests", request.FindPrimaryKey()!.GetName());
        Assert.Contains(request.GetIndexes(), index => index.GetDatabaseName() == "IX_ProvisioningRequests_AccountId");
        Assert.Equal("LeaseOwner", model.FindEntityType(typeof(ActivationOutbox))!.FindProperty(nameof(ActivationOutbox.LeaseOwner))!.GetColumnName());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityTokens_PreservePurposesAcrossGenericUserTypes(bool dedicatedReset)
    {
        using var legacyContext = new Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityUserContext<IdentityUser>(
            new DbContextOptionsBuilder<Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityUserContext<IdentityUser>>()
                .UseSqlite("Data Source=:memory:").Options);
        using var currentContext = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var legacyManager = Manager(new Microsoft.AspNetCore.Identity.EntityFrameworkCore.UserOnlyStore<IdentityUser>(legacyContext));
        using var currentManager = Manager(new Microsoft.AspNetCore.Identity.EntityFrameworkCore.UserOnlyStore<IdentityAccount, StaticXDbContext>(currentContext));
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var expectedName = dedicatedReset ? "Static-X.PasswordReset.Identity.v1" : "DataProtectorTokenProvider";
        var oldOptions = new DataProtectionTokenProviderOptions { Name = expectedName, TokenLifespan = TimeSpan.FromMinutes(dedicatedReset ? 30 : 1440) };
        var legacy = new DataProtectorTokenProvider<IdentityUser>(protection, Options.Create(oldOptions),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DataProtectorTokenProvider<IdentityUser>>.Instance);
        DataProtectorTokenProvider<IdentityAccount> current = dedicatedReset
            ? new PasswordResetTokenProvider(protection, Options.Create(new PasswordResetTokenProviderOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DataProtectorTokenProvider<IdentityAccount>>.Instance)
            : new DataProtectorTokenProvider<IdentityAccount>(protection, Options.Create(new DataProtectionTokenProviderOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DataProtectorTokenProvider<IdentityAccount>>.Instance);
        Assert.Equal(expectedName, current.Name);
        Assert.Equal("Static-X.PasswordReset", PasswordResetTokenProvider.ProviderName);
        Assert.Equal(TimeSpan.FromMinutes(30), new PasswordResetTokenProviderOptions().TokenLifespan);
        var previousUser = new IdentityUser { Id = "persisted-id", SecurityStamp = "persisted-stamp" };
        var renamedUser = new IdentityAccount { Id = previousUser.Id, SecurityStamp = previousUser.SecurityStamp };
        var token = await legacy.GenerateAsync("ResetPassword", legacyManager, previousUser);
        Assert.True(await current.ValidateAsync("ResetPassword", token, currentManager, renamedUser));
        var currentToken = await current.GenerateAsync("ResetPassword", currentManager, renamedUser);
        Assert.True(await legacy.ValidateAsync("ResetPassword", currentToken, legacyManager, previousUser));
        Assert.False(await current.ValidateAsync("EmailConfirmation", token, currentManager, renamedUser));
        renamedUser.SecurityStamp = "rotated-stamp";
        Assert.False(await current.ValidateAsync("ResetPassword", token, currentManager, renamedUser));

        UserManager<TUser> Manager<TUser>(IUserStore<TUser> store) where TUser : class =>
            new(store, Options.Create(new IdentityOptions()), new PasswordHasher<TUser>(), [], [],
                new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), services,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<TUser>>.Instance);
    }

    [Fact]
    public void RenamedEntities_PreserveFrozenMigrationRelationalModelOffline()
    {
        using var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations["20261125013528_AddVideoLibrary"], context.Database.ProviderName!);
        var frozen = context.GetService<Microsoft.EntityFrameworkCore.Infrastructure.IModelRuntimeInitializer>()
            .Initialize(migration.TargetModel, designTime: true);
        var current = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        Assert.Empty(context.GetService<IMigrationsModelDiffer>().GetDifferences(frozen.GetRelationalModel(), current.GetRelationalModel()));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    private static readonly (Type Contract, Type Implementation)[] Services =
    [
        (typeof(IAccountService), typeof(AccountService)),
        (typeof(IActivationService), typeof(ActivationService)),
        (typeof(IActivationDispatcher), typeof(ActivationDispatcher)),
        (typeof(IPasswordRecoveryDispatcher), typeof(PasswordRecoveryDispatcher)),
        (typeof(IGymService), typeof(GymService)),
        (typeof(IStudentService), typeof(StudentService)),
        (typeof(IDashboardService), typeof(DashboardService)),
        (typeof(IPaymentService), typeof(PaymentService)),
        (typeof(IRoutineService), typeof(RoutineService)),
        (typeof(IVideoLibraryService), typeof(VideoLibraryService))
    ];

    private static readonly (Type Contract, Type Implementation)[] DeclaredServices =
    [
        .. Services
    ];

    [Fact]
    public void ServiceInterfaces_PreservePublicSignaturesAndDefaults()
    {
        var nullability = new NullabilityInfoContext();
        foreach (var (contract, implementation) in DeclaredServices)
        {
            Assert.Equal("BLL.Services.Interfaces", contract.Namespace);
            Assert.Contains(contract, implementation.GetInterfaces());
            Assert.True(implementation.IsSealed);
            var publicMethods = implementation.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.Equal(publicMethods.Length, contract.GetMethods().Length);
            foreach (var method in publicMethods)
            {
                var contractMethod = contract.GetMethod(method.Name, method.GetParameters().Select(p => p.ParameterType).ToArray());
                Assert.NotNull(contractMethod);
                Assert.Equal(method.ReturnType, contractMethod.ReturnType);
                Assert.Equal(NullabilityShape(nullability.Create(method.ReturnParameter)),
                    NullabilityShape(nullability.Create(contractMethod.ReturnParameter)));
                var parameters = method.GetParameters();
                var contractParameters = contractMethod.GetParameters();
                for (var i = 0; i < parameters.Length; i++)
                {
                    Assert.Equal(parameters[i].Name, contractParameters[i].Name);
                    Assert.Equal(parameters[i].HasDefaultValue, contractParameters[i].HasDefaultValue);
                    Assert.Equal(parameters[i].DefaultValue, contractParameters[i].DefaultValue);
                    Assert.Equal(NullabilityShape(nullability.Create(parameters[i])),
                        NullabilityShape(nullability.Create(contractParameters[i])));
                }
            }
        }
    }

    private static string NullabilityShape(NullabilityInfo info) =>
        $"{info.ReadState}/{info.WriteState}[{string.Join(",", info.GenericTypeArguments.Select(NullabilityShape))}]";

    [Fact]
    public void PublicServiceContracts_DoNotExposeEntities_EvenInsideDtosOrGenericResults()
    {
        var contracts = typeof(AccountService).Assembly.GetExportedTypes().Where(type => type.IsInterface &&
            type.Namespace is "BLL.Services.Interfaces" or "BLL.Interfaces").ToArray();
        Assert.Equal(13, contracts.Length);
        Assert.Equal(DeclaredServices.Select(pair => pair.Contract)
                .Concat(new[] { typeof(IActivationEmailSender), typeof(IPasswordRecoveryEmailSender), typeof(IVideoObjectStore) })
                .OrderBy(type => type.FullName),
            contracts.OrderBy(type => type.FullName));
        Assert.Contains(typeof(BLL.Interfaces.IPasswordRecoveryEmailSender), contracts);
        Assert.Contains(typeof(IAccountService), contracts);
        Assert.Contains(typeof(IPasswordRecoveryDispatcher), contracts);
        Assert.Contains(typeof(IRoutineService), contracts);
        Assert.Contains(typeof(IDashboardService), contracts);
        foreach (var type in contracts.Concat(DeclaredServices.Select(pair => pair.Implementation)))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.False(ExposesEntity(method.ReturnType, new HashSet<Type>()), $"{type.Name}.{method.Name} returns an entity.");
                Assert.All(method.GetParameters(), parameter => Assert.False(ExposesEntity(parameter.ParameterType, new HashSet<Type>()),
                    $"{type.Name}.{method.Name} accepts an entity through {parameter.Name}."));
            }
        }
    }

    [Fact]
    public void BusinessServiceContracts_GroupFieldsInDtos_AndKeepTrustedContextSeparate()
    {
        AssertSignature<IDashboardService>("GetAsync", typeof(Task<DashboardResponse>), typeof(int), typeof(AccountRole), typeof(CancellationToken));
        AssertSignature<IDashboardRepository>("GetStudentCountsAsync", typeof(Task<global::DAL.Repositories.Models.DashboardStudentCounts>), typeof(int), typeof(CancellationToken));
        AssertSignature<IStudentService>("AddAsync", typeof(Task<StudentResponse>), typeof(int), typeof(CreateStudentRequest), typeof(CancellationToken));
        AssertSignature<IStudentService>("UpdateAsync", typeof(Task<StudentResponse>), typeof(int), typeof(int), typeof(UpdateStudentRequest), typeof(CancellationToken));
        AssertSignature<IStudentService>("FindAsync", typeof(Task<StudentResponse>), typeof(int), typeof(int), typeof(CancellationToken));
        AssertSignature<IStudentService>("ListAsync", typeof(Task<StudentListResponse>), typeof(int), typeof(StudentListRequest), typeof(CancellationToken));
        AssertSignature<IStudentService>("DeactivateAsync", typeof(Task<StudentResponse>), typeof(int), typeof(int), typeof(CancellationToken));
        AssertSignature<IStudentService>("ReactivateAsync", typeof(Task<StudentResponse>), typeof(int), typeof(int), typeof(CancellationToken));
        AssertSignature<IAccountService>("CreateAsync", typeof(Task<AccountResponse>), typeof(int), typeof(CreateAccountRequest), typeof(AccountRole), typeof(CancellationToken));
        AssertSignature<IAccountService>("SignInAsync", typeof(Task<AccountResponse>), typeof(int?), typeof(LoginRequest), typeof(CancellationToken));
        AssertSignature<IAccountService>("FindByEmailAsync", typeof(Task<AccountResponse>), typeof(int), typeof(string), typeof(CancellationToken));
        AssertSignature<IAccountService>("FindByIdAsync", typeof(Task<AccountResponse>), typeof(int?), typeof(string), typeof(CancellationToken));
        AssertSignature<IAccountService>("ChangePasswordAsync", typeof(Task<bool>), typeof(int?), typeof(string), typeof(ChangePasswordRequest), typeof(CancellationToken));
        AssertSignature<IAccountService>("ListTrainersAsync", typeof(Task<TrainerListResponse>), typeof(int), typeof(AccountRole), typeof(TrainerListRequest), typeof(CancellationToken));
        AssertSignature<IAccountService>("SetTrainerEnabledAsync", typeof(Task<bool>), typeof(int), typeof(AccountRole), typeof(string), typeof(bool), typeof(CancellationToken));
        AssertSignature<IAccountRepository>("ListTrainersAsync", typeof(Task<global::DAL.Repositories.Models.TrainerAccountPage>), typeof(int), typeof(int), typeof(int), typeof(CancellationToken));
        AssertSignature<IAccountRepository>("SetTrainerEnabledAsync", typeof(Task<bool>), typeof(int), typeof(string), typeof(bool), typeof(CancellationToken));
        Assert.Equal(new[] { "Page", "PageSize" }, typeof(TrainerListRequest).GetProperties().Select(property => property.Name).Order());
        Assert.Same(typeof(Account).Assembly, typeof(TrainerAccountProjection).Assembly);
        Assert.Equal(typeof(IReadOnlyList<TrainerAccountProjection>), typeof(global::DAL.Repositories.Models.TrainerAccountPage).GetProperty("Items")!.PropertyType);
        AssertSignature<IAccountRepository>("ChangePasswordAsync", typeof(Task<bool>), typeof(int?), typeof(string), typeof(string), typeof(string), typeof(CancellationToken));
        Assert.Equal(new[] { "CurrentPassword", "NewPassword" }, typeof(ChangePasswordRequest).GetProperties().Select(property => property.Name).Order());
        AssertSignature<IGymService>("AddAsync", typeof(Task<GymResponse>), typeof(CreateGymRequest), typeof(CancellationToken));
        AssertSignature<IGymService>("ListAsync", typeof(Task<GymListResponse>), typeof(GymListRequest), typeof(CancellationToken));
        AssertSignature<IGymService>("ListPendingAdministratorsAsync", typeof(Task<PendingAdministratorListResponse>), typeof(int), typeof(GymListRequest), typeof(CancellationToken));
        Assert.Equal(new[] { "Page", "PageSize" }, typeof(GymListRequest).GetProperties().Select(property => property.Name).Order());
        AssertSignature<IActivationService>("ProvisionAsync", typeof(Task<int>), typeof(ProvisionGymRequest), typeof(CancellationToken));
        AssertSignature<IActivationService>("ActivateAsync", typeof(Task<ActivationResponse>), typeof(ActivateAccountRequest), typeof(CancellationToken));
        AssertSignature<IActivationService>("ReissueAsync", typeof(Task<bool>), typeof(int), typeof(string), typeof(CancellationToken));
        AssertSignature<IActivationDispatcher>("DispatchOneAsync", typeof(Task<bool>), typeof(CancellationToken));
        AssertSignature<IAccountService>("ForgotAsync", typeof(Task<ForgotPasswordResponse>), typeof(ForgotPasswordRequest), typeof(CancellationToken));
        AssertSignature<IAccountService>("ResetAsync", typeof(Task<string>), typeof(ResetPasswordRequest), typeof(CancellationToken));
        AssertSignature<IPasswordRecoveryDispatcher>("DispatchOneAsync", typeof(Task<bool>), typeof(CancellationToken));
        Assert.Equal(new[] { "Email", "Password" }, typeof(CreateAccountRequest).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(new[] { "Apellido", "Email", "Nombre" }, typeof(CreateStudentRequest).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(new[] { "Apellido", "Email", "Nombre" }, typeof(UpdateStudentRequest).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(new[] { "Activo", "Page", "PageSize", "Search" }, typeof(StudentListRequest).GetProperties().Select(property => property.Name).Order());
    }

    private static void AssertSignature<T>(string name, Type result, params Type[] parameters)
    {
        var method = typeof(T).GetMethod(name);
        Assert.NotNull(method);
        Assert.Equal(result, method.ReturnType);
        Assert.Equal(parameters, method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void EntityBoundaryCheck_TraversesNestedResults_AndAllowsDomainEnums()
    {
        Assert.True(ExposesEntity(typeof(Task<IReadOnlyList<Account>>), new HashSet<Type>()));
        Assert.True(ExposesEntity(typeof(Dictionary<string, List<Student[]>>), new HashSet<Type>()));
        Assert.True(ExposesEntity(typeof(Task<Gym>), new HashSet<Type>()));
        Assert.False(ExposesEntity(typeof(BLL.DTOs.Accounts.AccountResponse), new HashSet<Type>()));
        Assert.False(ExposesEntity(typeof(CancellationToken), new HashSet<Type>()));
        Assert.False(ExposesEntity(typeof(AccountRole), new HashSet<Type>()));
    }

    private static bool ExposesEntity(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type) || type.IsEnum) return false;
        if (type.Assembly == typeof(Account).Assembly) return true;
        if (type.HasElementType && ExposesEntity(type.GetElementType()!, visited)) return true;
        if (type.IsGenericType && type.GetGenericArguments().Any(argument => ExposesEntity(argument, visited))) return true;
        return type.Namespace?.StartsWith("BLL.DTOs.", StringComparison.Ordinal) == true &&
            type.GetProperties().Any(property => ExposesEntity(property.PropertyType, visited));
    }

    [Fact]
    public void Controllers_DoNotReferenceBusinessEntities_IncludingAsyncBodies()
    {
        // Auth/Identity and Program composition are deliberately outside the business-controller boundary.
        var controllers = typeof(Program).Assembly.GetTypes().Where(type =>
            type.Namespace == "Static_X.API.Controllers").ToArray();
        Assert.Contains(controllers, type => type.Name == "StudentsController");
        Assert.Contains(controllers, type => type.Name == "AuthenticationController");
        foreach (var type in controllers)
        {
            Assert.All(type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
                field => Assert.False(ExposesEntity(field.FieldType, new HashSet<Type>()), field.ToString()));
            Assert.DoesNotContain(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType == typeof(IMapper));
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Assert.False(ExposesEntity(method.ReturnType, new HashSet<Type>()), method.ToString());
                Assert.All(method.GetParameters(), parameter => Assert.False(ExposesEntity(parameter.ParameterType, new HashSet<Type>()), method.ToString()));
                var body = method.GetMethodBody();
                if (body is null) continue;
                Assert.All(body.LocalVariables, local => Assert.False(ExposesEntity(local.LocalType, new HashSet<Type>()), method.ToString()));
                foreach (var member in ReferencedMembers(method))
                {
                    var owner = member as Type ?? member.DeclaringType;
                    Assert.False(owner is not null && ExposesEntity(owner, new HashSet<Type>()),
                        $"{method.DeclaringType?.FullName}.{method.Name} references {member}.");
                }
            }
        }
        Assert.Same(typeof(Account).Assembly, typeof(IdentityAccount).Assembly); // Retain the explicit Identity adapter.
    }

    private static IEnumerable<MemberInfo> ReferencedMembers(MethodInfo method)
    {
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => unchecked((ushort)code.Value));
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (var offset = 0; offset < bytes.Length;)
        {
            ushort value = bytes[offset++];
            if (value == 0xfe) value = (ushort)(0xfe00 | bytes[offset++]);
            var operand = codes[value].OperandType;
            if (operand is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok)
            {
                var member = method.Module.ResolveMember(BitConverter.ToInt32(bytes, offset), method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments());
                if (member is not null) yield return member;
            }
            offset += operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4
            };
        }
    }

    [Fact]
    public void PasswordRecovery_UsesCanonicalAccountBindingsAndPreservesActionMetadata()
    {
        var api = typeof(Program).Assembly;
        var business = typeof(AccountService).Assembly;
        var data = typeof(AccountRepository).Assembly;
        Assert.Null(api.GetType("Static_X.API.Controllers.PasswordRecoveryController"));
        Assert.Null(business.GetType("BLL.PasswordRecoveryService"));
        Assert.Null(business.GetType("BLL.Services.Interfaces.IPasswordRecoveryService"));
        Assert.Null(data.GetType("DAL.Repositories.Implementation.PasswordRecoveryRepository"));
        Assert.Null(data.GetType("DAL.Repositories.Interfaces.IPasswordRecoveryRepository"));

        var controller = typeof(Static_X.API.Controllers.AuthenticationController);
        Assert.Equal("api/account", controller.GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!.Template);
        Assert.Single(controller.GetConstructors().Single().GetParameters(), parameter => parameter.ParameterType == typeof(IAccountService));
        foreach (var (name, route, request) in new[]
        {
            ("ForgotPassword", "forgot-password", typeof(ForgotPasswordRequest)),
            ("ResetPassword", "reset-password", typeof(ResetPasswordRequest))
        })
        {
            var action = controller.GetMethod(name)!;
            Assert.Equal(route, action.GetCustomAttribute<Microsoft.AspNetCore.Mvc.HttpPostAttribute>()!.Template);
            Assert.NotNull(action.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
            Assert.Equal(new[] { request, typeof(CancellationToken) }, action.GetParameters().Select(parameter => parameter.ParameterType));
        }
        Assert.Equal("password-recovery", controller.GetMethod("ForgotPassword")!
            .GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()!.PolicyName);
        Assert.Null(controller.GetMethod("ResetPassword")!
            .GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>());
        Assert.Single(typeof(PasswordRecoveryDispatcher).GetConstructors().Single().GetParameters(),
            parameter => parameter.ParameterType == typeof(IAccountRepository));
    }

    [Fact]
    public void ApplicationDI_ResolvesEachServiceInterfaceOncePerScope()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
            .UseSetting("Activation:KeyRingPath", ""));
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        Assert.Single(factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            service => service is Static_X.API.Integrations.ActivationOutboxWorker);
        foreach (var (contract, implementation) in Services)
        {
            var service = first.ServiceProvider.GetRequiredService(contract);
            Assert.IsType(implementation, service);
            Assert.Same(service, Assert.Single(first.ServiceProvider.GetServices(contract)));
            Assert.Same(service, first.ServiceProvider.GetRequiredService(contract));
            Assert.NotSame(service, second.ServiceProvider.GetRequiredService(contract));
            Assert.Null(first.ServiceProvider.GetService(implementation));
        }
        var repository = Assert.Single(first.ServiceProvider.GetServices<IAccountRepository>());
        Assert.IsType<AccountRepository>(repository);
        Assert.Same(repository, first.ServiceProvider.GetRequiredService<IAccountRepository>());
        Assert.NotSame(repository, second.ServiceProvider.GetRequiredService<IAccountRepository>());
        Assert.IsType<VideoLibraryRepository>(Assert.Single(first.ServiceProvider.GetServices<IVideoLibraryRepository>()));
        var store = Assert.IsType<Static_X.API.Integrations.Storage.S3VideoObjectStore>(Assert.Single(first.ServiceProvider.GetServices<IVideoObjectStore>()));
        Assert.Same(store, first.ServiceProvider.GetRequiredService<IVideoObjectStore>());
        Assert.NotSame(store, second.ServiceProvider.GetRequiredService<IVideoObjectStore>());
    }

    [Fact]
    public void ApplicationDI_UsesSingleBllMappingConfigurationAndExternalLicenseSetting()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
            .UseSetting("Activation:KeyRingPath", "")
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["AutoMapper:LicenseKey"] = "" })));
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        var mapper = first.ServiceProvider.GetRequiredService<IMapper>();
        var configuration = first.ServiceProvider.GetRequiredService<AutoMapper.IConfigurationProvider>();

        Assert.Same(configuration, mapper.ConfigurationProvider);
        Assert.Same(configuration, second.ServiceProvider.GetRequiredService<IMapper>().ConfigurationProvider);
        configuration.AssertConfigurationIsValid();
        Assert.Equal("", factory.Services.GetRequiredService<IOptions<MapperConfigurationExpression>>().Value.LicenseKey);
        Assert.Equal(new[] { "AccountProfile", "AccountProfile", "AccountProfile", "DashboardProfile", "GymProfile", "PaymentProfile", "PaymentProfile",
                "RoutineProfile", "RoutineProfile", "RoutineProfile", "StudentProfile", "VideoProfile" },
            configuration.Internal().GetAllTypeMaps().Select(map => map.Profile.Name.Split('.').Last()).Order());
        var result = mapper.Map<StudentResponse>(new Student { Id = 73, FirstName = "DI name", LastName = "DI surname", Email = "DI@EXAMPLE.COM" });
        Assert.Equal((73, "DI name", "DI surname", "DI@EXAMPLE.COM"),
            (result.Id, result.Nombre, result.Apellido, result.Email));
    }

    [Fact]
    public void MappingDependencies_RemainOutsideDalEntityAndActivationServices()
    {
        foreach (var assembly in new[] { typeof(Account).Assembly, typeof(StaticXDbContext).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name == "AutoMapper");
        foreach (var service in new[] { typeof(ActivationService), typeof(ActivationDispatcher) })
            Assert.DoesNotContain(service.GetConstructors().SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType == typeof(IMapper));
        foreach (var service in new[] { typeof(StudentService), typeof(AccountService), typeof(GymService), typeof(DashboardService), typeof(VideoLibraryService) })
            Assert.Single(service.GetConstructors().Single().GetParameters(), parameter => parameter.ParameterType == typeof(IMapper));
    }

    [Fact]
    public void DomainAccount_IsIndependentOfIdentityAndPersistence()
    {
        Assert.Equal("Entity", typeof(Account).Namespace);
        Assert.Equal("Entity", typeof(AccountRole).Namespace);
        Assert.Equal(typeof(object), typeof(Account).BaseType);
        Assert.All(typeof(Account).GetProperties(), property =>
            Assert.DoesNotContain("Microsoft.AspNetCore", property.PropertyType.FullName ?? ""));
        Assert.Equal(typeof(IdentityUser), typeof(IdentityAccount).BaseType);
        Assert.Equal("DAL", typeof(IdentityAccount).Namespace);
        Assert.Same(typeof(Account).Assembly, typeof(IdentityAccount).Assembly);
        Assert.Same(typeof(Account).Assembly, typeof(ActivationInvitation).Assembly);
        Assert.Same(typeof(Account).Assembly, typeof(GymProvisioningRecord).Assembly);
        Assert.Same(typeof(Account).Assembly, typeof(ActivationDelivery).Assembly);
        Assert.NotSame(typeof(Account).Assembly, typeof(StaticXDbContext).Assembly);
    }

    [Fact]
    public void RepositoryContractsAndProviderAdapters_OccupyTheirOwningLayers()
    {
        AssertSignature<IGymRepository>("ListAsync", typeof(Task<global::DAL.Repositories.Models.GymPage>), typeof(int), typeof(int), typeof(CancellationToken));
        AssertSignature<IGymRepository>("ListPendingAdministratorsAsync", typeof(Task<global::DAL.Repositories.Models.PendingAdministratorPage>), typeof(int), typeof(int), typeof(int), typeof(CancellationToken));
        Assert.Same(typeof(StaticXDbContext).Assembly, typeof(global::DAL.Repositories.Models.GymPage).Assembly);
        Assert.Same(typeof(StaticXDbContext).Assembly, typeof(global::DAL.Repositories.Models.PendingAdministratorPage).Assembly);
        Assert.Same(typeof(StaticXDbContext).Assembly, typeof(global::DAL.Repositories.Models.DashboardStudentCounts).Assembly);
        Assert.Equal(typeof(IReadOnlyList<Gym>), typeof(global::DAL.Repositories.Models.GymPage).GetProperty("Items")!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<Account>), typeof(global::DAL.Repositories.Models.PendingAdministratorPage).GetProperty("Items")!.PropertyType);
        var ports = new[] { typeof(IAccountRepository), typeof(IGymRepository),
            typeof(IStudentRepository), typeof(IDashboardRepository), typeof(IActivationRepository),
            typeof(IActivationDeliveryRepository), typeof(IPaymentRepository), typeof(IRoutineRepository), typeof(IVideoLibraryRepository) };
        Assert.All(ports, port => Assert.Equal("DAL.Repositories.Interfaces", port.Namespace));
        Assert.Equal("BLL.Interfaces", typeof(IActivationEmailSender).Namespace);
        Assert.Equal("BLL.Interfaces", typeof(BLL.Interfaces.IPasswordRecoveryEmailSender).Namespace);
        Assert.Equal("BLL.Interfaces", typeof(IVideoObjectStore).Namespace);
        Assert.Same(typeof(AccountService).Assembly, typeof(IVideoObjectStore).Assembly);

        var assembly = typeof(AccountService).Assembly;
        var businessTypes = assembly.GetTypes().Where(type => type.Namespace == "BLL" ||
            type.Namespace == "Entity" || type.Namespace == "BLL.Interfaces" ||
            type.Namespace == "BLL.Services.Interfaces");
        Assert.All(businessTypes, type =>
        {
            var dependencies = type.GetConstructors().SelectMany(ctor => ctor.GetParameters()
                .Select(parameter => parameter.ParameterType));
            Assert.DoesNotContain(dependencies, dependency =>
                (dependency.Namespace?.StartsWith("DAL", StringComparison.Ordinal) == true &&
                 !(type.Namespace == "BLL" && dependency.IsInterface && ports.Contains(dependency))) ||
                dependency.Namespace?.StartsWith("Resend", StringComparison.Ordinal) == true);
        });
        var dataAssembly = typeof(StaticXDbContext).Assembly;
        Assert.DoesNotContain(dataAssembly.GetTypes(),
            type => type.GetConstructors().SelectMany(ctor => ctor.GetParameters())
                .Any(parameter => parameter.ParameterType.Namespace?.StartsWith("Resend", StringComparison.Ordinal) == true));
        var adapter = typeof(Program).Assembly.GetType("Static_X.API.Integrations.ResendActivationEmailSender");
        Assert.NotNull(adapter);
        Assert.Contains(typeof(IActivationEmailSender), adapter.GetInterfaces());
        Assert.DoesNotContain(dataAssembly.GetTypes(),
            type => type.GetConstructors().SelectMany(ctor => ctor.GetParameters())
                .Any(parameter => parameter.ParameterType.Namespace?.StartsWith("BLL", StringComparison.Ordinal) == true));
        Assert.Equal("DAL.Repositories.Interfaces", typeof(ActivationDelivery).Namespace);
        var repositories = new[] { typeof(AccountRepository), typeof(GymRepository), typeof(StudentRepository), typeof(DashboardRepository),
            typeof(ActivationRepository), typeof(ActivationDeliveryRepository), typeof(RoutineRepository), typeof(VideoLibraryRepository) };
        Assert.All(repositories, repository => Assert.Equal("DAL.Repositories.Implementation", repository.Namespace));
        Assert.All(repositories.Concat(ports), type => Assert.Same(dataAssembly, type.Assembly));
        Assert.All(DeclaredServices, pair =>
        {
            Assert.Same(assembly, pair.Contract.Assembly);
            Assert.Same(assembly, pair.Implementation.Assembly);
        });
        Assert.Same(assembly, typeof(IActivationEmailSender).Assembly);
        Assert.Same(assembly, typeof(BLL.Interfaces.IPasswordRecoveryEmailSender).Assembly);
        Assert.Contains(typeof(BLL.Interfaces.IPasswordRecoveryEmailSender), typeof(Static_X.API.Integrations.ResendPasswordRecoveryEmailSender).GetInterfaces());
        Assert.Same(typeof(Program).Assembly, typeof(Static_X.API.Integrations.ResendPasswordRecoveryEmailSender).Assembly);
        Assert.Same(typeof(Program).Assembly, adapter.Assembly);
    }

    [Fact]
    public void IdentityAdapter_RetainsIdentityTableAndRoleMapping()
    {
        var options = new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlite("Data Source=:memory:").Options;
        using var context = new StaticXDbContext(options);
        var user = context.Model.FindEntityType(typeof(IdentityAccount));
        Assert.NotNull(user);
        Assert.Equal("AspNetUsers", user.GetTableName());
        Assert.Equal(typeof(string), user.FindProperty(nameof(IdentityAccount.Role))?.GetTypeMapping().Converter?.ProviderClrType);
        Assert.Contains(user.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(new[] { "GymId", "NormalizedEmail" }));
        Assert.Null(context.Model.FindEntityType(typeof(ActivationDelivery)));
        Assert.Null(context.Model.FindEntityType(typeof(Account)));
    }

    [Fact]
    public void PhysicalProjects_EnforceTraditionalReferenceBoundaries()
    {
        var root = RepositoryRoot();
        var expected = new Dictionary<string, string[]>
        {
            ["Entity"] = [],
            ["DAL"] = ["Entity"],
            ["BLL"] = ["DAL", "Entity"],
            ["API"] = ["BLL", "DAL", "Entity"]
        };
        foreach (var (layer, dependencies) in expected)
        {
            var directory = Path.Combine(root, "src", "Static_X." + layer);
            var project = XDocument.Load(Path.Combine(directory, "Static_X." + layer + ".csproj"));
            var actual = project.Descendants("ProjectReference").Select(reference =>
                Path.GetFullPath(Path.Combine(directory, reference.Attribute("Include")!.Value
                    .Replace('\\', Path.DirectorySeparatorChar)))).Order().ToArray();
            var allowed = dependencies.Select(dependency => Path.GetFullPath(Path.Combine(root, "src",
                "Static_X." + dependency, "Static_X." + dependency + ".csproj"))).Order().ToArray();
            Assert.Equal(allowed, actual);
            Assert.Equal("net10.0", project.Descendants("TargetFramework").Single().Value);
        }

        var entity = XDocument.Load(Path.Combine(root, "src", "Static_X.Entity", "Static_X.Entity.csproj"));
        Assert.Empty(entity.Descendants("PackageReference"));
        Assert.Equal("Microsoft.AspNetCore.App", entity.Descendants("FrameworkReference")
            .Single().Attribute("Include")!.Value); // Deliberate IdentityAccount Identity exception, not EF.
        var assemblies = new[] { typeof(Account).Assembly, typeof(StaticXDbContext).Assembly,
            typeof(AccountService).Assembly, typeof(Program).Assembly };
        foreach (var assembly in assemblies)
        {
            var layer = assembly.GetName().Name!["Static_X.".Length..];
            var referencedLayers = assembly.GetReferencedAssemblies()
                .Where(reference => reference.Name!.StartsWith("Static_X.", StringComparison.Ordinal))
                .Select(reference => reference.Name!["Static_X.".Length..]).ToArray();
            Assert.All(referencedLayers, reference => Assert.Contains(reference, expected[layer]));
        }
        Assert.DoesNotContain(typeof(Account).Assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            reference.Name.StartsWith("Resend", StringComparison.Ordinal));
        Assert.All(typeof(Account).Assembly.GetExportedTypes().Where(type => type != typeof(IdentityAccount)), type =>
        {
            Assert.False(type.BaseType?.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true,
                $"Only IdentityAccount may inherit an ASP.NET Core type, not {type.FullName}.");
            Assert.All(type.GetProperties(), property =>
                Assert.False(property.PropertyType.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true,
                    $"Domain property {type.FullName}.{property.Name} must not expose ASP.NET Core types."));
        });
    }

    [Fact]
    public void SqlServerModel_MatchesPreservedSnapshotAndMigrationHistoryOffline()
    {
        using var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var migrations = context.GetService<IMigrationsAssembly>();
        Assert.Same(typeof(StaticXDbContext).Assembly, migrations.Assembly);
        Assert.NotNull(migrations.ModelSnapshot);
        Assert.Equal(new[]
        {
            "20261018181500_InicialAlumno", "20261019234555_AddGymStudentTenancy",
            "20261020000233_AddGymAccounts", "20261020002609_AddActivationProvisioning",
            "20261020003316_AddActivationDeliveryRecovery", "20261027023459_AddStudentActiveState",
            "20261028230341_AddGlobalSuperAdmin", "20261109221729_AddPasswordRecoveryOutbox",
            "20261111012132_AddTenantAccountInvitations", "20261116000000_AddPayments",
            "20261119013122_AddStudentRoutines", "20261123000000_AddRoutineMetadata",
            "20261123010000_AddExerciseDuration", "20261125013528_AddVideoLibrary"
        }, migrations.Migrations.Keys);
        Assert.False(context.Database.HasPendingModelChanges());
        var script = context.GetService<IMigrator>().GenerateScript();
        Assert.Contains("CREATE TABLE [AspNetUsers]", script);
        Assert.Contains("[IX_AspNetUsers_GymId_NormalizedEmail]", script);
        Assert.Contains("[LeaseExpiresAt]", script);
        Assert.DoesNotContain("CREATE TABLE [ActivationDelivery]", script);
    }

    [Fact]
    public void StudentStateMigration_OnlyAddsNonNullBitDefaultTrue_AndDownOnlyRemovesColumn()
    {
        using var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations["20261027023459_AddStudentActiveState"], context.Database.ProviderName!);
        var add = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal(("Alumnos", "Activo", "bit", false, true), (add.Table, add.Name, add.ColumnType, add.IsNullable, add.DefaultValue));
        var drop = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.DropColumnOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal(("Alumnos", "Activo"), (drop.Table, drop.Name));
        var script = context.GetService<IMigrator>().GenerateScript("20261020003316_AddActivationDeliveryRecovery", "20261027023459_AddStudentActiveState");
        Assert.Contains("ALTER TABLE [Alumnos] ADD [Activo] bit NOT NULL DEFAULT CAST(1 AS bit)", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("DELETE", script);
        Assert.DoesNotContain("CREATE TABLE [Alumnos]", script);
        Assert.Empty(context.Model.FindEntityType(typeof(Student))!.GetDeclaredQueryFilters());
    }

    [Fact]
    public void HttpHost_UsesMovedApiAssemblyAndContentRoot()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Static-X", "Server=unused;Database=unused;Integrated Security=true")
            .UseSetting("Activation:KeyRingPath", ""));
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        Assert.Equal("Static_X.API", environment.ApplicationName);
        Assert.Equal(Path.GetFullPath(Path.Combine(RepositoryRoot(), "src", "Static_X.API")),
            Path.GetFullPath(environment.ContentRootPath));
    }

    [Fact]
    public void GlobalMigration_ChangesOnlyScopeIntegrityAndDoesNotSeedAccounts()
    {
        using var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var assembly = context.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations["20261028230341_AddGlobalSuperAdmin"], context.Database.ProviderName!);
        Assert.Equal(5, migration.UpOperations.Count);
        Assert.DoesNotContain(migration.UpOperations, operation => operation is Microsoft.EntityFrameworkCore.Migrations.Operations.InsertDataOperation or
            Microsoft.EntityFrameworkCore.Migrations.Operations.UpdateDataOperation or Microsoft.EntityFrameworkCore.Migrations.Operations.DeleteDataOperation);
        var column = Assert.Single(migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AlterColumnOperation>());
        Assert.Equal(("AspNetUsers", "GymId", true), (column.Table, column.Name, column.IsNullable));
        var indexes = migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation>().ToArray();
        Assert.Contains(indexes, index => index.Name == "GlobalEmailIndex" && index.IsUnique && index.Filter == "[GymId] IS NULL");
        Assert.Contains(indexes, index => index.Name == "IX_AspNetUsers_GymId_NormalizedEmail" && index.IsUnique && index.Filter == "[GymId] IS NOT NULL");
        var constraint = Assert.Single(migration.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.AddCheckConstraintOperation>());
        Assert.Equal("CK_AspNetUsers_RoleGym", constraint.Name);
        var rollback = context.GetService<IMigrator>().GenerateScript("20261028230341_AddGlobalSuperAdmin", "20261027023459_AddStudentActiveState");
        Assert.Contains("THROW 51000", rollback);
        Assert.DoesNotContain("SET [GymId] = 0", rollback);
        Assert.Null(typeof(Program).Assembly.GetType("Static_X.API.ActivationCli"));
        Assert.Null(typeof(Program).Assembly.GetType("Static_X.API.Integrations.DevelopmentPickupEmailSender"));
        Assert.DoesNotContain(typeof(ActivationDispatcher).GetConstructors().Single().GetParameters(), parameter => parameter.ParameterType == typeof(bool));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Static-X.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Static-X.sln was not found.");
    }
}
