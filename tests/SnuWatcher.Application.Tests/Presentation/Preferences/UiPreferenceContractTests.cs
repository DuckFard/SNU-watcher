using System.Reflection;
using SnuWatcher.Application.Monitoring.Scheduling;
using SnuWatcher.Application.Presentation.Preferences;
using Xunit;

namespace SnuWatcher.Application.Tests.Presentation.Preferences;

public sealed class UiPreferenceContractTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T01:00:00Z");
    private static readonly Guid Id = Guid.NewGuid();

    [Fact]
    public void DefaultsAreStableEnglishAndDoNotImplicitlySelectAnAcademicTerm()
    {
        var values = UiPreferencesValues.Defaults;
        Assert.Equal(UiLanguage.English, values.Language);
        Assert.Null(values.SelectedYear);
        Assert.Null(values.TermHint);
        Assert.Equal(WatchMonitorProfile.Normal, values.DesiredProfile);
        Assert.Equal(UiCloseBehavior.MinimizeToTray, values.CloseBehavior);
        Assert.Equal(0, values.OnboardingVersion);
        var initial = new UiPreferencesSnapshot(0, values, null);
        Assert.Equal(0, initial.Revision);
        Assert.Null(initial.SavedAtUtc);
        Assert.Same(values, initial.Values);
        Assert.Throws<ArgumentException>(() => new UiPreferencesSnapshot(0, Korean(), null));
        Assert.Throws<ArgumentException>(() => new UiPreferencesSnapshot(0, values, Now));
        Assert.Throws<ArgumentException>(() => new UiPreferencesSnapshot(1, values, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiPreferencesSnapshot(-1, values, null));
        Assert.Throws<ArgumentNullException>(() => new UiPreferencesSnapshot(0, null!, null));
    }

    [Fact]
    public void TermHintsAreOpaqueUnconfirmedPreferencesWithAnExplicitMatchingYear()
    {
        var hint = new AcademicTermHint(2026, "U000200002.U000300002");
        var values = Korean(hint);
        Assert.Equal(UiLanguage.Korean, values.Language);
        Assert.Equal(2026, values.SelectedYear);
        Assert.Same(hint, values.TermHint);
        Assert.Equal(2026, hint.Year);
        Assert.Equal("U000200002.U000300002", hint.Selector);
        Assert.Equal(WatchMonitorProfile.Faster, values.DesiredProfile);
        Assert.Equal(UiCloseBehavior.Quit, values.CloseBehavior);
        Assert.Equal(10000, values.OnboardingVersion);
        Assert.NotEqual(UiPreferencesValues.Defaults, values);
        Assert.Throws<ArgumentException>(() => new UiPreferencesValues(UiLanguage.English, null, hint, WatchMonitorProfile.Normal, UiCloseBehavior.Quit, 0));
        Assert.Throws<ArgumentException>(() => new UiPreferencesValues(UiLanguage.English, 2025, hint, WatchMonitorProfile.Normal, UiCloseBehavior.Quit, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AcademicTermHint(2012, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AcademicTermHint(2201, "x"));
        Assert.Throws<ArgumentException>(() => new AcademicTermHint(2026, "x\n"));
        Assert.Throws<ArgumentException>(() => new AcademicTermHint(2026, new string('x', 81)));
        Assert.Throws<ArgumentException>(() => new AcademicTermHint(2026, ""));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public void PreferenceBoundsCannotHideUnsupportedSettings(int onboarding) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiPreferencesValues(UiLanguage.English, null, null, WatchMonitorProfile.Normal, UiCloseBehavior.MinimizeToTray, onboarding));

    [Fact]
    public void EnumAndYearSettingsAreValidatedAtTheBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiPreferencesValues((UiLanguage)99, null, null, WatchMonitorProfile.Normal, UiCloseBehavior.Quit, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiPreferencesValues(UiLanguage.English, 2000, null, WatchMonitorProfile.Normal, UiCloseBehavior.Quit, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiPreferencesValues(UiLanguage.English, null, null, (WatchMonitorProfile)99, UiCloseBehavior.Quit, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiPreferencesValues(UiLanguage.English, null, null, WatchMonitorProfile.Normal, (UiCloseBehavior)99, 0));
    }

    [Fact]
    public void AnAcceptedReceiptBindsCommandAndOneRevisionWhileReplayMayExposeANewerCurrentSave()
    {
        var command = new SaveUiPreferencesCommand(Id, 0, Korean());
        var saved = new UiPreferencesSnapshot(1, command.Values, Now.ToOffset(TimeSpan.FromHours(9)));
        var receipt = new UiPreferencesSaveReceipt(Id, 0, saved);
        var applied = new UiPreferencesSaveResult(UiPreferencesSaveStatus.Applied, receipt, saved);
        Assert.Equal(Id, command.CommandId);
        Assert.Equal(0, command.ExpectedRevision);
        Assert.Equal(Id, receipt.CommandId);
        Assert.Equal(0, receipt.PreviousRevision);
        Assert.Same(saved, receipt.SavedSnapshot);
        Assert.Equal(TimeSpan.Zero, saved.SavedAtUtc!.Value.Offset);
        Assert.Equal(UiPreferencesSaveStatus.Applied, applied.Status);
        Assert.Same(receipt, applied.Receipt);
        Assert.Same(saved, applied.CurrentSnapshot);
        var newer = new UiPreferencesSnapshot(2, UiPreferencesValues.Defaults, Now.AddMinutes(1));
        var replayed = new UiPreferencesSaveResult(UiPreferencesSaveStatus.Replayed, receipt, newer);
        Assert.Same(newer, replayed.CurrentSnapshot);
        Assert.Same(saved, replayed.Receipt!.SavedSnapshot);
        Assert.Null(new UiPreferencesSaveResult(UiPreferencesSaveStatus.VersionConflict, null, newer).Receipt);
        Assert.Throws<ArgumentException>(() => new UiPreferencesSaveReceipt(Id, 1, saved));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiPreferencesSaveReceipt(Id, long.MaxValue, saved));
        Assert.Throws<ArgumentException>(() => new UiPreferencesSaveResult(UiPreferencesSaveStatus.Applied, receipt, newer));
        Assert.Throws<ArgumentException>(() => new UiPreferencesSaveResult(UiPreferencesSaveStatus.Replayed, receipt, new(0, UiPreferencesValues.Defaults, null)));
        Assert.Throws<ArgumentException>(() => new UiPreferencesSaveResult(UiPreferencesSaveStatus.Replayed, receipt, new(1, UiPreferencesValues.Defaults, Now)));
        Assert.Throws<ArgumentException>(() => new UiPreferencesSaveResult(UiPreferencesSaveStatus.VersionConflict, receipt, newer));
        Assert.Throws<ArgumentException>(() => new UiPreferencesSaveResult(UiPreferencesSaveStatus.Applied, null, newer));
    }

    [Fact]
    public void ConflictingReplayHasItsOwnSafeExceptionAndCommandsCannotCarryEmptyOrNegativeIdentity()
    {
        var conflict = new UiPreferencesReplayConflictException();
        Assert.IsAssignableFrom<InvalidOperationException>(conflict);
        Assert.DoesNotContain(Id.ToString(), conflict.Message);
        Assert.Throws<ArgumentException>(() => new SaveUiPreferencesCommand(Guid.Empty, 0, UiPreferencesValues.Defaults));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaveUiPreferencesCommand(Id, -1, UiPreferencesValues.Defaults));
        Assert.Throws<ArgumentNullException>(() => new SaveUiPreferencesCommand(Id, 0, null!));
        Assert.Throws<ArgumentException>(() => new UiPreferencesSaveReceipt(Guid.Empty, 0, new(1, UiPreferencesValues.Defaults, Now)));
        Assert.Throws<ArgumentNullException>(() => new UiPreferencesSaveReceipt(Id, 0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiPreferencesSaveResult((UiPreferencesSaveStatus)99, null, new(0, UiPreferencesValues.Defaults, null)));
        Assert.Throws<ArgumentNullException>(() => new UiPreferencesSaveResult(UiPreferencesSaveStatus.VersionConflict, null, null!));
    }

    [Fact]
    public void PreferenceSnapshotsHaveNoPublicSetters()
    {
        Type[] types = [typeof(AcademicTermHint), typeof(UiPreferencesValues), typeof(UiPreferencesSnapshot),
            typeof(SaveUiPreferencesCommand), typeof(UiPreferencesSaveReceipt), typeof(UiPreferencesSaveResult)];
        Assert.All(types, type => Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            property => Assert.Null(property.SetMethod)));
    }

    private static UiPreferencesValues Korean(AcademicTermHint? hint = null) =>
        new(UiLanguage.Korean, 2026, hint, WatchMonitorProfile.Faster, UiCloseBehavior.Quit, 10000);
}
