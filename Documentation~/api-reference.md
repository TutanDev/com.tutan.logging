# API Reference

Every public type of `Tutan.Logging`, in namespace `Tutan.Logging`.
Members marked *generator* are what generated code calls; they are hidden from IntelliSense and not meant to be called by hand.
The log console, in the editor-only assembly `Tutan.Logging.Editor`, has no public API: [The Log Console](log-console.md) says how to use it.

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
| `bool IsEnabled(LogEventSpec spec)` | Whether the event is on at its own level; never for an event a file declares |
| `bool IsEnabled(LogEventSpec spec, LogLevel level)` | Whether the event is on at a level chosen at the call |
| `LogWriter Write(LogEventSpec, LogLevel, int chars, string callerFile, int callerLine)` | *Generator:* reserves an event's record |
| `LogWriter BeginScope(LogEventSpec, LogLevel, int chars, string callerFile, int callerLine)` | *Generator:* reserves a scope's opening |
| `Equals`, `==`, `!=`, `GetHashCode` | Equal when both are handles to one store and one scope |
| `ToString()` | The store's program and the scope, for a debugger |

`Write` and `BeginScope` throw for an event of the other kind, a level other than the event's own or past `Critical`, and an event a file declares.

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

### `LogStore` (`LogRecords`)

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
| `IReadOnlyList<LogEventSpec> Events` | The catalog's events |
| `LogRedaction Redacted` | `None`: a store's records lack nothing |
| `LogLateMark LateMark` | `default`: a store holds its records in the order they were written |
| `LogLate LateFrom(LogLateMark seen)` | `Next`, with the `default` mark |
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
| `bool IsBacklog` | Whether a sink was handed it as an error's backlog; never for a store's |
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
| `string ToText(LogRedaction redact)` | It as a sink that keeps strings writes it, leaving out `redact`: an exception's whole text, where a message holds its type and message; allocated |
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
| `int Index` | Its place in the catalog, which is what a record stores: -1 until it is registered, and always for an event a file declares |
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
| `void Register(params LogEventSpec[] specs)` | *Generator:* registers specs, idempotently, and all or none: it throws for a null spec or an event a file declares before it registers any |

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
| `LogRedaction Redact` | What the sink leaves out: its options' and what the records already lack |
| `bool TryScope(long scope, out LogRecord begin)` | The record that opened a scope still open |
| `bool TryFormat(LogRecord record, Span<char> destination, out int written)` | A record's message with the sink's redaction |
| `string Text(LogRecord record)` | The same, as a string |
| `string Text(LogValue value)` | A field's value as a string, with the sink's redaction: an exception's whole text |

## Sinks

### `UnityConsoleSink` (`ILogSink`)

| Member | Description |
|---|---|
| `UnityConsoleSink(bool openOnDoubleClick = true, string name = "unity console", bool callSiteLine = true)` | A sink that writes to Unity's Console; `callSiteLine: false` leaves out the second line, the call site and frame |
| `static LogType TypeOf(LogLevel level)` | The row type of a level |

### `JsonLinesSink` (`ILogSink`, `IDisposable`)

| Member | Description |
|---|---|
| `JsonLinesSink(Stream stream, bool leaveOpen = false, string name = "json")` | A sink that writes JSON lines to a stream |
| `const int Format` | The format's version, 1 |
| `void Dispose()` | Flushes, and disposes the stream unless left open |

### `LogConsoleSink` (`ILogSink`, `IDisposable`)

| Member | Description |
|---|---|
| `LogConsoleSink(int cap = DefaultCap, string name = "log console")` | A sink that keeps a session of the log console, of at most `cap` records; a `cap` below 1 throws `ArgumentOutOfRangeException` |
| `const int DefaultCap` | 1,000,000 |
| `int Cap` | The most records its session holds |
| `void Open(LogContext context)` | Starts its session, in the editor only; opening it again throws `InvalidOperationException`, and options that redact throw `ArgumentException` |
| `void Dispose()` | Ends its session, which stays until the console is cleared; opening it after throws `ObjectDisposedException` |

