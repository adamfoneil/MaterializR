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
            services.AddSingleton<PointsRollupConfiguration>();
            services.AddLogging();
            return services.BuildServiceProvider();
        }        
    }

    [TestMethod]
    public async Task SimpleCase()
    {
        using var scope = Services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

        db.Database.EnsureCreated();

        await db.DailyLimitDefaults.ExecuteDeleteAsync();
        await db.PointsRollup.ExecuteDeleteAsync();
        await db.Appointments.ExecuteDeleteAsync();

        await SetScheduleLimitsAsync(db);
        var (appts, from, to) = await CreateSampleAppointments(db, 25, 7);

        // assert that we have dimensions for each day in the range

        var rollupData = await PointsRollupConfiguration.QueryLiveImplementationAsync(db, from, to);

        var rollup = scope.ServiceProvider.GetRequiredService<PointsRollupConfiguration>();
        // all inserted
        await rollup.UpdateAsync();

        var rollupResults = await db.PointsRollup.AsNoTracking().ToArrayAsync();

        // assert that the rollup results match the live query results

        // todo: update a few random appointments (change the dates, cancel a few), delete a few, then run the rollup again and verify the results

    }

    private static async Task<(Appointment[], DateOnly From, DateOnly To)> CreateSampleAppointments(ApplicationDbContext db, int apptsPerDay, int daysAhead)
    {
        List<Appointment> appointments = [];
        for (int i = 0; i < daysAhead; i++)
        {
            var date = DateOnly.FromDateTime(DateTime.Today.AddDays(i));

            for (int j = 0; j < apptsPerDay; j++)
            {                
                int? transportId = Random.Shared.NextDouble() < 0.4
                    ? null
                    : (i % 2) + 1;
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
