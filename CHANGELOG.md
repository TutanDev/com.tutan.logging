# Changelog
All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [Unreleased]

First version: a structured log for Unity, ported from the design of FunctionalRenderingLab's `Utilities.Logging` (decision record `decisions/Logging.md` in the workspace).

### Added
- **Declared events and scopes:** `[LogCategory]` on a top-level `static partial` class, and `[LogEvent]` or `[LogScope]` on a `static partial void` method with no body, whose parameters are the event's fields.
  `[Personal]` marks a field a sink can redact, and `[LogTag]` says what a reader can do with one.
  An event's level is fixed by its attribute or chosen at each call by a `LogLevel` parameter, and `PerSecond` limits how many of its records a store keeps.
- **Source generator** (`Tutan.Logging.Generators`, Roslyn 4.3, labelled `RunOnlyOnAssembliesWithReference`).
  Beside each declaration it writes a `public static` extension of `Log` with the same fields plus `[CallerFilePath]` and `[CallerLineNumber]`, so every record carries the call that wrote it.
  Scopes return a `LogScope`.
  It writes the specs into a nested `Events` class and registers them through `InitializeOnLoadMethod` and `RuntimeInitializeOnLoadMethod(SubsystemRegistration)`.
  Declarations it cannot write for are compile errors TLOG0001 to TLOG0010.
  What it writes parses as C# 9.
- **Field types:** bool, char, every integer type, float, double, decimal, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, enums (by name, `[Flags]` joined with ` | `), `string`, `Exception`, `ReadOnlySpan<char>` (copied, cut at 1024 chars), unmanaged `IFormattable` structs such as `Vector3`, and `Optional<T>` or nullable of any of these but spans.
- **`LogCatalog`:** process-wide, listing every declared event with its source, and the conflicts two assemblies can make (`KeyTwice`, `CategoryTwice`), which every store also says as records.
  A registration checks every spec before it registers any.
- **`Log`:** a two-word value handle to a store and a scope.
  `Log.Discard` captures nothing, and a `default` `Log` throws on its first event, saying it was never handed a store.
  There is no static logger.
- **`LogScope`:** a value whose `Log` writes under the scope.
  Disposing it writes the closing record with how long it was open, and a scope that is off hands back the `Log` it came from.
- **`LogStore`:** three fixed rings (records, value slots, references), sized by `LogStoreOptions` and rounded to powers of two, about 2 MB by default.
  Writers never wait for readers, and overwritten records are counted for every reader.
  The record that opened a scope still open is kept even after its slot is overwritten.
  A record of an event written as a scope, of a scope written as an event, of a fixed level written at another, at `None`, or of an event a file declares, throws.
- **`LogRules`:** a least level, plus rules by `*`, category, `category.*` or key that set a level, `off`, a rate limit (`10/s`) or both.
  `LogRules.Parse` reads them from text and fails with a `LogRulesError` code.
  `LogStore.Apply` swaps them at runtime and says, as a record, any rule that matches no declared event.
- **`LogDrain` and `ILogSink`:** the drain reads the store when pumped and hands records to its sinks in sequence order.
  `LogSinkOptions` sets each sink's least level, its backlog (the latest records below that level sharing an error's scope, written before the error as context) and its redaction (`Personal`, `HomeFolder`).
  A sink that throws is reported once and not written to again.
- **`UnityConsoleSink`:** one Console row per record with the category in brackets, message, rate-limit count, enclosing scope, frame, and a hyperlink to the call site mapped to the path Unity opens.
  Rows are written without a stack trace.
  `callSiteLine: false` leaves out the second line, the call site and frame.
  In the editor a row also opens its call site on double-click through an internal Unity method, falling back to the link when that is missing.
- **`JsonLinesSink` and `LogFile`:** a self-describing JSON lines format (a header, a declaration before the first record of each event, then records), written without allocating once its buffers have grown.
  `LogFile.Session` opens a file per session under `Application.persistentDataPath/Logs` and keeps the latest ten.
- **`MemoryLog` and `MemorySink`** for tests: records as values, with fields by name.
- **`LogRecords`:** what a reader reads by sequence, a live `LogStore` or a session's `LogArchive` and nothing else, with `Redacted` saying what its records lack, and `LateMark` and `LateFrom` saying where records were placed after later ones, with the mark to pass next, as one `LogLate`.
- **`LogArchive`:** a session's records, held in sequence order up to a cap, letting the oldest go a block at a time (`Dropped`), and placing a record handed late, as an error's backlog is, by its sequence.
  A read says records were lost only where it was told so.
  A file's archive holds what the file held and takes nothing after.
- **`LogFile.Read`:** reads back a file a `JsonLinesSink` wrote, as far as it goes, even while a session still writes it, into a `LogSessionFile` of its `LogArchive` and the lines it skipped.
  A line it cannot read is skipped with a `LogSkipReason` that says why, and the rest is read.
  It fails with a `LogFileReadError` code only when the file cannot be read, is not a log, or is of another format.
  A record whose key, kind, level or fields are not as its declaration says is skipped, and a first line past 64K characters is not a log.
  A file's events are its archive's alone: the catalog never holds them, and no store writes them.
  Fields read back as the kinds they were declared as, but for chars, values of the program's own types and redacted personal fields, which read back as text, and exceptions, which read back as `ArchivedException`.
- **`LogFilter`:** a filter query, such as `level>=warning game.* -game.input/Moved Slot:3 frame:1200..1300 scope:4182 "no image"`, read by `LogFilter.Parse`, which fails with a `LogFilterError` code and the word, and written back by `ToString`.
  `/pattern/` matches the message and `Name:/pattern/` a field's value by regular expression, both ignoring case, and a match that takes too long over a record gives up and does not match it, rather than stall the editor.
  `\/` in a pattern is a slash, so a term and the query it writes back are equal.
  `MatchingNothing` says the events and fields a filter names that none of the events is or has.
- **`LogView`:** the records of a store or an archive that a filter keeps, as rows kept up to date a frame at a time, without allocating once grown.
  A run of repeating records is one row that counts them, a scope's opening is a row that what is under it nests under and can be folded, and a loss is said on the row after it.
  `Counts` are by level, of everything the filter keeps but for its levels.
  A new filter is scanned over several updates while the rows stay as they were.
  A regular expression that took too long over a record matches nothing in the rows from that record on, `GaveUp` says which did, and `Retry` tries them afresh.
  `Find` keeps a selection by sequence, and `Read` and `Write` copy rows or write every record they stand for to a sink, which leaves out what the records already lack and what the rows leave out.
- **`LogScopes`:** what is under what, as the records a view has read say, with each scope's parent, whether it is open, and how long it was.
- **`LogRecord.IsBacklog`:** whether a sink was handed the record as an error's backlog.
- **`LogPump`:** installs a frame stamp at the start of `EarlyUpdate` and the drain pump at the end of `PostLateUpdate`, and removes itself when Play Mode ends.
- **`UnityLogBridge`:** brings `Application.logMessageReceivedThreaded` into a store as `unity/Logged`, at the level its `LogType` maps to, skipping what the Console sink writes.
- **The package's own events**, in category `log`: `DrainFellBehind`, `SinkFailed`, `KeyTaken`, `CategoryTaken` and `RuleMatchesNothing`.
- **Tests:** an Edit Mode suite in `Tests/Editor` covering the store, rules, scopes, every field kind, the catalog, the drain, both sinks, the file, redaction, path mapping, the bridge, the pump, allocation-free writing, the archive, reading a file back, scopes, filters and the view, plus xunit tests of the generator in `Generators~`.
