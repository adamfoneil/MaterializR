using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Demo;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    PointsRollupConfiguration pointsRollup) : DbContext(options)
{
    private readonly PointsRollupConfiguration _pointsRollup = pointsRollup;

    /// <summary>
    /// rollup/view entity
    /// </summary>
    public DbSet<PointsRollup> PointsRollup { get; set; }
    public DbSet<Appointment> Appointments { get; set; }
    public DbSet<DailyLimitDefault> DailyLimitDefaults { get; set; }
    public DbSet<DailyLimitOverride> DailyLimitOverrides { get; set; }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var apptChanges = _pointsRollup.GetDimensionChanges<Appointment>(this, a => new PointsRollup.Dimensions(a.Date, a.TransportId ?? 0));

        var result = await base.SaveChangesAsync(cancellationToken);

        await _pointsRollup.TrackDimensionChangesAsync(this, apptChanges);

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
    }

    public ApplicationDbContext CreateDbContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        Configure(optionsBuilder);
        return new ApplicationDbContext(optionsBuilder.Options, new PointsRollupConfiguration(this));
    }

    public ApplicationDbContext CreateDbContext(string[] args) => CreateDbContext();

}