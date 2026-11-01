# Store and Rules

## The store

A `LogStore` is where a program's records are held.
The composition root makes one, hands its `Log` to the code it composes, and owns it: the package never makes a store on its own.
A process can have several, side by side, as two tests running at once do.

```csharp
var store = new LogStore(LogRules.Default);
var quiet = new LogStore(LogRules.Parse("warning").ValueUnsafe(), new LogStoreOptions(Records: 1024, Program: "Server"));
```

### The rings

A store holds three rings of fixed size, allocated once:

| Ring | Holds | Default | Least |
|---|---|---|---|
| Records | One 72-byte entry per record: its stamps, level, event, scope and call site | 16,384 | 16 (`LogStore.MinRecords`) |
| Bits | The values copied at the call, in 8-byte slots | 65,536 slots | 32,768 (`LogStore.MinBits`) |
| Refs | The strings and exceptions kept by reference | 16,384 | 64 (`LogStore.MinRefs`) |

`LogStoreOptions` sets each size, which is rounded up to a power of two; a size below the least throws `ArgumentOutOfRangeException`.
The defaults hold about 2 MB: some sixteen thousand records, a few frames' worth of a busy game or minutes of a quiet one.
`Capacity` is how many records the store holds once rounded.
`LogStoreOptions.Program` names the program, which a file sink writes first and names its file by; null reads `Application.productName`.

### Writing

A record is written under a lock, held for the copy of its fields and nothing else: no user code and no allocation run under it.
The timestamp is taken under the lock too, so sequences are in the order of time.

A writer never waits for a reader.
When a ring is full the oldest records are overwritten, and since every reader reads by sequence, a reader that fell behind knows how many it lost.
A drain that loses records says so, as a `log/DrainFellBehind` record and as a `Lost` call on each sink.
The record that opened a scope still open is kept, however old it is, so whatever is written under the scope can still say what it is under.

### Stamps

Every record carries:

| Stamp | What it is |
|---|---|
| `Sequence` | From 1 in each store, with no holes; how every reader addresses a record |
| `Timestamp` | A `Stopwatch` timestamp, which the store's `LogClock` turns into a time |
| `Frame` | The frame the store was told last, by `EnterFrame`; 0 before the first |
| `Scope` | The scope it was written under, or 0 |
| `Thread` | The managed id of the thread that wrote it |
| `CallSite` | The file and line of the call, as the compiler filled them in |
| `Suppressed` | How many records of its event a limit dropped since the last one kept |

`LogPump` calls `store.EnterFrame(Time.frameCount)` at the start of every frame (see [Unity Integration](unity-integration.md)).
A store that is not pumped can be told its frame by whatever drives it.

`store.Clock` holds when the store started, as `LogClock(Started, StartedAt)`: `Clock.At(timestamp)` is the wall-clock time of a record, and `LogClock.Between(from, to)` is the time between two timestamps.

### Reading

A sink never reads the store itself: a drain reads it and hands the sinks what it read.
A reader of its own, as a console view, reads into a `LogBatch`:

```csharp
var batch = new LogBatch(records: 1024);
long next = store.First;

LogRead read = store.Read(next, batch);
for (int i = 0; i < batch.Count; i++)
{
    LogRecord record = batch[i];
    // ...
}
next = read.Next;
```

- `Read(from, batch)` copies the records from a sequence on, as many as fit, and returns a `LogRead` that says the first copied, how many, the next sequence to read, and how many were `Lost` to overwriting after `from`.
- `Read(sequences, batch)` copies the records of the given sequences the store still holds, in the order given; a scope's opening still open is found however old.
- `First` is the oldest sequence the store still holds, and `Next` the one the next record gets.
- A `LogRecord` is valid until the next read into its batch, and being a `ref struct` it cannot be kept past it.

## Log

A `Log` is what a program passes to the code that logs: a handle to one store, and to the scope what is written through it is under.
It is a value of two words, so the `Log` of every scope a program opens allocates nothing.

| Member | What it is |
|---|---|
| `store.Log` | A handle under no scope |
| `Log.Discard` | A log that captures nothing: every event is off in it |
| `log.IsHanded` | Whether it was handed a store |
| `log.Scope` | The scope it writes under, or 0 |
| `log.IsEnabled(spec)` | Whether the event is on, which is what the generated method checks first |
| `==`, `Equals` | Whether two handles are to one store and one scope |

