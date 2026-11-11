using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// The stack a record was written from: its frames, one at least, from the method that made the
    /// call outward. A store captures it at the call for a record that has a call site and is at or
    /// above its event's stack level (<see cref="LogRules.StackLevelFor"/>), and a file reads it back.
    /// </summary>
    /// <remarks>
    /// The package's own frames are left out, and so is a method whose first parameter is a
    /// <see cref="Log"/> and that takes a <c>[CallerFilePath]</c>: the method the generator writes,
    /// and a wrapper of the program's own that forwards its caller's call site. A frame has a file and
    /// a line where the build has symbols: in the editor and in development builds, and in an IL2CPP
    /// build only with line numbers turned on.
    /// </remarks>
    public sealed class LogStack : IEquatable<LogStack>
    {
        private static readonly Assembly s_package = typeof(LogStack).Assembly;

        private readonly LogStackFrame[] _frames;
        private readonly ReadOnlyCollection<LogStackFrame> _frameList;

        /// <summary>A stack of <paramref name="frames"/>, the innermost first: for a reader of a file, and for a test.</summary>
        /// <exception cref="ArgumentException">There are no frames, or a frame is <c>default</c>, which names no method.</exception>
        public LogStack(IEnumerable<LogStackFrame> frames)
            : this(Checked(frames))
        {
        }

        private LogStack(LogStackFrame[] frames)
        {
            _frames = frames;
            _frameList = Array.AsReadOnly(frames);
        }

        /// <summary>The frames, from the method that made the call outward.</summary>
        public IReadOnlyList<LogStackFrame> Frames => _frameList;

        /// <summary>
        /// The stack of the call being written, from the method that made it outward: none when the
        /// runtime cannot walk it, or every frame it walks is how a record is written. What a store
        /// keeps of a record at its event's stack level.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Optional<LogStack> Here()
        {
            try
            {
                var trace = new StackTrace(1, fNeedFileInfo: true);
                var frames = new List<LogStackFrame>(trace.FrameCount);
                bool plumbing = true;
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    StackFrame? frame = trace.GetFrame(i);
                    MethodBase? method = frame?.GetMethod();
                    if (frame is null || method is null)
                        continue;
                    if (plumbing && IsPlumbing(method))
                        continue;

                    plumbing = false;
                    frames.Add(new LogStackFrame(Name(method), new LogCallSite(frame.GetFileName(), frame.GetFileLineNumber())));
                }

                return frames.Count == 0 ? F.None : F.Some(new LogStack(frames.ToArray()));
            }
            catch (Exception exception) when (exception is TypeLoadException or FileNotFoundException or FileLoadException
                or BadImageFormatException or NotSupportedException or MemberAccessException)
            {
                // A frame whose types the runtime cannot load: a record without its stack beats a log call that throws.
                return F.None;
            }
        }

        /// <summary>Equal when their frames are, in order.</summary>
        public bool Equals(LogStack? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;
            if (_frames.Length != other._frames.Length)
                return false;

            for (int i = 0; i < _frames.Length; i++)
            {
                if (!_frames[i].Equals(other._frames[i]))
                    return false;
            }

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is LogStack other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (LogStackFrame frame in _frames)
                hash.Add(frame);

            return hash.ToHashCode();
        }

        /// <summary>A line a frame, as <c>Game.SaveManager.Save(int) (at Assets/Game/SaveManager.cs:42)</c>.</summary>
        public override string ToString() => string.Join("\n", _frames);

        private static LogStackFrame[] Checked(IEnumerable<LogStackFrame> frames)
        {
            if (frames is null)
                throw new ArgumentNullException(nameof(frames));

            var copy = new List<LogStackFrame>(frames);
            if (copy.Count == 0)
                throw new ArgumentException("A stack has one frame at least; a record without one has no stack.", nameof(frames));

            foreach (LogStackFrame frame in copy)
            {
                if (frame.Method.Length == 0)
                    throw new ArgumentException("One of the frames is default, and a frame names its method.", nameof(frames));
            }

            return copy.ToArray();
        }

        /// <summary>
        /// Whether <paramref name="method"/> is how a record is written, not why: the package's own, or a
        /// method whose first parameter is a <see cref="Log"/> and that takes a <c>[CallerFilePath]</c>.
        /// </summary>
        private static bool IsPlumbing(MethodBase method)
        {
            if (method.DeclaringType?.Assembly == s_package)
                return true;
            if (!method.IsStatic)
                return false;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0 || parameters[0].ParameterType != typeof(Log))
                return false;

            foreach (ParameterInfo parameter in parameters)
            {
                if (parameter.IsDefined(typeof(CallerFilePathAttribute), false))
                    return true;
            }

            return false;
        }

        /// <summary>A method as a frame names it: <c>Game.SaveManager.Save(int, string, ref long)</c>.</summary>
        private static string Name(MethodBase method)
        {
            var text = new StringBuilder();
            if (method.DeclaringType is { } declaring)
            {
                AppendType(text, declaring, full: true);
                text.Append('.');
            }

            text.Append(method.Name);
            if (method.IsGenericMethod)
            {
                Type[] arguments = method.GetGenericArguments();
                text.Append('<');
                for (int i = 0; i < arguments.Length; i++)
                {
                    if (i > 0)
                        text.Append(", ");
                    AppendType(text, arguments[i], full: false);
                }

                text.Append('>');
            }

            text.Append('(');
            ParameterInfo[] parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                    text.Append(", ");

                ParameterInfo parameter = parameters[i];
                Type type = parameter.ParameterType;
                if (type.IsByRef && type.GetElementType() is { } passed)
                {
                    text.Append(parameter.IsOut ? "out " : parameter.IsIn ? "in " : "ref ");
                    type = passed;
                }

                AppendType(text, type, full: false);
            }

            return text.Append(')').ToString();
        }

        /// <summary>
        /// A type's name, with its namespace and the types it is nested in when <paramref name="full"/>,
        /// its own generic arguments in angle brackets, and a C# keyword for a built-in type's.
        /// </summary>
        private static void AppendType(StringBuilder text, Type type, bool full)
        {
            if (type.HasElementType && type.GetElementType() is { } element)
            {
                AppendType(text, element, full);
                text.Append(type.IsArray ? "[]" : type.IsPointer ? "*" : "&");
                return;
            }

            if (!full && Keyword(type) is { } keyword)
            {
                text.Append(keyword);
                return;
            }

            if (full && !type.IsGenericParameter)
            {
                if (type.DeclaringType is { } outer)
                {
                    AppendType(text, outer, full: true);
                    text.Append('.');
                }
                else if (!string.IsNullOrEmpty(type.Namespace))
                {
                    text.Append(type.Namespace).Append('.');
                }
            }

            string name = type.Name;
            int tick = name.IndexOf('`');
            text.Append(name, 0, tick < 0 ? name.Length : tick);

            // A nested type's arguments are its outer types' and then its own: the name counts its own.
            if (tick < 0 || !int.TryParse(name.Substring(tick + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int own))
                return;

            Type[] arguments = type.GetGenericArguments();
            text.Append('<');
            for (int i = Math.Max(0, arguments.Length - own); i < arguments.Length; i++)
            {
                if (i > arguments.Length - own)
                    text.Append(", ");
                AppendType(text, arguments[i], full: false);
            }

            text.Append('>');
        }

        private static string? Keyword(Type type) =>
            type == typeof(bool) ? "bool"
            : type == typeof(byte) ? "byte"
            : type == typeof(sbyte) ? "sbyte"
            : type == typeof(char) ? "char"
            : type == typeof(short) ? "short"
            : type == typeof(ushort) ? "ushort"
            : type == typeof(int) ? "int"
            : type == typeof(uint) ? "uint"
            : type == typeof(long) ? "long"
            : type == typeof(ulong) ? "ulong"
            : type == typeof(float) ? "float"
            : type == typeof(double) ? "double"
            : type == typeof(decimal) ? "decimal"
            : type == typeof(string) ? "string"
            : type == typeof(object) ? "object"
            : type == typeof(void) ? "void"
            : null;
    }

    /// <summary>A frame of a <see cref="LogStack"/>: the method, and where in its source the call it made was, when the build has symbols.</summary>
    public readonly struct LogStackFrame : IEquatable<LogStackFrame>
    {
        private readonly string? _method;

        /// <summary>A frame of <paramref name="method"/>, at <paramref name="at"/>.</summary>
        /// <param name="method">The method, as <c>Game.SaveManager.Save(int)</c>.</param>
        /// <param name="at">The file and line of the call it made, as the compiler wrote the file; unknown when the build has no symbols.</param>
        /// <exception cref="ArgumentException"><paramref name="method"/> is null or empty.</exception>
        public LogStackFrame(string method, LogCallSite at)
        {
            if (string.IsNullOrEmpty(method))
                throw new ArgumentException("A frame names its method.", nameof(method));

            _method = method;
            At = at;
        }

        /// <summary>The method, as <c>Game.SaveManager.Save(int)</c>: empty only for a <c>default</c> frame.</summary>
        public string Method => _method ?? "";

        /// <summary>The file and line of the call it made, as the compiler wrote the file: unknown when the build has no symbols.</summary>
        public LogCallSite At { get; }

        /// <inheritdoc />
        public bool Equals(LogStackFrame other) => string.Equals(_method, other._method, StringComparison.Ordinal) && At.Equals(other.At);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is LogStackFrame other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(_method, At);

        /// <summary>The method and, when known, where: <c>Game.SaveManager.Save(int) (at Assets/Game/SaveManager.cs:42)</c>.</summary>
        public override string ToString() => At.IsKnown ? $"{Method} (at {At})" : Method;
    }
}
