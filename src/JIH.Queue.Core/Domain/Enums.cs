namespace JIH.Queue.Core.Domain;

/// <summary>Operational status of a clinic. Stored as text in the database.</summary>
public enum ClinicStatus
{
    /// <summary>Normal: tickets are issued and patients are called.</summary>
    Open,
    /// <summary>Temporarily not calling (break / prayer). Tickets can still be issued.</summary>
    Paused,
    /// <summary>No new tickets (daily limit reached or set manually). Waiting patients are still called.</summary>
    Full,
    /// <summary>Closed: no tickets and no calls.</summary>
    Closed
}

public enum TicketStatus
{
    /// <summary>In the queue.</summary>
    Waiting,
    /// <summary>Called to a room and currently with the doctor (or on the way).</summary>
    Called,
    /// <summary>Visit ended by the doctor/nurse.</summary>
    Completed,
    /// <summary>Skipped twice (or skipped after a reinstatement) – did not show up.</summary>
    NoShow,
    /// <summary>Referred to another clinic; a new ticket was created there.</summary>
    Transferred,
    /// <summary>Cancelled by reception.</summary>
    Cancelled,
    /// <summary>Still waiting when the day was closed.</summary>
    NotSeen
}

public enum TicketSource
{
    /// <summary>Patient scanned the rotating QR code at reception and filled the form on their phone.</summary>
    SelfServiceQr,
    /// <summary>Issued by reception staff (patients without a smartphone).</summary>
    Reception,
    /// <summary>Created by a doctor transfer from another clinic.</summary>
    Transfer
}

public enum Shift
{
    Morning,
    Evening
}

public enum AssignmentStatus
{
    /// <summary>Planned by the admin, doctor not started yet.</summary>
    Scheduled,
    /// <summary>Doctor is receiving patients.</summary>
    Active,
    /// <summary>Doctor on a break – will not be given patients.</summary>
    Paused,
    /// <summary>Shift finished.</summary>
    Finished
}

public enum StaffRole
{
    Admin,
    Receptionist,
    Doctor,
    Nurse
}

public enum NotificationKind
{
    TicketIssued,
    TurnApproaching,
    Called
}

public enum NotificationChannel
{
    /// <summary>Logged only – nothing leaves the server. Default for demos.</summary>
    Mock,
    WhatsApp,
    Sms
}

public enum NotificationStatus
{
    Queued,
    Sent,
    Failed,
    Mocked
}

public enum ResetTrigger
{
    Scheduled,
    Manual
}
