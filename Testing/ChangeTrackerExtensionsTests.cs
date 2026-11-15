using Demo;
using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Testing;

[TestClass]
public sealed class ChangeTrackerExtensionsTests
{
    [TestMethod]
    public void GetTouchedKeys_UsesSelectorFlagAndFiltersDefaultAndDuplicatePairs()
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
            Appointment a => (a.Id is 1 or 2, a.Id),
            AppointmentItem item => (false, item.AppointmentId),
            _ => (false, 0)
        });

        CollectionAssert.AreEquivalent(
            new[] { (true, 1), (false, 1), (true, 2), (false, 3) }, keys);
    }

    [TestMethod]
    public void KeyHelpers_UseTrackedStateAndRelatedKeys()
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
            Appointment a => db.ChangeTracker.InsertedKey(a, x => x.Id),
            AppointmentItem i => db.ChangeTracker.RelatedKey(i, x => x.AppointmentId),
            _ => (false, 0)
        });

        CollectionAssert.AreEquivalent(new[] { (true, 1), (false, 2), (false, 3) }, keys);
    }
}
