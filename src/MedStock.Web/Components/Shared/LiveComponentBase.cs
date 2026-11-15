using System.Security.Claims;
using MedStock.Web.Data;
using MedStock.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace MedStock.Web.Components.Shared;

/// <summary>
/// Base for components that stay current in real time: subscribes to the singleton event hub and reloads
/// (coalesced, at most ~3 times per second) when an event with a relevant topic arrives.
/// The push to the browser is the Blazor Server SignalR circuit; no JavaScript involved.
/// Also resolves the signed-in user's name and role flags.
/// </summary>
public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    [Inject] protected InventoryEventHub Hub { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState>? AuthTask { get; set; }

    protected string Actor { get; private set; } = "unknown";
    protected bool CanManage { get; private set; }
    protected bool CanFulfill { get; private set; }
    protected bool IsAdmin { get; private set; }

    private bool _running, _pending, _disposed;

    protected virtual Topic Topics => Topic.None;
    protected abstract Task LoadAsync();

    protected override async Task OnInitializedAsync()
    {
        if (AuthTask is not null)
        {
            var u = (await AuthTask).User;
            Actor = u.FindFirstValue("display_name") ?? u.Identity?.Name ?? "unknown";
            IsAdmin = u.IsInRole(Roles.Admin);
            CanManage = IsAdmin || u.IsInRole(Roles.WarehouseManager);
            CanFulfill = CanManage || u.IsInRole(Roles.Picker);
        }
        Hub.Published += OnPublished;
        await LoadAsync();
    }

    private void OnPublished(ActivityEvent e)
    {
        if (_disposed || (e.Topics & Topics) == 0) return;
        _ = InvokeAsync(RefreshAsync);
    }

    protected async Task RefreshAsync()
    {
        if (_disposed) return;
        if (_running) { _pending = true; return; }
        _running = true;
        try
        {
            do
            {
                _pending = false;
                await LoadAsync();
                if (_disposed) return;
                StateHasChanged();
                await Task.Delay(300);
            } while (_pending && !_disposed);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally { _running = false; }
    }

    public virtual void Dispose()
    {
        _disposed = true;
        Hub.Published -= OnPublished;
        GC.SuppressFinalize(this);
    }
}
