namespace SnuWatcher.Core;

/// <summary>
/// A watch's end date. The selected local day is included: the stored boundary is the exclusive
/// start of the next local day, resolved to UTC once, so a later host timezone change never moves it.
/// </summary>
public sealed record WatchExpiry(DateOnly LocalDate, string ZoneId, DateTimeOffset ExclusiveUtc)
{
    /// <summary>Registration is in Korea, so expiry dates default to Asia/Seoul.</summary>
    public static TimeZoneInfo DefaultZone { get; } = ResolveSeoul();

    public static WatchExpiry FromLocalDate(DateOnly localDate, TimeZoneInfo? zone = null)
    {
        zone ??= DefaultZone;
        if (localDate == DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(localDate), "The last representable date cannot be an expiry day.");

        var start = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        start = FirstValidLocalTime(start, zone);
        // A midnight that occurs twice (fall-back inside the new day) begins at its first occurrence:
        // the clock first reads the next date there, so the selected day ends at the earlier instant.
        var offset = zone.IsAmbiguousTime(start)
            ? zone.GetAmbiguousTimeOffsets(start).Max()
            : zone.GetUtcOffset(start);
        return new WatchExpiry(localDate, zone.Id, new DateTimeOffset(start, offset).ToUniversalTime());
    }

    /// <summary>True from the boundary onward. The boundary itself is already expired.</summary>
    public bool IsExpiredAt(DateTimeOffset now) => now >= ExclusiveUtc;

    private static DateTime FirstValidLocalTime(DateTime start, TimeZoneInfo zone)
    {
        if (!zone.IsInvalidTime(start)) return start;

        // Find a valid upper bound, then refine within its preceding invalid minute. A gap may
        // end on a second boundary, so the first whole valid minute is not necessarily midnight's replacement.
        do { start = start.AddMinutes(1); } while (zone.IsInvalidTime(start));
        var invalidTicks = start.AddMinutes(-1).Ticks;
        var validTicks = start.Ticks;
        while (validTicks - invalidTicks > 1)
        {
            var middle = invalidTicks + (validTicks - invalidTicks) / 2;
            if (zone.IsInvalidTime(new DateTime(middle, DateTimeKind.Unspecified))) invalidTicks = middle;
            else validTicks = middle;
        }
        return new DateTime(validTicks, DateTimeKind.Unspecified);
    }

    private static TimeZoneInfo ResolveSeoul()
    {
        foreach (var id in new[] { "Asia/Seoul", "Korea Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        }
        // Seoul has had a fixed +09:00 offset for decades; use it if the host has no tz database.
        return TimeZoneInfo.CreateCustomTimeZone("Asia/Seoul", TimeSpan.FromHours(9), "Seoul", "KST");
    }
}
