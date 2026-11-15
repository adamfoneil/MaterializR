using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace Demo.Cache;

public class AppointmentPriceRowCache(
    ILogger<AppointmentPriceRowCache> logger, IDbContextFactory<ApplicationDbContext> dbFactory) :
    RowCache<ApplicationDbContext, AppointmentPriceCache, int, int>(logger, dbFactory)
{
    protected override Expression<Func<AppointmentPriceCache, int>> KeySelector => ap => ap.AppointmentId;

    protected override Task<AppointmentPriceCache[]> QueryLiveAsync(ApplicationDbContext dbContext, int tenantId, int[] keys, CancellationToken cancellationToken)
    {
        if (keys.Length == 0)
        {
            return Task.FromResult(Array.Empty<AppointmentPriceCache>());
        }

        return dbContext.Appointments
            .Where(a => a.TenantId == tenantId && keys.Contains(a.Id))
            .Select(a => new AppointmentPriceCache
            {
                AppointmentId = a.Id,
                TotalPrice = a.Items.Sum(i => (decimal?)(i.UnitPrice * i.Quantity)) ?? 0m
            })
            .ToArrayAsync(cancellationToken);
    }

    protected override void UpdateFacts(AppointmentPriceCache existingRow, AppointmentPriceCache incomingRow)
    {
        existingRow.TotalPrice = incomingRow.TotalPrice;
    }

    protected override AppointmentPriceCache FromKey(int key) => new() { AppointmentId = key };

    protected override DbSet<AppointmentPriceCache> CacheDbSet(ApplicationDbContext db) => db.AppointmentPrices;

    protected override async Task UpsertStaleAsync(ApplicationDbContext dbContext, int tenantId, AppointmentPriceCache[] rows, CancellationToken cancellationToken)
    {
        var keys = rows.Select(row => row.AppointmentId).ToHashSet();
        var existingRows = await dbContext.AppointmentPrices
            .Where(row => row.TenantId == tenantId && keys.Contains(row.AppointmentId))
            .ToArrayAsync(cancellationToken);
        var existingKeys = existingRows.Select(row => row.AppointmentId).ToHashSet();

        foreach (var existing in existingRows)
        {
            existing.IsStale = true;
            dbContext.Entry(existing).Property(row => row.IsStale).IsModified = true;
        }

        dbContext.AppointmentPrices.AddRange(rows.Where(row => !existingKeys.Contains(row.AppointmentId)));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}