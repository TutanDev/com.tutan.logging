# Reading Records

A sink writes records out; a reader reads them as rows.
A `LogView` is what a console reads: the records a filter keeps, kept up to date a frame at a time, with repeats counted rather than shown again and scopes drawn as a tree.
It has nothing about drawing in it, so a test, an overlay and an Editor window read it alike.

A view reads a live `LogStore`, or a session held as a `LogArchive`, which `LogFile.Read` makes from the file a `JsonLinesSink` wrote.
It asks both the same questions.

## Records a reader reads

`LogRecords` is a store or an archive, and nothing else.

| Member | What it says |
|---|---|
| `Program`, `Clock` | The program that wrote the records, and when its store started |
| `Events` | The events the records are of: the catalog's for a store, the file's own declarations for an archive |
| `Capacity` | The most records it holds |
| `First`, `Next` | The oldest sequence it holds, and one past the newest |
| `Redacted` | What the records lack, as the sink that wrote them left it out: `None` for a store, the file's for an archive |
| `LateMark`, `LateFrom(seen)` | Where it is in placing records it was handed after a later one, and, as a `LogLate`, the least sequence placed late since the mark `seen` with the mark to pass next time |
| `Read(from, into)` | Copies the records from a sequence on into a `LogBatch`, as a store's does |
| `Read(sequences, into)` | Copies the records of the given sequences it holds |

A store has no late records: its `LateFrom` is always `Next`.
A `LogLateMark` is opaque: a reader keeps the one it was handed and passes it back.
`default`, or a mark of other records, has seen none of these.

## The view

```csharp
LogFilter filter = LogFilter.Parse("level>=warning game.*").ValueUnsafe();
var view = new LogView(store, filter);

// Once a frame:
view.Update();
for (int row = 0; row < view.Count; row++)
{
    LogRow shown = view[row];
    // shown.Sequence, shown.Records, shown.Depth, shown.LostBefore...
}
```

`Update` reads what arrived since it last did, and drops the rows of records the store has since overwritten.
It allocates nothing once its index has grown to the rows.
A view is read and updated on one thread, as a console draws on one.

### Rows

```csharp
public readonly record struct LogRow(long Sequence, long Last, int Records, long Suppressed, LogLevel Level,
    LogRowKind Kind, int Depth, long LostBefore);
```

A row is a record, or a run of records that repeat one another.

- **A repeat** is a record of the same event, level, scope and values as the row before it, and of the same stack when it carries one, and is counted on that row.
  `Records` is how many, `Sequence` and `Last` the first and the last, and `Suppressed` adds up what a rate limit dropped among them.
  A run spans records the filter hides between them, but not a loss, and never joins a record written as an error's backlog to one that was not.
- **A scope's opening** has a row of its own, `LogRowKind.Scope`, and what is under it is drawn under it: `Depth` is how many of the scopes a row is under have rows.
  A scope's closing has no row, since its opening's row says how long it was open (`view.Scopes.Elapsed(scope)`).
- **A loss** is said on the row after it: `LostBefore` is how many records were lost just before that row, and `view.Lost` is all of them.

`Find(sequence)` is the row that holds a record, or the repeat whose run spans it, so a selection held by sequence keeps its row as the rows change.

### Counts

`Counts` is how many records match, by level: everything the filter keeps but for its levels.
So the count of a level is what keeping it would show, which is what a toolbar's level badges say.
`Counts.Total` is all of them.

### A new filter, and folds

Setting `Filter` to a filter not equal to the one it has scans every record held again, about sixteen thousand an update, into an index of its own.
Setting an equal one does nothing.
The rows stay as they were until the scan has caught up, and `IsScanning` says when one is under way.
Until then, what the rows are asked is answered as the filter they were made with, `Shown`, says.

`Fold(scope)` hides what is under a scope and keeps the scope's own row, and `Unfold(scope)` shows it again.
Each scans again, as a new filter does.

