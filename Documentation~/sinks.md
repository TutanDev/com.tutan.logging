# Sinks

A sink is where records leave the program: the Unity Console, the log console, a file, or memory for a test.
Sinks never read the store; a drain reads it and hands every record to each of its sinks, which write the ones their options let through.

## The drain

```csharp
var drain = new LogDrain(store,
    (new UnityConsoleSink(), new LogSinkOptions(LogLevel.Information, Backlog: 20)),
    (file, new LogSinkOptions(LogLevel.Debug, Backlog: 100, Redact: LogRedaction.Personal)));
```

A `LogDrain` reads a store when it is pumped and hands what it read to its sinks, in sequence order, on the thread that pumps it.
It has no thread of its own, since WebGL has none.
It starts from the oldest record the store still holds, so what was logged before the drain was made is not lost.

| Member | What it does |
|---|---|
| `Pump()` | Reads what arrived since the last read, hands it to the sinks, and flushes them; throws `ObjectDisposedException` once disposed |
| `Flush()` | Everything the store holds, to the sinks, now, on the caller's thread, and flushed: for a crash or an exit; does nothing once disposed |
| `Dispose()` | Drains and flushes what is left, and disposes the sinks that are disposable |
| `Store` | The store it reads |

`LogPump.Install(drain)` pumps it once a frame from the PlayerLoop (see [Unity Integration](unity-integration.md)).
An editor tool that wants records in Edit Mode, when the PlayerLoop does not run every editor frame, pumps it itself: `EditorApplication.update += drain.Pump;`.

The drain owns its sinks.
A sink given twice is refused with `ArgumentException`, since it would write every record twice.

A sink that throws is said, as a `log/SinkFailed` record with the exception, and is not written to again.
The program and the other sinks go on.

## Sink options

```csharp
public sealed record LogSinkOptions(LogLevel Least = LogLevel.Information, int Backlog = 0, LogRedaction Redact = LogRedaction.None);
```

### Least

The least level the sink writes.
The store captures from `Debug` by default and sinks write from `Information`, so `Debug` records are there for a backlog without filling the Console.

### Backlog

How many records below `Least` the sink writes before an error, as its context, at most.
When a record at `Error` or `Critical` arrives, the sink is first handed the latest records it skipped that share the error's scope, oldest first, each marked as context.

- The error's scope is its outermost known scope, so an error deep in nested scopes brings what happened anywhere in the operation it belongs to.
- An error under no scope brings the latest records under no scope.
- The opening of the error's scope is context too, so the sink can say what the error was under.
- A scope's closing is never context: by the time the error arrives, it says only that something ended.
- A record is written as context once, however many errors follow it.

0, the default, writes no backlog.
A negative backlog is refused.

### Redact

What the sink leaves out of the fields it writes:

| Flag | Writes |
|---|---|
| `LogRedaction.None` | Every field as it was logged |
| `LogRedaction.Personal` | `[personal]` for every field marked `[Personal]`, exceptions included |
| `LogRedaction.HomeFolder` | `~` for the user's home folder, wherever a string, chars or exception field has it |

The store keeps every value; redaction is per sink.
A file that leaves the player's machine redacts what a developer's Console does not need to.

## Unity Console

```csharp
new UnityConsoleSink(openOnDoubleClick: true, name: "unity console", callSiteLine: true)
```

Writes each record as one Console row, and in a player as one line of the player log:

```
[game.save] Saved slot 3 in 00:00:01.2000000  < Saving slot 3
SaveManager.cs:42  frame 1240
```

- The first line is the category in brackets and the message, then `  (+N)` when a limit dropped N records of the event before it, then `  < ` and the opening of the scope it is under.
- A scope's closing reads `Saving slot 3: done in 12.5 ms`.
- A record written as an error's context starts with `(context) `.
- An exception follows its record whole, on the lines after it.
- The second line is the call site, which in the editor is a link, `<a href="Assets/Game/SaveManager.cs" line="42">`, that opens the file at the line; then the frame, when the store had one.
  In a player it reads `(at Assets/Game/SaveManager.cs:42)`.
  With `callSiteLine: false` a row is its first line alone, and in the editor, with `openOnDoubleClick`, a double-click still opens the call site.
- Levels map to row types: `Warning` is a warning, `Error` and `Critical` are errors, and the rest are logs (`UnityConsoleSink.TypeOf`).
- Records overwritten before the drain read them are a warning row, `1,234 records were overwritten before the Console read them`.

A row is written with `LogOption.NoStacktrace`, since the stack Unity would capture is the drain's, not the call's.
With `openOnDoubleClick`, in the editor, a row also opens its call site when double-clicked, through an internal Unity method; if a Unity version does not have it, every row is written with the public API and the link still opens the call site.

