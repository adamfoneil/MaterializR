using Demo;
using Demo.Cache;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Testing;

[TestClass]
[DoNotParallelize]
public sealed class SetCacheTests
{
    private const int Tenant = 1;
    private const int OtherTenant = 2;

    [TestMethod]
    public async Task InsertedRows_PopulatePointsCache()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var baseDate = DateOnly.FromDateTime(DateTime.Today);

        db.Appointments.AddRange(
            new Appointment { TenantId = Tenant, Date = baseDate, TransportId = 1, Points = 3, ClientId = 100, Status = AppointmentStatus.Scheduled },
            new Appointment { TenantId = Tenant, Date = baseDate, TransportId = 1, Points = 5, ClientId = 101, Status = AppointmentStatus.Completed },
            new Appointment { TenantId = Tenant, Date = baseDate, TransportId = null, Points = 4, ClientId = 102, Status = AppointmentStatus.Scheduled },
            new Appointment { TenantId = Tenant, Date = baseDate, TransportId = 1, Points = 7, ClientId = 103, Status = AppointmentStatus.Canceled });

        await db.SaveChangesAsync();

        var rollup = scope.ServiceProvider.GetRequiredService<PointsSetCache>();
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1);
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 0);
        await rollup.UpdateAsync(Tenant);

        await AssertRollupMatchesLiveAsync(db, Tenant, baseDate, baseDate);
    }

    [TestMethod]
    public async Task UpdatedRows_RefreshExistingDimension()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var baseDate = DateOnly.FromDateTime(DateTime.Today);
        var appointment = new Appointment
        {
            TenantId = Tenant,
            Date = baseDate,
            TransportId = 1,
            Points = 6,
            ClientId = 200,
            Status = AppointmentStatus.Scheduled
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var rollup = scope.ServiceProvider.GetRequiredService<PointsSetCache>();
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1);
        await rollup.UpdateAsync(Tenant);

        await db.Appointments
            .Where(a => a.Id == appointment.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Points, 9));

        await db.PointsCache
            .Where(r => r.TenantId == Tenant && r.Date == baseDate && r.TransportId == 1)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.IsStale, true));

        await rollup.UpdateAsync(Tenant);

        await AssertRollupMatchesLiveAsync(db, Tenant, baseDate, baseDate);
    }

    [TestMethod]
    public async Task DeletedRows_DecreaseScheduledPoints()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var baseDate = DateOnly.FromDateTime(DateTime.Today);

        var keep = new Appointment
        {
            TenantId = Tenant,
            Date = baseDate,
            TransportId = 1,
            Points = 5,
            ClientId = 300,
            Status = AppointmentStatus.Scheduled
        };
        var remove = new Appointment
        {
            TenantId = Tenant,
            Date = baseDate,
            TransportId = 1,
            Points = 4,
            ClientId = 301,
            Status = AppointmentStatus.Completed
        };

        db.Appointments.AddRange(keep, remove);
        await db.SaveChangesAsync();

        var rollup = scope.ServiceProvider.GetRequiredService<PointsSetCache>();
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1);
        await rollup.UpdateAsync(Tenant);

        await db.Appointments
            .Where(a => a.Id == remove.Id)
            .ExecuteDeleteAsync();

        await db.PointsCache
            .Where(r => r.TenantId == Tenant && r.Date == baseDate && r.TransportId == 1)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.IsStale, true));

        await rollup.UpdateAsync(Tenant);

        await AssertRollupMatchesLiveAsync(db, Tenant, baseDate, baseDate);
    }

    [TestMethod]
    public async Task UpdateAsync_OnlyRefreshesRequestedTenant()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var baseDate = DateOnly.FromDateTime(DateTime.Today);

        // same date and transport in both tenants
        db.Appointments.AddRange(
            new Appointment { TenantId = Tenant, Date = baseDate, TransportId = 1, Points = 3, ClientId = 100, Status = AppointmentStatus.Scheduled },
            new Appointment { TenantId = OtherTenant, Date = baseDate, TransportId = 1, Points = 8, ClientId = 101, Status = AppointmentStatus.Scheduled });
        await db.SaveChangesAsync();

        var rollup = scope.ServiceProvider.GetRequiredService<PointsSetCache>();
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1);
        await MarkStaleAsync(rollup, db, OtherTenant, baseDate, 1);

        await rollup.UpdateAsync(Tenant);

        await AssertRollupMatchesLiveAsync(db, Tenant, baseDate, baseDate);

        var other = await db.PointsCache.AsNoTracking().SingleAsync(r => r.TenantId == OtherTenant);
        Assert.IsTrue(other.IsStale);
        Assert.AreEqual(0, other.Scheduled);

        await rollup.UpdateAsync(OtherTenant);

        await AssertRollupMatchesLiveAsync(db, OtherTenant, baseDate, baseDate);
        await AssertRollupMatchesLiveAsync(db, Tenant, baseDate, baseDate);

        Assert.AreEqual(3, (await db.PointsCache.AsNoTracking().SingleAsync(r => r.TenantId == Tenant)).Scheduled);
        Assert.AreEqual(8, (await db.PointsCache.AsNoTracking().SingleAsync(r => r.TenantId == OtherTenant)).Scheduled);
    }

    [TestMethod]
    public async Task MarkStaleDimensions_DoesNotTouchOtherTenantRows()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var baseDate = DateOnly.FromDateTime(DateTime.Today);
        db.Appointments.AddRange(
            new Appointment { TenantId = Tenant, Date = baseDate, TransportId = 1, Points = 3, ClientId = 100, Status = AppointmentStatus.Scheduled },
            new Appointment { TenantId = OtherTenant, Date = baseDate, TransportId = 1, Points = 8, ClientId = 101, Status = AppointmentStatus.Scheduled });
        await db.SaveChangesAsync();

        var rollup = scope.ServiceProvider.GetRequiredService<PointsSetCache>();
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1);
        await MarkStaleAsync(rollup, db, OtherTenant, baseDate, 1);
        await rollup.UpdateAsync(Tenant);
        await rollup.UpdateAsync(OtherTenant);

        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1);

        var rows = await db.PointsCache.AsNoTracking().ToArrayAsync();
        Assert.IsTrue(rows.Single(r => r.TenantId == Tenant).IsStale);
        Assert.IsFalse(rows.Single(r => r.TenantId == OtherTenant).IsStale);
    }

    [TestMethod]
    public async Task UpdateAsync_RemovesOrphansAndIgnoresUnrequestedLiveDimensions()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var date = new DateOnly(2026, 1, 5);
        db.Appointments.AddRange(
            new Appointment { TenantId = Tenant, Date = date, TransportId = 1, Points = 3, ClientId = 1, Status = AppointmentStatus.Scheduled },
            new Appointment { TenantId = Tenant, Date = date, TransportId = 2, Points = 8, ClientId = 2, Status = AppointmentStatus.Scheduled });
        db.PointsCache.AddRange(
            new PointsCache { TenantId = Tenant, Date = date, TransportId = 1, Scheduled = 1, IsStale = true },
            new PointsCache { TenantId = Tenant, Date = date, TransportId = 2, Scheduled = 99, IsStale = false },
            new PointsCache { TenantId = Tenant, Date = date, TransportId = 3, Scheduled = 12, IsStale = true },
            new PointsCache { TenantId = OtherTenant, Date = date, TransportId = 3, Scheduled = 14, IsStale = true });
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<PointsSetCache>();
        Assert.AreEqual(2, await cache.UpdateAsync(Tenant));

        var rows = await db.PointsCache.AsNoTracking().ToArrayAsync();
        Assert.AreEqual(3, rows.Single(row => row.TenantId == Tenant && row.TransportId == 1).Scheduled);
        Assert.IsFalse(rows.Single(row => row.TenantId == Tenant && row.TransportId == 1).IsStale);
        Assert.AreEqual(99, rows.Single(row => row.TenantId == Tenant && row.TransportId == 2).Scheduled);
        Assert.IsFalse(rows.Any(row => row.TenantId == Tenant && row.TransportId == 3));
        Assert.IsTrue(rows.Single(row => row.TenantId == OtherTenant).IsStale);
    }

    [TestMethod]
    public async Task UpdateAsync_RefreshesWithNoTrackingAndDisabledChangeDetection()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var date = new DateOnly(2026, 1, 5);
        db.Appointments.Add(new Appointment { TenantId = Tenant, Date = date, TransportId = 1, Points = 7, ClientId = 1, Status = AppointmentStatus.Scheduled });
        db.PointsCache.Add(new PointsCache { TenantId = Tenant, Date = date, TransportId = 1, Scheduled = 2, IsStale = true });
        await db.SaveChangesAsync();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<PointsSetCache>>();
        var cache = new PointsSetCache(logger, new NoTrackingFactory(factory));

        Assert.AreEqual(1, await cache.UpdateAsync(Tenant));

        var row = await db.PointsCache.AsNoTracking().SingleAsync();
        Assert.AreEqual(7, row.Scheduled);
        Assert.IsFalse(row.IsStale);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpdateAsync_UsesSuppliedContextAndRespectsTransaction(bool existingTransaction)
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var date = new DateOnly(2026, 1, 5);
        db.Appointments.Add(new Appointment { TenantId = Tenant, Date = date, TransportId = 1, Points = 7, ClientId = 1, Status = AppointmentStatus.Scheduled });
        await db.SaveChangesAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<PointsSetCache>>();
        var cache = new PointsSetCache(logger, new ThrowingContextFactory());
        await MarkStaleAsync(cache, db, Tenant, date, 1);

        await using var transaction = existingTransaction ? await db.Database.BeginTransactionAsync() : null;
        await db.Appointments.Where(a => a.TenantId == Tenant).ExecuteUpdateAsync(setters => setters.SetProperty(a => a.Points, 12));
        Assert.AreEqual(1, await cache.UpdateAsync(db, Tenant));
        Assert.AreEqual(0, await cache.UpdateAsync(db, Tenant));
        Assert.AreSame(transaction, db.Database.CurrentTransaction);
        Assert.AreEqual(12, (await db.PointsCache.AsNoTracking().SingleAsync()).Scheduled);

        if (transaction is not null)
        {
            await transaction.RollbackAsync();
            var row = await db.PointsCache.AsNoTracking().SingleAsync();
            Assert.IsTrue(row.IsStale);
            Assert.AreEqual(0, row.Scheduled);
        }
        else
        {
            Assert.IsFalse((await db.PointsCache.AsNoTracking().SingleAsync()).IsStale);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpdateAsync_FailureRollsBackOnlyOwnedTransaction(bool existingTransaction)
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var date = new DateOnly(2026, 1, 5);
        db.Appointments.Add(new Appointment { TenantId = Tenant, Date = date, TransportId = 1, Points = 7, ClientId = 1, Status = AppointmentStatus.Scheduled });
        await db.SaveChangesAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<PointsSetCache>>();
        var cache = new FailingPointsSetCache(logger, new ThrowingContextFactory());
        await MarkStaleAsync(cache, db, Tenant, date, 1);

        await using var transaction = existingTransaction ? await db.Database.BeginTransactionAsync() : null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cache.UpdateAsync(db, Tenant));
        Assert.AreSame(transaction, db.Database.CurrentTransaction);
        var row = await db.PointsCache.AsNoTracking().SingleAsync();
        Assert.AreEqual(existingTransaction ? 7 : 0, row.Scheduled);
        Assert.AreEqual(!existingTransaction, row.IsStale);
        if (transaction is not null) await transaction.RollbackAsync();
        Assert.IsTrue((await db.PointsCache.AsNoTracking().SingleAsync()).IsStale);
    }

    private sealed class ThrowingContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => throw new InvalidOperationException("Unexpected context creation");
    }

    private sealed class FailingPointsSetCache(ILogger<PointsSetCache> logger, IDbContextFactory<ApplicationDbContext> factory)
        : PointsSetCache(logger, factory)
    {
        protected override async Task UpsertFactsAsync(ApplicationDbContext db, int tenantId, PointsCache[] rows, CancellationToken cancellationToken)
        {
            await base.UpsertFactsAsync(db, tenantId, rows, cancellationToken);
            throw new InvalidOperationException("Refresh failed");
        }
    }

    private sealed class NoTrackingFactory(IDbContextFactory<ApplicationDbContext> inner) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            var db = inner.CreateDbContext();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            return db;
        }
    }

    private static Task MarkStaleAsync(PointsSetCache rollup, ApplicationDbContext db, int tenantId, DateOnly date, int transportId) =>
        rollup.MarkStaleDimensionsAsync(db, tenantId, [new PointsCache.Dimensions(date, transportId)]);

    private static async Task<ApplicationDbContext> InitDbAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        await SetScheduleLimitsAsync(db, Tenant);
        await SetScheduleLimitsAsync(db, OtherTenant);

        return db;
    }

    private static async Task SetScheduleLimitsAsync(ApplicationDbContext db, int tenantId)
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            db.DailyLimitDefaults.Add(new DailyLimitDefault
            {
                TenantId = tenantId,
                DayOfWeek = day,
                MaxPoints = 100
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task AssertRollupMatchesLiveAsync(ApplicationDbContext db, int tenantId, DateOnly from, DateOnly to)
    {
        var expected = (await PointsSetCache.QueryLiveImplementationAsync(db, tenantId, from, to))
            .OrderBy(r => r.Date)
            .ThenBy(r => r.TransportId)
            .ToArray();

        var actual = await db.PointsCache.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Date >= from && r.Date <= to)
            .OrderBy(r => r.Date)
            .ThenBy(r => r.TransportId)
            .ToArrayAsync();

        Assert.HasCount(expected.Length, actual);

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].Date, actual[i].Date);
            Assert.AreEqual(expected[i].TransportId, actual[i].TransportId);
            Assert.AreEqual(expected[i].Maximum, actual[i].Maximum);
            Assert.AreEqual(expected[i].Scheduled, actual[i].Scheduled);
            Assert.IsFalse(actual[i].IsStale);
        }
    }
}
