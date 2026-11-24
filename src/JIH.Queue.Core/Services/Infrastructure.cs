using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;

namespace JIH.Queue.Core.Services;

// ---------------------------------------------------------------- Clock

public interface IClock
{
    /// <summary>Current hospital local time (Asia/Amman).</summary>
    DateTime Now { get; }
    DateTime Today => Now.Date;
}

public sealed class AmmanClock : IClock
{
    private static readonly TimeZoneInfo? Zone = FindZone();

    private static TimeZoneInfo? FindZone()
    {
        foreach (var id in new[] { "Asia/Amman", "Jordan Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* try next */ }
        }
        return null;
    }

    // Jordan has used permanent UTC+3 since October 2022 – used if the OS has no tz database.
    public DateTime Now => Zone is null
        ? DateTime.SpecifyKind(DateTime.UtcNow.AddHours(3), DateTimeKind.Unspecified)
        : DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone), DateTimeKind.Unspecified);
}

// ---------------------------------------------------------------- Results

/// <summary>Outcome of a business operation. Error is a localisation key (see Loc in the web project).</summary>
public record OpResult(bool Ok, string? Error = null, object[]? Args = null)
{
    public static OpResult Success() => new(true);
    public static OpResult Fail(string error, params object[] args) => new(false, error, args);
}

public record OpResult<T>(bool Ok, T? Value, string? Error = null, object[]? Args = null)
{
    public static OpResult<T> Success(T value) => new(true, value);
    public static OpResult<T> Fail(string error, params object[] args) => new(false, default, error, args);
}

// ---------------------------------------------------------------- Phone numbers

public static partial class PhoneNumbers
{
    [GeneratedRegex(@"^7[789]\d{7}$")]
    private static partial Regex JordanMobile();

    /// <summary>
    /// Normalises a Jordanian mobile number to E.164 (+9627XXXXXXXX).
    /// Accepts 07XXXXXXXX, 7XXXXXXXX, +9627XXXXXXXX, 009627XXXXXXXX, 9627XXXXXXXX and Arabic-Indic digits.
    /// </summary>
    public static bool TryNormalize(string? input, out string e164)
    {
        e164 = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            if (ch is >= '0' and <= '9') sb.Append(ch);
            else if (ch is >= '٠' and <= '٩') sb.Append((char)('0' + (ch - '٠')));   // Arabic-Indic
            else if (ch is >= '۰' and <= '۹') sb.Append((char)('0' + (ch - '۰')));   // Eastern Arabic-Indic
        }
        var digits = sb.ToString();
        if (digits.StartsWith("00962")) digits = digits[5..];
        else if (digits.StartsWith("962")) digits = digits[3..];
        if (digits.StartsWith('0')) digits = digits[1..];
        if (!JordanMobile().IsMatch(digits)) return false;
        e164 = "+962" + digits;
        return true;
    }

    /// <summary>+962791234567 → +96279****567 (used in logs).</summary>
    public static string Mask(string e164) =>
        e164.Length < 9 ? "****" : e164[..6] + new string('*', e164.Length - 9) + e164[^3..];

    /// <summary>+962791234567 → 0791234567 (local display for staff).</summary>
    public static string ToLocal(string e164) => e164.StartsWith("+962") ? "0" + e164[4..] : e164;
}

// ---------------------------------------------------------------- Rotating QR token

/// <summary>
/// The reception screen shows a QR code that changes every N seconds, so the self-service form
/// can only be opened by someone physically at the hospital. Token = window.signature (HMAC-SHA256).
/// The current and the previous window are accepted.
/// </summary>
public sealed class QrTokenService(IClock clock, QrTokenOptions options)
{
    public string CurrentToken(int rotationSeconds) => Sign(Window(clock.Now, rotationSeconds));

    public TimeSpan TimeLeft(int rotationSeconds)
    {
        var secs = (long)(clock.Now - DateTime.UnixEpoch).TotalSeconds;
        return TimeSpan.FromSeconds(rotationSeconds - secs % rotationSeconds);
    }

    public bool Validate(string? token, int rotationSeconds)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var window)) return false;
        var now = Window(clock.Now, rotationSeconds);
        if (window != now && window != now - 1) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(window)), Encoding.ASCII.GetBytes(token));
    }

    private static long Window(DateTime now, int rotationSeconds) =>
        (long)(now - DateTime.UnixEpoch).TotalSeconds / Math.Max(15, rotationSeconds);

    private string Sign(long window)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(options.Secret));
        var sig = h.ComputeHash(Encoding.ASCII.GetBytes(window.ToString()));
        return window + "." + Convert.ToBase64String(sig, 0, 12).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

public sealed class QrTokenOptions
{
    public string Secret { get; set; } = "change-me-in-appsettings";
}

// ---------------------------------------------------------------- Real-time events

public sealed record QueueChange(int ClinicId);

/// <summary>A call to be shown and spoken on the waiting-hall screens.</summary>
public sealed record Announcement(int ClinicId, string TicketNumber, int RoomNumber, bool IsRecall, DateTime At);

/// <summary>
/// In-process event bus. Blazor Server components subscribe and refresh in real time
/// (Blazor Server already pushes UI changes to the browser over SignalR).
/// Note: one server instance. For several instances behind a load balancer, replace with a SignalR backplane.
/// </summary>
public sealed class QueueEvents
{
    public event Action<QueueChange>? QueueChanged;
    public event Action<Announcement>? Announced;
    /// <summary>Clinics, doctors, assignments or settings changed.</summary>
    public event Action? ConfigurationChanged;

    public void PublishQueueChanged(int clinicId) => Safe(() => QueueChanged?.Invoke(new QueueChange(clinicId)));
    public void PublishAnnouncement(Announcement a) => Safe(() => Announced?.Invoke(a));
    public void PublishConfigurationChanged() => Safe(() => ConfigurationChanged?.Invoke());

    private static void Safe(Action a)
    {
        try { a(); } catch { /* a broken subscriber must never break the queue */ }
    }
}

// ---------------------------------------------------------------- Audit

public static class Audit
{
    /// <summary>Adds an audit row to the current unit of work. Never pass patient names or phone numbers.</summary>
    public static void Write(IDataStore store, IClock clock, string username, string action, string entityType,
        object? entityId = null, int? clinicId = null, string? details = null)
    {
        store.Add(new AuditLog
        {
            Timestamp = clock.Now,
            Username = string.IsNullOrWhiteSpace(username) ? "system" : username,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            ClinicId = clinicId,
            Details = details is { Length: > 1000 } ? details[..1000] : details
        });
    }
}

/// <summary>Names of audit actions (kept as constants so reports can group by them).</summary>
public static class AuditActions
{
    public const string TicketIssued = "TicketIssued";
    public const string TicketCalled = "TicketCalled";
    public const string TicketRecalled = "TicketRecalled";
    public const string TicketSkipped = "TicketSkipped";
    public const string TicketNoShow = "TicketNoShow";
    public const string VisitEnded = "VisitEnded";
    public const string TicketTransferred = "TicketTransferred";
    public const string TicketReinstated = "TicketReinstated";
    public const string TicketCancelled = "TicketCancelled";
    public const string TicketPriorityChanged = "TicketPriorityChanged";
    public const string TicketClinicChanged = "TicketClinicChanged";
    public const string ClinicStatusChanged = "ClinicStatusChanged";
    public const string AssignmentStatusChanged = "AssignmentStatusChanged";
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Deleted = "Deleted";
    public const string SettingChanged = "SettingChanged";
    public const string Login = "Login";
    public const string LoginFailed = "LoginFailed";
    public const string EndOfDay = "EndOfDay";
}
