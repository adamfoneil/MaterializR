using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Demo;

public class DailyLimit
{
    public int LocationId { get; set; }
    public DateOnly Date { get; set; }
    public int MaxPoints { get; set; }
}

public class ScheduleConfiguration : IEntityTypeConfiguration<DailyLimit>
{
    public void Configure(EntityTypeBuilder<DailyLimit> builder)
    {
        builder.HasIndex(e => new { e.LocationId, e.Date }).IsUnique();
    }
}
