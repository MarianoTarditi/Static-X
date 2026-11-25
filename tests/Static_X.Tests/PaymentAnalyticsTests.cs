using System.Reflection;
using BLL;
using BLL.DTOs.Payments;
using DAL;
using DAL.Repositories.Implementation;
using Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed class PaymentAnalyticsTests
{
    private static readonly DateOnly Today = new DateOnly(2026, 11, 15);

    [Fact]
    public async Task Summary_VoidRestoresPreviousRenewalAndAllVoidMeansNoValidPayment()
    {
        await using var f = await PaymentFixture.CreateAsync();
        var paid = await f.PayAsync(Today);
        var prepaid = await f.PayAsync(Today);
        f.Clock.Now = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, (await f.Service.SummaryAsync(1, AccountRole.Trainer, default)).EstimatedRenewalCount);
        await f.VoidAsync(prepaid.Id);
        var restored = await f.Service.SummaryAsync(1, AccountRole.Trainer, default);
        Assert.Equal((1, 100099L), (restored.EstimatedRenewalCount, restored.EstimatedRenewalCents));
        await f.VoidAsync(paid.Id);
        var empty = await f.Service.SummaryAsync(1, AccountRole.Trainer, default);
        Assert.Equal((1, 0, 0, 0, 0), (empty.NoValidPayment, empty.Expired, empty.DueSoon, empty.Current, empty.EstimatedRenewalCount));
    }

    [Theory]
    [InlineData(PaymentMethod.Cash)]
    [InlineData(PaymentMethod.Transfer)]
    [InlineData(PaymentMethod.Card)]
    public async Task Collections_EachMethodVoidRemainsSeparateFromValidRevenue(PaymentMethod method)
    {
        await using var f = await PaymentFixture.CreateAsync();
        await AddPayment(f, 1, Today.AddDays(30), 1, isVoid: true, method: method, amount: 9876543210L);
        var result = await f.Service.CollectionsAsync(1, AccountRole.GymAdmin, new PaymentCollectionRequest { From = Today, To = Today }, default);
        Assert.Equal((0, 0L, 1, 9876543210L), (result.ValidCount, result.ValidCents, result.VoidCount, result.VoidCents));
        var row = Assert.Single(result.Methods, m => m.Method == method);
        Assert.Equal((0, 0L, 1, 9876543210L), (row.ValidCount, row.ValidCents, row.VoidCount, row.VoidCents));
    }

    [Fact]
    public async Task Summary_ExclusiveBoundariesActiveGymAndLatestNonvoidAreMutuallyExclusive()
    {
        await using var f = await PaymentFixture.CreateAsync();
        await AddStudent(f, 3, true); await AddStudent(f, 4, true); await AddStudent(f, 5, true);
        await AddStudent(f, 6, true); await AddStudent(f, 7, false);
        await AddStudent(f, 8, true);
        await AddPayment(f, 1, Today, 1);
        await AddPayment(f, 3, Today.AddDays(1), 1);
        await AddPayment(f, 4, Today.AddDays(6), 1);
        await AddPayment(f, 5, Today.AddDays(7), 1);
        await AddPayment(f, 7, Today.AddDays(1), 1);
        await AddPayment(f, 8, Today.AddDays(-1), 1);
        await AddPayment(f, 2, Today.AddDays(1), 1, gym: 2);
        await AddPayment(f, 1, Today.AddDays(30), 2, isVoid: true);
        var result = await f.Service.SummaryAsync(1, AccountRole.Trainer, default);
        Assert.Equal((Today, 6, 1, 2, 2, 1), (result.BusinessDate, result.ActiveStudents, result.NoValidPayment, result.Expired, result.DueSoon, result.Current));
        Assert.Equal(result.ActiveStudents, result.NoValidPayment + result.Expired + result.DueSoon + result.Current);
        Assert.Equal("ARS", result.Currency);
        var direct = await f.Repository.SummaryAsync(1, Today, Today.AddDays(6), new DateOnly(2026, 12, 1), default);
        Assert.Equal((6, 1, 2, 2, 1), (direct.ActiveStudents, direct.NoValidPayment, direct.Expired, direct.DueSoon, direct.Current));
    }

    [Fact]
    public async Task Summary_RenewalUsesScheduledPriceAtExclusiveEndAndExcludesPrepaidAndNextMonth()
    {
        await using var f = await PaymentFixture.CreateAsync();
        await AddStudent(f, 3, true); await AddStudent(f, 4, true); await AddStudent(f, 5, true);
        await AddPayment(f, 1, Today.AddDays(1), 1);
        await AddPayment(f, 3, Today.AddDays(2), 1);
        await AddPayment(f, 4, new DateOnly(2026, 12, 1), 1);
        await AddPayment(f, 5, Today.AddDays(1), 1);
        await AddPayment(f, 5, Today.AddDays(31), 2);
        // Renewal at tomorrow's exclusive end uses tomorrow's price, not today's or the recorded amount.
        await f.Service.SetPriceAsync(1, AccountRole.GymAdmin, "admin", new SetPassPriceRequest { AmountCents = 250099 }, default);
        var result = await f.Service.SummaryAsync(1, AccountRole.GymAdmin, default);
        Assert.Equal((2, 500198L), (result.EstimatedRenewalCount, result.EstimatedRenewalCents));
        Assert.Equal(4, result.ActiveStudents);
        Assert.Equal(0, result.NoValidPayment);
    }

    [Theory]
    [InlineData(2026, 12, 31, 2027, 1, 1)]
    [InlineData(2028, 2, 28, 2028, 3, 1)]
    public async Task Summary_LocalMonthBoundaryAndPrepaidExtensionExcludeNextMonth(int year, int month, int day, int nextYear, int nextMonth, int nextDay)
    {
        await using var f = await PaymentFixture.CreateAsync();
        var date = new DateOnly(year, month, day);
        f.Clock.Now = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);
        await AddPayment(f, 1, new DateOnly(nextYear, nextMonth, nextDay), 1);
        var result = await f.Service.SummaryAsync(1, AccountRole.Trainer, default);
        Assert.Equal(date, result.BusinessDate);
        Assert.Equal((0, 0L), (result.EstimatedRenewalCount, result.EstimatedRenewalCents));
    }

    [Fact]
    public async Task Summary_MidnightCapturesOneBuenosAiresDateForCountsAndRenewalMonth()
    {
        await using var f = await PaymentFixture.CreateAsync();
        await AddPayment(f, 1, new DateOnly(2026, 12, 1), 1);
        var clock = new CrossingClock();
        var service = new PaymentService(f.Repository, clock, TestMapper.Instance, f.Protection);
        var result = await service.SummaryAsync(1, AccountRole.Trainer, default);
        Assert.Equal(new DateOnly(2026, 11, 30), result.BusinessDate);
        Assert.Equal((1, 0, 0), (result.DueSoon, result.Expired, result.EstimatedRenewalCount));
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public async Task Summary_NoApplicableTariffIsNullAndEmptyConfiguredGymIsZero()
    {
        await using var f = await PaymentFixture.CreateAsync(false);
        var missing = await f.Service.SummaryAsync(1, AccountRole.Trainer, default);
        Assert.Equal((1, 1, 0), (missing.ActiveStudents, missing.NoValidPayment, missing.EstimatedRenewalCount));
        Assert.Null(missing.EstimatedRenewalCents);
        f.Db.GymPassPrices.Add(new GymPassPrice { GymId = 1, AmountCents = 500, EffectiveFrom = Today.AddDays(10), CreatedBy = "admin" });
        await f.Db.SaveChangesAsync();
        await AddPayment(f, 1, Today.AddDays(1), 1);
        var notApplicable = await f.Service.SummaryAsync(1, AccountRole.Trainer, default);
        Assert.Equal(1, notApplicable.EstimatedRenewalCount);
        Assert.Null(notApplicable.EstimatedRenewalCents);
        await f.Db.Alumnos.Where(s => s.GimnasioId == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Activo, false));
        f.Clock.Now = f.Clock.Now.AddDays(10);
        var empty = await f.Service.SummaryAsync(1, AccountRole.Trainer, default);
        Assert.Equal((0, 0, 0, 0, 0, 0, 0L), (empty.ActiveStudents, empty.NoValidPayment, empty.Expired, empty.DueSoon, empty.Current, empty.EstimatedRenewalCount, empty.EstimatedRenewalCents));
    }

    [Fact]
    public async Task Collections_InclusivePaidOnAllMethodsInactiveRetroactiveAndLaterVoidStayInOriginalPeriod()
    {
        await using var f = await PaymentFixture.CreateAsync();
        await AddStudent(f, 3, false);
        await AddPayment(f, 1, Today.AddDays(30), 1, method: PaymentMethod.Cash, amount: 101);
        await AddPayment(f, 1, Today.AddDays(31), 2, method: PaymentMethod.Transfer, amount: 202);
        await AddPayment(f, 3, Today.AddDays(30), 1, method: PaymentMethod.Card, amount: 303);
        await AddPayment(f, 1, Today.AddDays(29), 3, method: PaymentMethod.Cash, amount: 404);
        await AddPayment(f, 1, Today.AddDays(32), 4, method: PaymentMethod.Card, amount: 505);
        await AddPayment(f, 2, Today.AddDays(30), 1, gym: 2, amount: 999);
        var request = new PaymentCollectionRequest { From = Today, To = Today.AddDays(1) };
        var before = await f.Service.CollectionsAsync(1, AccountRole.GymAdmin, request, default);
        Assert.Equal((3, 606L, 0, 0L), (before.ValidCount, before.ValidCents, before.VoidCount, before.VoidCents));
        await f.Db.Payments.Where(p => p.GymId == 1 && p.Method == PaymentMethod.Transfer).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.VoidedAt, new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero)).SetProperty(x => x.VoidedBy, "admin").SetProperty(x => x.VoidReason, "Correction"));
        var result = await f.Service.CollectionsAsync(1, AccountRole.GymAdmin, request, default);
        Assert.Equal((Today, Today, Today.AddDays(1), "ARS"), (result.BusinessDate, result.From, result.To, result.Currency));
        Assert.Equal((2, 404L, 1, 202L), (result.ValidCount, result.ValidCents, result.VoidCount, result.VoidCents));
        Assert.Collection(result.Methods,
            cash => Assert.Equal((PaymentMethod.Cash, 1, 101L, 0, 0L), (cash.Method, cash.ValidCount, cash.ValidCents, cash.VoidCount, cash.VoidCents)),
            transfer => Assert.Equal((PaymentMethod.Transfer, 0, 0L, 1, 202L), (transfer.Method, transfer.ValidCount, transfer.ValidCents, transfer.VoidCount, transfer.VoidCents)),
            card => Assert.Equal((PaymentMethod.Card, 1, 303L, 0, 0L), (card.Method, card.ValidCount, card.ValidCents, card.VoidCount, card.VoidCents)));
        var sameDay = await f.Service.CollectionsAsync(1, AccountRole.GymAdmin, new PaymentCollectionRequest { From = Today, To = Today }, default);
        Assert.Equal((2, 404L, 0), (sameDay.ValidCount, sameDay.ValidCents, sameDay.VoidCount));
        var direct = await f.Repository.CollectionsAsync(1, Today, Today.AddDays(1), default);
        Assert.Equal(404L, direct.Sum(r => r.ValidCents));
    }

    [Fact]
    public async Task Collections_EmptyHasAllThreeZeroMethodsAndReadsClockOnce()
    {
        await using var f = await PaymentFixture.CreateAsync();
        var clock = new CrossingClock();
        var service = new PaymentService(f.Repository, clock, TestMapper.Instance, f.Protection);
        var result = await service.CollectionsAsync(1, AccountRole.GymAdmin, new PaymentCollectionRequest { From = Today, To = Today.AddDays(365) }, default);
        Assert.Equal((0, 0L, 0, 0L), (result.ValidCount, result.ValidCents, result.VoidCount, result.VoidCents));
        Assert.Equal(Enum.GetValues<PaymentMethod>(), result.Methods.Select(m => m.Method));
        Assert.All(result.Methods, m => Assert.Equal((0, 0L, 0, 0L), (m.ValidCount, m.ValidCents, m.VoidCount, m.VoidCents)));
        Assert.Equal(new DateOnly(2026, 11, 30), result.BusinessDate);
        Assert.Equal(1, clock.Reads);
    }

    [Theory]
    [InlineData(AccountRole.Student)]
    [InlineData(AccountRole.SuperAdmin)]
    [InlineData((AccountRole)99)]
    public async Task Bll_RejectsUnauthorizedAnalyticsRolesIndependently(AccountRole role)
    {
        await using var f = await PaymentFixture.CreateAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SummaryAsync(1, role, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CollectionsAsync(1, role, new PaymentCollectionRequest(), default));
    }

    [Fact]
    public async Task Bll_RejectsTrainerCollectionsInvalidGymAndInvalidRangesAndPropagatesCancellation()
    {
        await using var f = await PaymentFixture.CreateAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CollectionsAsync(1, AccountRole.Trainer, new PaymentCollectionRequest(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SummaryAsync(0, AccountRole.Trainer, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.CollectionsAsync(0, AccountRole.GymAdmin, new PaymentCollectionRequest(), default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => f.Service.CollectionsAsync(1, AccountRole.GymAdmin, null!, default));
        foreach (var request in new[] {
            new PaymentCollectionRequest(), new PaymentCollectionRequest { From = Today }, new PaymentCollectionRequest { To = Today },
            new PaymentCollectionRequest { From = default(DateOnly), To = Today }, new PaymentCollectionRequest { From = Today, To = default(DateOnly) },
            new PaymentCollectionRequest { From = Today, To = Today.AddDays(-1) }, new PaymentCollectionRequest { From = Today, To = Today.AddDays(366) } })
            await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CollectionsAsync(1, AccountRole.GymAdmin, request, default));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.SummaryAsync(1, AccountRole.Trainer, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.CollectionsAsync(1, AccountRole.GymAdmin,
            new PaymentCollectionRequest { From = Today, To = Today }, cts.Token));
    }

    [Fact]
    public async Task Repository_QueriesAreSqlAggregatesWithBoundedCommandCountAndNoTracking()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var repository = new PaymentRepository(db);
        app.Fault.Commands.Clear();
        var empty = await repository.SummaryAsync(1, Today, Today.AddDays(6), new DateOnly(2026, 12, 1), default);
        Assert.Equal(1, empty.NoValidPayment);
        Assert.Equal(3, app.Fault.Commands.Count);
        Assert.Contains("SUM(", app.Fault.Commands[0]); Assert.Contains("NOT EXISTS", app.Fault.Commands[0]);
        Assert.Contains("SUM(", app.Fault.Commands[1]);
        db.Alumnos.AddRange(Enumerable.Range(10, 100).Select(id => new Alumno { Id = id, GimnasioId = 1, Nombre = "Additional", Apellido = "Ficha", Email = $"{id}@example.invalid" }));
        await db.SaveChangesAsync(); db.ChangeTracker.Clear(); app.Fault.Commands.Clear();
        Assert.Equal(101, (await repository.SummaryAsync(1, Today, Today.AddDays(6), new DateOnly(2026, 12, 1), default)).ActiveStudents);
        Assert.Equal(3, app.Fault.Commands.Count);
        app.Fault.Commands.Clear();
        Assert.Empty(await repository.CollectionsAsync(1, Today, Today, default));
        Assert.Single(app.Fault.Commands); Assert.Contains("GROUP BY", app.Fault.Commands[0]); Assert.Contains("SUM(", app.Fault.Commands[0]);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public void Repository_OfflineSqlServerTranslationLiftsRenewalTariffSubqueries()
    {
        using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var repository = new PaymentRepository(db);
        // Inspect the exact private query builders used by the public operations without opening a connection.
        string Sql(string method, params object[] args) => ((IQueryable)typeof(PaymentRepository).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(repository, args)!).ToQueryString();
        var coverage = Sql("CoverageSummaryQuery", 1, Today, Today.AddDays(6));
        var renewals = Sql("RenewalSummaryQuery", 1, Today, new DateOnly(2026, 12, 1));
        var collections = Sql("CollectionsQuery", 1, Today, Today);
        Assert.Contains("LEFT JOIN", coverage); Assert.Contains("NOT EXISTS", coverage); Assert.Contains("SUM(", coverage);
        Assert.Contains("OUTER APPLY", renewals); Assert.Contains("SUM(", renewals); Assert.Contains("ORDER BY", renewals);
        Assert.Contains("GROUP BY", collections); Assert.Contains("[PaidOn] >=", collections); Assert.Contains("[PaidOn] <=", collections);
        Assert.DoesNotContain("[ActorId]", collections); Assert.DoesNotContain("[StudentId]", collections);
    }

    private static async Task AddStudent(PaymentFixture f, int id, bool active)
    {
        f.Db.Alumnos.Add(new Alumno { Id = id, GimnasioId = 1, Nombre = "Test", Apellido = "Ficha", Email = $"{id}@example.invalid", Activo = active });
        await f.Db.SaveChangesAsync();
    }

    private static async Task AddPayment(PaymentFixture f, int student, DateOnly end, long sequence, bool isVoid = false, int gym = 1, PaymentMethod method = PaymentMethod.Cash, long amount = 100099)
    {
        var price = await f.Db.GymPassPrices.FirstOrDefaultAsync(p => p.GymId == gym);
        if (price is null)
        {
            price = new GymPassPrice { GymId = gym, AmountCents = amount, EffectiveFrom = new DateOnly(2026, 1, 1), CreatedBy = "admin" };
            f.Db.GymPassPrices.Add(price); await f.Db.SaveChangesAsync();
        }
        f.Db.Payments.Add(new Payment
        {
            GymId = gym, StudentId = student, Sequence = sequence, PriceId = price.Id, AmountCents = amount, Method = method,
            PaidOn = end.AddDays(-30), CoverageStart = end.AddDays(-30), ExtensionStart = end.AddDays(-30), CoverageEndExclusive = end,
            IdempotencyKey = $"{student}-{sequence}", RequestFingerprint = new string('A', 64), ActorId = "admin",
            CreatedAt = new DateTimeOffset(2026, 12, 20, 12, 0, 0, TimeSpan.Zero),
            VoidedAt = isVoid ? new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero) : null,
            VoidedBy = isVoid ? "admin" : null, VoidReason = isVoid ? "Correction" : null
        });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
    }

    private sealed class CrossingClock : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() => ++Reads == 1
            ? new DateTimeOffset(2026, 12, 1, 2, 59, 59, TimeSpan.Zero)
            : new DateTimeOffset(2026, 12, 1, 3, 0, 0, TimeSpan.Zero);
    }
}
