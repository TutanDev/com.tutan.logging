using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Tutan.Logging.Generators
{
    /// <summary>
    /// Writes, beside every <c>[LogEvent]</c> and <c>[LogScope]</c> declaration of a
    /// <c>[LogCategory]</c> class, the method a program calls: an extension of <c>Log</c> with the
    /// declaration's parameters and the call site the compiler fills in, which checks that the event is
    /// on and copies each argument into the store. Beside them it writes the class's specs, in a nested
    /// <c>Events</c> class, and a method Unity calls on load that registers them in the catalog. A
    /// declaration or class it cannot write for is refused with an error that names the parameter, the
    /// hole or the class.
    /// </summary>
    /// <remarks>
    /// A declaration is a partial method with no body, so it is a signature the generator reads, and
    /// the call binds to the generated extension beside it. The caller-info parameters are optional,
    /// and a partial method's parameters are its author's, so they cannot be added to the declaration
    /// itself. What it writes is C# 9, the language Unity compiles a project's own code in.
    /// </remarks>
    [Generator(LanguageNames.CSharp)]
    public sealed class LogEventGenerator : IIncrementalGenerator
    {
        private const string Category = "Logging";
        private const string CategoryAttribute = "Tutan.Logging.LogCategoryAttribute";
        private const string EventAttribute = "Tutan.Logging.LogEventAttribute";
        private const string ScopeAttribute = "Tutan.Logging.LogScopeAttribute";
        private const string PersonalAttribute = "Tutan.Logging.PersonalAttribute";
        private const string TagAttribute = "Tutan.Logging.LogTagAttribute";
        private const string LogType = "Tutan.Logging.Log";
        private const string LevelType = "Tutan.Logging.LogLevel";
        private const string OptionalType = "Tutan.Functional.Optional<T>";
        private const string Logging = "global::Tutan.Logging";

        /// <summary>The names the generator writes into a class beside its events, which an event cannot have.</summary>
        private const string EventsClass = "Events";
        private const string Registration = "RegisterLogEvents";

        private const string Supported =
            "a bool, char, number, decimal, Guid, DateTime, DateTimeOffset, TimeSpan, enum, string, Exception, ReadOnlySpan<char>, " +
            "an unmanaged struct that implements IFormattable, as Vector3, or an Optional<T> or a nullable of one of these but ReadOnlySpan<char>";

        /// <summary>TLOG0001: a hole in a template names no parameter.</summary>
        public static readonly DiagnosticDescriptor HoleWithoutParameter = Error(
            "TLOG0001", "A hole in an event's template names no parameter",
            "The template of '{0}' has the hole {{{1}}}, and '{0}' has no parameter of that name");

        /// <summary>TLOG0002: a parameter's type is not one a field can hold.</summary>
        public static readonly DiagnosticDescriptor UnsupportedType = Error(
            "TLOG0002", "An event's parameter is of a type a field cannot hold",
            "'{0}' is a '{1}', and a field is " + Supported,
            "A value is copied into the store when it is logged, so a reader sees it as it was; a reference to anything but a string, which cannot change, or an exception, would be read as it is when it is read.");

        /// <summary>TLOG0003: a declaration or its class is not of the shape the generator writes for.</summary>
        public static readonly DiagnosticDescriptor Malformed = Error(
            "TLOG0003", "An event's declaration or class is not of the shape the generator writes for",
            "'{0}' {1}");

        /// <summary>TLOG0004: two events of one class have one key.</summary>
        public static readonly DiagnosticDescriptor KeyTwice = Error(
            "TLOG0004", "Two events have one key",
            "'{0}' and '{1}' are both {2}, and a key names one event");

        /// <summary>TLOG0005: a category, an event's name or a field's name is malformed.</summary>
        public static readonly DiagnosticDescriptor BadName = Error(
            "TLOG0005", "A category or a name is malformed",
            "'{0}' is not {1}");

        /// <summary>TLOG0006: an event's level or limit is malformed.</summary>
        public static readonly DiagnosticDescriptor BadLevel = Error(
            "TLOG0006", "An event's level or limit is malformed",
            "'{0}' {1}");

        /// <summary>TLOG0007: a template does not parse.</summary>
        public static readonly DiagnosticDescriptor BadTemplate = Error(
            "TLOG0007", "An event's template does not parse",
            "The template of '{0}' does not parse: {1}");

        /// <summary>TLOG0008: an event is declared in a class with no category.</summary>
        public static readonly DiagnosticDescriptor NoCategory = Error(
            "TLOG0008", "An event is declared in a class with no [LogCategory]",
            "'{0}' is in '{1}', which has no [LogCategory], so its event has no key");

        /// <summary>TLOG0009: two classes of one assembly declare one category.</summary>
        public static readonly DiagnosticDescriptor CategoryTwice = Error(
            "TLOG0009", "Two classes declare one category",
            "The category {0} is declared by '{1}' and by '{2}', and a category is one class's");

        /// <summary>TLOG0010: an event has more fields than a record holds.</summary>
        public static readonly DiagnosticDescriptor TooManyFields = Error(
            "TLOG0010", "An event has more fields than a record holds",
            "'{0}' has {1} fields, and an event has at most {2}");

        /// <inheritdoc />
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            IncrementalValuesProvider<LogClass> classes = context.SyntaxProvider.ForAttributeWithMetadataName(
                CategoryAttribute,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (attributed, cancellation) => Describe(attributed, cancellation));

            context.RegisterSourceOutput(classes, static (output, type) => Write(output, type));

            IncrementalValueProvider<ImmutableArray<Claim>> claims = classes
                .Select(static (type, _) => new Claim(type.Category, type.TypeName, type.CategoryAt))
                .Collect();
            context.RegisterSourceOutput(claims, static (output, all) => ReportCategoriesTwice(output, all));

            foreach (string attribute in new[] { EventAttribute, ScopeAttribute })
            {
                IncrementalValuesProvider<Problem?> orphans = context.SyntaxProvider.ForAttributeWithMetadataName(
                    attribute,
                    predicate: static (node, _) => node is MethodDeclarationSyntax,
                    transform: static (attributed, _) => Orphan((IMethodSymbol)attributed.TargetSymbol, attributed.TargetNode));
                context.RegisterSourceOutput(orphans, static (output, orphan) =>
                {
                    if (orphan is { } problem)
                        output.ReportDiagnostic(problem.ToDiagnostic());
                });
            }
        }

        /// <summary>What the generator writes for one class, or the problems that stop it, as values the pipeline can compare from one run to the next.</summary>
        internal sealed record LogClass(
            string HintName,
            string? Namespace,
            string Name,
            string FullName,
            string TypeName,
            string Assembly,
            string Category,
            Place? CategoryAt,
            bool RuntimeRegistration,
            bool EditorRegistration,
            EquatableArray<LogMethod> Methods,
            EquatableArray<Problem> Problems);

        /// <summary>One event or scope.</summary>
        internal sealed record LogMethod(
            string MethodName,
            string EventName,
            bool Scope,
            string? Level,
            string Template,
            int PerSecond,
            string Declaration,
            EquatableArray<string> Parameters,
            string LogArgument,
            string WriterName,
            string FileArgument,
            string LineArgument,
            string? LevelArgument,
            EquatableArray<LogParameter> Fields,
            string File,
            string FileName,
            int Line);

        /// <summary>One field: the argument it is copied from, and everything its spec says.</summary>
        internal sealed record LogParameter(
            string Argument,
            string FieldName,
            Shape Shape,
            string Kind,
            string ValueType,
            string Underlying,
            string TypeName,
            bool Personal,
            string? Tag,
            Optionality Optional,
            string? Conversion,
            EquatableArray<string> EnumNames,
            EquatableArray<long> EnumValues,
            bool Flags);

        /// <summary>How a field's value is held, which is which factory makes its spec and which writer call copies it.</summary>
        internal enum Shape
        {
            Value,
            Enum,
            Ref,
            Chars,
            Formattable,
        }

        /// <summary>Whether a field may hold no value, and what it is wrapped in when it may.</summary>
        internal enum Optionality
        {
            None,
            Optional,
            Nullable,
        }

        /// <summary>A class's claim to a category.</summary>
        internal readonly record struct Claim(string Category, string TypeName, Place? At);

        /// <summary>A place in a file, kept as values rather than a <see cref="Location"/>, which holds the syntax tree.</summary>
        internal readonly record struct Place(string Path, TextSpan Span, LinePositionSpan Lines)
        {
            public static Place? Of(Location? location) =>
                location is { IsInSource: true } ? new Place(location.SourceTree!.FilePath, location.SourceSpan, location.GetLineSpan().Span) : null;

            public Location ToLocation() => Location.Create(Path, Span, Lines);
        }

        /// <summary>One diagnostic, at one place.</summary>
        internal readonly record struct Problem(DiagnosticDescriptor Descriptor, Place? At, EquatableArray<string> Arguments)
        {
            public Diagnostic ToDiagnostic() =>
                Diagnostic.Create(Descriptor, At?.ToLocation(), Arguments.Cast<object>().ToArray());
        }

        private static DiagnosticDescriptor Error(string id, string title, string message, string? description = null) =>
            new DiagnosticDescriptor(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, description: description);

        private static Problem At(DiagnosticDescriptor descriptor, Location? location, params string[] arguments) =>
            new Problem(descriptor, Place.Of(location), new EquatableArray<string>(arguments));

        private static Problem? Orphan(IMethodSymbol method, SyntaxNode node)
        {
            if (method.ContainingType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == CategoryAttribute))
                return null;

            Location? at = (node as MethodDeclarationSyntax)?.Identifier.GetLocation();
            return At(NoCategory, at, method.Name, method.ContainingType.ToDisplayString());
        }

        private static void ReportCategoriesTwice(SourceProductionContext output, ImmutableArray<Claim> claims)
        {
            foreach (IGrouping<string, Claim> group in claims.Where(claim => claim.Category.Length > 0).GroupBy(claim => claim.Category))
            {
                Claim[] ordered = group.OrderBy(claim => claim.TypeName, StringComparer.Ordinal).ToArray();
                for (int i = 1; i < ordered.Length; i++)
                {
                    output.ReportDiagnostic(Diagnostic.Create(
                        CategoryTwice, ordered[i].At?.ToLocation(), group.Key, ordered[0].TypeName, ordered[i].TypeName));
                }
            }
        }

        private static LogClass Describe(GeneratorAttributeSyntaxContext attributed, CancellationToken cancellation)
        {
            var type = (INamedTypeSymbol)attributed.TargetSymbol;
            var node = (ClassDeclarationSyntax)attributed.TargetNode;
            Compilation compilation = attributed.SemanticModel.Compilation;
            var problems = new List<Problem>();
            Location at = node.Identifier.GetLocation();
            string typeName = type.ToDisplayString();

            // The methods the generator writes are extension methods, which C# allows only in a
            // top-level, static, non-generic class.
            if (type.ContainingType is not null)
                problems.Add(At(Malformed, at, typeName, $"is nested in '{type.ContainingType.ToDisplayString()}', and the methods the generator writes are extensions of Log, which a top-level class declares"));
            if (!type.IsStatic)
                problems.Add(At(Malformed, at, typeName, "is not a static class, and the methods the generator writes are extensions of Log, which a static class declares"));
            if (type.IsGenericType)
                problems.Add(At(Malformed, at, typeName, "is generic, so its events would be one spec per type argument"));

            bool partial = type.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(cancellation) is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (!partial)
                problems.Add(At(Malformed, at, typeName, "is not partial, so its events cannot be written beside it"));

            AttributeData category = type.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == CategoryAttribute);
            string categoryName = category.ConstructorArguments.Length == 1 && category.ConstructorArguments[0].Value is string name ? name : "";
            Location? categoryAt = category.ApplicationSyntaxReference?.GetSyntax(cancellation).GetLocation() ?? at;
            if (!LogNames.IsCategory(categoryName))
                problems.Add(At(BadName, categoryAt, categoryName, "a category, which is lowercase words of letters and digits, each starting with a letter, joined by dots, as game.save"));

            var methods = new List<LogMethod>();
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (IMethodSymbol method in type.GetMembers().OfType<IMethodSymbol>())
            {
                AttributeData? attribute = method.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() is EventAttribute or ScopeAttribute);
                if (attribute is null)
                    continue;

                cancellation.ThrowIfCancellationRequested();
                LogMethod? described = DescribeMethod(method, attribute, compilation, problems, cancellation);
                if (described is null)
                    continue;

                Location? methodAt = method.Locations.FirstOrDefault();
                if (keys.TryGetValue(described.EventName, out string? first))
                    problems.Add(At(KeyTwice, methodAt, first, method.Name, $"{categoryName}/{described.EventName}"));
                else
                    keys.Add(described.EventName, method.Name);

                methods.Add(described);
            }

            return new LogClass(
                HintName: typeName + ".Log.g.cs",
                Namespace: type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                Name: type.Name,
                FullName: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                TypeName: typeName,
                Assembly: compilation.AssemblyName ?? "",
                Category: categoryName,
                CategoryAt: Place.Of(categoryAt),
                RuntimeRegistration: compilation.GetTypeByMetadataName("UnityEngine.RuntimeInitializeOnLoadMethodAttribute") is not null,
                EditorRegistration: compilation.GetTypeByMetadataName("UnityEditor.InitializeOnLoadMethodAttribute") is not null,
                Methods: new EquatableArray<LogMethod>(methods.ToArray()),
                Problems: new EquatableArray<Problem>(problems.ToArray()));
        }

        private static LogMethod? DescribeMethod(IMethodSymbol method, AttributeData attribute, Compilation compilation, List<Problem> problems,
            CancellationToken cancellation)
        {
            var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellation) as MethodDeclarationSyntax;
            Location? at = syntax?.Identifier.GetLocation() ?? method.Locations.FirstOrDefault();
            string methodName = method.Name;
            int before = problems.Count;
            bool isScope = attribute.AttributeClass?.ToDisplayString() == ScopeAttribute;

            // The declaration: static partial void, with no body and no access modifier, which is
            // the one partial method C# lets go without an implementation.
            if (!method.IsStatic || !method.IsPartialDefinition)
                problems.Add(At(Malformed, at, methodName, "is not a static partial method with no body, which is how an event is declared: static partial void Saved(int slot);"));
            else if (method.PartialImplementationPart is not null)
                problems.Add(At(Malformed, at, methodName, "has a body, and an event's declaration has none: the generator writes the method a program calls beside it"));
            if (syntax is not null && syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword)
                    || m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)))
                problems.Add(At(Malformed, at, methodName, "has an access modifier, which makes C# require a body; the method the generator writes is public, and the class's own accessibility holds it"));
            if (method.IsGenericMethod)
                problems.Add(At(Malformed, at, methodName, "is generic, so it would be one event per type argument"));
            if (!method.ReturnsVoid)
                problems.Add(At(Malformed, at, methodName, $"returns '{method.ReturnType.ToDisplayString()}', and a declaration returns void: the method the generator writes returns {(isScope ? "the LogScope" : "nothing")}"));
            if (methodName is EventsClass or Registration)
                problems.Add(At(Malformed, at, methodName, $"has a name the generator gives to what it writes into the class beside the events: {EventsClass} and {Registration}"));

            ImmutableArray<IParameterSymbol> parameters = method.Parameters;
            foreach (IParameterSymbol parameter in parameters)
            {
                Location? parameterAt = parameter.Locations.FirstOrDefault();
                if (parameter.RefKind != RefKind.None)
                    problems.Add(At(Malformed, parameterAt, methodName, $"takes '{parameter.Name}' by reference, and an event takes its values as they are"));
                if (parameter.HasExplicitDefaultValue)
                    problems.Add(At(Malformed, parameterAt, methodName, $"gives '{parameter.Name}' a default, and an event's fields are each given at the call"));
                if (parameter.IsParams)
                    problems.Add(At(Malformed, parameterAt, methodName, $"takes '{parameter.Name}' as params, and an event's fields are each given at the call"));
                if (parameter.Type.ToDisplayString() == LogType)
                    problems.Add(At(Malformed, parameterAt, methodName, $"takes the Log '{parameter.Name}', and the generator adds the Log: a declaration names only the fields"));
            }

            // The attribute: a level and a template, or a template alone for a level chosen at the call.
            string template = "";
            byte? declaredLevel = null;
            foreach (TypedConstant argument in attribute.ConstructorArguments)
            {
                if (argument.Value is string text)
                    template = text;
                else if (argument.Value is byte b)
                    declaredLevel = b;
            }

            string eventName = methodName;
            int perSecond = 0;
            foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
            {
                if (named.Key == "Name" && named.Value.Value is string given)
                    eventName = given;
                else if (named.Key == "PerSecond" && named.Value.Value is int limit)
                    perSecond = limit;
            }

            Location? attributeAt = attribute.ApplicationSyntaxReference?.GetSyntax(cancellation).GetLocation() ?? at;
            if (!LogNames.IsEventName(eventName))
                problems.Add(At(BadName, attributeAt, eventName, "an event's name, which is an ASCII letter or '_' followed by letters, digits and '_'"));
            else if (eventName is EventsClass)
                problems.Add(At(BadName, attributeAt, eventName, $"an event's name the generator can write, since its spec is a member of the class {EventsClass}"));
            if (perSecond < 0)
                problems.Add(At(BadLevel, attributeAt, methodName, $"is limited to {perSecond} a second, which is less than none"));

            INamedTypeSymbol? levelType = compilation.GetTypeByMetadataName(LevelType);
            string? levelName = null;
            if (declaredLevel.HasValue)
            {
                levelName = LevelName(levelType, declaredLevel.Value);
                if (levelName == "None")
                    problems.Add(At(BadLevel, attributeAt, methodName, "is declared at None, which is a threshold and not a level a record has"));
            }

            List<IParameterSymbol> levelParameters = parameters.Where(p => SymbolEqualityComparer.Default.Equals(p.Type, levelType)).ToList();
            IParameterSymbol? levelParameter = null;
            if (declaredLevel.HasValue && levelParameters.Count > 0)
                problems.Add(At(BadLevel, levelParameters[0].Locations.FirstOrDefault(), methodName, "has a LogLevel parameter, and its attribute gives its level"));
            else if (!declaredLevel.HasValue && levelParameters.Count != 1)
                problems.Add(At(BadLevel, at, methodName, levelParameters.Count == 0
                    ? "has no LogLevel parameter, and its attribute gives no level, so a call chooses it"
                    : "has two LogLevel parameters, and one is the level chosen at the call"));
            else if (!declaredLevel.HasValue)
                levelParameter = levelParameters[0];

            List<IParameterSymbol> fieldParameters = parameters.Where(p => !SymbolEqualityComparer.Default.Equals(p, levelParameter)).ToList();
            if (fieldParameters.Count > LogNames.MaxFields)
                problems.Add(At(TooManyFields, at, methodName, fieldParameters.Count.ToString(CultureInfo.InvariantCulture), LogNames.MaxFields.ToString(CultureInfo.InvariantCulture)));

            // The template: every hole names a field, which takes the hole's spelling.
            var spelling = new Dictionary<IParameterSymbol, string>(SymbolEqualityComparer.Default);
            ParsedTemplate? parsed = TemplateParser.Parse(template, out string? error);
            if (parsed is null)
            {
                problems.Add(At(BadTemplate, attributeAt, methodName, error ?? ""));
            }
            else
            {
                foreach (TemplateHole hole in parsed.Holes)
                {
                    IParameterSymbol? named = fieldParameters.FirstOrDefault(p => string.Equals(p.Name, hole.Name, StringComparison.OrdinalIgnoreCase));
                    if (named is null)
                        problems.Add(At(HoleWithoutParameter, attributeAt, methodName, hole.Name));
                    else if (!spelling.ContainsKey(named))
                        spelling.Add(named, hole.Name);
                }
            }

            var fields = new List<LogParameter>();
            var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IParameterSymbol parameter in fieldParameters)
            {
                Location? parameterAt = parameter.Locations.FirstOrDefault();
                string fieldName = spelling.TryGetValue(parameter, out string? spelt) ? spelt : Pascal(parameter.Name);
                if (!LogNames.IsEventName(fieldName))
                    problems.Add(At(BadName, parameterAt, fieldName, "a field's name, which is an ASCII letter or '_' followed by letters, digits and '_'"));
                else if (!fieldNames.Add(fieldName))
                    problems.Add(At(BadName, parameterAt, fieldName, "a field's name of its own, since another parameter's differs from it only in case"));

                LogParameter? field = DescribeField(parameter, fieldName);
                if (field is null)
                    problems.Add(At(UnsupportedType, parameterAt, parameter.Name, parameter.Type.ToDisplayString()));
                else
                    fields.Add(field);
            }

            if (problems.Count > before || syntax is null)
                return null;

            // The names the generated method gives its Log, its writer and the call site, kept clear of the fields'.
            var taken = new HashSet<string>(parameters.Select(p => p.Name), StringComparer.Ordinal);
            string log = Unused("log", taken);
            string writer = Unused("writer", taken);
            string file = Unused("callerFile", taken);
            string line = Unused("callerLine", taken);

            Location location = method.Locations[0];
            FileLinePositionSpan span = location.GetMappedLineSpan();
            string path = CallerFilePath(span, location.SourceTree, compilation);
            return new LogMethod(
                MethodName: methodName,
                EventName: eventName,
                Scope: isScope,
                Level: levelName,
                Template: template,
                PerSecond: perSecond,
                Declaration: $"{methodName}({string.Join(", ", parameters.Select(p => p.Type.ToDisplayString() + " " + p.Name))})",
                Parameters: new EquatableArray<string>(parameters.Select(Declare).ToArray()),
                LogArgument: Identifier(log),
                WriterName: Identifier(writer),
                FileArgument: Identifier(file),
                LineArgument: Identifier(line),
                LevelArgument: levelParameter is null ? null : Identifier(levelParameter.Name),
                Fields: new EquatableArray<LogParameter>(fields.ToArray()),
                File: path,
                FileName: path.Substring(path.LastIndexOfAny(new[] { '/', '\\' }) + 1),
                Line: span.StartLinePosition.Line + 1);
        }

        /// <summary>The field a parameter is, or null for a type a field cannot hold.</summary>
        private static LogParameter? DescribeField(IParameterSymbol parameter, string fieldName)
        {
            // What a field holds is the type without its annotation: a string? or Exception? is a
            // string or Exception that may be null, which a field holds as it holds no value.
            ITypeSymbol type = parameter.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            Optionality optional = Optionality.None;
            if (type is INamedTypeSymbol { IsGenericType: true } generic)
            {
                if (generic.OriginalDefinition.ToDisplayString() == OptionalType)
                    optional = Optionality.Optional;
                else if (generic.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                    optional = Optionality.Nullable;

                if (optional != Optionality.None)
                    type = generic.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.NotAnnotated);
            }

            bool personal = parameter.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == PersonalAttribute);
            string? tag = parameter.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == TagAttribute)
                .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
                .FirstOrDefault();

            string full = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string display = parameter.Type.ToDisplayString();
            LogParameter Make(Shape shape, string kind, string valueType, string? conversion = null) =>
                new LogParameter(Identifier(parameter.Name), fieldName, shape, kind, valueType, full, display, personal, tag, optional, conversion,
                    default, default, false);

            string? builtIn = type.SpecialType switch
            {
                SpecialType.System_Boolean => "Boolean",
                SpecialType.System_Char => "Char",
                SpecialType.System_SByte => "SByte",
                SpecialType.System_Byte => "Byte",
                SpecialType.System_Int16 => "Int16",
                SpecialType.System_UInt16 => "UInt16",
                SpecialType.System_Int32 => "Int32",
                SpecialType.System_UInt32 => "UInt32",
                SpecialType.System_Int64 => "Int64",
                SpecialType.System_UInt64 => "UInt64",
                SpecialType.System_Single => "Single",
                SpecialType.System_Double => "Double",
                SpecialType.System_Decimal => "Decimal",
                SpecialType.System_DateTime => "DateTime",
                _ => type.ToDisplayString() switch
                {
                    "System.Guid" => "Guid",
                    "System.DateTimeOffset" => "DateTimeOffset",
                    "System.TimeSpan" => "TimeSpan",
                    _ => null,
                },
            };

            if (builtIn is not null)
                return Make(Shape.Value, builtIn, full);
            if (type.SpecialType == SpecialType.System_IntPtr)
                return Make(Shape.Value, "Int64", "long", "(long)");
            if (type.SpecialType == SpecialType.System_UIntPtr)
                return Make(Shape.Value, "UInt64", "ulong", "(ulong)");
            if (type.SpecialType == SpecialType.System_String)
                return Make(Shape.Ref, "String", "string");
            if (IsException(type))
                return Make(Shape.Ref, "Exception", full);

            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                var names = new List<string>();
                var values = new List<long>();
                foreach (IFieldSymbol member in enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                {
                    names.Add(member.Name);
                    values.Add(member.ConstantValue is ulong big ? unchecked((long)big) : Convert.ToInt64(member.ConstantValue, CultureInfo.InvariantCulture));
                }

                bool flags = enumType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute");
                return new LogParameter(Identifier(parameter.Name), fieldName, Shape.Enum, "Enum", full, full, display, personal, tag, optional, null,
                    new EquatableArray<string>(names.ToArray()), new EquatableArray<long>(values.ToArray()), flags);
            }

            if (optional == Optionality.None && type is INamedTypeSymbol { IsGenericType: true } span
                && span.OriginalDefinition.ToDisplayString() == "System.ReadOnlySpan<T>"
                && span.TypeArguments[0].SpecialType == SpecialType.System_Char)
                return Make(Shape.Chars, "Chars", full);

            if (type.IsUnmanagedType && type.TypeKind == TypeKind.Struct && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                && type.AllInterfaces.Any(i => i.ToDisplayString() == "System.IFormattable"))
                return Make(Shape.Formattable, "Formattable", full);

            return null;
        }

        private static void Write(SourceProductionContext output, LogClass type)
        {
            if (type.Problems.Length > 0)
            {
                foreach (Problem problem in type.Problems)
                    output.ReportDiagnostic(problem.ToDiagnostic());
                return;
            }

            if (type.Methods.Length == 0)
                return;

            var source = new StringBuilder();
            source.AppendLine("// <auto-generated/>");
            source.AppendLine("// Written by Tutan.Logging.Generators from the class's [LogEvent] and [LogScope] declarations. Do not edit.");
            source.AppendLine("#nullable enable");
            source.AppendLine();

            string indent = "";
            if (type.Namespace is not null)
            {
                source.Append("namespace ").AppendLine(type.Namespace);
                source.AppendLine("{");
                indent = "    ";
            }

            source.Append(indent).Append("static partial class ").AppendLine(type.Name);
            source.Append(indent).AppendLine("{");
            var lines = new List<string>();
            WriteEvents(lines, type);
            WriteRegistration(lines, type);
            foreach (LogMethod method in type.Methods)
                WriteMethod(lines, type, method);

            foreach (string line in lines)
            {
                // A preprocessor line or a blank one is written as it is; the rest take the namespace's indent.
                if (line.Length == 0 || line[0] == '#')
                    source.AppendLine(line);
                else
                    source.Append(indent).Append("    ").AppendLine(line);
            }

            source.Append(indent).AppendLine("}");
            if (type.Namespace is not null)
                source.AppendLine("}");

            output.AddSource(type.HintName, SourceText.From(source.ToString(), Encoding.UTF8));
        }

        private static void WriteEvents(List<string> lines, LogClass type)
        {
            lines.Add("/// <summary>The specs of the events and scopes this class declares, by name, which the catalog holds and a call site asks about.</summary>");
            lines.Add($"public static class {EventsClass}");
            lines.Add("{");
            for (int i = 0; i < type.Methods.Length; i++)
            {
                LogMethod method = type.Methods[i];
                if (i > 0)
                    lines.Add("");

                string key = $"{type.Category}/{method.EventName}";
                lines.Add($"    /// <summary><c>{Xml(key)}</c>: {Xml(method.Template)}</summary>");
                lines.Add($"    public static readonly {Logging}.LogEventSpec {method.EventName} = {Logging}.LogEventSpec.Declare(");
                lines.Add($"        {Literal(type.Category)}, {Literal(method.EventName)}, {Logging}.LogEventKind.{(method.Scope ? "Scope" : "Event")}, " +
                    (method.Level is null ? "null," : $"{Logging}.LogLevel.{method.Level},"));
                lines.Add($"        {Literal(method.Template)},");
                if (method.Fields.Length == 0)
                {
                    lines.Add($"        global::System.Array.Empty<{Logging}.LogField>(),");
                }
                else
                {
                    lines.Add($"        new {Logging}.LogField[]");
                    lines.Add("        {");
                    foreach (LogParameter field in method.Fields)
                        lines.Add($"            {FieldSpec(field)},");
                    lines.Add("        },");
                }

                lines.Add($"        {method.PerSecond.ToString(CultureInfo.InvariantCulture)},");
                lines.Add($"        new {Logging}.LogSource({Literal(type.Assembly)}, {Literal(type.TypeName)}, {Literal(method.File)}, " +
                    $"{method.Line.ToString(CultureInfo.InvariantCulture)}));");
            }

            lines.Add("}");
            lines.Add("");
        }

        private static void WriteRegistration(List<string> lines, LogClass type)
        {
            if (!type.RuntimeRegistration && !type.EditorRegistration)
                return;

            // Registered when the editor loads the assembly and before the first scene of a player,
            // so the catalog lists every event before any of them fires. Registering is idempotent,
            // which entering Play Mode without a domain reload relies on.
            lines.Add("/// <summary>Registers the class's specs in the catalog, which Unity calls when it loads the assembly.</summary>");
            if (type.EditorRegistration)
                lines.Add("[global::UnityEditor.InitializeOnLoadMethod]");
            if (type.RuntimeRegistration)
                lines.Add("[global::UnityEngine.RuntimeInitializeOnLoadMethod(global::UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]");
            lines.Add($"private static void {Registration}() => {Logging}.LogCatalog.Register(");
            lines.Add("    " + string.Join(", ", type.Methods.Select(m => $"{EventsClass}.{m.EventName}")) + ");");
        }

        private static void WriteMethod(List<string> lines, LogClass type, LogMethod method)
        {
            string spec = $"{type.FullName}.{EventsClass}.{method.EventName}";
            string log = method.LogArgument;
            string writer = method.WriterName;
            string level = method.LevelArgument ?? $"{Logging}.LogLevel.{method.Level}";
            string enabled = method.LevelArgument is null ? $"{log}.IsEnabled({spec})" : $"{log}.IsEnabled({spec}, {method.LevelArgument})";
            string[] charSlots = method.Fields.Where(f => f.Shape == Shape.Chars).Select(f => $"{Logging}.LogWriter.CharSlots({f.Argument})").ToArray();
            string chars = charSlots.Length == 0 ? "0" : string.Join(" + ", charSlots);
            var parameters = new List<string> { $"this {Logging}.Log {log}" };
            parameters.AddRange(method.Parameters);
            parameters.Add($"[global::System.Runtime.CompilerServices.CallerFilePath] string {method.FileArgument} = \"\"");
            parameters.Add($"[global::System.Runtime.CompilerServices.CallerLineNumber] int {method.LineArgument} = 0");

            lines.Add("");
            lines.Add($"/// <summary>{(method.Scope ? "Opens" : "Writes")} <c>{Xml(type.Category + "/" + method.EventName)}</c>: {Xml(method.Template)}</summary>");
            lines.Add($"/// <remarks>Declared in {Xml(method.FileName)} at line {method.Line.ToString(CultureInfo.InvariantCulture)} as <c>{Xml(method.Declaration)}</c>.</remarks>");
            if (method.Scope)
                lines.Add("/// <returns>The scope, which closes when it is disposed, and whose <c>Log</c> writes under it.</returns>");
            lines.Add($"public static {(method.Scope ? Logging + ".LogScope" : "void")} {method.MethodName}({string.Join(", ", parameters)})");
            lines.Add("{");
            lines.Add($"    if (!{enabled})");
            lines.Add(method.Scope ? $"        return {Logging}.LogScope.Off({log});" : "        return;");
            lines.Add("");
            lines.Add($"    {Logging}.LogWriter {writer} = {log}.{(method.Scope ? "BeginScope" : "Write")}({spec}, {level}, {chars}, {method.FileArgument}, {method.LineArgument});");
            if (method.Fields.Length > 0)
            {
                lines.Add("    try");
                lines.Add("    {");
                foreach (LogParameter field in method.Fields)
                    WriteField(lines, field, writer);
                lines.Add("    }");
                lines.Add("    finally");
                lines.Add("    {");
                lines.Add($"        {writer}.Commit();");
                lines.Add("    }");
            }
            else
            {
                lines.Add($"    {writer}.Commit();");
            }

            if (method.Scope)
            {
                lines.Add("");
                lines.Add($"    return {writer}.Scope;");
            }

            lines.Add("}");
        }

        private static void WriteField(List<string> lines, LogParameter field, string writer)
        {
            const string indent = "        ";
            string argument = field.Argument;

            // An optional field writes its presence, then its value or the default that stands in for none.
            string value = field.Optional switch
            {
                Optionality.Optional => $"global::Tutan.Functional.OptionalExtensions.Or({argument}, default({field.Underlying}){(field.Shape == Shape.Ref ? "!" : "")})",
                Optionality.Nullable => $"{argument}.GetValueOrDefault()",
                _ => argument,
            };

            switch (field.Shape)
            {
                case Shape.Ref:
                    lines.Add($"{indent}{writer}.Ref({value});");
                    return;
                case Shape.Chars:
                    lines.Add($"{indent}{writer}.Chars({value});");
                    return;
            }

            if (field.Optional == Optionality.Optional)
                lines.Add($"{indent}{writer}.Presence({argument}.IsSome);");
            else if (field.Optional == Optionality.Nullable)
                lines.Add($"{indent}{writer}.Presence({argument}.HasValue);");

            string copied = field.Shape == Shape.Enum ? $"unchecked((long){value})" : field.Conversion is null ? value : $"{field.Conversion}{value}";
            lines.Add($"{indent}{writer}.Value({copied});");
        }

        private static string FieldSpec(LogParameter field)
        {
            string name = Literal(field.FieldName);
            string typeName = Literal(field.TypeName);
            var extra = new StringBuilder();
            if (field.Personal)
                extra.Append(", personal: true");
            if (field.Tag is not null)
                extra.Append(", tag: ").Append(Literal(field.Tag));
            if (field.Optional != Optionality.None)
                extra.Append(", optional: true");

            string kind = $"{Logging}.LogValueKind.{field.Kind}";
            return field.Shape switch
            {
                Shape.Value => $"{Logging}.LogField.Value<{field.ValueType}>({name}, {kind}, {typeName}{extra})",
                Shape.Ref => $"{Logging}.LogField.Ref({name}, {kind}, {typeName}{extra})",
                Shape.Chars => $"{Logging}.LogField.Chars({name}, {typeName}{extra})",
                Shape.Formattable => $"{Logging}.LogField.Formattable<{field.ValueType}>({name}, {typeName}{extra})",
                _ => $"{Logging}.LogField.Enum({name}, {typeName}, " +
                    $"new string[] {{ {string.Join(", ", field.EnumNames.Select(Literal))} }}, " +
                    $"new long[] {{ {string.Join(", ", field.EnumValues.Select(v => v.ToString(CultureInfo.InvariantCulture) + "L"))} }}, " +
                    $"{(field.Flags ? "true" : "false")}{extra})",
            };
        }

        /// <summary>
        /// A parameter as the generated method declares it: its type, fully qualified, and its name.
        /// A reference type the declaration left oblivious to nulls is written as nullable, since a
        /// field holds a null as it holds no value.
        /// </summary>
        private static string Declare(IParameterSymbol parameter)
        {
            ITypeSymbol type = parameter.Type;
            if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.None)
                type = type.WithNullableAnnotation(NullableAnnotation.Annotated);

            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)) + " " + Identifier(parameter.Name);
        }

        private static string Identifier(string name) =>
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        /// <summary><paramref name="name"/>, with underscores added until no parameter of the declaration has it.</summary>
        private static string Unused(string name, HashSet<string> taken)
        {
            while (taken.Contains(name))
                name += "_";

            taken.Add(name);
            return name;
        }

        /// <summary>
        /// The file of <paramref name="span"/> as the compiler writes a <c>[CallerFilePath]</c> there: after
        /// any <c>#line</c>, made whole and normalized against the compilation's folder, then mapped by any
        /// <c>-pathmap</c>. So an event's declaration names its file as a call in the same file does, which
        /// Unity compiles as <c>D:\Game\Assets/Game/SaveLog.cs</c> and the compiler writes with one separator.
        /// </summary>
        private static string CallerFilePath(FileLinePositionSpan span, SyntaxTree? tree, Compilation compilation) =>
            compilation.Options.SourceReferenceResolver?.NormalizePath(span.Path, span.HasMappedPath ? tree?.FilePath : null) ?? span.Path;

        private static string Pascal(string name) =>
            name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);

        private static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

        private static string Xml(string text) =>
            text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string LevelName(INamedTypeSymbol? levelType, byte value) =>
            levelType?.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.HasConstantValue && f.ConstantValue is byte b && b == value)?.Name
            ?? value.ToString(CultureInfo.InvariantCulture);

        private static bool IsException(ITypeSymbol type)
        {
            for (ITypeSymbol? at = type; at is not null; at = at.BaseType)
            {
                if (at.ToDisplayString() == "System.Exception")
                    return true;
            }

            return false;
        }
    }

    /// <summary>An array compared by its items, which is what an incremental generator's values need to be to be cached.</summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    {
        private readonly T[]? _items;

        public EquatableArray(T[]? items) => _items = items;

        public int Length => Items.Length;

        public T this[int index] => Items[index];

        private T[] Items => _items ?? Array.Empty<T>();

        public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            int hash = 17;
            foreach (T item in Items)
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            return hash;
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
