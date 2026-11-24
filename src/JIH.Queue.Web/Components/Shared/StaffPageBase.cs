using System.Security.Claims;
using JIH.Queue.Core.Services;
using JIH.Queue.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace JIH.Queue.Web.Components.Shared;

/// <summary>
/// Base for interactive pages: current user, localisation, a flash message,
/// and real-time refresh when the queue or the configuration changes.
/// </summary>
public abstract class LivePageBase : ComponentBase, IDisposable
{
    [Inject] protected Loc L { get; set; } = default!;
    [Inject] protected QueueEvents Events { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

    protected ClaimsPrincipal User { get; private set; } = new();
    protected string Username => User.Identity?.Name ?? "anonymous";
    protected string DisplayName => User.FindFirst("display_name")?.Value ?? Username;
    protected int? DoctorId => int.TryParse(User.FindFirst("doctor_id")?.Value, out var d) ? d : null;
    protected bool IsInRole(string role) => User.IsInRole(role);

    protected string? FlashMessage;
    protected bool FlashIsError;
    private CancellationTokenSource? _flashCts;
    private bool _subscribed;
    private int _refreshing;

    protected override async Task OnInitializedAsync()
    {
        if (AuthState is not null) User = (await AuthState).User;
        await LoadAsync();
        if (RendererInfo.IsInteractive && !_subscribed)
        {
            Events.QueueChanged += OnQueueChanged;
            Events.ConfigurationChanged += OnConfigChanged;
            Events.Announced += OnAnnounced;
            _subscribed = true;
        }
    }

    /// <summary>Load / reload the page data.</summary>
    protected abstract Task LoadAsync();

    /// <summary>Return false to ignore changes of clinics that are not shown.</summary>
    protected virtual bool IsRelevant(int clinicId) => true;

    protected virtual Task OnAnnouncementAsync(Announcement a) => Task.CompletedTask;

    private void OnQueueChanged(QueueChange c) { if (IsRelevant(c.ClinicId)) _ = RefreshAsync(); }
    private void OnConfigChanged() => _ = RefreshAsync();
    private void OnAnnounced(Announcement a) => _ = InvokeAsync(() => OnAnnouncementAsync(a));

    /// <summary>Coalesces bursts of events into one reload.</summary>
    protected async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            await Task.Delay(150);
            await InvokeAsync(async () =>
            {
                Interlocked.Exchange(ref _refreshing, 0);
                await LoadAsync();
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException) { }
        finally { Interlocked.Exchange(ref _refreshing, 0); }
    }

    protected void Flash(string message, bool error = false)
    {
        FlashMessage = message;
        FlashIsError = error;
        _flashCts?.Cancel();
        var cts = _flashCts = new CancellationTokenSource();
        _ = Task.Delay(error ? 7000 : 4000, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            FlashMessage = null;
            InvokeAsync(StateHasChanged);
        }, TaskScheduler.Default);
    }

    protected void Flash(OpResult r, string okMessage)
    {
        if (r.Ok) Flash(okMessage); else Flash(L.Error(r.Error, r.Args), true);
    }

    public virtual void Dispose()
    {
        if (_subscribed)
        {
            Events.QueueChanged -= OnQueueChanged;
            Events.ConfigurationChanged -= OnConfigChanged;
            Events.Announced -= OnAnnounced;
        }
        _flashCts?.Cancel();
        GC.SuppressFinalize(this);
    }
}
