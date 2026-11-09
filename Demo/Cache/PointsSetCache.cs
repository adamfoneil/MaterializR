using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Demo.Cache;

public class PointsSetCache(
    ILogger<PointsSetCache> logger,
    IDbContextFactory<ApplicationDbContext> dbFactory) : SetCache<ApplicationDbContext, PointsCache, PointsCache.Dimensions, int>(logger, dbFactory)
{
    private record DailyPointLimitDto(DateOnly Date, int Maximum);

    protected override Task<PointsCache[]> ExistingRowsAsync(ApplicationDbContext dbContext, int tenantId, PointsCache.Dimensions[] dimensions, CancellationToken cancellationToken)
    {
        if (dimensions.Length == 0)
        {
            return Task.FromResult(Array.Empty<PointsCache>());
        }

        var uniqueDimensions = dimensions.Distinct().ToArray();
        var dates = uniqueDimensions.Select(d => d.Date).Distinct().ToArray();
        var transportIds = uniqueDimensions.Select(d => d.TransportId).Distinct().ToArray();

        return dbContext.PointsCache
            .Where(row => row.TenantId == tenantId && dates.Contains(row.Date) && transportIds.Contains(row.TransportId))
            .ToArrayAsync(cancellationToken);
    }

    private static async Task<DailyPointLimitDto[]> DailyPointLimitsAsync(ApplicationDbContext db, int tenantId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (to < from)
        {
            return [];
        }

        var defaults = await db.DailyLimitDefaults
            .AsNoTracking()
                .Where(d => d.TenantId == tenantId)
                .Select(d => new { d.DayOfWeek, d.MaxPoints })
                .ToArrayAsync(cancellationToken);

        var overrides = await db.DailyLimitOverrides
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.Date >= from && o.Date <= to)
            .Select(o => new { o.Date, o.MaxPoints })
            .ToArrayAsync(cancellationToken);

        var defaultLookup = defaults.ToDictionary(d => d.DayOfWeek, d => d.MaxPoints);

        var overrideLookup = overrides.ToDictionary(o => o.Date, o => o.MaxPoints);

        var results = new List<DailyPointLimitDto>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (overrideLookup.TryGetValue(date, out var overrideLimit))
            {
                results.Add(new DailyPointLimitDto(date, overrideLimit));
                continue;
            }

            if (defaultLookup.TryGetValue(date.DayOfWeek, out var defaultLimit))
            {
                results.Add(new DailyPointLimitDto(date, defaultLimit));
            }
        }

        return [.. results];
    }

    protected override async Task<PointsCache[]> QueryLiveAsync(ApplicationDbContext dbContext, int tenantId, PointsCache.Dimensions[] dimensions, CancellationToken cancellationToken)
    {
        if (!dimensions.Any()) return [];

        var uniqueDimensions = dimensions.Distinct().ToArray();
        var from = uniqueDimensions.Min(d => d.Date);
        var to = uniqueDimensions.Max(d => d.Date);

        return await QueryLiveImplementationAsync(dbContext, tenantId, from, to, cancellationToken);
    }

    /// <summary>
    /// this is really for test purposes to make the low-level query results observable
    /// </summary>
    public static async Task<PointsCache[]> QueryLiveImplementationAsync(ApplicationDbContext db, int tenantId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var dailyLimits = await DailyPointLimitsAsync(db, tenantId, from, to, cancellationToken);
        var dailyLimitLookup = dailyLimits.ToDictionary(l => l.Date, l => l.Maximum);

        var scheduledLookup = (await db.Appointments
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Date >= from && a.Date <= to)
            .Where(Appointment.AffectsCapacityExpression)
            .ToArrayAsync(cancellationToken))
            .GroupBy(a => (a.Date, TransportId: a.TransportId ?? 0))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Points));

        return [.. scheduledLookup.Select(d => new PointsCache
        {
            Date = d.Key.Date,
            TransportId = d.Key.TransportId,
            Maximum = dailyLimitLookup.GetValueOrDefault(d.Key.Date),
            Scheduled = scheduledLookup.GetValueOrDefault((d.Key.Date, d.Key.TransportId))
        })];
    }

    protected override DbSet<PointsCache> CacheDbSet(ApplicationDbContext db) => db.PointsCache;

    protected override void UpdateFacts(PointsCache existingRow, PointsCache incomingRow)
    {
        existingRow.Maximum = incomingRow.Maximum;
        existingRow.Scheduled = incomingRow.Scheduled;
    }

    protected override PointsCache FromDimensions(PointsCache.Dimensions dimensions) => new()
    {
        Date = dimensions.Date,
        TransportId = dimensions.TransportId
    };

    protected override PointsCache.Dimensions ToDimensions(PointsCache entity) => new(entity.Date, entity.TransportId);
}