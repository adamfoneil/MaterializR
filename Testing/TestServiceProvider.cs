using Demo;
using Demo.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Testing;

internal static class TestServiceProvider
{
    private static readonly Lazy<IServiceProvider> ServicesLazy = new(CreateServices);

    internal static IServiceProvider Services => ServicesLazy.Value;

    private static IServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ApplicationDbContext>(ApplicationDbContextFactory.Configure, ServiceLifetime.Singleton);
        services.AddSingleton<PointsCacheConfiguration>();
        services.AddSingleton<AppointmentPriceCacheConfiguration>();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
            logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
        });
        return services.BuildServiceProvider();
    }
}