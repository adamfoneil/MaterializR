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
        await _pointsRollup.TrackModifiedAsync<Appointment>(this, (appt) => new PointsRollup.Dimensions(appt.Date, appt.TransportId ?? 0));

        return await base.SaveChangesAsync(cancellationToken);
    }
}

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>, IDbContextFactory<ApplicationDbContext>
{
    public const string ConnectionString = "Data Source=Data\\demo.db";

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