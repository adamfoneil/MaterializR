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

    protected override AppointmentPriceCache FromKey(int key) => new() { AppointmentId = key };

    protected override DbSet<AppointmentPriceCache> CacheDbSet(ApplicationDbContext db) => db.AppointmentPrices;

    protected override Task UpsertStaleAsync(ApplicationDbContext dbContext, int tenantId, AppointmentPriceCache[] rows, CancellationToken cancellationToken) =>
        UpsertAsync(dbContext, tenantId, rows, refresh: false, cancellationToken);

    protected override Task UpsertFactsAsync(ApplicationDbContext dbContext, int tenantId, AppointmentPriceCache[] rows, CancellationToken cancellationToken) =>
        UpsertAsync(dbContext, tenantId, rows, refresh: true, cancellationToken);

    private static Task UpsertAsync(ApplicationDbContext dbContext, int tenantId, AppointmentPriceCache[] rows, bool refresh, CancellationToken cancellationToken) =>
        SqliteUpsert.ExecuteAsync(dbContext, rows, row =>
            $"INSERT INTO \"AppointmentPrices\" (\"TenantId\", \"AppointmentId\", \"TotalPrice\", \"IsStale\") VALUES ({tenantId}, {row.AppointmentId}, {row.TotalPrice}, {!refresh}) ON CONFLICT (\"TenantId\", \"AppointmentId\") DO UPDATE SET \"TotalPrice\" = CASE WHEN {refresh} THEN excluded.\"TotalPrice\" ELSE \"TotalPrice\" END, \"IsStale\" = excluded.\"IsStale\"",
            cancellationToken);
}