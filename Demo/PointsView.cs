using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;
using System.ComponentModel.DataAnnotations.Schema;

namespace Demo;

internal class PointsView : IViewEntity<PointsView.Dimensions>
{
    // dimensions

    public int LocationId { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>
    /// 0 = none, any other value is transport
    /// </summary>
    public int TransportId { get; set; }

    // you'll need a type that represents the unique index of the cache/view table
    public record Dimensions(int LocationId, DateOnly Date, int TransportId);

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

    public Dimensions ToDimensions() => new(LocationId, Date, TransportId);    
}

public class PointsConfiguration : IEntityTypeConfiguration<PointsView>
{
    void IEntityTypeConfiguration<PointsView>.Configure(EntityTypeBuilder<PointsView> builder)
    {
        builder.HasIndex(e => new { e.LocationId, e.Date, e.TransportId }).IsUnique();        

        builder.Property(e => e.PercentFree)
            .HasComputedColumnSql(
                "CASE WHEN \"Maximum\" = 0 THEN 0 ELSE 1 - CAST((\"Maximum\" - \"Scheduled\") AS float) / \"Maximum\" * 100 END",
                stored: true);

        builder.Property(e => e.Available).HasComputedColumnSql("\"Maximum\" - \"Scheduled\"", stored: true);
    }
}

internal class PointsViewConfiguration(IDbContextFactory<ApplicationDbContext> dbFactory) : ViewConfigurationBase<ApplicationDbContext, PointsView, PointsView.Dimensions>(dbFactory)
{   
    private record DailyPointLimitDto(int LocationId, DateOnly Date, int Maximum);

    protected override Task MergeAsync(ApplicationDbContext dbContext, PointsView[] data)
    {
        throw new NotImplementedException();
    }

    protected override Task MergeDimensionsAsync(ApplicationDbContext dbContext, PointsView.Dimensions[] dimensions)
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
            .Select(d => new { d.LocationId, d.DayOfWeek, d.MaxPoints })
            .ToArrayAsync();

        var overrides = await db.DailyLimitOverrides
            .AsNoTracking()
            .Where(o => o.Date >= from && o.Date <= to)
            .Select(o => new { o.LocationId, o.Date, o.MaxPoints })
            .ToArrayAsync();

        var defaultLookup = defaults.ToDictionary(
            d => (d.LocationId, d.DayOfWeek),
            d => d.MaxPoints);

        var overrideLookup = overrides.ToDictionary(
            o => (o.LocationId, o.Date),
            o => o.MaxPoints);

        var locationIds = defaults.Select(d => d.LocationId)
            .Union(overrides.Select(o => o.LocationId))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        var results = new List<DailyPointLimitDto>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            foreach (var locationId in locationIds)
            {
                if (overrideLookup.TryGetValue((locationId, date), out var overrideLimit))
                {
                    results.Add(new DailyPointLimitDto(locationId, date, overrideLimit));
                    continue;
                }

                if (defaultLookup.TryGetValue((locationId, date.DayOfWeek), out var defaultLimit))
                {
                    results.Add(new DailyPointLimitDto(locationId, date, defaultLimit));
                }
            }
        }

        return [.. results];
    }

    protected override async Task<PointsView[]> QueryLiveAsync(ApplicationDbContext dbContext, PointsView.Dimensions[] dimensions)
    {
        if (dimensions.Length == 0)
        {
            return [];
        }

        var uniqueDimensions = dimensions.Distinct().ToArray();
        var from = uniqueDimensions.Min(d => d.Date);
        var to = uniqueDimensions.Max(d => d.Date);

        var dailyLimits = await DailyPointLimitsAsync(dbContext, from, to);
        var dailyLimitLookup = dailyLimits.ToDictionary(l => (l.LocationId, l.Date), l => l.Maximum);

        var locationIds = uniqueDimensions.Select(d => d.LocationId).Distinct().ToArray();

        var scheduledLookup = (await dbContext.Appointments
            .AsNoTracking()
            .Where(a => a.Date >= from && a.Date <= to && locationIds.Contains(a.LocationId))
            .ToArrayAsync())
            .Where(a => a.AffectsCapacity())
            .GroupBy(a => (a.LocationId, a.Date, TransportId: a.TransportId ?? 0))
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Points));

        return [.. uniqueDimensions.Select(d => new PointsView
        {
            LocationId = d.LocationId,
            Date = d.Date,
            TransportId = d.TransportId,
            Maximum = dailyLimitLookup.GetValueOrDefault((d.LocationId, d.Date)),
            Scheduled = scheduledLookup.GetValueOrDefault((d.LocationId, d.Date, d.TransportId)),
            PercentFree = 0
        })];
    }

    protected override Task<PointsView.Dimensions[]> TrackModifiedDimensionsAsync(ApplicationDbContext dbContext, ChangeTracker changeTracker)
    {
        throw new NotImplementedException();
    }

    protected override DbSet<PointsView> ViewEntity(ApplicationDbContext db) => db.PointsView;    
}