using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>A session's log, as a JSON lines file in a folder that keeps the latest few.</summary>
    public static class LogFile
    {
        /// <summary>The extension of a session's file.</summary>
        public const string Extension = ".jsonl";

        // When a session's store started, as its file's name ends: "{program}-{stamp}.jsonl".
        private const string SessionStamp = "yyyyMMdd-HHmmss-fff";

        // An export is written beside its file under this extension, and moved over it once whole.
        private const string Partial = ".partial";

        // The file an export replaces is moved aside under this extension until the export is in its place.
        private const string Replaced = ".replaced";

        /// <summary>
        /// The <c>Logs</c> folder of <c>Application.persistentDataPath</c>, where
        /// <see cref="Session(LogStore, int)"/> keeps a program's files: the folder a player build can always write.
        /// </summary>
        public static string Folder => Path.Combine(UnityEngine.Application.persistentDataPath, "Logs");

        /// <summary>Opens a file for this session in <see cref="Folder"/>, as <see cref="Session(LogStore, string, int)"/> does.</summary>
        public static Result<JsonLinesSink> Session(LogStore store, int keep = 10) => Session(store, Folder, keep);

        /// <summary>
        /// Opens a file for this session in <paramref name="folder"/>, which it makes if it has to,
        /// named for the program and the time the store started, as
        /// <c>Sample Game-20261101-102409-000.jsonl</c>; then deletes the program's oldest files beyond
        /// <paramref name="keep"/>, this one counted. A file that cannot be deleted is left. Fails with
        /// <see cref="LogFileError"/> as the error's code when the folder or the file cannot be made.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="keep"/> is less than 1.</exception>
        public static Result<JsonLinesSink> Session(LogStore store, string folder, int keep = 10)
        {
            if (store is null)
                throw new ArgumentNullException(nameof(store));
            if (folder is null)
                throw new ArgumentNullException(nameof(folder));
            if (keep < 1)
                throw new ArgumentOutOfRangeException(nameof(keep), $"A folder of logs keeps at least this session's, and {keep.ToString(CultureInfo.InvariantCulture)} were asked for.");

            string program = FileName(store.Program);
            string name = $"{program}-{store.Clock.Started.ToString(SessionStamp, CultureInfo.InvariantCulture)}{Extension}";

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return F.Error($"The folder {folder} could not be made: {exception.Message}", (int)LogFileError.NoFolder);
            }

            string path = Path.Combine(folder, name);
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return F.Error($"The file {path} could not be made: {exception.Message}", (int)LogFileError.NoFile);
            }

            DeleteOldest(folder, program, name, keep);
            return new JsonLinesSink(stream, leaveOpen: false, name: path);
        }

        /// <summary>
        /// The name to offer for an export of <paramref name="program"/>'s records made at
        /// <paramref name="at"/>, as <c>Sample Game export 20261104-093015.jsonl</c>. It is never the
        /// name of a session's file, so <see cref="Session(LogStore, string, int)"/> does not count an
        /// export kept beside them, and does not delete it as one of the oldest.
        /// </summary>
        /// <param name="program">The program the records are of, with every char a file's name cannot have written as <c>_</c>.</param>
        /// <param name="at">When the export was made, written at its own offset.</param>
        public static string ExportName(string program, DateTimeOffset at)
        {
            if (program is null)
                throw new ArgumentNullException(nameof(program));

            // A space after the program, where a session's file has a dash and a stamp to the millisecond.
            return $"{FileName(program)} export {at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{Extension}";
        }

        /// <summary>
        /// Writes every row of <paramref name="view"/> as JSON lines to <paramref name="path"/>, a file
        /// <see cref="Read"/> reads back, as a console exports the rows it shows. It is written beside the
        /// path and moved to it only once whole, so an export is never read back cut short, and one that
        /// fails leaves the file that was at the path as it was, unless a replace that failed half way could
        /// not put it back. Fails with <see cref="LogExportError.NotWritten"/> as the error's code when it
        /// cannot be written, and with <see cref="LogExportError.NotPutBack"/> when a replace failed half way
        /// and the file that was there could not be put back: it is then beside the path, with
        /// <c>.replaced</c> after its name, or <c>.replaced-2</c> and on when that name was taken, as the
        /// error says. Fails with <see cref="LogExportError.LeftBeside"/> when a replace that failed half way
        /// left the file at the path and one beside it under such a name too, which is kept and named in the
        /// error, as it may be the only copy of the file that was there. A failure deletes what it wrote, and
        /// its error says where that is when it cannot. A file written over that cannot be deleted once
        /// replaced stays beside the path under such a name too, as <see cref="LogExported.NotDeleted"/> says.
        /// </summary>
        /// <param name="view">The rows, each written with every record it stands for.</param>
        /// <param name="path">The file to write, written over when it is there.</param>
        /// <param name="options">What it is written with: what it leaves out, as <see cref="LogSinkOptions.Redact"/> says.</param>
        public static Result<LogExported> Export(LogView view, string path, LogSinkOptions options) =>
            Export(view, path, options, Replace, Delete);

        /// <summary>
        /// <see cref="Export(LogView, string, LogSinkOptions)"/>, replacing a file with <paramref name="replace"/>
        /// and deleting one with <paramref name="delete"/>, so a test can fail each as a disk does.
        /// </summary>
        /// <param name="view">The rows, each written with every record it stands for.</param>
        /// <param name="path">The file to write, written over when it is there.</param>
        /// <param name="options">What it is written with.</param>
        /// <param name="replace">What replaces the file at the path: <see cref="Replace"/>, but in a test.</param>
        /// <param name="delete">What deletes a file: <see cref="Delete"/>, but in a test.</param>
        internal static Result<LogExported> Export(LogView view, string path, LogSinkOptions options, ReplaceFile replace, DeleteFile delete)
        {
            if (view is null)
                throw new ArgumentNullException(nameof(view));
            if (path is null)
                throw new ArgumentNullException(nameof(path));
            if (options is null)
                throw new ArgumentNullException(nameof(options));
            if (replace is null)
                throw new ArgumentNullException(nameof(replace));
            if (delete is null)
                throw new ArgumentNullException(nameof(delete));

            string partial = path + Partial;
            FileStream stream;
            try
            {
                stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // The path names no file that can be written, as one in a folder that is not there: nothing
                // was written, so nothing is left to delete.
                return NotWritten(path, exception, partial, F.Success());
            }

            string replaced;
            try
            {
                using (stream)
                using (var sink = new JsonLinesSink(stream, leaveOpen: true, name: path))
                    view.Write(sink, 0, view.Count, options);

                if (!File.Exists(path))
                {
                    File.Move(partial, path);
                    return new LogExported(F.None);
                }

                // Moved aside, not deleted, under a name no file has: a replace that fails half way, as when a
                // scanner holds the file just written, then leaves the file it replaced under that name, never
                // under none, and no replace writes over a file an earlier export left aside.
                replaced = Unused(path + Replaced);
                try
                {
                    replace(partial, path, replaced);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Result<Unit> back = PutBack(replaced, path);
                    Result<Unit> gone = delete(partial);
                    if (back.IsError)
                        return NotPutBack(path, replaced, exception, back.ErrorUnsafe(), partial, gone);

                    // Nothing moved aside is left to put back, so a file at the name no file had is one the
                    // replace left too, which may be the only copy of what was at the path.
                    return File.Exists(replaced)
                        ? LeftBeside(path, exception, partial, gone, replaced)
                        : NotWritten(path, exception, partial, gone);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The disk, not the rows: as when it filled, or the file named is open elsewhere. The streams
                // are closed by now, so what was written can be deleted, and is said when it cannot be.
                Result<Unit> gone = delete(partial);
                return NotWritten(path, exception, partial, gone);
            }
            catch
            {
                // A bug, as when writing the rows threw: what was written is let go of, and the bug goes on up.
                _ = delete(partial);
                throw;
            }

            // In place, so nothing after this is a failure to write it. The file it replaced, when it cannot
            // be deleted, as when another program holds it, is said, never left unsaid: it may hold what this
            // export leaves out.
            Result<Unit> deleted = delete(replaced);
            return new LogExported(deleted.IsError ? F.Some(new LogNotDeleted(replaced, deleted.ErrorUnsafe().Message)) : F.None);
        }

        /// <summary>
        /// The names of <paramref name="names"/> a session of <paramref name="program"/> deletes to keep
        /// <paramref name="keep"/> files, its own, <paramref name="current"/>, among them: the program's
        /// session files but its own, from the newest, after the newest <c>keep - 1</c>. Its own is never
        /// among them, even when the clock was set back since an older one was named, as after a change to
        /// winter time.
        /// </summary>
        internal static IReadOnlyList<string> Pruned(IEnumerable<string> names, string program, string current, int keep) =>
            Array.AsReadOnly(names
                .Where(name => !string.Equals(name, current, StringComparison.Ordinal) && IsSessionFile(name, program))
                .OrderByDescending(static name => name, StringComparer.Ordinal)
                .Skip(keep - 1)
                .ToArray());

        /// <summary>
        /// Puts back at <paramref name="path"/> the file a replace that failed moved aside to
        /// <paramref name="replaced"/>: a success when nothing moved aside is left to put back, as the file is
        /// at the path or nothing is at <paramref name="replaced"/>, or why it could not be put back, under
        /// the name it was moved aside to.
        /// </summary>
        private static Result<Unit> PutBack(string replaced, string path)
        {
            if (File.Exists(path) || !File.Exists(replaced))
                return F.Success();

            try
            {
                File.Move(replaced, path);
                return F.Success();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return F.Error(exception.Message);
            }
        }

        private static Error NotWritten(string path, Exception exception, string partial, Result<Unit> gone) =>
            F.Error($"The rows could not be exported to {path}: {LogText.Closed(exception.Message)}{StillThere(partial, gone)}",
                (int)LogExportError.NotWritten);

        private static Error LeftBeside(string path, Exception exception, string partial, Result<Unit> gone, string beside) =>
            F.Error($"The rows could not be exported to {path}: {LogText.Closed(exception.Message)}{StillThere(partial, gone)} The replace left {beside} beside it, which may be the only copy of the file that was there.",
                (int)LogExportError.LeftBeside);

        private static Error NotPutBack(string path, string replaced, Exception exception, Error stuck, string partial, Result<Unit> gone) =>
            F.Error($"The rows could not be exported to {path}: {LogText.Closed(exception.Message)} The file that was there is now {replaced}, and could not be put back: {LogText.Closed(stuck.Message)}{StillThere(partial, gone)}",
                (int)LogExportError.NotPutBack);

        /// <summary>What a failed export says of what it wrote, <paramref name="partial"/>: where it is and why, when it is not gone.</summary>
        private static string StillThere(string partial, Result<Unit> gone) =>
            gone.Match(partial,
                static (error, file) => $" What was written could not be deleted, and is {file}: {LogText.Closed(error.Message)}",
                static (_, _) => "");

        /// <summary><paramref name="name"/>, or the first of <c>name-2</c>, <c>name-3</c> and on that no file has.</summary>
        internal static string Unused(string name)
        {
            string unused = name;
            for (int i = 2; File.Exists(unused) || Directory.Exists(unused); i++)
                unused = name + "-" + i.ToString(CultureInfo.InvariantCulture);
            return unused;
        }

        /// <summary>
        /// Reads back the log a <see cref="JsonLinesSink"/> wrote to <paramref name="path"/>, as far as it
        /// goes: a session that is still writing it is read up to its last whole line. A line it cannot
        /// read is skipped and said in <see cref="LogSessionFile.Skipped"/>. Fails with
        /// <see cref="LogFileReadError"/> as the error's code when the file cannot be read, or is not a
        /// log of the format this reads.
        /// </summary>
        public static Result<LogSessionFile> Read(string path)
        {
            if (path is null)
                throw new ArgumentNullException(nameof(path));

            FileStream stream;
            try
            {
                // Shared for writing and deleting, so a session that writes it, or a folder that keeps the latest few, goes on.
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Unreadable(path, exception);
            }

            using (stream)
                return Read(stream, path);
        }

        /// <summary>
        /// Reads back the log a <see cref="JsonLinesSink"/> wrote to <paramref name="stream"/>, from where
        /// it is to its end, which it leaves open; <paramref name="name"/> says what it is in the errors
        /// and in <see cref="LogSessionFile.Path"/>. As <see cref="Read(string)"/> does otherwise.
        /// </summary>
        public static Result<LogSessionFile> Read(Stream stream, string name)
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));
            if (name is null)
                throw new ArgumentNullException(nameof(name));

            try
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
                return ArchiveReader.Read(name, reader);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or NotSupportedException)
            {
                return Unreadable(name, exception);
            }
        }

        private static Error Unreadable(string file, Exception exception) =>
            F.Error($"The log {file} could not be read: {exception.Message}", (int)LogFileReadError.Unreadable);

        /// <summary><paramref name="program"/> with every char a file's name cannot have made a <c>_</c>.</summary>
        private static string FileName(string program)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var name = new StringBuilder(program.Length);
            foreach (char c in program)
                name.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            return name.Length == 0 ? "Program" : name.ToString();
        }

        /// <summary>
        /// Whether the file <paramref name="name"/> is a session's file of <paramref name="program"/>,
        /// <c>{program}-{stamp}.jsonl</c>: not an export, nor another program's whose name goes on after a dash.
        /// </summary>
        private static bool IsSessionFile(string name, string program) =>
            name.Length == program.Length + 1 + SessionStamp.Length + Extension.Length
            && name.StartsWith(program + "-", StringComparison.Ordinal)
            && name.EndsWith(Extension, StringComparison.Ordinal)
            && DateTime.TryParseExact(name.Substring(program.Length + 1, SessionStamp.Length), SessionStamp, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);

        /// <summary>
        /// Puts <paramref name="partial"/> at <paramref name="path"/>, moving the file there to
        /// <paramref name="replaced"/>, as <see cref="Replace"/> does.
        /// </summary>
        internal delegate void ReplaceFile(string partial, string path, string replaced);

        /// <summary>
        /// Deletes the file at <paramref name="path"/>: a success once it is gone, or why it could not be,
        /// as <see cref="Delete"/> does.
        /// </summary>
        internal delegate Result<Unit> DeleteFile(string path);

        /// <summary>
        /// Puts <paramref name="partial"/> at <paramref name="path"/>, moving the file there to
        /// <paramref name="replaced"/>: how <see cref="Export(LogView, string, LogSinkOptions)"/> replaces a file.
        /// </summary>
        internal static void Replace(string partial, string path, string replaced) =>
            File.Replace(partial, path, replaced, ignoreMetadataErrors: true);

        /// <summary>
        /// Deletes the file at <paramref name="path"/>: a success once it is gone, or why it could not be, as
        /// when another program holds it.
        /// </summary>
        internal static Result<Unit> Delete(string path)
        {
            try
            {
                File.Delete(path);
                return F.Success();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return F.Error(exception.Message);
            }
        }

        /// <summary>Deletes the files in <paramref name="folder"/> that <see cref="Pruned"/> says to.</summary>
        private static void DeleteOldest(string folder, string program, string current, int keep)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, $"{program}-*{Extension}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (string name in Pruned(Array.ConvertAll(files, static file => Path.GetFileName(file)), program, current, keep))
            {
                try
                {
                    File.Delete(Path.Combine(folder, name));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Another session that still has it open; the next one will try again.
                }
            }
        }
    }

    /// <summary>Why <see cref="LogFile.Session(LogStore, string, int)"/> failed: the <see cref="Error.Code"/> of its error.</summary>
    public enum LogFileError
    {
        /// <summary>The folder could not be made.</summary>
        NoFolder = 1,

        /// <summary>The file could not be made.</summary>
        NoFile,
    }

    /// <summary>
    /// Why <see cref="LogFile.Export(LogView, string, LogSinkOptions)"/> failed: the <see cref="Error.Code"/>
    /// of its error. Either way, what it wrote is deleted, or the error says where it is.
    /// </summary>
    public enum LogExportError
    {
        /// <summary>
        /// An export could not be written whole, and the file it was to write over, when there was one, was
        /// left as it was, at its path, with nothing beside it.
        /// </summary>
        NotWritten = 1,

        /// <summary>
        /// An export could not be written whole, and the file it was to write over, which a replace that
        /// failed half way moved aside, could not be put back: it is beside its path, under the name the
        /// error says.
        /// </summary>
        NotPutBack,

        /// <summary>
        /// An export could not be written whole, and the file it was to write over is at its path, but the
        /// replace that failed half way also left a file beside it, under the name it moves aside to, as the
        /// error says. That file is kept: it may be the only copy of the file that was there.
        /// </summary>
        LeftBeside,
    }

    /// <summary>What a <see cref="LogFile.Export(LogView, string, LogSinkOptions)"/> that was written left beside its path.</summary>
    public sealed record LogExported
    {
        internal LogExported(Optional<LogNotDeleted> notDeleted) => NotDeleted = notDeleted;

        /// <summary>
        /// The file the export wrote over, when it could not be deleted once replaced, as when another program
        /// holds it: beside the path, where and why as this says. It may hold what the export leaves out.
        /// </summary>
        public Optional<LogNotDeleted> NotDeleted { get; }
    }

    /// <summary>A file an export wrote over and could not delete once replaced: where it is, and why.</summary>
    public sealed record LogNotDeleted
    {
        internal LogNotDeleted(string path, string reason)
        {
            Path = path;
            Reason = LogText.Closed(reason);
        }

        /// <summary>Where the file is: beside the export's path, as <c>.replaced</c>, or <c>.replaced-2</c> and on.</summary>
        public string Path { get; }

        /// <summary>Why it could not be deleted, in the disk's own words, closed, as when another program holds it.</summary>
        public string Reason { get; }
    }
}
