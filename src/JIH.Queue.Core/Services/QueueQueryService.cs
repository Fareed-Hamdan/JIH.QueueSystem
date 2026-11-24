using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;
using JIH.Queue.Core.Rules;

namespace JIH.Queue.Core.Services;

// ---------------------------------------------------------------- read models

public sealed record ServingItem(string Number, int RoomNumber, DateTime CalledAt);

/// <summary>What a waiting-hall screen shows for one clinic. Contains NO patient data.</summary>
public sealed record DisplayClinic(int ClinicId, string Code, string NameAr, string NameEn, ClinicStatus Status,
    IReadOnlyList<ServingItem> Serving, IReadOnlyList<string> Next, int WaitingCount);

public sealed record TicketStatusView(string Number, string ClinicNameAr, string ClinicNameEn, TicketStatus Status,
    int PeopleAhead, int? RoomNumber, int? EstimatedMinutes, DateTime IssuedAt, string Language, string? TransferredToPublicId);

public sealed record QueueRow(int TicketId, string Number, string PatientName, string PhoneLocal, string Complaint,
    bool IsPriority, int SkipCount, TicketStatus Status, TicketSource Source, DateTime IssuedAt, int ClinicId,
    int? RoomNumber, int? DoctorId, string PublicId);

public sealed record AssignmentView(int Id, int DoctorId, string DoctorNameAr, string DoctorNameEn, int ClinicId,
    string ClinicNameAr, string ClinicNameEn, int RoomId, int RoomNumber, Shift Shift, AssignmentStatus Status, int? CurrentTicketId, DateTime ServiceDate);

public sealed record DoctorPanel(AssignmentView Assignment, ClinicStatus ClinicStatus, QueueRow? Current,
    IReadOnlyList<QueueRow> Waiting, int ServedByMe, double? MyAvgServiceMinutes, int ClinicCompleted, int ClinicNoShows);

public sealed class QueueQueryService(IDataStoreFactory factory, IClock clock)
{
    public async Task<List<Clinic>> GetClinicsAsync(bool activeOnly = true)
    {
        await using var store = factory.Create();
        var q = store.Query<Clinic>();
        if (activeOnly) q = q.Where(c => c.IsActive);
        return await store.ToListAsync(q.OrderBy(c => c.SortOrder).ThenBy(c => c.Code));
    }

    public async Task<List<DisplayClinic>> GetDisplayAsync(IReadOnlyCollection<int> clinicIds, int nextCount)
    {
        var today = clock.Today;
        await using var store = factory.Create();
        var clinics = await store.ToListAsync(store.Query<Clinic>().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.Code));
        if (clinicIds.Count > 0) clinics = clinics.Where(c => clinicIds.Contains(c.Id)).ToList();
        var tickets = await store.ToListAsync(store.Query<QueueTicket>()
            .Where(t => t.ServiceDate == today && (t.Status == TicketStatus.Waiting || t.Status == TicketStatus.Called)));
        var rooms = (await store.ToListAsync(store.Query<Room>())).ToDictionary(r => r.Id, r => r.Number);

