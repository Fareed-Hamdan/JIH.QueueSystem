using System.Globalization;
using System.Text;
using System.Text.Json;
using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;

namespace JIH.Queue.Core.Services;

public sealed record ClinicStat(int ClinicId, string Code, string NameAr, string NameEn, int Total, int Completed, int NoShows,
    int NotSeen, int Waiting, double? AvgWaitMinutes, double? AvgServiceMinutes, double? MaxWaitMinutes);

public sealed record DoctorStat(int DoctorId, string NameAr, string NameEn, int Completed, double? AvgServiceMinutes, int NoShows);

public sealed record DayStat(DateTime Date, int Total, int Completed, int NoShows, double? AvgWaitMinutes);

public sealed record ReportSummary
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int Total { get; init; }
    public int Completed { get; init; }
    public int NoShows { get; init; }
    public int NotSeen { get; init; }
    public int Transferred { get; init; }
    public int Cancelled { get; init; }
    public int Waiting { get; init; }
    public int InService { get; init; }
    public int Priority { get; init; }
    public int SelfService { get; init; }
    public double? AvgWaitMinutes { get; init; }
    public double? AvgServiceMinutes { get; init; }
    public double NoShowRate => Total == 0 ? 0 : Math.Round(100.0 * NoShows / Total, 1);
    public List<ClinicStat> Clinics { get; init; } = [];
    public List<DoctorStat> Doctors { get; init; } = [];
    public int[] IssuedByHour { get; init; } = new int[24];
    public List<DayStat> Days { get; init; } = [];
}

/// <summary>Builds statistics from either live tickets (today) or the anonymous VisitHistory (past days).</summary>
public sealed class ReportService(IDataStoreFactory factory, IClock clock)
{
    /// <summary>Uniform row used by both sources.</summary>
    public sealed record VisitRow(DateTime Date, int ClinicId, int? DoctorId, TicketStatus Status, bool Priority, TicketSource Source,
        DateTime IssuedAt, DateTime? FirstCalledAt, DateTime? LastCalledAt, DateTime? CompletedAt);

    public static VisitRow FromTicket(QueueTicket t) => new(t.ServiceDate, t.ClinicId, t.DoctorId, t.Status, t.IsPriority, t.Source,
        t.IssuedAt, t.FirstCalledAt, t.LastCalledAt, t.CompletedAt);

    public async Task<ReportSummary> LiveTodayAsync()
    {
        var today = clock.Today;
        await using var store = factory.Create();
        var tickets = await store.ToListAsync(store.Query<QueueTicket>().Where(t => t.ServiceDate == today));
        var archived = await store.ToListAsync(store.Query<VisitHistory>().Where(v => v.ServiceDate == today));
        var rows = tickets.Select(FromTicket).Concat(archived.Select(FromHistory)).ToList();
        return await BuildAsync(store, rows, today, today);
    }

    /// <summary>Historical report from the anonymous VisitHistory (plus today's live tickets if today is in range).</summary>
    public async Task<ReportSummary> RangeAsync(DateTime from, DateTime to, int? clinicId = null)
    {
        from = from.Date; to = to.Date;
        await using var store = factory.Create();
        var hq = store.Query<VisitHistory>().Where(v => v.ServiceDate >= from && v.ServiceDate <= to);
        if (clinicId is int c) hq = hq.Where(v => v.ClinicId == c);
        var rows = (await store.ToListAsync(hq)).Select(FromHistory).ToList();
        if (clock.Today >= from && clock.Today <= to)
        {
            var today = clock.Today;
            var tq = store.Query<QueueTicket>().Where(t => t.ServiceDate == today);
            if (clinicId is int c2) tq = tq.Where(t => t.ClinicId == c2);
            rows.AddRange((await store.ToListAsync(tq)).Select(FromTicket));
        }
        return await BuildAsync(store, rows, from, to);
    }

    public static VisitRow FromHistory(VisitHistory v) => new(v.ServiceDate, v.ClinicId, v.DoctorId, v.Outcome, v.IsPriority, v.Source,
        v.IssuedAt, v.FirstCalledAt, v.CompletedAt is null || v.ServiceMinutes is null ? null : v.CompletedAt.Value.AddMinutes(-v.ServiceMinutes.Value), v.CompletedAt);

