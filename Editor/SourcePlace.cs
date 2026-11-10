using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    /// <summary>A line of a source file, the file as Unity opens it: <c>Assets/...</c> or <c>Packages/&lt;name&gt;/...</c> in the project, and its whole path outside it.</summary>
    internal sealed record SourcePlace
    {
        private SourcePlace(string file, int line)
        {
            File = file;
            Line = line;
        }

        /// <summary>The file, with forward slashes.</summary>
        public string File { get; }

        /// <summary>The line, from 1.</summary>
        public int Line { get; }

        /// <summary>Whether the file is one of the project's or its packages', which Unity opens as an asset.</summary>
        public bool InProject =>
            File.StartsWith("Assets/", StringComparison.Ordinal) || File.StartsWith("Packages/", StringComparison.Ordinal);

        /// <summary>
        /// The place <paramref name="file"/>, as the compiler or a stack wrote it, and
        /// <paramref name="line"/> are, with the file mapped as Unity opens it: none for no file, a file
        /// a stack does not know, or a line before the first.
        /// </summary>
        public static Optional<SourcePlace> Of(string? file, int line, UnityPaths paths)
        {
            if (paths is null)
                throw new ArgumentNullException(nameof(paths));
            if (string.IsNullOrWhiteSpace(file) || line < 1 || file!.StartsWith("<", StringComparison.Ordinal))
                return F.None;

            return new SourcePlace(paths.Map(file), line);
        }

        /// <summary>The file's name and the line, as <c>SaveManager.cs:42</c>.</summary>
        public string Short => $"{UnityPaths.FileName(File)}:{Line.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>The file and the line, as <c>Assets/Game/SaveManager.cs:42</c>.</summary>
        public override string ToString() => $"{File}:{Line.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>A line of a source file, with its number from 1.</summary>
    internal sealed record SourceLine(int Number, string Text);

    /// <summary>The lines of a source file around a line, which the details show of a call site.</summary>
    internal static class SourceLines
    {
        /// <summary>
        /// The lines of <paramref name="lines"/> from <paramref name="around"/> lines before
        /// <paramref name="line"/> to as many after it, those the file has: none when the file is
        /// shorter than the line, since it changed since the record was written.
        /// </summary>
        public static ReadOnlyCollection<SourceLine> Around(IReadOnlyList<string> lines, int line, int around)
        {
            if (lines is null)
                throw new ArgumentNullException(nameof(lines));
            if (around < 0)
                throw new ArgumentOutOfRangeException(nameof(around), $"Lines around are 0 or more, and {around.ToString(CultureInfo.InvariantCulture)} was given.");
            if (line < 1 || line > lines.Count)
                return Array.AsReadOnly(Array.Empty<SourceLine>());

            int first = Math.Max(1, line - around);
            int last = Math.Min(lines.Count, line + around);
            var shown = new SourceLine[last - first + 1];
            for (int number = first; number <= last; number++)
                shown[number - first] = new SourceLine(number, lines[number - 1].TrimEnd());

            return Array.AsReadOnly(shown);
        }
    }

    /// <summary>A frame of a stack: the method, and where in its source it was, when the stack says.</summary>
    internal sealed record StackFrame(string Method, Optional<SourcePlace> At);

    /// <summary>The frames of a stack as Unity and .NET write one, each a line.</summary>
    internal static class StackFrames
    {
        private static readonly ReadOnlyCollection<StackFrame> s_none = Array.AsReadOnly(Array.Empty<StackFrame>());

        // By key, not by spec: a session read from a file has specs of its own.
        private static readonly string s_loggedKey = UnityLog.Events.Logged.Key;

        /// <summary>
        /// Whether a record of <paramref name="spec"/> is a message Unity's log handed over, which has
        /// no call site of its own and holds its stack as a field, which <see cref="IsUnityStack"/> tells.
        /// </summary>
        public static bool HoldsUnityStack(LogEventSpec spec) => spec.Key == s_loggedKey;

        /// <summary>Whether <paramref name="value"/>, a field of a record that <see cref="HoldsUnityStack"/>, is that stack, read by <see cref="OfUnity"/>.</summary>
        public static bool IsUnityStack(LogValue value) => value.Field.Name == UnityLog.StackTraceField && value.Kind == LogValueKind.String;

        /// <summary>
        /// The frames of a stack Unity's log handed over: a line each, as
        /// <c>Game.SaveManager:Save () (at Assets/Game/SaveManager.cs:42)</c>, or an exception's own
        /// lines, as <c>at Game.SaveManager.Save () [0x0001c] in D:\Game\SaveManager.cs:42</c>.
        /// </summary>
        public static ReadOnlyCollection<StackFrame> OfUnity(string stack, UnityPaths paths) => Parse(stack, paths, onlyAt: false);

        /// <summary>
        /// The frames of an exception's text, its type and message and then its frames, each a line
        /// that starts <c>at</c>, its inner exceptions' among them: what is not a frame is skipped.
        /// </summary>
        public static ReadOnlyCollection<StackFrame> OfException(string text, UnityPaths paths) => Parse(text, paths, onlyAt: true);

        /// <summary>
        /// Where the first of <paramref name="frames"/> that is in the project is: the code that
        /// wrote what the stack is of, past the engine's and the packages' own frames; none when no
        /// frame is in the project.
        /// </summary>
        public static Optional<SourcePlace> FirstInProject(IReadOnlyList<StackFrame> frames)
        {
            if (frames is null)
                throw new ArgumentNullException(nameof(frames));

            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].At.HasValue(out SourcePlace at) && at.InProject)
                    return at;
            }

            return F.None;
        }

        private static ReadOnlyCollection<StackFrame> Parse(string text, UnityPaths paths, bool onlyAt)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));
            if (paths is null)
                throw new ArgumentNullException(nameof(paths));

            var frames = new List<StackFrame>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                bool at = line.StartsWith("at ", StringComparison.Ordinal);
                if (line.Length == 0 || (onlyAt && !at) || line.StartsWith("---", StringComparison.Ordinal))
                    continue;

                frames.Add(Frame(at ? line.Substring(3).TrimStart() : line, paths));
            }

            return frames.Count == 0 ? s_none : Array.AsReadOnly(frames.ToArray());
        }

        /// <summary>A line of a stack, without its <c>at</c>, as a frame.</summary>
        private static StackFrame Frame(string line, UnityPaths paths)
        {
            // Unity's: Method (args) (at path:line)
            int unity = line.LastIndexOf(" (at ", StringComparison.Ordinal);
            if (unity > 0 && line.EndsWith(")", StringComparison.Ordinal))
            {
                string place = line.Substring(unity + 5, line.Length - unity - 6);
                return new StackFrame(line.Substring(0, unity), Place(place, paths));
            }

            // Mono's: Method (args) [0x0001c] in path:line, and .NET's: Method(args) in path:line 42
            int dotNet = line.LastIndexOf(" in ", StringComparison.Ordinal);
            if (dotNet > 0)
            {
                string method = line.Substring(0, dotNet);
                int offset = method.LastIndexOf(" [0x", StringComparison.Ordinal);
                if (offset > 0 && method.EndsWith("]", StringComparison.Ordinal))
                    method = method.Substring(0, offset);

                return new StackFrame(method, Place(line.Substring(dotNet + 4).Trim(), paths));
            }

            return new StackFrame(line, F.None);
        }

        /// <summary><c>path:42</c> or <c>path:line 42</c> as a place: none when it has no line, or a file the stack did not know.</summary>
        private static Optional<SourcePlace> Place(string place, UnityPaths paths)
        {
            int colon = place.LastIndexOf(':');
            if (colon <= 0)
                return F.None;

            string line = place.Substring(colon + 1).Trim();
            if (line.StartsWith("line ", StringComparison.Ordinal))
                line = line.Substring(5);

            return int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? SourcePlace.Of(place.Substring(0, colon), number, paths)
                : F.None;
        }
    }
}
