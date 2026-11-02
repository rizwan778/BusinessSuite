using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace BusinessSuite.Web.Framework.Mvc;

/// <summary>Visual style of a one-time alert (maps to Bootstrap's alert colours).</summary>
public enum AlertType
{
    Success,
    Info,
    Warning,
    Danger,
}

/// <summary>
/// One-time messages ("Product saved") that survive a redirect, stored in TempData and
/// rendered by <c>Views/Shared/_Alerts.cshtml</c>.
/// </summary>
/// <remarks>
/// In controllers use the <see cref="AppController"/> helpers (<c>NotifySuccess</c>, ...).
/// Messages are HTML-encoded when displayed, so user input in them is safe.
/// </remarks>
public static class TempDataAlertExtensions
{
    private const string KeyPrefix = "Alerts.";

    /// <summary>Queues an alert for the next page that is rendered.</summary>
    public static void AddAlert(this ITempDataDictionary tempData, AlertType type, string message)
    {
        ArgumentNullException.ThrowIfNull(tempData);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var key = KeyPrefix + type;
        var existing = tempData.Peek(key) as string[] ?? [];
        string[] updated = [.. existing, message];
        tempData[key] = updated;
    }

    /// <summary>Returns and removes the queued alerts of one type.</summary>
    public static IReadOnlyList<string> TakeAlerts(this ITempDataDictionary tempData, AlertType type)
    {
        ArgumentNullException.ThrowIfNull(tempData);
        return tempData[KeyPrefix + type] as string[] ?? [];
    }
}
