using System;
using Microsoft.Windows.AppNotifications;
using SnuWatcher.Application.Delivery.Batches;
using SnuWatcher.Windows.Notifications;

namespace SnuWatcher.Desktop.Notifications;

/// <summary>SDK object creation and synchronous posting on the channel's exclusively owned MTA worker.</summary>
internal sealed class SdkNativeNotificationDriver(Func<bool> registered, Func<bool> shortcutVerified) : INativeNotificationDriver
{
    public BatchChannelReadiness Probe()
    {
        if (!registered() || !shortcutVerified() || NativeAppIdentity.ReadCurrent() != NativeAppIdentity.AppId)
            return BatchChannelReadiness.Unavailable;
        var setting = AppNotificationManager.Default.Setting;
        return setting == AppNotificationSetting.Enabled ? BatchChannelReadiness.Ready
            : setting == AppNotificationSetting.Unsupported ? BatchChannelReadiness.Unavailable : BatchChannelReadiness.Disabled;
    }

    public IPreparedNativeNotification Prepare(NativeToastPayload payload)
    {
        var notification = new AppNotification(payload.Xml) { Tag = payload.Tag, Group = payload.Group };
        if (payload.ExpiresAtUtc is { } expiry) notification.Expiration = expiry;
        return new Prepared(notification);
    }

    private sealed class Prepared(AppNotification notification) : IPreparedNativeNotification
    {
        public NativeShowResult Show()
        {
            // Pinned SDK 2.6.4 Show returns void synchronously; it has no cancellation/abort API.
            // Only a newly nonzero ID proves posting acceptance. Exceptions and zero remain unknown.
            AppNotificationManager.Default.Show(notification);
            return new NativeShowResult(notification.Id);
        }
        public void Dispose() { /* AppNotification exposes no disposable native lifetime. */ }
    }
}
