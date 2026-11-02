using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Persistence;

/// <summary>Resource bounds for commands and snapshots; not a polling-rate policy.</summary>
public static class PersistenceLimits
{
    public const int MaxSections = 2000;
    public const int MaxLabelLength = 120;
    public const int MaxPendingRead = 200;
}

internal static class PersistenceValidation
{
    internal static Guid Id(Guid value, string name) => value != Guid.Empty
        ? value : throw new ArgumentException("An identity cannot be empty.", name);

    internal static long Positive(long value, string name) => value > 0
        ? value : throw new ArgumentOutOfRangeException(name, "A positive value is required.");

    internal static long Nonnegative(long value, string name) => value >= 0
        ? value : throw new ArgumentOutOfRangeException(name, "A nonnegative value is required.");

    internal static T Defined<T>(T value, string name) where T : struct, Enum => Enum.IsDefined(value)
        ? value : throw new ArgumentOutOfRangeException(name, "The value is unsupported.");

    internal static string Label(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > PersistenceLimits.MaxLabelLength || value.Any(char.IsControl))
            throw new ArgumentException("A label needs 1-120 characters without control characters.", nameof(value));
        return value.Trim();
    }

    internal static ImmutableArray<T> Snapshot<T>(IEnumerable<T> values, int limit, string name) where T : class
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values is ImmutableArray<T> { IsDefault: true })
            throw new ArgumentException("The collection must be initialized.", name);
        var result = values.Take(limit + 1).ToImmutableArray();
        if (result.Length > limit || result.Any(item => item is null))
            throw new ArgumentException("The collection exceeds its bound or contains a null item.", name);
        return result;
    }

    internal static ImmutableArray<SectionKey> Sections(IEnumerable<SectionKey> sections)
    {
        var result = Snapshot(sections, PersistenceLimits.MaxSections, nameof(sections)).Distinct().ToImmutableArray();
        if (result.IsEmpty) throw new ArgumentException("At least one section is required.", nameof(sections));
        var first = result[0];
        if (result.Any(k => k.Year != first.Year || k.SemesterCode != first.SemesterCode || k.DetailSemesterCode != first.DetailSemesterCode))
            throw new ArgumentException("Sections must belong to one full academic term.", nameof(sections));
        return result;
    }

    internal static WatchExpiry? Expiry(WatchExpiry? expiry)
    {
        if (expiry is not null && (string.IsNullOrWhiteSpace(expiry.ZoneId) || expiry.ZoneId.Length > 128 || expiry.ExclusiveUtc.Offset != TimeSpan.Zero))
            throw new ArgumentException("Expiry needs a bounded zone ID and a UTC boundary.", nameof(expiry));
        return expiry;
    }

    internal static void Notification(OpeningNotification notification)
    {
        if (notification.CurrentSeats <= 0 || notification.Recipients.Count is < 1 or > PersistenceLimits.MaxSections)
            throw new ArgumentException("An opening needs positive seats and bounded recipients.", nameof(notification));
        if (notification.Recipients.Any(r => r is null || !Guid.TryParseExact(r.SubscriptionId, "N", out var id)
            || id == Guid.Empty || id.ToString("N") != r.SubscriptionId || r.Generation <= 0 || !Enum.IsDefined(r.Reason)))
            throw new ArgumentException("Recipients need canonical identities, positive generations and supported reasons.", nameof(notification));
        if (notification.Recipients.DistinctBy(r => (r.SubscriptionId, r.Generation)).Count() != notification.Recipients.Count)
            throw new ArgumentException("Recipient associations must be unique.", nameof(notification));
    }
}
