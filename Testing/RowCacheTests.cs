using Demo;
using Demo.Cache;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Testing;

[TestClass]
[DoNotParallelize]
public sealed class RowCacheTests
{
    private const int Tenant = 1;
    private const int OtherTenant = 2;

    [TestMethod]
    public async Task InsertedRows_PopulateAppointmentPriceCache()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = new Appointment
        {
            TenantId = Tenant,
            Date = DateOnly.FromDateTime(DateTime.Today),
            Time = new TimeOnly(9, 0),
            TransportId = 1,
            Points = 3,
            ClientId = 100,
            Status = AppointmentStatus.Scheduled,
            Items =
            [
                new AppointmentItem { ItemId = 1, Quantity = 2, UnitPrice = 10m },
                new AppointmentItem { ItemId = 2, Quantity = 1, UnitPrice = 7.5m }
            ]
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [(true, appointment.Id)]);
        await cache.UpdateAsync(Tenant);

        await AssertCacheMatchesLiveAsync(db, Tenant);
    }

    [TestMethod]
    public async Task UpdatedRows_RefreshExistingAppointmentPrice()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = new Appointment
        {
            TenantId = Tenant,
            Date = DateOnly.FromDateTime(DateTime.Today),
            Time = new TimeOnly(10, 30),
            TransportId = 2,
            Points = 4,
            ClientId = 200,
            Status = AppointmentStatus.Scheduled,
            Items =
            [
                new AppointmentItem { ItemId = 5, Quantity = 1, UnitPrice = 12m }
            ]
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [(true, appointment.Id)]);
        await cache.UpdateAsync(Tenant);

        await db.AppointmentItems
            .Where(i => i.AppointmentId == appointment.Id && i.ItemId == 5)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.Quantity, 3)
                .SetProperty(i => i.UnitPrice, 15m));

        await db.AppointmentPrices
            .Where(p => p.AppointmentId == appointment.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.IsStale, true));

        await cache.UpdateAsync(Tenant);

        await AssertCacheMatchesLiveAsync(db, Tenant);
    }

    [TestMethod]
    public async Task DeletedRows_RefreshExistingAppointmentPrice()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = new Appointment
        {
            TenantId = Tenant,
            Date = DateOnly.FromDateTime(DateTime.Today),
            Time = new TimeOnly(8, 0),
            TransportId = 3,
            Points = 2,
            ClientId = 300,
            Status = AppointmentStatus.Scheduled,
            Items =
            [
                new AppointmentItem { ItemId = 10, Quantity = 1, UnitPrice = 20m },
                new AppointmentItem { ItemId = 12, Quantity = 3, UnitPrice = 5m }
            ]
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [(true, appointment.Id)]);
        await cache.UpdateAsync(Tenant);

        await db.AppointmentItems
            .Where(i => i.AppointmentId == appointment.Id && i.ItemId == 12)
            .ExecuteDeleteAsync();

        await cache.MarkStaleKeysAsync(db, Tenant, [(false, appointment.Id)]);

        await cache.UpdateAsync(Tenant);

        await AssertCacheMatchesLiveAsync(db, Tenant);
    }

    [TestMethod]
    public async Task UpdateAsync_OnlyRefreshesRequestedTenant()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var mine = NewAppointment(Tenant, 10m);
        var theirs = NewAppointment(OtherTenant, 99m);
        db.Appointments.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [(true, mine.Id)]);
        await cache.MarkStaleKeysAsync(db, OtherTenant, [(true, theirs.Id)]);

        await cache.UpdateAsync(Tenant);

        await AssertCacheMatchesLiveAsync(db, Tenant);

        var other = await db.AppointmentPrices.AsNoTracking().SingleAsync(p => p.TenantId == OtherTenant);
        Assert.IsTrue(other.IsStale);
        Assert.AreEqual(0m, other.TotalPrice);

        await cache.UpdateAsync(OtherTenant);

        await AssertCacheMatchesLiveAsync(db, OtherTenant);
        await AssertCacheMatchesLiveAsync(db, Tenant);
    }

    [TestMethod]
    public async Task MarkStaleKeys_DoesNotTouchOtherTenantRows()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var mine = NewAppointment(Tenant, 10m);
        var theirs = NewAppointment(OtherTenant, 99m);
        db.Appointments.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [(true, mine.Id)]);
        await cache.MarkStaleKeysAsync(db, OtherTenant, [(true, theirs.Id)]);
        await cache.UpdateAsync(Tenant);
        await cache.UpdateAsync(OtherTenant);

        await cache.MarkStaleKeysAsync(db, Tenant, [(false, mine.Id), (false, theirs.Id)]);

        var rows = await db.AppointmentPrices.AsNoTracking().ToArrayAsync();
        Assert.IsTrue(rows.Single(r => r.TenantId == Tenant).IsStale);
        Assert.IsFalse(rows.Single(r => r.TenantId == OtherTenant).IsStale);
    }

    [TestMethod]
    public async Task MarkAndUpdateWhere_UpsertsOnlyMatchingRowsForTenant()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var existing = NewAppointment(Tenant, 10m);
        var fresh = NewAppointment(Tenant, 20m);
        var outOfWindow = NewAppointment(Tenant, 30m);
        outOfWindow.Date = today.AddDays(10);
        var theirs = NewAppointment(OtherTenant, 99m);
        db.Appointments.AddRange(existing, fresh, outOfWindow, theirs);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [(true, existing.Id)]);
        await cache.UpdateAsync(Tenant);

        var marked = await cache.MarkStaleWhereAsync(db, Tenant,
            db.Appointments.Where(a => a.TenantId == Tenant && a.Date == today), a => a.Id);
        Assert.AreEqual(2, marked);

        var rows = await db.AppointmentPrices.AsNoTracking().Where(p => p.TenantId == Tenant).ToArrayAsync();
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows.All(r => r.IsStale));
        Assert.AreEqual(0m, rows.Single(r => r.AppointmentId == fresh.Id).TotalPrice);

        await cache.MarkAndUpdateWhereAsync(db, Tenant,
            db.Appointments.Where(a => a.TenantId == Tenant && a.Date == today), a => a.Id);

        var after = await db.AppointmentPrices.AsNoTracking().Where(p => p.TenantId == Tenant).ToArrayAsync();
        Assert.HasCount(2, after);
        Assert.IsTrue(after.All(r => !r.IsStale));
        Assert.AreEqual(40m, after.Single(r => r.AppointmentId == fresh.Id).TotalPrice);
        Assert.IsFalse(after.Any(r => r.AppointmentId == outOfWindow.Id));
        Assert.IsFalse(await db.AppointmentPrices.AnyAsync(p => p.TenantId == OtherTenant));
    }

    [TestMethod]
    public async Task GetAsync_RefreshesStaleAppointmentPrice()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [(true, appointment.Id)]);

        var result = await cache.GetAsync(Tenant, appointment.Id);

        Assert.IsNotNull(result);
        Assert.AreEqual(20m, result.TotalPrice);
        Assert.IsFalse(result.IsStale);

        var persisted = await db.AppointmentPrices.AsNoTracking().SingleAsync(p => p.TenantId == Tenant);
        Assert.AreEqual(20m, persisted.TotalPrice);
        Assert.IsFalse(persisted.IsStale);
    }

    private static Appointment NewAppointment(int tenantId, decimal unitPrice) => new()
    {
        TenantId = tenantId,
        Date = DateOnly.FromDateTime(DateTime.Today),
        Time = new TimeOnly(9, 0),
        TransportId = 1,
        Points = 3,
        ClientId = 100,
        Status = AppointmentStatus.Scheduled,
        Items = [new AppointmentItem { ItemId = 1, Quantity = 2, UnitPrice = unitPrice }]
    };

    private static async Task<ApplicationDbContext> InitDbAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task AssertCacheMatchesLiveAsync(ApplicationDbContext db, int tenantId)
    {
        var appointmentIds = await db.Appointments
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => a.Id)
            .OrderBy(id => id)
            .ToArrayAsync();

        var totalsByAppointment = (await db.AppointmentItems
            .AsNoTracking()
            .Where(i => i.Appointment!.TenantId == tenantId)
            .GroupBy(i => i.AppointmentId)
            .Select(g => new
            {
                AppointmentId = g.Key,
                TotalPrice = g.Sum(i => i.UnitPrice * i.Quantity)
            })
            .ToArrayAsync())
            .ToDictionary(i => i.AppointmentId, i => i.TotalPrice);

        var expected = appointmentIds
            .Select(id => new
            {
                AppointmentId = id,
                TotalPrice = totalsByAppointment.GetValueOrDefault(id, 0m)
            })
            .ToArray();

        var actual = await db.AppointmentPrices
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.AppointmentId)
            .ToArrayAsync();

        Assert.HasCount(expected.Length, actual);

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].AppointmentId, actual[i].AppointmentId);
            Assert.AreEqual(expected[i].TotalPrice, actual[i].TotalPrice);
            Assert.IsFalse(actual[i].IsStale);
        }
    }

}
