using System.Text.Json;
using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;

namespace JIH.Queue.Core.Services;

/// <summary>
/// End-of-day procedure ("daily reset"). For each service date that still has live data:
///  1. Waiting tickets → NotSeen; tickets still with a doctor → Completed (auto-closed).
///  2. A DailyReport is generated from the tickets, notifications and audit log.
///  3. Each ticket is copied to VisitHistory WITHOUT name, phone or complaint.
///  4. Tickets, notification logs and patients of that date are deleted.
///  5. Assignments of that date are finished; clinics marked Full are re-opened.
/// The audit log itself is kept (it contains no patient data).
/// </summary>
public sealed class EndOfDayService(IDataStoreFactory factory, IClock clock, QueueEvents events)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Closes <paramref name="upToDate"/> and every earlier date that still has live data. Returns created report ids.</summary>
    public async Task<List<int>> RunAsync(ResetTrigger trigger, string username, DateTime? upToDate = null)
    {
        await _gate.WaitAsync();
        var reportIds = new List<int>();
        var affectedClinics = new HashSet<int>();
        try
        {
            var limit = (upToDate ?? clock.Today).Date;
            List<DateTime> dates;
            await using (var store = factory.Create())
            {
                var t = await store.ToListAsync(store.Query<QueueTicket>().Where(x => x.ServiceDate <= limit).Select(x => x.ServiceDate));
                var p = await store.ToListAsync(store.Query<Patient>().Where(x => x.ServiceDate <= limit).Select(x => x.ServiceDate));
                dates = t.Concat(p).Distinct().OrderBy(d => d).ToList();
                // Always produce a report for the requested day (even an empty one) on a manual/scheduled run.
                if (!dates.Contains(limit) && !await store.AnyAsync(store.Query<DailyReport>().Where(r => r.ServiceDate == limit)))
                    dates.Add(limit);
            }

            foreach (var date in dates)
            {
                var (id, clinics) = await CloseDateAsync(date, trigger, username);
                reportIds.Add(id);
                affectedClinics.UnionWith(clinics);
            }
        }
        finally
        {
            _gate.Release();
        }
        foreach (var c in affectedClinics) events.PublishQueueChanged(c);
        events.PublishConfigurationChanged();
        return reportIds;
    }

    /// <summary>True if there is live data from a previous day (server was off at reset time).</summary>
    public async Task<bool> HasStaleDataAsync()
    {
        var today = clock.Today;
        await using var store = factory.Create();
        return await store.AnyAsync(store.Query<QueueTicket>().Where(t => t.ServiceDate < today))
            || await store.AnyAsync(store.Query<Patient>().Where(p => p.ServiceDate < today));
    }

    public async Task<bool> ScheduledReportExistsAsync(DateTime date)
    {
        await using var store = factory.Create();
        return await store.AnyAsync(store.Query<DailyReport>().Where(r => r.ServiceDate == date && r.Trigger == ResetTrigger.Scheduled));
    }

    private async Task<(int reportId, HashSet<int> clinics)> CloseDateAsync(DateTime date, ResetTrigger trigger, string username)
    {
        var now = clock.Now;
        await using var store = factory.Create();
        var tickets = await store.ToListAsync(store.Query<QueueTicket>().Where(t => t.ServiceDate == date));
        var patients = await store.ToListAsync(store.Query<Patient>().Where(p => p.ServiceDate == date));
        var ticketIds = tickets.Select(t => t.Id).ToHashSet();
        var notifications = (await store.ToListAsync(store.Query<NotificationLog>())).Where(n => ticketIds.Contains(n.TicketId)).ToList();
        var dayEnd = date.AddDays(1);
        var auditCount = await store.CountAsync(store.Query<AuditLog>().Where(a => a.Timestamp >= date && a.Timestamp < dayEnd));

        // 1. close open tickets
        foreach (var t in tickets)
        {
            if (t.Status == TicketStatus.Waiting) { t.Status = TicketStatus.NotSeen; t.CompletedAt = now; }
            else if (t.Status == TicketStatus.Called) { t.Status = TicketStatus.Completed; t.CompletedAt ??= now; }
        }

        // 2. report (includes tickets already archived by an earlier run on the same date)
        var earlier = await store.ToListAsync(store.Query<VisitHistory>().Where(v => v.ServiceDate == date));
        var rows = tickets.Select(ReportService.FromTicket).Concat(earlier.Select(ReportService.FromHistory)).ToList();
        var summary = await ReportService.BuildAsync(store, rows, date, date);
        var report = new DailyReport
        {
            ServiceDate = date,
            GeneratedAt = now,
            GeneratedBy = username,
            Trigger = trigger,
            TotalTickets = summary.Total,
            Completed = summary.Completed,
            NoShows = summary.NoShows,
            NotSeen = summary.NotSeen,
            Transferred = summary.Transferred,
            Cancelled = summary.Cancelled,
            AvgWaitMinutes = summary.AvgWaitMinutes,
            AvgServiceMinutes = summary.AvgServiceMinutes,
            NotificationsSent = notifications.Count(n => n.Status is NotificationStatus.Sent or NotificationStatus.Mocked),
            NotificationsFailed = notifications.Count(n => n.Status == NotificationStatus.Failed),
            AuditEventCount = auditCount,
            DetailsJson = JsonSerializer.Serialize(summary)
        };
        store.Add(report);

        // 3. anonymous history
        foreach (var t in tickets)
        {
            double? wait = t.FirstCalledAt is null ? null : Math.Round(Math.Max(0, (t.FirstCalledAt.Value - t.IssuedAt).TotalMinutes), 2);
            double? service = t.Status == TicketStatus.Completed && t.CompletedAt != null && t.LastCalledAt != null
                ? Math.Round(Math.Max(0, (t.CompletedAt.Value - t.LastCalledAt.Value).TotalMinutes), 2) : null;
            store.Add(new VisitHistory
            {
                ServiceDate = t.ServiceDate, ClinicId = t.ClinicId, DoctorId = t.DoctorId, RoomId = t.RoomId, Sequence = t.Sequence,
                TicketNumber = t.Number, Source = t.Source, Outcome = t.Status, IsPriority = t.IsPriority, SkipCount = t.SkipCount,
                IssuedAt = t.IssuedAt, FirstCalledAt = t.FirstCalledAt, CompletedAt = t.CompletedAt,
                WaitMinutes = wait, ServiceMinutes = service, IssuedHour = t.IssuedAt.Hour
            });
        }

        // 5. assignments & clinics
        var assignments = await store.ToListAsync(store.Query<DoctorAssignment>().Where(a => a.ServiceDate == date));
        foreach (var a in assignments) { a.CurrentTicketId = null; if (a.Status != AssignmentStatus.Scheduled) a.Status = AssignmentStatus.Finished; }
        if (date == clock.Today || date < clock.Today)
        {
            foreach (var c in await store.ToListAsync(store.Query<Clinic>().Where(c => c.Status == ClinicStatus.Full)))
            {
                c.Status = ClinicStatus.Open;
                Audit.Write(store, clock, username, AuditActions.ClinicStatusChanged, nameof(Clinic), c.Id, c.Id, "Full → Open (end of day)");
            }
        }
        await store.SaveChangesAsync();

        // 4. delete personal data
        store.RemoveRange(notifications);
        store.RemoveRange(tickets);
        store.RemoveRange(patients);
        Audit.Write(store, clock, username, AuditActions.EndOfDay, nameof(DailyReport), report.Id, null,
            $"{date:yyyy-MM-dd} {trigger}: {summary.Total} tickets archived, {patients.Count} patient records deleted");
        await store.SaveChangesAsync();

        return (report.Id, tickets.Select(t => t.ClinicId).ToHashSet());
    }
}