### `LogFile` (static)

| Member | Description |
|---|---|
| `const string Extension` | `.jsonl` |
| `static string Folder` | `Application.persistentDataPath/Logs`, where `Session(store)` keeps a program's files |
| `static Result<JsonLinesSink> Session(LogStore store, int keep = 10)` | A file for this session in `Folder` |
| `static Result<JsonLinesSink> Session(LogStore store, string folder, int keep = 10)` | A file for this session in a folder, keeping the program's latest `keep` |
| `static Result<LogSessionFile> Read(string path)` | The log a `JsonLinesSink` wrote to a file, as far as it goes; fails with a `LogFileReadError` code |
| `static Result<LogSessionFile> Read(Stream stream, string name)` | The same, from a stream, which it leaves open; `name` is what the errors call it |
| `static string ExportName(string program, DateTimeOffset at)` | The name to offer for an export, as `Sample Game export 20261104-093015.jsonl`: never a session file's, so `Session` never deletes it |
| `static Result<LogExported> Export(LogView view, string path, LogSinkOptions options)` | Writes every row of a view as JSON lines that `Read` reads back, beside the path and moved to it once whole; fails with a `LogExportError` code |

### `LogFileError`

`NoFolder`, `NoFile`: the `Error.Code` of a failed `Session`.

### `LogExportError`

The `Error.Code` of a failed `Export`.
`NotWritten`: it could not be written, and the file at the path, when there was one, is as it was, with nothing beside it.
`NotPutBack`: a replace failed half way and could not put back the file it moved aside, which is beside the path under the name the error says, with why.
`LeftBeside`: a replace failed half way, and the file at the path is there, but the replace also left a file beside it under the name it moves aside to, which the error names; that file is kept, as it may be the only copy of the file that was there.
Each way, what it wrote is deleted, or the error says where it is.

### `LogExported` (record)

What an export that was written left beside its path.

| Member | Description |
|---|---|
| `Optional<LogNotDeleted> NotDeleted` | The file it wrote over, when that could not be deleted once replaced; it may hold what the export left out |

### `LogNotDeleted` (record)

A file an export wrote over and could not delete once replaced.

| Member | Description |
|---|---|
| `string Path` | Where it is: beside the export's path, as `.replaced`, or `.replaced-2` and on |
| `string Reason` | Why it could not be deleted, in the disk's own words, closed as a sentence: with a full stop unless they end in one or in `?` or `!` |

### `LogSessionFile`

| Member | Description |
|---|---|
| `string Path` | What it was read from |
| `LogArchive Archive` | Its records; `Capacity` is their count |
| `IReadOnlyList<LogSkippedLine> Skipped` | The lines it could not read, in the order of the file |

### `LogFileReadError`

`Unreadable`, `NotALog`, `UnknownFormat`: the `Error.Code` of a failed `Read`.
A first line past 64K characters is `NotALog`, as no header is that long.

### `LogSkippedLine` (record)

`LogSkippedLine(int Line, LogSkipReason Reason)`: a line's number, from 1, and why it was skipped.
`ToString()` says it as a sentence.

### `LogSkipReason` (abstract record)

| Case | The line |
|---|---|
| `NotJson` | Is not JSON |
| `NotALine` | Is JSON, but not a record, a declaration or a loss |
| `Undeclared(string Key, long Id)` | Is a record of an event the file does not declare before it |
| `NotADeclaration(string Key, string Why)` | Declares an event as the format does not write a declaration |
| `NotAnEvent(string Key, string Why)` | Declares what is not an event |
| `DeclaredTwice(string Key, long Id)` | Declares an event a line before it declares |
| `NotItsDeclaration(string Key, string Why)` | Is a record not as its declaration says |
| `NotAStamp(string Stamp)` | Is a record with a stamp no record can have |
| `SequenceTwice(long Sequence)` | Is a second record of a sequence |
| `LostAtEnd(long Count)` | Says records were lost after the last record |

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

## Reading

### `LogRecords` (abstract)

