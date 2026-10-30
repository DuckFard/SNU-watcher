using Microsoft.Win32.SafeHandles;
using SnuWatcher.Windows;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class ForegroundPermissionPolicyTests
{
    [Theory]
    [InlineData(ActivationAction.Show, true)]
    [InlineData(ActivationAction.ViewSection, true)]
    [InlineData(ActivationAction.OpenRegistration, false)]
    [InlineData(ActivationAction.Quit, false)]
    [InlineData((ActivationAction)100, false)]
    public void OnlyWindowActionsRequestForeground(ActivationAction action, bool expected)
    {
        Assert.Equal(expected, ForegroundPermissionPolicy.RequiresForeground(action));
    }

    [Theory]
    [InlineData(0U, 1U, 1U)]
    [InlineData(uint.MaxValue, 1U, 1U)]
    [InlineData(2147483648U, 1U, 1U)]
    [InlineData(42U, 2U, 1U)]
    public void NeverGrantsAnInvalidOrOtherSessionProcess(uint processId, uint ownerSession, uint currentSession)
    {
        var calls = 0;
        var allowed = ForegroundPermissionPolicy.TryAllow(processId, ownerSession, currentSession, _ =>
        {
            calls++;
            return true;
        });

        Assert.False(allowed);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReturnsTheActualWindowsPermissionResult(bool permitted)
    {
        uint? observedProcess = null;
        var allowed = ForegroundPermissionPolicy.TryAllow(42, 1, 1, processId =>
        {
            observedProcess = processId;
            return permitted;
        });

        Assert.Equal(42U, observedProcess);
        Assert.Equal(permitted, allowed);
    }

    [Fact]
    public void RequiresThePermissionBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => ForegroundPermissionPolicy.TryAllow(42, 1, 1, null!));
    }

    [Fact]
    public void AnInvalidPipeCannotSupplyAProcessIdentity()
    {
        using var pipe = new SafePipeHandle(IntPtr.Zero, ownsHandle: false);

        Assert.False(ForegroundPermissionTransfer.TryGetServerIdentity(pipe, out var identity));
        Assert.Equal(default, identity);
        Assert.False(ForegroundPermissionTransfer.TryAllow(pipe));
    }

    [Fact]
    public void AClosedPipeCannotSupplyAProcessIdentity()
    {
        var pipe = new SafePipeHandle(new IntPtr(123), ownsHandle: false);
        pipe.Dispose();

        Assert.False(ForegroundPermissionTransfer.TryGetServerIdentity(pipe, out _));
    }

    [Fact]
    public void ARegularFileHandleCannotSupplyAPipeServerIdentity()
    {
        using var file = File.OpenRead(typeof(ForegroundPermissionPolicyTests).Assembly.Location);
        using var notAPipe = new SafePipeHandle(file.SafeFileHandle.DangerousGetHandle(), ownsHandle: false);

        Assert.False(ForegroundPermissionTransfer.TryGetServerIdentity(notAPipe, out _));
    }
}
