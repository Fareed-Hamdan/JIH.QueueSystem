// Business-rule tests for the queue system. No test framework needed:
//   dotnet run --project tests/JIH.Queue.RuleTests
// Each test builds a fresh in-memory database with the dummy seed data.

using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;
using JIH.Queue.Core.Notifications;
using JIH.Queue.Core.Rules;
using JIH.Queue.Core.Services;

var tests = new List<(string Name, Func<Task> Body)>
{
    ("Ticket numbers run per clinic (A-001, A-002, B-001)", Tests.NumbersPerClinic),
    ("Validation: phone, name, complaint, consent", Tests.Validation),
    ("Self-service cannot set priority", Tests.SelfServiceNoPriority),
    ("One active ticket per phone per clinic", Tests.DuplicatePhone),
    ("Closed / Full clinics reject tickets; daily limit makes clinic Full", Tests.ClinicStatusRules),
    ("Priority ticket goes after the last priority ticket", Tests.PriorityOrdering),
    ("Call next needs an active assignment and no current patient", Tests.CallNextRules),
    ("Two doctors in one clinic share the queue", Tests.SharedClinicQueue),
    ("Skip once → moved back 3 positions", Tests.SkipMovesBackThree),
    ("Skip when fewer than 3 waiting → end of queue", Tests.SkipShortQueue),
    ("Second skip → No-show", Tests.SecondSkipNoShow),
    ("Reinstated no-show → end of queue, next skip = no-show", Tests.Reinstate),
    ("Transfer creates a ticket in the target clinic", Tests.Transfer),
    ("'Turn approaching' sent once when 3 or fewer ahead", Tests.ApproachingNotification),
    ("Assignment rules: no double booking, room must belong to clinic", Tests.AssignmentRules),
    ("Doctor cannot be active in two assignments", Tests.DoctorActiveOnce),
    ("End of day: report + anonymous history, patients deleted", Tests.EndOfDay),
    ("Numbers continue after an early manual reset", Tests.NumbersAfterEarlyReset),
    ("Phone normalisation (Jordan)", Tests.Phones),
    ("Rotating QR token validity", Tests.QrTokens),
    ("Audit log never contains patient names or phones", Tests.AuditHasNoPii),
};

var failed = 0;
foreach (var (name, body) in tests)
{
    try { await body(); Console.WriteLine($"  PASS  {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"  FAIL  {name}\n        {ex.Message}"); }
}
Console.WriteLine(failed == 0 ? $"\nAll {tests.Count} tests passed." : $"\n{failed} of {tests.Count} tests FAILED.");
return failed == 0 ? 0 : 1;

// ====================================================================================

sealed class FakeClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 11, 23, 9, 0, 0);
    public DateTime Today => Now.Date;
    public void Advance(int minutes) => Now = Now.AddMinutes(minutes);
}

sealed class Env
{
    public FakeClock Clock = new();
    public InMemoryDataStoreFactory Factory;
    public QueueEvents Events = new();
    public NotificationQueue Notifications = new();
    public SettingsService Settings;
    public QueueService Queue;
    public AdminService Admin;
    public QueueQueryService Query;
    public EndOfDayService EndOfDay;
    public List<Clinic> Clinics = [];

    private Env()
    {
        Factory = new InMemoryDataStoreFactory(new InMemoryDatabase());
        Settings = new SettingsService(Factory, Clock, Events);
        Queue = new QueueService(Factory, Clock, Settings, Events, Notifications);
        Admin = new AdminService(Factory, Clock, Events);
        Query = new QueueQueryService(Factory, Clock);
        EndOfDay = new EndOfDayService(Factory, Clock, Events);
    }

    public static async Task<Env> CreateAsync()
    {
        var e = new Env();
        await new DemoDataSeeder(e.Factory, e.Clock, e.Settings, e.Queue).SeedAsync(false, false);
        e.Clinics = await e.Query.GetClinicsAsync();
        return e;
    }

    public Clinic C(string code) => Clinics.First(c => c.Code == code);

    private int _phone;
    public async Task<IssuedTicket> Issue(string clinic, bool priority = false, string? phone = null)
    {
        var r = await Queue.IssueAsync(new IssueTicketRequest
        {
            FullName = "مريض تجريبي", Phone = phone ?? $"0791{++_phone:000000}", ClinicId = C(clinic).Id,
            Complaint = "ألم تجريبي", ConsentGiven = true, IsPriority = priority, Source = TicketSource.Reception, IssuedBy = "test"
        });
        Assert.True(r.Ok, $"issue failed: {r.Error}");
        return r.Value!;
    }

