; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TLOG0001 | Logging | Error | A hole in an event's template names no parameter
TLOG0002 | Logging | Error | An event's parameter is of a type a field cannot hold
TLOG0003 | Logging | Error | An event's declaration or class is not of the shape the generator writes for
TLOG0004 | Logging | Error | Two events have one key
TLOG0005 | Logging | Error | A category or a name is malformed
TLOG0006 | Logging | Error | An event's level or limit is malformed
TLOG0007 | Logging | Error | An event's template does not parse
TLOG0008 | Logging | Error | An event is declared in a class with no [LogCategory]
TLOG0009 | Logging | Error | Two classes declare one category
TLOG0010 | Logging | Error | An event has more fields than a record holds
