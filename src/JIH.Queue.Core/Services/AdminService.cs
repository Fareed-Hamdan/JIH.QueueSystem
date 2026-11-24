using System.Text.RegularExpressions;
using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;
using Microsoft.AspNetCore.Identity;

namespace JIH.Queue.Core.Services;

/// <summary>System-admin maintenance: clinics, rooms, doctors, daily assignments, staff users, display screens.</summary>
public sealed partial class AdminService(IDataStoreFactory factory, IClock clock, QueueEvents events)
{
    private static readonly PasswordHasher<StaffUser> Hasher = new();

    [GeneratedRegex("^[A-Z]$")] private static partial Regex ClinicCode();
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")] private static partial Regex HhMm();
    [GeneratedRegex("^[a-z0-9._-]{3,40}$")] private static partial Regex UsernameRx();
    [GeneratedRegex("^[A-Za-z0-9-]{2,30}$")] private static partial Regex DeviceCode();

    // ================================================================ Clinics

    public async Task<OpResult<int>> SaveClinicAsync(Clinic input, string username)
    {
        input.Code = (input.Code ?? "").Trim().ToUpperInvariant();
        input.NameAr = (input.NameAr ?? "").Trim();
        input.NameEn = (input.NameEn ?? "").Trim();
        input.TicketCutoffTime = string.IsNullOrWhiteSpace(input.TicketCutoffTime) ? null : input.TicketCutoffTime.Trim();
        if (!ClinicCode().IsMatch(input.Code)) return OpResult<int>.Fail("err.clinicCode");
        if (input.NameAr.Length is < 2 or > 100 || input.NameEn.Length is < 2 or > 100) return OpResult<int>.Fail("err.required");
        if (input.DailyTicketLimit is < 1 or > 9999) return OpResult<int>.Fail("err.settingRange");
        if (input.TicketCutoffTime is not null && !HhMm().IsMatch(input.TicketCutoffTime)) return OpResult<int>.Fail("err.timeFormat");

        int id;
        await using (var store = factory.Create())
        {
            if (await store.AnyAsync(store.Query<Clinic>().Where(c => c.Code == input.Code && c.Id != input.Id)))
                return OpResult<int>.Fail("err.clinicCodeTaken");
            if (input.Id == 0)
            {
                var c = new Clinic
                {
                    Code = input.Code, NameAr = input.NameAr, NameEn = input.NameEn, Status = input.Status,
                    DailyTicketLimit = input.DailyTicketLimit, TicketCutoffTime = input.TicketCutoffTime,
                    SortOrder = input.SortOrder, IsActive = input.IsActive, CreatedAt = clock.Now
                };
                store.Add(c);
                await store.SaveChangesAsync();
                Audit.Write(store, clock, username, AuditActions.Created, nameof(Clinic), c.Id, c.Id, $"{c.Code} {c.NameEn}");
                id = c.Id;
            }
            else
            {
                var c = await store.FirstOrDefaultAsync(store.Query<Clinic>().Where(x => x.Id == input.Id));
                if (c is null) return OpResult<int>.Fail("err.clinicNotFound");
                if (c.Code != input.Code && await store.AnyAsync(store.Query<QueueTicket>().Where(t => t.ClinicId == c.Id && t.ServiceDate == clock.Today)))
                    return OpResult<int>.Fail("err.clinicCodeInUse");
                if (c.Status != input.Status)
                    Audit.Write(store, clock, username, AuditActions.ClinicStatusChanged, nameof(Clinic), c.Id, c.Id, $"{c.Status} → {input.Status}");
                c.Code = input.Code; c.NameAr = input.NameAr; c.NameEn = input.NameEn; c.Status = input.Status;
                c.DailyTicketLimit = input.DailyTicketLimit; c.TicketCutoffTime = input.TicketCutoffTime;
                c.SortOrder = input.SortOrder; c.IsActive = input.IsActive;
                Audit.Write(store, clock, username, AuditActions.Updated, nameof(Clinic), c.Id, c.Id, $"{c.Code} {c.NameEn}");
                id = c.Id;
            }
            await store.SaveChangesAsync();
        }
        events.PublishConfigurationChanged();
        events.PublishQueueChanged(id);
        return OpResult<int>.Success(id);
    }

