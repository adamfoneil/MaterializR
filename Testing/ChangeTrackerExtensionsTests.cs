using Demo;
using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Testing;

[TestClass]
public sealed class ChangeTrackerExtensionsTests
{
    [TestMethod]
    public void GetTouchedKeys_FiltersDefaultAndDuplicateKeys()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

        var added = new Appointment { Id = 1 };
        var modified = new Appointment { Id = 2 };
        var deleted = new Appointment { Id = 3 };
        var unchanged = new Appointment { Id = 4 };
        db.AttachRange(modified, deleted, unchanged);
        db.Entry(modified).State = EntityState.Modified;
        db.Entry(deleted).State = EntityState.Deleted;
        db.Add(added);
        db.AddRange(
            new AppointmentItem { Id = 10, AppointmentId = 1 },
            new AppointmentItem { Id = 11, AppointmentId = 1 },
            new AppointmentItem { Id = 12, AppointmentId = 0 });

        var keys = db.ChangeTracker.GetTouchedKeys(entity => entity switch
        {
            Appointment a => a.Id,
            AppointmentItem item => item.AppointmentId,
            _ => 0
        });

        CollectionAssert.AreEquivalent(
            new[] { 1, 2, 3 }, keys);
    }

    [TestMethod]
    public void GetTouchedKeys_UsesValuesFromTrackedEntities()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        var added = new Appointment { Id = 1 };
        var modified = new Appointment { Id = 2 };
        var deleted = new Appointment { Id = 3 };
        var item = new AppointmentItem { Id = 10, AppointmentId = 2 };
        db.AddRange(added, item);
        db.AttachRange(modified, deleted);
        db.Entry(modified).State = EntityState.Modified;
        db.Entry(deleted).State = EntityState.Deleted;

        var keys = db.ChangeTracker.GetTouchedKeys(entity => entity switch
        {
            Appointment a => a.Id,
            AppointmentItem i => i.AppointmentId,
            _ => 0
        });

        CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, keys);
    }

    [TestMethod]
    public void GetTouchedDimensions_ReturnsDistinctCurrentAndOriginalDimensions()
    {
        using var scope = TestServiceProvider.Services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();
        var oldDate = new DateOnly(2026, 1, 1);
        var newDate = new DateOnly(2026, 1, 2);
        var modified = new Appointment { Id = 1, Date = oldDate, TransportId = 3 };
        var added = new Appointment { Id = 2, Date = newDate, TransportId = 3 };
        db.Attach(modified);
        db.Entry(modified).Property(appointment => appointment.Date).CurrentValue = newDate;
        db.Entry(modified).Property(appointment => appointment.Date).IsModified = true;
        db.Add(added);

        var dimensions = db.ChangeTracker.GetTouchedDimensions<Appointment, PointsCache.Dimensions>(
            appointment => new(appointment.Date, appointment.TransportId ?? 0));

        CollectionAssert.AreEquivalent(
            new[] { new PointsCache.Dimensions(oldDate, 3), new PointsCache.Dimensions(newDate, 3) },
            dimensions);
    }
}