It skips what `UnityLogBridge` captured from Unity's own log, which the Console already shows, and the bridge skips what this sink writes, so neither feeds the other.
A row is a string, which Unity's log needs, so this sink allocates for every record it writes; records below its level cost nothing here.

## JSON lines file

```csharp
LogFile.Session(store).Match(
    error => Debug.LogWarning($"No log file: {error.Message}"),
    file => sinks.Add((file, new LogSinkOptions(LogLevel.Debug, Backlog: 100))));
```

`LogFile.Session(store, keep: 10)` opens a file for this session in the `Logs` folder of `Application.persistentDataPath`, the folder a player build can always write.
`LogFile.Session(store, folder, keep)` opens it in a folder of the program's choosing, which it makes if it has to.

- The file is named for the program and the time the store started: `Sample Game-20261101-102409-000.jsonl`.
  Characters a file name cannot have become `_`.
- The program's oldest files beyond `keep`, this one counted, are deleted; a file that cannot be deleted is left, and other programs' files are never touched.
- It fails with an `Error` whose `Code` is a `LogFileError`: `NoFolder` when the folder cannot be made, `NoFile` when the file cannot.
- A `keep` below 1 throws `ArgumentOutOfRangeException`.

`new JsonLinesSink(stream, leaveOpen: false, name: "json")` writes the same format to any stream.

### The format

One JSON object per line, UTF-8 without a byte order mark:

```json
{"format":1,"program":"Sample Game","started":"2026-11-01T10:24:09.0000000+01:00","redact":["Personal"]}
{"event":"game.save/Saved","id":1,"kind":"Event","lvl":"Information","template":"Saved slot {Slot} in {Elapsed}","fields":[{"name":"Slot","kind":"Int32","type":"int"},{"name":"Elapsed","kind":"TimeSpan","type":"System.TimeSpan"}],"source":{"assembly":"Assembly-CSharp","type":"Game.SaveLog","file":"C:\\Projects\\MyGame\\Assets\\Game\\SaveLog.cs","line":12}}
{"seq":4190,"scope":4182,"t":"2026-11-01T10:24:11.3510000+01:00","frame":1240,"thread":1,"lvl":"Information","key":"game.save/Saved","id":1,"msg":"Saved slot 3 in 00:00:01.2000000","file":"C:\\Projects\\MyGame\\Assets\\Game\\SaveManager.cs","line":42,"fields":{"Slot":3,"Elapsed":"00:00:01.2000000"}}
```

