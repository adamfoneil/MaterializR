using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Demo;

public class DailyLimitOverride
{
    public int Id { get; set; }
    public int LocationId { get; set; }
    public DateOnly Date { get; set; }
    public int MaxPoints { get; set; }
}

public class DailyLimitOverrideConfiguration : IEntityTypeConfiguration<DailyLimitOverride>
{
    public void Configure(EntityTypeBuilder<DailyLimitOverride> builder)
    {
        builder.HasIndex(e => new { e.LocationId, e.Date }).IsUnique();
    }
}
