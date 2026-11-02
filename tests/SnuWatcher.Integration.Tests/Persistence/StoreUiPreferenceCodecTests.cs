using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using SnuWatcher.Application.Presentation.Preferences;
using Xunit;

namespace SnuWatcher.Integration.Tests.Persistence;

public sealed class StoreUiPreferenceCodecTests
{
    internal const string Legacy = "{\"version\":1,\"language\":\"en\",\"year\":null,\"term\":null,\"profile\":\"normal\",\"close\":\"tray\",\"onboarding\":0}";
    private static string VersionTwo(string sound) => Legacy.Replace("\"version\":1", "\"version\":2")[..^1] + ",\"soundMuted\":" + sound + "}";

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void VersionTwoRequiresAndReadsAnExplicitSoundBoolean(string json, bool expected)
    {
        var values = Invoke<UiPreferencesValues>("ReadValues", VersionTwo(json));
        Assert.Equal(expected, typeof(UiPreferencesValues).GetProperty("NotificationSoundMuted")!.GetValue(values));
    }

    [Fact]
    public void WritersKeepOuterVersionOneAndUseNestedValuesVersionTwo()
    {
        var command = new SaveUiPreferencesCommand(Guid.NewGuid(), 0, UiPreferencesValues.Defaults);
        using var values = JsonDocument.Parse(Invoke<string>("Values", command.Values));
        Assert.Equal(2, values.RootElement.GetProperty("version").GetInt32());
        Assert.False(values.RootElement.GetProperty("soundMuted").GetBoolean());
        using var facts = JsonDocument.Parse(Invoke<string>("CommandFacts", command));
        Assert.Equal(1, facts.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(2, facts.RootElement.GetProperty("values").GetProperty("version").GetInt32());
        var receipt = new UiPreferencesSaveReceipt(command.CommandId, 0, new(1, command.Values, DateTimeOffset.UnixEpoch));
        using var stored = JsonDocument.Parse(Invoke<string>("Receipt", receipt));
        Assert.Equal(1, stored.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(2, stored.RootElement.GetProperty("values").GetProperty("version").GetInt32());
    }

    [Fact]
    public void LegacyValuesDecodeAsUnmutedAndKeepEveryExistingField()
    {
        var values = Invoke<UiPreferencesValues>("ReadValues", Legacy);
        Assert.Equal(UiPreferencesValues.Defaults, values);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"true\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void VersionTwoNeverCoercesSoundMuteTypes(string value) =>
        Assert.ThrowsAny<Exception>(() => Invoke<UiPreferencesValues>("ReadValues", VersionTwo(value)));

    [Theory]
    [InlineData("{\"version\":2,\"language\":\"en\",\"year\":null,\"term\":null,\"profile\":\"normal\",\"close\":\"tray\",\"onboarding\":0}")]
    [InlineData("{\"version\":1,\"language\":\"en\",\"year\":null,\"term\":null,\"profile\":\"normal\",\"close\":\"tray\",\"onboarding\":0,\"soundMuted\":false}")]
    [InlineData("{\"version\":3,\"language\":\"en\",\"year\":null,\"term\":null,\"profile\":\"normal\",\"close\":\"tray\",\"onboarding\":0,\"soundMuted\":false}")]
    public void EachValuesVersionHasAnExactFieldSet(string json) =>
        Assert.ThrowsAny<Exception>(() => Invoke<UiPreferencesValues>("ReadValues", json));

    [Fact]
    public void DuplicateAndUnknownVersionTwoFieldsAreRejected()
    {
        foreach (var suffix in new[] { ",\"soundMuted\":false}", ",\"sound\\u004duted\":false}", ",\"extra\":true}" })
            Assert.ThrowsAny<Exception>(() => Invoke<UiPreferencesValues>("ReadValues", VersionTwo("true")[..^1] + suffix));
    }

    internal static T Invoke<T>(string method, object argument)
    {
        var wire = Assembly.Load("SnuWatcher.Infrastructure").GetType("SnuWatcher.Infrastructure.Persistence.StoreUiWire", true)!;
        try { return (T)wire.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [argument])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
