using System.Text;
using JIH.Queue.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace JIH.Queue.Data.Oracle;

/// <summary>
/// EF Core model for Oracle. Naming follows Oracle conventions (UPPER_SNAKE_CASE, unquoted-safe),
/// so DBAs can query tables directly, e.g. SELECT * FROM QUEUE_TICKETS.
/// Enums are stored as readable text; booleans as NUMBER(1) (works on 19c and 23ai).
/// The matching DDL is in database/oracle/01_schema.sql.
/// </summary>
public class QueueDbContext(DbContextOptions<QueueDbContext> options) : DbContext(options)
{
    public DbSet<Clinic> Clinics => Set<Clinic>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<DoctorAssignment> DoctorAssignments => Set<DoctorAssignment>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<QueueTicket> QueueTickets => Set<QueueTicket>();
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<VisitHistory> VisitHistory => Set<VisitHistory>();
    public DbSet<DailyReport> DailyReports => Set<DailyReport>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<DisplayDevice> DisplayDevices => Set<DisplayDevice>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<bool>().HaveConversion<int>().HaveColumnType("NUMBER(1)");
        b.Properties<string>().HaveMaxLength(200);
        b.Properties<ClinicStatus>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<TicketStatus>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<TicketSource>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<Shift>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<AssignmentStatus>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<StaffRole>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<NotificationKind>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<NotificationChannel>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<NotificationStatus>().HaveConversion<string>().HaveMaxLength(20);
        b.Properties<ResetTrigger>().HaveConversion<string>().HaveMaxLength(20);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Clinic>(e =>
        {
            e.ToTable("CLINICS");
            e.Property(x => x.Code).HasMaxLength(1).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(100).IsRequired();
            e.Property(x => x.NameEn).HasMaxLength(100).IsRequired();
            e.Property(x => x.TicketCutoffTime).HasMaxLength(5);
            e.HasIndex(x => x.Code).IsUnique();
        });

