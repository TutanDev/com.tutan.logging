using System;
using System.IO;
using System.Text;
using Tutan.Functional;
using UnityEditor;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// The console's two settings files, as the editor has them: read when the editor loads, after a
    /// domain reload, and when the editor has focus again after a file changed on disk, as after a
    /// pull; written whole beside its path and moved into place on each change. An imperative shell,
    /// one per editor, as deviation 10 of the profile says: what it reads is a value, a
    /// <see cref="SettingsRead{T}"/>, and every change is a pure function of the settings it hands.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsoleSettings
    {
        /// <summary>What a team shares, committed with the project.</summary>
        public const string SharedPath = "ProjectSettings/TutanLogConsole.json";

        /// <summary>What one developer keeps, which source control leaves out.</summary>
        public const string PersonalPath = "UserSettings/TutanLogConsole.json";

        private static Held<SharedSettings> s_shared;
        private static Held<PersonalSettings> s_personal;

        static ConsoleSettings()
        {
            s_shared = Load(SharedPath, SharedSettingsFile.Read, SharedSettings.None);
            s_personal = Load(PersonalPath, PersonalSettingsFile.Read, PersonalSettings.Default);
            EditorApplication.focusChanged += static focused =>
            {
                if (focused)
                    Reload();
            };
        }

        /// <summary>Raised on the main thread after the settings changed: a change made here, or a file changed on disk.</summary>
        public static event Action? Changed;

        /// <summary>What the team shares: nothing saved while its file cannot be read.</summary>
        public static SharedSettings Shared => s_shared.Settings;

        /// <summary>What this developer keeps: the defaults while their file cannot be read.</summary>
        public static PersonalSettings Personal => s_personal.Settings;

        /// <summary>How the shared file read: with the entries it skipped, or why it cannot be read at all.</summary>
        public static SettingsRead<SharedSettings> SharedRead => s_shared.Read;

        /// <summary>How the personal file read.</summary>
        public static SettingsRead<PersonalSettings> PersonalRead => s_personal.Read;

        /// <summary>
        /// Changes what the team shares, as <paramref name="change"/> makes the next settings of these,
        /// and writes them: refused as <see cref="SettingsError.Unwritable"/> while the file cannot be
        /// read, as the change refuses, or as <see cref="SettingsError.NotWritten"/> when the file
        /// cannot be written, which leaves the settings as they were.
        /// </summary>
        public static Result<SharedSettings> ChangeShared(Func<SharedSettings, Result<SharedSettings>> change)
        {
            if (change is null)
                throw new ArgumentNullException(nameof(change));

            return Change(ref s_shared, SharedPath, SharedSettingsFile.Read, SharedSettings.None, change,
                static settings => SharedSettingsFile.FromDomain(settings).ToJson());
        }

        /// <summary>
        /// Changes what this developer keeps, as <paramref name="change"/> makes the next settings of these,
        /// and writes them: refused as <see cref="SettingsError.Unwritable"/> while the file cannot be read,
        /// or as <see cref="SettingsError.NotWritten"/> when it cannot be written, which leaves the settings
        /// as they were.
        /// </summary>
        public static Result<PersonalSettings> ChangePersonal(Func<PersonalSettings, PersonalSettings> change)
        {
            if (change is null)
                throw new ArgumentNullException(nameof(change));

            return Change(ref s_personal, PersonalPath, PersonalSettingsFile.Read, PersonalSettings.Default, settings => change(settings),
                static settings => PersonalSettingsFile.FromDomain(settings).ToJson());
        }

        /// <summary>Reads each file again that changed on disk since it was read or written, and says so when what it holds changed.</summary>
        private static void Reload()
        {
            bool changed = Reload(ref s_shared, SharedPath, SharedSettingsFile.Read, SharedSettings.None);
            changed |= Reload(ref s_personal, PersonalPath, PersonalSettingsFile.Read, PersonalSettings.Default);
            if (changed)
                Changed?.Invoke();
        }

        private static bool Reload<T>(ref Held<T> held, string path, Func<Optional<string>, SettingsRead<T>> read, T none)
            where T : class
        {
            if (FileStamp.Of(path) == held.Stamp)
                return false;

            Held<T> now = Load(path, read, none);
            bool changed = !now.Read.Equals(held.Read) || !now.Settings.Equals(held.Settings);
            held = now;
            return changed;
        }

        /// <summary>
        /// Reads the file at <paramref name="path"/>: a file that could not be read, as when another
        /// program holds it, is stamped as no file is, so it is read again on the next try.
        /// </summary>
        private static Held<T> Load<T>(string path, Func<Optional<string>, SettingsRead<T>> read, T none)
            where T : class
        {
            FileStamp stamp = FileStamp.Of(path);
            return ReadText(path).Match<Held<T>>(
                failed => new Held<T>(new SettingsRead<T>.Unreadable(failed.Message), none, FileStamp.Unread),
                text =>
                {
                    SettingsRead<T> settings = read(text);
                    return new Held<T>(settings, settings.SettingsOr(none), stamp);
                });
        }

        private static Result<T> Change<T>(
            ref Held<T> held, string path, Func<Optional<string>, SettingsRead<T>> read, T none, Func<T, Result<T>> change, Func<T, string> write)
            where T : class
        {
            // A file changed on disk while the editor kept focus, as an update made in the editor changes
            // it, is read again first, so the change is made to what it holds now and writes nothing over.
            if (Reload(ref held, path, read, none))
                Changed?.Invoke();

            Result<Optional<T>> changing = held.Read.Changing(change, path);
            if (!changing.IsSuccess(out Optional<T> next))
                return changing.ErrorUnsafe();
            if (!next.HasValue(out T settings))
                return held.Settings;

            Result<FileStamp> written = Write(path, write(settings));
            if (!written.IsSuccess(out FileStamp stamp))
                return written.ErrorUnsafe();

            held = new Held<T>(new SettingsRead<T>.Read(settings, SettingsText.NoneSkipped), settings, stamp);
            Changed?.Invoke();
            return settings;
        }

        /// <summary>The text of the file at <paramref name="path"/>, none when there is no file, or why it could not be read.</summary>
        private static Result<Optional<string>> ReadText(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return F.Success<Optional<string>>(F.None);

                return F.Success(F.Some(File.ReadAllText(path, Encoding.UTF8)));
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
            {
                return F.Error($"It could not be read: {SaidLines.Closed(failed.Message)}");
            }
        }

        /// <summary>Writes <paramref name="text"/> whole beside <paramref name="path"/>, then moves it into place, so no reader sees half a file.</summary>
        private static Result<FileStamp> Write(string path, string text)
        {
            string beside = path + ".writing";
            try
            {
                string? folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                File.WriteAllText(beside, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (File.Exists(path))
                    File.Replace(beside, path, destinationBackupFileName: null);
                else
                    File.Move(beside, path);

                return FileStamp.Of(path);
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                TryDelete(beside);
                return SettingsErrors.Of(SettingsError.NotWritten, $"{path} could not be written: {SaidLines.Closed(failed.Message)}");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
            {
                // Left beside the file, where the next write writes over it.
            }
        }

        /// <summary>A file's settings as read, those the console goes by, and the stamp of the file they were read from or written to.</summary>
        private readonly record struct Held<T>(SettingsRead<T> Read, T Settings, FileStamp Stamp);

        /// <summary>Whether a file is there, when it was last written and how long it is: when any of it changes, the file is read again.</summary>
        private readonly record struct FileStamp(bool Exists, DateTime Written, long Length)
        {
            /// <summary>The stamp of a file that could not be read, which no file has, so it is read again.</summary>
            public static FileStamp Unread { get; } = new FileStamp(true, DateTime.MinValue, -1);

            public static FileStamp Of(string path)
            {
                var file = new FileInfo(path);
                return file.Exists ? new FileStamp(true, file.LastWriteTimeUtc, file.Length) : default;
            }
        }
    }
}
