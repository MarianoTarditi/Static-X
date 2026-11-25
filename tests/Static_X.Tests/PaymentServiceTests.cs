using BLL;
using BLL.DTOs.Payments;
using DAL;
using DAL.Repositories.Implementation;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Static_X.Tests;

public sealed class PaymentServiceTests
{
    [Fact]
    public async Task List_CrossingBuenosAiresMidnightKeepsFilterAndRowOnSameBusinessDate()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        await fixture.PayAsync(new DateOnly(2026, 11, 15));
        var service = new PaymentService(fixture.Repository, new MidnightBoundaryClock(), TestMapper.Instance, fixture.Protection);

        var page = await service.ListAsync(1, AccountRole.Trainer,
            new PaymentListRequest { Status = PaymentStatus.ExpiringSoon }, default);

        Assert.Equal(1, page.TotalCount);
        var student = Assert.Single(page.Items);
        Assert.Equal((new DateOnly(2026, 12, 14), PaymentStatus.ExpiringSoon), (student.BusinessDate, student.Status));
    }

    private sealed class MidnightBoundaryClock : TimeProvider
    {
        private int reads;

        public override DateTimeOffset GetUtcNow() => ++reads == 1
            ? new DateTimeOffset(2026, 12, 15, 2, 59, 59, TimeSpan.Zero)
            : new DateTimeOffset(2026, 12, 15, 3, 0, 0, TimeSpan.Zero);
    }

    [Theory]
    [InlineData("Ana", null)]
    [InlineData(null, PaymentStatus.Current)]
    public async Task History_RejectsTableOnlyFilters(string? search, PaymentStatus? status)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var request = new PaymentListRequest { Search = search, Status = status };

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.HistoryAsync(
            1, AccountRole.Trainer, "trainer", 1, request, default));
    }

    [Fact]
    public async Task FirstEarlyAndLapsedPayments_PreserveContinuousStartAndRestoreExactPriorSnapshotOnVoid()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var first = await fixture.PayAsync(new DateOnly(2026, 11, 15));
        Assert.Equal((new DateOnly(2026, 11, 15), new DateOnly(2026, 12, 15)), (first.CoverageStart, first.CoverageEndExclusive));
        fixture.Clock.Now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var early = await fixture.PayAsync(new DateOnly(2026, 11, 20));
        Assert.Equal((new DateOnly(2026, 11, 15), new DateOnly(2026, 12, 15), new DateOnly(2027, 1, 14)),
            (early.CoverageStart, early.ExtensionStart, early.CoverageEndExclusive));
        fixture.Clock.Now = new DateTimeOffset(2027, 2, 1, 12, 0, 0, TimeSpan.Zero);
        var lapsed = await fixture.PayAsync(new DateOnly(2027, 2, 1));
        Assert.Equal((new DateOnly(2027, 2, 1), new DateOnly(2027, 3, 3)), (lapsed.CoverageStart, lapsed.CoverageEndExclusive));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.VoidAsync(first.Id));
        Assert.True(await fixture.VoidAsync(lapsed.Id));
        var restored = await fixture.Service.OwnCoverageAsync(1, AccountRole.Student, "student", default);
        Assert.Equal(early.CoverageEndExclusive, restored!.CoverageEndExclusive);
        Assert.Equal(PaymentStatus.Expired, restored.Status);
        Assert.True(await fixture.VoidAsync(early.Id));
        Assert.Equal(first.CoverageEndExclusive, (await fixture.Service.OwnCoverageAsync(1, AccountRole.Student, "student", default))!.CoverageEndExclusive);
        Assert.True(await fixture.VoidAsync(first.Id));
        var empty = await fixture.Service.OwnCoverageAsync(1, AccountRole.Student, "student", default);
        Assert.Equal(PaymentStatus.NoPayments, empty!.Status);
        Assert.Null(empty.CoverageEndExclusive);
        Assert.Equal(3, (await fixture.Service.HistoryAsync(1, AccountRole.Student, "student", null, new PaymentListRequest(), default)).Count);
        Assert.Equal(6, (await fixture.Db.Alumnos.AsNoTracking().SingleAsync(s => s.Id == 1)).PaymentRevision);
    }

    [Theory]
    [InlineData(8, PaymentStatus.Current)]
    [InlineData(9, PaymentStatus.ExpiringSoon)]
    [InlineData(14, PaymentStatus.ExpiringSoon)]
    [InlineData(15, PaymentStatus.Expired)]
    public async Task Status_SixFiveZeroAndExpiredDayBoundaries_UseInclusivePaidThrough(int decemberDay, PaymentStatus expected)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        await fixture.PayAsync(new DateOnly(2026, 11, 15));
        fixture.Clock.Now = new DateTimeOffset(2026, 12, decemberDay, 12, 0, 0, TimeSpan.Zero);
        var result = await fixture.Service.OwnCoverageAsync(1, AccountRole.Student, "student", default);
        Assert.Equal(expected, result!.Status);
        Assert.Equal(new DateOnly(2026, 12, 14), result.PaidThrough);
        Assert.Equal(expected != PaymentStatus.Expired, await fixture.Service.HasCoverageAsync(1, "student", default));
    }

    [Fact]
    public async Task ArgentinaMidnight_ExpiresExactlyAtThreeUtcWithoutChangingAccount()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        await fixture.PayAsync(new DateOnly(2026, 11, 15));
        fixture.Clock.Now = new DateTimeOffset(2026, 12, 15, 2, 59, 59, TimeSpan.Zero);
        Assert.True(await fixture.Service.HasCoverageAsync(1, "student", default));
        fixture.Clock.Now = fixture.Clock.Now.AddSeconds(1);
        Assert.False(await fixture.Service.HasCoverageAsync(1, "student", default));
        Assert.True((await fixture.Db.Users.AsNoTracking().SingleAsync()).IsEnabled);
    }

    [Fact]
    public async Task Prices_UseHistoricalPaidOnAndTomorrowImmutableConflictWithoutRepricingLedger()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var first = await fixture.PayAsync(new DateOnly(2026, 11, 15));
        var next = await fixture.Service.SetPriceAsync(1, AccountRole.GymAdmin, "admin", new SetPassPriceRequest { AmountCents = 250099 }, default);
        Assert.Equal(new DateOnly(2026, 11, 16), next.EffectiveFrom);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SetPriceAsync(1, AccountRole.GymAdmin, "admin", new SetPassPriceRequest { AmountCents = 400000 }, default));
        Assert.Equal(100099, (await fixture.PayAsync(new DateOnly(2026, 11, 15))).AmountCents);
        fixture.Clock.Now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var beforeNewPricePayment = Assert.Single((await fixture.Service.ListAsync(1, AccountRole.Trainer, new PaymentListRequest(), default)).Items);
        Assert.Equal(250099, beforeNewPricePayment.PricePer30DaysCents);
        Assert.Equal(100099, beforeNewPricePayment.LastPayment!.AmountCents);
        Assert.Equal(100099, (await fixture.PayAsync(new DateOnly(2026, 11, 15))).AmountCents);
        Assert.Equal(250099, (await fixture.PayAsync(new DateOnly(2026, 11, 16))).AmountCents);
        Assert.Equal(100099, (await fixture.Db.Payments.AsNoTracking().SingleAsync(p => p.Id == first.Id)).AmountCents);
        var table = await fixture.Service.ListAsync(1, AccountRole.Trainer, new PaymentListRequest { Search = "Ana", PageSize = 1 }, default);
        Assert.Equal(250099, Assert.Single(table.Items).PricePer30DaysCents);
        Assert.Equal(1, table.TotalCount);
    }

    [Fact]
    public async Task PreviewRevision_RejectsStaleAndAbaAndAllowsNewSameDayRenewal()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var pending = await fixture.ConfirmationAsync(new DateOnly(2026, 11, 15));
        var paid = await fixture.PayAsync(new DateOnly(2026, 11, 15));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, pending, default));
        Assert.True(await fixture.VoidAsync(paid.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, pending, default));
        var replacement = await fixture.PayAsync(new DateOnly(2026, 11, 15));
        Assert.Equal(3, replacement.Sequence);
        Assert.Equal(paid.CoverageEndExclusive, replacement.CoverageEndExclusive);
    }

    [Fact]
    public async Task Idempotency_ReplaysSameResultAfterVoidAndRejectsChangedActorTargetPayload()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var request = await fixture.ConfirmationAsync(new DateOnly(2026, 11, 15));
        var first = await fixture.Service.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, request, default);
        Assert.Equal(first.Id, (await fixture.Service.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, request, default)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RegisterAsync(1, AccountRole.GymAdmin, "other", 1, request, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RegisterAsync(1, AccountRole.Trainer, "trainer", 2, request, default));
        request.Method = PaymentMethod.Card;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, request, default));
        request.Method = PaymentMethod.Cash;
        await fixture.VoidAsync(first.Id);
        var replay = await fixture.Service.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, request, default);
        Assert.Equal(first.Id, replay.Id);
        Assert.True(replay.IsVoid);
        Assert.Equal(first.CoverageEndExclusive, replay.CoverageEndExclusive);
        Assert.Equal(1, await fixture.Db.Payments.CountAsync());
        Assert.False(await fixture.Service.HasCoverageAsync(1, "student", default));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("method")]
    [InlineData("inactive")]
    [InlineData("before-initial")]
    [InlineData("before-last")]
    [InlineData("foreign")]
    [InlineData("missing")]
    public async Task Preview_InvalidTargetsDatesMethodsAndState_DoNotWrite(string scenario)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var date = new DateOnly(2026, 11, 15);
        if (scenario == "before-last") await fixture.PayAsync(date);
        if (scenario == "inactive") await fixture.Db.Alumnos.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Activo, false));
        if (scenario == "future") date = date.AddDays(1);
        if (scenario == "before-initial") date = new DateOnly(2026, 10, 31);
        if (scenario == "before-last") date = date.AddDays(-1);
        var before = await fixture.Db.Payments.CountAsync();
        var error = await Record.ExceptionAsync(() => fixture.Service.PreviewAsync(1, AccountRole.Trainer, "trainer", scenario == "foreign" ? 2 : scenario == "missing" ? 99 : 1,
            new PaymentPreviewRequest { PaidOn = date, Method = scenario == "method" ? (PaymentMethod)99 : PaymentMethod.Transfer }, default));
        if (scenario is "foreign" or "missing") Assert.IsType<KeyNotFoundException>(error);
        else if (scenario is "inactive" or "before-initial") Assert.IsType<InvalidOperationException>(error);
        else Assert.IsType<ArgumentException>(error);
        Assert.Equal(before, await fixture.Db.Payments.CountAsync());
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(10000000001L)]
    public async Task Price_InvalidExactCents_RejectsWithoutMutation(long cents)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SetPriceAsync(1, AccountRole.GymAdmin, "admin", new SetPassPriceRequest { AmountCents = cents }, default));
        Assert.Single(await fixture.Db.GymPassPrices.ToArrayAsync());
    }

    [Theory]
    [InlineData(AccountRole.Student)]
    [InlineData(AccountRole.SuperAdmin)]
    public async Task TenantAuthority_RejectsStudentAndGlobalInBll(AccountRole role)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.PreviewAsync(1, role, "actor", 1, new PaymentPreviewRequest(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ListAsync(1, role, new PaymentListRequest(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SetPriceAsync(1, role, "actor", new SetPassPriceRequest(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.VoidAsync(1, role, "actor", 1, 1, new VoidPaymentRequest { Reason = "reason" }, default));
    }

    [Fact]
    public async Task Trainer_CannotSetPriceOrVoid_StudentCannotSelectAnotherFicha()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SetPriceAsync(1, AccountRole.Trainer, "trainer", new SetPassPriceRequest(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.VoidAsync(1, AccountRole.Trainer, "trainer", 1, 1, new VoidPaymentRequest(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.HistoryAsync(1, AccountRole.Student, "student", 1, new PaymentListRequest(), default));
        Assert.Null(await fixture.Service.OwnProfileAsync(2, AccountRole.Student, "student", default));
        Assert.Null(await fixture.Service.OwnProfileAsync(1, AccountRole.Student, "missing", default));
        Assert.Equal(1, (await fixture.Service.OwnProfileAsync(1, AccountRole.Student, "student", default))!.Id);
    }

    [Fact]
    public async Task InactiveFicha_AllowsHistoryAndAdminVoidWithoutReactivation()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var paid = await fixture.PayAsync(new DateOnly(2026, 11, 15));
        await fixture.Db.Alumnos.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.Activo, false));
        Assert.Single(await fixture.Service.HistoryAsync(1, AccountRole.Trainer, "trainer", 1, new PaymentListRequest(), default));
        Assert.True(await fixture.VoidAsync(paid.Id));
        Assert.False((await fixture.Db.Alumnos.AsNoTracking().SingleAsync(s => s.Id == 1)).Activo);
        var ledger = await fixture.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(("admin", "Correction"), (ledger.VoidedBy, ledger.VoidReason));
        Assert.Equal(fixture.Clock.Now, ledger.VoidedAt);
        var staff = Assert.Single(await fixture.Service.HistoryAsync(1, AccountRole.Trainer, "trainer", 1, new PaymentListRequest(), default));
        Assert.Equal("Correction", staff.Audit!.VoidReason);
        Assert.Null(Assert.Single(await fixture.Service.HistoryAsync(1, AccountRole.Student, "student", null, new PaymentListRequest(), default)).Audit);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tamper")]
    [InlineData("method")]
    [InlineData("business-day")]
    public async Task PreviewBinding_RejectsAlteredOrNextDayConfirmationWithoutWriting(string scenario)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var request = await fixture.ConfirmationAsync(new DateOnly(2026, 11, 15));
        if (scenario == "tamper") request.PreviewToken = "not-a-protected-preview";
        if (scenario == "method") request.Method = PaymentMethod.Card;
        if (scenario == "business-day") fixture.Clock.Now = fixture.Clock.Now.AddDays(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RegisterAsync(1, AccountRole.Trainer, scenario == "actor" ? "another-actor" : "trainer", 1, request, default));
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
        Assert.Equal(0, (await fixture.Db.Alumnos.AsNoTracking().SingleAsync(s => s.Id == 1)).PaymentRevision);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("long")]
    public async Task Void_InvalidReasonRejectsWithoutLedgerMutation(string scenario)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var payment = await fixture.PayAsync(new DateOnly(2026, 11, 15));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.VoidAsync(1, AccountRole.GymAdmin, "admin", 1, payment.Id,
            new VoidPaymentRequest { Reason = scenario == "empty" ? "" : scenario == "blank" ? " " : new string('r', 501) }, default));
        Assert.Null((await fixture.Db.Payments.AsNoTracking().SingleAsync()).VoidedAt);
        Assert.True(await fixture.Service.VoidAsync(1, AccountRole.GymAdmin, "admin", 1, payment.Id, new VoidPaymentRequest { Reason = new string('r', 500) }, default));
    }

    [Fact]
    public async Task FichaWithoutAccount_CanPayWithoutCreatingOrReactivatingIdentity()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        await fixture.Db.Alumnos.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.AccountId, (string?)null));
        await fixture.Db.Users.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsEnabled, false));
        var payment = await fixture.PayAsync(new DateOnly(2026, 11, 15));
        Assert.Equal(100099, payment.AmountCents);
        Assert.Null((await fixture.Db.Alumnos.AsNoTracking().SingleAsync(s => s.Id == 1)).AccountId);
        Assert.False((await fixture.Db.Users.AsNoTracking().SingleAsync()).IsEnabled);
        Assert.Equal(1, await fixture.Db.Users.CountAsync());
    }

    [Fact]
    public async Task Cancellation_PropagatesFromRepositoryWithoutPaymentWrites()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.PreviewAsync(1, AccountRole.Trainer, "trainer", 1,
            new PaymentPreviewRequest { PaidOn = new DateOnly(2026, 11, 15), Method = PaymentMethod.Cash }, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.ListAsync(1, AccountRole.Trainer, new PaymentListRequest(), cancellation.Token));
        Assert.Empty(await fixture.Db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData("page-zero")]
    [InlineData("size-large")]
    [InlineData("overflow")]
    [InlineData("status")]
    [InlineData("search-long")]
    public async Task Query_InvalidBoundsStatusAndSearchRejectIndependentlyOfMvc(string scenario)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var request = new PaymentListRequest
        {
            Page = scenario == "page-zero" ? 0 : scenario == "overflow" ? int.MaxValue : 1,
            PageSize = scenario == "size-large" ? 101 : 20,
            Status = scenario == "status" ? (PaymentStatus)99 : null,
            Search = scenario == "search-long" ? new string('a', 321) : null
        };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.ListAsync(1, AccountRole.Trainer, request, default));
    }

    [Theory]
    [InlineData(PaymentMethod.Cash)]
    [InlineData(PaymentMethod.Transfer)]
    [InlineData(PaymentMethod.Card)]
    public async Task ManualMethods_RecordFullExactTariffAndMethod(PaymentMethod method)
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(1, AccountRole.GymAdmin, "admin", 1,
            new PaymentPreviewRequest { PaidOn = new DateOnly(2026, 11, 15), Method = method }, default);
        var result = await fixture.Service.RegisterAsync(1, AccountRole.GymAdmin, "admin", 1,
            new RegisterPaymentRequest { PaidOn = new DateOnly(2026, 11, 15), Method = method, PreviewToken = preview.PreviewToken, IdempotencyKey = "one" }, default);
        Assert.Equal(method, result.Method);
        Assert.Equal(100099, result.AmountCents);
        Assert.Equal(new DateOnly(2026, 12, 15), result.CoverageEndExclusive);
    }

    [Fact]
    public async Task Idempotency_CommitBetweenReplayLookupAndSnapshotReturnsOriginalResult()
    {
        await using var fixture = await PaymentFixture.CreateAsync();
        var request = await fixture.ConfirmationAsync(new DateOnly(2026, 11, 15));
        var original = await fixture.Service.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, request, default);
        // Model the exact interleaving: first lookup misses, then the committed row/revision becomes visible.
        var replayService = new PaymentService(new ReplayMissOnceRepository(fixture.Repository), fixture.Clock, TestMapper.Instance, fixture.Protection);
        var replay = await replayService.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, request, default);
        Assert.Equal(original.Id, replay.Id);
        Assert.Equal(original.CoverageEndExclusive, replay.CoverageEndExclusive);
        Assert.Equal(1, await fixture.Db.Payments.CountAsync());
        Assert.Equal(1, (await fixture.Db.Alumnos.AsNoTracking().SingleAsync(s => s.Id == 1)).PaymentRevision);
    }
}

