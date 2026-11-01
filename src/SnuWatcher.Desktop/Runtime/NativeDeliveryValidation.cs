using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SnuWatcher.Windows.Hosting;
using SnuWatcher.Windows.Validation;

namespace SnuWatcher.Desktop.Runtime;

/// <summary>Explicit installed-only native qualification. Malformed prefixes return before every normal startup route.</summary>
internal static class NativeDeliveryValidation
{
    private static NativeDeliveryValidationRun? _blocked;

    internal static async Task<bool> TryHandleAsync(string[] arguments, Action<int> shutdown)
    {
        var route = InstalledNativeDeliveryProtocol.ParseArguments(arguments, out var runId);
        if (route == ValidationArgumentDisposition.NotRequested) return false;
        if (route == ValidationArgumentDisposition.Invalid)
        { WriteError("invalid_native_delivery_argument"); shutdown(2); return true; }

        InstalledNativeDeliveryProfile? claimed = null;
        NativeDeliveryValidationRun? run = null;
        try
        {
            // The script checks the independently pinned full inventory BEFORE launching this apphost;
            // this second check binds the executing pipeline to its create-new private intent.
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("native_executable_missing");
            if (Path.GetFileName(executable) != "SnuWatcher.Desktop.exe") throw new InvalidOperationException("native_target_invalid");
            var fingerprint = InstalledNativePipeline.ReadInstalled(Path.GetDirectoryName(executable)!);
            claimed = InstalledNativeDeliveryProfile.Claim(runId, fingerprint.ExecutableSha256, fingerprint.PipelineSha256);
            run = new(claimed, executable);
            await run.ExecuteAsync();
            if (!await run.FinishAsync()) throw new InvalidOperationException("native_validation_stop_blocked");
            var handoff = await run.VerifyHandoffAsync();
            if (!handoff) throw new InvalidOperationException("native_validation_owner_handoff_failed");
            WriteResult(claimed.Profile, run.SuccessEvidence());
            shutdown(0);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var drained = run is null || await run.FinishAsync();
            if (claimed is not null)
            {
                try
                {
                    WriteResult(claimed.Profile, new { schemaVersion = 1, codePath = "native-delivery-validation", testStatus = "FAILED",
                        code = drained ? "installed_native_delivery_failed" : "native_validation_stop_blocked", runId = runId.ToString("N"),
                        @case = claimed.Intent.CaseName, processId = Environment.ProcessId, executableSha256 = claimed.Intent.ExecutableSha256,
                        pipelineSha256 = claimed.Intent.PipelineSha256, fixtureOnly = true, liveHttpRequests = 0,
                        workersDrained = drained, ownerHandoff = false, visibleBannerConfirmed = false, cleanVmGatePassed = false });
                }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or InvalidOperationException)
                { WriteError("native_validation_result_write_failed"); }
            }
            else WriteError("native_validation_preflight_rejected");
            if (drained) shutdown(2);
            else _blocked = run; // Never drop actual SDK/coordinator ownership to pretend the timeout drained.
        }
        return true;
    }

    private static void WriteResult(RuntimeProfile profile, object result)
    {
        profile.ValidateExisting();
        using var stream = new FileStream(profile.ResultPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, result); stream.Flush(true);
    }
    private static void WriteError(string code)
    {
        try { Console.Error.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, codePath = "native-delivery-validation",
            testStatus = "REJECTED", code, nativeShowCalls = 0, liveHttpRequests = 0 })); }
        catch (IOException) { /* A closed test stderr cannot fall through into production startup. */ }
    }
}
