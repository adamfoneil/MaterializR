using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;

namespace Demo;

public class PointsRollup : IRollupEntity<PointsRollup.Dimensions>
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
    public decimal PercentFreeLive => Maximum > 0 ? (Maximum - Scheduled) / Maximum : 0;

    public bool IsModified { get; set; }

    public Dimensions ToDimensions() => new(Date, TransportId);    
}

public class PointsConfiguration : IEntityTypeConfiguration<PointsRollup>
{
    void IEntityTypeConfiguration<PointsRollup>.Configure(EntityTypeBuilder<PointsRollup> builder)
    {
        builder.HasIndex(e => new { e.Date, e.TransportId }).IsUnique();        

        builder.Property(e => e.PercentFree)
            .HasComputedColumnSql(
                "CASE WHEN \"Maximum\" = 0 THEN 0 ELSE 1 - CAST((\"Maximum\" - \"Scheduled\") AS float) / \"Maximum\" * 100 END",
                stored: true);

        builder.Property(e => e.Available).HasComputedColumnSql("\"Maximum\" - \"Scheduled\"", stored: true);
    }
}

public class PointsRollupConfiguration(IDbContextFactory<ApplicationDbContext> dbFactory) : RollupConfiguration<ApplicationDbContext, PointsRollup, PointsRollup.Dimensions>(dbFactory)
{   
    private record DailyPointLimitDto(DateOnly Date, int Maximum);

    protected override Task MergeAsync(ApplicationDbContext dbContext, PointsRollup[] data)
    {
        throw new NotImplementedException();
    }

    protected override Task MergeModifiedDimensionsAsync(ApplicationDbContext dbContext, PointsRollup.Dimensions[] dimensions)
    {
        throw new NotImplementedException();
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

    protected override async Task<PointsRollup[]> QueryLiveAsync(ApplicationDbContext dbContext, PointsRollup.Dimensions[] dimensions)
    {
        if (dimensions.Length == 0)
        {
            return [];
        }

        var uniqueDimensions = dimensions.Distinct().ToArray();
        var from = uniqueDimensions.Min(d => d.Date);
        var to = uniqueDimensions.Max(d => d.Date);

        var dailyLimits = await DailyPointLimitsAsync(dbContext, from, to);
        var dailyLimitLookup = dailyLimits.ToDictionary(l => l.Date, l => l.Maximum);

        var scheduledLookup = (await dbContext.Appointments
            .AsNoTracking()
            .Where(a => a.Date >= from && a.Date <= to)
            .ToArrayAsync())
            .Where(a => a.AffectsCapacity())
            .GroupBy(a => (a.Date, TransportId: a.TransportId ?? 0))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Points));

        return [.. uniqueDimensions.Select(d => new PointsRollup
        {
            Date = d.Date,
            TransportId = d.TransportId,
            Maximum = dailyLimitLookup.GetValueOrDefault(d.Date),
            Scheduled = scheduledLookup.GetValueOrDefault((d.Date, d.TransportId)),
            PercentFree = 0
        })];
    }

    protected override DbSet<PointsRollup> ViewEntity(ApplicationDbContext db) => db.PointsRollup;    
}