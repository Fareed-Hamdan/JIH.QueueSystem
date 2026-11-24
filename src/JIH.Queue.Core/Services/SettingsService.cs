using System.Globalization;
using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;

namespace JIH.Queue.Core.Services;

/// <summary>Typed view of the SYSTEM_SETTINGS table.</summary>
public sealed record QueueSettings
{
    public string HospitalNameAr { get; init; } = "المستشفى الإسلامي الأردني";
    public string HospitalNameEn { get; init; } = "Jordan Islamic Hospital";
    /// <summary>Send "your turn is approaching" when this many patients (or fewer) are ahead.</summary>
    public int NotifyAheadCount { get; init; } = 3;
    /// <summary>How many positions a skipped patient is moved back.</summary>
    public int SkipOffset { get; init; } = 3;
    /// <summary>Skips allowed before the ticket becomes a no-show (1 = delayed once, then no-show).</summary>
    public int MaxSkips { get; init; } = 1;
    public bool AutoResetEnabled { get; init; } = true;
    /// <summary>HH:mm local time of the automatic end-of-day report + reset.</summary>
    public string ResetTime { get; init; } = "23:59";
    public int QrRotationSeconds { get; init; } = 90;
    public bool AnnounceEnglish { get; init; } = true;
    /// <summary>"Clips" (pre-recorded files, works offline) or "BrowserTts".</summary>
    public string AudioMode { get; init; } = "Clips";
    public int DisplayNextCount { get; init; } = 3;
    public NotificationChannel NotificationChannel { get; init; } = NotificationChannel.Mock;
    /// <summary>Base URL that patients' phones can reach, e.g. http://192.168.1.20:8080. Empty = use the request URL.</summary>
    public string PublicBaseUrl { get; init; } = "";

    public string HospitalName(bool arabic) => arabic ? HospitalNameAr : HospitalNameEn;

    public TimeSpan ResetTimeOfDay =>
        TimeSpan.TryParseExact(ResetTime, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : new TimeSpan(23, 59, 0);
}

public static class SettingKeys
{
    public const string HospitalNameAr = "Hospital.NameAr";
    public const string HospitalNameEn = "Hospital.NameEn";
    public const string NotifyAheadCount = "Queue.NotifyAheadCount";
    public const string SkipOffset = "Queue.SkipOffset";
    public const string MaxSkips = "Queue.MaxSkipsBeforeNoShow";
    public const string AutoResetEnabled = "Reset.AutoEnabled";
    public const string ResetTime = "Reset.Time";
    public const string QrRotationSeconds = "Qr.RotationSeconds";
    public const string AnnounceEnglish = "Display.AnnounceEnglish";
    public const string AudioMode = "Display.AudioMode";
    public const string DisplayNextCount = "Display.NextCount";
    public const string NotificationChannel = "Notifications.Channel";
    public const string PublicBaseUrl = "Public.BaseUrl";

    public static readonly string[] All =
    [
        HospitalNameAr, HospitalNameEn, NotifyAheadCount, SkipOffset, MaxSkips, AutoResetEnabled, ResetTime,
        QrRotationSeconds, AnnounceEnglish, AudioMode, DisplayNextCount, NotificationChannel, PublicBaseUrl
    ];
}

public sealed class SettingsService(IDataStoreFactory factory, IClock clock, QueueEvents events)
{
    private QueueSettings? _cache;
    private readonly object _sync = new();

    public async Task<QueueSettings> GetAsync()
    {
        var cached = _cache;
        if (cached is not null) return cached;
        await using var store = factory.Create();
        var rows = await store.ToListAsync(store.Query<SystemSetting>());
        var s = FromRows(rows.ToDictionary(r => r.Key, r => r.Value));
        lock (_sync) _cache = s;
        return s;
    }

    public async Task<Dictionary<string, string>> GetRawAsync()
    {
        var s = await GetAsync();
        return ToRows(s);
    }

    public async Task<OpResult> SaveAsync(Dictionary<string, string> values, string username)
    {
        // Validate by parsing into the typed record first.
        var current = ToRows(await GetAsync());
        foreach (var (k, v) in values) current[k] = (v ?? "").Trim();
        var error = Validate(current);
        if (error is not null) return OpResult.Fail(error);

        await using (var store = factory.Create())
        {
            var rows = (await store.ToListAsync(store.Query<SystemSetting>())).ToDictionary(r => r.Key);
            foreach (var key in SettingKeys.All)
            {
                var value = current[key];
                if (rows.TryGetValue(key, out var row))
                {
                    if (row.Value == value) continue;
                    Audit.Write(store, clock, username, AuditActions.SettingChanged, nameof(SystemSetting), key, null, $"{row.Value} → {value}");
                    row.Value = value; row.UpdatedAt = clock.Now; row.UpdatedBy = username;
                }
                else
                {
                    store.Add(new SystemSetting { Key = key, Value = value, UpdatedAt = clock.Now, UpdatedBy = username });
                }
            }
            await store.SaveChangesAsync();
        }
        Invalidate();
        events.PublishConfigurationChanged();
        return OpResult.Success();
    }

