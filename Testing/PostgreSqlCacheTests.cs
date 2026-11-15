using MaterializR.Core;
using MaterializR.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Linq.Expressions;

namespace Testing;

[TestClass]
[DoNotParallelize]
public sealed class PostgreSqlCacheTests
{
    private const int Tenant = 1;
    private const int OtherTenant = 2;

    [TestMethod]
    public async Task RowCache_UpsertsDistinctKeysAndPreservesExistingFactsPerTenant()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var existing = new PgRowCache { TenantId = Tenant, CacheKey = 5, CachedFact = "preserved" };
        db.RowCaches.AddRange(
            existing,
            new PgRowCache { TenantId = OtherTenant, CacheKey = 5, CachedFact = "other", IsStale = false });
        await db.SaveChangesAsync();

        var cache = CreateRowCache(factory);
        await cache.MarkStaleKeysAsync(db, Tenant, [5, 5, 6]);

        Assert.IsTrue(existing.IsStale);
        var rows = await db.RowCaches.AsNoTracking().OrderBy(row => row.TenantId).ThenBy(row => row.CacheKey).ToArrayAsync();
        Assert.HasCount(3, rows);
        Assert.AreEqual("preserved", rows.Single(row => row.TenantId == Tenant && row.CacheKey == 5).CachedFact);
        Assert.IsTrue(rows.Single(row => row.TenantId == Tenant && row.CacheKey == 5).IsStale);
        Assert.AreEqual("placeholder", rows.Single(row => row.TenantId == Tenant && row.CacheKey == 6).CachedFact);
        Assert.IsTrue(rows.Single(row => row.TenantId == Tenant && row.CacheKey == 6).IsStale);
        Assert.AreEqual("other", rows.Single(row => row.TenantId == OtherTenant).CachedFact);
        Assert.IsFalse(rows.Single(row => row.TenantId == OtherTenant).IsStale);
        Assert.IsInstanceOfType<PgSpecialRowCache>(rows.Single(row => row.TenantId == Tenant && row.CacheKey == 6));
        Assert.AreEqual("special placeholder", ((PgSpecialRowCache)rows.Single(row => row.TenantId == Tenant && row.CacheKey == 6)).SpecialFact);
    }

    [TestMethod]
    public async Task RowCache_ConcurrentContextsUpsertTheSameMissingKey()
    {
        var factory = CreateFactory();
        await using (var setup = factory.CreateDbContext()) await ResetTablesAsync(setup);
        var cache = CreateRowCache(factory);

        await Task.WhenAll(
            MarkRowInNewContextAsync(cache, factory, 9),
            MarkRowInNewContextAsync(cache, factory, 9));

        await using var verify = factory.CreateDbContext();
        Assert.AreEqual(1, await verify.RowCaches.CountAsync(row => row.TenantId == Tenant && row.CacheKey == 9));
        Assert.IsTrue(await verify.RowCaches.Where(row => row.TenantId == Tenant && row.CacheKey == 9).Select(row => row.IsStale).SingleAsync());
    }

    [TestMethod]
    public async Task RowCache_DetachesMatchingPendingAddedEntityAfterUpsert()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var pending = new PgRowCache { TenantId = Tenant, CacheKey = 15, CachedFact = "unpersisted" };
        db.RowCaches.Add(pending);
        var cache = CreateRowCache(factory);

        await cache.MarkStaleKeysAsync(db, Tenant, [15]);
        Assert.AreEqual(EntityState.Detached, db.Entry(pending).State);
        await db.SaveChangesAsync();

        var persisted = await db.RowCaches.AsNoTracking().SingleAsync(row => row.TenantId == Tenant && row.CacheKey == 15);
        Assert.AreEqual("placeholder", persisted.CachedFact);
        Assert.IsTrue(persisted.IsStale);
    }

    [TestMethod]
    public async Task RowCache_UsesCallerTransactionAndDoesNotCommitIt()
    {
        var factory = CreateFactory();
        await using var setup = factory.CreateDbContext();
        await ResetTablesAsync(setup);
        await using var transaction = await setup.Database.BeginTransactionAsync();
        var cache = CreateRowCache(factory);

        await cache.MarkStaleKeysAsync(setup, Tenant, [10]);
        Assert.IsNotNull(setup.Database.CurrentTransaction);
        Assert.AreEqual(1, await setup.RowCaches.CountAsync(row => row.TenantId == Tenant && row.CacheKey == 10));
        await transaction.RollbackAsync();

        await using var verify = factory.CreateDbContext();
        Assert.AreEqual(0, await verify.RowCaches.CountAsync(row => row.TenantId == Tenant && row.CacheKey == 10));
    }

    [TestMethod]
    public async Task RowCache_MarkStaleWhereUpsertsMatchingTenantKeys()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        db.Sources.AddRange(
            new PgSource { TenantId = Tenant, CacheKey = 11 },
            new PgSource { TenantId = Tenant, CacheKey = 12 },
            new PgSource { TenantId = OtherTenant, CacheKey = 13 });
        db.RowCaches.Add(new PgRowCache { TenantId = Tenant, CacheKey = 11, CachedFact = "retained", IsStale = false });
        await db.SaveChangesAsync();
        var cache = CreateRowCache(factory);

        await cache.MarkStaleWhereAsync(db, Tenant, db.Sources.Where(source => source.TenantId == Tenant), source => source.CacheKey);

        var rows = await db.RowCaches.AsNoTracking().OrderBy(row => row.CacheKey).ToArrayAsync();
        Assert.HasCount(2, rows);
        Assert.AreEqual("retained", rows[0].CachedFact);
        Assert.IsTrue(rows.All(row => row.IsStale));
        Assert.IsFalse(await db.RowCaches.AnyAsync(row => row.CacheKey == 13));
    }

    [TestMethod]
    public async Task RowCache_PropagatesUnrelatedDatabaseErrors()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var cache = CreateRowCache(factory);

        await Assert.ThrowsAsync<PostgresException>(() => cache.MarkStaleKeysAsync(db, Tenant, [-99]));
    }

    [TestMethod]
    public async Task RowCache_RollsBackEarlierBatchesWhenALaterBatchFails()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var cache = CreateRowCache(factory);
        var keys = Enumerable.Range(1, 15000).Append(-99).ToArray();

        await Assert.ThrowsAsync<PostgresException>(() => cache.MarkStaleKeysAsync(db, Tenant, keys));

        Assert.AreEqual(0, await db.RowCaches.CountAsync());
    }

    [TestMethod]
    public async Task SetCache_UpsertsDimensionsPreservingFactsAndIsolatingTenants()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var dimension = new PgSetCache.Dimensions(new DateOnly(2026, 1, 2), "north");
        db.SetCaches.AddRange(
            new PgSetCache { TenantId = Tenant, Date = dimension.Date, Region = dimension.Region, CachedFact = "preserved" },
            new PgSetCache { TenantId = OtherTenant, Date = dimension.Date, Region = dimension.Region, CachedFact = "other", IsStale = false });
        await db.SaveChangesAsync();
        var cache = CreateSetCache(factory);

        await cache.MarkStaleDimensionsAsync(db, Tenant, [dimension, dimension, new(new DateOnly(2026, 1, 3), "south")]);

        var rows = await db.SetCaches.AsNoTracking().OrderBy(row => row.TenantId).ThenBy(row => row.Date).ToArrayAsync();
        Assert.HasCount(3, rows);
        Assert.AreEqual("preserved", rows.Single(row => row.TenantId == Tenant && row.Date == dimension.Date).CachedFact);
        Assert.IsTrue(rows.Single(row => row.TenantId == Tenant && row.Date == dimension.Date).IsStale);
        Assert.IsTrue(rows.Single(row => row.TenantId == Tenant && row.Date == new DateOnly(2026, 1, 3)).IsStale);
        Assert.AreEqual("other", rows.Single(row => row.TenantId == OtherTenant).CachedFact);
        Assert.IsFalse(rows.Single(row => row.TenantId == OtherTenant).IsStale);
    }

    [TestMethod]
    public async Task SetCache_ConcurrentContextsUpsertTheSameMissingDimension()
    {
        var factory = CreateFactory();
        await using (var setup = factory.CreateDbContext()) await ResetTablesAsync(setup);
        var cache = CreateSetCache(factory);
        var dimension = new PgSetCache.Dimensions(new DateOnly(2026, 2, 1), "east");

        await Task.WhenAll(
            MarkSetInNewContextAsync(cache, factory, dimension),
            MarkSetInNewContextAsync(cache, factory, dimension));

        await using var verify = factory.CreateDbContext();
        Assert.AreEqual(1, await verify.SetCaches.CountAsync(row => row.TenantId == Tenant && row.Date == dimension.Date && row.Region == dimension.Region));
    }

    [TestMethod]
    public async Task SetCache_UsesCallerTransactionAndDoesNotCommitIt()
    {
        var factory = CreateFactory();
        await using var setup = factory.CreateDbContext();
        await ResetTablesAsync(setup);
        await using var transaction = await setup.Database.BeginTransactionAsync();
        var dimension = new PgSetCache.Dimensions(new DateOnly(2026, 3, 1), "west");
        var cache = CreateSetCache(factory);

        await cache.MarkStaleDimensionsAsync(setup, Tenant, [dimension]);
        Assert.IsNotNull(setup.Database.CurrentTransaction);
        Assert.AreEqual(1, await setup.SetCaches.CountAsync(row => row.TenantId == Tenant && row.Date == dimension.Date));
        await transaction.RollbackAsync();

        await using var verify = factory.CreateDbContext();
        Assert.AreEqual(0, await verify.SetCaches.CountAsync(row => row.TenantId == Tenant && row.Date == dimension.Date));
    }

    private static async Task MarkRowInNewContextAsync(PgRowCacheImpl cache, IDbContextFactory<PgTestContext> factory, int key)
    {
        await using var db = factory.CreateDbContext();
        await cache.MarkStaleKeysAsync(db, Tenant, [key]);
    }

    private static async Task MarkSetInNewContextAsync(PgSetCacheImpl cache, IDbContextFactory<PgTestContext> factory, PgSetCache.Dimensions dimension)
    {
        await using var db = factory.CreateDbContext();
        await cache.MarkStaleDimensionsAsync(db, Tenant, [dimension]);
    }

    private static PgRowCacheImpl CreateRowCache(IDbContextFactory<PgTestContext> factory) =>
        new(NullLogger<PgRowCacheImpl>.Instance, factory);

    private static PgSetCacheImpl CreateSetCache(IDbContextFactory<PgTestContext> factory) =>
        new(NullLogger<PgSetCacheImpl>.Instance, factory);

    private static PgTestContextFactory CreateFactory()
    {
        var connectionString = Environment.GetEnvironmentVariable("MATERIALIZR_POSTGRESQL");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("Set MATERIALIZR_POSTGRESQL to a disposable PostgreSQL database to run integration tests.");
        }

        var options = new DbContextOptionsBuilder<PgTestContext>().UseNpgsql(connectionString).Options;
        return new PgTestContextFactory(options);
    }

    private static Task ResetTablesAsync(PgTestContext db) =>
        db.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "source rows";
            DROP TABLE IF EXISTS "set cache";
            CREATE SCHEMA IF NOT EXISTS "cache schema";
            DROP TABLE IF EXISTS "cache schema"."row cache";
            CREATE TABLE "cache schema"."row cache" (
                "tenant id" integer NOT NULL,
                "cache key" integer NOT NULL CHECK ("cache key" <> -99),
                "cached fact" text NOT NULL,
                "stale flag" boolean NOT NULL,
                "entity kind" text NOT NULL,
                "special fact" text NULL,
                CONSTRAINT "CK_special_fact" CHECK ("entity kind" <> 'special' OR "special fact" IS NOT NULL),
                CONSTRAINT "PK_row cache" PRIMARY KEY ("tenant id", "cache key")
            );
            CREATE TABLE "set cache" (
                "tenant id" integer NOT NULL,
                "cache date" date NOT NULL,
                "region code" text NOT NULL,
                "cached fact" text NOT NULL,
                "stale flag" boolean NOT NULL,
                CONSTRAINT "PK_set cache" PRIMARY KEY ("tenant id", "cache date", "region code")
            );
            CREATE TABLE "source rows" (
                "id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "tenant id" integer NOT NULL,
                "cache key" integer NOT NULL
            );
            """);

    private sealed class PgTestContextFactory(DbContextOptions<PgTestContext> options) : IDbContextFactory<PgTestContext>
    {
        public PgTestContext CreateDbContext() => new(options);
    }

    private sealed class PgTestContext(DbContextOptions<PgTestContext> options) : DbContext(options)
    {
        public DbSet<PgRowCache> RowCaches => Set<PgRowCache>();
        public DbSet<PgSetCache> SetCaches => Set<PgSetCache>();
        public DbSet<PgSource> Sources => Set<PgSource>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PgRowCache>(entity =>
            {
                entity.ToTable("row cache", "cache schema");
                entity.HasKey(row => new { row.TenantId, row.CacheKey });
                entity.Property(row => row.TenantId).HasColumnName("tenant id");
                entity.Property(row => row.CacheKey).HasColumnName("cache key");
                entity.Property(row => row.CachedFact).HasColumnName("cached fact");
                entity.Property(row => row.IsStale).HasColumnName("stale flag");
                entity.HasDiscriminator<string>("entity kind")
                    .HasValue<PgRowCache>("row")
                    .HasValue<PgSpecialRowCache>("special");
                entity.Property<string>("entity kind").HasColumnName("entity kind");
            });
            modelBuilder.Entity<PgSpecialRowCache>().Property(row => row.SpecialFact).HasColumnName("special fact");
            modelBuilder.Entity<PgSetCache>(entity =>
            {
                entity.ToTable("set cache");
                entity.HasKey(row => new { row.TenantId, row.Date, row.Region });
                entity.Property(row => row.TenantId).HasColumnName("tenant id");
                entity.Property(row => row.Date).HasColumnName("cache date");
                entity.Property(row => row.Region).HasColumnName("region code");
                entity.Property(row => row.CachedFact).HasColumnName("cached fact");
                entity.Property(row => row.IsStale).HasColumnName("stale flag");
            });
            modelBuilder.Entity<PgSource>(entity =>
            {
                entity.ToTable("source rows");
                entity.HasKey(source => source.Id);
                entity.Property(source => source.Id).HasColumnName("id");
                entity.Property(source => source.TenantId).HasColumnName("tenant id");
                entity.Property(source => source.CacheKey).HasColumnName("cache key");
            });
        }
    }

    private class PgRowCache : ITenantCacheEntity<int>
    {
        public int TenantId { get; set; }
        public int CacheKey { get; set; }
        public string CachedFact { get; set; } = "placeholder";
        public bool IsStale { get; set; } = true;
    }

    private sealed class PgSpecialRowCache : PgRowCache
    {
        public string? SpecialFact { get; set; } = "special placeholder";
    }

    private sealed class PgSetCache : ITenantCacheEntity<int>
    {
        public record Dimensions(DateOnly Date, string Region);

        public int TenantId { get; set; }
        public DateOnly Date { get; set; }
        public string Region { get; set; } = "";
        public string CachedFact { get; set; } = "placeholder";
        public bool IsStale { get; set; } = true;
    }

    private sealed class PgSource
    {
        public int Id { get; set; }
        public int TenantId { get; set; }
        public int CacheKey { get; set; }
    }

    private sealed class PgRowCacheImpl(
        ILogger<PostgreSqlRowCache<PgTestContext, PgRowCache, int, int>> logger,
        IDbContextFactory<PgTestContext> factory)
        : PostgreSqlRowCache<PgTestContext, PgRowCache, int, int>(logger, factory)
    {
        protected override DbSet<PgRowCache> CacheDbSet(PgTestContext db) => db.RowCaches;
        protected override Expression<Func<PgRowCache, int>> KeySelector => row => row.CacheKey;
        protected override PgRowCache FromKey(int key) => new PgSpecialRowCache { CacheKey = key };
        protected override void UpdateFacts(PgRowCache existingRow, PgRowCache incomingRow) => existingRow.CachedFact = incomingRow.CachedFact;
        protected override Task<PgRowCache[]> QueryLiveAsync(PgTestContext dbContext, int tenantId, int[] keys, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<PgRowCache>());
    }

    private sealed class PgSetCacheImpl(
        ILogger<PostgreSqlSetCache<PgTestContext, PgSetCache, PgSetCache.Dimensions, int>> logger,
        IDbContextFactory<PgTestContext> factory)
        : PostgreSqlSetCache<PgTestContext, PgSetCache, PgSetCache.Dimensions, int>(logger, factory)
    {
        protected override DbSet<PgSetCache> CacheDbSet(PgTestContext db) => db.SetCaches;
        protected override Expression<Func<PgSetCache, object?>> DimensionsKeySelector => row => new { row.Date, row.Region };
        protected override PgSetCache FromDimensions(PgSetCache.Dimensions dimensions) => new() { Date = dimensions.Date, Region = dimensions.Region };
        protected override PgSetCache.Dimensions ToDimensions(PgSetCache entity) => new(entity.Date, entity.Region);
        protected override void UpdateFacts(PgSetCache existingRow, PgSetCache incomingRow) => existingRow.CachedFact = incomingRow.CachedFact;
        protected override Task<PgSetCache[]> QueryLiveAsync(PgTestContext dbContext, int tenantId, PgSetCache.Dimensions[] dimensions, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<PgSetCache>());
    }
}
