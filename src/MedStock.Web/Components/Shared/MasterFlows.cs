using MedStock.Web.Services;
using MudBlazor;

namespace MedStock.Web.Components.Shared;

/// <summary>Shared UI flows for the master data pages: confirm-then-delete (falling back to Deactivate) and activate/deactivate.</summary>
public static class MasterFlows
{
    public static async Task DeleteAsync(IDialogService dialogs, ISnackbar snack, MasterDataService svc, MasterKind kind, int id, string label, Caller me)
    {
        var ok = await dialogs.ShowMessageBoxAsync($"Delete {label}?", "This permanently removes the record. It only works when nothing refers to it.",
            yesText: "Delete", cancelText: "Cancel");
        if (ok != true) return;
        var r = await svc.DeleteAsync(kind, id, me);
        if (r.Deleted) { snack.Add($"{label} deleted", Severity.Success); return; }
        if (!r.Blocked) { snack.Add(r.Message ?? "Delete failed", Severity.Error); return; }
        var deactivate = await dialogs.ShowMessageBoxAsync("Cannot delete", r.Message ?? "This record is in use.", yesText: "Deactivate instead", cancelText: "Keep as is");
        if (deactivate == true) await SetActiveAsync(snack, svc, kind, id, label, false, me);
    }

    public static async Task SetActiveAsync(ISnackbar snack, MasterDataService svc, MasterKind kind, int id, string label, bool active, Caller me)
    {
        var r = await svc.SetActiveAsync(kind, id, active, me);
        snack.Add(r.Ok ? $"{label} {(active ? "reactivated" : "deactivated")}" : r.Error!, r.Ok ? Severity.Success : Severity.Error);
    }

    public static DialogOptions Options => new() { MaxWidth = MaxWidth.Small, FullWidth = true };
}