    public async Task<int> ActiveAssignment(string clinic, int index = 0)
    {
        var a = (await Query.GetAssignmentsAsync(Clock.Today)).Where(x => x.ClinicId == C(clinic).Id && x.Shift == Shift.Morning).ElementAt(index);
        var r = await Queue.SetAssignmentStatusAsync(a.Id, AssignmentStatus.Active, "test");
        Assert.True(r.Ok, $"activate failed: {r.Error}");
        return a.Id;
    }

    public async Task<List<string>> WaitingNumbers(string clinic) =>
        (await Query.GetDoctorPanelAsync((await Query.GetAssignmentsAsync(Clock.Today)).First(x => x.ClinicId == C(clinic).Id).Id))!
            .Waiting.Select(w => w.Number).ToList();

    public List<NotificationJob> DrainNotifications()
    {
        var list = new List<NotificationJob>();
        while (Notifications.Reader.TryRead(out var j)) list.Add(j);
        return list;
    }
}

static class Assert
{
    public static void True(bool c, string msg) { if (!c) throw new Exception(msg); }
    public static void Equal<T>(T expected, T actual, string what) { if (!Equals(expected, actual)) throw new Exception($"{what}: expected '{expected}', got '{actual}'"); }
    public static void Seq(IEnumerable<string> expected, IEnumerable<string> actual, string what)
    { if (!expected.SequenceEqual(actual)) throw new Exception($"{what}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]"); }
}

static class Tests
{
    public static async Task NumbersPerClinic()
    {
        var e = await Env.CreateAsync();
        Assert.Equal("A-001", (await e.Issue("A")).Number, "first A");
        Assert.Equal("A-002", (await e.Issue("A")).Number, "second A");
        Assert.Equal("B-001", (await e.Issue("B")).Number, "first B");
    }

    public static async Task Validation()
    {
        var e = await Env.CreateAsync();
        var ok = new IssueTicketRequest { FullName = "اسم", Phone = "0791111111", ClinicId = e.C("A").Id, Complaint = "ألم", ConsentGiven = true, Source = TicketSource.Reception };
        Assert.Equal("err.phone", (await e.Queue.IssueAsync(ok with { Phone = "0612345678" })).Error, "landline");
        Assert.Equal("err.name", (await e.Queue.IssueAsync(ok with { FullName = "x" })).Error, "short name");
        Assert.Equal("err.complaint", (await e.Queue.IssueAsync(ok with { Complaint = "" })).Error, "empty complaint");
        Assert.Equal("err.consent", (await e.Queue.IssueAsync(ok with { ConsentGiven = false })).Error, "consent");
        Assert.True((await e.Queue.IssueAsync(ok)).Ok, "valid request");
    }

    public static async Task SelfServiceNoPriority()
    {
        var e = await Env.CreateAsync();
        var r = await e.Queue.IssueAsync(new IssueTicketRequest { FullName = "اسم", Phone = "0791111111", ClinicId = e.C("A").Id, Complaint = "ألم", ConsentGiven = true, IsPriority = true, Source = TicketSource.SelfServiceQr });
        Assert.Equal("err.priorityReceptionOnly", r.Error, "error");
    }

    public static async Task DuplicatePhone()
    {
        var e = await Env.CreateAsync();
        await e.Issue("A", phone: "0795555555");
        var dup = await e.Queue.IssueAsync(new IssueTicketRequest { FullName = "اسم", Phone = "+962795555555", ClinicId = e.C("A").Id, Complaint = "ألم", ConsentGiven = true, Source = TicketSource.Reception });
        Assert.Equal("err.duplicateTicket", dup.Error, "same clinic");
        Assert.Equal("A-001", dup.Args![0].ToString(), "returns existing number");
        await e.Issue("B", phone: "0795555555"); // other clinic is allowed
    }

