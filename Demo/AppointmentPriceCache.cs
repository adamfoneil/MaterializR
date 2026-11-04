using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace Demo;

public class AppointmentPriceCache : ICacheEntity
{
    public int Id { get; set; }
    public int AppointmentId { get; set; }
    public decimal TotalPrice { get; set; }
    public bool IsStale { get; set; }

    public Appointment? Appointment { get; set; }
}

public class AppointmentPriceConfiguration : IEntityTypeConfiguration<AppointmentPriceCache>
{
    public void Configure(EntityTypeBuilder<AppointmentPriceCache> builder)
    {
        builder.HasOne(ap => ap.Appointment).WithOne(a => a.Price).HasForeignKey<AppointmentPriceCache>(ap => ap.AppointmentId).OnDelete(DeleteBehavior.Cascade);

        builder.HasKey(ap => ap.AppointmentId);
        builder.Property(ap => ap.TotalPrice).HasColumnType("decimal(18,2)");
    }
}

public class AppointmentPriceCacheConfiguration(
    ILogger<AppointmentPriceCacheConfiguration> logger, IDbContextFactory<ApplicationDbContext> dbFactory) :
    RowCache<ApplicationDbContext, AppointmentPriceCache, int>(logger, dbFactory)
{
    protected override Expression<Func<AppointmentPriceCache, int>> KeySelector => ap => ap.AppointmentId;

    protected override Task<AppointmentPriceCache[]> QueryLiveAsync(ApplicationDbContext dbContext, int[] keys)
    {
        if (keys.Length == 0)
        {
            return Task.FromResult(Array.Empty<AppointmentPriceCache>());
        }

        return dbContext.Appointments
            .Where(a => keys.Contains(a.Id))
            .Select(a => new AppointmentPriceCache
            {
                AppointmentId = a.Id,
                TotalPrice = a.Items.Sum(i => (decimal?)(i.UnitPrice * i.Quantity)) ?? 0m
            })
            .ToArrayAsync();
    }

    protected override void UpdateFacts(AppointmentPriceCache existingRow, AppointmentPriceCache incomingRow)
    {
        existingRow.TotalPrice = incomingRow.TotalPrice;
    }

    protected override AppointmentPriceCache FromKey(int key) => new() { AppointmentId = key };

    protected override DbSet<AppointmentPriceCache> CacheDbSet(ApplicationDbContext db) => db.AppointmentPrices;    
}