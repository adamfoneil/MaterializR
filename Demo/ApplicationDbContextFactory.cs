using Demo.Cache;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;

namespace Demo;

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>, IDbContextFactory<ApplicationDbContext>
{
    public static string ConnectionString
    {
        get
        {
            var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
            Directory.CreateDirectory(dataDirectory);
            var dbPath = Path.Combine(dataDirectory, "demo.db");
            return $"Data Source={dbPath}";
        }
    }

    public static void Configure(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(ConnectionString);
        Console.WriteLine($"Using SQLite database at: {ConnectionString}");
    }

    public ApplicationDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        Configure(optionsBuilder);
        var rollupLogger = new LoggerFactory().CreateLogger<PointsCacheConfiguration>();
        var rolldownLogger = new LoggerFactory().CreateLogger<AppointmentPriceCacheConfiguration>();        
        return new ApplicationDbContext(optionsBuilder.Options, 
            new PointsCacheConfiguration(rollupLogger, this), 
            new AppointmentPriceCacheConfiguration(rolldownLogger, this));
    }

    public ApplicationDbContext CreateDbContext(string[] args) => CreateDbContext();

}