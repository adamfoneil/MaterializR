using Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Testing;

[TestClass]
[DoNotParallelize]
public sealed class RowCacheTests
{
    private static IServiceProvider Services
    {
        get
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<ApplicationDbContext>(ApplicationDbContextFactory.Configure, ServiceLifetime.Singleton);
            services.AddSingleton<PointsCacheConfiguration>();
            services.AddSingleton<AppointmentPriceCacheConfiguration>();
            services.AddLogging();
            return services.BuildServiceProvider();
        }
    }

    [TestMethod]
    public async Task InsertedRows_PopulateAppointmentPriceCache()
    {
        using var scope = Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = new Appointment
        {
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

        await MarkStaleAppointmentsAsync(db, appointment.Id);

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceCacheConfiguration>();
        await cache.UpdateAsync();

        await AssertCacheMatchesLiveAsync(db);
    }

    [TestMethod]
    public async Task UpdatedRows_RefreshExistingAppointmentPrice()
    {
        using var scope = Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = new Appointment
        {
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

        await MarkStaleAppointmentsAsync(db, appointment.Id);

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceCacheConfiguration>();
        await cache.UpdateAsync();

        await db.AppointmentItems
            .Where(i => i.AppointmentId == appointment.Id && i.ItemId == 5)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.Quantity, 3)
                .SetProperty(i => i.UnitPrice, 15m));

        await db.AppointmentPrices
            .Where(p => p.AppointmentId == appointment.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.IsStale, true));

        await cache.UpdateAsync();

        await AssertCacheMatchesLiveAsync(db);
    }

    [TestMethod]
    public async Task DeletedRows_RefreshExistingAppointmentPrice()
    {
        using var scope = Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = new Appointment
        {
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

        await MarkStaleAppointmentsAsync(db, appointment.Id);

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceCacheConfiguration>();
        await cache.UpdateAsync();

        await db.AppointmentItems
            .Where(i => i.AppointmentId == appointment.Id && i.ItemId == 12)
            .ExecuteDeleteAsync();

        await MarkStaleAppointmentsAsync(db, appointment.Id);

        await cache.UpdateAsync();

        await AssertCacheMatchesLiveAsync(db);
    }

    private static async Task<ApplicationDbContext> InitDbAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task AssertCacheMatchesLiveAsync(ApplicationDbContext db)
    {
        var appointmentIds = await db.Appointments
            .AsNoTracking()
            .Select(a => a.Id)
            .OrderBy(id => id)
            .ToArrayAsync();

        var totalsByAppointment = (await db.AppointmentItems
            .AsNoTracking()
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

    private static async Task MarkStaleAppointmentsAsync(ApplicationDbContext db, params int[] appointmentIds)
    {
        if (appointmentIds.Length == 0)
        {
            return;
        }

        var ids = appointmentIds.Distinct().ToArray();

        var existingIds = await db.AppointmentPrices
            .Where(p => ids.Contains(p.AppointmentId))
            .Select(p => p.AppointmentId)
            .ToArrayAsync();

        var missingIds = ids.Except(existingIds).ToArray();
        if (missingIds.Length > 0)
        {
            db.AppointmentPrices.AddRange(missingIds.Select(id => new AppointmentPriceCache
            {
                AppointmentId = id,
                IsStale = true
            }));

            await db.SaveChangesAsync();
        }

        if (existingIds.Length > 0)
        {
            await db.AppointmentPrices
                .Where(p => existingIds.Contains(p.AppointmentId))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.IsStale, true));
        }
    }
}
