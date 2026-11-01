using System;
using Tutan.Functional;
using UnityEngine;

namespace Tutan.Logging.Tests
{
    [Flags]
    public enum Doors
    {
        None = 0,
        Front = 1,
        Back = 2,
        Side = 4,
    }

    // The categories are the process's, as every category is, so each starts with tests. to stay
    // clear of the game's.

    [LogCategory("tests.save")]
    public static partial class SaveLog
    {
        [LogScope(LogLevel.Information, "Saving slot {Slot}")]
        static partial void Saving(int slot);

        [LogEvent(LogLevel.Information, "Saved slot {Slot} as {File}")]
        static partial void Saved(int slot, string file);

        [LogEvent(LogLevel.Debug, "Wrote {Bytes} bytes")]
        static partial void Wrote(long bytes);

        [LogEvent(LogLevel.Trace, "Step {Step}")]
        static partial void Step(int step);

        [LogEvent(LogLevel.Error, "Saving slot {Slot} failed")]
        static partial void Failed(int slot, Exception error);
    }

    [LogCategory("tests.fields")]
    public static partial class FieldLog
    {
        [LogEvent(LogLevel.Information, "{Flag} {Letter} {Tiny} {Small} {Short} {Word} {Int} {Count} {Long} {Huge}")]
        static partial void Numbers(bool flag, char letter, sbyte tiny, byte small, short @short, ushort word, int @int,
            uint count, long @long, ulong huge);

        [LogEvent(LogLevel.Information, "{Single} {Double} {Money}")]
        static partial void Reals(float single, double @double, decimal money);

        [LogEvent(LogLevel.Information, "{Id} {When} {At} {Took}")]
        static partial void Times(Guid id, DateTime when, DateTimeOffset at, TimeSpan took);

        [LogEvent(LogLevel.Information, "{Doors} {Text} {Chars} {Where}")]
        static partial void Things(Doors doors, string text, ReadOnlySpan<char> chars, Vector3 where);

        [LogEvent(LogLevel.Information, "{Maybe} {Count} {Note} {Where}")]
        static partial void Optionals(Optional<int> maybe, int? count, Optional<string> note, Optional<Vector3> where);

        [LogEvent(LogLevel.Information, "[{Left,-6}] [{Right,6}] {Ratio:0.00} {Doors:D}")]
        static partial void Aligned(string left, int right, double ratio, Doors doors);

        [LogEvent(LogLevel.Information, "{Text}")]
        static partial void Said(string? text);
    }

    [LogCategory("tests.people")]
    public static partial class PeopleLog
    {
        [LogEvent(LogLevel.Information, "Signed in as {Email} from {Folder}")]
        static partial void SignedIn([Personal] string email, [LogTag("path")] string folder);

        [LogEvent(LogLevel.Error, "Lost {Email}")]
        static partial void Lost([Personal] string email, [Personal] Exception error);
    }

    [LogCategory("tests.heard")]
    public static partial class HeardLog
    {
        [LogEvent("Heard {Message}")]
        static partial void Heard(LogLevel level, string message);
    }

    [LogCategory("tests.limits")]
    public static partial class LimitLog
    {
        [LogEvent(LogLevel.Information, "Hit {Count}", PerSecond = 2)]
        static partial void Hit(int count);

        [LogEvent(LogLevel.Information, "Struck", Name = "Struck")]
        static partial void Strike();
    }
}
