using MaterializR.Core;
using MaterializR.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Linq.Expressions;
using Testcontainers.PostgreSql;

namespace Testing;

[TestClass]
[DoNotParallelize]
public sealed class PostgreSqlCacheTests
{
    private const int Tenant = 1;
    private const int OtherTenant = 2;
    private static readonly PostgreSqlContainer Database = new PostgreSqlBuilder("postgres:17-alpine").Build();

    [ClassInitialize]
    public static async Task StartDatabaseAsync(TestContext context)
    {
        try
        {
            await Database.StartAsync();
        }
        catch
        {
            await Database.DisposeAsync();
            throw;
        }
    }

    [ClassCleanup]
    public static async Task StopDatabaseAsync() => await Database.DisposeAsync();

    [TestMethod]
    public async Task RowCache_UpsertsDistinctKeysAndPreservesExistingFactsPerTenant()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var existing = new PgRowCache { TenantId = Tenant, CacheKey = 5, CachedFact = "preserved", IsStale = false };
        db.RowCaches.AddRange(
            existing,
            new PgRowCache { TenantId = OtherTenant, CacheKey = 5, CachedFact = "other", IsStale = false });
        await db.SaveChangesAsync();

        var cache = CreateRowCache(factory);
        await cache.MarkStaleKeysAsync(db, Tenant, [5, 5, 6]);

        Assert.IsFalse(existing.IsStale);
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
    public async Task RowCache_RefreshUpsertUpdatesOnlyFactsForTheSelectedTenantWithoutSynchronizingTrackedRows()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var tracked = new PgRowCache { TenantId = Tenant, CacheKey = 5, CachedFact = "old", IsStale = true };
        db.RowCaches.AddRange(
            tracked,
            new PgRowCache { TenantId = OtherTenant, CacheKey = 5, CachedFact = "other", IsStale = true });
        await db.SaveChangesAsync();
        var cache = CreateRowCache(factory);

        await cache.RefreshAsync(db, Tenant,
        [
            new PgRowCache { TenantId = Tenant, CacheKey = 5, CachedFact = "superseded", IsStale = true },
            new PgRowCache { TenantId = Tenant, CacheKey = 5, CachedFact = "refreshed", IsStale = true },
            new PgRowCache { TenantId = Tenant, CacheKey = 6, CachedFact = "inserted", IsStale = true }
        ]);

