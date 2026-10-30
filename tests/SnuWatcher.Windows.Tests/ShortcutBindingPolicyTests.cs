using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class ShortcutBindingPolicyTests
{
    [Fact]
    public void AcceptsOnlyMatchingExecutableAndActualRegisteredActivator()
    {
        var activator = Guid.NewGuid();
        Assert.Equal(activator, ShortcutBindingPolicy.Validate(
            @"C:\Apps\SNU Watcher\SnuWatcher.Desktop.exe",
            @"c:\apps\SNU Watcher\SnuWatcher.Desktop.exe", activator.ToString("B")));
    }

    [Theory]
    [InlineData(@"C:\Other\Other.exe", @"C:\Apps\SnuWatcher.Desktop.exe", "{21B17E1A-536A-4BBF-A445-E30574467732}")]
    [InlineData("relative.exe", @"C:\Apps\SnuWatcher.Desktop.exe", "{21B17E1A-536A-4BBF-A445-E30574467732}")]
    [InlineData(@"C:\Apps\SnuWatcher.Desktop.exe", @"C:\Apps\SnuWatcher.Desktop.exe", "not-a-guid")]
    [InlineData(@"C:\Apps\SnuWatcher.Desktop.exe", @"C:\Apps\SnuWatcher.Desktop.exe", "00000000-0000-0000-0000-000000000000")]
    public void RejectsOtherApplicationsAndInvalidRegistryIdentity(string target, string process, string activator)
    {
        Assert.Throws<ArgumentException>(() => ShortcutBindingPolicy.Validate(target, process, activator));
    }
}
