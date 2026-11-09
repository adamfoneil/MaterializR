using Demo;
using Demo.Cache;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1, true);
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 0, true);
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
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1, true);
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
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1, true);
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
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1, true);
        await MarkStaleAsync(rollup, db, OtherTenant, baseDate, 1, true);

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
        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1, true);
        await MarkStaleAsync(rollup, db, OtherTenant, baseDate, 1, true);
        await rollup.UpdateAsync(Tenant);
        await rollup.UpdateAsync(OtherTenant);

        await MarkStaleAsync(rollup, db, Tenant, baseDate, 1, false);

        var rows = await db.PointsCache.AsNoTracking().ToArrayAsync();
        Assert.IsTrue(rows.Single(r => r.TenantId == Tenant).IsStale);
        Assert.IsFalse(rows.Single(r => r.TenantId == OtherTenant).IsStale);
    }

    private static Task MarkStaleAsync(PointsSetCache rollup, ApplicationDbContext db, int tenantId, DateOnly date, int transportId, bool inserted) =>
        rollup.MarkStaleDimensionsAsync(db, tenantId, [(inserted, new PointsCache.Dimensions(date, transportId))]);

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
