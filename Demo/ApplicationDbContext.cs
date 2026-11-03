using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;

namespace Demo;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    PointsCacheConfiguration pointsRollup,
    AppointmentPriceCacheConfiguration rolldownConfig) : DbContext(options)
{
    private readonly PointsCacheConfiguration _pointsRollup = pointsRollup;
    private readonly AppointmentPriceCacheConfiguration _priceRolldown = rolldownConfig;

    /// <summary>
    /// rollup/view entity
    /// </summary>
    public DbSet<PointsCache> PointsCache { get; set; }
    public DbSet<Appointment> Appointments { get; set; }
    public DbSet<DailyLimitDefault> DailyLimitDefaults { get; set; }
    public DbSet<DailyLimitOverride> DailyLimitOverrides { get; set; }
    public DbSet<AppointmentItem> AppointmentItems { get; set; }
    public DbSet<AppointmentPriceCache> AppointmentPrices { get; set; }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var apptChanges = _pointsRollup.GetDimensionChanges<Appointment>(this.ChangeTracker, a => new PointsCache.Dimensions(a.Date, a.TransportId ?? 0));

        var priceChanges = _priceRolldown.GetAffectedKeys(this.ChangeTracker, e => e switch
        {
            Appointment a => a.Id,
            AppointmentItem ai => ai.AppointmentId,
            _ => 0
        });

        var result = await base.SaveChangesAsync(cancellationToken);

        await _pointsRollup.MarkStaleDimensionsAsync(this, apptChanges);
        await _priceRolldown.MarkStaleKeysAsync(this, priceChanges);

        return result;
    }
}

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