    public static async Task ClinicStatusRules()
    {
        var e = await Env.CreateAsync();
        await e.Admin.SetClinicStatusAsync(e.C("A").Id, ClinicStatus.Closed, "test");
        var r = await e.Queue.IssueAsync(new IssueTicketRequest { FullName = "اسم", Phone = "0791111111", ClinicId = e.C("A").Id, Complaint = "ألم", ConsentGiven = true, Source = TicketSource.Reception });
        Assert.Equal("err.clinicClosed", r.Error, "closed");

        var b = e.C("B"); b.DailyTicketLimit = 2;
        await e.Admin.SaveClinicAsync(b, "test");
        await e.Issue("B"); await e.Issue("B");
        var clinicB = (await e.Query.GetClinicsAsync()).First(c => c.Code == "B");
        Assert.Equal(ClinicStatus.Full, clinicB.Status, "becomes full at limit");
        var r2 = await e.Queue.IssueAsync(new IssueTicketRequest { FullName = "اسم", Phone = "0791111199", ClinicId = b.Id, Complaint = "ألم", ConsentGiven = true, Source = TicketSource.Reception });
        Assert.Equal("err.clinicFull", r2.Error, "full");
    }

    public static async Task PriorityOrdering()
    {
        var e = await Env.CreateAsync();
        await e.Issue("A"); await e.Issue("A");              // A-001, A-002
        await e.Issue("A", priority: true);                  // A-003 → front
        await e.Issue("A");                                  // A-004 → end
        await e.Issue("A", priority: true);                  // A-005 → after A-003
        Assert.Seq(["A-003", "A-005", "A-001", "A-002", "A-004"], await e.WaitingNumbers("A"), "order");
    }

    public static async Task CallNextRules()
    {
        var e = await Env.CreateAsync();
        var a = (await e.Query.GetAssignmentsAsync(e.Clock.Today)).First(x => x.ClinicId == e.C("A").Id);
        await e.Issue("A"); await e.Issue("A");
        Assert.Equal("err.assignmentNotActive", (await e.Queue.CallNextAsync(a.Id, "t")).Error, "scheduled assignment");
        await e.Queue.SetAssignmentStatusAsync(a.Id, AssignmentStatus.Active, "t");
        var c1 = await e.Queue.CallNextAsync(a.Id, "t");
        Assert.Equal("A-001", c1.Value!.Number, "first call");
        Assert.Equal(101, c1.Value.RoomNumber, "room");
        Assert.Equal("err.finishCurrentFirst", (await e.Queue.CallNextAsync(a.Id, "t")).Error, "must end first");
        Assert.Equal("err.finishCurrentFirst", (await e.Queue.SetAssignmentStatusAsync(a.Id, AssignmentStatus.Paused, "t")).Error, "cannot pause with patient");
        await e.Queue.EndVisitAsync(a.Id, "t");
        Assert.Equal("A-002", (await e.Queue.CallNextAsync(a.Id, "t")).Value!.Number, "second call");
        await e.Queue.EndVisitAsync(a.Id, "t");
        Assert.Equal("err.queueEmpty", (await e.Queue.CallNextAsync(a.Id, "t")).Error, "empty");
        await e.Issue("A");
        await e.Admin.SetClinicStatusAsync(e.C("A").Id, ClinicStatus.Paused, "t");
        Assert.Equal("err.clinicPaused", (await e.Queue.CallNextAsync(a.Id, "t")).Error, "paused clinic");
    }

    public static async Task SharedClinicQueue()
    {
        var e = await Env.CreateAsync();
        var a1 = await e.ActiveAssignment("A", 0);
        var a2 = await e.ActiveAssignment("A", 1);
        for (var i = 0; i < 3; i++) await e.Issue("A");
        var r = await Task.WhenAll(e.Queue.CallNextAsync(a1, "d1"), e.Queue.CallNextAsync(a2, "d2"));
        Assert.True(r.All(x => x.Ok), "both calls succeed");
        Assert.True(r[0].Value!.Number != r[1].Value!.Number, "different patients");
        Assert.True(r[0].Value!.RoomNumber != r[1].Value!.RoomNumber, "different rooms");
        Assert.Seq(["A-003"], await e.WaitingNumbers("A"), "remaining");
    }

    public static async Task SkipMovesBackThree()
    {
        var e = await Env.CreateAsync();
        var a = await e.ActiveAssignment("A");
        for (var i = 0; i < 6; i++) await e.Issue("A");     // A-001..A-006
        await e.Queue.CallNextAsync(a, "t");                 // A-001 called
        var s = await e.Queue.SkipAsync(a, "t");
        Assert.True(s.Ok && s.Value == false, "skipped, not no-show");
        Assert.Seq(["A-002", "A-003", "A-004", "A-001", "A-005", "A-006"], await e.WaitingNumbers("A"), "A-001 after 3 patients");
    }

