using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;

namespace JIH.Queue.Core.Services;

/// <summary>
/// Seeds DUMMY data only (no real patients): clinics, rooms, doctors, staff accounts, today's assignments,
/// display screens and – optionally – sample tickets and 14 days of anonymous history for the reports.
/// Runs only when the database has no clinics.
/// </summary>
public sealed class DemoDataSeeder(IDataStoreFactory factory, IClock clock, SettingsService settings, QueueService queue)
{
    public async Task<bool> SeedAsync(bool demoTickets, bool demoHistory)
    {
        await settings.EnsureDefaultsAsync();
        await using (var check = factory.Create())
            if (await check.AnyAsync(check.Query<Clinic>())) return false;

        var now = clock.Now;
        var today = clock.Today;

        var clinicDefs = new (string Code, string Ar, string En, int[] Rooms)[]
        {
            ("A", "الباطنية", "Internal Medicine", [101, 102, 103]),
            ("B", "الأطفال", "Pediatrics", [201, 202]),
            ("C", "العظام", "Orthopedics", [301, 302]),
            ("D", "الأنف والأذن والحنجرة", "ENT", [401]),
            ("E", "الجلدية", "Dermatology", [501]),
            ("F", "العيون", "Ophthalmology", [601, 602]),
            ("G", "النسائية والتوليد", "Obstetrics & Gynecology", [701, 702]),
        };
        var doctorDefs = new (string Ar, string En, string SpAr, string SpEn)[]
        {
            ("د. سامر الحداد", "Dr. Samer Haddad", "باطنية", "Internal Medicine"),
            ("د. رنا العلي", "Dr. Rana Ali", "باطنية", "Internal Medicine"),
            ("د. عمر الخطيب", "Dr. Omar Khatib", "باطنية", "Internal Medicine"),
            ("د. ليلى حسن", "Dr. Layla Hasan", "أطفال", "Pediatrics"),
            ("د. يوسف النجار", "Dr. Yousef Najjar", "أطفال", "Pediatrics"),
            ("د. خالد منصور", "Dr. Khaled Mansour", "عظام", "Orthopedics"),
            ("د. هبة سليمان", "Dr. Hiba Suleiman", "عظام", "Orthopedics"),
            ("د. طارق عيسى", "Dr. Tareq Issa", "أنف وأذن وحنجرة", "ENT"),
            ("د. نور الدين صالح", "Dr. Nour Saleh", "جلدية", "Dermatology"),
            ("د. مريم يوسف", "Dr. Mariam Yousef", "عيون", "Ophthalmology"),
            ("د. فادي جابر", "Dr. Fadi Jaber", "عيون", "Ophthalmology"),
            ("د. سلمى قاسم", "Dr. Salma Qasem", "نسائية وتوليد", "Obstetrics & Gynecology"),
            ("د. دانا عوض", "Dr. Dana Awad", "نسائية وتوليد", "Obstetrics & Gynecology"),
        };

        var clinics = new List<Clinic>();
        var rooms = new List<Room>();
        var doctors = new List<Doctor>();
        await using (var store = factory.Create())
        {
            var order = 0;
            foreach (var c in clinicDefs)
            {
                var clinic = new Clinic { Code = c.Code, NameAr = c.Ar, NameEn = c.En, Status = ClinicStatus.Open, SortOrder = ++order, CreatedAt = now };
                store.Add(clinic);
                clinics.Add(clinic);
            }
            await store.SaveChangesAsync();
            for (var i = 0; i < clinicDefs.Length; i++)
                foreach (var no in clinicDefs[i].Rooms)
                {
                    var r = new Room { ClinicId = clinics[i].Id, Number = no };
                    store.Add(r); rooms.Add(r);
                }
            foreach (var d in doctorDefs)
            {
                var doc = new Doctor { NameAr = d.Ar, NameEn = d.En, SpecialtyAr = d.SpAr, SpecialtyEn = d.SpEn, CreatedAt = now };
                store.Add(doc); doctors.Add(doc);
            }
            await store.SaveChangesAsync();

            // Staff accounts (change these passwords after installation!)
            void User(string u, string name, StaffRole role, string pw, int? doctorId = null)
            {
                var su = new StaffUser { Username = u, DisplayName = name, Role = role, DoctorId = doctorId, CreatedAt = now };
                su.PasswordHash = AdminService.HashPassword(su, pw);
                store.Add(su);
            }
            User("admin", "مدير النظام", StaffRole.Admin, "Admin@12345");
            User("reception", "الاستقبال", StaffRole.Receptionist, "Reception@123");
            User("nurse", "التمريض", StaffRole.Nurse, "Nurse@12345");
            string[] logins = ["dr.samer", "dr.rana", "dr.omar", "dr.layla", "dr.yousef", "dr.khaled", "dr.hiba", "dr.tareq", "dr.nour", "dr.mariam", "dr.fadi", "dr.salma", "dr.dana"];
            for (var i = 0; i < doctors.Count; i++) User(logins[i], doctors[i].NameAr, StaffRole.Doctor, "Doctor@123", doctors[i].Id);

            // Today's distribution: one doctor per room (morning); a few evening shifts.
            var docIndex = 0;
            foreach (var room in rooms)
            {
                if (docIndex >= doctors.Count) break;
                store.Add(new DoctorAssignment
                {
                    DoctorId = doctors[docIndex++].Id, ClinicId = room.ClinicId, RoomId = room.Id, ServiceDate = today,
                    Shift = Shift.Morning, Status = AssignmentStatus.Scheduled, CreatedAt = now, CreatedBy = "seed"
                });
            }
            store.Add(new DoctorAssignment { DoctorId = doctors[0].Id, ClinicId = clinics[0].Id, RoomId = rooms[0].Id, ServiceDate = today, Shift = Shift.Evening, CreatedAt = now, CreatedBy = "seed" });
            store.Add(new DoctorAssignment { DoctorId = doctors[3].Id, ClinicId = clinics[1].Id, RoomId = rooms[3].Id, ServiceDate = today, Shift = Shift.Evening, CreatedAt = now, CreatedBy = "seed" });

            store.Add(new DisplayDevice { Code = "HALL-1", Name = "قاعة الانتظار الرئيسية – Main waiting hall", ClinicIds = "" });
            store.Add(new DisplayDevice { Code = "HALL-2", Name = "قاعة الطابق الثاني – 2nd floor hall", ClinicIds = $"{clinics[0].Id},{clinics[1].Id},{clinics[2].Id}" });
            Audit.Write(store, clock, "system", AuditActions.Created, "Seed", null, null, "Dummy reference data created");
            await store.SaveChangesAsync();
        }

        if (demoHistory) await SeedHistoryAsync(clinics, doctors, rooms);
        if (demoTickets) await SeedTicketsAsync(clinics);
        return true;
    }