A `LogStore` or a `LogArchive`, and nothing else.

| Member | Description |
|---|---|
| `string Program` | The program that wrote the records |
| `LogClock Clock` | When its store started |
| `IReadOnlyList<LogEventSpec> Events` | The events its records are of |
| `int Capacity` | The most records it holds |
| `long First` | The oldest sequence it holds |
| `long Next` | One past the newest |
| `LogRedaction Redacted` | What the records lack, as the sink that wrote them left it out |
| `LogLateMark LateMark` | Where it is in placing records it was handed after a later one |
| `LogLate LateFrom(LogLateMark seen)` | Where to read again from to see the records placed late since the mark `seen`, with the mark to pass next time, read in one step |
| `LogRead Read(long from, LogBatch into)` | Copies the records from a sequence on, as many as fit |
| `int Read(ReadOnlySpan<long> sequences, LogBatch into)` | Copies the records of the given sequences it holds |

### `LogArchive` (`LogRecords`)

A session's records, held after the store that wrote them has moved on.
A console sink keeps one for its session, and `LogFile.Read` makes one from a file.

| Member | Description |
|---|---|
| `int Count` | How many records it holds |
| `long Dropped` | How many of its oldest it let go to stay under its cap, since it was last cleared |
| `LogErrorsHeld ErrorsFrom(long sequence)` | The errors it holds from a sequence on, and where it is now, read at once, without allocating |

An error is an event's record at `Error` or `Critical` that was not written as an error's backlog.
An error it let go, past its cap or on a clear, is not held, so it is not counted.

A console's `Clear` lets every record go: `First` and `Next` are then where it stopped, and it takes no record older.
A record older than what it let go is let go too, and so is one older than all it holds while it is full.
A file's archive, and an ended session's, hold what they held, and take nothing after.

### `LogErrorsHeld` (readonly record struct)

What `ErrorsFrom` read of an archive's errors.
Only an archive makes one, so its count and its ends always agree.

| Member | Description |
|---|---|
| `long Count` | How many errors it holds from the sequence on |
| `Optional<long> First` | The sequence of the first of them: none when there are none |
| `Optional<long> Last` | The sequence of the newest of them: none when there are none |
| `long Next` | The archive's `Next`, read with them |

### `LogLateMark` (readonly record struct)

Opaque: what `LateMark` and `LateFrom` hand a reader, to pass back to `LateFrom` next time.
It is of the records that made it: `default`, or a mark of other records, has seen none of these.

### `LogLate` (readonly record struct)

| Member | Description |
|---|---|
| `long From` | The least sequence placed late since the mark: `Next` when none was, `First` when more were than the records remember |
| `LogLateMark Mark` | The mark to pass to `LateFrom` next time, taken with `From` |

### `ArchivedException` (`Exception`)

| Member | Description |
|---|---|
| `ArchivedException(string typeName, string message, string text)` | An exception read back from a file |
| `string TypeName` | The full name it was thrown as |
| `ToString()` | The text the file holds, stack trace and all |

### `LogView`

