using MaterializR.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging.Abstractions;
using System.Linq.Expressions;

namespace Testing;

[TestClass]
public sealed class InlineCacheTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GetAsync_RefreshesOnlyStaleRequestedRow(bool isStale)
    {
        using var factory = new TestDbFactory();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.Rows.AddRange(
            new CacheRow { Id = 1, NeedsRefresh = isStale, Value = 10 },
            new CacheRow { Id = 2, NeedsRefresh = true, Value = 20 });
        await db.SaveChangesAsync();
        var cache = new TestCache(factory);

        var row = await cache.GetAsync(1);

        Assert.IsNotNull(row);
        Assert.AreEqual(1, row.Id);
        Assert.IsFalse(row.NeedsRefresh);
        Assert.AreEqual(isStale ? 100 : 10, row.Value);
        Assert.AreEqual(isStale ? "Updated" : null, row.Description);
        Assert.AreEqual(isStale ? 1 : 0, cache.RefreshCount);
        if (isStale)
        {
            Assert.AreSame(cache.RefreshedRow, row);
        }

        var stored = await db.Rows.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.IsFalse(stored.NeedsRefresh);
        Assert.AreEqual(row.Value, stored.Value);
        Assert.AreEqual(row.Description, stored.Description);
        var other = await db.Rows.AsNoTracking().SingleAsync(r => r.Id == 2);
        Assert.IsTrue(other.NeedsRefresh);
        Assert.AreEqual(20, other.Value);
        Assert.IsNull(other.Description);

        await cache.GetAsync(1);
        Assert.AreEqual(isStale ? 1 : 0, cache.RefreshCount);
        Assert.AreEqual(isStale ? 1 : 0, factory.UpdateCount);
    }

    [TestMethod]
    public async Task GetAsync_MissingRowReturnsNullWithoutRefreshing()
    {
        using var factory = new TestDbFactory();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var cache = new TestCache(factory);

        Assert.IsNull(await cache.GetAsync(1));
        Assert.AreEqual(0, cache.RefreshCount);
    }

    [TestMethod]
    public async Task GetAsync_FailedRefreshLeavesRowStale()
    {
        using var factory = new TestDbFactory();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.Rows.Add(new CacheRow { Id = 1, NeedsRefresh = true, Value = 10 });
        await db.SaveChangesAsync();
        var cache = new TestCache(factory) { FailRefresh = true };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cache.GetAsync(1));

        var stored = await db.Rows.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.IsTrue(stored.NeedsRefresh);
        Assert.AreEqual(10, stored.Value);
    }

    private sealed class TestCache(IDbContextFactory<TestDbContext> factory)
        : InlineCache<TestDbContext, CacheRow, int>(
            NullLogger<InlineCache<TestDbContext, CacheRow, int>>.Instance, factory)
    {
        public int RefreshCount { get; private set; }
        public bool FailRefresh { get; init; }
        public CacheRow? RefreshedRow { get; private set; }

        protected override DbSet<CacheRow> CacheDbSet(TestDbContext db) => db.Rows;
        protected override Expression<Func<CacheRow, int>> KeySelector => row => row.Id;
        protected override Expression<Func<CacheRow, bool>> IsStaleSelector => row => row.NeedsRefresh;

        protected override async Task<Action<UpdateSettersBuilder<CacheRow>>> UpdateLiveAsync(
            DbContext dbContext, CacheRow row, CancellationToken cancellationToken)
        {
            RefreshCount++;
            RefreshedRow = row;
            Assert.AreEqual(0, dbContext.ChangeTracker.Entries().Count());
            if (FailRefresh)
            {
                throw new InvalidOperationException("Refresh failed.");
            }

            var value = 100;
            var description = "Updated";
            row.Value = value;
            row.Description = description;
            var stored = await dbContext.Set<CacheRow>().AsNoTracking()
                .SingleAsync(r => r.Id == row.Id, cancellationToken);
            Assert.AreEqual(10, stored.Value);
            Assert.IsNull(stored.Description);
            Assert.IsTrue(stored.NeedsRefresh);
            Assert.AreEqual(0, dbContext.ChangeTracker.Entries().Count());

            return updates => updates
                .SetProperty(r => r.Value, value)
                .SetProperty(r => r.Description, description);
        }
    }

    private sealed class TestDbFactory : IDbContextFactory<TestDbContext>, IDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly DbContextOptions<TestDbContext> options;

        public int UpdateCount { get; private set; }

        public TestDbFactory()
        {
            connection.Open();
            options = new DbContextOptionsBuilder<TestDbContext>().UseSqlite(connection)
                .LogTo(message =>
                {
                    if (message.Contains("UPDATE \"Rows\"", StringComparison.Ordinal))
                    {
                        UpdateCount++;
                    }
                }, [RelationalEventId.CommandExecuted])
                .Options;
        }

        public TestDbContext CreateDbContext() => new(options);
        public void Dispose() => connection.Dispose();
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
        public DbSet<CacheRow> Rows => Set<CacheRow>();
    }

    private sealed class CacheRow
    {
        public int Id { get; set; }
        public bool NeedsRefresh { get; set; }
        public int Value { get; set; }
        public string? Description { get; set; }
    }
}
