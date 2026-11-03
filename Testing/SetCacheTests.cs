using Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Testing;

[TestClass]
[DoNotParallelize]
public sealed class SetCacheTests
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
    public async Task InsertedRows_PopulatePointsCache()
    {
        using var scope = Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var baseDate = DateOnly.FromDateTime(DateTime.Today);

        db.Appointments.AddRange(
            new Appointment { Date = baseDate, TransportId = 1, Points = 3, ClientId = 100, Status = AppointmentStatus.Scheduled },
            new Appointment { Date = baseDate, TransportId = 1, Points = 5, ClientId = 101, Status = AppointmentStatus.Completed },
            new Appointment { Date = baseDate, TransportId = null, Points = 4, ClientId = 102, Status = AppointmentStatus.Scheduled },
            new Appointment { Date = baseDate, TransportId = 1, Points = 7, ClientId = 103, Status = AppointmentStatus.Canceled });

        await db.SaveChangesAsync();

        var rollup = scope.ServiceProvider.GetRequiredService<PointsCacheConfiguration>();
        await rollup.UpdateAsync();

        await AssertRollupMatchesLiveAsync(db, baseDate, baseDate);
    }

    [TestMethod]
    public async Task UpdatedRows_RefreshExistingDimension()
    {
        using var scope = Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var baseDate = DateOnly.FromDateTime(DateTime.Today);
        var appointment = new Appointment
        {
            Date = baseDate,
            TransportId = 1,
            Points = 6,
            ClientId = 200,
            Status = AppointmentStatus.Scheduled
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var rollup = scope.ServiceProvider.GetRequiredService<PointsCacheConfiguration>();
        await rollup.UpdateAsync();

        await db.Appointments
            .Where(a => a.Id == appointment.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(a => a.Points, 9));

        await db.PointsCache
            .Where(r => r.Date == baseDate && r.TransportId == 1)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.IsStale, true));

        await rollup.UpdateAsync();

        await AssertRollupMatchesLiveAsync(db, baseDate, baseDate);
    }

    [TestMethod]
    public async Task DeletedRows_DecreaseScheduledPoints()
    {
        using var scope = Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var baseDate = DateOnly.FromDateTime(DateTime.Today);

        var keep = new Appointment
        {
            Date = baseDate,
            TransportId = 1,
            Points = 5,
            ClientId = 300,
            Status = AppointmentStatus.Scheduled
        };
        var remove = new Appointment
        {
            Date = baseDate,
            TransportId = 1,
            Points = 4,
            ClientId = 301,
            Status = AppointmentStatus.Completed
        };

        db.Appointments.AddRange(keep, remove);
        await db.SaveChangesAsync();

        var rollup = scope.ServiceProvider.GetRequiredService<PointsCacheConfiguration>();
        await rollup.UpdateAsync();

        await db.Appointments
            .Where(a => a.Id == remove.Id)
            .ExecuteDeleteAsync();

        await db.PointsCache
            .Where(r => r.Date == baseDate && r.TransportId == 1)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.IsStale, true));

        await rollup.UpdateAsync();

        await AssertRollupMatchesLiveAsync(db, baseDate, baseDate);
    }

    private static async Task<ApplicationDbContext> InitDbAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        await SetScheduleLimitsAsync(db);

        return db;
    }

    private static async Task SetScheduleLimitsAsync(ApplicationDbContext db)
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            db.DailyLimitDefaults.Add(new DailyLimitDefault
            {
                DayOfWeek = day,
                MaxPoints = 100
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task AssertRollupMatchesLiveAsync(ApplicationDbContext db, DateOnly from, DateOnly to)
    {
        var expected = (await PointsCacheConfiguration.QueryLiveImplementationAsync(db, from, to))
            .OrderBy(r => r.Date)
            .ThenBy(r => r.TransportId)
            .ToArray();

        var actual = await db.PointsCache.AsNoTracking()
            .Where(r => r.Date >= from && r.Date <= to)
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
