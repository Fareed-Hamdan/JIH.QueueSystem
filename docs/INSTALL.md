# Installation guide – local server

Two ways to install on a hospital server: **A) Docker** (recommended, about 15 minutes) or **B) manual** (Windows/IIS or Linux with an existing Oracle database).
The server must be on the same network as the reception PCs, the doctors' PCs, the waiting-hall TVs, and the hospital Wi-Fi the patients use.

## Requirements

| | Minimum |
|---|---|
| Server | 4 CPU cores, 8 GB RAM (Oracle Free uses up to 2 GB), 30 GB disk |
| OS | Linux (Ubuntu 22.04/24.04) or Windows Server 2019+ with Docker Desktop / Docker Engine |
| Network | Fixed IP address (e.g. 192.168.1.20), port 8080 open on the LAN |
| Waiting-hall TVs | Any PC/mini-PC/smart TV with Chrome or Edge, speakers, connected to the LAN |
| Printer (optional) | 80 mm thermal receipt printer installed as the default printer on the reception PC |

---

## A) Docker installation (Oracle + application + nginx)

1. Install Docker Engine and the Compose plugin (`docker compose version` must work).
2. Copy the project folder to the server, e.g. `/opt/jih-queue`.
3. Create the configuration file:
   ```bash
   cd /opt/jih-queue
   cp .env.example .env
   nano .env
   ```
   - `ORACLE_SYS_PASSWORD`, `APP_DB_PASSWORD`: new strong passwords.
   - `PUBLIC_BASE_URL`: the address patients' phones will open, e.g. `http://192.168.1.20:8080`. It's used in QR codes and SMS links.
   - `QR_SECRET`: a long random string (e.g. `openssl rand -hex 32`).
4. Build and start:
   ```bash
   docker compose up -d --build
   docker compose logs -f app      # wait for "Oracle schema created" and "Application started"
   ```
   The first start downloads the images. Oracle needs 1–3 minutes to start, and the app waits for it automatically.
   The Docker build also runs the 21 business-rule tests, and the image is only built if they all pass.
5. Open `http://192.168.1.20:8080`, sign in as `admin / Admin@12345`, then go to **Admin → Users** and **change every default password**.
6. Go to **Admin → Settings** and check the hospital name, the address patients' phones can reach, and the reset time.

Useful commands:
```bash
docker compose ps                         # status
docker compose restart app                # restart the application
docker compose down                       # stop (data is kept in the oracle-data volume)
docker compose exec oracle-db sqlplus JIH_QUEUE/<APP_DB_PASSWORD>@FREEPDB1   # SQL console
```

**Backup** (daily, e.g. with cron):
```bash
docker compose exec oracle-db bash -c "expdp JIH_QUEUE/<APP_DB_PASSWORD>@FREEPDB1 schemas=JIH_QUEUE directory=DATA_PUMP_DIR dumpfile=jih_%U.dmp logfile=jih.log"
```

**Quick demo without Oracle** (data in memory, lost on restart, includes sample patients):
```bash
docker compose -f docker-compose.demo.yml up --build     # http://localhost:8080
```

**Official Oracle image instead of the community image:** in `docker-compose.yml` replace the `oracle-db` image with
`container-registry.oracle.com/database/free:latest`, set `ORACLE_PWD`, and create the user yourself with
`database/oracle/00_create_user.sql` (see below). The service name is `FREEPDB1` in both images.

---

## B) Manual installation (existing Oracle database)

### 1. Database
Ask the DBA to create the schema user (Oracle 19c or later):
```sql
CREATE USER JIH_QUEUE IDENTIFIED BY "StrongPassword" QUOTA UNLIMITED ON USERS;
GRANT CREATE SESSION, CREATE TABLE, CREATE SEQUENCE TO JIH_QUEUE;
```
The application creates the tables on first start. If the DBA prefers to create them, run `database/oracle/01_schema.sql` as `JIH_QUEUE` first.

### 2. Build the application
On a machine with the .NET 10 SDK and internet access (to download NuGet packages):
```bash
dotnet run --project tests/JIH.Queue.RuleTests            # all tests must pass
dotnet publish src/JIH.Queue.Web -c Release -o publish
```

### 3. Configure
Edit `publish/appsettings.json` (or set environment variables, where `__` stands for `:`):

