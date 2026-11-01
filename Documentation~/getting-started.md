# Getting Started

## Install

Add the package and its one dependency to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.tutan.functional": "https://github.com/TutanDev/com.tutan.functional.git",
    "com.tutan.logging": "https://github.com/TutanDev/com.tutan.logging.git"
  }
}
```

`Tutan.Logging` is auto-referenced, so `Assembly-CSharp` sees it.
An assembly with its own `.asmdef` adds `Tutan.Logging` to its references, and the generator runs on it from then on.
The generator runs only on assemblies that reference `Tutan.Logging`, and what it writes is C# 9, so no `csc.rsp` is needed.

## Declare an event

Events are declared in a top-level `static partial` class that names their category:

```csharp
using System;
using Tutan.Logging;

[LogCategory("game.save")]
internal static partial class SaveLog
{
    [LogEvent(LogLevel.Information, "Saved slot {Slot} in {Elapsed}")]
    static partial void Saved(int slot, TimeSpan elapsed);

    [LogScope(LogLevel.Information, "Saving slot {Slot}")]
    static partial void Saving(int slot);
}
```

A declaration has no body and no access modifier.
Its parameters are the event's fields, and its template names them in holes.
The generator writes, beside it, the method a program calls:

```csharp
// What the generator writes, in outline.
public static void Saved(this Log log, int slot, TimeSpan elapsed,
    [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0);

public static LogScope Saving(this Log log, int slot,
    [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0);
```

So `log.Saved(3, elapsed)` binds to the generated extension, and the compiler fills in the call site.
See [Events and Scopes](events-and-scopes.md) for the field types, templates and everything else a declaration can say.

## Write through a Log

Code that logs takes a `Log`, as it takes any other dependency:

```csharp
public sealed class SaveManager
{
    private readonly Log _log;

    public SaveManager(Log log) => _log = log;

    public void Save(int slot)
    {
        using LogScope saving = _log.Saving(slot);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        // ...write the file...
        saving.Log.Saved(slot, watch.Elapsed);
    }
}
```

What is written through `saving.Log` is under the scope, and disposing `saving` writes the record that closes it, with how long it was open.

## Compose

The composition root makes the store, its sinks and its pump, and hands `Log`s to the code it composes.
How it is reached, and how a `MonoBehaviour` receives its `Log`, is the application's choice; the package ships no locator.
One way to write it:

```csharp
using System;
using System.Collections.Generic;
using Tutan.Functional;
using Tutan.Logging;
using UnityEngine;

public sealed class GameRoot : MonoBehaviour
{
    private readonly List<IDisposable> _teardown = new List<IDisposable>();

    private void Awake()
    {
        LogRules rules = LogRules.Parse("debug; game.save=trace").Match(
            error => { Debug.LogWarning($"Log rules: {error.Message}"); return LogRules.Default; },
            parsed => parsed);
        var store = new LogStore(rules);

        var sinks = new List<(ILogSink, LogSinkOptions)>
        {
            (new UnityConsoleSink(), new LogSinkOptions(LogLevel.Information, Backlog: 20)),
        };
        LogFile.Session(store).Match(
            error => Debug.LogWarning($"No log file: {error.Message}"),
            file => sinks.Add((file, new LogSinkOptions(LogLevel.Debug, Backlog: 100, Redact: LogRedaction.HomeFolder))));

        var drain = new LogDrain(store, sinks.ToArray());
        _teardown.Add(drain);
        _teardown.Add(LogPump.Install(drain));
        _teardown.Add(UnityLogBridge.Attach(store.Log));

        var saves = new SaveManager(store.Log);
        // ...hand saves, and store.Log, to what needs them...
    }

    private void OnDestroy()
    {
        for (int i = _teardown.Count - 1; i >= 0; i--)
            _teardown[i].Dispose();
    }
}
```

Teardown runs in reverse: the bridge detaches, the pump leaves the PlayerLoop, and the drain writes what is left and closes the file.

## Test

A test hands the code a `MemoryLog`, which captures every level by default, and asserts on records as values:

```csharp
using System.Linq;
using NUnit.Framework;
using Tutan.Logging;

public class SaveManagerTests
{
    [Test]
    public void Saving_says_the_slot()
    {
        using var logged = new MemoryLog();

        new SaveManager(logged.Log).Save(3);

        MemoryRecord saved = logged.Events.Single(r => r.Event == SaveLog.Events.Saved);
        Assert.That(saved.Field("Slot"), Is.EqualTo(3));
        Assert.That(saved.Scope, Is.Not.EqualTo(0));
    }
}
```

`SaveLog.Events.Saved` is the event's spec, which the generator writes into a nested `Events` class.
Code that wants nothing logged is handed `Log.Discard`.
A `default` `Log` was never handed a store, and its first event throws, so a forgotten dependency fails loudly instead of logging nowhere.
