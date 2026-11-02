using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace SnuWatcher.Desktop.Runtime;

internal enum StudentUiValidationFailureKind { DeadlineExceeded, Cancelled, Validation, IoFailure, AccessDenied, Unexpected }

internal static class StudentUiValidationFailureDiagnostics
{
    // This private offline journey includes startup, reference captures, real work-area checks and reopen.
    // Its fixed budget is independent of student monitoring/refresh settings.
    internal const int BudgetSeconds = 180;

    internal static StudentUiValidationFailureKind Classify(Exception error, bool deadlineCancelled, CancellationToken deadlineToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is OperationCanceledException cancellation)
            return deadlineCancelled && deadlineToken.CanBeCanceled && cancellation.CancellationToken == deadlineToken
                ? StudentUiValidationFailureKind.DeadlineExceeded : StudentUiValidationFailureKind.Cancelled;
        if (error is UnauthorizedAccessException) return StudentUiValidationFailureKind.AccessDenied;
        if (error is IOException) return StudentUiValidationFailureKind.IoFailure;
        if (error is InvalidOperationException && IsPrivateUiCode(error.Message)) return StudentUiValidationFailureKind.Validation;
        return StudentUiValidationFailureKind.Unexpected;
    }

    internal static bool IsPrivateUiCode(string value) => value is { Length: > 0 and <= 128 }
        && value.StartsWith("ui_", StringComparison.Ordinal)
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
}
