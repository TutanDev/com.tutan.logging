using System;
using System.IO;
using System.Text;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>A session's log, as a JSON lines file in a folder that keeps the latest few.</summary>
    public static class LogFile
    {
        /// <summary>The extension of a session's file.</summary>
        public const string Extension = ".jsonl";

        /// <summary>
        /// Opens a file for this session in the <c>Logs</c> folder of <c>Application.persistentDataPath</c>,
        /// as <see cref="Session(LogStore, string, int)"/> does: the folder a player build can always write.
        /// </summary>
        public static Result<JsonLinesSink> Session(LogStore store, int keep = 10) =>
            Session(store, Path.Combine(UnityEngine.Application.persistentDataPath, "Logs"), keep);

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
                throw new ArgumentOutOfRangeException(nameof(keep), $"A folder of logs keeps at least this session's, and {keep} were asked for.");

            string program = FileName(store.Program);
            string name = $"{program}-{store.Clock.Started.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture)}{Extension}";

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

            DeleteOldest(folder, program, keep);
            return new JsonLinesSink(stream, leaveOpen: false, name: path);
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

        private static void DeleteOldest(string folder, string program, int keep)
        {
            string[] older;
            try
            {
                older = Directory.GetFiles(folder, $"{program}-*{Extension}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            // The names end in the time each session started, so their order is the sessions'.
            Array.Sort(older, StringComparer.Ordinal);
            for (int i = 0; i < older.Length - keep; i++)
            {
                try
                {
                    File.Delete(older[i]);
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
}
