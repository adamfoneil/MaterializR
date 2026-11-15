using Demo.Cache;
using MaterializR.Core;
using Microsoft.EntityFrameworkCore;

namespace Demo;

public class ApplicationDbContext(
    DbContextOptions<ApplicationDbContext> options,
    PointsSetCache pointsRollup,
    AppointmentPriceRowCache priceCacheConfig) : DbContext(options)
{
    private readonly PointsSetCache _pointsRollup = pointsRollup;
    private readonly AppointmentPriceRowCache _priceCache = priceCacheConfig;

    /// <summary>
    /// rollup/view entity
    /// </summary>
    public DbSet<PointsCache> PointsCache { get; set; }
    public DbSet<Appointment> Appointments { get; set; }
    public DbSet<DailyLimitDefault> DailyLimitDefaults { get; set; }
    public DbSet<DailyLimitOverride> DailyLimitOverrides { get; set; }
    public DbSet<AppointmentItem> AppointmentItems { get; set; }
    public DbSet<AppointmentPriceCache> AppointmentPrices { get; set; }

    /// <summary>
    /// Saves changes and marks the affected cache rows of <paramref name="tenantId"/> as stale.
    /// Clients that want to prevent cache rows being missed can disable the parameterless SaveChanges methods
    /// </summary>
    public async Task<int> SaveChangesAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        // capture SetCache changes
        var apptChanges = ChangeTracker.GetTouchedDimensions<Appointment, PointsCache.Dimensions>(
            a => new PointsCache.Dimensions(a.Date, a.TransportId ?? 0));

        // Save first so generated keys are available, while retaining entity states.
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);

        var priceChanges = ChangeTracker.GetTouchedKeys(e => e switch
        {
            Appointment a => ChangeTracker.InsertedKey(a, x => x.Id),
            AppointmentItem ai => ChangeTracker.RelatedKey(ai, x => x.AppointmentId),
            _ => (false, 0)
        });
        ChangeTracker.AcceptAllChanges();

        await _pointsRollup.MarkStaleDimensionsAsync(this, tenantId, apptChanges);

        await _priceCache.MarkStaleKeysAsync(this, tenantId, priceChanges);

        return result;
    }
}