        return clinics.Select(c =>
        {
            var mine = tickets.Where(t => t.ClinicId == c.Id).ToList();
            var serving = mine.Where(t => t.Status == TicketStatus.Called)
                .OrderByDescending(t => t.LastCalledAt)
                .Select(t => new ServingItem(t.Number, t.RoomId is int r && rooms.TryGetValue(r, out var n) ? n : 0, t.LastCalledAt ?? t.IssuedAt))
                .ToList();
            var waiting = QueueOrdering.Ordered(mine);
            return new DisplayClinic(c.Id, c.Code, c.NameAr, c.NameEn, c.Status, serving,
                waiting.Take(nextCount).Select(t => t.Number).ToList(), waiting.Count);
        }).ToList();
    }

    /// <summary>Public tracking page. Shows only the ticket's own data.</summary>
    public async Task<TicketStatusView?> GetTicketStatusAsync(string publicId)
    {
        if (string.IsNullOrWhiteSpace(publicId)) return null;
        await using var store = factory.Create();
        var t = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.PublicId == publicId));
        if (t is null) return null;
        var clinic = await store.FirstOrDefaultAsync(store.Query<Clinic>().Where(c => c.Id == t.ClinicId));
        var patient = await store.FirstOrDefaultAsync(store.Query<Patient>().Where(p => p.Id == t.PatientId));
        int? room = null;
        if (t.RoomId is int rid) room = (await store.FirstOrDefaultAsync(store.Query<Room>().Where(r => r.Id == rid)))?.Number;

        var clinicTickets = await store.ToListAsync(store.Query<QueueTicket>().Where(x => x.ClinicId == t.ClinicId && x.ServiceDate == t.ServiceDate));
        var ahead = QueueOrdering.PeopleAhead(QueueOrdering.Ordered(clinicTickets), t.Id);

        int? estimate = null;
        if (t.Status == TicketStatus.Waiting)
        {
            var done = clinicTickets.Where(x => x.Status == TicketStatus.Completed && x.LastCalledAt != null && x.CompletedAt != null)
                .Select(x => (x.CompletedAt!.Value - x.LastCalledAt!.Value).TotalMinutes).ToList();
            var avg = done.Count >= 3 ? done.Average() : 10; // assume 10 min until we have data
            var activeDoctors = await store.CountAsync(store.Query<DoctorAssignment>().Where(a =>
                a.ClinicId == t.ClinicId && a.ServiceDate == t.ServiceDate && a.Status == AssignmentStatus.Active));
            estimate = (int)Math.Ceiling((ahead + 1) * avg / Math.Max(1, activeDoctors));
        }

        string? transferredTo = null;
        if (t.Status == TicketStatus.Transferred)
            transferredTo = (await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.TransferredFromTicketId == t.Id)))?.PublicId;

        return new TicketStatusView(t.Number, clinic?.NameAr ?? "", clinic?.NameEn ?? "", t.Status, Math.Max(0, ahead), room,
            estimate, t.IssuedAt, patient?.Language ?? "ar", transferredTo);
    }

    /// <summary>Today's tickets with patient details – for reception and doctors only.</summary>
    public async Task<List<QueueRow>> GetTodayTicketsAsync(int? clinicId = null)
    {
        var today = clock.Today;
        await using var store = factory.Create();
        var q = store.Query<QueueTicket>().Where(t => t.ServiceDate == today);
        if (clinicId is int cid) q = q.Where(t => t.ClinicId == cid);
        var tickets = await store.ToListAsync(q);
        return await ToRowsAsync(store, tickets);
    }

    public async Task<List<AssignmentView>> GetAssignmentsAsync(DateTime date, int? doctorId = null)
    {
        await using var store = factory.Create();
        return await AssignmentViewsAsync(store, date.Date, doctorId);
    }

    public async Task<DoctorPanel?> GetDoctorPanelAsync(int assignmentId)
    {
        await using var store = factory.Create();
        var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == assignmentId));
        if (a is null) return null;
        var view = (await AssignmentViewsAsync(store, a.ServiceDate, null)).First(v => v.Id == a.Id);
        var clinic = await store.FirstOrDefaultAsync(store.Query<Clinic>().Where(c => c.Id == a.ClinicId));
        var tickets = await store.ToListAsync(store.Query<QueueTicket>().Where(t => t.ClinicId == a.ClinicId && t.ServiceDate == a.ServiceDate));
        var rows = await ToRowsAsync(store, tickets);
        var waitingOrder = QueueOrdering.Ordered(tickets).Select(t => t.Id).ToList();
        var waiting = waitingOrder.Select(id => rows.First(r => r.TicketId == id)).ToList();
        var current = a.CurrentTicketId is int cid ? rows.FirstOrDefault(r => r.TicketId == cid) : null;
        var mine = tickets.Where(t => t.AssignmentId == a.Id && t.Status == TicketStatus.Completed && t.CompletedAt != null && t.LastCalledAt != null).ToList();
        double? avg = mine.Count == 0 ? null : Math.Round(mine.Average(t => (t.CompletedAt!.Value - t.LastCalledAt!.Value).TotalMinutes), 1);
        return new DoctorPanel(view, clinic?.Status ?? ClinicStatus.Closed, current, waiting, mine.Count, avg,
            tickets.Count(t => t.Status == TicketStatus.Completed), tickets.Count(t => t.Status == TicketStatus.NoShow));
    }

    private static async Task<List<AssignmentView>> AssignmentViewsAsync(IDataStore store, DateTime date, int? doctorId)
    {
        var q = store.Query<DoctorAssignment>().Where(a => a.ServiceDate == date);
        if (doctorId is int d) q = q.Where(a => a.DoctorId == d);
        var list = await store.ToListAsync(q);
        var doctors = (await store.ToListAsync(store.Query<Doctor>())).ToDictionary(x => x.Id);
        var clinics = (await store.ToListAsync(store.Query<Clinic>())).ToDictionary(x => x.Id);
        var rooms = (await store.ToListAsync(store.Query<Room>())).ToDictionary(x => x.Id);
        return list.Select(a => new AssignmentView(a.Id, a.DoctorId,
                doctors.TryGetValue(a.DoctorId, out var doc) ? doc.NameAr : "?", doc?.NameEn ?? "?",
                a.ClinicId, clinics.TryGetValue(a.ClinicId, out var c) ? c.NameAr : "?", c?.NameEn ?? "?",
                a.RoomId, rooms.TryGetValue(a.RoomId, out var r) ? r.Number : 0, a.Shift, a.Status, a.CurrentTicketId, a.ServiceDate))
            .OrderBy(v => v.Shift).ThenBy(v => clinics.TryGetValue(v.ClinicId, out var cc) ? cc.SortOrder : 0).ThenBy(v => v.RoomNumber)
            .ToList();
    }

    private static async Task<List<QueueRow>> ToRowsAsync(IDataStore store, List<QueueTicket> tickets)
    {
        if (tickets.Count == 0) return [];
        var date = tickets[0].ServiceDate;
        var patients = (await store.ToListAsync(store.Query<Patient>().Where(p => p.ServiceDate == date))).ToDictionary(p => p.Id);
        var rooms = (await store.ToListAsync(store.Query<Room>())).ToDictionary(r => r.Id, r => r.Number);
        return tickets.OrderBy(t => t.IssuedAt).Select(t =>
        {
            patients.TryGetValue(t.PatientId, out var p);
            return new QueueRow(t.Id, t.Number, p?.FullName ?? "—", p is null ? "" : PhoneNumbers.ToLocal(p.Phone), t.Complaint,
                t.IsPriority, t.SkipCount, t.Status, t.Source, t.IssuedAt, t.ClinicId,
                t.RoomId is int r && rooms.TryGetValue(r, out var n) ? n : null, t.DoctorId, t.PublicId);
        }).ToList();
    }
}
