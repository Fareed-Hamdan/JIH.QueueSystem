using JIH.Queue.Core.Data;
using JIH.Queue.Core.Domain;
using JIH.Queue.Core.Notifications;
using JIH.Queue.Core.Services;

namespace JIH.Queue.Web.Services;

/// <summary>Creates the schema, writes default settings, seeds dummy data, and closes days left open while the server was off.</summary>
public sealed class StartupInitializer(IServiceProvider sp, IConfiguration config, ILogger<StartupInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var factory = sp.GetRequiredService<IDataStoreFactory>();
        await factory.InitializeAsync(ct);
        await sp.GetRequiredService<SettingsService>().EnsureDefaultsAsync();

        if (config.GetValue("Seed:Enabled", true))
        {
            var seeded = await sp.GetRequiredService<DemoDataSeeder>().SeedAsync(
                config.GetValue("Seed:DemoTickets", false), config.GetValue("Seed:DemoHistory", true));
            if (seeded) logger.LogInformation("Dummy data seeded. Default admin login: admin / Admin@12345 – change it!");
        }

        var eod = sp.GetRequiredService<EndOfDayService>();
        if (await eod.HasStaleDataAsync())
        {
            var clock = sp.GetRequiredService<IClock>();
            logger.LogWarning("Found queue data from a previous day – closing it now.");
            await eod.RunAsync(ResetTrigger.Scheduled, "system", clock.Today.AddDays(-1));
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Sends SMS / WhatsApp messages in the background so staff screens never wait for Twilio.</summary>
public sealed class NotificationWorker(NotificationQueue queue, NotificationDispatcher dispatcher, IConfiguration config, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var fallbackBase = config["App:PublicBaseUrl"] ?? "http://localhost:8080";
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await dispatcher.ProcessAsync(job, fallbackBase, stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Notification job failed"); }
        }
    }
}

/// <summary>Runs the end-of-day report + reset at the configured time, and closes any stale previous day.</summary>
public sealed class EndOfDayScheduler(EndOfDayService eod, SettingsService settings, IClock clock, ILogger<EndOfDayScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var s = await settings.GetAsync();
                var now = clock.Now;
                if (await eod.HasStaleDataAsync())
                    await eod.RunAsync(ResetTrigger.Scheduled, "system", now.Date.AddDays(-1));
                if (s.AutoResetEnabled && now.TimeOfDay >= s.ResetTimeOfDay && !await eod.ScheduledReportExistsAsync(now.Date))
                {
                    logger.LogInformation("Running scheduled end of day for {Date:yyyy-MM-dd}", now.Date);
                    await eod.RunAsync(ResetTrigger.Scheduled, "system", now.Date);
                }
            }
            catch (Exception ex) { logger.LogError(ex, "End-of-day scheduler error"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