    internal static async Task<ReportSummary> BuildAsync(IDataStore store, List<VisitRow> rows, DateTime from, DateTime to)
    {
        var clinics = await store.ToListAsync(store.Query<Clinic>().OrderBy(c => c.SortOrder));
        var doctors = (await store.ToListAsync(store.Query<Doctor>())).ToDictionary(d => d.Id);

        static double? Wait(VisitRow r) => r.FirstCalledAt is null ? null : Math.Max(0, (r.FirstCalledAt.Value - r.IssuedAt).TotalMinutes);
        static double? Service(VisitRow r) => r.Status == TicketStatus.Completed && r.CompletedAt != null && r.LastCalledAt != null
            ? Math.Max(0, (r.CompletedAt.Value - r.LastCalledAt.Value).TotalMinutes) : null;
        static double? Avg(IEnumerable<double?> xs) { var l = xs.Where(x => x != null).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : Math.Round(l.Average(), 1); }

        var hours = new int[24];
        foreach (var r in rows) hours[r.IssuedAt.Hour]++;

        return new ReportSummary
        {
            From = from, To = to,
            Total = rows.Count,
            Completed = rows.Count(r => r.Status == TicketStatus.Completed),
            NoShows = rows.Count(r => r.Status == TicketStatus.NoShow),
            NotSeen = rows.Count(r => r.Status == TicketStatus.NotSeen),
            Transferred = rows.Count(r => r.Status == TicketStatus.Transferred),
            Cancelled = rows.Count(r => r.Status == TicketStatus.Cancelled),
            Waiting = rows.Count(r => r.Status == TicketStatus.Waiting),
            InService = rows.Count(r => r.Status == TicketStatus.Called),
            Priority = rows.Count(r => r.Priority),
            SelfService = rows.Count(r => r.Source == TicketSource.SelfServiceQr),
            AvgWaitMinutes = Avg(rows.Select(Wait)),
            AvgServiceMinutes = Avg(rows.Select(Service)),
            IssuedByHour = hours,
            Clinics = clinics.Select(c =>
            {
                var cr = rows.Where(r => r.ClinicId == c.Id).ToList();
                var waits = cr.Select(Wait).Where(w => w != null).Select(w => w!.Value).ToList();
                return new ClinicStat(c.Id, c.Code, c.NameAr, c.NameEn, cr.Count,
                    cr.Count(r => r.Status == TicketStatus.Completed), cr.Count(r => r.Status == TicketStatus.NoShow),
                    cr.Count(r => r.Status == TicketStatus.NotSeen), cr.Count(r => r.Status == TicketStatus.Waiting),
                    Avg(cr.Select(Wait)), Avg(cr.Select(Service)), waits.Count == 0 ? null : Math.Round(waits.Max(), 1));
            }).Where(s => s.Total > 0 || clinics.Count <= 12).ToList(),
            Doctors = rows.Where(r => r.DoctorId != null).GroupBy(r => r.DoctorId!.Value).Select(g =>
            {
                doctors.TryGetValue(g.Key, out var d);
                return new DoctorStat(g.Key, d?.NameAr ?? "?", d?.NameEn ?? "?", g.Count(r => r.Status == TicketStatus.Completed),
                    Avg(g.Select(Service)), g.Count(r => r.Status == TicketStatus.NoShow));
            }).OrderByDescending(d => d.Completed).ToList(),
            Days = rows.GroupBy(r => r.Date).OrderBy(g => g.Key).Select(g => new DayStat(g.Key, g.Count(),
                g.Count(r => r.Status == TicketStatus.Completed), g.Count(r => r.Status == TicketStatus.NoShow), Avg(g.Select(Wait)))).ToList()
        };
    }

    public async Task<List<DailyReport>> GetDailyReportsAsync(int take = 60)
    {
        await using var store = factory.Create();
        return await store.ToListAsync(store.Query<DailyReport>().OrderByDescending(r => r.ServiceDate).ThenByDescending(r => r.Id).Take(take));
    }

