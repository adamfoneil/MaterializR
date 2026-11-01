using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging;

namespace Demo;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    PointsRollupConfiguration pointsRollup,
    AppointmentPriceAggregate rolldownConfig) : DbContext(options)
{
    private readonly PointsRollupConfiguration _pointsRollup = pointsRollup;
    private readonly AppointmentPriceAggregate _priceRolldown = rolldownConfig;

    /// <summary>
    /// rollup/view entity
    /// </summary>
    public DbSet<PointsCache> PointsRollup { get; set; }
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
        var rollupLogger = new LoggerFactory().CreateLogger<PointsRollupConfiguration>();
        var rolldownLogger = new LoggerFactory().CreateLogger<AppointmentPriceAggregate>();        
        return new ApplicationDbContext(optionsBuilder.Options, 
            new PointsRollupConfiguration(rollupLogger, this), 
            new AppointmentPriceAggregate(rolldownLogger, this));
    }

    public ApplicationDbContext CreateDbContext(string[] args) => CreateDbContext();

}