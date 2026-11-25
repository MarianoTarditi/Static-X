using BLL;
using BLL.DTOs.Payments;
using DAL;
using DAL.Repositories.Implementation;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Static_X.Tests;

public sealed class PaymentConcurrencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentConfirmations_SeparateConnectionsProduceOneLedgerEffect(bool sameKey)
    {
        var path = Path.Combine(Path.GetTempPath(), "static-x-payment-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10, Pooling = false }.ToString()).Options;
        var protection = new EphemeralDataProtectionProvider(); var clock = new PaymentClock();
        try
        {
            RegisterPaymentRequest confirmation;
            await using (var db = new StaticXDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Gimnasios.Add(new Gimnasio { Id = 1, Nombre = "Concurrent test" });
                db.Alumnos.Add(new Alumno { Id = 1, GimnasioId = 1, Nombre = "Ana", Apellido = "One", Email = "ANA@EXAMPLE.INVALID" });
                await db.SaveChangesAsync();
                var service = new PaymentService(new PaymentRepository(db), clock, TestMapper.Instance, protection);
                await service.SetPriceAsync(1, AccountRole.GymAdmin, "admin", new SetPassPriceRequest { AmountCents = 100099, InitialEffectiveFrom = new DateOnly(2026, 11, 1) }, default);
                var preview = await service.PreviewAsync(1, AccountRole.Trainer, "trainer", 1, new PaymentPreviewRequest { PaidOn = new DateOnly(2026, 11, 15), Method = PaymentMethod.Cash }, default);
                confirmation = new RegisterPaymentRequest { PaidOn = new DateOnly(2026, 11, 15), Method = PaymentMethod.Cash, PreviewToken = preview.PreviewToken, IdempotencyKey = "same" };
            }
            using var barrier = new Barrier(2);
            var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
            {
                await using var db = new StaticXDbContext(options);
                var service = new PaymentService(new PaymentRepository(db), clock, TestMapper.Instance, protection);
                var request = new RegisterPaymentRequest { PaidOn = confirmation.PaidOn, Method = confirmation.Method, PreviewToken = confirmation.PreviewToken, IdempotencyKey = sameKey ? "same" : "key-" + index };
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "Both independent requests must reach the start barrier.");
                try { return (Id: (long?)(await service.RegisterAsync(1, AccountRole.Trainer, "trainer", 1, request, default)).Id, Error: (Exception?)null); }
                catch (Exception exception) { return (Id: (long?)null, Error: exception); }
            })).ToArray();
            var results = await Task.WhenAll(tasks);
            if (sameKey)
            {
                Assert.All(results, result => Assert.Null(result.Error));
                Assert.Equal(results[0].Id, results[1].Id);
            }
            else
            {
                Assert.Single(results, result => result.Id.HasValue);
                Assert.IsType<InvalidOperationException>(Assert.Single(results, result => result.Error is not null).Error);
            }
            await using var verification = new StaticXDbContext(options);
            Assert.Equal(1, await verification.Payments.CountAsync());
            Assert.Equal(1, (await verification.Alumnos.SingleAsync()).PaymentRevision);
            Assert.Equal(new DateOnly(2026, 12, 15), (await verification.Payments.SingleAsync()).CoverageEndExclusive);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
