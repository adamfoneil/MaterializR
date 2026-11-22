using System.Linq.Expressions;
using System.Text.Json;
using MaterializR.Core;
using MaterializR.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Testing;

[TestClass]
[DoNotParallelize]
public sealed class JsonFactTests
{
    private static readonly PostgreSqlContainer Database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    [ClassInitialize]
    public static async Task StartAsync(TestContext context)
    {
        try { await Database.StartAsync(); }
        catch { await Database.DisposeAsync(); throw; }
    }

    [ClassCleanup]
    public static async Task StopAsync() => await Database.DisposeAsync();

    [TestMethod]
    public async Task RowCache_JsonFactsSurviveMarkingAndAreReplacedOnRefresh()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetAsync(db);
        db.Rows.AddRange(
            new JsonRow { TenantId = 1, Key = 1, Items = [new() { Name = "old" }], OtherItems = [new() { Name = "kept" }], IsStale = false },
            new JsonRow { TenantId = 2, Key = 1, Items = [new() { Name = "other tenant" }], IsStale = false });
        await db.SaveChangesAsync();
        var cache = new RowCacheImpl(factory);

        await cache.MarkStaleKeysAsync(db, 1, [1, 2]);
        var marked = await db.Rows.AsNoTracking().Where(row => row.TenantId == 1).OrderBy(row => row.Key).ToArrayAsync();
        Assert.AreEqual("old", marked[0].Items.Single().Name);
        Assert.AreEqual("kept", marked[0].OtherItems.Single().Name);
        Assert.IsTrue(marked[0].IsStale);
        Assert.HasCount(0, marked[1].Items);
        Assert.HasCount(0, marked[1].OtherItems);
        Assert.IsTrue(marked[1].IsStale);