internal sealed class ReplayMissOnceRepository : IPaymentRepository
{
    public Task<PaymentSummary> SummaryAsync(int gymId, DateOnly today, DateOnly soonEnd, DateOnly nextMonth, CancellationToken cancellationToken) => inner.SummaryAsync(gymId, today, soonEnd, nextMonth, cancellationToken);
    public Task<IReadOnlyList<PaymentCollection>> CollectionsAsync(int gymId, DateOnly from, DateOnly to, CancellationToken cancellationToken) => inner.CollectionsAsync(gymId, from, to, cancellationToken);
    private readonly IPaymentRepository inner;
    private bool missed;
    public ReplayMissOnceRepository(IPaymentRepository inner) { this.inner = inner; }
    public Task<Payment?> FindReplayAsync(int gymId, string key, CancellationToken cancellationToken)
    {
        if (!missed) { missed = true; return Task.FromResult<Payment?>(null); }
        return inner.FindReplayAsync(gymId, key, cancellationToken);
    }
    public Task<PaymentSnapshot?> SnapshotAsync(int gymId, int studentId, DateOnly paidOn, CancellationToken cancellationToken) => inner.SnapshotAsync(gymId, studentId, paidOn, cancellationToken);
    public Task<Alumno?> FindOwnAsync(int gymId, string accountId, CancellationToken cancellationToken) => inner.FindOwnAsync(gymId, accountId, cancellationToken);
    public Task<Payment> RegisterAsync(Payment payment, long revision, DateOnly today, CancellationToken cancellationToken) => inner.RegisterAsync(payment, revision, today, cancellationToken);
    public Task<bool> VoidAsync(int gymId, int studentId, long paymentId, string actorId, string reason, DateTimeOffset now, CancellationToken cancellationToken) => inner.VoidAsync(gymId, studentId, paymentId, actorId, reason, now, cancellationToken);
    public Task<GymPassPrice> SetPriceAsync(GymPassPrice price, bool initial, CancellationToken cancellationToken) => inner.SetPriceAsync(price, initial, cancellationToken);
    public Task<IReadOnlyList<GymPassPrice>> PricesAsync(int gymId, CancellationToken cancellationToken) => inner.PricesAsync(gymId, cancellationToken);
    public Task<IReadOnlyList<Payment>> HistoryAsync(int gymId, int studentId, int skip, int take, CancellationToken cancellationToken) => inner.HistoryAsync(gymId, studentId, skip, take, cancellationToken);
    public Task<PaymentStudentPage> ListAsync(int gymId, string? search, PaymentStatus? status, DateOnly today, int skip, int take, CancellationToken cancellationToken) => inner.ListAsync(gymId, search, status, today, skip, take, cancellationToken);
}

