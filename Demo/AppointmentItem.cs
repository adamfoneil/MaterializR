using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Demo;

public class AppointmentItem
{
    public int Id { get; set; }
    public int AppointmentId { get; set; }
    public int ItemId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice => Quantity * UnitPrice;

    public Appointment? Appointment { get; set; }
}

public class AppointmentItemConfiguration : IEntityTypeConfiguration<AppointmentItem>
{
    public void Configure(EntityTypeBuilder<AppointmentItem> builder)
    {
        builder.HasOne(e => e.Appointment).WithMany(a => a.Items).HasForeignKey(e => e.AppointmentId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(ai => ai.Quantity).IsRequired();
        builder.Property(ai => ai.UnitPrice).IsRequired().HasColumnType("decimal(18,2)");
        builder.Property(ai => ai.TotalPrice).HasComputedColumnSql("[Quantity] * [UnitPrice]", stored: true);
    }
}
