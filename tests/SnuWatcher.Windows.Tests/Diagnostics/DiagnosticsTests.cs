using System.Text;
using System.Text.Json;
using SnuWatcher.Application.Monitoring.Hosting;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Diagnostics;
using SnuWatcher.Application.Presentation.Preferences;
using SnuWatcher.Windows.Diagnostics;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Application.Delivery.Batches;
using Xunit;

namespace SnuWatcher.Windows.Tests.Diagnostics;

public sealed class DiagnosticsTests
{
    internal static DiagnosticsSnapshot Snapshot(StorageFootprint? storage = null) => new(new(0, 1, 0, 0), new(10, 0),
        DateTimeOffset.UnixEpoch, DiagnosticsStateProjector.Project(RuntimeSessionState.Running,
            "C:/private/student/F32.102?cookie=secret", true, false, null, WatchMonitorProfile.Normal,
            UiPreferencesValues.Defaults, null), storage ?? new(new(DiagnosticFileState.Available, 12),
                new(DiagnosticFileState.Missing), new(DiagnosticFileState.Unavailable)));

    [Fact]
    public void ExportIsAnExactWhitelistAndCannotIncludeUnrecognizedRawEvidence()
    {
        var bytes = DiagnosticsJson.Serialize(Snapshot());
        Assert.InRange(bytes.Length, 1, 64 * 1024);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("private", text); Assert.DoesNotContain("cookie", text); Assert.DoesNotContain("F32", text);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(new[] { "format", "capturedAtUtc", "applicationVersion", "runtimeVersion", "runtime", "storage" },
            json.RootElement.EnumerateObject().Select(field => field.Name));
        Assert.Equal("OtherBlocked", json.RootElement.GetProperty("runtime").GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("storage").GetProperty("shm").GetProperty("bytes").ValueKind);
        Assert.DoesNotContain("path", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StorageUnknownIsNeverZeroAndThePreviewWarningNeverClaimsDeletion()
    {
        Assert.Throws<ArgumentException>(() => new DiagnosticFileSize(DiagnosticFileState.Unavailable, 0));
        Assert.Throws<ArgumentException>(() => new DiagnosticFileSize(DiagnosticFileState.Available));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticFileSize(DiagnosticFileState.Available, -1));
        var unknown = Snapshot().Storage;
        Assert.Null(unknown.TotalBytes); Assert.False(unknown.IsComplete); Assert.False(unknown.HasSizeWarning);
        var threshold = new StorageFootprint(new(DiagnosticFileState.Available, 256L * 1024 * 1024),
            new(DiagnosticFileState.Missing), new(DiagnosticFileState.Missing));
        Assert.True(threshold.IsComplete); Assert.True(threshold.HasSizeWarning);
        Assert.Equal(256L * 1024 * 1024, threshold.TotalBytes);
        var overflow = new StorageFootprint(new(DiagnosticFileState.Available, long.MaxValue),
            new(DiagnosticFileState.Available, 1), new(DiagnosticFileState.Missing));
        Assert.Null(overflow.TotalBytes); Assert.True(overflow.HasSizeWarning);
    }

    [Fact]
    public async Task FixedProfileMetadataReadsOnlyThreeFileLengthsWithoutReadingDatabaseContents()
    {
        var profile = RuntimeProfile.CreateValidationRun(Guid.NewGuid());
        try
        {
            await File.WriteAllTextAsync(profile.DatabasePath, "private contents are not SQLite and must not be opened as SQLite");
            await File.WriteAllBytesAsync(profile.DatabasePath + "-wal", [1, 2, 3]);
            var snapshot = await new ProfileStorageMetadataReader(profile).ReadAsync();
            Assert.Equal(new FileInfo(profile.DatabasePath).Length, snapshot.Database.Bytes);
            Assert.Equal(3, snapshot.Wal.Bytes); Assert.Equal(DiagnosticFileState.Missing, snapshot.Shm.State);
            Assert.True(snapshot.IsComplete);
            File.Delete(profile.DatabasePath); File.Delete(profile.DatabasePath + "-wal");
            Directory.CreateDirectory(profile.DatabasePath);
            Assert.Equal(DiagnosticFileState.Unavailable, (await new ProfileStorageMetadataReader(profile).ReadAsync()).Database.State);
            Directory.Delete(profile.DatabasePath);
        }
        finally
        {
            if (Directory.Exists(profile.DatabasePath)) Directory.Delete(profile.DatabasePath);
            else File.Delete(profile.DatabasePath);
            File.Delete(profile.DatabasePath + "-wal");
            File.Delete(profile.ManifestPath); Directory.Delete(profile.DirectoryPath);
        }
    }

    [Fact]
    public async Task ExportCreatesNewFileAndNeverOverwritesAnExistingSelection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SnuWatcher-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "report.json");
        var other = Path.Combine(directory, "canceled.json");
        try
        {
            Assert.Equal(DiagnosticsExportOutcome.Saved, await DiagnosticsFileExporter.SaveAsync(path, Snapshot()));
            var original = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<IOException>(() => DiagnosticsFileExporter.SaveAsync(path, Snapshot()));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiagnosticsFileExporter.SaveAsync(other, Snapshot(), canceled.Token));
            Assert.False(File.Exists(other));
        }
        finally { File.Delete(path); File.Delete(other); Directory.Delete(directory); }
    }

    [Fact]
    public void ProjectorUsesOnlyClosedEnumsAndKnownSavedPreferences()
    {
        var state = DiagnosticsStateProjector.Project(RuntimeSessionState.StopBlocked, "source_cooldown", false, false,
            RuntimeFailureKind.SavedState, WatchMonitorProfile.Faster, null, null);
        Assert.Equal(DiagnosticsSourceCondition.StorageFailed, state.Source);
        Assert.Null(state.Preferences.Language); Assert.Null(state.Preferences.SoundMuted);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticFileSize((DiagnosticFileState)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticsRuntimeFacts((RuntimeSessionState)99,
            DiagnosticsSourceCondition.Unknown, DiagnosticsNotificationCondition.Unknown, null, new(null, null, null, null), null, null));
    }

    [Theory]
    [InlineData(null, null, null, null, DiagnosticsSourceCondition.Unknown)]
    [InlineData(null, true, null, null, DiagnosticsSourceCondition.NoKnownBlock)]
    [InlineData(null, false, false, null, DiagnosticsSourceCondition.Offline)]
    [InlineData(null, true, true, null, DiagnosticsSourceCondition.Suspended)]
    [InlineData("source_cooldown", true, false, null, DiagnosticsSourceCondition.Cooldown)]
    [InlineData("source_access_restricted", true, false, null, DiagnosticsSourceCondition.AccessRestricted)]
    [InlineData(null, true, false, RuntimeFailureKind.Actor, DiagnosticsSourceCondition.ActorFailed)]
    public void RuntimeConditionsAreProjectedWithoutInferringSourceSuccess(string? code, bool? online, bool? suspended,
        RuntimeFailureKind? failure, DiagnosticsSourceCondition expected) => Assert.Equal(expected,
            DiagnosticsStateProjector.Project(RuntimeSessionState.Running, code, online, suspended, failure, null, null, null).Source);

    [Theory]
    [InlineData(BatchChannelReadiness.Initializing, DiagnosticsNotificationCondition.Initializing)]
    [InlineData(BatchChannelReadiness.Ready, DiagnosticsNotificationCondition.Ready)]
    [InlineData(BatchChannelReadiness.Disabled, DiagnosticsNotificationCondition.Disabled)]
    [InlineData(BatchChannelReadiness.Unavailable, DiagnosticsNotificationCondition.Unavailable)]
    public void CachedNotificationReadinessIsNotProbed(BatchChannelReadiness readiness, DiagnosticsNotificationCondition expected) =>
        Assert.Equal(expected, DiagnosticsStateProjector.Project(RuntimeSessionState.Running, null, null, null, null, null, null, readiness).Notifications);

    [Fact]
    public async Task ReaderCapturesExistingFactsOnceAndRejectsAlreadyCanceledWork()
    {
        var source = Snapshot(); var metadata = new Metadata(source.Storage); var calls = 0;
        var reader = new WindowsDiagnosticsReader(metadata, () => { calls++; return source.Runtime; }, new(1, 2, 3), TimeProvider.System);
        var result = await reader.ReadAsync();
        Assert.Same(source.Runtime, result.Runtime); Assert.Same(source.Storage, result.Storage);
        Assert.Equal(new Version(1, 2, 3), result.ApplicationVersion); Assert.Equal(Environment.Version, result.RuntimeVersion);
        Assert.Equal(1, calls); Assert.Equal(1, metadata.Calls);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(canceled.Token));
        Assert.Equal(1, calls); Assert.Equal(1, metadata.Calls);
    }

    [Fact]
    public void MaximumScalarValuesStillProduceABoundedWhitelistedDocumentAndInputsAreImmutable()
    {
        var snapshot = Snapshot(new(new(DiagnosticFileState.Available, long.MaxValue),
            new(DiagnosticFileState.Available, long.MaxValue), new(DiagnosticFileState.Available, long.MaxValue)));
        var extreme = new DiagnosticsSnapshot(new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue),
            new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue), DateTimeOffset.MaxValue, snapshot.Runtime, snapshot.Storage);
        Assert.InRange(DiagnosticsJson.Serialize(extreme).Length, 1, DiagnosticsJson.MaximumBytes);
        foreach (var type in new[] { typeof(DiagnosticsSnapshot), typeof(DiagnosticsRuntimeFacts), typeof(DiagnosticPreferenceFacts), typeof(StorageFootprint), typeof(DiagnosticFileSize) })
            Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Throws<ArgumentNullException>(() => DiagnosticsJson.Serialize(null!));
        Assert.Throws<ArgumentNullException>(() => new StorageFootprint(null!, snapshot.Storage.Wal, snapshot.Storage.Shm));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticPreferenceFacts((UiLanguage)99, null, null, null));
    }

    private sealed class Metadata(StorageFootprint value) : IStorageDiagnosticsReader
    {
        internal int Calls;
        public Task<StorageFootprint> ReadAsync(CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(value); }
    }
}