    public static async Task SkipShortQueue()
    {
        var e = await Env.CreateAsync();
        var a = await e.ActiveAssignment("A");
        for (var i = 0; i < 3; i++) await e.Issue("A");
        await e.Queue.CallNextAsync(a, "t");
        await e.Queue.SkipAsync(a, "t");
        Assert.Seq(["A-002", "A-003", "A-001"], await e.WaitingNumbers("A"), "to end");
    }

    public static async Task SecondSkipNoShow()
    {
        var e = await Env.CreateAsync();
        var a = await e.ActiveAssignment("A");
        await e.Issue("A");
        await e.Queue.CallNextAsync(a, "t");
        await e.Queue.SkipAsync(a, "t");                     // back to queue (alone)
        var again = await e.Queue.CallNextAsync(a, "t");
        Assert.Equal("A-001", again.Value!.Number, "called again");
        var s = await e.Queue.SkipAsync(a, "t");
        Assert.True(s.Value, "no-show");
        var t = (await e.Query.GetTodayTicketsAsync()).Single();
        Assert.Equal(TicketStatus.NoShow, t.Status, "status");
        Assert.Seq([], await e.WaitingNumbers("A"), "queue empty");
    }

    public static async Task Reinstate()
    {
        var e = await Env.CreateAsync();
        var a = await e.ActiveAssignment("A");
        var first = await e.Issue("A");
        await e.Queue.CallNextAsync(a, "t"); await e.Queue.SkipAsync(a, "t");
        await e.Queue.CallNextAsync(a, "t"); await e.Queue.SkipAsync(a, "t"); // no-show
        await e.Issue("A"); await e.Issue("A");
        Assert.True((await e.Queue.ReinstateAsync(first.TicketId, "reception")).Ok, "reinstate ok");
        Assert.Seq(["A-002", "A-003", "A-001"], await e.WaitingNumbers("A"), "end of queue");
        await e.Queue.CallNextAsync(a, "t"); await e.Queue.EndVisitAsync(a, "t");
        await e.Queue.CallNextAsync(a, "t"); await e.Queue.EndVisitAsync(a, "t");
        await e.Queue.CallNextAsync(a, "t");
        Assert.True((await e.Queue.SkipAsync(a, "t")).Value, "skip after reinstatement = no-show");
        Assert.Equal("err.onlyNoShowReinstate", (await e.Queue.ReinstateAsync((await e.Query.GetTodayTicketsAsync()).First(x => x.Number == "A-002").TicketId, "r")).Error, "only no-shows");
    }

    public static async Task Transfer()
    {
        var e = await Env.CreateAsync();
        var a = await e.ActiveAssignment("A");
        await e.Issue("B");                                   // B-001 already waiting
        var orig = await e.Issue("A");
        e.Clock.Advance(20);
        await e.Queue.CallNextAsync(a, "t");
        var tr = await e.Queue.TransferAsync(a, e.C("B").Id, false, "doctor");
        Assert.True(tr.Ok, tr.Error ?? "");
        Assert.Equal("B-002", tr.Value!.Number, "new number");
        Assert.Equal(1, tr.Value.PeopleAhead, "end of B queue");
        var rows = await e.Query.GetTodayTicketsAsync();
        Assert.Equal(TicketStatus.Transferred, rows.First(r => r.TicketId == orig.TicketId).Status, "old ticket");
        var panel = await e.Query.GetDoctorPanelAsync(a);
        Assert.True(panel!.Current is null, "doctor free again");
        var urgent = await e.Issue("A");
        await e.Queue.CallNextAsync(a, "t");
        var tr2 = await e.Queue.TransferAsync(a, e.C("B").Id, true, "doctor");
        Assert.Equal(0, tr2.Value!.PeopleAhead, "urgent → front");
    }

