# Tutan Logging

A structured log for Unity 6.
Records are typed data, not text.
An event is declared once as a `static partial` method, and a source generator writes the method a program calls.
An event that is off costs a compare, and one that is on copies its values into fixed rings and allocates nothing.
Records are drained once a frame to the Unity Console, with a link to the line that wrote them, and to a JSON lines file.
An error brings the records that led to it.

## Quick start

Declare the events of one category in a `static partial` class:

```csharp
using System;
using Tutan.Logging;

[LogCategory("game.save")]
internal static partial class SaveLog
{
    [LogScope(LogLevel.Information, "Saving slot {Slot}")]
    static partial void Saving(int slot);

    [LogEvent(LogLevel.Information, "Saved slot {Slot} as {File}")]
    static partial void Saved(int slot, string file);

    [LogEvent(LogLevel.Error, "Saving slot {Slot} failed")]
    static partial void Failed(int slot, Exception error);
}
```

Write them through the `Log` the code was handed:

```csharp
public sealed class SaveManager
{
    private readonly Log _log;

    public SaveManager(Log log) => _log = log;

    public void Save(int slot, string file)
    {
        using LogScope saving = _log.Saving(slot);
        try
        {
            // ...write the file...
            saving.Log.Saved(slot, file);
        }
        catch (Exception error)
        {
            saving.Log.Failed(slot, error);
            throw;
        }
    }
}
```

The composition root makes the store, its sinks and its pump, and hands out the `Log`:

```csharp
var store = new LogStore(LogRules.Default);
var drain = new LogDrain(store,
    (new UnityConsoleSink(), new LogSinkOptions(LogLevel.Information, Backlog: 20)));
IDisposable pump = LogPump.Install(drain);
IDisposable bridge = UnityLogBridge.Attach(store.Log);

var saves = new SaveManager(store.Log);
```

A test hands the same code a `MemoryLog` and asserts on what it logged as values:

```csharp
using var logged = new MemoryLog();
new SaveManager(logged.Log).Save(3, "slot3.sav");

MemoryRecord saved = logged.Events.Single(r => r.Event == SaveLog.Events.Saved);
Assert.That(saved.Field("Slot"), Is.EqualTo(3));
```

## Features

- **Declared events:** a declaration names the event's fields and nothing else.
  The generator writes a `Log` extension method beside it, with the call site the compiler fills in.
- **No allocation at the call:** values are copied into three fixed rings under a short lock, and strings and exceptions are kept by reference.
- **Clickable call sites:** a Console row ends with a link to the file and line that wrote it, and double-clicking the row opens it.
- **Scopes as values:** `using LogScope s = log.Saving(slot)` returns a `Log` whose records are under the scope.
  There is no ambient state to lose across an `await`.
- **Rules:** `debug; game.save=trace; ai.*=warning; physics/Contact=10/s` chooses levels and rate limits per category or event, applied at runtime.
- **Backlog:** a sink writes the latest records below its level that share an error's scope before the error, marked as context.
- **Frames:** every record carries `Time.frameCount`, stamped by a pump in the PlayerLoop.
- **JSON lines file:** one self-describing file per session in `Application.persistentDataPath`, with the oldest pruned.
- **Views:** a `LogView` reads a live store, or a session's file read back by `LogFile.Read`, as rows a filter query such as `level>=warning game.* Slot:3 /fail(ed|ure)/` keeps, with repeats counted and scopes nested.
- **The log console:** an editor window, beside Unity's Console, that reads a session's records whole, with a query line, level badges, a category tree, columns each developer shows and sizes, and each record's fields, scopes and source.
- **Unity's own log:** a bridge brings every `Debug.Log`, warning, error and exception into the store, so they reach the file with their backlog.
- **No static logger:** a `Log` is a dependency like any other, handed in by the composition root.

## The log console

Add a `LogConsoleSink` to the drain, and open **Window > General > Log Console**:

```csharp
var drain = new LogDrain(store,
    (new UnityConsoleSink(), new LogSinkOptions(LogLevel.Warning)),
    (new LogConsoleSink(), new LogSinkOptions(LogLevel.Debug)));
```

The window shows a session's records as rows a query keeps, with a badge for each level and a tree of the categories.
Selecting a row shows its fields, the scopes it is under, where it was written and the source around that line, and double-clicking it opens that line.
A session that ended, as when Play Mode exits, stays until the console is cleared, and a session's JSON lines file can be opened and read back.
The team shares saved filters, category colours and what is always left out, in `ProjectSettings`, and each developer keeps their own columns, Clear on Play and Pause on Error, in `UserSettings`.
Outside the editor the sink keeps nothing.

## Requirements

- Unity 6000.1 or later.
- `com.tutan.functional` 1.0.0.

The generator runs on every assembly that references `Tutan.Logging`.
`Assembly-CSharp` references it automatically, and an assembly with its own `.asmdef` adds `Tutan.Logging` to its references.
The code it writes is C# 9, so no `csc.rsp` is needed.

## Documentation

| Guide | What it covers |
|---|---|
| [Getting Started](Documentation~/getting-started.md) | Installation, a first event, the composition root, tests |
| [Events and Scopes](Documentation~/events-and-scopes.md) | Declarations, field types, templates, scopes, what the generator writes, the catalog |
| [Store and Rules](Documentation~/store-and-rules.md) | The rings, levels, rules and rate limits, what the log says about itself |
| [Sinks](Documentation~/sinks.md) | The drain, backlog, redaction, the Console, JSON lines and memory sinks, writing a sink |
| [Reading Records](Documentation~/reading.md) | The view, filter queries, scopes, and reading a session's file back |
| [The Log Console](Documentation~/log-console.md) | The editor window: sessions, the query line, badges and categories, columns, details, exporting, unseen errors, settings |
| [Unity Integration](Documentation~/unity-integration.md) | The PlayerLoop pump, the `Debug.Log` bridge, Console links, Play Mode, caller paths in builds |
| [Generator Diagnostics](Documentation~/diagnostics.md) | TLOG0001 to TLOG0010 |
| [API Reference](Documentation~/api-reference.md) | Every public type and member |
