using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Notifications;

namespace SnuWatcher.Windows.Validation;

/// <summary>Qualification-only decorator. The underlying worker, permission probe, Show result, identity and cleanup remain authoritative.</summary>
public sealed class NativeDeliveryFixtureDriver : INativeNotificationDriver
{
    private readonly INativeNotificationDriver _inner;
    private readonly UiLanguage _language;
    private readonly object _sync = new();
    private int _showCalls;
    private uint? _platformId;
    private string? _payloadHash;
    private bool _muted;
    /// <summary>Wraps the real driver without replacing its platform boundary.</summary>
    public NativeDeliveryFixtureDriver(INativeNotificationDriver inner, UiLanguage language)
    { ArgumentNullException.ThrowIfNull(inner); if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language)); _inner = inner; _language = language; }
    /// <summary>Actual entries to underlying Show, including throwing or uncertain calls.</summary>
    public int ShowCalls => Volatile.Read(ref _showCalls);
    /// <summary>The unchanged returned platform ID; null means no actual result was returned.</summary>
    public uint? PlatformNotificationId { get { lock (_sync) return _platformId; } }
    /// <summary>Raw UTF-8 labelled payload hash, without collecting source content or private paths.</summary>
    public string? PayloadSha256 { get { lock (_sync) return _payloadHash; } }
    /// <summary>Actual audio-silent element in the labelled payload.</summary>
    public bool SoundMuted { get { lock (_sync) return _muted; } }
    /// <summary>Returns the underlying probe unchanged; it cannot turn Disabled into Ready.</summary>
    public BatchChannelReadiness Probe() => _inner.Probe();
    /// <summary>Labels a validated opening before creating the real prepared native object.</summary>
    public IPreparedNativeNotification Prepare(NativeToastPayload payload)
    {
        var labelled = NativeDeliveryFixturePayload.LabelOpening(payload, _language);
        lock (_sync)
        {
            _payloadHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(labelled.Xml)));
            _muted = XDocument.Parse(labelled.Xml).Root!.Element("audio")?.Attribute("silent")?.Value == "true";
        }
        return new Prepared(this, _inner.Prepare(labelled));
    }
    private sealed class Prepared(NativeDeliveryFixtureDriver owner, IPreparedNativeNotification inner) : IPreparedNativeNotification
    {
        public NativeShowResult Show()
        {
            Interlocked.Increment(ref owner._showCalls);
            var result = inner.Show();
            lock (owner._sync) owner._platformId = result.PlatformNotificationId;
            return result;
        }
        public void Dispose() => inner.Dispose();
    }
}
