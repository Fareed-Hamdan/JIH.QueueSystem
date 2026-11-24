using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;
using JIH.Queue.Core.Services;
using Microsoft.Extensions.Logging;

namespace JIH.Queue.Core.Notifications;

public sealed record NotificationJob(int TicketId, NotificationKind Kind);

/// <summary>Non-blocking queue: the doctor's "Call next" never waits for Twilio.</summary>
public sealed class NotificationQueue
{
    private readonly Channel<NotificationJob> _channel = Channel.CreateUnbounded<NotificationJob>();
    public void Enqueue(NotificationJob job) => _channel.Writer.TryWrite(job);
    public ChannelReader<NotificationJob> Reader => _channel.Reader;
}

public interface INotificationSender
{
    NotificationChannel Channel { get; }
    /// <summary>Returns provider message id. Throws on failure.</summary>
    Task<string?> SendAsync(string toE164, string body, CancellationToken ct);
}

/// <summary>Builds bilingual message texts. Messages never contain the complaint.</summary>
public static class NotificationTexts
{
    public static string Compose(NotificationKind kind, bool arabic, string hospital, string number, string clinic,
        int peopleAhead, int? room, string trackUrl) => (kind, arabic) switch
    {
        (NotificationKind.TicketIssued, true) =>
            $"{hospital}\nرقم دورك: {number} – {clinic}\nعدد المنتظرين قبلك: {peopleAhead}\nتابع دورك: {trackUrl}",
        (NotificationKind.TicketIssued, false) =>
            $"{hospital}\nYour number: {number} – {clinic}\nPatients ahead of you: {peopleAhead}\nTrack: {trackUrl}",
        (NotificationKind.TurnApproaching, true) =>
            $"{hospital}\nاقترب دورك ({number}). يوجد {peopleAhead} قبلك. يرجى التواجد في قاعة الانتظار.",
        (NotificationKind.TurnApproaching, false) =>
            $"{hospital}\nYour turn is approaching ({number}). {peopleAhead} ahead of you. Please be in the waiting hall.",
        (NotificationKind.Called, true) =>
            $"{hospital}\nحان دورك: {number}. يرجى التوجه إلى غرفة {room}.",
        _ =>
            $"{hospital}\nIt's your turn: {number}. Please go to room {room}."
    };
}

/// <summary>Loads the ticket, composes the message, sends it on the configured channel and logs the result.</summary>
public sealed class NotificationDispatcher(
    IDataStoreFactory factory,
    IClock clock,
    SettingsService settings,
    IEnumerable<INotificationSender> senders,
    ILogger<NotificationDispatcher> logger)
{
    public async Task ProcessAsync(NotificationJob job, string fallbackBaseUrl, CancellationToken ct)
    {
        var s = await settings.GetAsync();
        string to, body;
        int logId;
        await using (var store = factory.Create())
        {
            var ticket = await store.FirstOrDefaultAsync(store.Query<QueueTicket>().Where(t => t.Id == job.TicketId), ct);
            if (ticket is null) return;
            var patient = await store.FirstOrDefaultAsync(store.Query<Patient>().Where(p => p.Id == ticket.PatientId), ct);
            var clinic = await store.FirstOrDefaultAsync(store.Query<Clinic>().Where(c => c.Id == ticket.ClinicId), ct);
            if (patient is null || clinic is null) return;
            int? roomNo = null;
            if (ticket.RoomId is int rid)
                roomNo = (await store.FirstOrDefaultAsync(store.Query<Room>().Where(r => r.Id == rid), ct))?.Number;

            var waiting = await store.ToListAsync(store.Query<QueueTicket>()
                .Where(t => t.ClinicId == ticket.ClinicId && t.ServiceDate == ticket.ServiceDate && t.Status == TicketStatus.Waiting), ct);
            var ahead = Math.Max(0, Rules.QueueOrdering.PeopleAhead(Rules.QueueOrdering.Ordered(waiting), ticket.Id));

            var ar = patient.Language != "en";
            var baseUrl = string.IsNullOrWhiteSpace(s.PublicBaseUrl) ? fallbackBaseUrl : s.PublicBaseUrl;
            body = NotificationTexts.Compose(job.Kind, ar, s.HospitalName(ar), ticket.Number, clinic.Name(ar), ahead, roomNo,
                $"{baseUrl.TrimEnd('/')}/ticket/{ticket.PublicId}");
            to = patient.Phone;

            var log = new NotificationLog
            {
                TicketId = ticket.Id, Kind = job.Kind, Channel = s.NotificationChannel, ToMasked = PhoneNumbers.Mask(to),
                Body = body, Status = NotificationStatus.Queued, CreatedAt = clock.Now
            };
            store.Add(log);
            await store.SaveChangesAsync(ct);
            logId = log.Id;
        }

        NotificationStatus status;
        string? providerId = null, error = null;
        if (s.NotificationChannel == NotificationChannel.Mock)
        {
            status = NotificationStatus.Mocked;
            logger.LogInformation("[MOCK {Kind}] to {To}: {Body}", job.Kind, PhoneNumbers.Mask(to), body.Replace('\n', ' '));
        }
        else
        {
            var sender = senders.FirstOrDefault(x => x.Channel == s.NotificationChannel);
            try
            {
                if (sender is null) throw new InvalidOperationException($"No sender configured for {s.NotificationChannel}");
                providerId = await sender.SendAsync(to, body, ct);
                status = NotificationStatus.Sent;
            }
            catch (Exception ex)
            {
                status = NotificationStatus.Failed;
                error = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
                logger.LogWarning(ex, "Notification {Kind} for ticket {Ticket} failed", job.Kind, job.TicketId);
            }
        }

        await using (var store = factory.Create())
        {
            var log = await store.FirstOrDefaultAsync(store.Query<NotificationLog>().Where(l => l.Id == logId), ct);
            if (log is null) return; // the day was reset in the meantime
            log.Status = status; log.ProviderMessageId = providerId; log.Error = error; log.SentAt = clock.Now;
            await store.SaveChangesAsync(ct);
        }
    }
}

