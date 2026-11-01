# Generator Diagnostics

The generator refuses a declaration it cannot write for with a compile error, and writes nothing for that class until it is fixed.
Every diagnostic is an error, in category `Logging`, reported at the declaration, parameter or attribute at fault.

| Id | Title |
|---|---|
| [TLOG0001](#tlog0001) | A hole in an event's template names no parameter |
| [TLOG0002](#tlog0002) | An event's parameter is of a type a field cannot hold |
| [TLOG0003](#tlog0003) | An event's declaration or class is not of the shape the generator writes for |
| [TLOG0004](#tlog0004) | Two events have one key |
| [TLOG0005](#tlog0005) | A category or a name is malformed |
| [TLOG0006](#tlog0006) | An event's level or limit is malformed |
| [TLOG0007](#tlog0007) | An event's template does not parse |
| [TLOG0008](#tlog0008) | An event is declared in a class with no `[LogCategory]` |
| [TLOG0009](#tlog0009) | Two classes declare one category |
| [TLOG0010](#tlog0010) | An event has more fields than a record holds |

## TLOG0001

A hole in the template names no parameter of the declaration.
Holes match parameters ignoring case, so `{Slot}` matches `slot`.

```csharp
[LogEvent(LogLevel.Information, "Saved slot {Slot}")]
static partial void Saved(int index);   // TLOG0001: no parameter named Slot
```

## TLOG0002

A parameter's type is not one a field can hold.
A field is a bool, char, number, decimal, `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan`, enum, `string`, `Exception`, `ReadOnlySpan<char>`, an unmanaged struct that implements `IFormattable` (as `Vector3`), or an `Optional<T>` or nullable of one of these but `ReadOnlySpan<char>`.

A value is copied into the store when it is logged, so a reader sees it as it was.
A reference to anything but a string, which cannot change, or an exception would be read as it is when it is read.
Log what the program needs of an object, as its name or id, instead of the object.

A struct bigger than 256 bytes (`LogField.MaxValueBytes`) is not caught here, since its size is known only to Unity: making its spec throws `ArgumentException` when the class's events are registered.

## TLOG0003

The declaration or its class is not of the shape the generator writes for.
The message says which rule it breaks:

- The class is nested, is not static, is generic, or is not partial.
- The method is not a `static partial` method, has a body, has an access modifier, is generic, or returns something other than `void`.
- The method is named `Events` or `RegisterLogEvents`, which the generator writes into the class.
- A parameter is passed by `ref`, `out` or `in`, has a default, is `params`, or is a `Log`, which the generator adds.

```csharp
[LogEvent(LogLevel.Information, "Saved")]
public static partial void Saved();   // TLOG0003: an access modifier makes C# require a body
```

## TLOG0004

Two events of one class have one key: the same method name, or the same `Name` given in their attributes.
A key names one event, which rules, files and the catalog rely on.
Give one of them a `Name` of its own.

## TLOG0005

A category, an event's name or a field's name is malformed.

- A category is lowercase words of letters and digits, each starting with a letter, joined by dots, as `game.save`.
- An event's name, and a field's, is an ASCII letter or `_` followed by letters, digits and `_`, and an event's name is one the generator can write as a member of the `Events` class.
- Two parameters whose names differ only in case would be one field, since holes match ignoring case.

## TLOG0006

An event's level or limit is malformed.

- An event declared at `LogLevel.None`, which is a threshold and not a level a record has.
- A negative `PerSecond`.
- An attribute that gives a level and a `LogLevel` parameter as well.
- An attribute that gives no level and no `LogLevel` parameter, or two `LogLevel` parameters.

## TLOG0007

The template does not parse, as when a brace is not closed or a hole's alignment is not a number.
A literal brace is written `{{` or `}}`.

## TLOG0008

An event is declared in a class with no `[LogCategory]`, so it has no key.

## TLOG0009

Two classes of one assembly declare one category.
A category is one class's, so its events are listed in one place.
Two assemblies declaring one category is caught at runtime instead, as a `LogCatalogConflict.CategoryTwice` and a `log/CategoryTaken` record.

## TLOG0010

An event has more than 32 fields, which is more than a record holds.