A `default` `Log` was never handed a store, and the first event written through it throws `InvalidOperationException`, saying so.
That is on purpose: code given no log fails loudly instead of logging nowhere, and code that wants nothing from a log is handed `Log.Discard`.

There is no static logger, no `Log.Default`, and no store that makes itself.
Specs are static, in the catalog, because they describe the code that was compiled; records are not.

## Rules

`LogRules` say what a store captures: a least level every event is held to, and rules that change it, or limit how often an event is kept, for the events a pattern names.
A store compiles them with the catalog into one byte per event, which is all a call reads to decide it is off.

### From text

```csharp
Result<LogRules> rules = LogRules.Parse("debug; game.save=trace; ai.*=warning; game.input/Moved=off; physics/Contact=10/s");
```

Items are separated by `;`, and spaces around them do not matter.

- A bare level is the least.
  There can be one, and text with none keeps the default, `debug`.
- `pattern=what` is a rule, where what is a level, `off`, a limit as `10/s`, or a level and a limit separated by a comma, as `debug,10/s`.

Levels are `trace`, `debug`, `information`, `warning`, `error`, `critical` and `off`, ignoring case; `info` and `warn` are read too.

| Pattern | Names |
|---|---|
| `*` | Every event |
| `game` | The category `game` |
| `game.*` | `game` and every category under it, as `game.save`, but not `gameplay` |
| `game.save/Saved` | One event, by its key |

Where two patterns match an event, the more specific wins: a key beats a category, a category beats every prefix, a longer prefix beats a shorter one, and every prefix beats `*`.
Of two rules alike, the later wins.
A level and a limit are resolved apart: a rule with only a limit keeps the level a less specific rule gave, and the other way round.

`Parse` fails with an `Error` whose `Code` is a `LogRulesError`:

| Code | When |
|---|---|
| `NotALevel` | A bare item, or what follows `=`, is not a level |
| `NotAPattern` | A pattern is none of the four kinds |
| `NotALimit` | A limit is not a whole number a second, or a rule has two |
| `LeastTwice` | A second bare level |

`rules.ToString()` writes the text `Parse` reads back as the same rules.

### In code

```csharp
var rules = new LogRules(LogLevel.Debug,
    new LogRule("game.save", LogLevel.Trace, F.None),
    new LogRule("physics/Contact", F.None, 10));
```

A `LogRule` is a `Pattern`, an optional `Least` (`LogLevel.None` for off) and an optional `PerSecond`.
`LogRules.Default` is `debug` and no rules: a store captures what a developer would want when something goes wrong, and the sinks, which write from `Information` by default, write the rest only as an error's context.

### Rate limits

A limit is a bucket per event that holds `PerSecond` records and refills at that rate, starting full.
A record that finds the bucket empty is dropped and counted, and the next one kept carries the count as `Suppressed`.
A rule's limit overrides the event's own `PerSecond`, and `0/s` takes the event's limit away.

### Changing them at runtime

```csharp
store.Apply(LogRules.Parse("trace; ai.*=debug").ValueUnsafe());
```

`Apply` swaps the rules from any thread, and calls from then on read the new ones.
It says, as a `log/RuleMatchesNothing` record, each rule whose pattern matches nothing the program declares, which is how a misspelt category is caught.
`store.Rules` is the rules applied last.

`LeastFor(spec)`, `PerSecondFor(spec)` and `MatchingNothing(specs)` answer what the rules say of an event without a store.

## What the log says about itself

The package declares its own events, in category `log`, so its trouble is in the same place as everything else's:

| Key | Level | Says |
|---|---|---|
| `log/DrainFellBehind` | Warning | `{Count} records were overwritten before the drain read them` |
| `log/SinkFailed` | Error | `The sink {Sink} failed, and is not written to again`, with the exception |
| `log/KeyTaken` | Warning | Two assemblies declare one key |
| `log/CategoryTaken` | Warning | Two assemblies declare one category |
| `log/RuleMatchesNothing` | Information | `The rule {Rule} matches no event the program declares` |

They are registered before any other event, and are held to the rules like any other: `log=off` silences them.