    public async Task<DailyReport?> GetDailyReportAsync(int id)
    {
        await using var store = factory.Create();
        return await store.FirstOrDefaultAsync(store.Query<DailyReport>().Where(r => r.Id == id));
    }

    public static ReportSummary? ParseDetails(DailyReport r)
    {
        try { return JsonSerializer.Deserialize<ReportSummary>(r.DetailsJson); } catch { return null; }
    }

    /// <summary>CSV with UTF-8 BOM so Excel shows Arabic correctly.</summary>
    public static byte[] ToCsv(ReportSummary s, bool arabic)
    {
        var sb = new StringBuilder();
        static string Q(object? v) => "\"" + Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace("\"", "\"\"") + "\"";
        string L(string ar, string en) => arabic ? ar : en;

        sb.AppendLine(string.Join(",", Q(L("من", "From")), Q(s.From.ToString("yyyy-MM-dd")), Q(L("إلى", "To")), Q(s.To.ToString("yyyy-MM-dd"))));
        sb.AppendLine();
        sb.AppendLine(string.Join(",", Q(L("المؤشر", "Metric")), Q(L("القيمة", "Value"))));
        foreach (var (k, v) in new (string, object?)[]
        {
            (L("إجمالي التذاكر", "Total tickets"), s.Total), (L("مكتملة", "Completed"), s.Completed), (L("لم يحضر", "No-shows"), s.NoShows),
            (L("لم تتم معاينتهم", "Not seen"), s.NotSeen), (L("محوّلون", "Transferred"), s.Transferred), (L("ملغاة", "Cancelled"), s.Cancelled),
            (L("متوسط الانتظار (دقيقة)", "Avg wait (min)"), s.AvgWaitMinutes), (L("متوسط مدة المعاينة (دقيقة)", "Avg consultation (min)"), s.AvgServiceMinutes),
            (L("نسبة عدم الحضور %", "No-show rate %"), s.NoShowRate)
        }) sb.AppendLine(Q(k) + "," + Q(v));

        sb.AppendLine();
        sb.AppendLine(string.Join(",", new[] { L("العيادة", "Clinic"), L("الإجمالي", "Total"), L("مكتملة", "Completed"), L("لم يحضر", "No-shows"),
            L("لم تتم معاينتهم", "Not seen"), L("متوسط الانتظار", "Avg wait"), L("أقصى انتظار", "Max wait"), L("متوسط المعاينة", "Avg consultation") }.Select(Q)));
        foreach (var c in s.Clinics)
            sb.AppendLine(string.Join(",", new object?[] { arabic ? c.NameAr : c.NameEn, c.Total, c.Completed, c.NoShows, c.NotSeen,
                c.AvgWaitMinutes, c.MaxWaitMinutes, c.AvgServiceMinutes }.Select(Q)));

        sb.AppendLine();
        sb.AppendLine(string.Join(",", new[] { L("الطبيب", "Doctor"), L("مكتملة", "Completed"), L("متوسط المعاينة", "Avg consultation"), L("لم يحضر", "No-shows") }.Select(Q)));
        foreach (var d in s.Doctors)
            sb.AppendLine(string.Join(",", new object?[] { arabic ? d.NameAr : d.NameEn, d.Completed, d.AvgServiceMinutes, d.NoShows }.Select(Q)));

        sb.AppendLine();
        sb.AppendLine(Q(L("الساعة", "Hour")) + "," + Q(L("تذاكر صادرة", "Tickets issued")));
        for (var h = 0; h < 24; h++) if (s.IssuedByHour[h] > 0) sb.AppendLine(Q($"{h:00}:00") + "," + Q(s.IssuedByHour[h]));

        if (s.Days.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine(string.Join(",", new[] { L("التاريخ", "Date"), L("الإجمالي", "Total"), L("مكتملة", "Completed"), L("لم يحضر", "No-shows"), L("متوسط الانتظار", "Avg wait") }.Select(Q)));
            foreach (var d in s.Days) sb.AppendLine(string.Join(",", new object?[] { d.Date.ToString("yyyy-MM-dd"), d.Total, d.Completed, d.NoShows, d.AvgWaitMinutes }.Select(Q)));
        }

        var bom = Encoding.UTF8.GetPreamble();
        return bom.Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }
}
