# API Reference

Every public type of `Tutan.Logging`, in namespace `Tutan.Logging`.
Members marked *generator* are what generated code calls; they are hidden from IntelliSense and not meant to be called by hand.

## Declaring

### `LogCategoryAttribute`

| Member | Description |
|---|---|
| `LogCategoryAttribute(string name)` | Names the category of every event the class declares |
| `string Name` | The category, as `game.save` |

### `LogEventAttribute`

| Member | Description |
|---|---|
| `LogEventAttribute(LogLevel level, string template)` | An event at a level of its own |
| `LogEventAttribute(string template)` | An event whose level a `LogLevel` parameter chooses at each call |
| `LogLevel? Level` | The level, or null when a parameter chooses it |
| `string Template` | The message, with its holes |
| `string? Name { init; }` | The name in the key, when it is not the method's |
| `int PerSecond { init; }` | How many records a second a store keeps, at most; 0 for no limit |

### `LogScopeAttribute`

| Member | Description |
|---|---|
| `LogScopeAttribute(LogLevel level, string template)` | A scope at a level of its own |
| `LogLevel Level` | The level of the records that open and close it |
| `string Template` | The message, with its holes |
| `string? Name { init; }` | The name in the key, when it is not the method's |

### `PersonalAttribute`

Marks a parameter whose field can name a person, which a sink can redact.

### `LogTagAttribute`

| Member | Description |
|---|---|
| `LogTagAttribute(string tag)` | Tags the field |
| `string Tag` | What the field is, for a reader that offers to act on it |

### `LogLevel`

