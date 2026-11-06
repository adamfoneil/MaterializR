using MaterializR.Core;
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    PointsCacheConfiguration pointsRollup,
    AppointmentPriceCacheConfiguration rolldownConfig) : DbContext(options)
{
    private readonly PointsCacheConfiguration _pointsRollup = pointsRollup;
    private readonly AppointmentPriceCacheConfiguration _priceCache = rolldownConfig;

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
        // capture SetCache changes
        var apptChanges = _pointsRollup.GetDimensionChanges<Appointment>(this.ChangeTracker, a => new PointsCache.Dimensions(a.Date, a.TransportId ?? 0));

        // capture RowCache changes
        var priceChanges = ChangeTracker.GetAffectedKeys(e => e switch
        {
            Appointment a => a.Id,
            AppointmentItem ai => ai.AppointmentId,
            _ => 0
        });

        // normal SaveChanges behavior
        var result = await base.SaveChangesAsync(cancellationToken);

        // SetCache
        await _pointsRollup.MarkStaleDimensionsAsync(this, apptChanges);

        // RowCache
        await _priceCache.MarkStaleKeysAsync(this, priceChanges);

        return result;
    }
}
