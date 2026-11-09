using Demo.Cache;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Demo;

public class Program
{
    public static async Task Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ApplicationDbContext>(ApplicationDbContextFactory.Configure, ServiceLifetime.Singleton);
        services.AddSingleton<PointsSetCache>();
        var sp = services.BuildServiceProvider();

        using var scope = sp.CreateScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        using var db = dbContextFactory.CreateDbContext();
        db.Database.EnsureCreated();

        // set default schedule
        //await SetScheduleLimitsAsync(db);

        // create sample appts
        /*
               
        db.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 3, ClientId = Random.Shared.Next(100, 500), TransportId = 1 });
        db.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 4, ClientId = Random.Shared.Next(100, 500), TransportId = 1 });
        db.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 2, ClientId = Random.Shared.Next(100, 500), TransportId = 1 });
        db.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 4, ClientId = Random.Shared.Next(100, 500) });
        db.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 5, ClientId = Random.Shared.Next(100, 500) });

        await db.SaveChangesAsync();
        */

        const int tenantId = 1;
        var rollup = sp.GetRequiredService<PointsSetCache>();
        await rollup.UpdateAsync(tenantId);

        var dims = await db.PointsCache.Where(p => p.TenantId == tenantId).ToArrayAsync();
        foreach (var d in dims)
        {
            Console.WriteLine($"Date: {d.Date}, TransportId: {d.TransportId}, Maximum: {d.Maximum}, Scheduled: {d.Scheduled}, Available: {d.Available}, PercentFree: {d.PercentFree}");
        }
    }

    private static async Task SetScheduleLimitsAsync(ApplicationDbContext db)
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            db.DailyLimitDefaults.Add(new DailyLimitDefault
            {
                TenantId = 1,
                DayOfWeek = day,
                MaxPoints = 100
            });
        }
        await db.SaveChangesAsync();
    }
}
