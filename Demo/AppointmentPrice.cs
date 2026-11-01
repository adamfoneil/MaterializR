using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Logging;
using System.Linq.Expressions;

namespace Demo;

public class AppointmentPrice : ICacheEntity
{
    public int Id { get; set; }
    public int AppointmentId { get; set; }
    public decimal TotalPrice { get; set; }
    public bool IsStale { get; set; }

    public Appointment? Appointment { get; set; }
}

public class AppointmentPriceConfiguration : IEntityTypeConfiguration<AppointmentPrice>
{
    public void Configure(EntityTypeBuilder<AppointmentPrice> builder)
    {
        builder.HasOne(ap => ap.Appointment).WithOne(a => a.Price).HasForeignKey<AppointmentPrice>(ap => ap.AppointmentId).OnDelete(DeleteBehavior.Cascade);

        builder.HasKey(ap => ap.AppointmentId);
        builder.Property(ap => ap.TotalPrice).HasColumnType("decimal(18,2)");
    }
}

public class AppointmentPriceRolldownConfiguration(
    ILogger<AppointmentPriceRolldownConfiguration> logger, IDbContextFactory<ApplicationDbContext> dbFactory) :
    RolldownConfiguration<ApplicationDbContext, AppointmentPrice, int>(logger, dbFactory)
{
    protected override Expression<Func<AppointmentPrice, int>> KeySelector => ap => ap.AppointmentId;

    protected override Task<AppointmentPrice[]> QueryLiveAsync(ApplicationDbContext dbContext, int[] keys)
    {
        throw new NotImplementedException();
    }

    protected override void UpdateFacts(AppointmentPrice existingRow, AppointmentPrice incomingRow)
    {
        existingRow.TotalPrice = incomingRow.TotalPrice;
    }

    protected override AppointmentPrice FromKey(int key) => new() { AppointmentId = key };

    protected override DbSet<AppointmentPrice> RolldownDbSet(ApplicationDbContext db) => db.AppointmentPrices;    
}