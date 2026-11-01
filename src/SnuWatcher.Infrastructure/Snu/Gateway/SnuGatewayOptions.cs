namespace SnuWatcher.Infrastructure.Snu.Gateway;

/// <summary>Bounded metadata and caller resources. These values do not change source request pacing.</summary>
public sealed record SnuGatewayOptions
{
    public SnuGatewayOptions(int catalogCapacity = 16, int detailsCapacity = 128, TimeSpan? metadataTtl = null,
        int maxLiveFlights = 128, int maxCallers = 256)
    {
        if (catalogCapacity is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(catalogCapacity));
        if (detailsCapacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(detailsCapacity));
        var ttl = metadataTtl ?? TimeSpan.FromMinutes(15);
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(metadataTtl));
        if (maxLiveFlights is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maxLiveFlights));
        if (maxCallers is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxCallers));
        CatalogCapacity = catalogCapacity;
        DetailsCapacity = detailsCapacity;
        MetadataTtl = ttl;
        MaxLiveFlights = maxLiveFlights;
        MaxCallers = maxCallers;
    }

    public int CatalogCapacity { get; }
    public int DetailsCapacity { get; }
    public TimeSpan MetadataTtl { get; }
    public int MaxLiveFlights { get; }
    public int MaxCallers { get; }
}