| Setting | Example |
|---|---|
| `Storage:Provider` | `Oracle` |
| `ConnectionStrings:Oracle` | `User Id=JIH_QUEUE;Password=...;Data Source=dbserver:1521/ORCLPDB1` |
| `App:PublicBaseUrl` | `http://192.168.1.20:8080` |
| `Qr:Secret` | long random string |
| `Seed:Enabled` | `true` on first start (creates dummy clinics, doctors and logins), `false` afterwards |
| `Seed:DemoHistory` | `true` adds 14 days of anonymous sample statistics; use `false` for a clean installation |
| `Twilio:*` | see "Notifications" below |

### 4. Run
- **Linux:** install the ASP.NET Core 10 runtime, then run `ASPNETCORE_URLS=http://0.0.0.0:8080 dotnet JIH.Queue.Web.dll` as a systemd service. Put nginx in front using `deploy/nginx.conf`; WebSockets must be allowed.
- **Windows / IIS:** install the *ASP.NET Core 10 Hosting Bundle*, create a site pointing to the `publish` folder with the app pool set to "No Managed Code", **enable the WebSocket Protocol feature** (Server Manager → Web Server → Application Development), and set the app pool's *Start Mode* to *AlwaysRunning* and *Idle Time-out* to 0, so the end-of-day job and the TV screens keep working.
- Set the server time zone to **Asia/Amman**. The app uses Amman time either way.

---

## Waiting-hall TV setup

1. **Admin → Waiting screens**: each screen has a code (e.g. `HALL-1`) and the clinics it shows (leave all unticked to show every clinic).
2. On the TV PC open `http://192.168.1.20:8080/display/HALL-1` in full screen (F11).
3. Browsers only play sound after one click: tap the screen once ("Tap to enable sound"). For an unattended TV, start Chrome in kiosk mode:
   ```
   chrome --kiosk --autoplay-policy=no-user-gesture-required http://192.168.1.20:8080/display/HALL-1
   ```
   Put this command in the PC's start-up folder so the screen opens by itself after a power cut.
4. Voice: **Admin → Settings → Voice source**. *Recorded clips* works without internet. *Browser voice* uses the PC's own speech engine (needs an Arabic voice installed in Windows).
   To use a human voice, record files with the same names as those in `src/JIH.Queue.Web/wwwroot/audio` (see `tools/generate-audio.sh`) and replace them.

## Reception QR code
- Show `/reception` on the receptionist's PC, or `/reception/qr` full screen on a monitor or tablet facing the patients.
- Patients' phones must reach the server: connect them to the hospital guest Wi-Fi with access to the server IP on port 8080, and set **Admin → Settings → Address patients' phones can reach**.

## Notifications (SMS / WhatsApp) – sandbox / trial
Default channel is **Mock**: messages are only written to the log and nothing is sent.

**Twilio WhatsApp sandbox (free):**
1. Create a Twilio account → Console → Messaging → *Try it out* → *Send a WhatsApp message*.
2. Every test phone must first send the shown code (e.g. `join example-word`) to the sandbox number **+1 415 523 8886** on WhatsApp.
3. Put `TWILIO_ACCOUNT_SID`, `TWILIO_AUTH_TOKEN` and `TWILIO_WHATSAPP_FROM=+14155238886` in `.env`, then run `docker compose up -d`.
4. **Admin → Settings → Channel → WhatsApp (Twilio sandbox)**.

**Twilio SMS trial:** buy or claim the trial number, verify each recipient number in the Twilio console (trial accounts only send to verified numbers), set `TWILIO_SMS_FROM`, then choose *SMS* in settings.

Delivery results appear in each daily report (sent / failed). Errors are written to the application log.
The server needs outbound internet access to `api.twilio.com` (HTTPS 443).

## Updating
```bash
cd /opt/jih-queue && git pull     # or copy the new files
docker compose up -d --build
```
The database schema is created once. If a future version changes tables, it will come with a migration script.

## Troubleshooting

| Symptom | Check |
|---|---|
| Screens don't update by themselves / "Rejoining the server…" | WebSockets blocked by a proxy. Use `deploy/nginx.conf`, or enable WebSockets in IIS |
| QR code opens "This code has expired" | Clock of the server; QR older than 3 minutes; the patient opened a forwarded link |
| QR opens nothing on the phone | `PUBLIC_BASE_URL` / Admin setting must be an address reachable from the guest Wi-Fi |
| No sound on the TV | Tap the screen once, or use the kiosk command above; check the TV volume |
| App waits for Oracle forever | `docker compose logs oracle-db`; check passwords in `.env` |
| Notifications "Failed" | Twilio credentials; recipient joined the sandbox / is verified; outbound internet |
