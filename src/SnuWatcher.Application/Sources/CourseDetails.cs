using System.Collections.Immutable;
using SnuWatcher.Core;

namespace SnuWatcher.Application.Sources;

public sealed record CourseMeeting
{
    public CourseMeeting(string time, string? type, string? room)
    {
        Time = SourceValidation.Display(time, SourceLimits.MaxLongDisplay, nameof(time));
        Type = SourceValidation.OptionalDisplay(type, SourceLimits.MaxShortDisplay, nameof(type));
        Room = SourceValidation.OptionalDisplay(room, SourceLimits.MaxLongDisplay, nameof(room));
    }

    public string Time { get; }
    public string? Type { get; }
    public string? Room { get; }
}

public sealed record CourseDetails
{
    public CourseDetails(SectionKey key, string name, string? englishName, string? instructor, string? englishInstructor,
        string? department, decimal? credits, int? capacity, string? instructionLanguage, string? restrictions,
        IEnumerable<CourseMeeting> meetings)
    {
        ArgumentNullException.ThrowIfNull(key);
        Key = key;
        Name = SourceValidation.Display(name, SourceLimits.MaxShortDisplay, nameof(name), requireText: true);
        EnglishName = SourceValidation.OptionalDisplay(englishName, SourceLimits.MaxShortDisplay, nameof(englishName));
        Instructor = SourceValidation.OptionalDisplay(instructor, SourceLimits.MaxShortDisplay, nameof(instructor));
        EnglishInstructor = SourceValidation.OptionalDisplay(englishInstructor, SourceLimits.MaxShortDisplay, nameof(englishInstructor));
        Department = SourceValidation.OptionalDisplay(department, SourceLimits.MaxShortDisplay, nameof(department));
        if (credits < 0) throw new ArgumentOutOfRangeException(nameof(credits));
        Credits = credits;
        Capacity = SourceValidation.Count(capacity, nameof(capacity));
        InstructionLanguage = SourceValidation.OptionalDisplay(instructionLanguage, SourceLimits.MaxShortDisplay, nameof(instructionLanguage));
        Restrictions = SourceValidation.OptionalDisplay(restrictions, SourceLimits.MaxRestrictions, nameof(restrictions));
        Meetings = SourceValidation.Snapshot(meetings, SourceLimits.MaxMeetings, nameof(meetings));
    }

    public SectionKey Key { get; }
    public string Name { get; }
    public string? EnglishName { get; }
    public string? Instructor { get; }
    public string? EnglishInstructor { get; }
    public string? Department { get; }
    public decimal? Credits { get; }
    public int? Capacity { get; }
    public string? InstructionLanguage { get; }
    public string? Restrictions { get; }
    public ImmutableArray<CourseMeeting> Meetings { get; }
}
