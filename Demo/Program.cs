using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Demo;

public class Program
{
    public static async Task Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ApplicationDbContext>(ApplicationDbContextFactory.Configure, ServiceLifetime.Singleton);
        services.AddSingleton<PointsRollupConfiguration>();
        var sp = services.BuildServiceProvider();

        // create sample appts
        using (var scope = sp.CreateScope())
        {
            var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();            
            using var dbContext = dbContextFactory.CreateDbContext();
            
            dbContext.Database.EnsureCreated();

            dbContext.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 3, ClientId = Random.Shared.Next(100,500), TransportId = 1 });
            dbContext.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 4, ClientId = Random.Shared.Next(100, 500), TransportId = 1 });
            dbContext.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 2, ClientId = Random.Shared.Next(100, 500), TransportId = 1 });
            dbContext.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 4, ClientId = Random.Shared.Next(100, 500) });
            dbContext.Appointments.Add(new Appointment { Date = DateOnly.FromDateTime(DateTime.Today), Points = 5, ClientId = Random.Shared.Next(100, 500) });

            await dbContext.SaveChangesAsync();
        }
    }
}
