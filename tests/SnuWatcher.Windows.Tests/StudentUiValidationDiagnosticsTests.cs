using SnuWatcher.Desktop.Runtime;
using Xunit;

namespace SnuWatcher.Windows.Tests;

public sealed class StudentUiValidationDiagnosticsTests
{
    [Fact]
    public void WholeJourneyHasAFixedThreeMinuteBudget() =>
        Assert.Equal(180, StudentUiValidationFailureDiagnostics.BudgetSeconds);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyCancellationFromTheExpiredDeadlineIsADeadlineFailure(bool taskCancellation)
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();
        Exception error = taskCancellation
            ? new TaskCanceledException("private", null, deadline.Token)
            : new OperationCanceledException(deadline.Token);

        Assert.Equal(StudentUiValidationFailureKind.DeadlineExceeded,
            StudentUiValidationFailureDiagnostics.Classify(error, deadlineCancelled: true, deadline.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedCancellationIsNotRelabeledByAnExpiredDeadline(bool deadlineCancelled)
    {
        using var deadline = new CancellationTokenSource();
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        if (deadlineCancelled) deadline.Cancel();

        Assert.Equal(StudentUiValidationFailureKind.Cancelled,
            StudentUiValidationFailureDiagnostics.Classify(new OperationCanceledException(unrelated.Token),
                deadlineCancelled, deadline.Token));
    }

    [Fact]
    public void CancellationBeforeTheDeadlineIsNotADeadlineFailure()
    {
        using var deadline = new CancellationTokenSource();
        Assert.Equal(StudentUiValidationFailureKind.Cancelled,
            StudentUiValidationFailureDiagnostics.Classify(new OperationCanceledException(deadline.Token),
                deadlineCancelled: false, deadline.Token));
    }

    [Fact]
    public void TokenlessCancellationDoesNotProveTheDeadlineExpired() =>
        Assert.Equal(StudentUiValidationFailureKind.Cancelled,
            StudentUiValidationFailureDiagnostics.Classify(new OperationCanceledException(),
                deadlineCancelled: true, CancellationToken.None));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileFailureKeepsItsCategoryEvenWhenTheDeadlineExpired(bool deadlineCancelled)
    {
        using var deadline = new CancellationTokenSource();
        Assert.Equal(StudentUiValidationFailureKind.IoFailure,
            StudentUiValidationFailureDiagnostics.Classify(new IOException("a private path"),
                deadlineCancelled, deadline.Token));
        Assert.Equal(StudentUiValidationFailureKind.AccessDenied,
            StudentUiValidationFailureDiagnostics.Classify(new UnauthorizedAccessException("a private path"),
                deadlineCancelled, deadline.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitValidationFailureKeepsItsCategoryEvenWhenTheDeadlineExpired(bool deadlineCancelled)
    {
        Assert.Equal(StudentUiValidationFailureKind.Validation,
            StudentUiValidationFailureDiagnostics.Classify(new InvalidOperationException("ui_control_unreachable"),
                deadlineCancelled, CancellationToken.None));
        Assert.Equal(StudentUiValidationFailureKind.Unexpected,
            StudentUiValidationFailureDiagnostics.Classify(new InvalidOperationException("a private path"),
                deadlineCancelled, CancellationToken.None));
        Assert.Equal(StudentUiValidationFailureKind.Unexpected,
            StudentUiValidationFailureDiagnostics.Classify(new ArgumentException("a private value"),
                deadlineCancelled, CancellationToken.None));
    }

    [Theory]
    [InlineData("ui_control_unreachable", true)]
    [InlineData("ui_", true)]
    [InlineData("ui_A1.b-c", true)]
    [InlineData("", false)]
    [InlineData("UI_control", false)]
    [InlineData("ui_서울", false)]
    [InlineData("ui_value/path", false)]
    [InlineData("ui_value\\path", false)]
    [InlineData("ui_value\npath", false)]
    [InlineData("ui_value path", false)]
    public void OnlyTheExistingBoundedAsciiPrivateCodeFormatIsAccepted(string value, bool expected) =>
        Assert.Equal(expected, StudentUiValidationFailureDiagnostics.IsPrivateUiCode(value));

    [Fact]
    public void CodeLengthBoundaryIsPreserved()
    {
        Assert.True(StudentUiValidationFailureDiagnostics.IsPrivateUiCode("ui_" + new string('x', 125)));
        Assert.False(StudentUiValidationFailureDiagnostics.IsPrivateUiCode("ui_" + new string('x', 126)));
    }

    [Fact]
    public void MissingPrivateCodeIsRejected() =>
        Assert.False(StudentUiValidationFailureDiagnostics.IsPrivateUiCode(null!));

    [Fact]
    public void MissingExceptionIsRejected() =>
        Assert.Throws<ArgumentNullException>(() => StudentUiValidationFailureDiagnostics.Classify(null!,
            deadlineCancelled: false, CancellationToken.None));
}
