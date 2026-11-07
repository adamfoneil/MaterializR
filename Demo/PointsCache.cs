using MaterializR.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;

namespace Demo;

[DebuggerDisplay("{Date} - {TransportId} - {Scheduled}/{Maximum} ({PercentFreeLive})")]
public class PointsCache : ITenantCacheEntity<int>
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    // dimensions

    public DateOnly Date { get; set; }
    /// <summary>
    /// 0 = none, any other value is transport
    /// </summary>
    public int TransportId { get; set; }

    // you'll need a type that represents the unique index of the cache/view table
    public record Dimensions(DateOnly Date, int TransportId);

    // facts

    public int Maximum { get; set; }
    public int Scheduled { get; set; }
    public int Available { get; set; }
    [NotMapped]
    public int AvailableLive => Maximum - Scheduled;
    public decimal PercentFree { get; set; }
    [NotMapped]
    public decimal PercentFreeLive => Maximum > 0 ? (decimal)(Maximum - Scheduled) / Maximum : 0m;

    public bool IsStale { get; set; } = true;
}

public class PointsConfiguration : IEntityTypeConfiguration<PointsCache>
{
    void IEntityTypeConfiguration<PointsCache>.Configure(EntityTypeBuilder<PointsCache> builder)
    {
        builder.HasIndex(e => new { e.TenantId, e.Date, e.TransportId }).IsUnique();        

        builder.Property(e => e.PercentFree)
            .HasComputedColumnSql(
                "CASE WHEN \"Maximum\" = 0 THEN 0 ELSE 1 - CAST((\"Maximum\" - \"Scheduled\") AS float) / \"Maximum\" * 100 END",
                stored: true);

        builder.Property(e => e.Available).HasComputedColumnSql("\"Maximum\" - \"Scheduled\"", stored: true);
    }
}