        m.Entity<Room>(e =>
        {
            e.ToTable("ROOMS");
            e.HasIndex(x => x.Number).IsUnique();
            e.HasOne<Clinic>().WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<Doctor>(e =>
        {
            e.ToTable("DOCTORS");
            e.Property(x => x.NameAr).HasMaxLength(100).IsRequired();
            e.Property(x => x.NameEn).HasMaxLength(100).IsRequired();
            e.Property(x => x.SpecialtyAr).HasMaxLength(100);
            e.Property(x => x.SpecialtyEn).HasMaxLength(100);
        });

        m.Entity<DoctorAssignment>(e =>
        {
            e.ToTable("DOCTOR_ASSIGNMENTS");
            e.Property(x => x.CreatedBy).HasMaxLength(50);
            e.HasIndex(x => new { x.DoctorId, x.ServiceDate, x.Shift }).IsUnique();
            e.HasIndex(x => new { x.RoomId, x.ServiceDate, x.Shift }).IsUnique();
            e.HasIndex(x => new { x.ClinicId, x.ServiceDate });
            e.HasOne<Doctor>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Clinic>().WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Restrict);
            // CURRENT_TICKET_ID intentionally has no FK: tickets are deleted daily.
        });

        m.Entity<Patient>(e =>
        {
            e.ToTable("PATIENTS");
            e.Property(x => x.FullName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(20).IsRequired();
            e.Property(x => x.Language).HasMaxLength(2).IsRequired();
            e.HasIndex(x => new { x.ServiceDate, x.Phone });
        });

        m.Entity<QueueTicket>(e =>
        {
            e.ToTable("QUEUE_TICKETS");
            e.Property(x => x.PublicId).HasMaxLength(32).IsRequired();
            e.Property(x => x.Number).HasMaxLength(10).IsRequired();
            e.Property(x => x.Complaint).HasMaxLength(500).IsRequired();
            e.Property(x => x.IssuedBy).HasMaxLength(50);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => new { x.ServiceDate, x.ClinicId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.ClinicId, x.ServiceDate, x.Status, x.SortKey });
            e.HasOne<Clinic>().WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<StaffUser>(e =>
        {
            e.ToTable("STAFF_USERS");
            e.Property(x => x.Username).HasMaxLength(40).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.HasOne<Doctor>().WithMany().HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<AuditLog>(e =>
        {
            e.ToTable("AUDIT_LOG");
            e.Property(x => x.Username).HasMaxLength(50);
            e.Property(x => x.Action).HasMaxLength(50).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(50);
            e.Property(x => x.EntityId).HasMaxLength(50);
            e.Property(x => x.Details).HasMaxLength(1000);
            e.HasIndex(x => x.Timestamp);
        });

        m.Entity<NotificationLog>(e =>
        {
            e.ToTable("NOTIFICATION_LOG");
            e.Property(x => x.ToMasked).HasMaxLength(20);
            e.Property(x => x.Body).HasMaxLength(1000);
            e.Property(x => x.ProviderMessageId).HasMaxLength(100);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => x.TicketId);
        });

        m.Entity<VisitHistory>(e =>
        {
            e.ToTable("VISIT_HISTORY");
            e.Property(x => x.TicketNumber).HasMaxLength(10);
            e.HasIndex(x => new { x.ServiceDate, x.ClinicId });
            e.HasOne<Clinic>().WithMany().HasForeignKey(x => x.ClinicId).OnDelete(DeleteBehavior.Restrict);
        });

        m.Entity<DailyReport>(e =>
        {
            e.ToTable("DAILY_REPORTS");
            e.Property(x => x.GeneratedBy).HasMaxLength(50);
            e.Property(x => x.DetailsJson).HasColumnType("NCLOB").Metadata.SetMaxLength(null); // no 200-char convention on the CLOB
            e.HasIndex(x => x.ServiceDate);
        });

        m.Entity<SystemSetting>(e =>
        {
            e.ToTable("SYSTEM_SETTINGS");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(60);
            e.Property(x => x.Value).HasMaxLength(500);
            e.Property(x => x.UpdatedBy).HasMaxLength(50);
        });

        m.Entity<DisplayDevice>(e =>
        {
            e.ToTable("DISPLAY_DEVICES");
            e.Property(x => x.Code).HasMaxLength(30).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.ClinicIds).HasMaxLength(500);
            e.HasIndex(x => x.Code).IsUnique();
        });

        // UPPER_SNAKE_CASE for every column, key, index and FK.
        foreach (var entity in m.Model.GetEntityTypes())
        {
            foreach (var p in entity.GetProperties())
                p.SetColumnName(ToUpperSnake(p.Name));
            var table = entity.GetTableName()!;
            foreach (var k in entity.GetKeys())
                k.SetName(k.IsPrimaryKey() ? $"PK_{table}" : $"AK_{table}_{string.Join("_", k.Properties.Select(p => ToUpperSnake(p.Name)))}");
            foreach (var fk in entity.GetForeignKeys())
                fk.SetConstraintName($"FK_{table}_{string.Join("_", fk.Properties.Select(p => ToUpperSnake(p.Name)))}");
            foreach (var ix in entity.GetIndexes())
                ix.SetDatabaseName(Truncate($"IX_{table}_{string.Join("_", ix.Properties.Select(p => ToUpperSnake(p.Name)))}", 120));
        }

        // Avoid Oracle reserved words / keywords as column names (NUMBER, TIMESTAMP, KEY, VALUE, SEQUENCE).
        m.Entity<Room>().Property(x => x.Number).HasColumnName("ROOM_NUMBER");
        m.Entity<QueueTicket>().Property(x => x.Number).HasColumnName("TICKET_NUMBER");
        m.Entity<QueueTicket>().Property(x => x.Sequence).HasColumnName("SEQ_NO");
        m.Entity<VisitHistory>().Property(x => x.Sequence).HasColumnName("SEQ_NO");
        m.Entity<AuditLog>().Property(x => x.Timestamp).HasColumnName("LOGGED_AT");
        m.Entity<SystemSetting>().Property(x => x.Key).HasColumnName("SETTING_KEY");
        m.Entity<SystemSetting>().Property(x => x.Value).HasColumnName("SETTING_VALUE");
        m.Entity<DailyReport>().Property(x => x.Trigger).HasColumnName("RESET_TRIGGER");

        // Oracle stores an empty string as NULL, so columns that may legitimately be "" are nullable.
        // The entity setters turn NULL back into "" when reading.
        m.Entity<Doctor>().Property(x => x.SpecialtyAr).IsRequired(false);
        m.Entity<Doctor>().Property(x => x.SpecialtyEn).IsRequired(false);
        m.Entity<DoctorAssignment>().Property(x => x.CreatedBy).IsRequired(false);
        m.Entity<QueueTicket>().Property(x => x.IssuedBy).IsRequired(false);
        m.Entity<AuditLog>().Property(x => x.EntityType).IsRequired(false);
        m.Entity<DailyReport>().Property(x => x.GeneratedBy).IsRequired(false);
        m.Entity<SystemSetting>().Property(x => x.Value).IsRequired(false);
        m.Entity<SystemSetting>().Property(x => x.UpdatedBy).IsRequired(false);
        m.Entity<DisplayDevice>().Property(x => x.ClinicIds).IsRequired(false);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public static string ToUpperSnake(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1])))) sb.Append('_');
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }
}