    public async Task<OpResult> SetClinicStatusAsync(int clinicId, ClinicStatus status, string username)
    {
        await using (var store = factory.Create())
        {
            var c = await store.FirstOrDefaultAsync(store.Query<Clinic>().Where(x => x.Id == clinicId));
            if (c is null) return OpResult.Fail("err.clinicNotFound");
            if (c.Status == status) return OpResult.Success();
            Audit.Write(store, clock, username, AuditActions.ClinicStatusChanged, nameof(Clinic), c.Id, c.Id, $"{c.Status} → {status}");
            c.Status = status;
            await store.SaveChangesAsync();
        }
        events.PublishConfigurationChanged();
        events.PublishQueueChanged(clinicId);
        return OpResult.Success();
    }

    // ================================================================ Rooms

    public async Task<List<Room>> GetRoomsAsync(int? clinicId = null)
    {
        await using var store = factory.Create();
        var q = store.Query<Room>();
        if (clinicId is int c) q = q.Where(r => r.ClinicId == c);
        return await store.ToListAsync(q.OrderBy(r => r.Number));
    }

    public async Task<OpResult> SaveRoomAsync(Room input, string username)
    {
        if (input.Number is < 1 or > 999) return OpResult.Fail("err.roomNumber");
        await using (var store = factory.Create())
        {
            if (!await store.AnyAsync(store.Query<Clinic>().Where(c => c.Id == input.ClinicId))) return OpResult.Fail("err.clinicNotFound");
            if (await store.AnyAsync(store.Query<Room>().Where(r => r.Number == input.Number && r.Id != input.Id)))
                return OpResult.Fail("err.roomTaken");
            if (input.Id == 0)
            {
                var r = new Room { ClinicId = input.ClinicId, Number = input.Number, IsActive = input.IsActive };
                store.Add(r);
                await store.SaveChangesAsync();
                Audit.Write(store, clock, username, AuditActions.Created, nameof(Room), r.Id, r.ClinicId, $"Room {r.Number}");
            }
            else
            {
                var r = await store.FirstOrDefaultAsync(store.Query<Room>().Where(x => x.Id == input.Id));
                if (r is null) return OpResult.Fail("err.notFound");
                r.ClinicId = input.ClinicId; r.Number = input.Number; r.IsActive = input.IsActive;
                Audit.Write(store, clock, username, AuditActions.Updated, nameof(Room), r.Id, r.ClinicId, $"Room {r.Number}");
            }
            await store.SaveChangesAsync();
        }
        events.PublishConfigurationChanged();
        return OpResult.Success();
    }

    // ================================================================ Doctors

    public async Task<List<Doctor>> GetDoctorsAsync(bool activeOnly = false)
    {
        await using var store = factory.Create();
        var q = store.Query<Doctor>();
        if (activeOnly) q = q.Where(d => d.IsActive);
        return await store.ToListAsync(q.OrderBy(d => d.NameEn));
    }

