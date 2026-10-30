using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Demo;

public class Program
{
    public static void Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ApplicationDbContext>(ApplicationDbContextFactory.Configure);
        services.AddScoped<PointsRollupConfiguration>();

        // create sample appts
    }


}
