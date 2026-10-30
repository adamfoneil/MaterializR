using Microsoft.EntityFrameworkCore;

namespace Demo;

internal class ApplicationDbContext : DbContext
{
    /// <summary>
    /// rollup/view entity
    /// </summary>
    public DbSet<PointsRollup> PointsRollup { get; set; }
    public DbSet<Appointment> Appointments { get; set; }
    public DbSet<DailyLimitDefault> DailyLimitDefaults { get; set; }
    public DbSet<DailyLimitOverride> DailyLimitOverrides { get; set; }
}
