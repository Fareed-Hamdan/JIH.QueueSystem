namespace JIH.Queue.Core.Domain;

// Entities are intentionally "flat": foreign keys only, no navigation properties.
// This keeps the Oracle mapping simple and lets the same business code run against
// the in-memory store (development / tests) and Oracle (production).
//
// All timestamps are hospital local time (Asia/Amman), see IClock.

public interface IEntity
{
    int Id { get; set; }
}

/// <summary>An outpatient clinic, e.g. Internal Medicine. Patients queue per clinic.</summary>
public class Clinic : IEntity
{
    public int Id { get; set; }
    /// <summary>Single-letter prefix used on tickets (A, B, C…). Unique.</summary>
    public string Code { get; set; } = "";
    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";
    public ClinicStatus Status { get; set; } = ClinicStatus.Open;
    /// <summary>Maximum tickets per day. Null = unlimited. When reached the clinic becomes Full.</summary>
    public int? DailyTicketLimit { get; set; }
    /// <summary>Optional time (HH:mm) after which no new tickets are issued.</summary>
    public string? TicketCutoffTime { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public string Name(bool arabic) => arabic ? NameAr : NameEn;
}

/// <summary>A consultation room. Announcements say the room number.</summary>
public class Room : IEntity
{
    public int Id { get; set; }
    public int ClinicId { get; set; }
    /// <summary>Room number (1–999) – spoken by the waiting-hall voice.</summary>
    public int Number { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Doctor : IEntity
{
    public int Id { get; set; }
    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";
    private string specialtyArValue = "";
    public string SpecialtyAr { get => specialtyArValue; set => specialtyArValue = value ?? ""; } // Oracle stores "" as NULL
    private string specialtyEnValue = "";
    public string SpecialtyEn { get => specialtyEnValue; set => specialtyEnValue = value ?? ""; } // Oracle stores "" as NULL
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public string Name(bool arabic) => arabic ? NameAr : NameEn;
}

/// <summary>Daily distribution of doctors to clinics and rooms, per shift.</summary>
public class DoctorAssignment : IEntity
{
    public int Id { get; set; }
    public int DoctorId { get; set; }
    public int ClinicId { get; set; }
    public int RoomId { get; set; }
    /// <summary>Date only (time part is 00:00).</summary>
    public DateTime ServiceDate { get; set; }
    public Shift Shift { get; set; }
    public AssignmentStatus Status { get; set; } = AssignmentStatus.Scheduled;
    /// <summary>The ticket currently with this doctor (status Called), if any.</summary>
    public int? CurrentTicketId { get; set; }
    public DateTime CreatedAt { get; set; }
    private string createdByValue = "";
    public string CreatedBy { get => createdByValue; set => createdByValue = value ?? ""; } // Oracle stores "" as NULL
}

/// <summary>
/// A patient registered TODAY. This table is emptied by the end-of-day reset
/// (after the anonymous VisitHistory and the DailyReport are written).
/// </summary>
public class Patient : IEntity
{
    public int Id { get; set; }
    public DateTime ServiceDate { get; set; }
    public string FullName { get; set; } = "";
    /// <summary>E.164, e.g. +962791234567.</summary>
    public string Phone { get; set; } = "";
    /// <summary>"ar" or "en" – language for notifications and the ticket page.</summary>
    public string Language { get; set; } = "ar";
    public DateTime ConsentAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A queue ticket. Linked to the CLINIC; the doctor is only known once the patient is called.</summary>
public class QueueTicket : IEntity
{
    public int Id { get; set; }
    /// <summary>Unguessable id used in the patient's tracking link and the printed QR code.</summary>
    public string PublicId { get; set; } = "";
    public DateTime ServiceDate { get; set; }
    public int ClinicId { get; set; }
    public int PatientId { get; set; }
    /// <summary>Running number per clinic per day.</summary>
    public int Sequence { get; set; }
    /// <summary>Display number, e.g. "A-015".</summary>
    public string Number { get; set; } = "";
    /// <summary>Patient's own short description of the problem. Never shown on public screens.</summary>
    public string Complaint { get; set; } = "";
    public TicketStatus Status { get; set; } = TicketStatus.Waiting;
    public TicketSource Source { get; set; }
    public bool IsPriority { get; set; }
    /// <summary>Order inside the clinic queue (priority tickets always come first).</summary>
    public long SortKey { get; set; }
    public int SkipCount { get; set; }
    public int RecallCount { get; set; }
    public DateTime IssuedAt { get; set; }
    /// <summary>For transferred tickets: when the patient first arrived at the hospital.</summary>
    public DateTime OriginalIssuedAt { get; set; }
    public DateTime? FirstCalledAt { get; set; }
    public DateTime? LastCalledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? AssignmentId { get; set; }
    public int? DoctorId { get; set; }
    public int? RoomId { get; set; }
    public int? TransferredFromTicketId { get; set; }
    public bool NotifiedIssued { get; set; }
    public bool NotifiedApproaching { get; set; }
    public bool NotifiedCalled { get; set; }
    private string issuedByValue = "";
    public string IssuedBy { get => issuedByValue; set => issuedByValue = value ?? ""; } // Oracle stores "" as NULL
}

public class StaffUser : IEntity
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public StaffRole Role { get; set; }
    /// <summary>For users with role Doctor: the doctor record they operate.</summary>
    public int? DoctorId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>Append-only audit trail. Contains ticket numbers and staff names, never patient names or phones.</summary>
public class AuditLog : IEntity
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    private string entityTypeValue = "";
    public string EntityType { get => entityTypeValue; set => entityTypeValue = value ?? ""; } // Oracle stores "" as NULL
    public string? EntityId { get; set; }
    public int? ClinicId { get; set; }
    public string? Details { get; set; }
}

public class NotificationLog : IEntity
{
    public int Id { get; set; }
    public int TicketId { get; set; }
    public NotificationKind Kind { get; set; }
    public NotificationChannel Channel { get; set; }
    /// <summary>Masked phone, e.g. +96279****567.</summary>
    public string ToMasked { get; set; } = "";
    public string Body { get; set; } = "";
    public NotificationStatus Status { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

/// <summary>Anonymous record of each visit kept forever for statistics (no name, phone or complaint).</summary>
public class VisitHistory : IEntity
{
    public int Id { get; set; }
    public DateTime ServiceDate { get; set; }
    public int ClinicId { get; set; }
    public int? DoctorId { get; set; }
    public int? RoomId { get; set; }
    public int Sequence { get; set; }
    public string TicketNumber { get; set; } = "";
    public TicketSource Source { get; set; }
    public TicketStatus Outcome { get; set; }
    public bool IsPriority { get; set; }
    public int SkipCount { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime? FirstCalledAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double? WaitMinutes { get; set; }
    public double? ServiceMinutes { get; set; }
    public int IssuedHour { get; set; }
}

/// <summary>Frozen end-of-day report, generated before the daily reset.</summary>
public class DailyReport : IEntity
{
    public int Id { get; set; }
    public DateTime ServiceDate { get; set; }
    public DateTime GeneratedAt { get; set; }
    private string generatedByValue = "";
    public string GeneratedBy { get => generatedByValue; set => generatedByValue = value ?? ""; } // Oracle stores "" as NULL
    public ResetTrigger Trigger { get; set; }
    public int TotalTickets { get; set; }
    public int Completed { get; set; }
    public int NoShows { get; set; }
    public int NotSeen { get; set; }
    public int Transferred { get; set; }
    public int Cancelled { get; set; }
    public double? AvgWaitMinutes { get; set; }
    public double? AvgServiceMinutes { get; set; }
    public int NotificationsSent { get; set; }
    public int NotificationsFailed { get; set; }
    public int AuditEventCount { get; set; }
    /// <summary>JSON: per-clinic, per-doctor and per-hour breakdown.</summary>
    public string DetailsJson { get; set; } = "{}";
}

/// <summary>Key/value settings editable from the admin dashboard.</summary>
public class SystemSetting
{
    public string Key { get; set; } = "";
    private string valueValue = "";
    public string Value { get => valueValue; set => valueValue = value ?? ""; } // Oracle stores "" as NULL
    public DateTime UpdatedAt { get; set; }
    private string updatedByValue = "";
    public string UpdatedBy { get => updatedByValue; set => updatedByValue = value ?? ""; } // Oracle stores "" as NULL
}

/// <summary>A waiting-hall screen. Opened at /display/{Code} without login.</summary>
public class DisplayDevice : IEntity
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Comma-separated clinic ids shown on this screen. Empty = all clinics.</summary>
    private string clinicIdsValue = "";
    public string ClinicIds { get => clinicIdsValue; set => clinicIdsValue = value ?? ""; } // Oracle stores "" as NULL
    public bool IsActive { get; set; } = true;
    public DateTime? LastSeenAt { get; set; }

    public IReadOnlyList<int> ClinicIdList() =>
        ClinicIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(s => int.TryParse(s, out var i) ? i : 0).Where(i => i > 0).ToList();
}
