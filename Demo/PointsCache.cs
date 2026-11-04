using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;

namespace Demo;

[DebuggerDisplay("{Date} - {TransportId} - {Scheduled}/{Maximum} ({PercentFreeLive})")]
public class PointsCache : ICacheEntity
{
    public int Id { get; set; }

    // dimensions

    public DateOnly Date { get; set; }
    /// <summary>
    /// 0 = none, any other value is transport
    /// </summary>
    public int TransportId { get; set; }

    // you'll need a type that represents the unique index of the cache/view table
    public record Dimensions(DateOnly Date, int TransportId);

    // facts

    public int Maximum { get; set; }
    public int Scheduled { get; set; }
    public int Available { get; set; }
    [NotMapped]
    public int AvailableLive => Maximum - Scheduled;
    public decimal PercentFree { get; set; }
    [NotMapped]
    public decimal PercentFreeLive => Maximum > 0 ? (decimal)(Maximum - Scheduled) / Maximum : 0m;

    public bool IsStale { get; set; } = true;
}

public class PointsConfiguration : IEntityTypeConfiguration<PointsCache>
{
    void IEntityTypeConfiguration<PointsCache>.Configure(EntityTypeBuilder<PointsCache> builder)
    {
        builder.HasIndex(e => new { e.Date, e.TransportId }).IsUnique();        

        builder.Property(e => e.PercentFree)
            .HasComputedColumnSql(
                "CASE WHEN \"Maximum\" = 0 THEN 0 ELSE 1 - CAST((\"Maximum\" - \"Scheduled\") AS float) / \"Maximum\" * 100 END",
                stored: true);

        builder.Property(e => e.Available).HasComputedColumnSql("\"Maximum\" - \"Scheduled\"", stored: true);
    }
}

public class PointsCacheConfiguration(
    ILogger<PointsCacheConfiguration> logger,
    IDbContextFactory<ApplicationDbContext> dbFactory) : SetCache<ApplicationDbContext, PointsCache, PointsCache.Dimensions>(logger, dbFactory)
{   
    private record DailyPointLimitDto(DateOnly Date, int Maximum);

    protected override Task<PointsCache[]> ExistingRowsAsync(ApplicationDbContext dbContext, PointsCache.Dimensions[] dimensions)
    {
        if (dimensions.Length == 0)
        {
            return Task.FromResult(Array.Empty<PointsCache>());
        }

        var uniqueDimensions = dimensions.Distinct().ToArray();
        var dates = uniqueDimensions.Select(d => d.Date).Distinct().ToArray();
        var transportIds = uniqueDimensions.Select(d => d.TransportId).Distinct().ToArray();

        return dbContext.PointsCache
            .Where(row => dates.Contains(row.Date) && transportIds.Contains(row.TransportId))
            .ToArrayAsync();
    }

    private static async Task<DailyPointLimitDto[]> DailyPointLimitsAsync(ApplicationDbContext db, DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return [];
        }

        var defaults = await db.DailyLimitDefaults
            .AsNoTracking()
            .Select(d => new { d.DayOfWeek, d.MaxPoints })
            .ToArrayAsync();

        var overrides = await db.DailyLimitOverrides
            .AsNoTracking()
            .Where(o => o.Date >= from && o.Date <= to)
            .Select(o => new { o.Date, o.MaxPoints })
            .ToArrayAsync();

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

    protected override async Task<PointsCache[]> QueryLiveAsync(ApplicationDbContext dbContext, PointsCache.Dimensions[] dimensions)
    {
        if (!dimensions.Any()) return [];

        var uniqueDimensions = dimensions.Distinct().ToArray();
        var from = uniqueDimensions.Min(d => d.Date);
        var to = uniqueDimensions.Max(d => d.Date);

        return await QueryLiveImplementationAsync(dbContext, from, to);        
    }

    /// <summary>
    /// this is really for test purposes to make the low-level query results observable
    /// </summary>
    public static async Task<PointsCache[]> QueryLiveImplementationAsync(ApplicationDbContext db, DateOnly from, DateOnly to)
    {
        var dailyLimits = await DailyPointLimitsAsync(db, from, to);
        var dailyLimitLookup = dailyLimits.ToDictionary(l => l.Date, l => l.Maximum);

        var scheduledLookup = (await db.Appointments
            .AsNoTracking()
            .Where(a => a.Date >= from && a.Date <= to)
            .Where(Appointment.AffectsCapacityExpression)
            .ToArrayAsync())            
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