    /// <summary>Writes default rows for any missing keys.</summary>
    public async Task EnsureDefaultsAsync()
    {
        await using var store = factory.Create();
        var existing = (await store.ToListAsync(store.Query<SystemSetting>())).Select(r => r.Key).ToHashSet();
        foreach (var (k, v) in ToRows(new QueueSettings()))
            if (!existing.Contains(k))
                store.Add(new SystemSetting { Key = k, Value = v, UpdatedAt = clock.Now, UpdatedBy = "system" });
        await store.SaveChangesAsync();
        Invalidate();
    }

    public void Invalidate() { lock (_sync) _cache = null; }

    private static string? Validate(Dictionary<string, string> v)
    {
        static bool Int(string s, int min, int max) => int.TryParse(s, out var i) && i >= min && i <= max;
        if (string.IsNullOrWhiteSpace(v[SettingKeys.HospitalNameAr]) || string.IsNullOrWhiteSpace(v[SettingKeys.HospitalNameEn])) return "err.required";
        if (!Int(v[SettingKeys.NotifyAheadCount], 0, 20)) return "err.settingRange";
        if (!Int(v[SettingKeys.SkipOffset], 1, 20)) return "err.settingRange";
        if (!Int(v[SettingKeys.MaxSkips], 0, 5)) return "err.settingRange";
        if (!Int(v[SettingKeys.QrRotationSeconds], 15, 3600)) return "err.settingRange";
        if (!Int(v[SettingKeys.DisplayNextCount], 0, 10)) return "err.settingRange";
        if (!TimeSpan.TryParseExact(v[SettingKeys.ResetTime], @"hh\:mm", CultureInfo.InvariantCulture, out _)) return "err.timeFormat";
        if (!Enum.TryParse<NotificationChannel>(v[SettingKeys.NotificationChannel], out _)) return "err.settingRange";
        if (v[SettingKeys.AudioMode] is not ("Clips" or "BrowserTts")) return "err.settingRange";
        return null;
    }

    private static QueueSettings FromRows(Dictionary<string, string> r)
    {
        var d = new QueueSettings();
        string S(string k, string def) => r.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;
        int I(string k, int def) => r.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : def;
        bool B(string k, bool def) => r.TryGetValue(k, out var v) && bool.TryParse(v, out var b) ? b : def;
        return new QueueSettings
        {
            HospitalNameAr = S(SettingKeys.HospitalNameAr, d.HospitalNameAr),
            HospitalNameEn = S(SettingKeys.HospitalNameEn, d.HospitalNameEn),
            NotifyAheadCount = I(SettingKeys.NotifyAheadCount, d.NotifyAheadCount),
            SkipOffset = I(SettingKeys.SkipOffset, d.SkipOffset),
            MaxSkips = I(SettingKeys.MaxSkips, d.MaxSkips),
            AutoResetEnabled = B(SettingKeys.AutoResetEnabled, d.AutoResetEnabled),
            ResetTime = S(SettingKeys.ResetTime, d.ResetTime),
            QrRotationSeconds = I(SettingKeys.QrRotationSeconds, d.QrRotationSeconds),
            AnnounceEnglish = B(SettingKeys.AnnounceEnglish, d.AnnounceEnglish),
            AudioMode = S(SettingKeys.AudioMode, d.AudioMode),
            DisplayNextCount = I(SettingKeys.DisplayNextCount, d.DisplayNextCount),
            NotificationChannel = Enum.TryParse<NotificationChannel>(S(SettingKeys.NotificationChannel, ""), out var c) ? c : d.NotificationChannel,
            PublicBaseUrl = r.TryGetValue(SettingKeys.PublicBaseUrl, out var u) ? u : d.PublicBaseUrl
        };
    }

    private static Dictionary<string, string> ToRows(QueueSettings s) => new()
    {
        [SettingKeys.HospitalNameAr] = s.HospitalNameAr,
        [SettingKeys.HospitalNameEn] = s.HospitalNameEn,
        [SettingKeys.NotifyAheadCount] = s.NotifyAheadCount.ToString(),
        [SettingKeys.SkipOffset] = s.SkipOffset.ToString(),
        [SettingKeys.MaxSkips] = s.MaxSkips.ToString(),
        [SettingKeys.AutoResetEnabled] = s.AutoResetEnabled.ToString().ToLowerInvariant(),
        [SettingKeys.ResetTime] = s.ResetTime,
        [SettingKeys.QrRotationSeconds] = s.QrRotationSeconds.ToString(),
        [SettingKeys.AnnounceEnglish] = s.AnnounceEnglish.ToString().ToLowerInvariant(),
        [SettingKeys.AudioMode] = s.AudioMode,
        [SettingKeys.DisplayNextCount] = s.DisplayNextCount.ToString(),
        [SettingKeys.NotificationChannel] = s.NotificationChannel.ToString(),
        [SettingKeys.PublicBaseUrl] = s.PublicBaseUrl
    };
}
