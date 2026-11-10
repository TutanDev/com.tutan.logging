---
title: com.tutan.logging
---

# Tutan Logging

> A structured log for Unity 6: declared once, captured without allocating, read when something needs it.

`Debug.Log` formats text at the call, allocates, captures a stack trace for every line, and keeps nothing a filter or a backlog can use.
This package keeps records as typed data instead.
An event is declared once, as a `static partial` method whose parameters are its fields, and a source generator writes the method a program calls.
An event that is off costs one compare.
An event that is on copies its values into fixed rings and allocates nothing.
Formatting waits for a reader: the Unity Console once a frame, a JSON lines file, or a test.

---

## Features

| | |
|---|---|
| **Declared events** | `[LogEvent]` on a `static partial void` method; the generator writes a `Log` extension beside it |
| **Call sites** | Every record carries the file and line that wrote it, and a Console row links to it |
| **Scopes** | A `LogScope` is a value, and what is written through its `Log` is under it |
| **Rules** | Levels and rate limits per category or event, as data, applied at runtime |
| **Backlog** | An error brings the records that led to it, marked as context |
| **Frames** | Every record carries `Time.frameCount` |
| **Sinks** | The Unity Console, the log console's sessions, a JSON lines file per session, and memory for tests |
| **Views** | The records a filter query keeps, as rows that count repeats and nest under scopes, from a live store or a session's file |
| **The log console** | An editor window that reads a session's records whole: a query line, level badges, a category tree, columns, and each record's fields, scopes and source |
| **Unity's log** | A bridge brings every `Debug.Log` and exception into the store |
| **No static logger** | A `Log` is handed in, like any other dependency |

---

## Documentation

| Guide | What it covers |
|---|---|
| [Getting Started](getting-started.md) | Installation, a first event, the composition root, tests |
| [Events and Scopes](events-and-scopes.md) | Declarations, field types, templates, scopes, what the generator writes, the catalog |
| [Store and Rules](store-and-rules.md) | The rings, levels, rules and rate limits, what the log says about itself |
| [Sinks](sinks.md) | The drain, backlog, redaction, the Console, log console, JSON lines and memory sinks, writing a sink |
| [Reading Records](reading.md) | The view, filter queries, scopes, and reading a session's file back |
| [The Log Console](log-console.md) | The editor window: sessions, the query line, badges and categories, columns, details, exporting, unseen errors, settings |
| [Unity Integration](unity-integration.md) | The PlayerLoop pump, the `Debug.Log` bridge, Console links, Play Mode, caller paths in builds |
| [Generator Diagnostics](diagnostics.md) | TLOG0001 to TLOG0010 |
| [API Reference](api-reference.md) | Every public type and member |

---

## How the pieces fit

```
code ── log.Saved(3, file) ──▶ LogStore (fixed rings) ◀── LogPump stamps the frame (EarlyUpdate)
                                     │
                                     ▼  LogPump pumps the drain (end of PostLateUpdate)
                                 LogDrain ──▶ UnityConsoleSink   (rows with links)
                                     │──────▶ LogConsoleSink     (the log console's sessions ──▶ Window > General > Log Console)
                                     │──────▶ JsonLinesSink      (a file per session)
                                     └──────▶ MemorySink         (tests)

Debug.Log ── UnityLogBridge ──▶ LogStore
```

The composition root makes the store, the drain and its sinks, installs the pump and attaches the bridge.
Each of those returns what undoes it, so the root owns teardown too.
The code it composes receives a `Log`.

## Requirements

- Unity 6000.1 or later.
  The drain has no thread of its own, so nothing in the package needs threads.
- `com.tutan.functional` 1.0.0.

## Version 2

Version 2 reads records as well as writing them.
A `LogView` shows what a `LogFilter` keeps, over a live store or a `LogArchive` read back from a session's file.
A `LogConsoleSink` keeps every record of a session, whole, in `LogConsole.Sessions`, and a session that ended stays until the console is cleared.
The [log console](log-console.md), an editor window beside Unity's Console, draws them.
A runtime overlay, actions on a field's value and reading a player's log remotely are not in this version.
