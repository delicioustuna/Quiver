; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
QVRHE001 | Quiver.Hyperedge | Error | A member cannot be both a hyperedge role and a property.
QVRHE002 | Quiver.Hyperedge | Error | A role must be typed as GraphNodeRef&lt;TNode&gt; or IReadOnlyList&lt;GraphNodeRef&lt;TNode&gt;&gt;.
QVRHE003 | Quiver.Hyperedge | Error | A role or property must have an accessible setter.