    private static readonly string[] FirstNames = ["محمد", "أحمد", "خالد", "سارة", "ليلى", "يوسف", "عمر", "رنا", "هبة", "زيد", "نور", "مريم", "طارق", "سلمى", "فادي", "دانا", "علي", "جنى", "حمزة", "تالا"];
    private static readonly string[] LastNames = ["تجريبي", "اختبار", "نموذج", "وهمي"]; // obviously dummy surnames
    private static readonly string[] Complaints =
        ["ألم في المعدة منذ يومين", "حرارة وسعال", "مراجعة نتائج تحاليل", "ألم في الركبة", "التهاب في الأذن", "طفح جلدي", "ضعف في النظر", "صداع متكرر", "مراجعة دورية", "ألم في الظهر"];

    private async Task SeedTicketsAsync(List<Clinic> clinics)
    {
        var rnd = new Random(42);
        var n = 0;
        foreach (var clinic in clinics.Take(5))
        {
            var count = clinic.Code == "A" ? 9 : rnd.Next(3, 7);
            for (var i = 0; i < count; i++)
            {
                n++;
                await queue.IssueAsync(new IssueTicketRequest
                {
                    FullName = $"{FirstNames[rnd.Next(FirstNames.Length)]} {LastNames[rnd.Next(LastNames.Length)]}",
                    Phone = $"0790000{n:000}",
                    ClinicId = clinic.Id,
                    Complaint = Complaints[rnd.Next(Complaints.Length)],
                    Language = rnd.Next(5) == 0 ? "en" : "ar",
                    ConsentGiven = true,
                    IsPriority = i == 4,
                    Source = i == 4 || rnd.Next(3) == 0 ? TicketSource.Reception : TicketSource.SelfServiceQr, // priority is reception-only
                    IssuedBy = "reception"
                });
            }
        }

        // Make the demo look like a running morning: some doctors started and called patients.
        List<DoctorAssignment> morning;
        await using (var store = factory.Create())
            morning = await store.ToListAsync(store.Query<DoctorAssignment>().Where(a => a.ServiceDate == clock.Today && a.Shift == Shift.Morning));
        foreach (var a in morning.Where(a => clinics.Take(4).Any(c => c.Id == a.ClinicId)))
        {
            await queue.SetAssignmentStatusAsync(a.Id, AssignmentStatus.Active, "seed");
            await queue.CallNextAsync(a.Id, "seed");
            if (a.ClinicId == clinics[0].Id) { await queue.EndVisitAsync(a.Id, "seed"); await queue.CallNextAsync(a.Id, "seed"); }
        }
    }