| Member | Description |
|---|---|
| `LogView(LogRecords source, LogFilter filter)` | A view of the records the filter keeps, which the first `Update` reads |
| `LogRecords Source` | The records it reads |
| `LogScopes Scopes` | The scopes it has read |
| `LogFilter Filter` | Which records it is to show; setting one not equal to it scans again, and an equal one does nothing |
| `LogFilter Shown` | The filter the rows were made with: `Filter`, but while a scan is under way |
| `IReadOnlyList<LogFilterTerm> GaveUp` | The terms of `Shown` whose regular expression took too long over a record, in the filter's order, which match nothing in the rows from that record on |
| `bool IsScanning` | Whether a scan for a new filter or fold is under way |
| `int Count` | How many rows it has |
| `LogRow this[int row]` | A row, from 0, the oldest |
| `LogLevelCounts Counts` | How many records match, by level, everything the filter keeps but for its levels |
| `long EventCount(int place)` | How many records match of the event at a place in `Source`'s events, everything the filter keeps but for its event terms; 0 for an event declared since it last read |
| `long Lost` | How many records were lost before it read them |
| `void Update()` | Reads what arrived since it last did, or goes on with a scan; once a frame |
| `void Retry()` | Scans again when a regular expression of `Filter` took too long, to try it afresh; nothing when none did |
| `void Clear()` | Starts it at the newest record |
| `void Fold(long scope)`, `void Unfold(long scope)` | Hides or shows what is under a scope; scans again |
| `bool IsFolded(long scope)` | Whether the rows hide what is under a scope |
| `Optional<int> Find(long sequence)` | The row that holds a record, or the repeat that spans it |
| `int Read(int row, int count, LogBatch into)` | Copies the records of rows, one a row |
| `void Write(ILogSink sink, int row, int count, LogSinkOptions? options = null)` | Writes every record the rows stand for to a sink not yet opened, as a drain would |
| `void Write(ILogSink sink, IReadOnlyList<int> rows, LogSinkOptions? options = null)` | The same for rows that need not be one run, in order and each once, through one opening of the sink |

### `LogRow` (readonly record struct)

`LogRow(long Sequence, long Last, int Records, long Suppressed, LogLevel Level, LogRowKind Kind, int Depth, long LostBefore)`: the first and last record's sequences, how many records it stands for, what a limit dropped among them, their level, what kind of row it is, how many scopes with rows it is under, and how many records were lost just before it.

### `LogRowKind`

`Event`, `Scope`.

### `LogLevelCounts` (readonly record struct)

`LogLevelCounts(long Trace, long Debug, long Information, long Warning, long Error, long Critical)`, with `this[LogLevel level]` and `Total`.

### `LogScopes`

| Member | Description |
|---|---|
| `int Count` | How many scopes it knows |
| `bool IsUnder(long scope, long ancestor)` | Whether a scope is the ancestor or under it |
| `long Parent(long scope)` | The scope it was opened under, or 0 |
| `bool IsOpen(long scope)` | Whether it is known and its closing has not been read |
| `Optional<TimeSpan> Elapsed(long scope)` | How long it was open, once closed |

### `LogFilter` (record)

| Member | Description |
|---|---|
| `LogFilter(LogLevelRange levels, LogFrameRange frames, IReadOnlyList<LogFilterTerm> keep, IReadOnlyList<LogFilterTerm> leaveOut)` | A filter of its parts; the lists are copied |
| `static LogFilter All` | Keeps every record |
| `LogLevelRange Levels`, `LogFrameRange Frames` | The levels and frames kept |
| `IReadOnlyList<LogFilterTerm> Keep`, `LeaveOut` | The terms a record must meet, and must not |
| `static Result<LogFilter> Parse(string query)` | Reads a query; fails with a `LogFilterError` code and the word |
| `bool Matches(LogRecord record, LogScopes scopes)` | Whether a record is kept; a regular expression that takes too long over it does not match it |
| `bool KeepsEvent(LogEventSpec spec)` | Whether its event terms keep records of an event: one it keeps, when it keeps any, and none it leaves out |
| `IReadOnlyList<LogFilterTerm> MatchingNothing(IReadOnlyList<LogEventSpec> events)` | The events and fields it names that none of the events is or has |
| `ToString()` | The query that reads back as it |

Two filters are equal when their levels, frames and terms, in order, are.

### `LogFilterTerm` (abstract record)

| Case | Matches |
|---|---|
| `Event(string Pattern)` | Records of the events a pattern names, as a rule names them |
| `Scope(long Id)` | A scope's records, and every record under it |
| `Thread(int Id)` | What one managed thread wrote |
| `Field(string Name, string Pattern)` | A field whose formatted value a wildcard pattern matches, ignoring case |
| `Text(string Value)` | A message that holds the text, ignoring case |
| `TextRegex(string Pattern)` | A message a regular expression matches, ignoring case |
| `FieldRegex(string Name, string Pattern)` | A field whose formatted value a regular expression matches, ignoring case |

