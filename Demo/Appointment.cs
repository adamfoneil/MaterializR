namespace Demo;

using System.Linq.Expressions;

public enum AppointmentStatus
{
    Scheduled,
    Canceled,
    Completed,
    Requested,
    NoShow
}

public class Appointment
{
    public int Id { get; set; }    
    public int ClientId { get; set; }
    public int? TransportId { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }
    public AppointmentStatus Status { get; set; }
    public int Points { get; set; }

    public static readonly Expression<Func<Appointment, bool>> AffectsCapacityExpression =
        a => a.Status == AppointmentStatus.Scheduled
          || a.Status == AppointmentStatus.Completed;

    private static readonly Func<Appointment, bool> AffectsCapacityFunc =
        AffectsCapacityExpression.Compile();

    public bool AffectsCapacity() => AffectsCapacityFunc(this);

    public ICollection<AppointmentItem> Items { get; set; } = [];
    public AppointmentPrice? Price { get; set; }
}

public static class AppointmentPredicates
{
    public static readonly Expression<Func<Appointment, bool>> AffectsCapacity =
        Appointment.AffectsCapacityExpression;
}
