# User and operation manual – Outpatient Queue System

**Jordan Islamic Hospital – prototype (all data is dummy data)**

This manual covers each role: reception, doctor/nurse, patient and system admin. For installation see INSTALL.md.

## The idea in short

- A patient gets a **queue number for a clinic** (not for a specific doctor), e.g. `A-015`. The letter is the clinic.
- Doctors are **assigned daily** to clinic rooms. All doctors of a clinic call from the **same queue**.
- The waiting-hall screen shows numbers and calls them **by number and room only**, in Arabic then English. Patient names are never shown.
- The patient gets a message when the ticket is issued, when **3 or fewer** patients are ahead, and when called.
- Each night a daily report is generated automatically, then **patient data is deleted** (names, phones, complaints). Only anonymous statistics are kept.

## Signing in

Open the system address (e.g. `http://192.168.1.20:8080`) and sign in. Use **العربية / English** at the top to switch language.

---

## 1. Reception – "Reception" page

### Self-service QR code
- Shown at the top of the page. It changes every 90 seconds, so it can't be used from outside the hospital.
- The patient scans it with the phone camera and fills in name, mobile number, clinic and a short description of the problem, then ticks the consent box.
- **Show code full screen** opens it for a monitor facing the patients.

### Issuing a ticket for a patient without a smartphone
1. Fill in name and phone and choose the clinic. The problem description is optional here.
2. Tick **Priority** for elderly, disabled, pregnant or urgent patients. Only reception can give priority.
3. Tick **The patient agreed verbally** after explaining how their data is used.
4. Click **Issue and print**. The ticket opens for the thermal printer, with a QR code the patient can scan to follow their turn.

### Clinics now
Each clinic shows how many are waiting, which numbers are with doctors now, and its status (Open, Paused, Full or Closed).

### Today's tickets
Search by number, name or phone, or filter by clinic and status. Actions:

| Action | When |
|---|---|
| Print | Any ticket |
| Priority / Remove priority | Waiting tickets. Removing priority never moves the patient back |
| Move to clinic | Patient chose the wrong clinic. They get a new number at the end of the new clinic's queue |
| Cancel ticket | Patient left |
| Back to queue | A no-show patient arrived. They go to the end of the queue; if the doctor skips them again they become a no-show straight away |

**Note:** one phone number can't have two active tickets in the same clinic on the same day.

---

## 2. Doctor and nurse – "Doctor dashboard"

1. A doctor goes straight to their room for today. Nurses and admins choose a room from the list.
2. Click **Start receiving patients**. No patient is sent to you before this.
3. Click **Call next**: the number appears on the hall screen with a voice announcement, and the patient gets an "It's your turn" message.
4. You see the number, name, waiting time and the complaint the patient typed.

| Button | What it does |
|---|---|
| **End visit** | Ends the visit and frees the room for the next patient |
| **Recall** | Repeats the announcement without changing the queue |
| **Didn't come** | First time: the patient moves back **3 places** (or to the end if the queue is shorter). Second time: marked **No-show** |
| **Refer to clinic** | Sends the patient to another clinic with a new number at the end of its queue, or at the front if you tick "Urgent" |
| **Take a break / Resume** | During a break no patients are called to your room; colleagues in the clinic keep going |
| **Finish shift** | At the end of the shift |

- You can't call a new patient before ending, skipping or referring the current one.
- If the clinic is Paused or Closed, Call next doesn't work.
- The queue on the side is shared by all doctors of the clinic and updates by itself.

---

## 3. Patient

1. Scan the QR code at reception and fill in the form, or get a printed ticket from the receptionist.
2. The ticket page shows the number, clinic, patients ahead and estimated wait. **It updates by itself.**
3. A WhatsApp/SMS message arrives when the turn is near, and again when called.
4. When called, the number appears on the hall screen and is announced: "Number A 15, please go to room 101".

If "This code has expired" appears, scan the code currently shown at reception again.

---

## 4. Waiting-hall screen

- Open `/display/HALL-1` on the TV. No sign-in is needed.
- Tap the screen once after opening it to enable sound, or use the kiosk command in INSTALL.md.
- Each clinic row shows the number(s) being served with the room (one line per doctor), then the next numbers.
- Every call shows a large card for 8 seconds with the voice announcement.

---

## 5. System admin – "Admin"

| Page | Use |
|---|---|
| **Admin dashboard** | Today's live figures (tickets, waiting, no-shows, average wait and consultation), change clinic status, doctors today, end-of-day button |
| **Clinics & rooms** | Add and edit clinics: code (one letter), Arabic and English names, status, daily ticket limit, last ticket time, and rooms (room numbers are announced) |
| **Doctors** | Add, edit and deactivate doctors |
| **Doctor schedule** | For each day and shift (morning/evening): doctor → clinic → room. **Copy** copies a previous day. A doctor or room can't be booked twice in one shift |
| **Users** | Staff accounts and roles (admin, reception, doctor linked to a doctor record, nurse), password changes |
| **Waiting screens** | TV screens and which clinics each one shows |
| **Reports** | Report for any period (by clinic, doctor, hour, day), saved daily reports, export to Excel (CSV), print |
| **Audit log** | Every action: who did what and when, with ticket numbers only |
| **Settings** | Hospital name, patients ahead before "turn approaching" (3), places moved back on skip (3), skips before no-show (1), QR lifetime, message channel, voice source, daily reset time |

### Clinic statuses

| Status | Tickets | Calls |
|---|---|---|
| Open | yes | yes |
| Paused | yes | no |
| Full (automatic when the daily limit is reached) | no | yes |
| Closed | no | no |

### End of day and the daily report
- Runs automatically at the time in Settings (default **23:59**). It can also be run manually from the Admin dashboard → **Generate report and reset now**.
- What happens, in order:
  1. Open tickets are closed: waiting patients become "Not seen".
  2. **The daily report is generated from the tickets, the message log and the audit log.**
  3. An anonymous copy of every visit is kept for statistics.
  4. **Patient names, phones and complaints are deleted.**
- If the server was off at reset time, the previous day is closed automatically at start-up.
- This can't be undone.

---

## FAQ

- **The patient says the code doesn't work:** it changes every 90 seconds. Ask them to scan the current code, or issue a ticket with the form.
- **The doctor can't call:** check they clicked "Start receiving patients", ended the current visit, and that the clinic isn't Paused or Closed.
- **No sound in the hall:** tap the TV screen once and check the volume.
- **Messages don't arrive:** in the prototype the default channel is "Mock", which sends nothing. See Notifications in INSTALL.md.
