using System.Globalization;
using System.Security.Claims;
using JIH.Queue.Core.Data;
using JIH.Queue.Core.Notifications;
using JIH.Queue.Core.Services;
using JIH.Queue.Web.Components;
using JIH.Queue.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
#if ORACLE
using JIH.Queue.Data.Oracle;
#endif

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ------------------------------------------------------------------ storage
var provider = config["Storage:Provider"] ?? "InMemory";
if (provider.Equals("Oracle", StringComparison.OrdinalIgnoreCase))
{
#if ORACLE
    var cs = config.GetConnectionString("Oracle")
             ?? throw new InvalidOperationException("ConnectionStrings:Oracle is missing.");
    builder.Services.AddOracleQueueStorage(cs);
#else
    throw new InvalidOperationException("This build was compiled without Oracle support (UseOracle=false). Set Storage:Provider=InMemory or rebuild.");
#endif
}
else
{
    builder.Services.AddSingleton<InMemoryDatabase>();
    builder.Services.AddSingleton<IDataStoreFactory, InMemoryDataStoreFactory>();
}

// ------------------------------------------------------------------ core services (all singletons: they create short-lived stores per operation)
builder.Services.AddSingleton<IClock, AmmanClock>();
builder.Services.AddSingleton<QueueEvents>();
builder.Services.AddSingleton<NotificationQueue>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<QueueService>();
builder.Services.AddSingleton<QueueQueryService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<ReportService>();
builder.Services.AddSingleton<EndOfDayService>();
builder.Services.AddSingleton<DemoDataSeeder>();
builder.Services.AddSingleton(config.GetSection("Qr").Get<QrTokenOptions>() ?? new QrTokenOptions());
builder.Services.AddSingleton<QrTokenService>();

// notifications (Twilio sandbox / trial; "Mock" channel by default)
builder.Services.AddSingleton(config.GetSection("Twilio").Get<TwilioOptions>() ?? new TwilioOptions());
builder.Services.AddHttpClient<TwilioWhatsAppSender>();
builder.Services.AddHttpClient<TwilioSmsSender>();
builder.Services.AddSingleton<INotificationSender>(sp => sp.GetRequiredService<TwilioWhatsAppSender>());
builder.Services.AddSingleton<INotificationSender>(sp => sp.GetRequiredService<TwilioSmsSender>());
builder.Services.AddSingleton<NotificationDispatcher>();

builder.Services.AddHostedService<StartupInitializer>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<EndOfDayScheduler>();

// ------------------------------------------------------------------ web
builder.Services.AddScoped<Loc>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/denied";
        o.ExpireTimeSpan = TimeSpan.FromHours(14);
        o.SlidingExpiration = true;
        o.Cookie.Name = "jih_queue_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear(); o.KnownProxies.Clear(); // behind the bundled nginx
});

var app = builder.Build();

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

var cultures = new[] { new CultureInfo("ar-JO"), new CultureInfo("en-US") };
foreach (var c in cultures) { c.NumberFormat.DigitSubstitution = DigitShapes.None; c.NumberFormat.NumberDecimalSeparator = "."; c.NumberFormat.NumberGroupSeparator = ","; } // Western digits, "." decimals
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("ar-JO"),
    SupportedCultures = cultures,
    SupportedUICultures = cultures,
    RequestCultureProviders = [new CookieRequestCultureProvider()] // Arabic first; English only when chosen
});

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

// ------------------------------------------------------------------ endpoints
app.MapPost("/account/login", async (HttpContext ctx, AdminService admin) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var returnUrl = form["returnUrl"].ToString();
    if (string.IsNullOrEmpty(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//")) returnUrl = "/";
    var user = await admin.ValidateLoginAsync(form["username"].ToString(), form["password"].ToString());
    if (user is null) return Results.Redirect($"/login?failed=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.Role, user.Role.ToString()),
        new("display_name", user.DisplayName),
    };
    if (user.DoctorId is int d) claims.Add(new Claim("doctor_id", d.ToString()));
    await ctx.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Redirect(returnUrl);
}).DisableAntiforgery();

app.MapGet("/account/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Redirect("/login");
});

app.MapGet("/culture/set", (HttpContext ctx, string culture, string? redirectUri) =>
{
    var c = culture == "en" ? "en-US" : "ar-JO";
    ctx.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(c)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });
    var target = !string.IsNullOrEmpty(redirectUri) && redirectUri.StartsWith('/') && !redirectUri.StartsWith("//") ? redirectUri : "/";
    return Results.Redirect(target);
});

app.MapGet("/admin/export/report.csv", async (DateTime from, DateTime to, string? lang, ReportService reports) =>
{
    var summary = await reports.RangeAsync(from, to);
    return Results.File(ReportService.ToCsv(summary, lang != "en"), "text/csv; charset=utf-8", $"report_{from:yyyyMMdd}_{to:yyyyMMdd}.csv");
}).RequireAuthorization(p => p.RequireRole("Admin"));

app.MapGet("/admin/export/daily/{id:int}.csv", async (int id, string? lang, ReportService reports) =>
{
    var r = await reports.GetDailyReportAsync(id);
    var summary = r is null ? null : ReportService.ParseDetails(r);
    return summary is null ? Results.NotFound()
        : Results.File(ReportService.ToCsv(summary, lang != "en"), "text/csv; charset=utf-8", $"daily_report_{r!.ServiceDate:yyyyMMdd}.csv");
}).RequireAuthorization(p => p.RequireRole("Admin"));

app.MapGet("/health", () => Results.Ok(new { status = "ok", storage = provider }));

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
