# Unity Integration

The store, the rules and the drain know nothing of Unity's frame.
Four pieces connect them to it: the pump, the bridge, the Console sink, and the registration the generator writes.
Each piece the composition root installs returns what undoes it, so the root owns teardown too.

## The pump

```csharp
IDisposable pump = LogPump.Install(drain);
// ...
pump.Dispose();
```

`LogPump.Install` adds two systems to Unity's PlayerLoop:

| System | Where | What it does |
|---|---|---|
| `LogFrameStamp` | First in `EarlyUpdate` | `store.EnterFrame(Time.frameCount)`, so every record says the frame it was written in |
| `LogDrainPump` | Last in `PostLateUpdate` | Drains the store to the sinks and flushes them, once the frame's work is done |

Both show by those names in the Profiler.
Disposing the pump takes out its two systems and no others, so several pumps can be installed, and disposing twice does nothing.
A pump whose drain was disposed first does nothing, rather than throwing every frame.

Installed in Play Mode in the editor, the pump removes itself when Play Mode ends, since Unity keeps a PlayerLoop that was changed into Edit Mode.
That also covers entering Play Mode with domain reload off: each entry installs a pump of its own, and nothing of the last one is left.

In Edit Mode the PlayerLoop does not run every editor frame, so an editor tool pumps its drain itself: `EditorApplication.update += drain.Pump;`.

## The bridge

```csharp
IDisposable bridge = UnityLogBridge.Attach(store.Log);
// ...
bridge.Dispose();
```

`UnityLogBridge.Attach` brings Unity's own log into a store, through `Application.logMessageReceivedThreaded`: every `Debug.Log`, warning, error, failed assertion and unhandled exception, from any thread.
Each is a record of `unity/Logged`, with the fields `Message`, `StackTrace` and `Type`, at the level its type maps to:

| `LogType` | Level |
|---|---|
| `Log` | `Information` |
| `Warning` | `Warning` |
| `Error`, `Assert`, `Exception` | `Error` |

So an exception Unity reports brings the records that led to it to a sink with a backlog, and code that does not log through a `Log`, as other packages', reaches the same file as code that does.
Rules hold it like any other category: `unity=warning` keeps only Unity's warnings and errors.

- The message and stack trace are kept by reference, so a message costs the store nothing Unity has not already allocated.
- A bridged record has no call site: where Unity's log was called from is in its stack trace.
- What `UnityConsoleSink` writes is not captured, and the sink does not write what the bridge captured, so the two never feed each other.
- Attaching `default` throws `ArgumentException`, since it was never handed a store.
- Attached in Play Mode in the editor, the bridge detaches itself when Play Mode ends, so a composition root that does not tear down still leaves nothing behind.

## Console links

A record reaches the Console only when the drain writes it, so the stack Unity would capture is the drain's.
The call site travels in the record instead: the generated method takes `[CallerFilePath]` and `[CallerLineNumber]`, two values the compiler writes as constants, so a call still allocates nothing.

`UnityConsoleSink` ends each row with the call site as a Unity hyperlink:

```
<a href="Assets/Game/SaveManager.cs" line="42">SaveManager.cs:42</a>
```

Unity compiles the assemblies of packages with `/pathmap:<project>=.`, so the compiler writes a package's file in the project relative to it, as `.\Packages\com.tutan.messages\Runtime\MessageBus.cs` on Windows.
It compiles the assemblies under `Assets/` with no path map, so their files are written absolute, as is any file outside the project.
The sink maps each to the path Unity opens:

| Compiled as | Opens as |
|---|---|
| `C:\Projects\MyGame\Assets\Game\SaveManager.cs` | `Assets/Game/SaveManager.cs` |
| `.\Packages\com.tutan.messages\Runtime\MessageBus.cs` | `Packages/com.tutan.messages/Runtime/MessageBus.cs` |
| `.\Library\PackageCache\com.tutan.messages@1.0.0\Runtime\MessageBus.cs` | `Packages/com.tutan.messages/Runtime/MessageBus.cs` |
| A package folder anywhere on disk | `Packages/<name>/...`, by the folder Unity registered it from |
| `.\Assets\Game\SaveManager.cs`, as a path map of your own writes it | `Assets/Game/SaveManager.cs` |
| A path outside the project | Kept as it is, with forward slashes |

Each path is mapped once and then read from a table.

In the editor, a row written with `openOnDoubleClick` (the default) also opens the call site when it is double-clicked, as a `Debug.Log` written at that line does.
Unity has no public way to say where a row opens, so this binds the internal `Debug.LogInfo`, `LogWarning` and `LogError` overloads that take a file and a line.
It is kept apart from the sink, and Editor-only, so that a Unity upgrade that renames or removes them costs only the double-click: the sink falls back to rows with the link, for good, the first time binding or a call fails.
`new UnityConsoleSink(openOnDoubleClick: false)` writes every row with the public API.
`new UnityConsoleSink(callSiteLine: false)` leaves the link line out of every row, leaving the double-click as the way to the call site.

## Registration

The generator writes, into every category class, a registration method marked with both:

- `[InitializeOnLoadMethod]`, so the editor registers the class's events when it loads the assembly;
- `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`, so a player, and every entry to Play Mode, registers them before the first scene loads.

Registering is idempotent, so entering Play Mode with domain reload off lists each event once.
The package's own `log` and `unity` events are registered before any other.

## Caller paths in builds

The compiler writes each calling file's path into the assembly as a constant, once per file, and the generator writes each event's declaring file the same way.
Unity's `/pathmap:<project>=.` covers only the assemblies of packages, so a package's file in the project reaches a build relative to it.
`Assembly-CSharp` and every assembly defined under `Assets/` are compiled with no path map, so a build carries their files' absolute paths on the machine that built it, as `C:\Projects\MyGame\Assets\Game\SaveManager.cs`.
So does a package whose folder is outside the project.

Code that logs from an embedded package, a folder under `Packages/`, is compiled with Unity's path map like any other package's.
A `-pathmap` in an assembly's `csc.rsp` maps the rest, though the option names the project's folder on one machine.

A player knows no project folder, so it writes an absolute call site as the compiler wrote it, with forward slashes, as `(at C:/Projects/MyGame/Assets/Game/SaveManager.cs:42)`.

## Threads

A `Log` can be written from any thread: a record is written under the store's lock, which holds only for the copy of its fields.
The drain and its sinks run on the thread that pumps it, which with `LogPump` is the main thread.
`UnityConsoleSink.Open` asks Unity where the project and its packages are, which Unity answers only on its main thread, so a drain with a Console sink is made on the main thread.
