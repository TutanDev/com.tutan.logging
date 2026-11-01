using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Tutan.Logging.Generators.Tests
{
    public sealed class LogEventGeneratorTests
    {
        // The package's attributes and enums, as the generator reads them, and the two types of other
        // assemblies it recognizes by name.
        private static readonly string[] s_package = { Resource("Attributes.cs"), Resource("Kinds.cs"), Resource("LogLevel.cs") };

        private const string Others = @"
namespace Tutan.Logging { public readonly struct Log { } }
namespace Tutan.Functional { public readonly struct Optional<T> { public bool IsSome => false; } }
namespace UnityEngine
{
    public enum RuntimeInitializeLoadType { SubsystemRegistration = 4 }
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class RuntimeInitializeOnLoadMethodAttribute : System.Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { } }
    public struct Vector3 : System.IFormattable { public float x, y, z; public string ToString(string? format, System.IFormatProvider? provider) => """"; }
}
";

        private static readonly CSharpParseOptions s_csharp9 = new CSharpParseOptions(LanguageVersion.CSharp9);

        // A category of one event, declared at line 7.
        private const string SaveLog = @"using Tutan.Logging;

[LogCategory(""game.save"")]
static partial class SaveLog
{
    [LogEvent(LogLevel.Information, ""Saved"")]
    static partial void Saved();
}";

        [Fact]
        public void An_event_and_a_scope_are_written_beside_their_declarations()
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(@"
using System;
using Tutan.Logging;

namespace Game
{
    [LogCategory(""game.save"")]
    internal static partial class SaveLog
    {
        [LogEvent(LogLevel.Information, ""Saved slot {Slot} in {Elapsed}"")]
        static partial void Saved(int slot, TimeSpan elapsed);

        [LogScope(LogLevel.Information, ""Saving slot {Slot}"")]
        static partial void Saving(int slot);
    }
}");

            Assert.Empty(diagnostics);
            Assert.Contains("namespace Game", generated);
            Assert.Contains("static partial class SaveLog", generated);
            Assert.Contains("public static void Saved(this global::Tutan.Logging.Log log, int slot, global::System.TimeSpan elapsed, " +
                "[global::System.Runtime.CompilerServices.CallerFilePath] string callerFile = \"\", " +
                "[global::System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0)", generated);
            Assert.Contains("public static global::Tutan.Logging.LogScope Saving(this global::Tutan.Logging.Log log, int slot, ", generated);
            Assert.Contains("return global::Tutan.Logging.LogScope.Off(log);", generated);
            Assert.Contains("log.BeginScope(global::Game.SaveLog.Events.Saving, global::Tutan.Logging.LogLevel.Information, 0, callerFile, callerLine);", generated);
            Assert.Contains("return writer.Scope;", generated);
            Assert.Contains("global::Tutan.Logging.LogEventSpec.Declare(", generated);
            Assert.Contains("\"game.save\", \"Saved\", global::Tutan.Logging.LogEventKind.Event, global::Tutan.Logging.LogLevel.Information,", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Value<int>(\"Slot\", global::Tutan.Logging.LogValueKind.Int32, \"int\")", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Value<global::System.TimeSpan>(\"Elapsed\", global::Tutan.Logging.LogValueKind.TimeSpan, \"System.TimeSpan\")", generated);
            Assert.Contains("[global::UnityEngine.RuntimeInitializeOnLoadMethod(global::UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]", generated);
            Assert.DoesNotContain("UnityEditor", generated);
            Assert.Contains("global::Tutan.Logging.LogCatalog.Register(", generated);
            Assert.Contains("Events.Saved, Events.Saving);", generated);
            Assert.Contains("Declared in Test0.cs at line 11 as <c>Saved(int slot, System.TimeSpan elapsed)</c>", generated);
            AssertParsesAsCSharp9(generated);
        }

        [Fact]
        public void A_class_in_no_namespace_is_written_in_none()
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(@"
using Tutan.Logging;

[LogCategory(""game"")]
static partial class GameLog
{
    [LogEvent(LogLevel.Warning, ""Started"")]
    static partial void Started();
}");

            Assert.Empty(diagnostics);
            Assert.DoesNotContain("namespace", generated);
            Assert.Contains("global::System.Array.Empty<global::Tutan.Logging.LogField>()", generated);
            Assert.Contains("    writer.Commit();", generated);
            AssertParsesAsCSharp9(generated);
        }

        [Fact]
        public void Every_kind_of_field_is_described_and_copied()
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(@"
using System;
using Tutan.Logging;
using Tutan.Functional;
using UnityEngine;

namespace Game
{
    [Flags] public enum Doors : ulong { None = 0, Front = 1, Back = ulong.MaxValue }

    [LogCategory(""game.fields"")]
    public static partial class FieldLog
    {
        [LogEvent(LogLevel.Debug, ""{Name} {Error} {Text} {Doors} {At} {Where} {Count} {Maybe} {Handle} {Reason}"")]
        static partial void Everything(string name, Exception error, ReadOnlySpan<char> text, Doors doors, Vector3 at,
            Optional<Vector3> where, int? count, Optional<string> maybe, IntPtr handle, [Personal, LogTag(""why"")] string reason);
    }
}");

            Assert.Empty(diagnostics);
            Assert.Contains("global::Tutan.Logging.LogField.Ref(\"Name\", global::Tutan.Logging.LogValueKind.String, \"string\")", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Ref(\"Error\", global::Tutan.Logging.LogValueKind.Exception, \"System.Exception\")", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Chars(\"Text\", \"System.ReadOnlySpan<char>\")", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Enum(\"Doors\", \"Game.Doors\", new string[] { \"None\", \"Front\", \"Back\" }, new long[] { 0L, 1L, -1L }, true)", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Formattable<global::UnityEngine.Vector3>(\"At\", \"UnityEngine.Vector3\")", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Formattable<global::UnityEngine.Vector3>(\"Where\", \"Tutan.Functional.Optional<UnityEngine.Vector3>\", optional: true)", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Value<int>(\"Count\", global::Tutan.Logging.LogValueKind.Int32, \"int?\", optional: true)", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Ref(\"Maybe\", global::Tutan.Logging.LogValueKind.String, \"Tutan.Functional.Optional<string>\", optional: true)", generated);
            // An IntPtr is spelt nint where the runtime has numeric IntPtr, as .NET 7 and later do, and System.IntPtr in Unity.
            Assert.Contains("global::Tutan.Logging.LogField.Value<long>(\"Handle\", global::Tutan.Logging.LogValueKind.Int64, ", generated);
            Assert.Contains("global::Tutan.Logging.LogField.Ref(\"Reason\", global::Tutan.Logging.LogValueKind.String, \"string\", personal: true, tag: \"why\")", generated);

            Assert.Contains("log.Write(global::Game.FieldLog.Events.Everything, global::Tutan.Logging.LogLevel.Debug, global::Tutan.Logging.LogWriter.CharSlots(text), callerFile, callerLine);", generated);
            Assert.Contains("writer.Ref(name);", generated);
            Assert.Contains("writer.Chars(text);", generated);
            Assert.Contains("writer.Value(unchecked((long)doors));", generated);
            Assert.Contains("writer.Value(at);", generated);
            Assert.Contains("writer.Presence(where.IsSome);", generated);
            Assert.Contains("writer.Value(global::Tutan.Functional.OptionalExtensions.Or(where, default(global::UnityEngine.Vector3)));", generated);
            Assert.Contains("writer.Presence(count.HasValue);", generated);
            Assert.Contains("writer.Value(count.GetValueOrDefault());", generated);
            Assert.Contains("writer.Ref(global::Tutan.Functional.OptionalExtensions.Or(maybe, default(string)!));", generated);
            Assert.Contains("writer.Value((long)handle);", generated);
            AssertParsesAsCSharp9(generated);
        }

        [Fact]
        public void A_level_chosen_at_the_call_is_a_parameter_and_not_a_field()
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(@"
using Tutan.Logging;

[LogCategory(""game"")]
static partial class HeardLog
{
    [LogEvent(""{Message}"")]
    static partial void Heard(LogLevel level, string message);
}");

            Assert.Empty(diagnostics);
            Assert.Contains("\"game\", \"Heard\", global::Tutan.Logging.LogEventKind.Event, null,", generated);
            Assert.Contains("Heard(this global::Tutan.Logging.Log log, global::Tutan.Logging.LogLevel level, string message, ", generated);
            Assert.Contains("if (!log.IsEnabled(global::HeardLog.Events.Heard, level))", generated);
            Assert.Contains("log.Write(global::HeardLog.Events.Heard, level, 0, callerFile, callerLine);", generated);
            Assert.DoesNotContain("\"Level\"", generated);
            AssertParsesAsCSharp9(generated);
        }

        [Fact]
        public void Names_the_generator_gives_stay_clear_of_the_fields()
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(@"
using Tutan.Logging;

[LogCategory(""game"")]
static partial class ClashLog
{
    [LogEvent(LogLevel.Information, ""{Log} {Writer} {CallerFile} {CallerLine} {Event}"")]
    static partial void Clash(int log, int writer, string callerFile, int callerLine, int @event);
}");

            Assert.Empty(diagnostics);
            Assert.Contains("Clash(this global::Tutan.Logging.Log log_, int log, int writer, string callerFile, int callerLine, int @event, " +
                "[global::System.Runtime.CompilerServices.CallerFilePath] string callerFile_ = \"\", " +
                "[global::System.Runtime.CompilerServices.CallerLineNumber] int callerLine_ = 0)", generated);
            Assert.Contains("global::Tutan.Logging.LogWriter writer_ = log_.Write(", generated);
            Assert.Contains("writer_.Value(@event);", generated);
            AssertParsesAsCSharp9(generated);
        }

        [Fact]
        public void A_name_and_a_limit_are_taken_from_the_attribute()
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(@"
using Tutan.Logging;

[LogCategory(""game"")]
static partial class NamedLog
{
    [LogEvent(LogLevel.Information, ""Hit"", Name = ""Struck"", PerSecond = 5)]
    static partial void Hit();
}");

            Assert.Empty(diagnostics);
            Assert.Contains("public static readonly global::Tutan.Logging.LogEventSpec Struck = ", generated);
            Assert.Contains("        5,", generated);
            Assert.Contains("public static void Hit(this global::Tutan.Logging.Log log, ", generated);
            Assert.Contains("global::NamedLog.Events.Struck", generated);
        }

        [Fact]
        public void A_declaring_file_is_written_as_the_compiler_writes_a_callers_file()
        {
            // Unity hands the compiler each file relative to the project, which on Windows the compiler joins to its
            // folder with the other separator; a [CallerFilePath] gets that path normalized, and so does the declaration.
            string project = Path.Combine(Path.GetTempPath(), "Game");
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(SaveLog,
                project + Path.DirectorySeparatorChar + "Assets/Game/SaveLog.cs", new SourceFileResolver(ImmutableArray<string>.Empty, project));

            Assert.Empty(diagnostics);
            Assert.Contains(SymbolDisplay.FormatLiteral(Path.Combine(project, "Assets", "Game", "SaveLog.cs"), quote: true) + ", 7)", generated);
        }

        [Fact]
        public void A_declaring_file_is_mapped_as_the_compiler_maps_a_callers_file()
        {
            // Unity compiles the assemblies of packages with /pathmap:<project>=.
            string project = Path.Combine(Path.GetTempPath(), "Game");
            var resolver = new SourceFileResolver(ImmutableArray<string>.Empty, project, ImmutableArray.Create(new KeyValuePair<string, string>(project, ".")));
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(SaveLog,
                project + Path.DirectorySeparatorChar + "Packages/com.game.save/SaveLog.cs", resolver);

            Assert.Empty(diagnostics);
            Assert.Contains(SymbolDisplay.FormatLiteral(Path.Combine(".", "Packages", "com.game.save", "SaveLog.cs"), quote: true) + ", 7)", generated);
        }

        [Fact]
        public void A_line_directive_moves_a_declaration_as_it_moves_a_call()
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(
                SaveLog.Replace("    static partial void Saved();", "#line 40 \"SaveLog.tt\"\n    static partial void Saved();"));

            Assert.Empty(diagnostics);
            Assert.Contains("\"SaveLog.tt\", 40)", generated);
            Assert.Contains("Declared in SaveLog.tt at line 40", generated);
        }

        [Fact]
        public void The_events_the_package_tests_declare_are_accepted()
        {
            // The Unity tests of the package compile these; a refusal here would stop them compiling at all.
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(Resource("TestEvents.cs"));

            Assert.Empty(diagnostics);
            Assert.Contains("public static global::Tutan.Logging.LogScope Saving(this global::Tutan.Logging.Log log, int slot, ", generated);
            Assert.Contains("public static void Numbers(this global::Tutan.Logging.Log log, bool flag, char letter, sbyte tiny, byte small, short @short, ", generated);
            Assert.Contains("public static void Said(this global::Tutan.Logging.Log log, string? text, ", generated);
            Assert.Contains("public static void Strike(this global::Tutan.Logging.Log log, ", generated);
            AssertParsesAsCSharp9(generated);
        }

        [Fact]
        public void A_hole_that_names_no_parameter_is_refused()
        {
            AssertRefused("TLOG0001", @"[LogEvent(LogLevel.Information, ""Saved {Slot}"")] static partial void Saved(int other);");
        }

        [Fact]
        public void A_parameter_of_a_type_a_field_cannot_hold_is_refused()
        {
            AssertRefused("TLOG0002", @"[LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(object value);");
            AssertRefused("TLOG0002", @"[LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(int[] values);");
            AssertRefused("TLOG0002", @"[LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(System.Collections.Generic.List<int> values);");
        }

        [Theory]
        [InlineData(@"[LogEvent(LogLevel.Information, ""Saved"")] public static partial void Saved();")]
        [InlineData(@"[LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(); static partial void Saved() { }")]
        [InlineData(@"[LogEvent(LogLevel.Information, ""Saved"")] static void Saved() { }")]
        [InlineData(@"[LogEvent(LogLevel.Information, ""Saved {Slot}"")] static partial void Saved(int slot = 3);")]
        [InlineData(@"[LogEvent(LogLevel.Information, ""Saved {Slot}"")] static partial void Saved(in int slot);")]
        [InlineData(@"[LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved<T>();")]
        [InlineData(@"[LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(Log log);")]
        [InlineData(@"[LogEvent(LogLevel.Information, ""Saved"")] static partial void Events();")]
        public void A_declaration_not_of_the_shape_is_refused(string declaration)
        {
            AssertRefused("TLOG0003", declaration);
        }

        [Theory]
        [InlineData("[LogCategory(\"game\")] static class GameLog { [LogEvent(LogLevel.Information, \"Saved\")] static partial void Saved(); }")]
        [InlineData("[LogCategory(\"game\")] partial class GameLog { [LogEvent(LogLevel.Information, \"Saved\")] static partial void Saved(); }")]
        [InlineData("[LogCategory(\"game\")] static partial class GameLog<T> { [LogEvent(LogLevel.Information, \"Saved\")] static partial void Saved(); }")]
        [InlineData("static partial class Outer { [LogCategory(\"game\")] static partial class GameLog { [LogEvent(LogLevel.Information, \"Saved\")] static partial void Saved(); } }")]
        public void A_class_not_of_the_shape_is_refused(string source)
        {
            (ImmutableArray<Diagnostic> diagnostics, _) = Run("using Tutan.Logging;\n" + source);

            Assert.Contains(diagnostics, d => d.Id == "TLOG0003");
        }

        [Fact]
        public void Two_events_with_one_key_are_refused()
        {
            AssertRefused("TLOG0004", @"
                [LogEvent(LogLevel.Information, ""A"")] static partial void Saved();
                [LogEvent(LogLevel.Information, ""B"", Name = ""Saved"")] static partial void Stored();");
        }

        [Fact]
        public void A_malformed_name_is_refused()
        {
            (ImmutableArray<Diagnostic> diagnostics, _) = Run(@"
using Tutan.Logging;
[LogCategory(""Game.Save"")] static partial class GameLog { [LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(); }");
            Assert.Contains(diagnostics, d => d.Id == "TLOG0005");

            AssertRefused("TLOG0005", @"[LogEvent(LogLevel.Information, ""Saved"", Name = ""not a name"")] static partial void Saved();");
            AssertRefused("TLOG0005", @"[LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(int slot, int Slot);");
        }

        [Fact]
        public void A_malformed_level_or_limit_is_refused()
        {
            AssertRefused("TLOG0006", @"[LogEvent(LogLevel.None, ""Saved"")] static partial void Saved();");
            AssertRefused("TLOG0006", @"[LogEvent(LogLevel.Information, ""Saved"", PerSecond = -1)] static partial void Saved();");
            AssertRefused("TLOG0006", @"[LogEvent(""Saved"")] static partial void Saved();");
            AssertRefused("TLOG0006", @"[LogEvent(""Saved"")] static partial void Saved(LogLevel a, LogLevel b);");
            AssertRefused("TLOG0006", @"[LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(LogLevel level);");
        }

        [Fact]
        public void A_template_that_does_not_parse_is_refused()
        {
            AssertRefused("TLOG0007", @"[LogEvent(LogLevel.Information, ""Saved {Slot"")] static partial void Saved(int slot);");
            AssertRefused("TLOG0007", @"[LogEvent(LogLevel.Information, ""Saved {0}"")] static partial void Saved(int slot);");
        }

        [Fact]
        public void An_event_in_a_class_with_no_category_is_refused()
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(@"
using Tutan.Logging;
static partial class GameLog { [LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(); }");

            Assert.Contains(diagnostics, d => d.Id == "TLOG0008");
            Assert.Equal("", generated);
        }

        [Fact]
        public void Two_classes_with_one_category_are_refused()
        {
            (ImmutableArray<Diagnostic> diagnostics, _) = Run(@"
using Tutan.Logging;
[LogCategory(""game"")] static partial class A { [LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved(); }
[LogCategory(""game"")] static partial class B { [LogEvent(LogLevel.Information, ""Loaded"")] static partial void Loaded(); }");

            Diagnostic twice = Assert.Single(diagnostics, d => d.Id == "TLOG0009");
            Assert.Contains("'A' and by 'B'", twice.GetMessage());
        }

        [Fact]
        public void An_event_with_more_fields_than_a_record_holds_is_refused()
        {
            string parameters = string.Join(", ", Enumerable.Range(0, 33).Select(i => $"int f{i}"));
            AssertRefused("TLOG0010", $@"[LogEvent(LogLevel.Information, ""Many"")] static partial void Many({parameters});");
        }

        [Fact]
        public void A_class_with_a_problem_writes_nothing()
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(@"
using Tutan.Logging;
[LogCategory(""game"")] static partial class GameLog
{
    [LogEvent(LogLevel.Information, ""Saved"")] static partial void Saved();
    [LogEvent(LogLevel.Information, ""{Missing}"")] static partial void Broken();
}");

            Assert.Contains(diagnostics, d => d.Id == "TLOG0001");
            Assert.Equal("", generated);
        }

        private static void AssertRefused(string id, string members)
        {
            (ImmutableArray<Diagnostic> diagnostics, string generated) = Run(@"
using Tutan.Logging;

[LogCategory(""game"")]
static partial class GameLog
{
" + members + @"
}");

            Assert.Contains(diagnostics, d => d.Id == id);
            Assert.All(diagnostics.Where(d => d.Id.StartsWith("TLOG", StringComparison.Ordinal)), d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
            Assert.Equal("", generated);
        }

        private static void AssertParsesAsCSharp9(string generated)
        {
            IEnumerable<Diagnostic> errors = CSharpSyntaxTree.ParseText(generated, s_csharp9).GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error);
            Assert.Empty(errors);
        }

        /// <summary>
        /// Runs the generator over <paramref name="source"/>, compiled as C# 9 from <paramref name="path"/> with
        /// <paramref name="resolver"/> as a compiler's own, and returns its diagnostics and what it wrote, joined.
        /// </summary>
        private static (ImmutableArray<Diagnostic> Diagnostics, string Generated) Run(string source, string path = "Test0.cs", SourceReferenceResolver? resolver = null)
        {
            IEnumerable<SyntaxTree> trees = s_package.Append(Others).Select((text, i) => CSharpSyntaxTree.ParseText(text, s_csharp9, path: $"Package{i}.cs"))
                .Append(CSharpSyntaxTree.ParseText(source, s_csharp9, path: path));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(assembly => MetadataReference.CreateFromFile(assembly));
            CSharpCompilation compilation = CSharpCompilation.Create("Tests", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, sourceReferenceResolver: resolver));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new LogEventGenerator().AsSourceGenerator() }, parseOptions: s_csharp9);
            driver = driver.RunGenerators(compilation);
            GeneratorDriverRunResult run = driver.GetRunResult();
            string generated = string.Join("\n", run.GeneratedTrees.Select(tree => tree.ToString()));
            return (run.Diagnostics, generated);
        }

        private static string Resource(string name)
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The test assembly has no resource {name}.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
