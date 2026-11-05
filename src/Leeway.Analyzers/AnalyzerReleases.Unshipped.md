; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
LEE001 | Leeway | Warning | Decremented field has no merge rule
LEE002 | Leeway | Warning | Merged field is written directly
LEE003 | Leeway | Error | Merge attribute on an unsuitable property
LEE004 | Leeway | Error | More than one merge attribute
LEE005 | Leeway | Warning | Merge attribute outside an entity
LEE006 | Leeway | Warning | Lease grants nothing
