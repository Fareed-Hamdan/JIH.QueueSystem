using System.Collections.Concurrent;
using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;
using JIH.Queue.Core.Notifications;
using JIH.Queue.Core.Rules;

namespace JIH.Queue.Core.Services;

public sealed record IssueTicketRequest
{
    public string FullName { get; init; } = "";
    public string Phone { get; init; } = "";
    public int ClinicId { get; init; }
    public string Complaint { get; init; } = "";
    public string Language { get; init; } = "ar";
    public bool IsPriority { get; init; }
    public bool ConsentGiven { get; init; }
    public TicketSource Source { get; init; }
    public string IssuedBy { get; init; } = "";
}

public sealed record IssuedTicket(int TicketId, string PublicId, string Number, int PeopleAhead);

public sealed record CalledTicket(int TicketId, string Number, int RoomNumber);

/// <summary>
/// All queue business rules. Registered as a singleton; every operation on a clinic queue is
/// serialised by a per-clinic lock so two doctors pressing "Call next" at the same moment
/// can never receive the same patient.
/// </summary>
public sealed class QueueService(
    IDataStoreFactory factory,
    IClock clock,
    SettingsService settings,
    QueueEvents events,
    NotificationQueue notifications)
{
    public const int MaxNameLength = 100;
    public const int MaxComplaintLength = 500;

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _clinicLocks = new();

    private async Task<IDisposable> LockClinicAsync(int clinicId)
    {
        var sem = _clinicLocks.GetOrAdd(clinicId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        return new Releaser(sem);
    }

    private sealed class Releaser(SemaphoreSlim s) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) s.Release(); }
    }

    // ================================================================ Issue

    public async Task<OpResult<IssuedTicket>> IssueAsync(IssueTicketRequest req)
    {
        // ---- input validation (no data touched yet)
        var name = (req.FullName ?? "").Trim();
        var complaint = (req.Complaint ?? "").Trim();
        if (name.Length < 2 || name.Length > MaxNameLength) return OpResult<IssuedTicket>.Fail("err.name");
        if (!PhoneNumbers.TryNormalize(req.Phone, out var phone)) return OpResult<IssuedTicket>.Fail("err.phone");
        if (complaint.Length < 3 || complaint.Length > MaxComplaintLength) return OpResult<IssuedTicket>.Fail("err.complaint");
        if (!req.ConsentGiven) return OpResult<IssuedTicket>.Fail("err.consent");
        if (req.IsPriority && req.Source == TicketSource.SelfServiceQr) return OpResult<IssuedTicket>.Fail("err.priorityReceptionOnly");

        var s = await settings.GetAsync();
        var now = clock.Now;
        var today = now.Date;
        IssuedTicket result;
        bool becameFull = false;

        using (await LockClinicAsync(req.ClinicId))
        {
            await using var store = factory.Create();
            var clinic = await store.FirstOrDefaultAsync(store.Query<Clinic>().Where(c => c.Id == req.ClinicId && c.IsActive));
            if (clinic is null) return OpResult<IssuedTicket>.Fail("err.clinicNotFound");
            if (clinic.Status == ClinicStatus.Closed) return OpResult<IssuedTicket>.Fail("err.clinicClosed");
            if (clinic.Status == ClinicStatus.Full) return OpResult<IssuedTicket>.Fail("err.clinicFull");
            if (!string.IsNullOrWhiteSpace(clinic.TicketCutoffTime)
                && TimeSpan.TryParse(clinic.TicketCutoffTime, out var cutoff) && now.TimeOfDay > cutoff)
                return OpResult<IssuedTicket>.Fail("err.clinicCutoff", clinic.TicketCutoffTime);

            var todays = await store.ToListAsync(store.Query<QueueTicket>()
                .Where(t => t.ClinicId == clinic.Id && t.ServiceDate == today));

            // Daily limit counts every ticket of the day, including those already archived by an early reset.
            var archivedMaxSeq = await store.ToListAsync(store.Query<VisitHistory>()
                .Where(v => v.ClinicId == clinic.Id && v.ServiceDate == today).Select(v => v.Sequence));
            var issuedToday = todays.Count + archivedMaxSeq.Count;
            if (clinic.DailyTicketLimit is int limit && issuedToday >= limit)
            {
                clinic.Status = ClinicStatus.Full;
                Audit.Write(store, clock, "system", AuditActions.ClinicStatusChanged, nameof(Clinic), clinic.Id, clinic.Id, "Full (daily limit reached)");
                await store.SaveChangesAsync();
                events.PublishConfigurationChanged();
                return OpResult<IssuedTicket>.Fail("err.clinicFull");
            }

            // One active ticket per phone per clinic per day.
            var patient = await store.FirstOrDefaultAsync(store.Query<Patient>().Where(p => p.ServiceDate == today && p.Phone == phone));
            if (patient is not null)
            {
                var existing = todays.FirstOrDefault(t => t.PatientId == patient.Id && t.Status is TicketStatus.Waiting or TicketStatus.Called);
                if (existing is not null) return OpResult<IssuedTicket>.Fail("err.duplicateTicket", existing.Number);
            }

            // ---- mutations
            if (patient is null)
            {
                patient = new Patient { ServiceDate = today, Phone = phone, CreatedAt = now };
                store.Add(patient);
            }
            patient.FullName = name;
            patient.Language = req.Language == "en" ? "en" : "ar";
            patient.ConsentAt = now;
            await store.SaveChangesAsync(); // need patient.Id

            var seq = Math.Max(todays.Count == 0 ? 0 : todays.Max(t => t.Sequence),
                              archivedMaxSeq.Count == 0 ? 0 : archivedMaxSeq.Max()) + 1;

            var ordered = QueueOrdering.Ordered(todays);
            var ticket = new QueueTicket
            {
                PublicId = Guid.NewGuid().ToString("N"),
                ServiceDate = today,
                ClinicId = clinic.Id,
                PatientId = patient.Id,
                Sequence = seq,
                Number = QueueOrdering.FormatNumber(clinic.Code, seq),
                Complaint = complaint,
                Status = TicketStatus.Waiting,
                Source = req.Source,
                IsPriority = req.IsPriority,
                IssuedAt = now,
                OriginalIssuedAt = now,
                IssuedBy = req.Source == TicketSource.SelfServiceQr ? "self-service" : req.IssuedBy
            };
            var index = QueueOrdering.InsertIndexForNew(ordered, req.IsPriority);
            QueueOrdering.InsertAndRenumber(ordered, ticket, index);
            ticket.NotifiedIssued = true;
            ticket.NotifiedApproaching = index <= s.NotifyAheadCount; // the issue message already says how close they are
            store.Add(ticket);

            if (clinic.DailyTicketLimit is int lim && issuedToday + 1 >= lim)
            {
                clinic.Status = ClinicStatus.Full;
                becameFull = true;
                Audit.Write(store, clock, "system", AuditActions.ClinicStatusChanged, nameof(Clinic), clinic.Id, clinic.Id, "Full (daily limit reached)");
            }

            await store.SaveChangesAsync();
            Audit.Write(store, clock, req.IssuedBy, AuditActions.TicketIssued, nameof(QueueTicket), ticket.Id, clinic.Id,
                $"{ticket.Number} via {req.Source}{(req.IsPriority ? " (priority)" : "")}");
            await store.SaveChangesAsync();
            result = new IssuedTicket(ticket.Id, ticket.PublicId, ticket.Number, index);
        }

        notifications.Enqueue(new NotificationJob(result.TicketId, NotificationKind.TicketIssued));
        events.PublishQueueChanged(req.ClinicId);
        if (becameFull) events.PublishConfigurationChanged();
        return OpResult<IssuedTicket>.Success(result);
    }

    // ================================================================ Doctor actions

    /// <summary>Calls the next waiting patient of the assignment's clinic to the assignment's room.</summary>
    public async Task<OpResult<CalledTicket>> CallNextAsync(int assignmentId, string username)
    {
        var (assignment, error) = await LoadAssignmentAsync(assignmentId);
        if (assignment is null) return OpResult<CalledTicket>.Fail(error!);
        CalledTicket called;
        Announcement announcement;

        using (await LockClinicAsync(assignment.ClinicId))
        {
            await using var store = factory.Create();
            var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == assignmentId));
            if (a is null) return OpResult<CalledTicket>.Fail("err.assignmentNotFound");
            if (a.ServiceDate != clock.Today) return OpResult<CalledTicket>.Fail("err.assignmentNotToday");
            if (a.Status != AssignmentStatus.Active) return OpResult<CalledTicket>.Fail("err.assignmentNotActive");
            if (a.CurrentTicketId is not null) return OpResult<CalledTicket>.Fail("err.finishCurrentFirst");

            var clinic = await store.FirstOrDefaultAsync(store.Query<Clinic>().Where(c => c.Id == a.ClinicId));
            if (clinic is null) return OpResult<CalledTicket>.Fail("err.clinicNotFound");
            if (clinic.Status == ClinicStatus.Closed) return OpResult<CalledTicket>.Fail("err.clinicClosed");
            if (clinic.Status == ClinicStatus.Paused) return OpResult<CalledTicket>.Fail("err.clinicPaused");

            var waiting = QueueOrdering.Ordered(await store.ToListAsync(store.Query<QueueTicket>()
                .Where(t => t.ClinicId == a.ClinicId && t.ServiceDate == a.ServiceDate && t.Status == TicketStatus.Waiting)));
            if (waiting.Count == 0) return OpResult<CalledTicket>.Fail("err.queueEmpty");

            var room = await store.FirstOrDefaultAsync(store.Query<Room>().Where(r => r.Id == a.RoomId));
            var t = waiting[0];
            var now = clock.Now;
            t.Status = TicketStatus.Called;
            t.FirstCalledAt ??= now;
            t.LastCalledAt = now;
            t.AssignmentId = a.Id;
            t.DoctorId = a.DoctorId;
            t.RoomId = a.RoomId;
            a.CurrentTicketId = t.Id;
            waiting.RemoveAt(0);
            QueueOrdering.Renumber(waiting);
            Audit.Write(store, clock, username, AuditActions.TicketCalled, nameof(QueueTicket), t.Id, a.ClinicId,
                $"{t.Number} → room {room?.Number}");
            var approaching = MarkApproaching(waiting, (await settings.GetAsync()).NotifyAheadCount);
            await store.SaveChangesAsync();

            called = new CalledTicket(t.Id, t.Number, room?.Number ?? 0);
            announcement = new Announcement(a.ClinicId, t.Number, room?.Number ?? 0, false, now);
            if (!t.NotifiedCalled) { t.NotifiedCalled = true; await store.SaveChangesAsync(); notifications.Enqueue(new NotificationJob(t.Id, NotificationKind.Called)); }
            foreach (var id in approaching) notifications.Enqueue(new NotificationJob(id, NotificationKind.TurnApproaching));
        }

        events.PublishAnnouncement(announcement);
        events.PublishQueueChanged(assignment.ClinicId);
        return OpResult<CalledTicket>.Success(called);
    }

    /// <summary>Announces the current patient again. Does not change the queue.</summary>
    public async Task<OpResult> RecallAsync(int assignmentId, string username)
    {
        Announcement announcement;
        int clinicId;
        await using (var store = factory.Create())
        {
            var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == assignmentId));
            if (a is null) return OpResult.Fail("err.assignmentNotFound");
            if (a.CurrentTicketId is not int tid) return OpResult.Fail("err.noCurrentPatient");
            var t = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.Id == tid));
            if (t is null) return OpResult.Fail("err.noCurrentPatient");
            var room = await store.FirstOrDefaultAsync(store.Query<Room>().Where(r => r.Id == a.RoomId));
            t.RecallCount++;
            t.LastCalledAt = clock.Now;
            Audit.Write(store, clock, username, AuditActions.TicketRecalled, nameof(QueueTicket), t.Id, a.ClinicId, $"{t.Number} recall #{t.RecallCount}");
            await store.SaveChangesAsync();
            announcement = new Announcement(a.ClinicId, t.Number, room?.Number ?? 0, true, clock.Now);
            clinicId = a.ClinicId;
        }
        events.PublishAnnouncement(announcement);
        events.PublishQueueChanged(clinicId);
        return OpResult.Success();
    }

    /// <summary>
    /// Patient did not come. First time: moved back SkipOffset positions (default 3).
    /// After MaxSkips skips (default 1): marked No-show.
    /// Returns true when the ticket became a no-show.
    /// </summary>
    public async Task<OpResult<bool>> SkipAsync(int assignmentId, string username)
    {
        var (assignment, error) = await LoadAssignmentAsync(assignmentId);
        if (assignment is null) return OpResult<bool>.Fail(error!);
        bool noShow;
        using (await LockClinicAsync(assignment.ClinicId))
        {
            var s = await settings.GetAsync();
            await using var store = factory.Create();
            var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == assignmentId));
            if (a?.CurrentTicketId is not int tid) return OpResult<bool>.Fail("err.noCurrentPatient");
            var t = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.Id == tid));
            if (t is null || t.Status != TicketStatus.Called) return OpResult<bool>.Fail("err.noCurrentPatient");

            var waiting = QueueOrdering.Ordered(await store.ToListAsync(store.Query<QueueTicket>()
                .Where(x => x.ClinicId == t.ClinicId && x.ServiceDate == t.ServiceDate && x.Status == TicketStatus.Waiting)));

            a.CurrentTicketId = null;
            if (t.SkipCount >= s.MaxSkips)
            {
                noShow = true;
                t.Status = TicketStatus.NoShow;
                t.CompletedAt = clock.Now;
                Audit.Write(store, clock, username, AuditActions.TicketNoShow, nameof(QueueTicket), t.Id, t.ClinicId,
                    $"{t.Number} marked no-show after {t.SkipCount} skip(s)");
            }
            else
            {
                noShow = false;
                t.SkipCount++;
                t.Status = TicketStatus.Waiting;
                t.AssignmentId = null; t.DoctorId = null; t.RoomId = null;
                t.NotifiedApproaching = false;
                t.NotifiedCalled = false;
                var index = QueueOrdering.InsertIndexForSkip(waiting, s.SkipOffset);
                QueueOrdering.InsertAndRenumber(waiting, t, index);
                Audit.Write(store, clock, username, AuditActions.TicketSkipped, nameof(QueueTicket), t.Id, t.ClinicId,
                    $"{t.Number} moved back {index} position(s), skip #{t.SkipCount}");
            }
            var approaching = MarkApproaching(waiting, s.NotifyAheadCount);
            await store.SaveChangesAsync();
            foreach (var id in approaching) notifications.Enqueue(new NotificationJob(id, NotificationKind.TurnApproaching));
        }
        events.PublishQueueChanged(assignment.ClinicId);
        return OpResult<bool>.Success(noShow);
    }

    public async Task<OpResult> EndVisitAsync(int assignmentId, string username)
    {
        int clinicId;
        await using (var store = factory.Create())
        {
            var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == assignmentId));
            if (a?.CurrentTicketId is not int tid) return OpResult.Fail("err.noCurrentPatient");
            var t = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.Id == tid));
            if (t is null) return OpResult.Fail("err.noCurrentPatient");
            t.Status = TicketStatus.Completed;
            t.CompletedAt = clock.Now;
            a.CurrentTicketId = null;
            Audit.Write(store, clock, username, AuditActions.VisitEnded, nameof(QueueTicket), t.Id, t.ClinicId, t.Number);
            await store.SaveChangesAsync();
            clinicId = a.ClinicId;
        }
        events.PublishQueueChanged(clinicId);
        return OpResult.Success();
    }

    /// <summary>Refers the current patient to another clinic. A new ticket is issued there (end of queue, or priority block).</summary>
    public async Task<OpResult<IssuedTicket>> TransferAsync(int assignmentId, int targetClinicId, bool urgent, string username)
    {
        var (assignment, error) = await LoadAssignmentAsync(assignmentId);
        if (assignment is null) return OpResult<IssuedTicket>.Fail(error!);
        if (assignment.ClinicId == targetClinicId) return OpResult<IssuedTicket>.Fail("err.transferSameClinic");
        IssuedTicket result;

        // Lock both clinics in id order to avoid deadlocks.
        var first = Math.Min(assignment.ClinicId, targetClinicId);
        var second = Math.Max(assignment.ClinicId, targetClinicId);
        using (await LockClinicAsync(first))
        using (await LockClinicAsync(second))
        {
            var s = await settings.GetAsync();
            await using var store = factory.Create();
            var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == assignmentId));
            if (a?.CurrentTicketId is not int tid) return OpResult<IssuedTicket>.Fail("err.noCurrentPatient");
            var t = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.Id == tid));
            if (t is null) return OpResult<IssuedTicket>.Fail("err.noCurrentPatient");
            var target = await store.FirstOrDefaultAsync(store.Query<Clinic>().Where(c => c.Id == targetClinicId && c.IsActive));
            if (target is null) return OpResult<IssuedTicket>.Fail("err.clinicNotFound");
            if (target.Status == ClinicStatus.Closed) return OpResult<IssuedTicket>.Fail("err.clinicClosed");
            // A referral is accepted even if the target clinic is Full.

            var targetTickets = await store.ToListAsync(store.Query<QueueTicket>()
                .Where(x => x.ClinicId == target.Id && x.ServiceDate == t.ServiceDate));
            var archived = await store.ToListAsync(store.Query<VisitHistory>()
                .Where(v => v.ClinicId == target.Id && v.ServiceDate == t.ServiceDate).Select(v => v.Sequence));
            var seq = Math.Max(targetTickets.Count == 0 ? 0 : targetTickets.Max(x => x.Sequence), archived.Count == 0 ? 0 : archived.Max()) + 1;

            var now = clock.Now;
            var nt = new QueueTicket
            {
                PublicId = Guid.NewGuid().ToString("N"),
                ServiceDate = t.ServiceDate,
                ClinicId = target.Id,
                PatientId = t.PatientId,
                Sequence = seq,
                Number = QueueOrdering.FormatNumber(target.Code, seq),
                Complaint = t.Complaint,
                Status = TicketStatus.Waiting,
                Source = TicketSource.Transfer,
                IsPriority = urgent || t.IsPriority,
                IssuedAt = now,
                OriginalIssuedAt = t.OriginalIssuedAt,
                TransferredFromTicketId = t.Id,
                IssuedBy = username
            };
            var ordered = QueueOrdering.Ordered(targetTickets);
            var index = QueueOrdering.InsertIndexForNew(ordered, nt.IsPriority);
            QueueOrdering.InsertAndRenumber(ordered, nt, index);
            nt.NotifiedIssued = true;
            nt.NotifiedApproaching = index <= s.NotifyAheadCount;
            store.Add(nt);

            t.Status = TicketStatus.Transferred;
            t.CompletedAt = now;
            a.CurrentTicketId = null;
            await store.SaveChangesAsync();
            Audit.Write(store, clock, username, AuditActions.TicketTransferred, nameof(QueueTicket), t.Id, t.ClinicId,
                $"{t.Number} → {nt.Number}{(urgent ? " (urgent)" : "")}");
            await store.SaveChangesAsync();
            result = new IssuedTicket(nt.Id, nt.PublicId, nt.Number, index);
        }
        notifications.Enqueue(new NotificationJob(result.TicketId, NotificationKind.TicketIssued));
        events.PublishQueueChanged(assignment.ClinicId);
        events.PublishQueueChanged(targetClinicId);
        return OpResult<IssuedTicket>.Success(result);
    }

    // ================================================================ Reception actions

    /// <summary>A no-show patient arrived after all: back to the end of the queue. The next skip makes them a no-show again.</summary>
    public async Task<OpResult> ReinstateAsync(int ticketId, string username)
    {
        int clinicId;
        var t0 = await GetTicketAsync(ticketId);
        if (t0 is null) return OpResult.Fail("err.ticketNotFound");
        using (await LockClinicAsync(t0.ClinicId))
        {
            await using var store = factory.Create();
            var t = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.Id == ticketId));
            if (t is null) return OpResult.Fail("err.ticketNotFound");
            if (t.Status != TicketStatus.NoShow) return OpResult.Fail("err.onlyNoShowReinstate");
            if (t.ServiceDate != clock.Today) return OpResult.Fail("err.ticketNotToday");
            var waiting = QueueOrdering.Ordered(await store.ToListAsync(store.Query<QueueTicket>()
                .Where(x => x.ClinicId == t.ClinicId && x.ServiceDate == t.ServiceDate && x.Status == TicketStatus.Waiting)));
            t.Status = TicketStatus.Waiting;
            t.CompletedAt = null;
            t.AssignmentId = null; t.DoctorId = null; t.RoomId = null;
            t.NotifiedApproaching = false; t.NotifiedCalled = false;
            QueueOrdering.InsertAndRenumber(waiting, t, waiting.Count);
            Audit.Write(store, clock, username, AuditActions.TicketReinstated, nameof(QueueTicket), t.Id, t.ClinicId, t.Number);
            await store.SaveChangesAsync();
            clinicId = t.ClinicId;
        }
        events.PublishQueueChanged(clinicId);
        return OpResult.Success();
    }

    public async Task<OpResult> SetPriorityAsync(int ticketId, bool priority, string username)
    {
        var t0 = await GetTicketAsync(ticketId);
        if (t0 is null) return OpResult.Fail("err.ticketNotFound");
        using (await LockClinicAsync(t0.ClinicId))
        {
            await using var store = factory.Create();
            var t = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.Id == ticketId));
            if (t is null || t.Status != TicketStatus.Waiting) return OpResult.Fail("err.onlyWaiting");
            if (t.IsPriority == priority) return OpResult.Success();
            var waiting = QueueOrdering.Ordered(await store.ToListAsync(store.Query<QueueTicket>()
                .Where(x => x.ClinicId == t.ClinicId && x.ServiceDate == t.ServiceDate && x.Status == TicketStatus.Waiting)));
            t.IsPriority = priority;
            if (priority)
            {
                waiting.Remove(t);
                QueueOrdering.InsertAndRenumber(waiting, t, QueueOrdering.InsertIndexForNew(waiting, true));
            }
            // Removing the priority flag keeps the current position (no one is pushed back unfairly).
            Audit.Write(store, clock, username, AuditActions.TicketPriorityChanged, nameof(QueueTicket), t.Id, t.ClinicId,
                $"{t.Number} priority={priority}");
            var approaching = MarkApproaching(waiting, (await settings.GetAsync()).NotifyAheadCount);
            await store.SaveChangesAsync();
            foreach (var id in approaching) notifications.Enqueue(new NotificationJob(id, NotificationKind.TurnApproaching));
        }
        events.PublishQueueChanged(t0.ClinicId);
        return OpResult.Success();
    }

    /// <summary>Reception moves a waiting ticket to another clinic (wrong clinic chosen). A new number is issued.</summary>
    public async Task<OpResult<IssuedTicket>> ChangeClinicAsync(int ticketId, int targetClinicId, string username)
    {
        var t0 = await GetTicketAsync(ticketId);
        if (t0 is null) return OpResult<IssuedTicket>.Fail("err.ticketNotFound");
        if (t0.ClinicId == targetClinicId) return OpResult<IssuedTicket>.Fail("err.transferSameClinic");
        IssuedTicket result;
        var first = Math.Min(t0.ClinicId, targetClinicId);
        var second = Math.Max(t0.ClinicId, targetClinicId);
        using (await LockClinicAsync(first))
        using (await LockClinicAsync(second))
        {
            await using var store = factory.Create();
            var t = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.Id == ticketId));
            if (t is null || t.Status != TicketStatus.Waiting) return OpResult<IssuedTicket>.Fail("err.onlyWaiting");
            var target = await store.FirstOrDefaultAsync(store.Query<Clinic>().Where(c => c.Id == targetClinicId && c.IsActive));
            if (target is null) return OpResult<IssuedTicket>.Fail("err.clinicNotFound");
            if (target.Status is ClinicStatus.Closed or ClinicStatus.Full) return OpResult<IssuedTicket>.Fail(target.Status == ClinicStatus.Closed ? "err.clinicClosed" : "err.clinicFull");

            var targetTickets = await store.ToListAsync(store.Query<QueueTicket>().Where(x => x.ClinicId == target.Id && x.ServiceDate == t.ServiceDate));
            var archived = await store.ToListAsync(store.Query<VisitHistory>()
                .Where(v => v.ClinicId == target.Id && v.ServiceDate == t.ServiceDate).Select(v => v.Sequence));
            var seq = Math.Max(targetTickets.Count == 0 ? 0 : targetTickets.Max(x => x.Sequence), archived.Count == 0 ? 0 : archived.Max()) + 1;
            var oldNumber = t.Number;
            var oldClinic = t.ClinicId;
            var ordered = QueueOrdering.Ordered(targetTickets);
            var index = QueueOrdering.InsertIndexForNew(ordered, t.IsPriority);
            t.ClinicId = target.Id;
            t.Sequence = seq;
            t.Number = QueueOrdering.FormatNumber(target.Code, seq);
            t.IssuedAt = clock.Now;
            t.NotifiedApproaching = false;
            QueueOrdering.InsertAndRenumber(ordered, t, index);
            var oldWaiting = QueueOrdering.Ordered(await store.ToListAsync(store.Query<QueueTicket>()
                .Where(x => x.ClinicId == oldClinic && x.ServiceDate == t.ServiceDate && x.Status == TicketStatus.Waiting && x.Id != t.Id)));
            QueueOrdering.Renumber(oldWaiting);
            Audit.Write(store, clock, username, AuditActions.TicketClinicChanged, nameof(QueueTicket), t.Id, target.Id, $"{oldNumber} → {t.Number}");
            await store.SaveChangesAsync();
            result = new IssuedTicket(t.Id, t.PublicId, t.Number, index);
        }
        notifications.Enqueue(new NotificationJob(result.TicketId, NotificationKind.TicketIssued));
        events.PublishQueueChanged(t0.ClinicId);
        events.PublishQueueChanged(targetClinicId);
        return OpResult<IssuedTicket>.Success(result);
    }

    public async Task<OpResult> CancelAsync(int ticketId, string username)
    {
        var t0 = await GetTicketAsync(ticketId);
        if (t0 is null) return OpResult.Fail("err.ticketNotFound");
        using (await LockClinicAsync(t0.ClinicId))
        {
            await using var store = factory.Create();
            var t = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.Id == ticketId));
            if (t is null || t.Status != TicketStatus.Waiting) return OpResult.Fail("err.onlyWaiting");
            t.Status = TicketStatus.Cancelled;
            t.CompletedAt = clock.Now;
            var waiting = QueueOrdering.Ordered(await store.ToListAsync(store.Query<QueueTicket>()
                .Where(x => x.ClinicId == t.ClinicId && x.ServiceDate == t.ServiceDate && x.Status == TicketStatus.Waiting && x.Id != t.Id)));
            QueueOrdering.Renumber(waiting);
            Audit.Write(store, clock, username, AuditActions.TicketCancelled, nameof(QueueTicket), t.Id, t.ClinicId, t.Number);
            var approaching = MarkApproaching(waiting, (await settings.GetAsync()).NotifyAheadCount);
            await store.SaveChangesAsync();
            foreach (var id in approaching) notifications.Enqueue(new NotificationJob(id, NotificationKind.TurnApproaching));
        }
        events.PublishQueueChanged(t0.ClinicId);
        return OpResult.Success();
    }

    // ================================================================ Assignment status (doctor)

    public async Task<OpResult> SetAssignmentStatusAsync(int assignmentId, AssignmentStatus status, string username)
    {
        int clinicId;
        await using (var store = factory.Create())
        {
            var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == assignmentId));
            if (a is null) return OpResult.Fail("err.assignmentNotFound");
            if (a.ServiceDate != clock.Today) return OpResult.Fail("err.assignmentNotToday");
            if (status is AssignmentStatus.Paused or AssignmentStatus.Finished && a.CurrentTicketId is not null)
                return OpResult.Fail("err.finishCurrentFirst");
            if (a.Status == AssignmentStatus.Finished && status != AssignmentStatus.Active)
                return OpResult.Fail("err.assignmentFinished");
            if (status == AssignmentStatus.Active)
            {
                // A doctor can only be active in one place at a time.
                var other = await store.AnyAsync(store.Query<DoctorAssignment>().Where(x =>
                    x.DoctorId == a.DoctorId && x.Id != a.Id && x.ServiceDate == a.ServiceDate && x.Status == AssignmentStatus.Active));
                if (other) return OpResult.Fail("err.doctorActiveElsewhere");
            }
            var old = a.Status;
            a.Status = status;
            Audit.Write(store, clock, username, AuditActions.AssignmentStatusChanged, nameof(DoctorAssignment), a.Id, a.ClinicId, $"{old} → {status}");
            await store.SaveChangesAsync();
            clinicId = a.ClinicId;
        }
        events.PublishQueueChanged(clinicId);
        events.PublishConfigurationChanged();
        return OpResult.Success();
    }

    // ================================================================ helpers

    /// <summary>Marks tickets that just came within NotifyAheadCount and returns their ids (to notify after saving).</summary>
    private static List<int> MarkApproaching(List<QueueTicket> ordered, int aheadCount)
    {
        var ids = new List<int>();
        for (var i = 0; i < ordered.Count && i <= aheadCount; i++)
        {
            var t = ordered[i];
            if (t.NotifiedApproaching || t.Id == 0) continue;
            t.NotifiedApproaching = true;
            ids.Add(t.Id);
        }
        return ids;
    }

    private async Task<(DoctorAssignment? a, string? error)> LoadAssignmentAsync(int id)
    {
        await using var store = factory.Create();
        var a = await store.FirstOrDefaultAsync(store.Query<DoctorAssignment>().Where(x => x.Id == id));
        return a is null ? (null, "err.assignmentNotFound") : (a, null);
    }

    private async Task<QueueTicket?> GetTicketAsync(int id)
    {
        await using var store = factory.Create();
        return await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(x => x.Id == id));
    }
}