    public static async Task ApproachingNotification()
    {
        var e = await Env.CreateAsync();
        var a = await e.ActiveAssignment("A");
        for (var i = 0; i < 6; i++) await e.Issue("A");      // A-001..A-006; A-001..A-004 already "close" at issue time
        e.DrainNotifications();
        await e.Queue.CallNextAsync(a, "t");                  // A-005 now has 3 ahead
        var jobs = e.DrainNotifications();
        var rows = await e.Query.GetTodayTicketsAsync();
        int Id(string n) => rows.First(r => r.Number == n).TicketId;
        Assert.True(jobs.Any(j => j.Kind == NotificationKind.Called && j.TicketId == Id("A-001")), "called message");
        Assert.True(jobs.Any(j => j.Kind == NotificationKind.TurnApproaching && j.TicketId == Id("A-005")), "approaching for A-005");
        Assert.True(!jobs.Any(j => j.Kind == NotificationKind.TurnApproaching && j.TicketId != Id("A-005")), "only A-005");
        await e.Queue.EndVisitAsync(a, "t");
        await e.Queue.CallNextAsync(a, "t");                  // A-006 now 3 ahead; A-005 must not get a second message
        jobs = e.DrainNotifications();
        Assert.Equal(1, jobs.Count(j => j.Kind == NotificationKind.TurnApproaching), "one approaching message");
        Assert.Equal(Id("A-006"), jobs.First(j => j.Kind == NotificationKind.TurnApproaching).TicketId, "A-006");
    }

    public static async Task AssignmentRules()
    {
        var e = await Env.CreateAsync();
        var existing = (await e.Query.GetAssignmentsAsync(e.Clock.Today)).First(x => x.Shift == Shift.Morning);
        var roomsB = await e.Admin.GetRoomsAsync(e.C("B").Id);
        var r1 = await e.Admin.SaveAssignmentAsync(new DoctorAssignment { DoctorId = existing.DoctorId, ClinicId = e.C("B").Id, RoomId = roomsB[0].Id, ServiceDate = e.Clock.Today, Shift = Shift.Morning }, "admin");
        Assert.Equal("err.doctorDoubleBooked", r1.Error, "doctor double booked");
        var roomsA = await e.Admin.GetRoomsAsync(e.C("A").Id);
        var r2 = await e.Admin.SaveAssignmentAsync(new DoctorAssignment { DoctorId = existing.DoctorId, ClinicId = e.C("B").Id, RoomId = roomsA[0].Id, ServiceDate = e.Clock.Today.AddDays(1), Shift = Shift.Morning }, "admin");
        Assert.Equal("err.roomNotInClinic", r2.Error, "room of another clinic");
        var r3 = await e.Admin.SaveAssignmentAsync(new DoctorAssignment { DoctorId = existing.DoctorId, ClinicId = e.C("B").Id, RoomId = roomsB[0].Id, ServiceDate = e.Clock.Today.AddDays(1), Shift = Shift.Morning }, "admin");
        Assert.True(r3.Ok, "valid tomorrow");
        var copied = await e.Admin.CopyAssignmentsAsync(e.Clock.Today, e.Clock.Today.AddDays(1), "admin");
        Assert.True(copied.Ok && copied.Value > 0, "copy day");
    }

    public static async Task DoctorActiveOnce()
    {
        var e = await Env.CreateAsync();
        var list = await e.Query.GetAssignmentsAsync(e.Clock.Today);
        var morning = list.First(x => x.Shift == Shift.Morning && list.Any(y => y.DoctorId == x.DoctorId && y.Shift == Shift.Evening));
        var evening = list.First(x => x.DoctorId == morning.DoctorId && x.Shift == Shift.Evening);
        Assert.True((await e.Queue.SetAssignmentStatusAsync(morning.Id, AssignmentStatus.Active, "d")).Ok, "morning active");
        Assert.Equal("err.doctorActiveElsewhere", (await e.Queue.SetAssignmentStatusAsync(evening.Id, AssignmentStatus.Active, "d")).Error, "evening blocked");
        await e.Queue.SetAssignmentStatusAsync(morning.Id, AssignmentStatus.Finished, "d");
        Assert.True((await e.Queue.SetAssignmentStatusAsync(evening.Id, AssignmentStatus.Active, "d")).Ok, "evening after morning finished");
    }

