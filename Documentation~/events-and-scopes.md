# Events and Scopes

An event is something a program says: a save written, a level loaded, a connection lost.
A scope is an operation with a beginning and an end, and the records written while it is open are under it.
Both are declared once, and the generator writes the methods a program calls.

## The category class

```csharp
[LogCategory("game.save")]
internal static partial class SaveLog
{
    // declarations
}
```

- The class is top-level, `static`, `partial` and not generic, because what the generator writes beside its declarations are extension methods of `Log`.
- `[LogCategory]` names the category of every event the class declares: one or more lowercase words of letters and digits, each starting with a letter, joined by dots, as `game.save`.
- A category is one class's.
  Two classes of one assembly declaring one category is a compile error (TLOG0009), and two assemblies doing it is a conflict the catalog reports at runtime.
- The class's own accessibility holds the generated methods: an `internal` class keeps its events to its assembly.

## Declaring an event

```csharp
[LogEvent(LogLevel.Information, "Saved slot {Slot} in {Elapsed}")]
static partial void Saved(int slot, TimeSpan elapsed);
```

A declaration is a `static partial void` method with no body and no access modifier.
It is a signature the generator reads: C# allows a partial method without a body only when it has no access modifier and returns `void`.
Its parameters are the event's fields, in the order the call writes them.
A parameter is passed by value, with no default and no `params`, and a declaration never takes the `Log`, which the generator adds.
The names `Events` and `RegisterLogEvents` are the generator's own, and a declaration cannot use them.

### The key

An event's key is its category and its name, `game.save/Saved`, which is what rules, files and the catalog name it by.
The name is the method's, unless the attribute gives one, which keeps the key when the method is renamed:

```csharp
[LogEvent(LogLevel.Information, "Struck", Name = "Struck")]
static partial void Strike();
```

A name is an ASCII letter or `_`, followed by letters, digits and `_`.
Two events of one class with one key is a compile error (TLOG0004).

### The level

`LogLevel` runs `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`.
`None` is a threshold that nothing passes, and an event cannot be declared at it.

Most events have one level, given by the attribute.
An event whose level each call chooses, as for messages the program hears from elsewhere, leaves the level out of the attribute and takes exactly one `LogLevel` parameter:

```csharp
[LogEvent("Heard {Message}")]
static partial void Heard(LogLevel level, string message);

log.Heard(LogLevel.Warning, "careful");
```

The `LogLevel` parameter is not a field.

### A rate limit

```csharp
[LogEvent(LogLevel.Warning, "No path to {Target}", PerSecond = 5)]
static partial void NoPath(Vector3 target);
```

`PerSecond` is how many records of the event a store keeps a second, at most.
The rest are counted, not kept, and the next record kept says how many were dropped before it.
It answers a warning that fires every frame.
0, the default, is no limit, and a rule can override either way (see [Store and Rules](store-and-rules.md)).

## Fields

| Declared as | Held as | Formatted as |
|---|---|---|
| `bool` | copied | `true` or `false` |
| `char` | copied | the char |
| `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong` | copied | the number, invariant culture |
| `float`, `double`, `decimal` | copied | the number, invariant culture |
| `Guid` | copied | `D` by default |
| `DateTime`, `DateTimeOffset` | copied | ISO 8601 (`O`) by default |
| `TimeSpan` | copied | `c` by default |
| an enum | copied as a `long` | its name; a `[Flags]` value as names joined by ` \| `; `D` or `X` for the number |
| `string` | by reference | the string; `null` when null |
| `Exception` | by reference | its type's name and its message |
| `ReadOnlySpan<char>` | copied, at most 1024 chars | the chars, ending in `...` when cut |
| an unmanaged struct implementing `IFormattable`, as `Vector3` | copied, at most 256 bytes, checked at registration | its own `ToString(format, InvariantCulture)` |
| `Optional<T>` or `T?` of any of these but spans | copied, with a presence flag | the value, or `none` |

A value is copied into the store when it is logged, so a reader sees it as it was.
A reference to anything but a string, which cannot change, or an exception would be read as it is when it is read, so other reference types are refused (TLOG0002).
Formatting a struct of the program's own type allocates the string its `ToString` returns, when a reader formats it, never at the call.
An event has at most 32 fields (TLOG0010).

### Field names

A field is named as the template's hole spells it, or, when no hole names it, as its parameter with the first letter made upper case.
So `Saved(int slot, string file)` with the template `Saved slot {Slot}` has the fields `Slot` and `File`.
Two parameters whose names differ only in case are refused, since a hole matches its parameter ignoring case.

### Personal fields and tags

```csharp
[LogEvent(LogLevel.Information, "Signed in as {Email}")]
static partial void SignedIn([Personal] string email, [LogTag("path")] string folder);
```

