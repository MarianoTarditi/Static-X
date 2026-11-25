using BLL.DTOs.Payments;
using Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Static_X.Tests;

public sealed class PaymentRepositoryTests
{
    [Fact]
    public async Task SnapshotAndSelfLookup_AreGymScopedAndNeverInferContactEmail()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        Assert.Null(await fixture.Repository.SnapshotAsync(2, 1, new DateOnly(2026, 11, 15), default));
        Assert.Null(await fixture.Repository.FindOwnAsync(2, "student", default));
        Assert.Null(await fixture.Repository.FindOwnAsync(1, "CONTACT@EXAMPLE.INVALID", default));
        Assert.Equal(1, (await fixture.Repository.FindOwnAsync(1, "student", default))!.Id);
        Assert.Empty(await fixture.Repository.HistoryAsync(2, 1, 0, 20, default));
    }

    [Fact]
    public async Task Register_StaleRevisionAndDuplicateKeyCannotProduceSecondEffect()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var first = await fixture.PayAsync(new DateOnly(2026, 11, 15));
        var row = await fixture.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(first.Id, (await fixture.Repository.RegisterAsync(row, 0, new DateOnly(2026, 11, 15), default)).Id);
        row.Id = 0; row.IdempotencyKey = "different-key";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.RegisterAsync(row, 0, new DateOnly(2026, 11, 15), default));
        Assert.Equal(1, await fixture.Db.Payments.CountAsync());
        Assert.Equal(1, (await fixture.Db.Alumnos.AsNoTracking().SingleAsync(s => s.Id == 1)).PaymentRevision);
        row.RequestFingerprint = new string('A', 64); row.IdempotencyKey = (await fixture.Db.Payments.AsNoTracking().SingleAsync()).IdempotencyKey;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.RegisterAsync(row, 1, new DateOnly(2026, 11, 15), default));
    }

    [Theory]
    [InlineData(PaymentStatus.NoPayments, 1)]
    [InlineData(PaymentStatus.Current, 0)]
    [InlineData(PaymentStatus.ExpiringSoon, 0)]
    [InlineData(PaymentStatus.Expired, 0)]
    public async Task Table_EmptyCoverageStatusFilteringAndBoundedSearchStayInsideGym(PaymentStatus status, int count)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var page = await fixture.Repository.ListAsync(1, "Ana", status, new DateOnly(2026, 11, 15), 0, 1, default);
        Assert.Equal(count, page.TotalCount);
        Assert.Equal(count, page.Items.Count);
        Assert.All(page.Items, item => Assert.Equal(1, item.Student.GimnasioId));
        Assert.Empty((await fixture.Repository.ListAsync(1, "Foreign", null, new DateOnly(2026, 11, 15), 0, 100, default)).Items);
    }

    [Theory]
    [InlineData(8, PaymentStatus.Current)]
    [InlineData(9, PaymentStatus.ExpiringSoon)]
    [InlineData(14, PaymentStatus.ExpiringSoon)]
    [InlineData(15, PaymentStatus.Expired)]
    public async Task Table_StatusPredicatesMatchInclusivePaidThroughBoundaries(int decemberDay, PaymentStatus status)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        await fixture.PayAsync(new DateOnly(2026, 11, 15));
        foreach (var filter in Enum.GetValues<PaymentStatus>())
        {
            var result = await fixture.Repository.ListAsync(1, null, filter, new DateOnly(2026, 12, decemberDay), 0, 20, default);
            Assert.Equal(filter == status ? 1 : 0, result.TotalCount);
        }
    }

    [Fact]
    public async Task Schema_CompositeForeignKeysAndMethodChecksRejectCrossGymAndInvalidRows()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        await fixture.PayAsync(new DateOnly(2026, 11, 15));
        fixture.Db.ChangeTracker.Clear();
        var original = await fixture.Db.Payments.AsNoTracking().SingleAsync();
        foreach (var invalid in new[] { "gym", "student", "price", "method", "amount" })
        {
            var row = new Payment
            {
                GymId = invalid == "gym" ? 2 : 1,
                StudentId = invalid == "student" ? 2 : 1,
                PriceId = invalid == "price" ? 99 : original.PriceId,
                Sequence = 2,
                AmountCents = invalid == "amount" ? 0 : original.AmountCents,
                Method = invalid == "method" ? (PaymentMethod)99 : PaymentMethod.Card,
                PaidOn = original.PaidOn,
                CoverageStart = original.CoverageStart,
                ExtensionStart = original.ExtensionStart,
                CoverageEndExclusive = original.CoverageEndExclusive,
                IdempotencyKey = invalid,
                RequestFingerprint = new string('A', 64),
                ActorId = "admin"
            };
            fixture.Db.Payments.Add(row);
            await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Db.SaveChangesAsync());
            fixture.Db.ChangeTracker.Clear();
            Assert.Equal(1, await fixture.Db.Payments.CountAsync());
        }
    }

    [Fact]
    public async Task MissingInitialPrice_RejectsPaymentAndInitialDateRulesAreIndependentOfHttp()
    {
        await using var fixture = await PaymentFixture.CreateAsync(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ConfirmationAsync(new DateOnly(2026, 11, 15)));
        foreach (var date in new DateOnly?[] { null, default(DateOnly), new DateOnly(2026, 11, 16) })
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SetPriceAsync(1, AccountRole.GymAdmin, "admin", new SetPassPriceRequest { AmountCents = 1, InitialEffectiveFrom = date }, default));
        Assert.Empty(await fixture.Db.GymPassPrices.ToArrayAsync());
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
        var initial = await fixture.Service.SetPriceAsync(1, AccountRole.GymAdmin, "admin", new SetPassPriceRequest { AmountCents = 1, InitialEffectiveFrom = new DateOnly(2026, 11, 1) }, default);
        Assert.Equal(1, initial.AmountCents);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SetPriceAsync(1, AccountRole.GymAdmin, "admin", new SetPassPriceRequest { AmountCents = 2, InitialEffectiveFrom = new DateOnly(2026, 11, 1) }, default));
    }

    [Fact]
    public void Migration_OfflineSqlServerAddsOnlyPaymentSchemaAndGuardsLedgerRollback()
    {
        using var context = new global::DAL.StaticXDbContext(new DbContextOptionsBuilder<global::DAL.StaticXDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        Assert.False(context.Database.HasPendingModelChanges());
        var migrator = context.GetService<IMigrator>();
        var sql = migrator.GenerateScript("20261111012132_AddTenantAccountInvitations", "20261116000000_AddPayments");
        Assert.Contains("CREATE TABLE [Payments]", sql);
        Assert.Contains("CREATE TABLE [GymPassPrices]", sql);
        Assert.Contains("FOREIGN KEY ([GymId], [StudentId])", sql);
        Assert.Contains("FOREIGN KEY ([GymId], [PriceId])", sql);
        Assert.Contains("UNIQUE INDEX [IX_Payments_GymId_IdempotencyKey]", sql);
        Assert.DoesNotContain("UPDATE [AspNetUsers]", sql);
        Assert.DoesNotContain("INSERT INTO [Payments]", sql);
        var down = migrator.GenerateScript("20261116000000_AddPayments", "20261111012132_AddTenantAccountInvitations");
        Assert.Contains("THROW 51000", down);
        Assert.Contains("DROP TABLE [Payments]", down);
    }
}
