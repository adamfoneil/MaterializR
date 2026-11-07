using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Demo;

public class AppointmentPriceCache : ITenantCacheEntity<int>
{
    public int Id { get; set; }
    public int TenantId { get; set; }
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
