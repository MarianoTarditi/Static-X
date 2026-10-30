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
    private static readonly (Type Contract, Type Implementation)[] Services =
    [
        (typeof(IAccountService), typeof(AccountService)),
        (typeof(IActivationService), typeof(ActivationService)),
        (typeof(IActivationDispatcher), typeof(ActivationDispatcher)),
        (typeof(IGymService), typeof(GymService)),
        (typeof(IStudentService), typeof(StudentService))
    ];

    [Fact]
    public void ServiceInterfaces_PreservePublicSignaturesAndDefaults()
    {
        var nullability = new NullabilityInfoContext();
        foreach (var (contract, implementation) in Services)
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
        Assert.Equal(6, contracts.Length);
        foreach (var type in contracts.Concat(Services.Select(pair => pair.Implementation)))
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
        AssertSignature<IGymService>("AddAsync", typeof(Task<GymResponse>), typeof(CreateGymRequest), typeof(CancellationToken));
        AssertSignature<IGymService>("ListAsync", typeof(Task<GymListResponse>), typeof(GymListRequest), typeof(CancellationToken));
        AssertSignature<IGymService>("ListPendingAdministratorsAsync", typeof(Task<PendingAdministratorListResponse>), typeof(int), typeof(GymListRequest), typeof(CancellationToken));
        Assert.Equal(new[] { "Page", "PageSize" }, typeof(GymListRequest).GetProperties().Select(property => property.Name).Order());
        AssertSignature<IActivationService>("ProvisionAsync", typeof(Task<int>), typeof(ProvisionGymRequest), typeof(CancellationToken));
        AssertSignature<IActivationService>("ActivateAsync", typeof(Task<ActivationResponse>), typeof(ActivateAccountRequest), typeof(CancellationToken));
        AssertSignature<IActivationService>("ReissueAsync", typeof(Task<bool>), typeof(int), typeof(string), typeof(CancellationToken));
        AssertSignature<IActivationDispatcher>("DispatchOneAsync", typeof(Task<bool>), typeof(CancellationToken));
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
        Assert.True(ExposesEntity(typeof(Dictionary<string, List<Alumno[]>>), new HashSet<Type>()));
        Assert.True(ExposesEntity(typeof(Task<Gimnasio>), new HashSet<Type>()));
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
        Assert.Same(typeof(Account).Assembly, typeof(GymAccount).Assembly); // Retain the explicit Identity adapter.
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
            Assert.Same(service, first.ServiceProvider.GetRequiredService(contract));
            Assert.NotSame(service, second.ServiceProvider.GetRequiredService(contract));
            Assert.Null(first.ServiceProvider.GetService(implementation));
        }
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
        Assert.Equal(new[] { "AccountProfile", "AccountProfile", "GymProfile", "StudentProfile" },
            configuration.Internal().GetAllTypeMaps().Select(map => map.Profile.Name.Split('.').Last()).Order());
        var result = mapper.Map<StudentResponse>(new Alumno { Id = 73, Nombre = "DI name", Apellido = "DI surname", Email = "DI@EXAMPLE.COM" });
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
        foreach (var service in new[] { typeof(StudentService), typeof(AccountService), typeof(GymService) })
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
        Assert.Equal(typeof(IdentityUser), typeof(GymAccount).BaseType);
        Assert.Equal("DAL", typeof(GymAccount).Namespace);
        Assert.Same(typeof(Account).Assembly, typeof(GymAccount).Assembly);
        Assert.Same(typeof(Account).Assembly, typeof(ActivationInvitation).Assembly);
        Assert.Same(typeof(Account).Assembly, typeof(ProvisioningRequest).Assembly);
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
        Assert.Equal(typeof(IReadOnlyList<Gimnasio>), typeof(global::DAL.Repositories.Models.GymPage).GetProperty("Items")!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<Account>), typeof(global::DAL.Repositories.Models.PendingAdministratorPage).GetProperty("Items")!.PropertyType);
        var ports = new[] { typeof(IAccountRepository), typeof(IGymRepository),
            typeof(IStudentRepository), typeof(IActivationRepository),
            typeof(IActivationDeliveryRepository) };
        Assert.All(ports, port => Assert.Equal("DAL.Repositories.Interfaces", port.Namespace));
        Assert.Equal("BLL.Interfaces", typeof(IActivationEmailSender).Namespace);

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
        var repositories = new[] { typeof(AccountRepository), typeof(GymRepository), typeof(StudentRepository),
            typeof(ActivationRepository), typeof(ActivationDeliveryRepository) };
        Assert.All(repositories, repository => Assert.Equal("DAL.Repositories.Implementation", repository.Namespace));
        Assert.All(repositories.Concat(ports), type => Assert.Same(dataAssembly, type.Assembly));
        Assert.All(Services, pair =>
        {
            Assert.Same(assembly, pair.Contract.Assembly);
            Assert.Same(assembly, pair.Implementation.Assembly);
        });
        Assert.Same(assembly, typeof(IActivationEmailSender).Assembly);
        Assert.Same(typeof(Program).Assembly, adapter.Assembly);
    }

    [Fact]
    public void IdentityAdapter_RetainsIdentityTableAndRoleMapping()
    {
        var options = new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlite("Data Source=:memory:").Options;
        using var context = new StaticXDbContext(options);
        var user = context.Model.FindEntityType(typeof(GymAccount));
        Assert.NotNull(user);
        Assert.Equal("AspNetUsers", user.GetTableName());
        Assert.Equal(typeof(string), user.FindProperty(nameof(GymAccount.Role))?.GetTypeMapping().Converter?.ProviderClrType);
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
            .Single().Attribute("Include")!.Value); // Deliberate GymAccount Identity exception, not EF.
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
        Assert.All(typeof(Account).Assembly.GetExportedTypes().Where(type => type != typeof(GymAccount)), type =>
        {
            Assert.False(type.BaseType?.Namespace?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true,
                $"Only GymAccount may inherit an ASP.NET Core type, not {type.FullName}.");
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
            "20261028230341_AddGlobalSuperAdmin"
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
        Assert.Empty(context.Model.FindEntityType(typeof(Alumno))!.GetDeclaredQueryFilters());
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
