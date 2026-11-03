using Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Testing;

[TestClass]
public sealed class Core
{
    private static IServiceProvider Services
    {
        get
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<ApplicationDbContext>(ApplicationDbContextFactory.Configure, ServiceLifetime.Singleton);
            services.AddSingleton<PointsCacheConfiguration>();
            services.AddLogging();
            return services.BuildServiceProvider();
        }        
    }

    private static async Task<ApplicationDbContext> InitDbAsync(IServiceScope scope)
    {        
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

        db.Database.EnsureCreated();

        await db.DailyLimitDefaults.ExecuteDeleteAsync();
        await db.PointsCache.ExecuteDeleteAsync();
        await db.Appointments.ExecuteDeleteAsync();

        await SetScheduleLimitsAsync(db);

        return db;
    }

    [TestMethod]
    public async Task Inserts()
    {
        using var scope = Services.CreateScope();
        using var db = await InitDbAsync(scope);
        
        var (appts, from, to) = await CreateSampleAppointments(db, 25, 7);

        // assert that we have dimensions for each day in the range

        var rollupData = await PointsCacheConfiguration.QueryLiveImplementationAsync(db, from, to);

        var rollup = Services.GetRequiredService<PointsCacheConfiguration>();
        // all inserted
        await rollup.UpdateAsync();

        var rollupResults = await db.PointsCache.AsNoTracking().ToArrayAsync();

        // assert that the rollup results match the live query results
    }

    [TestMethod]
    public async Task Updates()
    {
        using var scope = Services.CreateScope();
        using var db = await InitDbAsync(scope);

        var (appts, from, to) = await CreateSampleAppointments(db, 10, 1);

        appts[0].Date = appts[0].Date.AddDays(1);
        db.Appointments.Update(appts[0]);
        await db.SaveChangesAsync();


    }

    private static async Task<(Appointment[], DateOnly From, DateOnly To)> CreateSampleAppointments(ApplicationDbContext db, int apptsPerDay, int daysAhead)
    {
        List<Appointment> appointments = [];
        for (int day = 0; day < daysAhead; day++)
        {
            var date = DateOnly.FromDateTime(DateTime.Today.AddDays(day));

            for (int appt = 0; appt < apptsPerDay; appt++)
            {                
                int? transportId = Random.Shared.NextDouble() < 0.4
                    ? null
                    : (day % 2) + 1;
                var points = Random.Shared.Next(3, 7);
                appointments.Add(new Appointment
                {
                    Date = date,
                    Points = points,
                    ClientId = Random.Shared.Next(100, 500),
                    TransportId = transportId,
                    Status = AppointmentStatus.Scheduled
                });
                db.Appointments.AddRange(appointments);
            }
        }
        await db.SaveChangesAsync();

        return (appointments.ToArray(), DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today.AddDays(daysAhead)));
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
}
