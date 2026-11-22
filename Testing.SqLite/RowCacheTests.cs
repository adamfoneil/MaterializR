using Demo;
using Demo.Cache;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Testing;

[TestClass]
[DoNotParallelize]
public sealed class RowCacheTests
{
    private const int Tenant = 1;
    private const int OtherTenant = 2;

    [TestMethod]
    public async Task SaveChanges_WithNewAppointmentAndItems_TracksGeneratedKeyAndLaterItemInsert()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);

        await db.SaveChangesAsync(Tenant);

        Assert.AreNotEqual(0, appointment.Id);
        var created = await db.AppointmentPrices.AsNoTracking().SingleAsync();
        Assert.AreEqual(appointment.Id, created.AppointmentId);
        Assert.IsTrue(created.IsStale);

        await cache.UpdateAsync(Tenant);
        db.AppointmentItems.Add(new AppointmentItem
        {
            AppointmentId = appointment.Id,
            ItemId = 2,
            Quantity = 1,
            UnitPrice = 7m
        });
        await db.SaveChangesAsync(Tenant);

        var marked = await db.AppointmentPrices.AsNoTracking().SingleAsync();
        Assert.AreEqual(appointment.Id, marked.AppointmentId);
        Assert.IsTrue(marked.IsStale);
        await cache.UpdateAsync(Tenant);
        var refreshed = await db.AppointmentPrices.AsNoTracking().SingleAsync();
        Assert.AreEqual(27m, refreshed.TotalPrice);
        Assert.IsFalse(refreshed.IsStale);
    }

    [TestMethod]
    public async Task InsertedRows_PopulateAppointmentPriceCache()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = new Appointment
        {
            TenantId = Tenant,
            Date = DateOnly.FromDateTime(DateTime.Today),
            Time = new TimeOnly(9, 0),
            TransportId = 1,
            Points = 3,
            ClientId = 100,
            Status = AppointmentStatus.Scheduled,
            Items =
            [
                new AppointmentItem { ItemId = 1, Quantity = 2, UnitPrice = 10m },
                new AppointmentItem { ItemId = 2, Quantity = 1, UnitPrice = 7.5m }
            ]
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);
        await cache.UpdateAsync(Tenant);

        await AssertCacheMatchesLiveAsync(db, Tenant);
    }

    [TestMethod]
    public async Task UpdatedRows_RefreshExistingAppointmentPrice()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = new Appointment
        {
            TenantId = Tenant,
            Date = DateOnly.FromDateTime(DateTime.Today),
            Time = new TimeOnly(10, 30),
            TransportId = 2,
            Points = 4,
            ClientId = 200,
            Status = AppointmentStatus.Scheduled,
            Items =
            [
                new AppointmentItem { ItemId = 5, Quantity = 1, UnitPrice = 12m }
            ]
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);
        await cache.UpdateAsync(Tenant);

        await db.AppointmentItems
            .Where(i => i.AppointmentId == appointment.Id && i.ItemId == 5)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.Quantity, 3)
                .SetProperty(i => i.UnitPrice, 15m));

        await db.AppointmentPrices
            .Where(p => p.AppointmentId == appointment.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.IsStale, true));

        await cache.UpdateAsync(Tenant);

        await AssertCacheMatchesLiveAsync(db, Tenant);
    }

    [TestMethod]
    public async Task DeletedRows_RefreshExistingAppointmentPrice()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = new Appointment
        {
            TenantId = Tenant,
            Date = DateOnly.FromDateTime(DateTime.Today),
            Time = new TimeOnly(8, 0),
            TransportId = 3,
            Points = 2,
            ClientId = 300,
            Status = AppointmentStatus.Scheduled,
            Items =
            [
                new AppointmentItem { ItemId = 10, Quantity = 1, UnitPrice = 20m },
                new AppointmentItem { ItemId = 12, Quantity = 3, UnitPrice = 5m }
            ]
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);
        await cache.UpdateAsync(Tenant);

        await db.AppointmentItems
            .Where(i => i.AppointmentId == appointment.Id && i.ItemId == 12)
            .ExecuteDeleteAsync();

        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);

        await cache.UpdateAsync(Tenant);

        await AssertCacheMatchesLiveAsync(db, Tenant);
    }

    [TestMethod]
    public async Task UpdateAsync_OnlyRefreshesRequestedTenant()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var mine = NewAppointment(Tenant, 10m);
        var theirs = NewAppointment(OtherTenant, 99m);
        db.Appointments.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [mine.Id]);
        await cache.MarkStaleKeysAsync(db, OtherTenant, [theirs.Id]);

        await cache.UpdateAsync(Tenant);

        await AssertCacheMatchesLiveAsync(db, Tenant);

        var other = await db.AppointmentPrices.AsNoTracking().SingleAsync(p => p.TenantId == OtherTenant);
        Assert.IsTrue(other.IsStale);
        Assert.AreEqual(0m, other.TotalPrice);

        await cache.UpdateAsync(OtherTenant);

        await AssertCacheMatchesLiveAsync(db, OtherTenant);
        await AssertCacheMatchesLiveAsync(db, Tenant);
    }

    [TestMethod]
    public async Task MarkStaleKeys_DoesNotTouchOtherTenantRows()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var mine = NewAppointment(Tenant, 10m);
        var theirs = NewAppointment(OtherTenant, 99m);
        db.Appointments.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [mine.Id]);
        await cache.MarkStaleKeysAsync(db, OtherTenant, [theirs.Id]);
        await cache.UpdateAsync(Tenant);
        await cache.UpdateAsync(OtherTenant);

        await cache.MarkStaleKeysAsync(db, Tenant, [mine.Id]);

        var rows = await db.AppointmentPrices.AsNoTracking().ToArrayAsync();
        Assert.IsTrue(rows.Single(r => r.TenantId == Tenant && r.AppointmentId == mine.Id).IsStale);
        Assert.IsFalse(rows.Single(r => r.TenantId == OtherTenant).IsStale);
    }

    [TestMethod]
    public async Task MarkAndUpdateWhere_UpsertsOnlyMatchingRowsForTenant()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var existing = NewAppointment(Tenant, 10m);
        var fresh = NewAppointment(Tenant, 20m);
        var outOfWindow = NewAppointment(Tenant, 30m);
        outOfWindow.Date = today.AddDays(10);
        var theirs = NewAppointment(OtherTenant, 99m);
        db.Appointments.AddRange(existing, fresh, outOfWindow, theirs);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [existing.Id]);
        await cache.UpdateAsync(Tenant);

        await cache.MarkStaleWhereAsync(db, Tenant,
            db.Appointments.Where(a => a.TenantId == Tenant && a.Date == today), a => a.Id);

        var rows = await db.AppointmentPrices.AsNoTracking().Where(p => p.TenantId == Tenant).ToArrayAsync();
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows.All(r => r.IsStale));
        Assert.AreEqual(0m, rows.Single(r => r.AppointmentId == fresh.Id).TotalPrice);

        await cache.MarkAndUpdateWhereAsync(db, Tenant,
            db.Appointments.Where(a => a.TenantId == Tenant && a.Date == today), a => a.Id);

        var after = await db.AppointmentPrices.AsNoTracking().Where(p => p.TenantId == Tenant).ToArrayAsync();
        Assert.HasCount(2, after);
        Assert.IsTrue(after.All(r => !r.IsStale));
        Assert.AreEqual(40m, after.Single(r => r.AppointmentId == fresh.Id).TotalPrice);
        Assert.IsFalse(after.Any(r => r.AppointmentId == outOfWindow.Id));
        Assert.IsFalse(await db.AppointmentPrices.AnyAsync(p => p.TenantId == OtherTenant));
    }

    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    public async Task UpdateAsync_PersistsFactsRegardlessOfTrackingConfiguration(bool noTracking, bool autoDetectChanges)
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var logger = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AppointmentPriceRowCache>>();
        var cache = new AppointmentPriceRowCache(logger, new ConfiguredContextFactory(factory, noTracking, autoDetectChanges));
        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);
        Assert.AreEqual(1, await cache.UpdateAsync(Tenant));

        var initial = await db.AppointmentPrices.AsNoTracking().SingleAsync();
        Assert.AreEqual(20m, initial.TotalPrice);
        Assert.IsFalse(initial.IsStale);

        await db.AppointmentItems.Where(item => item.AppointmentId == appointment.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UnitPrice, 25m));
        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);
        Assert.AreEqual(1, await cache.UpdateAsync(Tenant));

        var refreshed = await db.AppointmentPrices.AsNoTracking().SingleAsync();
        Assert.AreEqual(50m, refreshed.TotalPrice);
        Assert.IsFalse(refreshed.IsStale);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpdateAsync_UsesSuppliedContextAndRespectsTransaction(bool existingTransaction)
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppointmentPriceRowCache>>();
        var cache = new AppointmentPriceRowCache(logger, new ThrowingContextFactory());
        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);

        await using var transaction = existingTransaction ? await db.Database.BeginTransactionAsync() : null;
        await db.AppointmentItems.Where(item => item.AppointmentId == appointment.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UnitPrice, 25m));
        Assert.AreEqual(1, await cache.UpdateAsync(db, Tenant));
        Assert.AreEqual(50m, cache.UpdatedRows[appointment.Id].TotalPrice);
        Assert.AreEqual(0, await cache.UpdateAsync(db, Tenant));
        Assert.AreSame(transaction, db.Database.CurrentTransaction);
        Assert.AreEqual(50m, (await db.AppointmentPrices.AsNoTracking().SingleAsync()).TotalPrice);

        if (transaction is not null)
        {
            await transaction.RollbackAsync();
            var row = await db.AppointmentPrices.AsNoTracking().SingleAsync();
            Assert.IsTrue(row.IsStale);
            Assert.AreEqual(0m, row.TotalPrice);
        }
        else
        {
            Assert.IsFalse((await db.AppointmentPrices.AsNoTracking().SingleAsync()).IsStale);
        }
    }

    [TestMethod]
    public async Task MarkAndUpdateWhere_UsesSuppliedTransaction()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppointmentPriceRowCache>>();
        var cache = new AppointmentPriceRowCache(logger, new ThrowingContextFactory());

        await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.AreEqual(1, await cache.MarkAndUpdateWhereAsync(db, Tenant,
            db.Appointments.Where(a => a.TenantId == Tenant), a => a.Id));
        Assert.AreSame(transaction, db.Database.CurrentTransaction);
        var row = await db.AppointmentPrices.AsNoTracking().SingleAsync();
        Assert.IsFalse(row.IsStale);
        Assert.AreEqual(20m, row.TotalPrice);
        await transaction.RollbackAsync();
        Assert.AreEqual(0, await db.AppointmentPrices.CountAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpdateAsync_FailureRollsBackOnlyOwnedTransaction(bool existingTransaction)
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppointmentPriceRowCache>>();
        var cache = new FailingAppointmentPriceRowCache(logger, new ThrowingContextFactory());
        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);

        await using var transaction = existingTransaction ? await db.Database.BeginTransactionAsync() : null;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cache.UpdateAsync(db, Tenant));
        Assert.AreSame(transaction, db.Database.CurrentTransaction);
        var row = await db.AppointmentPrices.AsNoTracking().SingleAsync();
        Assert.AreEqual(existingTransaction ? 20m : 0m, row.TotalPrice);
        Assert.AreEqual(!existingTransaction, row.IsStale);
        if (transaction is not null) await transaction.RollbackAsync();
        Assert.IsTrue((await db.AppointmentPrices.AsNoTracking().SingleAsync()).IsStale);
    }

    private sealed class ThrowingContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => throw new InvalidOperationException("Unexpected context creation");
    }

    private sealed class FailingAppointmentPriceRowCache(
        ILogger<AppointmentPriceRowCache> logger, IDbContextFactory<ApplicationDbContext> factory)
        : AppointmentPriceRowCache(logger, factory)
    {
        protected override async Task UpsertFactsAsync(ApplicationDbContext db, int tenantId, AppointmentPriceCache[] rows, CancellationToken cancellationToken)
        {
            await base.UpsertFactsAsync(db, tenantId, rows, cancellationToken);
            throw new InvalidOperationException("Refresh failed");
        }
    }

    [TestMethod]
    public async Task UpsertFacts_ForeignKeyFailurePropagatesWithoutTrackingRows()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var logger = new RecordingLogger(LogLevel.Error);
        var cache = new TestableAppointmentPriceRowCache(logger, factory);

        await Assert.ThrowsExactlyAsync<SqliteException>(() =>
            cache.RefreshForTestAsync(db, Tenant, [new AppointmentPriceCache { AppointmentId = 12345, TotalPrice = 42m }]));

        Assert.AreEqual(0, db.ChangeTracker.Entries<AppointmentPriceCache>().Count());
        Assert.AreEqual(0, await db.AppointmentPrices.CountAsync());
    }

    [TestMethod]
    public async Task Upserts_DoNotModifyTrackedRowsOrSaveUnrelatedChanges()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        var tracked = new AppointmentPriceCache { TenantId = Tenant, AppointmentId = appointment.Id, TotalPrice = 7m, IsStale = false };
        db.AppointmentPrices.Add(tracked);
        await db.SaveChangesAsync();
        var unsaved = NewAppointment(Tenant, 30m);
        db.Appointments.Add(unsaved);
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var cache = new TestableAppointmentPriceRowCache(new RecordingLogger(LogLevel.Error), factory);

        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id, appointment.Id]);
        Assert.IsFalse(tracked.IsStale);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(tracked).State);
        var marked = await db.AppointmentPrices.AsNoTracking().SingleAsync();
        Assert.IsTrue(marked.IsStale);
        Assert.AreEqual(7m, marked.TotalPrice);

        await cache.RefreshForTestAsync(db, Tenant,
        [
            new AppointmentPriceCache { AppointmentId = appointment.Id, TotalPrice = 11m },
            new AppointmentPriceCache { AppointmentId = appointment.Id, TotalPrice = 42m }
        ]);
        var refreshed = await db.AppointmentPrices.AsNoTracking().SingleAsync();
        Assert.AreEqual(42m, refreshed.TotalPrice);
        Assert.IsFalse(refreshed.IsStale);
        Assert.AreEqual(7m, tracked.TotalPrice);
        Assert.IsFalse(tracked.IsStale);
        Assert.AreEqual(EntityState.Unchanged, db.Entry(tracked).State);
        Assert.AreEqual(EntityState.Added, db.Entry(unsaved).State);
        Assert.AreEqual(1, await db.Appointments.AsNoTracking().CountAsync());
    }

    [TestMethod]
    public async Task UpsertFacts_LaterFailureRollsBackEarlierRows()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var cache = new TestableAppointmentPriceRowCache(new RecordingLogger(LogLevel.Error), factory);

        await Assert.ThrowsExactlyAsync<SqliteException>(() => cache.RefreshForTestAsync(db, Tenant,
        [
            new AppointmentPriceCache { AppointmentId = appointment.Id, TotalPrice = 20m },
            new AppointmentPriceCache { AppointmentId = 12345, TotalPrice = 42m }
        ]));

        Assert.AreEqual(0, await db.AppointmentPrices.CountAsync());
    }

    [TestMethod]
    public async Task MarkStale_ConcurrentContextsUseOneCacheKey()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var setup = await InitDbAsync(scope);
        var appointment = NewAppointment(Tenant, 10m);
        setup.Appointments.Add(appointment);
        await setup.SaveChangesAsync();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();

        async Task MarkAsync()
        {
            await using var db = factory.CreateDbContext();
            await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);
        }

        await Task.WhenAll(MarkAsync(), MarkAsync());

        var rows = await setup.AppointmentPrices.AsNoTracking().ToArrayAsync();
        Assert.HasCount(1, rows);
        Assert.AreEqual(appointment.Id, rows[0].AppointmentId);
        Assert.IsTrue(rows[0].IsStale);
    }

    private sealed class TestableAppointmentPriceRowCache(
        ILogger<AppointmentPriceRowCache> logger, IDbContextFactory<ApplicationDbContext> factory)
        : AppointmentPriceRowCache(logger, factory)
    {
        public Task RefreshForTestAsync(ApplicationDbContext db, int tenantId, AppointmentPriceCache[] rows) =>
            UpsertFactsAsync(db, tenantId, rows, CancellationToken.None);
    }

    private sealed class RecordingLogger(LogLevel minimumLevel) : ILogger<AppointmentPriceRowCache>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class ConfiguredContextFactory(
        IDbContextFactory<ApplicationDbContext> inner, bool noTracking, bool autoDetectChanges) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext()
        {
            var db = inner.CreateDbContext();
            db.ChangeTracker.QueryTrackingBehavior = noTracking ? QueryTrackingBehavior.NoTracking : QueryTrackingBehavior.TrackAll;
            db.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
            return db;
        }
    }

    [TestMethod]
    public async Task GetAsync_RefreshesStaleAppointmentPrice()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);

        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();

        var cache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await cache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);

        var result = await cache.GetAsync(Tenant, appointment.Id);

        Assert.IsNotNull(result);
        Assert.AreEqual(20m, result.TotalPrice);
        Assert.IsFalse(result.IsStale);

        var persisted = await db.AppointmentPrices.AsNoTracking().SingleAsync(p => p.TenantId == Tenant);
        Assert.AreEqual(20m, persisted.TotalPrice);
        Assert.IsFalse(persisted.IsStale);
    }

    [TestMethod]
    public async Task UpdateAsync_RemovesOnlySelectedRowsWithoutLiveData()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var mine = NewAppointment(Tenant, 10m);
        var theirs = NewAppointment(OtherTenant, 20m);
        db.Appointments.AddRange(mine, theirs);
        await db.SaveChangesAsync();
        var normalCache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await normalCache.MarkStaleKeysAsync(db, Tenant, [mine.Id]);
        await normalCache.MarkStaleKeysAsync(db, OtherTenant, [theirs.Id]);
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppointmentPriceRowCache>>();
        var cache = new MissingLiveAppointmentPriceRowCache(logger, factory);

        Assert.AreEqual(1, await cache.UpdateAsync(Tenant));

        var rows = await db.AppointmentPrices.AsNoTracking().ToArrayAsync();
        Assert.HasCount(1, rows);
        Assert.AreEqual(OtherTenant, rows[0].TenantId);
        Assert.IsTrue(rows[0].IsStale);
    }

    [TestMethod]
    public async Task GetAsync_LeavesRowStaleWhenLiveDataIsMissing()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        await using var db = await InitDbAsync(scope);
        var appointment = NewAppointment(Tenant, 10m);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        var normalCache = scope.ServiceProvider.GetRequiredService<AppointmentPriceRowCache>();
        await normalCache.MarkStaleKeysAsync(db, Tenant, [appointment.Id]);
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var logger = new RecordingLogger(LogLevel.Warning);
        var cache = new MissingLiveAppointmentPriceRowCache(logger, factory);

        var result = await cache.GetAsync(Tenant, appointment.Id);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsStale);
        Assert.IsTrue((await db.AppointmentPrices.AsNoTracking().SingleAsync()).IsStale);
        Assert.AreEqual(LogLevel.Warning, logger.Entries.Single().Level);
    }

    private sealed class MissingLiveAppointmentPriceRowCache(
        ILogger<AppointmentPriceRowCache> logger, IDbContextFactory<ApplicationDbContext> factory)
        : AppointmentPriceRowCache(logger, factory)
    {
        protected override Task<AppointmentPriceCache[]> QueryLiveAsync(ApplicationDbContext dbContext, int tenantId, int[] keys, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<AppointmentPriceCache>());
    }

    private static Appointment NewAppointment(int tenantId, decimal unitPrice) => new()
    {
        TenantId = tenantId,
        Date = DateOnly.FromDateTime(DateTime.Today),
        Time = new TimeOnly(9, 0),
        TransportId = 1,
        Points = 3,
        ClientId = 100,
        Status = AppointmentStatus.Scheduled,
        Items = [new AppointmentItem { ItemId = 1, Quantity = 2, UnitPrice = unitPrice }]
    };

    private static async Task<ApplicationDbContext> InitDbAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task AssertCacheMatchesLiveAsync(ApplicationDbContext db, int tenantId)
    {
        var appointmentIds = await db.Appointments
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => a.Id)
            .OrderBy(id => id)
            .ToArrayAsync();

        var totalsByAppointment = (await db.AppointmentItems
            .AsNoTracking()
            .Where(i => i.Appointment!.TenantId == tenantId)
            .GroupBy(i => i.AppointmentId)
            .Select(g => new
            {
                AppointmentId = g.Key,
                TotalPrice = g.Sum(i => i.UnitPrice * i.Quantity)
            })
            .ToArrayAsync())
            .ToDictionary(i => i.AppointmentId, i => i.TotalPrice);

        var expected = appointmentIds
            .Select(id => new
            {
                AppointmentId = id,
                TotalPrice = totalsByAppointment.GetValueOrDefault(id, 0m)
            })
            .ToArray();

        var actual = await db.AppointmentPrices
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.AppointmentId)
            .ToArrayAsync();

        Assert.HasCount(expected.Length, actual);

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].AppointmentId, actual[i].AppointmentId);
            Assert.AreEqual(expected[i].TotalPrice, actual[i].TotalPrice);
            Assert.IsFalse(actual[i].IsStale);
        }
    }

}
