using Microsoft.EntityFrameworkCore;

namespace Demo;

internal class ApplicationDbContext : DbContext
{
    public DbSet<PointsView> Points { get; set; }
}