        cache.LiveRows = [
            new JsonRow { Key = 1, Scalar = "refreshed scalar", Items = [new() { Name = "new 'value'" }], OtherItems = [new() { Name = "second" }] },
            new JsonRow { Key = 2, Items = [new() { Name = "inserted" }], OtherItems = [] }
        ];
        Assert.AreEqual(2, await cache.UpdateAsync(1));
        var refreshed = await db.Rows.AsNoTracking().OrderBy(row => row.TenantId).ThenBy(row => row.Key).ToArrayAsync();
        Assert.AreEqual("new 'value'", refreshed[0].Items.Single().Name);
        Assert.AreEqual("second", refreshed[0].OtherItems.Single().Name);
        Assert.AreEqual("refreshed scalar", refreshed[0].Scalar);
        Assert.IsFalse(refreshed[0].IsStale);
        Assert.AreEqual("inserted", refreshed[1].Items.Single().Name);
        Assert.HasCount(0, refreshed[1].OtherItems);
        Assert.IsFalse(refreshed[1].IsStale);
        Assert.AreEqual("other tenant", refreshed[2].Items.Single().Name);
        Assert.IsFalse(refreshed[2].IsStale);
    }

    [TestMethod]
    public async Task SetCache_JsonFactsAreWrittenOnInsertAndRefresh()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetAsync(db);
        var cache = new SetCacheImpl(factory);
        await cache.MarkStaleDimensionsAsync(db, 1, [5]);
        var row = await db.Sets.AsNoTracking().SingleAsync();
        Assert.HasCount(0, row.Items);
        Assert.IsNull(row.ExtraJson);
        Assert.IsTrue(row.IsStale);
        await cache.RefreshAsync(db, 1, [new JsonSet { Key = 5, Items = [new() { Name = "updated" }], ExtraJson = "{\"answer\":42}" },
            new JsonSet { Key = 6, Items = [new() { Name = "insert" }], ExtraJson = "[]" }]);
        row = await db.Sets.AsNoTracking().SingleAsync(item => item.Key == 5);
        Assert.AreEqual("updated", row.Items.Single().Name);
        Assert.AreEqual("{\"answer\": 42}", row.ExtraJson);
        Assert.IsFalse(row.IsStale);
        var inserted = await db.Sets.AsNoTracking().SingleAsync(item => item.Key == 6);
        Assert.AreEqual("insert", inserted.Items.Single().Name);
        Assert.AreEqual("[]", inserted.ExtraJson);
        Assert.IsFalse(inserted.IsStale);
    }

    [TestMethod]
    public async Task JsonFacts_RejectInvalidOrConflictingColumns()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        var cache = new RowCacheImpl(factory);
        foreach (var facts in new IReadOnlyList<JsonFact<JsonRow>>[]
        {
            [new("missing", _ => "[]")],
            [new("TenantId", _ => "[]")],
            [new("items json", _ => "[]"), new("items json", _ => "[]")],
            [new("items json", null!)],
            [new(" ", _ => "[]")],
            [null!]
        })
        {
            cache.Facts = facts;
            await Assert.ThrowsAsync<ArgumentException>(() => cache.MarkStaleKeysAsync(db, 1, [1]));
        }

        var setCache = new SetCacheImpl(factory) { SelectExtraJsonAsScalar = true };
        await Assert.ThrowsAsync<ArgumentException>(() => setCache.MarkStaleDimensionsAsync(db, 1, [1]));
    }

    [TestMethod]
    public async Task InvalidJson_PropagatesErrorAndLeavesStaleFactsUntouched()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetAsync(db);
        var cache = new SetCacheImpl(factory);
        await cache.MarkStaleDimensionsAsync(db, 1, [5]);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => cache.RefreshAsync(db, 1,
            [new JsonSet { Key = 5, Items = [new() { Name = "not saved" }], ExtraJson = "not json" }]));
        Assert.AreEqual("22P02", exception.SqlState);
        var row = await db.Sets.AsNoTracking().SingleAsync();
        Assert.IsTrue(row.IsStale);
        Assert.HasCount(0, row.Items);
        Assert.IsNull(row.ExtraJson);
    }

    private static Factory CreateFactory() => new(new DbContextOptionsBuilder<JsonContext>()
        .UseNpgsql(Database.GetConnectionString()).Options);

    private static async Task ResetAsync(JsonContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Rows\", \"Sets\"");
    }

    private sealed class Factory(DbContextOptions<JsonContext> options) : IDbContextFactory<JsonContext>
    {
        public JsonContext CreateDbContext() => new(options);
    }

    private sealed class JsonContext(DbContextOptions<JsonContext> options) : DbContext(options)
    {
        public DbSet<JsonRow> Rows => Set<JsonRow>();
        public DbSet<JsonSet> Sets => Set<JsonSet>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<JsonRow>(entity =>
            {
                entity.HasKey(row => new { row.TenantId, row.Key });
                entity.OwnsMany(row => row.Items, owned => owned.ToJson("items json"));
                entity.OwnsMany(row => row.OtherItems, owned => owned.ToJson("other json"));
            });
            builder.Entity<JsonSet>(entity =>
            {
                entity.HasKey(row => new { row.TenantId, row.Key });
                entity.OwnsMany(row => row.Items, owned => owned.ToJson("items json"));
                entity.Property(row => row.ExtraJson).HasColumnType("jsonb");
            });
        }
    }

    private sealed class Item { public string Name { get; set; } = ""; }
    private sealed class JsonRow : ITenantCacheEntity<int>
    {
        public int TenantId { get; set; }
        public int Key { get; set; }
        public bool IsStale { get; set; }
        public string Scalar { get; set; } = "placeholder";
        public List<Item> Items { get; set; } = [];
        public List<Item> OtherItems { get; set; } = [];
    }
    private sealed class JsonSet : ITenantCacheEntity<int>
    {
        public int TenantId { get; set; }
        public int Key { get; set; }
        public bool IsStale { get; set; }
        public List<Item> Items { get; set; } = [];
        public string? ExtraJson { get; set; }
    }

    private sealed class RowCacheImpl(Factory factory) : PostgreSqlRowCache<JsonContext, JsonRow, int, int>(NullLogger<RowCacheImpl>.Instance, factory)
    {
        public IReadOnlyList<JsonFact<JsonRow>> Facts { get; set; } =
        [new("items json", row => JsonSerializer.Serialize(row.Items)), new("other json", row => JsonSerializer.Serialize(row.OtherItems))];
        public JsonRow[] LiveRows { get; set; } = [];
        protected override IReadOnlyList<JsonFact<JsonRow>> JsonFacts => Facts;
        protected override Expression<Func<JsonRow, object?>> FactProperties => row => row.Scalar;
        protected override Expression<Func<JsonRow, int>> KeySelector => row => row.Key;
        protected override JsonRow FromKey(int key) => new() { Key = key };
        protected override DbSet<JsonRow> CacheDbSet(JsonContext db) => db.Rows;
        protected override Task<JsonRow[]> QueryLiveAsync(JsonContext dbContext, int tenantId, int[] keys, CancellationToken cancellationToken) => Task.FromResult(LiveRows);
    }

    private sealed class SetCacheImpl(Factory factory) : PostgreSqlSetCache<JsonContext, JsonSet, int, int>(NullLogger<SetCacheImpl>.Instance, factory)
    {
        public bool SelectExtraJsonAsScalar { get; set; }
        protected override IReadOnlyList<JsonFact<JsonSet>> JsonFacts => [new("items json", row => JsonSerializer.Serialize(row.Items)), new("ExtraJson", row => row.ExtraJson)];
        protected override Expression<Func<JsonSet, object?>> FactProperties => SelectExtraJsonAsScalar ? row => row.ExtraJson : row => new { };
        protected override Expression<Func<JsonSet, object?>> DimensionsKeySelector => row => row.Key;
        protected override JsonSet FromDimensions(int dimensions) => new() { Key = dimensions };
        protected override int ToDimensions(JsonSet entity) => entity.Key;
        protected override DbSet<JsonSet> CacheDbSet(JsonContext db) => db.Sets;
        protected override Task<JsonSet[]> QueryLiveAsync(JsonContext dbContext, int tenantId, int[] dimensions, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<JsonSet>());
        public Task RefreshAsync(JsonContext db, int tenantId, JsonSet[] rows) => UpsertFactsAsync(db, tenantId, rows, CancellationToken.None);
    }
}
