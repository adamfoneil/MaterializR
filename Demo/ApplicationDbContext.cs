using Microsoft.EntityFrameworkCore;

namespace Demo;

internal class ApplicationDbContext : DbContext
{
    public DbSet<Points> Points { get; set; }
}
