# The Log Console

The log console is an editor window, beside Unity's Console, that reads a program's records whole and typed.
It shows the records a query keeps as rows, with a badge for each level, a tree of the categories, and the details of the row selected: its fields, the scopes it is under, where it was written, and the source around that line.
It reads the sessions a `LogConsoleSink` keeps, and the files a `JsonLinesSink` wrote.

## Setting it up

The console shows what a `LogConsoleSink` keeps, so the composition root adds one to its drain:

```csharp
var drain = new LogDrain(store,
    (new UnityConsoleSink(), new LogSinkOptions(LogLevel.Warning)),
    (new LogConsoleSink(), new LogSinkOptions(LogLevel.Debug)));
```

With the console open, Unity's Console can be raised to `Warning`, so it keeps compiler errors and the warnings and errors that matter, and the log console keeps the rest.
Outside the editor the sink keeps nothing, so the root adds it without an `#if`.
[Sinks](sinks.md#log-console) says what the sink keeps and for how long.

Open the console at **Window > General > Log Console**.
A shortcut that opens it, **Log Console > Open**, is registered with no key, for a developer to bind in **Edit > Shortcuts**.
The menu and the shortcut bring the open console forward, and open one only when none is.

## The window

From the top:

- **The toolbar:** the session shown, **Clear** and its menu, **Follow**, **Pause on Error**, a badge for each level, how many rows there are, and the **⋮** menu.
- **The saved filters:** a button for each filter the team saved, shown once there is one.
- **The query line**, and under it what the console says of the rows: why a query does not read, what it names that no event is or has, and what was lost or left out.
- **The categories**, on the left: a tree of the categories of the session's events, each with its count.
- **The rows**, in columns.
- **The details** of the row selected, under the rows.

The query line and **Follow** are kept across a domain reload and an editor restart.
The session shown, what is folded and what is selected are not.

## Sessions

The session menu, at the left of the toolbar, lists the process's sessions, oldest first, and shows the one chosen.

- A **live** session is one a sink keeps as the program logs.
- An **ended** session is one whose drain let its sink go, as when Play Mode exits.
  It stays until the console is cleared or it is closed, so a Play Mode session can be read after Play Mode exits.
- A **file's** session is one read from a JSON lines file, through **Open a Session File...**, in this menu and in the **⋮** menu.
  It stays until it is closed.

**Close This Session**, in this menu, lets the ended or file's session shown go.
A live session cannot be closed: it ends when its drain lets its sink go.

**Clear** empties the live sessions, which keep what is logged after, and lets the ended ones go.
A file's session stays.
Its menu sets **Clear on Play**, on at first, which clears the console as Play Mode is entered.

**Follow** keeps the newest row in sight, and shows a live session as it starts.
Scrolling up stops it, and scrolling to the newest row follows again.

A domain reload, as when a script compiles or Play Mode is entered with domain reload on, lets every session go.
To read one again, open the file a `JsonLinesSink` wrote of it.

## Choosing what is shown

The query line is a filter query, as [Reading Records](reading.md#filters) describes:

```
level>=warning game.* -game.input.* "no image" /fail(ed|ure)/ Slot:3
```

Each word keeps records, and a word after `-` leaves them out.
Pointing at the line shows a short guide to the words.
The rows follow the query as it is typed.
While a query does not read, the rows stay those of the last one that did, and a line under it says why.

The badges and the categories write the query too, so the line always says what is shown.

- **A level's badge**, `T`, `D`, `I`, `W`, `E` and `C`, counts the records the query keeps but for its levels.
  Clicking one shows only that level, and clicking it again shows every level.
- **A category**, clicked, is shown or hidden with every category under it.
  Ctrl+click (Cmd+click on macOS) shows only it, or every category again, and Shift+click shows every category.
  A category's mark says whether all, some or none of it is shown.

Under the query line the console says:

- why a query does not read, while it does not;
- the events and fields the query names that none of the session's events is or has, since such a word is more often text meant to be in quotes;
- the regular expressions that took too long over a record and match nothing from it on, with **Retry**;
- the lines of a file that could not be read;
- how many records were lost before the console read them;
- what the project always leaves out, with **Edit**, which opens its settings.

## The rows

A row is a record, a run of repeating records, or a scope's opening, under which what it holds is indented.
The rows are in columns:

| Column | What it says | At first |
|---|---|---|
| Time | When the record was written, as `14:03:12.481` | Shown |
| Frame | The frame it was written in | Shown |
| Level | Its level, as `WRN` | Shown |
| Category | Its event's category, in the category's colour | Shown |
| Event | Its event's name, and its key when pointed at | Hidden |
| Message | Its message's first line, with the row's marks | Always shown |
| Duration | How long a scope was open, or `open` | Shown |
| Call Site | The file and line that wrote it, and the whole path when pointed at | Shown |
| Thread | The managed id of the thread that wrote it | Hidden |

Right-clicking the header shows or hides a column, and dragging a column's edge resizes it.
The message is always shown and takes the room the others leave.
The columns are each developer's own, kept in `UserSettings`, and every console window shows the same ones.

The message's cell carries the row's marks:

- `⚠`: records were lost before this row, and pointing at it says how many.
- `▾` or `▸`: a scope's row, open or folded, and clicking it folds or unfolds what is under it.
- `×12`: the row stands for 12 records that repeat.
- `+3`: a rate limit dropped 3 records of the event before this one.

A row an error's backlog brought is drawn fainter, as context.
A row whose records the session has let go since, past its cap, is drawn apart, and its details say so.

A row's call site, double-clicked or with Enter, opens in the IDE at its line.
A message Unity's log handed over has no call site of its own, so it opens at the first frame of its stack that is in the project, and the Call Site column shows that frame.

Right-clicking a row shows:

| Item | What it does |
|---|---|
| **Copy** | Copies the rows selected as text, as Ctrl+C (Cmd+C on macOS) does |
| **Copy as JSON Lines** | Copies them as the lines a `JsonLinesSink` writes, which a session file is made of |
| **Copy, Nothing Left Out** | Copies them as text or JSON lines with personal fields and the home folder kept |
| **Fold** or **Unfold** | Folds the scope selected, or unfolds it |
| **Show Only This Scope** | Keeps only the records of the scope a scope's row opens, or else of the innermost scope the row is under, and those under it |
| **Open** *file:line* | Opens where the row was written |

Copying leaves out what a file that leaves the machine would: personal fields and the home folder in paths.
Copying a row that stands for many records copies every record it stands for.

## The details

Selecting a row shows its record:

- its level, its key, when it was written, in which frame and on which thread;
- its message whole, which can be selected and copied;
- **Fields:** each field and its value, with personal fields marked;
- **Under:** the scopes it is under, the innermost first, and clicking one shows only that scope's records;
- **Written at:** the call site, with three lines of source on each side, and clicking either opens it;
- **Declared at:** where its event was declared;
- **Stack:** the frames of an exception or of a message Unity's log handed over, each opening where it was.

## Exporting

The **⋮** menu's **Export the Rows Shown...** writes every record the rows stand for to a JSON lines file, which **Open a Session File...** reads back.
It leaves out personal fields and the home folder, as copying does.
**Export the Rows Shown, Nothing Left Out...** keeps them.

## Errors not yet seen

The console counts the errors no console window has shown: the records at `Error` or `Critical` that are not an error's backlog.
An error is seen once a visible console window shows its session, whether or not the query keeps it.

- From Unity 6.3, a badge on the editor's main toolbar says the count, while there is one, and pressing it shows the console at the first.
  If the query, a fold or what the project leaves out hides that error, a line under the query line says so, as it does of an error let go before it could be shown.
- Below Unity 6.3, the window's title says the count, as `Log Console (3)`.

**Pause on Error**, in the toolbar, pauses the game when an error arrives while it plays.
It is off at first.

## Settings

What the team shares is kept in `ProjectSettings/TutanLogConsole.json`, committed with the project, and edited on the **Project Settings > Log Console** page, which **Log Console Settings...** in the **⋮** menu opens:

- **Saved filters:** buttons over the query line.
  Pressing one shows the rows of its query, and pressing it again shows what was shown before.
  **+**, or **Save the Query as a Filter...** in the **⋮** menu, saves the query, under a name no other filter has, in its name's hue or a colour chosen for it.
  Right-clicking a button edits it, removes it, or moves it left or right.
- **Category colours:** a category is drawn in the colour chosen for it, and so is each category under it that has none of its own.
  Every other category is drawn in a hue made of its name.
  Right-clicking a category in the tree chooses its colour, or gives it its name's colour again.
- **Always left out:** words after `-` that every view leaves out, after its own query, as `-unity/Logged`.
  The query line does not show them, and a line under it says they are left out.

What each developer keeps is in `UserSettings/TutanLogConsole.json`, which the team does not share: **Clear on Play**, **Pause on Error**, and the columns.

Both files are read again when they change on disk, as when a teammate's change is pulled, and a change is made to the file as read again, so an edit made elsewhere is kept.
An entry that does not read is skipped, and the settings page says which and why.
A file that cannot be read at all is never written over: the console goes by the defaults, and the page says what is wrong with it.