        Assert.AreEqual("old", tracked.CachedFact);
        Assert.IsTrue(tracked.IsStale);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(tracked).State);
        var rows = await db.RowCaches.AsNoTracking().OrderBy(row => row.TenantId).ThenBy(row => row.CacheKey).ToArrayAsync();
        Assert.AreEqual("refreshed", rows.Single(row => row.TenantId == Tenant && row.CacheKey == 5).CachedFact);
        Assert.IsFalse(rows.Single(row => row.TenantId == Tenant && row.CacheKey == 5).IsStale);
        Assert.AreEqual("inserted", rows.Single(row => row.TenantId == Tenant && row.CacheKey == 6).CachedFact);
        Assert.IsFalse(rows.Single(row => row.TenantId == Tenant && row.CacheKey == 6).IsStale);
        Assert.AreEqual("other", rows.Single(row => row.TenantId == OtherTenant).CachedFact);
        Assert.IsTrue(rows.Single(row => row.TenantId == OtherTenant).IsStale);
    }

    [TestMethod]
    public async Task RowCache_LocksStaleRowsForOneTenantInStableKeyOrderWithoutTracking()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        db.RowCaches.AddRange(
            new PgRowCache { TenantId = Tenant, CacheKey = 9 },
            new PgRowCache { TenantId = Tenant, CacheKey = 3 },
            new PgRowCache { TenantId = Tenant, CacheKey = 4, IsStale = false },
            new PgRowCache { TenantId = OtherTenant, CacheKey = 1 });
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var cache = CreateRowCache(factory);

        var rows = await cache.LockStaleAsync(db, Tenant);

        CollectionAssert.AreEqual(new[] { 3, 9 }, rows.Select(row => row.CacheKey).ToArray());
        Assert.IsTrue(rows.All(row => db.Entry(row).State == EntityState.Detached));
        Assert.IsNotNull(db.Database.CurrentTransaction);
        await transaction.RollbackAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RowCache_UpdateAsyncSerializesConcurrentRefreshOrInvalidation(bool invalidate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var factory = CreateFactory();
        await using var setup = factory.CreateDbContext();
        await ResetTablesAsync(setup);
        setup.RowCaches.AddRange(
            new PgSpecialRowCache { TenantId = Tenant, CacheKey = 5, CachedFact = "old", SpecialFact = "retained subtype fact" },
            new PgRowCache { TenantId = Tenant, CacheKey = 6, CachedFact = "orphan" },
            new PgRowCache { TenantId = Tenant, CacheKey = 7, CachedFact = "fresh", IsStale = false },
            new PgRowCache { TenantId = OtherTenant, CacheKey = 5, CachedFact = "other live" },
            new PgRowCache { TenantId = OtherTenant, CacheKey = 6, CachedFact = "other orphan" });
        await setup.SaveChangesAsync(ct);

        var enteredQuery = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = CreateRowCache(factory);
        cache.LiveQuery = async (db, tenant, keys, token) =>
        {
            Assert.AreEqual(Tenant, tenant);
            CollectionAssert.AreEquivalent(new[] { 5, 6 }, keys);
            enteredQuery.TrySetResult(((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID);
            await releaseQuery.Task.WaitAsync(token);
            return [new PgRowCache { TenantId = tenant, CacheKey = 5, CachedFact = "live fact" }];
        };

        var applicationName = $"materializr-concurrency-{Guid.NewGuid():N}";
        var competingFactory = CreateFactory(applicationName);
        var competingCache = CreateRowCache(competingFactory);
        await using var marker = competingFactory.CreateDbContext();
        var first = cache.UpdateAsync(Tenant, ct);
        Task<int>? second = null;
        try
        {
            var blocker = await enteredQuery.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            second = invalidate
                ? MarkAsync()
                : competingCache.UpdateAsync(Tenant, ct);
            await WaitForBlockedSessionAsync(setup, applicationName, blocker, ct);
            Assert.IsFalse(second.IsCompleted, "The competing operation must wait for the first refresh's row locks.");
            releaseQuery.TrySetResult();

            Assert.AreEqual(2, await first.WaitAsync(TimeSpan.FromSeconds(10), ct));
            Assert.AreEqual(0, await second.WaitAsync(TimeSpan.FromSeconds(10), ct));

            var rows = await setup.RowCaches.AsNoTracking().ToArrayAsync(ct);
            var refreshed = rows.Single(row => row.TenantId == Tenant && row.CacheKey == 5);
            Assert.AreEqual("live fact", refreshed.CachedFact);
            Assert.AreEqual(invalidate, refreshed.IsStale, "A stale mark that waited for refresh must not be lost.");
            Assert.IsInstanceOfType<PgSpecialRowCache>(refreshed);
            Assert.AreEqual("retained subtype fact", ((PgSpecialRowCache)refreshed).SpecialFact);
            var orphan = rows.SingleOrDefault(row => row.TenantId == Tenant && row.CacheKey == 6);
            if (invalidate)
            {
                Assert.IsNotNull(orphan, "Invalidation after orphan deletion must atomically recreate its marker.");
                Assert.AreEqual("placeholder", orphan.CachedFact);
                Assert.IsTrue(orphan.IsStale);
                Assert.IsInstanceOfType<PgSpecialRowCache>(orphan);
            }
            else
            {
                Assert.IsNull(orphan, "Both refreshes must leave a missing live row deleted.");
            }
            var fresh = rows.Single(row => row.TenantId == Tenant && row.CacheKey == 7);
            Assert.AreEqual("fresh", fresh.CachedFact);
            Assert.IsFalse(fresh.IsStale);
            var otherLive = rows.Single(row => row.TenantId == OtherTenant && row.CacheKey == 5);
            var otherOrphan = rows.Single(row => row.TenantId == OtherTenant && row.CacheKey == 6);
            Assert.AreEqual("other live", otherLive.CachedFact);
            Assert.AreEqual("other orphan", otherOrphan.CachedFact);
            Assert.IsTrue(otherLive.IsStale);
            Assert.IsTrue(otherOrphan.IsStale);
            Assert.HasCount(invalidate ? 5 : 4, rows);
        }
        finally
        {
            releaseQuery.TrySetResult();
            await Task.WhenAll(second is null ? [first] : new Task[] { first, second })
                .WaitAsync(TimeSpan.FromSeconds(20));
        }

        async Task<int> MarkAsync()
        {
            await competingCache.MarkStaleKeysAsync(marker, Tenant, [5, 6], ct);
            return 0;
        }
    }

    private static async Task WaitForBlockedSessionAsync(PgTestContext observer, string applicationName, int blocker, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await observer.Database.OpenConnectionAsync(timeout.Token);
        await using var command = observer.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE application_name = @application
                  AND @blocker = ANY(pg_blocking_pids(pid))
                  AND wait_event_type = 'Lock')
            """;
        command.Parameters.Add(new NpgsqlParameter("application", applicationName));
        command.Parameters.Add(new NpgsqlParameter("blocker", blocker));
        while (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
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
    public async Task RowCache_UpsertsDoNotTouchTrackedOrIncomingEntities()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var existing = new PgRowCache { TenantId = Tenant, CacheKey = 14, CachedFact = "database", IsStale = false };
        db.RowCaches.Add(existing);
        await db.SaveChangesAsync();
        existing.CachedFact = "pending edit";
        db.ChangeTracker.DetectChanges();
        var pending = new PgRowCache { TenantId = Tenant, CacheKey = 15, CachedFact = "unpersisted" };
        db.RowCaches.Add(pending);
        var incoming = new PgRowCache { TenantId = OtherTenant, CacheKey = 14, CachedFact = "refreshed", IsStale = true };
        var cache = CreateRowCache(factory);

        await cache.MarkStaleKeysAsync(db, Tenant, [14, 15]);
        await cache.RefreshAsync(db, Tenant, [incoming]);

        Assert.HasCount(2, db.ChangeTracker.Entries<PgRowCache>().ToArray());
        Assert.AreEqual(EntityState.Modified, db.Entry(existing).State);
        Assert.AreEqual("pending edit", existing.CachedFact);
        Assert.AreEqual("database", db.Entry(existing).Property(row => row.CachedFact).OriginalValue);
        Assert.IsTrue(db.Entry(existing).Property(row => row.CachedFact).IsModified);
        Assert.IsFalse(existing.IsStale);
        Assert.AreEqual(EntityState.Added, db.Entry(pending).State);
        Assert.AreEqual("unpersisted", pending.CachedFact);
        Assert.AreEqual(EntityState.Detached, db.Entry(incoming).State);
        Assert.AreEqual(OtherTenant, incoming.TenantId);
        Assert.IsTrue(incoming.IsStale);
        var persisted = await db.RowCaches.AsNoTracking().OrderBy(row => row.CacheKey).ToArrayAsync();
        Assert.HasCount(2, persisted);
        Assert.AreEqual("refreshed", persisted[0].CachedFact);
        Assert.IsFalse(persisted[0].IsStale);
        Assert.AreEqual("placeholder", persisted[1].CachedFact);
        Assert.IsTrue(persisted[1].IsStale);
    }

    [TestMethod]
    public async Task RowCache_UsesConfiguredShadowDefaultWithoutReadingTrackedValue()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var incoming = new PgSpecialRowCache { TenantId = Tenant, CacheKey = 20, CachedFact = "fact" };
        db.RowCaches.Add(incoming);
        db.Entry(incoming).Property<string>("special shadow").CurrentValue = "tracked value";
        var cache = CreateRowCache(factory);

        await cache.RefreshAsync(db, Tenant, [incoming]);

        Assert.AreEqual(EntityState.Added, db.Entry(incoming).State);
        Assert.AreEqual("tracked value", db.Entry(incoming).Property<string>("special shadow").CurrentValue);
        var storedDefault = await db.RowCaches.AsNoTracking().OfType<PgSpecialRowCache>()
            .Where(row => row.CacheKey == 20)
            .Select(row => EF.Property<string>(row, "special shadow")).SingleAsync();
        Assert.AreEqual("configured default", storedDefault);
    }

    [TestMethod]
    public async Task RowCache_RejectsShadowPropertyWithoutConfiguredDefaultWithoutTracking()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var incoming = new PgMissingShadowRow { TenantId = Tenant, CacheKey = 21 };
        var cache = CreateRowCache(factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => cache.RefreshAsync(db, Tenant, [incoming]));

        StringAssert.Contains(exception.Message, "missing shadow");
        Assert.IsEmpty(db.ChangeTracker.Entries());
        Assert.AreEqual(0, await db.RowCaches.AsNoTracking().CountAsync());
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
    public async Task SetCache_RefreshUpsertUpdatesFactsAndKeepsOtherTenantRowsIsolated()
    {
        var factory = CreateFactory();
        await using var db = factory.CreateDbContext();
        await ResetTablesAsync(db);
        var dimension = new PgSetCache.Dimensions(new DateOnly(2026, 1, 2), "north");
        db.SetCaches.AddRange(
            new PgSetCache { TenantId = Tenant, Date = dimension.Date, Region = dimension.Region, CachedFact = "old", IsStale = true },
            new PgSetCache { TenantId = OtherTenant, Date = dimension.Date, Region = dimension.Region, CachedFact = "other", IsStale = true });
        await db.SaveChangesAsync();
        var cache = CreateSetCache(factory);

        await cache.RefreshAsync(db, Tenant,
        [
            new PgSetCache { TenantId = Tenant, Date = dimension.Date, Region = dimension.Region, CachedFact = "superseded", IsStale = true },
            new PgSetCache { TenantId = Tenant, Date = dimension.Date, Region = dimension.Region, CachedFact = "refreshed", IsStale = true },
            new PgSetCache { TenantId = Tenant, Date = new DateOnly(2026, 1, 3), Region = "south", CachedFact = "inserted", IsStale = true }
        ]);

        var rows = await db.SetCaches.AsNoTracking().OrderBy(row => row.TenantId).ThenBy(row => row.Date).ToArrayAsync();
        Assert.AreEqual("refreshed", rows.Single(row => row.TenantId == Tenant && row.Date == dimension.Date).CachedFact);
        Assert.IsFalse(rows.Single(row => row.TenantId == Tenant && row.Date == dimension.Date).IsStale);
        Assert.AreEqual("inserted", rows.Single(row => row.TenantId == Tenant && row.Date == new DateOnly(2026, 1, 3)).CachedFact);
        Assert.IsFalse(rows.Single(row => row.TenantId == Tenant && row.Date == new DateOnly(2026, 1, 3)).IsStale);
        Assert.AreEqual("other", rows.Single(row => row.TenantId == OtherTenant).CachedFact);
        Assert.IsTrue(rows.Single(row => row.TenantId == OtherTenant).IsStale);
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

    private static PgTestContextFactory CreateFactory(string? applicationName = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(Database.GetConnectionString());
        if (applicationName is not null)
        {
            builder.ApplicationName = applicationName;
            builder.Options = "-c lock_timeout=15000";
        }
        var options = new DbContextOptionsBuilder<PgTestContext>().UseNpgsql(builder.ConnectionString).Options;
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
                "special shadow" text NULL,
                "missing shadow" text NULL,
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
                    .HasValue<PgSpecialRowCache>("special")
                    .HasValue<PgMissingShadowRow>("missing");
                entity.Property<string>("entity kind").HasColumnName("entity kind");
            });
            modelBuilder.Entity<PgSpecialRowCache>().Property(row => row.SpecialFact).HasColumnName("special fact");
            modelBuilder.Entity<PgSpecialRowCache>().Property<string>("special shadow").HasColumnName("special shadow").HasDefaultValue("configured default").ValueGeneratedNever();
            modelBuilder.Entity<PgMissingShadowRow>().Property<string>("missing shadow").HasColumnName("missing shadow");
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

    private sealed class PgMissingShadowRow : PgRowCache;

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
        protected override Expression<Func<PgRowCache, object?>> FactProperties => row => row.CachedFact;
        protected override PgRowCache FromKey(int key) => new PgSpecialRowCache { CacheKey = key };
        public Func<PgTestContext, int, int[], CancellationToken, Task<PgRowCache[]>>? LiveQuery { get; set; }

        protected override Task<PgRowCache[]> QueryLiveAsync(PgTestContext dbContext, int tenantId, int[] keys, CancellationToken cancellationToken) =>
            LiveQuery?.Invoke(dbContext, tenantId, keys, cancellationToken) ?? Task.FromResult(Array.Empty<PgRowCache>());

        public Task RefreshAsync(PgTestContext dbContext, int tenantId, PgRowCache[] rows, CancellationToken cancellationToken = default) =>
            UpsertFactsAsync(dbContext, tenantId, rows, cancellationToken);

        public Task<PgRowCache[]> LockStaleAsync(PgTestContext dbContext, int tenantId, CancellationToken cancellationToken = default) =>
            LockStaleRowsAsync(dbContext, tenantId, cancellationToken);
    }

    private sealed class PgSetCacheImpl(
        ILogger<PostgreSqlSetCache<PgTestContext, PgSetCache, PgSetCache.Dimensions, int>> logger,
        IDbContextFactory<PgTestContext> factory)
        : PostgreSqlSetCache<PgTestContext, PgSetCache, PgSetCache.Dimensions, int>(logger, factory)
    {
        protected override DbSet<PgSetCache> CacheDbSet(PgTestContext db) => db.SetCaches;
        protected override Expression<Func<PgSetCache, object?>> DimensionsKeySelector => row => new { row.Date, row.Region };
        protected override Expression<Func<PgSetCache, object?>> FactProperties => row => row.CachedFact;
        protected override PgSetCache FromDimensions(PgSetCache.Dimensions dimensions) => new() { Date = dimensions.Date, Region = dimensions.Region };
        protected override PgSetCache.Dimensions ToDimensions(PgSetCache entity) => new(entity.Date, entity.Region);
        public Task RefreshAsync(PgTestContext dbContext, int tenantId, PgSetCache[] rows, CancellationToken cancellationToken = default) =>
            UpsertFactsAsync(dbContext, tenantId, rows, cancellationToken);
        protected override Task<PgSetCache[]> QueryLiveAsync(PgTestContext dbContext, int tenantId, PgSetCache.Dimensions[] dimensions, CancellationToken cancellationToken) => Task.FromResult(Array.Empty<PgSetCache>());
    }
}
