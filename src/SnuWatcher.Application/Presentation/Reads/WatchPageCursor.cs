using SnuWatcher.Application.Persistence;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Presentation.Reads;

/// <summary>Snapshot order and local read time, never a replacement for source observation time.</summary>
public sealed record UiReadStamp
{
    public UiReadStamp(long catalogRevision, long issuedCheckOrdinal, DateTimeOffset readAtUtc)
    {
        CatalogRevision = PersistenceValidation.Nonnegative(catalogRevision, nameof(catalogRevision));
        IssuedCheckOrdinal = PersistenceValidation.Nonnegative(issuedCheckOrdinal, nameof(issuedCheckOrdinal));
        ReadAtUtc = readAtUtc.ToUniversalTime();
    }
    public long CatalogRevision { get; }
    public long IssuedCheckOrdinal { get; }
    public DateTimeOffset ReadAtUtc { get; }
}

/// <summary>A full persisted academic term, constructed only from an already validated section identity.</summary>
public sealed record AcademicTermIdentity
{
    public AcademicTermIdentity(SectionKey section)
    {
        ArgumentNullException.ThrowIfNull(section);
        Year = section.Year; SemesterCode = section.SemesterCode; DetailSemesterCode = section.DetailSemesterCode;
    }
    public int Year { get; }
    public string SemesterCode { get; }
    public string DetailSemesterCode { get; }
}

/// <summary>Exclusive Guid-N ordinal/BINARY position in one nonremoved definition-catalog epoch.</summary>
public sealed record WatchPageCursor
{
    public WatchPageCursor(long catalogRevision, Guid afterSubscriptionId)
    {
        CatalogRevision = PersistenceValidation.Nonnegative(catalogRevision, nameof(catalogRevision));
        AfterSubscriptionId = PersistenceValidation.Id(afterSubscriptionId, nameof(afterSubscriptionId));
    }
    public long CatalogRevision { get; }
    public Guid AfterSubscriptionId { get; }
}