    public static async Task EndOfDay()
    {
        var e = await Env.CreateAsync();
        var a = await e.ActiveAssignment("A");
        for (var i = 0; i < 4; i++) await e.Issue("A");
        e.Clock.Advance(15);
        await e.Queue.CallNextAsync(a, "t"); e.Clock.Advance(10); await e.Queue.EndVisitAsync(a, "t");   // completed, wait 15, service 10
        await e.Queue.CallNextAsync(a, "t"); await e.Queue.SkipAsync(a, "t");
        await e.Queue.CallNextAsync(a, "t");                                                               // left "in service"
        var ids = await e.EndOfDay.RunAsync(ResetTrigger.Manual, "admin");
        Assert.Equal(1, ids.Count, "one report");
        await using var store = e.Factory.Create();
        Assert.Equal(0, store.Query<Patient>().Count(), "patients deleted");
        Assert.Equal(0, store.Query<QueueTicket>().Count(), "tickets deleted");
        var hist = store.Query<VisitHistory>().ToList();
        Assert.Equal(4, hist.Count, "history rows");
        Assert.Equal(2, hist.Count(h => h.Outcome == TicketStatus.NotSeen), "waiting → not seen");
        Assert.Equal(15.0, hist.First(h => h.TicketNumber == "A-001").WaitMinutes, "wait minutes");
        Assert.Equal(10.0, hist.First(h => h.TicketNumber == "A-001").ServiceMinutes, "service minutes");
        var report = store.Query<DailyReport>().Single();
        Assert.Equal(4, report.TotalTickets, "report total");
        Assert.True(report.AuditEventCount > 0, "audit counted");
        Assert.True(store.Query<DoctorAssignment>().Where(x => x.ServiceDate == e.Clock.Today).All(x => x.CurrentTicketId == null), "assignments cleared");
    }

    public static async Task NumbersAfterEarlyReset()
    {
        var e = await Env.CreateAsync();
        await e.Issue("A"); await e.Issue("A");
        await e.EndOfDay.RunAsync(ResetTrigger.Manual, "admin");
        Assert.Equal("A-003", (await e.Issue("A")).Number, "continues numbering");
    }

    public static Task Phones()
    {
        foreach (var (input, expected) in new[] {
            ("0791234567", "+962791234567"), ("791234567", "+962791234567"), ("+962 79 123 4567", "+962791234567"),
            ("00962781234567", "+962781234567"), ("٠٧٧١٢٣٤٥٦٧", "+962771234567") })
        {
            Assert.True(PhoneNumbers.TryNormalize(input, out var e164), $"valid {input}");
            Assert.Equal(expected, e164, input);
        }
        foreach (var bad in new[] { "0761234567", "065000000", "12345", "" })
            Assert.True(!PhoneNumbers.TryNormalize(bad, out _), $"invalid {bad}");
        Assert.Equal("+96279****567", PhoneNumbers.Mask("+962791234567"), "mask");
        return Task.CompletedTask;
    }

    public static Task QrTokens()
    {
        var clock = new FakeClock();
        var svc = new QrTokenService(clock, new QrTokenOptions { Secret = "test-secret" });
        var token = svc.CurrentToken(90);
        Assert.True(svc.Validate(token, 90), "valid now");
        clock.Advance(1);
        Assert.True(svc.Validate(token, 90), "valid in next window (grace)");
        clock.Advance(5);
        Assert.True(!svc.Validate(token, 90), "expired after two windows");
        Assert.True(!svc.Validate(token.Replace('.', '9'), 90), "tampered");
        Assert.True(!new QrTokenService(clock, new QrTokenOptions { Secret = "other" }).Validate(svc.CurrentToken(90), 90), "other secret");
        return Task.CompletedTask;
    }

    public static async Task AuditHasNoPii()
    {
        var e = await Env.CreateAsync();
        var a = await e.ActiveAssignment("A");
        await e.Queue.IssueAsync(new IssueTicketRequest { FullName = "زيد سرّي", Phone = "0798887766", ClinicId = e.C("A").Id, Complaint = "شكوى خاصة", ConsentGiven = true, Source = TicketSource.Reception, IssuedBy = "reception" });
        await e.Queue.CallNextAsync(a, "t"); await e.Queue.EndVisitAsync(a, "t");
        await e.EndOfDay.RunAsync(ResetTrigger.Manual, "admin");
        await using var store = e.Factory.Create();
        var text = string.Join("\n", store.Query<AuditLog>().ToList().Select(x => $"{x.Action} {x.EntityId} {x.Details}"))
                 + string.Join("\n", store.Query<DailyReport>().ToList().Select(r => r.DetailsJson));
        Assert.True(!text.Contains("زيد") && !text.Contains("798887766") && !text.Contains("شكوى"), "no PII in audit/report");
    }
}