    public async Task<OpResult<int>> SaveDoctorAsync(Doctor input, string username)
    {
        input.NameAr = (input.NameAr ?? "").Trim(); input.NameEn = (input.NameEn ?? "").Trim();
        input.SpecialtyAr = (input.SpecialtyAr ?? "").Trim(); input.SpecialtyEn = (input.SpecialtyEn ?? "").Trim();
        if (input.NameAr.Length is < 2 or > 100 || input.NameEn.Length is < 2 or > 100) return OpResult<int>.Fail("err.required");
        int id;
        await using (var store = factory.Create())
        {
            if (input.Id == 0)
            {
                var d = new Doctor
                {
                    NameAr = input.NameAr, NameEn = input.NameEn, SpecialtyAr = input.SpecialtyAr, SpecialtyEn = input.SpecialtyEn,
                    IsActive = input.IsActive, CreatedAt = clock.Now
                };
                store.Add(d);
                await store.SaveChangesAsync();
                Audit.Write(store, clock, username, AuditActions.Created, nameof(Doctor), d.Id, null, d.NameEn);
                id = d.Id;
            }
            else
            {
                var d = await store.FirstOrDefaultAsync(store.Query<Doctor>().Where(x => x.Id == input.Id));
                if (d is null) return OpResult<int>.Fail("err.notFound");
                if (d.IsActive && !input.IsActive && await store.AnyAsync(store.Query<DoctorAssignment>()
                        .Where(a => a.DoctorId == d.Id && a.ServiceDate == clock.Today && a.Status == AssignmentStatus.Active)))
                    return OpResult<int>.Fail("err.doctorActiveNow");
                d.NameAr = input.NameAr; d.NameEn = input.NameEn; d.SpecialtyAr = input.SpecialtyAr; d.SpecialtyEn = input.SpecialtyEn; d.IsActive = input.IsActive;
                Audit.Write(store, clock, username, AuditActions.Updated, nameof(Doctor), d.Id, null, d.NameEn);
                id = d.Id;
            }
            await store.SaveChangesAsync();
        }
        events.PublishConfigurationChanged();
        return OpResult<int>.Success(id);
    }

    // ================================================================ Daily assignments