    private async Task SeedHistoryAsync(List<Clinic> clinics, List<Doctor> doctors, List<Room> rooms)
    {
        var rnd = new Random(7);
        await using var store = factory.Create();
        for (var day = 14; day >= 1; day--)
        {
            var date = clock.Today.AddDays(-day);
            if (date.DayOfWeek == DayOfWeek.Friday) continue; // outpatient clinics closed on Fridays (demo assumption)
            foreach (var clinic in clinics)
            {
                var clinicRooms = rooms.Where(r => r.ClinicId == clinic.Id).ToList();
                var volume = rnd.Next(15, 45);
                for (var s = 1; s <= volume; s++)
                {
                    var issued = date.AddHours(8).AddMinutes(rnd.Next(0, 300));
                    var roll = rnd.Next(100);
                    var outcome = roll < 84 ? TicketStatus.Completed : roll < 93 ? TicketStatus.NoShow : roll < 97 ? TicketStatus.Transferred : TicketStatus.NotSeen;
                    var wait = Math.Round(5 + rnd.NextDouble() * 50, 2);
                    var service = Math.Round(4 + rnd.NextDouble() * 14, 2);
                    var room = clinicRooms[rnd.Next(clinicRooms.Count)];
                    DateTime? called = outcome == TicketStatus.NotSeen ? null : issued.AddMinutes(wait);
                    store.Add(new VisitHistory
                    {
                        ServiceDate = date, ClinicId = clinic.Id, Sequence = s, TicketNumber = $"{clinic.Code}-{s:000}",
                        DoctorId = outcome == TicketStatus.NotSeen ? null : doctors[Math.Min(doctors.Count - 1, rooms.IndexOf(room))].Id,
                        RoomId = outcome == TicketStatus.NotSeen ? null : room.Id,
                        Source = rnd.Next(10) < 6 ? TicketSource.SelfServiceQr : TicketSource.Reception,
                        Outcome = outcome, IsPriority = rnd.Next(12) == 0, SkipCount = outcome == TicketStatus.NoShow ? 1 : 0,
                        IssuedAt = issued, FirstCalledAt = called,
                        CompletedAt = called?.AddMinutes(outcome == TicketStatus.Completed ? service : 0),
                        WaitMinutes = called is null ? null : wait,
                        ServiceMinutes = outcome == TicketStatus.Completed ? service : null,
                        IssuedHour = issued.Hour
                    });
                }
            }
        }
        await store.SaveChangesAsync();

        // Frozen daily reports for the demo history.
        var all = await store.ToListAsync(store.Query<VisitHistory>());
        foreach (var g in all.GroupBy(v => v.ServiceDate).OrderBy(g => g.Key))
        {
            var summary = await ReportService.BuildAsync(store, g.Select(ReportService.FromHistory).ToList(), g.Key, g.Key);
            store.Add(new DailyReport
            {
                ServiceDate = g.Key, GeneratedAt = g.Key.AddHours(23).AddMinutes(59), GeneratedBy = "system", Trigger = ResetTrigger.Scheduled,
                TotalTickets = summary.Total, Completed = summary.Completed, NoShows = summary.NoShows, NotSeen = summary.NotSeen,
                Transferred = summary.Transferred, Cancelled = summary.Cancelled, AvgWaitMinutes = summary.AvgWaitMinutes,
                AvgServiceMinutes = summary.AvgServiceMinutes, NotificationsSent = summary.Total * 2, AuditEventCount = summary.Total * 4,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(summary)
            });
        }
        await store.SaveChangesAsync();
    }
}