`Clear()` starts the view at the newest record, as if nothing came before it.
The records are all still there.

### Copying rows

`Read(row, count, into)` copies the record of each row into a batch, one a row.
`Write(sink, row, count)` writes every record the rows stand for, each of a repeat's too, to a sink that has not been opened, as a drain would.
A console copies rows as text or as JSON lines, or exports what it shows, through it.

```csharp
using var file = new JsonLinesSink(File.Create(path));
view.Write(file, 0, view.Count);
```

Where the records say some were lost among them, or the store has overwritten some since, the sink is told how many.

### Records placed late

An error's backlog reaches a sink after records with later sequences.
An archive places such a record among the others by its sequence, and moves its `LateMark` on.
When the mark has moved, the next `Update` reads the rows again from the first record placed late since, and only those, so the record shows where its sequence puts it.

## Filters

A console's filter line is a query, which `LogFilter.Parse` reads and `ToString` writes back:

```
level>=warning  game.*  -game.input/Moved  Slot:3  frame:1200..1300  scope:4182  "no image"  /fail(ed|ure)/
```

A query is words split by spaces.
Each word is a term, or a term left out when it starts with `-`.

| Term | Keeps |
|---|---|
| `level>=warning` | Records at those levels; `>`, `<=`, `<`, `=` and `:` too, with a level as rules name it |
| `frame:1200..1300` | Records of those frames; `frame:1200..`, `frame:..1300` and `frame:1240` too |
| `scope:4182` | The scope and everything under it, on every thread it reached |
| `thread:7` | What one managed thread wrote |
| `File:*.png` | Records with a field of that name, ignoring case, whose formatted value the pattern matches, ignoring case; `*` is any text and `?` any char |
| `File:/\.(png\|jpg)$/` | Records with a field whose formatted value a regular expression matches, ignoring case |
| `@level:high` | A field named `level`, `frame`, `scope` or `thread`, which without the `@` are the terms above |
| `"no image"` | Records whose message holds the text, ignoring case |
| `/fail(ed\|ure)/` | Records whose message a regular expression matches, ignoring case |
| anything else | Records of an event, as a rule names it: `*`, a category, `category.*`, or a key |

A record is kept when its level and frame are in the ranges, it is of one of the events kept, if any are, it meets every other term kept, and it meets no term left out.
So `game.save game.load` is either category, and `game.* Slot:3` both.

A value with a space in it is in quotes, in which `\"` is a quote and `\\` a backslash.
A regular expression is between slashes, in which `\/` is a slash and every other backslash is the expression's own.
A regular expression ignores case and culture, as quoted text and wildcards do.
A match that takes too long over a record gives up, and the record is not matched, so a pattern that backtracks without end cannot stall the editor.
Over a view's rows, the term then matches nothing from that record on, rather than take as long over every record left: kept, it keeps nothing, and left out, it leaves nothing out.
A record before that one is matched as before, when a record placed late has the rows read again, and a copy of the rows leaves out what the rows do.
`view.GaveUp` says which terms did, in the filter's order, for a console to say under its filter line, and `view.Retry()` scans again to try them afresh.
`LogFilter.Matches` alone remembers nothing, and tries every record afresh.

`Parse` never throws for a query.
It fails with the first word that is not a term, and a `LogFilterError` as the error's code: `NotATerm`, `NotALevel`, `NotAFrame`, `NotAScope`, `NotAThread`, `NoText`, `NoValue`, `QuoteNotClosed`, `CannotLeaveOut`, `RegexNotClosed` or `NotARegex`.

`MatchingNothing(events)` is the events and fields a filter names that no event is or has.
A console says them under its filter line, since a word that is a category no event is of is more often text meant to be in quotes.

A filter is a value: two filters are equal when their levels, frames and terms are, and a view given an equal filter does not scan.
A regular expression is its pattern: `/a\/b/` and a term made from `a/b` are equal, and both write back as `/a\/b/`.
A filter is also made from its parts, `new LogFilter(levels, frames, keep, leaveOut)`, with `LogLevelRange`, `LogFrameRange` and the cases of `LogFilterTerm`.

