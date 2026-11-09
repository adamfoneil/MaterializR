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
}