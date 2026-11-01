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
- **`Log`:** a two-word value handle to a store and a scope.
  `Log.Discard` captures nothing, and a `default` `Log` throws on its first event, saying it was never handed a store.
  There is no static logger.
- **`LogScope`:** a value whose `Log` writes under the scope.
  Disposing it writes the closing record with how long it was open, and a scope that is off hands back the `Log` it came from.
- **`LogStore`:** three fixed rings (records, value slots, references), sized by `LogStoreOptions` and rounded to powers of two, about 2 MB by default.
  Writers never wait for readers, and overwritten records are counted for every reader.
  The record that opened a scope still open is kept even after its slot is overwritten.
- **`LogRules`:** a least level, plus rules by `*`, category, `category.*` or key that set a level, `off`, a rate limit (`10/s`) or both.
  `LogRules.Parse` reads them from text and fails with a `LogRulesError` code.
  `LogStore.Apply` swaps them at runtime and says, as a record, any rule that matches no declared event.
- **`LogDrain` and `ILogSink`:** the drain reads the store when pumped and hands records to its sinks in sequence order.
  `LogSinkOptions` sets each sink's least level, its backlog (the latest records below that level sharing an error's scope, written before the error as context) and its redaction (`Personal`, `HomeFolder`).
  A sink that throws is reported once and not written to again.
- **`UnityConsoleSink`:** one Console row per record with the category, message, rate-limit count, enclosing scope, frame, and a hyperlink to the call site mapped to the path Unity opens.
  Rows are written without a stack trace.
  In the editor a row also opens its call site on double-click through an internal Unity method, falling back to the link when that is missing.
- **`JsonLinesSink` and `LogFile`:** a self-describing JSON lines format (a header, a declaration before the first record of each event, then records), written without allocating once its buffers have grown.
  `LogFile.Session` opens a file per session under `Application.persistentDataPath/Logs` and keeps the latest ten.
- **`MemoryLog` and `MemorySink`** for tests: records as values, with fields by name.
- **`LogPump`:** installs a frame stamp at the start of `EarlyUpdate` and the drain pump at the end of `PostLateUpdate`, and removes itself when Play Mode ends.
- **`UnityLogBridge`:** brings `Application.logMessageReceivedThreaded` into a store as `unity/Logged`, at the level its `LogType` maps to, skipping what the Console sink writes.
- **The package's own events**, in category `log`: `DrainFellBehind`, `SinkFailed`, `KeyTaken`, `CategoryTaken` and `RuleMatchesNothing`.
- **Tests:** an Edit Mode suite in `Tests/Editor` covering the store, rules, scopes, every field kind, the catalog, the drain, both sinks, the file, redaction, path mapping, the bridge, the pump, and allocation-free writing, plus xunit tests of the generator in `Generators~`.
