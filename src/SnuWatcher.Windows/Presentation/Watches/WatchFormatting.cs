using System.Globalization;
using SnuWatcher.Application.Monitoring.Health;
using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Windows.Presentation.Watches;

internal static class WatchFormatting
{
    internal static string Identifier(SectionKey key) => $"{key.CourseCode} {key.Section}";
    internal static string Counts(SectionHealthSnapshot? health) => health?.LastKnownCounts is { } seats
        ? string.Create(CultureInfo.InvariantCulture, $"{seats.Registered}/{seats.Capacity}") : "—";
    internal static string Time(DateTimeOffset? value) => value is { } time
        ? TimeZoneInfo.ConvertTime(time, WatchExpiry.DefaultZone).ToString("yyyy-MM-dd HH:mm:ss 'KST'", CultureInfo.InvariantCulture) : "—";
    internal static string Status(WatchStatus status) => status switch
    {
        WatchStatus.Active => "Ui.Active", WatchStatus.Paused => "Ui.Paused",
        WatchStatus.Expired => "Ui.Expired", _ => "Ui.Unknown",
    };
    internal static string Availability(AvailabilityState state) => state switch
    {
        AvailabilityState.Full => "Ui.Full", AvailabilityState.Open => "Ui.Open", _ => "Ui.Unknown",
    };
    internal static string DataState(SectionDataState state) => state switch
    {
        SectionDataState.Current => "Ui.Current", SectionDataState.Delayed => "Ui.Delayed",
        SectionDataState.ClockChanged => "Ui.ClockChanged", SectionDataState.NeverChecked => "Ui.NotChecked", _ => "Ui.Unknown",
    };
    internal static string Outcome(CheckOutcome? outcome) => outcome switch
    {
        CheckOutcome.Success => "Ui.CheckSuccess", CheckOutcome.RateLimited => "Ui.SourceRateLimited",
        CheckOutcome.AccessRestricted => "Ui.SourceRestricted", CheckOutcome.ParseChanged => "Ui.SourceChanged",
        CheckOutcome.NotFound => "Ui.SectionNotFound", CheckOutcome.Incomplete => "Ui.PartialResults",
        null => "Ui.NotChecked", _ => "Ui.Unknown",
    };
}
