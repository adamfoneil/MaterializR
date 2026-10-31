using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MaterializR.Core;

public abstract class RolldownConfiguration<TDbContext, TCacheEntity>(
    ILogger<RolldownConfiguration<TDbContext, TCacheEntity>> logger,
    IDbContextFactory<TDbContext> dbFactory)
    where TDbContext : DbContext
    where TCacheEntity : class, ICacheEntity
{
    protected readonly ILogger<RolldownConfiguration<TDbContext, TCacheEntity>> Logger = logger;

    protected readonly IDbContextFactory<TDbContext> DbFactory = dbFactory;

    protected abstract DbSet<TCacheEntity> RolldownDbSet(TDbContext db);
}