Each case's constructor throws for what is not one.
A regular expression's `Pattern` is as a query reads it back, with `/` for each `\/` it was written with, so the two are equal.

### `LogLevelRange`, `LogFrameRange` (readonly record structs)

The levels or frames a filter keeps, both ends kept: `All` (the `default`), `Empty`, `Only(x)`, `Between(a, b)`, `Contains(x)`, `Intersect(other)`, `IsAll`, `IsEmpty`.
`LogLevelRange` has `Least` and `Most`, and `LogFrameRange` has `First` and `Last`.

### `LogFilterError`

`NotATerm`, `NotALevel`, `NotAFrame`, `NotAScope`, `NotAThread`, `NoText`, `NoValue`, `QuoteNotClosed`, `CannotLeaveOut`, `RegexNotClosed`, `NotARegex`: the `Error.Code` of a failed `Parse`.

## Console sessions

### `LogConsole` (static)

| Member | Description |
|---|---|
| `static IReadOnlyList<LogConsoleSession> Sessions` | The process's sessions, oldest first: a new list each time one starts, ends, is opened, is closed or is cleared away |
| `static Optional<LogConsoleSession> Find(LogSessionId id)` | The session as it is now, while it is one of them |
| `static LogConsoleSession.File Open(LogSessionFile file)` | Adds a file's session; the one it is already, when it was opened before |
| `static void Close(LogConsoleSession.Ended session)` | Lets an ended session go; does nothing when it is gone already |
| `static void Close(LogConsoleSession.File session)` | Lets a file's session go; does nothing when it is gone already |
| `static void Clear()` | Lets ended sessions go and empties live ones; files' sessions stay |

### `LogConsoleSession` (abstract record)

| Member | Description |
|---|---|
| `LogSessionId Id` | Which session it is, whatever its state |
| `LogArchive Records` | Its records, which a `LogView` reads |
| `R Match<R>(Func<Live, R> live, Func<Ended, R> ended, Func<File, R> file)` | What the function for its state makes of it |

Its cases are its states:

- `Live`: kept by a console sink; its `Records.Capacity` is the sink's cap.
- `Ended`: its drain let its sink go, or its sink threw; `DateTimeOffset At` is when, and its records take nothing after.
- `File`: read back from a file; `string Path` and `IReadOnlyList<LogSkippedLine> Skipped` are its file's.

### `LogSessionId` (readonly record struct)

`long Value`: numbered from 1, in the order the process's sessions started or were opened; `default` is no session.
Ids compare by `Value`, with `<`, `>`, `<=`, `>=` and `CompareTo`, so a later session's is the greater.

## Unity

### `LogPump` (static)

`static IDisposable Install(LogDrain drain)`: installs the frame stamp and the drain pump into the PlayerLoop, until what it returns is disposed.

### `UnityLogBridge` (static)

`static IDisposable Attach(Log log)`: writes Unity's log into the log, until what it returns is disposed.

### `UnityLog` (static)

| Member | Description |
|---|---|
| `Events.Logged` | The spec of `unity/Logged`, with the fields `Message`, `StackTrace` and `Type` |
| `const string StackTraceField` | `StackTrace`, the field of `unity/Logged` that holds the stack Unity handed over |
| `static LogLevel LevelOf(LogType type)` | The level a message of a Unity log type is captured at |

### `UnityPaths`

| Member | Description |
|---|---|
| `UnityPaths(string root, IReadOnlyList<(string Folder, string Name)> packages)` | Paths mapped against the project's folder, empty when not known, and each package's folder and name; a package of an empty or blank folder or name throws `ArgumentException`, and a null one `ArgumentNullException` |
| `static UnityPaths ForThisProject()` | The paths of the project Unity is running: its folder, and in the editor every registered package's |
| `string Map(string file)` | A call site's path as Unity opens it, `Assets/...` or `Packages/<name>/...`; one it cannot map is kept, with forward slashes |
| `static string FileName(string path)` | The file's name, without its folder |

A path is matched ignoring case on Windows and macOS, and by its case elsewhere.