- **The header**, first: `format` (`JsonLinesSink.Format`, now 1), `program`, `started`, and `redact` when the sink redacts.
- **A declaration**, before the first record of each event: its key, an `id` numbered from 1 in each file, `kind`, `lvl` (absent when each call chooses), `template`, `perSecond` when it has one, its `fields`, and its `source`.
  A field has `name`, `kind` and `type`, and `personal`, `tag`, `optional`, `names` (an enum's members) and `flags` when they apply.
  So the file says what its records are without the program that wrote them.
- **A record**: `seq`, `scope` when under one, `t`, `frame` when stamped, `thread`, `lvl`, `key` and the `id` of its declaration, `msg`, `suppressed` when a limit dropped some, `context` when written as an error's context, `file` and `line` when it has a call site, and its `fields`.
  A record names its declaration by `id` as well as its event by `key`, since two assemblies can declare one key.
- A scope's opening has `begin`, its own sequence, and its closing has `end`, the opening's, and `ms`, how long it was open.
- Numbers and bools are JSON's own, and a number that is not finite is a string.
  An exception is an object of its `type`, `message` and `text`.
  Every other value is its formatted text, and a value of the program's own type is formatted as the first hole that names it formats it.
- `{"lost":N}` says N records were overwritten before the drain read them.

Its buffers are reused for each line, so writing allocates nothing once they have grown, but for an exception's text.

## Log console

```csharp
var drain = new LogDrain(store,
    (new UnityConsoleSink(), new LogSinkOptions(LogLevel.Warning)),
    (new LogConsoleSink(), new LogSinkOptions(LogLevel.Debug)));
```

`LogConsoleSink` keeps a session for the log console: every record its drain hands it, whole and typed, in a live session of `LogConsole.Sessions`.
The session starts when the drain opens the sink, and ends when the drain disposes it.
An ended session stays until the console is cleared, so a Play Mode session can be read after Play Mode exits.

- A session holds at most `cap` records, a million by default, and past that lets the oldest go a block at a time, which its `Records.Dropped` counts.
  So it keeps what was written since the last clear, not only what the store's rings still hold.
- A record under a scope whose opening was below the sink's level is kept after that opening, so it still says what it is under.
  Such a scope's closing is below the level too, so the session shows the scope as open.
- It keeps records whole, so its options redact nothing: options that do are refused when it is opened, and the drain says so as `log/SinkFailed`.
- A sink keeps one session, so a sink given to a second drain fails there.
- A sink that throws is written to no more, so its session ends then, as when its drain lets it go.
- Outside the editor it keeps nothing, since no window can read a session, so a composition root can add it without an `#if`.
- A `cap` below 1 throws `ArgumentOutOfRangeException`.

`LogConsole` is the process's sessions.

| Member | What it does |
|---|---|
| `Sessions` | The sessions, oldest first: a new read-only list each time one starts, ends, is opened, is closed or is cleared away |
| `Find(id)` | A session's state now, by its `LogSessionId`, while it is one of them |
| `Open(file)` | Adds the session of a file `LogFile.Read` read, once |
| `Close(session)` | Lets an ended session or a file's go; a live one has no `Close`, since it ends when its drain lets its sink go |
| `Clear()` | Lets ended sessions go, and empties live ones, which keep what they are handed after; files' sessions stay |

A `LogConsoleSession` is `Live`, `Ended` (with `At`, when it ended) or `File` (with its `Path` and `Skipped` lines), and its `Match` takes a function for each.
Every one has an `Id` and its `Records`, a `LogArchive` that a `LogView` reads as it reads a store (see [Reading Records](reading.md)).
A session's state never changes in place: when a live session ends, `Sessions` is a new list with an `Ended` under the same id where it was.
A domain reload lets every session go.

## Memory, for tests

```csharp
using var logged = new MemoryLog();
var saves = new SaveManager(logged.Log);

saves.Save(3);

MemoryRecord saved = logged.Events.Single(r => r.Event == SaveLog.Events.Saved);
Assert.That(saved.Field("Slot"), Is.EqualTo(3));
```

`MemoryLog` is a store that captures every level, unless given rules, drained into a `MemorySink` each time its records are read.

| Member | What it is |
|---|---|
| `Log` | The handle to pass to the code under test |
| `Records` | Every record so far, scopes' openings and closings included |
| `Events` | The events so far, without scopes' openings and closings |
| `Store` | The store, for a test that reads it as a console would |

A `MemoryRecord` has every stamp as a value (`Sequence`, `Frame`, `Scope`, `Begin`, `Thread`, `Level`, `Kind`, `Event`, `Suppressed`, `Backlog`, `Elapsed`, `CallSite`), its `Message`, and its `Fields`.
`record.Field("Slot")` is a field's value by name, ignoring case: a number, bool, char, guid, date or span as itself, an enum as its `long`, text as a string, an exception as itself, and a value of the program's own type as its text.

`MemorySink` can be given to any drain, for a test that builds its own store; it also counts `Overwritten` records and `Flushes`.
It allocates for every record, which is what a test wants and a program does not.

## Writing a sink

```csharp
public interface ILogSink
{
    string Name { get; }
    void Open(LogContext context);
    void Write(LogRecord record, bool backlog, LogContext context);
    void Lost(long count, LogContext context);
    void Flush();
}
```

- `Open` is called once, before anything else.
- `Write` is called for every record the sink's options let through, and for each record of a backlog with `backlog` true.
- `Lost` says how many records were overwritten before the drain read them, at that point in the sequence.
- `Flush` makes what was written durable, as far as the sink can.
- A sink that is also `IDisposable` is disposed with the drain.

A `LogRecord` is valid only during `Write`.
A sink that keeps records copies what it needs out of it.

`LogContext` is what a sink writes with:

| Member | What it is |
|---|---|
| `Clock` | The store's clock, which turns a record's `Timestamp` into a time |
| `Program` | The program's name |
| `Options`, `Redact` | The sink's options |
| `TryScope(scope, out begin)` | The record that opened a scope, if the drain read it and the scope has not closed |
| `TryFormat(record, span, out written)` | The record's message with the sink's redaction, and for a scope's closing, its opening's message |
| `Text(record)` | The same, as a string |

A sink that wants to allocate nothing formats into a buffer of its own with `TryFormat`, and grows the buffer when it returns false.
`record.Field(i)` and `record.TryField(name, out value)` return a `LogValue`, which reads as itself (`Read<T>()`), as a number (`AsInt64()`, `AsDouble()`), as chars (`AsChars()`), or as the reference it was kept by (`AsRef()`), and formats into a span (`TryFormat`).
