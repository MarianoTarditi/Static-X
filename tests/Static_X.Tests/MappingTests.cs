using AutoMapper;
using AutoMapper.Internal;
using BLL;
using BLL.DTOs.Accounts;
using BLL.DTOs.Gyms;
using BLL.DTOs.Students;
using BLL.Mappings;
using DAL;
using Entity;
using Xunit;

namespace Static_X.Tests;

public sealed class MappingTests
{
    [Fact]
    public void Profiles_ConfigurationIsValid()
    {
        TestMapper.Configuration.AssertConfigurationIsValid();
    }

    [Fact]
    public void Profiles_ContainOnlyFourExplicitEntityToResponseMapsInBll()
    {
        // Test-only configuration inspection prevents accidental reverse, request, or Identity maps.
        var maps = TestMapper.Configuration.Internal().GetAllTypeMaps();
        Assert.Equal(4, maps.Count);
        Assert.Contains(maps, map => map.SourceType == typeof(Alumno) && map.DestinationType == typeof(StudentResponse));
        Assert.Contains(maps, map => map.SourceType == typeof(Account) && map.DestinationType == typeof(AccountResponse));
        Assert.Contains(maps, map => map.SourceType == typeof(Account) && map.DestinationType == typeof(PendingAdministratorResponse));
        Assert.Contains(maps, map => map.SourceType == typeof(Gimnasio) && map.DestinationType == typeof(GymResponse));
        Assert.All(new[] { typeof(StudentProfile), typeof(AccountProfile), typeof(GymProfile) }, profile =>
        {
            Assert.Same(typeof(StudentService).Assembly, profile.Assembly);
            Assert.Equal("BLL.Mappings", profile.Namespace);
            Assert.True(profile.IsSealed);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StudentProfile_MapsAllConstructorFieldsWithoutNormalizingOrExposingGym(bool active)
    {
        var source = new Alumno { Id = 43, GimnasioId = 97, Nombre = " First name ", Apellido = " Last name ", Email = " Mixed@Example.com ", Activo = active };
        var result = TestMapper.Instance.Map<StudentResponse>(source);

        Assert.Equal((43, " First name ", " Last name ", " Mixed@Example.com "),
            (result.Id, result.Nombre, result.Apellido, result.Email));
        Assert.Equal(active, result.Activo);
        Assert.Equal(active, source.Activo);
        Assert.Equal(new[] { "Activo", "Apellido", "Email", "Id", "Nombre" }, typeof(StudentResponse).GetProperties().Select(property => property.Name).Order());
        Assert.All(typeof(StudentResponse).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Equal((43, 97, " First name ", " Last name ", " Mixed@Example.com "),
            (source.Id, source.GimnasioId, source.Nombre, source.Apellido, source.Email));
    }

    [Fact]
    public void AccountProfile_MapsOnlyDomainIdentityFields()
    {
        var result = TestMapper.Instance.Map<AccountResponse>(new Account("account-82", 31, " Mixed@Example.com ", AccountRole.Trainer));

        Assert.Equal(("account-82", 31, " Mixed@Example.com ", AccountRole.Trainer),
            (result.Id, result.GymId, result.Email, result.Role));
        Assert.Equal(new[] { "Email", "GymId", "Id", "Role" }, typeof(AccountResponse).GetProperties().Select(property => property.Name).Order());
    }

    [Fact]
    public void GymProfile_MapsReadOnlyConstructorFieldsWithoutTrimming()
    {
        var result = TestMapper.Instance.Map<GymResponse>(new Gimnasio { Id = 61, Nombre = " Persisted gym " });

        Assert.Equal((61, " Persisted gym "), (result.Id, result.Nombre));
        Assert.All(typeof(GymResponse).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void AccountProfile_PendingAdministrator_MapsOnlyExactSafeFieldsWithoutNormalization()
    {
        var result = TestMapper.Instance.Map<PendingAdministratorResponse>(new Account("opaque", 31, " Mixed@Example.invalid ", AccountRole.GymAdmin));
        Assert.Equal(("opaque", " Mixed@Example.invalid "), (result.AccountId, result.Email));
        Assert.Equal(new[] { "AccountId", "Email" }, typeof(PendingAdministratorResponse).GetProperties().Select(property => property.Name).Order());
        Assert.Null(TestMapper.Instance.Map<PendingAdministratorResponse>((Account?)null));
        Assert.Empty(TestMapper.Instance.Map<PendingAdministratorResponse[]>(Array.Empty<Account>()));
        var items = TestMapper.Instance.Map<PendingAdministratorResponse[]>(new[]
        {
            new Account("z", 31, "z@example.invalid", AccountRole.GymAdmin),
            new Account("a", 31, "a@example.invalid", AccountRole.GymAdmin)
        });
        Assert.Equal(new[] { ("z", "z@example.invalid"), ("a", "a@example.invalid") }, items.Select(item => (item.AccountId, item.Email)));
    }

    [Fact]
    public void Profiles_NullEntitiesRemainNull_AndStudentArraysPreserveOrderAndEmptyResults()
    {
        Assert.Null(TestMapper.Instance.Map<StudentResponse>((Alumno?)null));
        Assert.Null(TestMapper.Instance.Map<AccountResponse>((Account?)null));
        Assert.Null(TestMapper.Instance.Map<GymResponse>((Gimnasio?)null));
        var result = TestMapper.Instance.Map<StudentResponse[]>(new[]
        {
            new Alumno { Id = 91, Nombre = "Zoe", Apellido = "West", Email = "Z@EXAMPLE.COM" },
            new Alumno { Id = 12, Nombre = "Amy", Apellido = "East", Email = "A@EXAMPLE.COM" }
        });

        Assert.Equal(new[] { (91, "Zoe", "West", "Z@EXAMPLE.COM"), (12, "Amy", "East", "A@EXAMPLE.COM") },
            result.Select(student => (student.Id, student.Nombre, student.Apellido, student.Email)));
        Assert.Empty(TestMapper.Instance.Map<StudentResponse[]>(Array.Empty<Alumno>()));
    }

    [Fact]
    public void Profiles_DoNotMapRequestsReverseResponsesOrIdentityCredentials()
    {
        var mapper = TestMapper.Instance;
        Assert.Throws<AutoMapperMappingException>(() => mapper.Map<Alumno>(new StudentResponse(1, "Name", "Surname", "a@example.com", true)));
        Assert.Throws<AutoMapperMappingException>(() => mapper.Map<Account>(new AccountResponse("id", 2, "a@example.com", AccountRole.Student)));
        Assert.Throws<AutoMapperMappingException>(() => mapper.Map<Gimnasio>(new GymResponse(3, "Gym")));
        Assert.Throws<AutoMapperMappingException>(() => mapper.Map<Alumno>(new CreateStudentRequest()));
        Assert.Throws<AutoMapperMappingException>(() => mapper.Map<Alumno>(new UpdateStudentRequest()));
        Assert.Throws<AutoMapperMappingException>(() => mapper.Map<GymAccount>(new CreateAccountRequest("a@example.com", "not-a-real-password")));
        Assert.Throws<AutoMapperMappingException>(() => mapper.Map<AccountResponse>(new GymAccount { PasswordHash = "not-a-real-hash", SecurityStamp = "not-a-real-stamp" }));
        Assert.Throws<AutoMapperMappingException>(() => mapper.Map<Account>(new PendingAdministratorResponse("id", "a@example.invalid")));
        Assert.Throws<AutoMapperMappingException>(() => mapper.Map<PendingAdministratorResponse>(new GymAccount { PasswordHash = "private" }));
    }
}