    public async Task<OpResult> SaveAssignmentAsync(DoctorAssignment input, string username)
    {
        var date = input.ServiceDate.Date;
        if (date < clock.Today) return OpResult.Fail("err.assignmentPast");
        await using (var store = factory.Create())
        {
            var doctor = await store.FirstOrDefaultAsync(store.Query<Doctor>().Where(d => d.Id == input.DoctorId && d.IsActive));
            if (doctor is null) return OpResult.Fail("err.doctorNotFound");
            var room = await store.FirstOrDefaultAsync(store.Query<Room>().Where(r => r.Id == input.RoomId && r.IsActive));
            if (room is null || room.ClinicId != input.ClinicId) return OpResult.Fail("err.roomNotInClinic");
            if (await store.AnyAsync(store.Query<DoctorAssignment>().Where(a =>
                    a.DoctorId == input.DoctorId && a.ServiceDate == date && a.Shift == input.Shift && a.Id != input.Id)))
                return OpResult.Fail("err.doctorDoubleBooked");
            if (await store.AnyAsync(store.Query<DoctorAssignment>().Where(a =>
                    a.RoomId == input.RoomId && a.ServiceDate == date && a.Shift == input.Shift && a.Id != input.Id)))
                return OpResult.Fail("err.roomDoubleBooked");

            if (input.Id == 0)
            {
                var a = new DoctorAssignment
                {
                    DoctorId = input.DoctorId, ClinicId = input.ClinicId, RoomId = input.RoomId, ServiceDate = date,
                    Shift = input.Shift, Status = AssignmentStatus.Scheduled, CreatedAt = clock.Now, CreatedBy = username
                };
                store.Add(a);
                await store.SaveChangesAsync();
                Audit.Write(store, clock, username, AuditActions.Created, nameof(DoctorAssignment), a.Id, a.ClinicId,
                    $"{doctor.NameEn} → room {room.Number}, {date:yyyy-MM-dd} {a.Shift}");
            }
            else
            {
                var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == input.Id));
                if (a is null) return OpResult.Fail("err.assignmentNotFound");
                if (a.CurrentTicketId is not null) return OpResult.Fail("err.finishCurrentFirst");
                a.DoctorId = input.DoctorId; a.ClinicId = input.ClinicId; a.RoomId = input.RoomId; a.ServiceDate = date; a.Shift = input.Shift;
                Audit.Write(store, clock, username, AuditActions.Updated, nameof(DoctorAssignment), a.Id, a.ClinicId,
                    $"{doctor.NameEn} → room {room.Number}, {date:yyyy-MM-dd} {a.Shift}");
            }
            await store.SaveChangesAsync();
        }
        events.PublishConfigurationChanged();
        return OpResult.Success();
    }

    public async Task<OpResult> DeleteAssignmentAsync(int id, string username)
    {
        await using (var store = factory.Create())
        {
            var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == id));
            if (a is null) return OpResult.Fail("err.assignmentNotFound");
            if (a.Status != AssignmentStatus.Scheduled) return OpResult.Fail("err.assignmentStarted");
            store.Remove(a);
            Audit.Write(store, clock, username, AuditActions.Deleted, nameof(DoctorAssignment), a.Id, a.ClinicId, $"{a.ServiceDate:yyyy-MM-dd} {a.Shift}");
            await store.SaveChangesAsync();
        }
        events.PublishConfigurationChanged();
        return OpResult.Success();
    }

    /// <summary>Copies all assignments of <paramref name="from"/> to <paramref name="to"/> (skips conflicts). Returns number copied.</summary>
    public async Task<OpResult<int>> CopyAssignmentsAsync(DateTime from, DateTime to, string username)
    {
        from = from.Date; to = to.Date;
        if (to < clock.Today) return OpResult<int>.Fail("err.assignmentPast");
        List<DoctorAssignment> source;
        await using (var store = factory.Create())
            source = await store.ToListAsync(store.Query<DoctorAssignment>().Where(a => a.ServiceDate == from));
        var copied = 0;
        foreach (var a in source)
        {
            var r = await SaveAssignmentAsync(new DoctorAssignment { DoctorId = a.DoctorId, ClinicId = a.ClinicId, RoomId = a.RoomId, ServiceDate = to, Shift = a.Shift }, username);
            if (r.Ok) copied++;
        }
        return OpResult<int>.Success(copied);
    }

    // ================================================================ Staff users

    public async Task<List<StaffUser>> GetUsersAsync()
    {
        await using var store = factory.Create();
        return await store.ToListAsync(store.Query<StaffUser>().OrderBy(u => u.Role).ThenBy(u => u.Username));
    }

    /// <summary>Creates or updates a user. Password is required for new users; empty on update = unchanged.</summary>
    public async Task<OpResult> SaveUserAsync(StaffUser input, string? newPassword, string username)
    {
        input.Username = (input.Username ?? "").Trim().ToLowerInvariant();
        input.DisplayName = (input.DisplayName ?? "").Trim();
        if (!UsernameRx().IsMatch(input.Username)) return OpResult.Fail("err.username");
        if (input.DisplayName.Length is < 2 or > 100) return OpResult.Fail("err.required");
        if (input.Id == 0 && string.IsNullOrEmpty(newPassword)) return OpResult.Fail("err.password");
        if (!string.IsNullOrEmpty(newPassword) && newPassword.Length < 8) return OpResult.Fail("err.password");
        if (input.Role == StaffRole.Doctor && input.DoctorId is null) return OpResult.Fail("err.userDoctorRequired");
        if (input.Role != StaffRole.Doctor) input.DoctorId = null;

        await using var store = factory.Create();
        if (await store.AnyAsync(store.Query<StaffUser>().Where(u => u.Username == input.Username && u.Id != input.Id)))
            return OpResult.Fail("err.usernameTaken");
        StaffUser user;
        if (input.Id == 0)
        {
            user = new StaffUser { Username = input.Username, CreatedAt = clock.Now };
            store.Add(user);
        }
        else
        {
            user = await store.FirstOrDefaultAsync(store.Query<StaffUser>().Where(u => u.Id == input.Id)) ?? throw new InvalidOperationException();
            if (user.Role == StaffRole.Admin && (!input.IsActive || input.Role != StaffRole.Admin))
            {
                var admins = await store.CountAsync(store.Query<StaffUser>().Where(u => u.Role == StaffRole.Admin && u.IsActive));
                if (admins <= 1) return OpResult.Fail("err.lastAdmin");
            }
            user.Username = input.Username;
        }
        user.DisplayName = input.DisplayName; user.Role = input.Role; user.DoctorId = input.DoctorId; user.IsActive = input.IsActive;
        if (!string.IsNullOrEmpty(newPassword)) user.PasswordHash = Hasher.HashPassword(user, newPassword);
        await store.SaveChangesAsync();
        Audit.Write(store, clock, username, input.Id == 0 ? AuditActions.Created : AuditActions.Updated, nameof(StaffUser), user.Id, null,
            $"{user.Username} ({user.Role}){(string.IsNullOrEmpty(newPassword) ? "" : " password set")}");
        await store.SaveChangesAsync();
        return OpResult.Success();
    }

    /// <summary>Returns the user if the credentials are valid. Writes Login / LoginFailed audit rows.</summary>
    public async Task<StaffUser?> ValidateLoginAsync(string username, string password)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        await using var store = factory.Create();
        var user = await store.FirstOrDefaultAsync(store.Query<StaffUser>().Where(u => u.Username == username && u.IsActive));
        var ok = user is not null && Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "") != PasswordVerificationResult.Failed;
        Audit.Write(store, clock, ok ? user!.Username : username, ok ? AuditActions.Login : AuditActions.LoginFailed, nameof(StaffUser), user?.Id);
        if (ok) user!.LastLoginAt = clock.Now;
        await store.SaveChangesAsync();
        return ok ? user : null;
    }

    public static string HashPassword(StaffUser u, string password) => Hasher.HashPassword(u, password);

    // ================================================================ Display screens

    public async Task<List<DisplayDevice>> GetDisplaysAsync()
    {
        await using var store = factory.Create();
        return await store.ToListAsync(store.Query<DisplayDevice>().OrderBy(d => d.Code));
    }

    public async Task<DisplayDevice?> GetDisplayByCodeAsync(string code)
    {
        await using var store = factory.Create();
        var d = await store.FirstOrDefaultAsync(store.Query<DisplayDevice>().Where(x => x.Code == code && x.IsActive));
        if (d is not null) { d.LastSeenAt = clock.Now; await store.SaveChangesAsync(); }
        return d;
    }

    public async Task<OpResult> SaveDisplayAsync(DisplayDevice input, string username)
    {
        input.Code = (input.Code ?? "").Trim().ToUpperInvariant();
        input.Name = (input.Name ?? "").Trim();
        if (!DeviceCode().IsMatch(input.Code)) return OpResult.Fail("err.deviceCode");
        if (input.Name.Length is < 2 or > 100) return OpResult.Fail("err.required");
        await using (var store = factory.Create())
        {
            if (await store.AnyAsync(store.Query<DisplayDevice>().Where(d => d.Code == input.Code && d.Id != input.Id)))
                return OpResult.Fail("err.deviceCodeTaken");
            DisplayDevice d;
            if (input.Id == 0) { d = new DisplayDevice(); store.Add(d); }
            else d = await store.FirstOrDefaultAsync(store.Query<DisplayDevice>().Where(x => x.Id == input.Id)) ?? throw new InvalidOperationException();
            d.Code = input.Code; d.Name = input.Name; d.ClinicIds = input.ClinicIds ?? ""; d.IsActive = input.IsActive;
            await store.SaveChangesAsync();
            Audit.Write(store, clock, username, input.Id == 0 ? AuditActions.Created : AuditActions.Updated, nameof(DisplayDevice), d.Id, null, d.Code);
            await store.SaveChangesAsync();
        }
        events.PublishConfigurationChanged();
        return OpResult.Success();
    }

    // ================================================================ Audit log

    public async Task<(List<AuditLog> Rows, int Total)> GetAuditAsync(DateTime from, DateTime to, string? action, string? user, int page, int pageSize)
    {
        await using var store = factory.Create();
        var end = to.Date.AddDays(1);
        var q = store.Query<AuditLog>().Where(a => a.Timestamp >= from.Date && a.Timestamp < end);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(user)) q = q.Where(a => a.Username == user);
        var total = await store.CountAsync(q);
        var rows = await store.ToListAsync(q.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id).Skip(page * pageSize).Take(pageSize));
        return (rows, total);
    }
}
