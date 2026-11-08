using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Demo;

public class DailyLimitDefault
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public int MaxPoints { get; set; }
}

public class ScheduleConfiguration : IEntityTypeConfiguration<DailyLimitDefault>
{
    public void Configure(EntityTypeBuilder<DailyLimitDefault> builder)
    {
        builder.HasIndex(e => new { e.TenantId, e.DayOfWeek }).IsUnique();
    }
}
