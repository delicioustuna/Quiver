; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
QVRHE001 | Yatagarasu.Nexus | Error | A member cannot be both a nexus role and a property.
QVRHE002 | Yatagarasu.Nexus | Error | A role must be typed as GraphVertexRef&lt;TVertex&gt; or IReadOnlyList&lt;GraphVertexRef&lt;TVertex&gt;&gt;.
QVRHE003 | Yatagarasu.Nexus | Error | A role or property must have an accessible setter.