- `[Personal]` marks a field that can name a person.
  The store keeps it, and a sink whose options redact personal fields writes `[personal]` instead.
- `[LogTag]` says what a field is beyond its type, for a reader that offers to act on it: a console that turns a field tagged `asset` into a button that pings the asset.

## Templates

A template is the message, with holes that name fields:

| Hole | Writes |
|---|---|
| `{Slot}` | the field `Slot` |
| `{Slot,6}` | padded on the left to 6 chars |
| `{Name,-6}` | padded on the right to 6 chars |
| `{Ratio:0.00}` | with the format handed to the value's own formatting |
| `{{` and `}}` | a literal brace |

A hole names a parameter, ignoring case, or the declaration is refused (TLOG0001).
A template that does not parse, as an unclosed brace, is refused too (TLOG0007).
A parameter no hole names is still a field: it is in the record, the file and the tests, just not in the message.
Formatting is in the invariant culture, so a message reads the same on every machine.

## Scopes

```csharp
[LogScope(LogLevel.Information, "Saving slot {Slot}")]
static partial void Saving(int slot);
```

A scope is declared as an event is, with `[LogScope]`, which always has a level.
The method the generator writes returns a `LogScope`:

```csharp
using LogScope saving = log.Saving(slot);
saving.Log.Saved(slot, elapsed);   // under "Saving slot 3"
```

- Opening it writes a record with its fields, whose sequence becomes the scope's `Id`.
- What is written through `saving.Log` is under the scope, and nothing else is, on any thread.
  There is no ambient state, so nothing is lost across an `await` or a UniTask continuation.
- Disposing it writes the record that closes it, which says how long the scope was open.
  Disposing it twice, or disposing a copy, closes it once.
- Scopes nest: a scope opened through `saving.Log` is under `saving`.
- A scope that is off, because its event is off or a limit dropped it, has `IsOn` false and hands back the `Log` it was opened from.
  Disposing it does nothing.

Work that outlives the call that opened the scope is handed `saving.Log`, and whoever finishes the work disposes `saving`, so the closing says how long the whole took.
A record written through the `Log` of a scope that has closed is still under that scope: the handle says which scope, and closing it cannot reach the copies.

## What the generator writes

For each class, beside its declarations:

- One `public static` extension method of `Log` per declaration, with the declaration's parameters and two caller-info parameters:

  ```csharp
  public static void Saved(this Log log, int slot, TimeSpan elapsed,
      [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0);
  ```

  It checks whether the event is on, which is one compare, and if it is, copies the values into the store.
  It carries a doc comment pointing back at the declaration, since going to the definition of a call lands in generated code.
- A nested `public static class Events` with one `LogEventSpec` field per declaration, named by the event: `SaveLog.Events.Saved`.
  A test compares a record's `Event` with it.
- A registration method, run by `[InitializeOnLoadMethod]` in the editor and by `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` in a player, so the catalog lists every event before any of them fires.

Caller-info parameters are optional parameters, and a partial method takes its parameter list from the declaration, so the generator cannot add them to it.
That is why the generated method is the declaration's sibling, not its body.
Generated parameters that would collide with a field are renamed, and C# keywords used as parameter names are escaped with `@`.

The generator runs on every assembly that references `Tutan.Logging`.
A declaration it cannot write for is a compile error, listed in [Generator Diagnostics](diagnostics.md).

## The catalog

`LogCatalog` lists every event and scope the process can say, filled as assemblies load.
It is static because specs are metadata, as types are, and nothing is written through it.

| Member | What it is |
|---|---|
| `LogCatalog.Events` | Every spec registered, in the order of their indices |
| `LogCatalog.Count` | How many are registered |
| `LogCatalog.Find(key)` | The spec with a key, as an `Optional<LogEventSpec>` |
| `LogCatalog.Conflicts` | What two assemblies declared alike |

Registering is idempotent, so registration can run on every entry to Play Mode, which it has to when domain reload is off, and still list each event once.
A spec made again by the same class, after a domain reload, replaces the one with its key.

Two assemblies can declare what no compiler could catch:

- `LogCatalogConflict.KeyTwice`: two events with one key.
  Both get an index and both are written, but `Find` keeps finding the first.
- `LogCatalogConflict.CategoryTwice`: one category declared by two classes.

Every store also says each conflict as a record (`log/KeyTaken`, `log/CategoryTaken`), so it reaches the Console and the file.

A `LogEventSpec` says everything known about an event before it is written: `Category`, `Name`, `Key`, `Kind`, `Level` (none when each call chooses), `Template`, `Fields`, `PerSecond`, and `Source`, the assembly, class, file and line of the declaration.
