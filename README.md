# Jordan Islamic Hospital – Outpatient Queue System (prototype)

A web system that organises outpatient queues: queue tickets at reception (printed or via a QR code on the patient's phone),
a waiting-hall TV screen that calls numbers by voice, a dashboard for doctors and nurses, SMS/WhatsApp notifications,
and an admin dashboard with daily reports. Arabic-first (right-to-left) with English. All screens update in real time.

> **Prototype – dummy data only.** No real patient data is included. Notifications go through a Twilio
> sandbox/trial account or the built-in "Mock" channel, which sends nothing.

| | |
|---|---|
| Stack | ASP.NET Core 10 · Blazor Server (interactive, real time over SignalR) · EF Core 10 · Oracle Database 19c+ (23ai Free in Docker) |
| Run | `docker compose up -d --build` → http://SERVER:8080 – see [docs/INSTALL.md](docs/INSTALL.md) |
| Manuals | [Arabic](docs/USER_MANUAL.ar.md) · [English](docs/USER_MANUAL.en.md) |
| Designs | [designs/index.html](designs/index.html) (Reception, Waiting screen, Doctor dashboard + patient and admin screens) |
| Default logins | `admin / Admin@12345`, `reception / Reception@123`, `nurse / Nurse@12345`, doctors `dr.samer … dr.dana / Doctor@123` – **change after installation** |

## Screens

| Address | Who | Purpose |
|---|---|---|
| `/reception` | Receptionist, Admin | Rotating QR code, issue/print tickets, clinic status, today's tickets (priority, move, cancel, reinstate no-show) |
| `/reception/qr` | Receptionist | Full-screen QR for a monitor facing the patients |
| `/join?k=…` | Patient (no login) | Form opened by scanning the QR: name, mobile, clinic, short problem description, consent |
| `/ticket/{id}` | Patient (no login) | Live ticket: patients ahead, estimated wait, "go to room X" |
| `/display/{CODE}` | TV (no login) | Waiting-hall board + voice announcement by **number and room only** |
| `/doctor` | Doctor, Nurse, Admin | Call next, recall, didn't come (skip), end visit, refer to clinic, start/pause/finish shift |
| `/admin/...` | Admin | Today's dashboard, clinics & rooms & statuses, doctors, daily doctor schedule, users, waiting screens, settings, reports, audit log, end of day |

## Business rules (enforced in the back end – `src/JIH.Queue.Core/Services/QueueService.cs`)

- **Patients belong to the clinic, not the doctor.** All doctors of a clinic call from one shared queue; the room is decided when the patient is called. A per-clinic lock makes sure two doctors never get the same patient.
- **Doctors are distributed daily** to a clinic + room per shift (morning/evening). A doctor can't be booked twice in a shift, a room can't hold two doctors, the room must belong to the clinic, and a doctor can only be *receiving patients* in one assignment at a time. Schedules can be copied from a previous day.
- **Ticket numbers** run per clinic per day with the clinic letter (A-001, B-001…). They continue (not restart) if the admin closes the day early.
- **Didn't come (skip):** the first time, the patient moves back **3 places** (to the end if fewer are waiting); the second time they are marked **No-show**. Reception can put a no-show back at the end of the queue; one more skip then makes them a no-show again. The offset (3) and the number of skips allowed (1) are settings.
- **Priority** (elderly, disabled, pregnant, urgent) is set by reception only; priority tickets go after the last waiting priority ticket. Removing priority never pushes the patient back.
- **Call next** requires the doctor's shift to be started and the current patient to be ended, skipped or referred first. A paused or closed clinic can't call. **Recall** repeats the announcement only.
- **Refer to clinic:** the current ticket becomes *Transferred* and a new ticket is created in the target clinic (end of queue, or priority if urgent). Accepted even when the target clinic is Full.
- **One active ticket per mobile number per clinic per day.** Jordanian mobile numbers only (07X…, +962…, Arabic-Indic digits accepted).
- **Clinic status:** Open (normal) · Paused (no calls, tickets still issued) · Full (no new tickets, calls continue – set automatically when the daily limit is reached) · Closed (nothing). Optional last-ticket time per clinic.
- **Rotating QR:** the self-service code is signed (HMAC) and changes every 90 s; a copied link stops working. Once opened, the patient has 15 minutes to submit.
- **Notifications:** on issue, when **3 or fewer** patients are ahead (once), and when called – in the patient's chosen language. They are sent in the background so staff screens never wait.
- **End of day (daily reset):** at 23:59 (setting) or manually by the admin: waiting tickets → *Not seen*, patients still with a doctor → closed, a **daily report is generated from the tickets, notifications and audit log**, every visit is copied to `VISIT_HISTORY` **without name, phone or complaint**, then patients, tickets and notification logs are deleted. If the server was off at reset time, the previous day is closed automatically at start-up.
- **Audit log** records every action (issue, call, recall, skip, no-show, end, transfer, reinstate, priority, clinic status, assignments, settings, logins, end of day) with ticket numbers and staff names only – never patient names or phones.

## Database (Oracle)

The app creates the schema on first start. The same DDL is in [database/oracle/01_schema.sql](database/oracle/01_schema.sql); useful SQL reports are in `02_report_queries.sql`.

| Table | Kept | Content |
|---|---|---|
| `CLINICS`, `ROOMS`, `DOCTORS` | permanent | Reference data, clinic status, daily limits |
| `DOCTOR_ASSIGNMENTS` | permanent | Daily distribution of doctors → clinic/room/shift, shift status |
| `STAFF_USERS` | permanent | Logins (PBKDF2 hashes), roles, link to doctor |
| `PATIENTS` | **reset daily** | Name, mobile, language, consent time |
| `QUEUE_TICKETS` | **reset daily** | Number, clinic, status, order, skips, timestamps, complaint |
| `NOTIFICATION_LOG` | **reset daily** | Messages and delivery status (masked phone) |
| `VISIT_HISTORY` | permanent, anonymous | One row per visit for statistics |
| `DAILY_REPORTS` | permanent | Frozen end-of-day report (+ JSON breakdown) |
| `AUDIT_LOG` | permanent | Who did what, when |
| `SYSTEM_SETTINGS` | permanent | Rules and options editable by the admin |
| `DISPLAY_DEVICES` | permanent | Waiting screens and the clinics each one shows |

## Project structure

```
src/JIH.Queue.Core          Domain, business rules, services, notifications, in-memory store (no NuGet dependencies)
src/JIH.Queue.Data.Oracle   EF Core model + Oracle storage (Oracle.EntityFrameworkCore 10)
src/JIH.Queue.Web           Blazor Server UI, Arabic/English texts (Services/Loc.cs), background jobs, endpoints
tests/JIH.Queue.RuleTests   21 business-rule tests (dotnet run) – also run during the Docker build
database/oracle             Schema DDL and report queries
deploy/, Dockerfile, docker-compose*.yml   Local server installation
tools/                      Voice-clip generator, translation checker
designs/                    Screen designs (Arabic + English) and sample announcement
docs/                       Installation guide and user manuals
```

Real time works like this: every change publishes an in-process event (`QueueEvents`), and each open screen (reception,
doctor, TV, patient phone) refreshes itself over its Blazor SignalR connection. This needs **one application instance**;
running several behind a load balancer would need a SignalR backplane (e.g. Redis).

## Development without Oracle

```bash
cd src/JIH.Queue.Web
dotnet run --launch-profile "InMemory (no Oracle)"     # http://localhost:5080, dummy data incl. sample tickets
dotnet run --project ../../tests/JIH.Queue.RuleTests   # business-rule tests
python3 ../../tools/check-translations.py              # every UI text has Arabic + English
```

## Known limitations / next steps for production

- Single server instance (see above). Add HTTPS (certificate on nginx) before using it outside a closed LAN.
- WhatsApp *Cloud API* (Meta) is not implemented; Twilio WhatsApp sandbox and SMS trial are. Production WhatsApp needs approved message templates.
- Voice clips are synthetic (espeak-ng). Replace them with human recordings of the same file names for a better result (`tools/generate-audio.sh` lists them).
- The logo is a generic placeholder (`wwwroot/img/mark.svg`, `Components/Shared/BrandMark.razor`).
- The data-protection review (Jordan Personal Data Protection Law No. 24 of 2023), retention periods and consent wording should be confirmed by the hospital's legal/IT team before going live.