internal sealed class PaymentClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class PaymentFixture : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    public StaticXDbContext Db { get; }
    public PaymentRepository Repository { get; }
    public PaymentService Service { get; }
    public PaymentClock Clock { get; }
    public IDataProtectionProvider Protection { get; }
    private PaymentFixture(SqliteConnection connection, StaticXDbContext db)
    {
        this.connection = connection; Db = db; Repository = new PaymentRepository(db); Clock = new PaymentClock();
        Protection = new EphemeralDataProtectionProvider();
        Service = new PaymentService(Repository, Clock, TestMapper.Instance, Protection);
    }

    public static async Task<PaymentFixture> CreateAsync(bool initialPrice = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Gimnasios.AddRange(new Gimnasio { Id = 1, Nombre = "First" }, new Gimnasio { Id = 2, Nombre = "Second" });
        db.Users.Add(new GymAccount { Id = "student", UserName = "student", Email = "same@example.invalid", NormalizedEmail = "SAME@EXAMPLE.INVALID", GymId = 1, Role = AccountRole.Student });
        db.Alumnos.AddRange(new Alumno { Id = 1, GimnasioId = 1, AccountId = "student", Nombre = "Ana", Apellido = "One", Email = "CONTACT@EXAMPLE.INVALID" },
            new Alumno { Id = 2, GimnasioId = 2, Nombre = "Ana", Apellido = "Foreign", Email = "SAME@EXAMPLE.INVALID" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var fixture = new PaymentFixture(connection, db);
        if (initialPrice) await fixture.Service.SetPriceAsync(1, AccountRole.GymAdmin, "admin", new SetPassPriceRequest { AmountCents = 100099, InitialEffectiveFrom = new DateOnly(2026, 11, 1) }, default);
        return fixture;
    }

    public async Task<RegisterPaymentRequest> ConfirmationAsync(DateOnly date)
    {
        var preview = await Service.PreviewAsync(1, AccountRole.Trainer, "trainer", 1, new PaymentPreviewRequest { PaidOn = date, Method = PaymentMethod.Cash }, default);
        return new RegisterPaymentRequest { PaidOn = date, Method = PaymentMethod.Cash, PreviewToken = preview.PreviewToken, IdempotencyKey = Guid.NewGuid().ToString("N") };
    }

    public async Task<PaymentResponse> PayAsync(DateOnly date) => await Service.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, await ConfirmationAsync(date), default);
    public Task<bool> VoidAsync(long id) => Service.VoidAsync(1, AccountRole.GymAdmin, "admin", 1, id, new VoidPaymentRequest { Reason = "Correction" }, default);
    public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
}
