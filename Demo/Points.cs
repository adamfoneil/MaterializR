using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace Demo;

internal class Points : IViewEntity<Points.Dimensions>
{
    // dimensions

    public int LocationId { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>
    /// 0 = clinic, any other value is transport
    /// </summary>
    public int ClientId { get; set; }

    // you'll need a type that represents the unique index of the cache/view table
    public record Dimensions(int LocationId, DateOnly Date, int ClientId);

    // facts

    public int Maximum { get; set; }
    public int Scheduled { get; set; }    
    public int Available { get; set; }

    public bool IsModified { get; set; }

    public Dimensions ToDimensions() => new(LocationId, Date, ClientId);    
}

internal class PointsViewConfiguration(IDbContextFactory<ApplicationDbContext> dbFactory) : ViewConfigurationBase<ApplicationDbContext, Points, Points.Dimensions>(dbFactory)
{   
    protected override Task MergeAsync(ApplicationDbContext dbContext, Points[] data)
    {
        throw new NotImplementedException();
    }

    protected override Task MergeDimensionsAsync(ApplicationDbContext dbContext, Points.Dimensions[] dimensions)
    {
        throw new NotImplementedException();
    }

    protected override Task<Points[]> QueryLiveAsync(ApplicationDbContext dbContext, Points.Dimensions[] dimensions)
    {
        throw new NotImplementedException();
    }

    protected override Task<Points.Dimensions[]> QueryModifiedDimensionsAsync(ApplicationDbContext dbContext, IDbContextTransaction txn)
    {
        throw new NotImplementedException();
    }

    protected override Task<Points.Dimensions[]> TrackModifiedDimensionsAsync(ApplicationDbContext dbContext, ChangeTracker changeTracker)
    {
        throw new NotImplementedException();
    }

    protected override DbSet<Points> ViewEntity(ApplicationDbContext db) => db.Points;    
}