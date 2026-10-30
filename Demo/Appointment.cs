namespace Demo;

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

    public bool AffectsCapacity() => Status switch
    {
        AppointmentStatus.Scheduled => true,
        AppointmentStatus.Completed => true,
        _ => false
    };
}