## Scopes

`view.Scopes` is what is under what, as the records the view has read say.

| Member | What it says |
|---|---|
| `IsUnder(scope, ancestor)` | Whether a scope is the ancestor or under it |
| `Parent(scope)` | The scope it was opened under, or 0 |
| `IsOpen(scope)` | Whether its opening, or a record under it, has been read, and its closing has not |
| `Elapsed(scope)` | How long it was open, once closed |

A scope is the sequence of the record that opened it.
Only the view teaches it, and asking it changes nothing, so a filter asks the same question twice and gets the same answer.

## Reading a session's file

```csharp
Result<LogSessionFile> read = LogFile.Read(path);
read.Match(
    error => Debug.LogWarning(error.Message),
    file =>
    {
        var view = new LogView(file.Archive, LogFilter.All);
        foreach (LogSkippedLine skipped in file.Skipped)
            Debug.LogWarning(skipped.ToString());
    });
```

`LogFile.Read` reads back a log a `JsonLinesSink` wrote, as far as it goes, so a session that is still writing its file is read up to its last whole line.
It opens the file shared, so the session writing it, and the folder keeping its latest few, go on.
`LogFile.Read(stream, name)` reads a stream instead, from where it is to its end, and leaves it open.

It fails, with a `LogFileReadError` as the error's code, only when there is no log to read:

| Code | When |
|---|---|
| `Unreadable` | The file could not be opened or read |
| `NotALog` | Its first line does not say its format, program and start, or runs past 64K characters, which no header does |
| `UnknownFormat` | It is of a format other than 1 |

A line it cannot read is skipped, and the rest is read.
`Skipped` says each such line, by number, with why, and `ToString` says it as a sentence:

| `LogSkipReason` | The line |
|---|---|
| `NotJson` | Is not JSON, as the last line of a file is when the program stopped while writing it |
| `NotALine` | Is JSON, but not a record, a declaration or a loss |
| `Undeclared` | Is a record of an event the file does not declare before it |
| `NotADeclaration` | Declares an event as the format does not write a declaration |
| `NotAnEvent` | Declares what is not an event, such as a template with a hole no field fills |
| `DeclaredTwice` | Declares an event a line before it declares; the first is kept |
| `NotItsDeclaration` | Is a record whose key, kind, level or fields are not as its declaration says |
| `NotAStamp` | Is a record whose sequence, scope, frame, thread or other stamp no record can have |
| `SequenceTwice` | Is a second record of a sequence; the first is kept |
| `LostAtEnd` | Says records were lost after the last record, with no record after to say it before |

### What a file reads back as

The archive's events are the file's declarations, not the catalog's, so it reads a log whatever program wrote it.
They are the file's alone: the catalog never holds one, a store's `IsEnabled` says no to one, and writing one throws.
A record keeps its sequence, its stamps, its call site, its time and its stack, frame for frame.
A `stack` that is not an array of one frame or more, each with a `method`, and a `file` and a `line` together when it has either, is a stamp no record can have, and so is a stack on a scope's closing.
A field reads back as the kind it was declared as, but for three the file holds only as text, which read back as strings:

- a `char` or `ReadOnlySpan<char>`,
- a value of the program's own type, such as a `Vector3`,
- a field marked `[Personal]` in a file written with personal fields left out, which reads `[personal]`.

An exception reads back as an `ArchivedException`: its `TypeName` is the full name it was thrown as, its `Message` is its message, and its `ToString()` is the text the file holds, stack trace and all.

The sequences have holes where the sink skipped records below its level.
A read says records were lost only where the file says so.

The archive holds what the file held, and takes nothing after.
Its `Redacted` is what the file's sink left out, and a view writing its rows to another sink leaves that out too, whatever the sink's own options, since the records no longer have it.