`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, and `None`, a threshold nothing passes.

### `LogEventKind`

`Event`, something that happened, or `Scope`, an operation with a beginning and an end.

## Writing

### `Log` (readonly struct)

| Member | Description |
|---|---|
| `static Log Discard` | A log that captures nothing |
| `bool IsHanded` | Whether it was handed a store; false for `default` |
| `long Scope` | The scope it writes under, or 0 |
| `bool IsEnabled(LogEventSpec spec)` | Whether the event is on at its own level |
| `bool IsEnabled(LogEventSpec spec, LogLevel level)` | Whether the event is on at a level chosen at the call |
| `LogWriter Write(LogEventSpec, LogLevel, int chars, string callerFile, int callerLine)` | *Generator:* reserves an event's record |
| `LogWriter BeginScope(LogEventSpec, LogLevel, int chars, string callerFile, int callerLine)` | *Generator:* reserves a scope's opening |
| `Equals`, `==`, `!=`, `GetHashCode` | Equal when both are handles to one store and one scope |
| `ToString()` | The store's program and the scope, for a debugger |

### `LogScope` (readonly struct, `IDisposable`)

| Member | Description |
|---|---|
| `Log Log` | The log whose records are under the scope, or the one it was opened from when it is off |
| `long Id` | The sequence of the record that opened it; 0 when off |
| `bool IsOn` | Whether it was opened |
| `void Dispose()` | Closes it, the first time any copy is disposed |
| `static LogScope Off(Log from)` | *Generator:* a scope that is off |
| `ToString()` | The scope's id, for a debugger |

### `LogWriter` (ref struct)

*Generator:* fills a reserved record with the lock held.
`const int MaxChars` is 1024, the most chars a `ReadOnlySpan<char>` field keeps.
`static int CharSlots(ReadOnlySpan<char>)` is the slots a span takes.
`Value<T>`, `Presence`, `Ref`, `Chars` write a field, `Scope` is the scope a scope's opening opened, and `Commit` ends the record.

## The store

### `LogStore`

| Member | Description |
|---|---|
| `LogStore(LogRules rules, LogStoreOptions? options = null)` | A store capturing what the rules say, with rings of the given sizes |
| `const int MinRecords`, `MinBits`, `MinRefs` | The least size of each ring: 16, 32,768 and 64 |
| `Log Log` | The handle to pass to what logs, under no scope |
| `string Program` | The program's name |
| `LogClock Clock` | When the store started |
| `LogRules Rules` | The rules applied last |
| `int Capacity` | How many records it holds, a power of two |
| `long Frame` | The frame records written now are stamped with |
| `long First` | The oldest sequence it still holds |
| `long Next` | The sequence the next record gets |
| `void Apply(LogRules rules)` | Captures what the rules say from now on, from any thread |
| `void EnterFrame(long frame)` | Stamps records written from now on with the frame |
| `LogRead Read(long from, LogBatch into)` | Copies the records from a sequence on, as many as fit |
| `int Read(ReadOnlySpan<long> sequences, LogBatch into)` | Copies the records of the given sequences it still holds |

### `LogStoreOptions` (record)

`LogStoreOptions(int Records = 16_384, int Bits = 65_536, int Refs = 16_384, string? Program = null)`: the ring sizes, each rounded up to a power of two, and the program's name, null for `Application.productName`.

### `LogRead` (readonly record struct)

`LogRead(long First, int Count, long Next, long Lost)`: the first sequence copied, how many, where to read from next, and how many were overwritten before they were read.

### `LogClock` (readonly record struct)

| Member | Description |
|---|---|
| `LogClock(DateTimeOffset Started, long StartedAt)` | The wall clock and the `Stopwatch` timestamp when the store started |
| `DateTimeOffset At(long timestamp)` | When a timestamp was, on the wall clock |
| `static TimeSpan Between(long from, long to)` | How long it was between two timestamps |

### `LogBatch`

| Member | Description |
|---|---|
| `LogBatch(int records = 1024)` | A batch with room for that many records |
| `int Count` | How many records the last read copied |
| `LogRecord this[int index]` | A record, valid until the next read into the batch |

## Rules

### `LogRules`

| Member | Description |
|---|---|
| `LogRules(LogLevel least, params LogRule[] rules)` | Every event held to `least`, but where the rules say otherwise |
| `static LogRules Default` | `debug`, and no rules |
| `LogLevel Least` | The least level where no rule says otherwise |
| `IReadOnlyList<LogRule> Rules` | The rules, in order |
| `static Result<LogRules> Parse(string text)` | Rules from their text, failing with a `LogRulesError` code |
| `LogLevel LeastFor(LogEventSpec spec)` | The least level an event is captured at; `None` when off |
| `int PerSecondFor(LogEventSpec spec)` | An event's limit, from the most specific rule or its own; 0 for none |
| `IReadOnlyList<LogRule> MatchingNothing(IReadOnlyList<LogEventSpec> specs)` | The rules whose patterns match none of the specs |
| `static string Name(LogLevel level)` | A level's name as rules spell it |
| `static bool TryLevel(string text, out LogLevel level)` | A level from its name, ignoring case |
| `ToString()` | The text `Parse` reads back as these rules |
| `Equals`, `GetHashCode` | Equal when the least and every rule, in order, are |

### `LogRule` (record)

`LogRule(string Pattern, Optional<LogLevel> Least, Optional<int> PerSecond)`: for the events a pattern names, a least level (`None` for off), a limit, or both.
`ToString()` writes it as `Parse` reads it.

### `LogRulesError`

`NotALevel`, `NotAPattern`, `NotALimit`, `LeastTwice`: the `Error.Code` of a failed `Parse`.

## Records

### `LogRecord` (readonly ref struct)

| Member | Description |
|---|---|
| `long Sequence` | Its sequence, from 1 in each store |
| `long Timestamp` | When it was written, as a `Stopwatch` timestamp |
| `long Frame` | The frame it was written in, or 0 |
| `long Scope` | The scope it was written under, or 0 |
| `long Begin` | The scope it opens or closes, or 0 for an event |
| `TimeSpan Elapsed` | For a scope's closing, how long the scope was open |
| `int Thread` | The managed id of the thread that wrote it |
| `LogLevel Level` | Its level |
| `LogRecordKind Kind` | An event, or a scope's opening or closing |
| `int Suppressed` | How many records of its event a limit dropped before it |
| `LogCallSite CallSite` | The call that wrote it |
| `LogEventSpec Event` | Its event or scope |
| `int FieldCount` | How many fields it has |
| `LogValue Field(int index)` | A field, in the order of the spec's |
| `bool TryField(string name, out LogValue value)` | A field by name, ignoring case |
| `bool TryFormat(Span<char> destination, out int written, LogRedaction redact = None)` | Writes its message |
| `ToString()` | Its message, allocated |

### `LogRecordKind`

`Event`, `ScopeBegin`, `ScopeEnd`.

### `LogCallSite` (readonly struct)

| Member | Description |
|---|---|
| `LogCallSite(string? file, int line)` | A call site |
| `string? File` | The file as the compiler wrote it, or null |
| `int Line` | The line from 1, or 0 |
| `bool IsKnown` | Whether the record has a call site |
| `ToString()` | `File:Line`, or empty |

### `LogValue` (readonly ref struct)

| Member | Description |
|---|---|
| `LogField Field` | The field it is a value of |
| `LogValueKind Kind` | How it is held |
| `bool IsNone` | An optional value that was none, or a reference that was null |
| `T Read<T>() where T : unmanaged` | The value as the type it was written as |
| `long AsInt64()` | A whole number, bool, char or enum as a `long` |
| `double AsDouble()` | Any number as a `double` |
| `ReadOnlySpan<char> AsChars()` | A string's chars, or chars copied at the call |
| `object? AsRef()` | The string or exception it was kept by |
| `bool TryFormat(Span<char> destination, out int written, string? format = null)` | Writes it in the invariant culture |
| `ToString()` | It formatted, allocated |

### `LogValueKind`

`Boolean`, `Char`, `SByte`, `Byte`, `Int16`, `UInt16`, `Int32`, `UInt32`, `Int64`, `UInt64`, `Single`, `Double`, `Decimal`, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `Enum`, `String`, `Exception`, `Chars`, `Formattable`.

## Specs and the catalog

### `LogEventSpec`

| Member | Description |
|---|---|
| `string Category` | The category |
| `string Name` | The name in the key |
| `string Key` | `category/Name` |
| `LogEventKind Kind` | An event or a scope |
| `Optional<LogLevel> Level` | Its level, or none when each call chooses |
| `LogTemplate Template` | The message, taken apart |
| `IReadOnlyList<LogField> Fields` | The fields, in the order the call writes them |
| `int PerSecond` | The limit it declares, or 0 |
| `LogSource Source` | Where it is declared |
| `int Index` | Its place in the catalog, which is what a record stores |
| `static LogEventSpec Declare(...)` | *Generator:* a spec, checked |
| `ToString()` | The key |

### `LogSource` (record)

`LogSource(string Assembly, string Type, string File, int Line)`: where an event is declared.
`ToString()` is `File:Line`.

### `LogTemplate`

| Member | Description |
|---|---|
| `string Text` | The template as declared |
| `IReadOnlyList<string> Literals` | The text around the holes, one more than there are holes |
| `IReadOnlyList<LogHole> Holes` | The holes, in order |

### `LogHole` (readonly record struct)

`LogHole(int Field, int Alignment, string? Format)`: the field a hole writes, its padding, and its format.

### `LogField`

| Member | Description |
|---|---|
| `const int MaxValueBytes` | 256, the most bytes a value of the program's own type may have |
| `string Name` | The field's name |
| `LogValueKind Kind` | How its value is held |
| `string Type` | The type the parameter was declared as |
| `Optional<string> Tag` | Its `[LogTag]` |
| `bool Personal` | Whether it is marked `[Personal]` |
| `bool IsOptional` | Whether it can be none |
| `int Slots` | How many 8-byte slots its value takes |
| `bool IsRef` | Whether its value is kept by reference |
| `Value<T>`, `Enum`, `Ref`, `Chars`, `Formattable<T>` | *Generator:* make a field of each kind |

### `LogCatalog` (static)

| Member | Description |
|---|---|
| `IReadOnlyList<LogEventSpec> Events` | Every spec registered, in index order |
| `int Count` | How many are registered |
| `IReadOnlyList<LogCatalogConflict> Conflicts` | What two assemblies declared alike |
| `Optional<LogEventSpec> Find(string key)` | The spec with a key, the first registered |
| `void Register(params LogEventSpec[] specs)` | *Generator:* registers specs, idempotently |

### `LogCatalogConflict` (abstract record)

| Member | Description |
|---|---|
| `KeyTwice(string Key, LogSource First, LogSource Second)` | Two events with one key |
| `CategoryTwice(string Category, LogSource First, LogSource Second)` | One category declared by two classes |

## Draining

### `LogDrain` (`IDisposable`)

| Member | Description |
|---|---|
| `LogDrain(LogStore store, params (ILogSink Sink, LogSinkOptions Options)[] sinks)` | A drain of a store into its sinks |
| `LogStore Store` | The store it reads |
| `void Pump()` | Hands what arrived since the last read to the sinks, and flushes them |
| `void Flush()` | Everything the store holds, to the sinks, now; harmless once disposed |
| `void Dispose()` | Drains what is left and disposes the sinks |

### `ILogSink`

| Member | Description |
|---|---|
| `string Name` | What the log calls the sink |
| `void Open(LogContext context)` | Called once, before anything else |
| `void Write(LogRecord record, bool backlog, LogContext context)` | Writes one record; `backlog` when written as an error's context |
| `void Lost(long count, LogContext context)` | Says records were overwritten before the drain read them |
| `void Flush()` | Makes what was written durable |

### `LogSinkOptions` (record)

`LogSinkOptions(LogLevel Least = Information, int Backlog = 0, LogRedaction Redact = None)`: the least level a sink writes, how many records below it it writes before an error, and what it leaves out.

### `LogRedaction` (flags)

`None`, `Personal` (fields marked `[Personal]` as `[personal]`), `HomeFolder` (the user's home folder as `~`).

### `LogContext`

| Member | Description |
|---|---|
| `LogClock Clock` | The store's clock |
| `string Program` | The program's name |
| `LogSinkOptions Options` | The sink's options |
| `LogRedaction Redact` | What the sink leaves out |
| `bool TryScope(long scope, out LogRecord begin)` | The record that opened a scope still open |
| `bool TryFormat(LogRecord record, Span<char> destination, out int written)` | A record's message with the sink's redaction |
| `string Text(LogRecord record)` | The same, as a string |

## Sinks

### `UnityConsoleSink` (`ILogSink`)

| Member | Description |
|---|---|
| `UnityConsoleSink(bool openOnDoubleClick = true, string name = "unity console")` | A sink that writes to Unity's Console |
| `static LogType TypeOf(LogLevel level)` | The row type of a level |

### `JsonLinesSink` (`ILogSink`, `IDisposable`)

| Member | Description |
|---|---|
| `JsonLinesSink(Stream stream, bool leaveOpen = false, string name = "json")` | A sink that writes JSON lines to a stream |
| `const int Format` | The format's version, 1 |
| `void Dispose()` | Flushes, and disposes the stream unless left open |

### `LogFile` (static)

| Member | Description |
|---|---|
| `const string Extension` | `.jsonl` |
| `static Result<JsonLinesSink> Session(LogStore store, int keep = 10)` | A file for this session in `Application.persistentDataPath/Logs` |
| `static Result<JsonLinesSink> Session(LogStore store, string folder, int keep = 10)` | A file for this session in a folder, keeping the program's latest `keep` |

### `LogFileError`

`NoFolder`, `NoFile`: the `Error.Code` of a failed `Session`.

### `MemoryLog` (`IDisposable`)

| Member | Description |
|---|---|
| `MemoryLog(LogRules? rules = null)` | A log capturing what the rules say, or every level |
| `LogStore Store` | The store it writes into |
| `Log Log` | The handle to pass to the code under test |
| `IReadOnlyList<MemoryRecord> Records` | Every record so far |
| `IReadOnlyList<MemoryRecord> Events` | The events so far, without scopes' openings and closings |

### `MemorySink` (`ILogSink`)

| Member | Description |
|---|---|
| `MemorySink(string name = "memory")` | A sink that keeps what it is given |
| `IReadOnlyList<MemoryRecord> Records` | Every record written so far |
| `long Overwritten` | How many records it was told were lost |
| `int Flushes` | How many times it was flushed |

### `MemoryRecord` (record)

`MemoryRecord(long Sequence, long Frame, long Scope, long Begin, int Thread, LogLevel Level, LogRecordKind Kind, LogEventSpec Event, int Suppressed, bool Backlog, TimeSpan Elapsed, LogCallSite CallSite, string Message, IReadOnlyList<MemoryField> Fields)`.
`object? Field(string name)` is a field's value by name, ignoring case, and throws `KeyNotFoundException` when there is none.

### `MemoryField` (record)

`MemoryField(string Name, object? Value, string Text)`: a field's name, its value boxed, and its value formatted.

## Unity

### `LogPump` (static)

`static IDisposable Install(LogDrain drain)`: installs the frame stamp and the drain pump into the PlayerLoop, until what it returns is disposed.

### `UnityLogBridge` (static)

`static IDisposable Attach(Log log)`: writes Unity's log into the log, until what it returns is disposed.

### `UnityLog` (static)

| Member | Description |
|---|---|
| `Events.Logged` | The spec of `unity/Logged`, with the fields `Message`, `StackTrace` and `Type` |
| `static LogLevel LevelOf(LogType type)` | The level a message of a Unity log type is captured at |
