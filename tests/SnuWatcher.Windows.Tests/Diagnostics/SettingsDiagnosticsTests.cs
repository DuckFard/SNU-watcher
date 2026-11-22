using SnuWatcher.Application.Presentation.Diagnostics;
using SnuWatcher.Windows.Presentation.Common;
using SnuWatcher.Windows.Presentation.Settings;
using SnuWatcher.Windows.Tests.Presentation.Settings;
using Xunit;

namespace SnuWatcher.Windows.Tests.Diagnostics;

public sealed class SettingsDiagnosticsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    [Fact]
    public async Task ExplicitRefreshAndCanceledExportUseNoPreferenceOrNativeCalls()
    {
        await using var fixture = new SettingsFixture(); var reader = new Reader(); var action = new ExportAction();
        await using var model = Create(fixture, reader, action);
        Assert.Equal(0, reader.Calls);
        await model.RefreshDiagnosticsCommand.ExecuteAsync();
        Assert.Same(reader.Value, model.DiagnosticsModel.State.Snapshot);
        Assert.Equal("Ui.DiagnosticsChecked", model.DiagnosticsModel.State.IssueKey);
        Assert.Equal(UiCommandOutcome.Completed, await model.ExportDiagnosticsCommand.ExecuteAsync());
        Assert.Equal("Ui.DiagnosticsCanceled", model.DiagnosticsModel.State.IssueKey);
        Assert.Equal(2, reader.Calls); Assert.Equal(1, action.Calls);
        Assert.Empty(fixture.Store.Commands); Assert.Equal(0, fixture.Store.Reads); Assert.Empty(fixture.Profile.Calls);
    }

    [Fact]
    public async Task ReadAndExportFailuresKeepHistoricalFactsAndExposeOnlyFixedKeys()
    {
        await using var fixture = new SettingsFixture(); var reader = new Reader(); var action = new ExportAction();
        await using var model = Create(fixture, reader, action);
        await model.RefreshDiagnosticsCommand.ExecuteAsync();
        reader.Fail = true;
        Assert.Equal(UiCommandOutcome.Completed, await model.RefreshDiagnosticsCommand.ExecuteAsync());
        Assert.Same(reader.Value, model.DiagnosticsModel.State.Snapshot);
        Assert.Equal("Ui.DiagnosticsFailed", model.DiagnosticsModel.State.IssueKey);
        reader.Fail = false; action.Export = _ => throw new IOException("C:/private/student/cookie=secret");
        Assert.Equal(UiCommandOutcome.Completed, await model.ExportDiagnosticsCommand.ExecuteAsync());
        Assert.Equal("Ui.DiagnosticsFailed", model.DiagnosticsModel.State.IssueKey);
        Assert.Empty(fixture.Errors); // Expected local IO failures cannot stop the watching runtime.
        action.Export = _ => Task.FromResult(DiagnosticsExportOutcome.Saved);
        await model.ExportDiagnosticsCommand.ExecuteAsync();
        Assert.Equal("Ui.DiagnosticsSaved", model.DiagnosticsModel.State.IssueKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportPermissionFailuresAreLocalButInvalidPortOutcomesRemainUnexpected(bool security)
    {
        await using var fixture = new SettingsFixture(); var reader = new Reader(); var action = new ExportAction();
        await using var model = Create(fixture, reader, action);
        action.Export = _ => throw (security ? new System.Security.SecurityException("private details") : new UnauthorizedAccessException("private details"));
        Assert.Equal(UiCommandOutcome.Completed, await model.ExportDiagnosticsCommand.ExecuteAsync());
        Assert.Empty(fixture.Errors); Assert.Equal("Ui.DiagnosticsFailed", model.DiagnosticsModel.State.IssueKey);
        action.Export = _ => Task.FromResult((DiagnosticsExportOutcome)99);
        Assert.Equal(UiCommandOutcome.Failed, await model.ExportDiagnosticsCommand.ExecuteAsync());
        Assert.Equal("ui_command_failed", Assert.Single(fixture.Errors));
        Assert.Equal("Ui.DiagnosticsFailed", model.DiagnosticsModel.State.IssueKey);
    }

    [Fact]
    public async Task StopDrainsActualUncooperativeExportAndRetainsLateKnownSavedResult()
    {
        await using var fixture = new SettingsFixture(); var reader = new Reader(); var action = new ExportAction();
        var entered = Barrier(); var release = Barrier();
        action.Export = async _ => { entered.TrySetResult(); await release.Task; return DiagnosticsExportOutcome.Saved; };
        await using var model = Create(fixture, reader, action);
        var exporting = model.ExportDiagnosticsCommand.ExecuteAsync(); Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            disposal = model.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
            Assert.False(model.ExportDiagnosticsCommand.CanExecute(null));
            Assert.Contains(await model.ExportDiagnosticsCommand.ExecuteAsync(), new[] { UiCommandOutcome.Busy, UiCommandOutcome.Unavailable });
        }
        finally { release.TrySetResult(); }
        await exporting.WaitAsync(Deadline); await disposal!.WaitAsync(Deadline);
        Assert.Equal("Ui.DiagnosticsSaved", model.DiagnosticsModel.State.IssueKey);
        Assert.Equal(1, action.Calls);
        Assert.Equal(UiCommandOutcome.Unavailable, await model.ExportDiagnosticsCommand.ExecuteAsync());
    }

    [Fact]
    public async Task CancellationBeforeReadFinishesNeverOpensExportAndPreferencesRemainIndependent()
    {
        await using var fixture = new SettingsFixture(); var reader = new Reader(); var action = new ExportAction();
        var entered = Barrier(); var release = Barrier();
        reader.Read = async _ => { entered.TrySetResult(); await release.Task; return reader.Value; };
        await using var model = Create(fixture, reader, action); await model.LoadAsync();
        var reading = model.ExportDiagnosticsCommand.ExecuteAsync();
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await fixture.Dispatcher.InvokeAsync(() => model.NotificationSoundMuted = true);
            Assert.Equal(UiCommandOutcome.Completed, await model.SaveCommand.ExecuteAsync().WaitAsync(Deadline));
            model.ExportDiagnosticsCommand.Cancel(); Assert.False(reading.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(UiCommandOutcome.Canceled, await reading.WaitAsync(Deadline));
        Assert.Equal(0, action.Calls); Assert.Equal("Ui.DiagnosticsCanceled", model.DiagnosticsModel.State.IssueKey);
        Assert.True(Assert.Single(fixture.Store.Commands).Values.NotificationSoundMuted);
    }

    [Fact]
    public async Task ReentrantStopThroughFlowingUiContextPreventsDiagnosticAdmissionAndStillDrains()
    {
        await using var fixture = new SettingsFixture(); var reader = new Reader(); var action = new ExportAction();
        await using var model = new SettingsViewModel(fixture.Store, fixture.Profile, new FlowingDispatcher(fixture.Dispatcher),
            new SettingsClock(), diagnostics: reader, diagnosticsExport: action);
        Task? disposal = null;
        model.DiagnosticsModel.PropertyChanged += (_, _) =>
        {
            if (!model.DiagnosticsModel.State.IsBusy || disposal is not null) return;
            disposal = model.DisposeAsync().AsTask();
        };
        await model.ExportDiagnosticsCommand.ExecuteAsync().WaitAsync(Deadline);
        Assert.NotNull(disposal); await disposal.WaitAsync(Deadline);
        Assert.Equal(0, reader.Calls); Assert.Equal(0, action.Calls);
    }

    [Fact]
    public async Task RejectingUiDispatcherCannotSkipTheActualExportDrain()
    {
        await using var fixture = new SettingsFixture(); var reader = new Reader(); var action = new ExportAction();
        var dispatcher = new RejectingDispatcher(fixture.Dispatcher);
        var entered = Barrier(); var release = Barrier();
        action.Export = async _ => { entered.TrySetResult(); await release.Task; return DiagnosticsExportOutcome.Saved; };
        await using var model = new SettingsViewModel(fixture.Store, fixture.Profile, dispatcher, new SettingsClock(),
            reportError: fixture.Errors.Add, diagnostics: reader, diagnosticsExport: action);
        var exporting = model.ExportDiagnosticsCommand.ExecuteAsync(); Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(Deadline); dispatcher.Reject = true;
            disposal = model.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(UiCommandOutcome.Failed, await exporting.WaitAsync(Deadline));
        await disposal!.WaitAsync(Deadline); Assert.Equal(1, action.Calls);
    }

    [Fact]
    public async Task DisposalRetainsTheActualExportCancellationCallback()
    {
        await using var fixture = new SettingsFixture(); var reader = new Reader(); var action = new ExportAction();
        var entered = Barrier(); var callbackEntered = Barrier(); var callbackRelease = Barrier();
        action.Export = async token =>
        {
            using var registration = token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Task.GetAwaiter().GetResult(); });
            entered.TrySetResult();
            // Only this callback owns cancellation: an independently canceled delay must not
            // race its registration's disposal before the callback begins.
            await callbackEntered.Task;
            token.ThrowIfCancellationRequested();
            return DiagnosticsExportOutcome.Saved;
        };
        await using var model = Create(fixture, reader, action);
        var exporting = model.ExportDiagnosticsCommand.ExecuteAsync(); Task? disposal = null;
        try
        {
            await entered.Task.WaitAsync(Deadline); disposal = model.DisposeAsync().AsTask();
            await callbackEntered.Task.WaitAsync(Deadline); Assert.False(disposal.IsCompleted);
        }
        finally { callbackRelease.TrySetResult(); }
        await exporting.WaitAsync(Deadline); await disposal!.WaitAsync(Deadline);
        Assert.Equal("Ui.DiagnosticsCanceled", model.DiagnosticsModel.State.IssueKey);
    }

    private static SettingsViewModel Create(SettingsFixture fixture, Reader reader, ExportAction action) =>
        new(fixture.Store, fixture.Profile, fixture.Dispatcher, new SettingsClock(), reportError: fixture.Errors.Add,
            diagnostics: reader, diagnosticsExport: action);
    private static TaskCompletionSource Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Reader : IDiagnosticsReader
    {
        internal DiagnosticsSnapshot Value { get; } = DiagnosticsTests.Snapshot();
        internal int Calls; internal bool Fail;
        internal Func<CancellationToken, Task<DiagnosticsSnapshot>>? Read;
        public Task<DiagnosticsSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        { Calls++; return Fail ? throw new IOException("private database path") : Read?.Invoke(cancellationToken) ?? Task.FromResult(Value); }
    }
    private sealed class ExportAction : IDiagnosticsExportAction
    {
        internal int Calls;
        internal Func<CancellationToken, Task<DiagnosticsExportOutcome>> Export = _ => Task.FromResult(DiagnosticsExportOutcome.Canceled);
        public Task<DiagnosticsExportOutcome> ExportAsync(DiagnosticsSnapshot snapshot, CancellationToken cancellationToken = default)
        { Calls++; return Export(cancellationToken); }
    }
    private sealed class FlowingDispatcher(IUiDispatcher inner) : IUiDispatcher
    {
        public bool CheckAccess() => inner.CheckAccess();
        public void Post(Action action) => inner.Post(Capture(action));
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => inner.InvokeAsync(Capture(action), cancellationToken);
        private static Action Capture(Action action)
        {
            var context = ExecutionContext.Capture();
            return () => { if (context is null) action(); else ExecutionContext.Run(context, _ => action(), null); };
        }
    }
    private sealed class RejectingDispatcher(IUiDispatcher inner) : IUiDispatcher
    {
        internal volatile bool Reject;
        public bool CheckAccess() => inner.CheckAccess();
        public void Post(Action action) { if (Reject) throw new InvalidOperationException("private UI path"); inner.Post(action); }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) => Reject
            ? Task.FromException(new InvalidOperationException("private UI path")) : inner.InvokeAsync(action, cancellationToken);
    }
}