// ---------------------------------------------------------------- Twilio (sandbox / trial)

public sealed class TwilioOptions
{
    public string AccountSid { get; set; } = "";
    public string AuthToken { get; set; } = "";
    /// <summary>WhatsApp sender, e.g. "+14155238886" (Twilio WhatsApp sandbox number).</summary>
    public string WhatsAppFrom { get; set; } = "";
    /// <summary>SMS sender: your Twilio trial number, e.g. "+15005550006".</summary>
    public string SmsFrom { get; set; } = "";
}

/// <summary>Sends through the Twilio REST API (no SDK needed). Works with the free WhatsApp sandbox and SMS trial.</summary>
public abstract class TwilioSenderBase(HttpClient http, TwilioOptions options) : INotificationSender
{
    public abstract NotificationChannel Channel { get; }
    protected abstract string From { get; }
    protected abstract string Address(string e164);

    public async Task<string?> SendAsync(string toE164, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.AccountSid) || string.IsNullOrWhiteSpace(options.AuthToken) || string.IsNullOrWhiteSpace(From))
            throw new InvalidOperationException("Twilio is not configured (Twilio:AccountSid / AuthToken / From).");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(options.AccountSid)}/Messages.json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{options.AccountSid}:{options.AuthToken}")));
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["To"] = Address(toE164),
            ["From"] = Address(From),
            ["Body"] = body
        });
        using var res = await http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            string msg = json;
            try { msg = JsonDocument.Parse(json).RootElement.GetProperty("message").GetString() ?? json; } catch { }
            throw new HttpRequestException($"Twilio {(int)res.StatusCode}: {msg}");
        }
        try { return JsonDocument.Parse(json).RootElement.GetProperty("sid").GetString(); } catch { return null; }
    }
}

public sealed class TwilioWhatsAppSender(HttpClient http, TwilioOptions o) : TwilioSenderBase(http, o)
{
    private readonly TwilioOptions _o = o;
    public override NotificationChannel Channel => NotificationChannel.WhatsApp;
    protected override string From => _o.WhatsAppFrom;
    protected override string Address(string e164) => e164.StartsWith("whatsapp:") ? e164 : "whatsapp:" + e164;
}

public sealed class TwilioSmsSender(HttpClient http, TwilioOptions o) : TwilioSenderBase(http, o)
{
    private readonly TwilioOptions _o = o;
    public override NotificationChannel Channel => NotificationChannel.Sms;
    protected override string From => _o.SmsFrom;
    protected override string Address(string e164) => e164;
}
